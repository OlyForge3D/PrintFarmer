import XCTest
@testable import PrintFarmer

/// Tests for `PrinterRunActionPresentation` — the immutable contract every
/// `PrinterRunActionBar` binds to. These prove the presentation-shape
/// invariants required by issue #2520 without hosting a SwiftUI view.
final class PrinterRunActionPresentationTests: XCTestCase {

    // MARK: - Printing fixture

    func test_printingFixture_exposesPauseCancelStopEmergencyStop_notResume() {
        let presentation = PrinterRunActionPresentation(descriptors: [
            .init(kind: .pause),
            .init(kind: .cancel),
            .init(kind: .stop),
            .init(kind: .emergencyStop),
        ])
        XCTAssertEqual(
            presentation.visibleKinds,
            [.pause, .cancel, .stop, .emergencyStop],
            "Printing should show Pause + destructive actions in render order"
        )
        XCTAssertNil(presentation.descriptor(for: .resume),
                     "Printing should NOT expose Resume")
    }

    // MARK: - Paused fixture

    func test_pausedFixture_exposesResumeCancelStopEmergencyStop_notPause() {
        let presentation = PrinterRunActionPresentation(descriptors: [
            .init(kind: .resume),
            .init(kind: .cancel),
            .init(kind: .stop),
            .init(kind: .emergencyStop),
        ])
        XCTAssertEqual(
            presentation.visibleKinds,
            [.resume, .cancel, .stop, .emergencyStop]
        )
        XCTAssertNil(presentation.descriptor(for: .pause),
                     "Paused should NOT expose Pause")
    }

    // MARK: - Idle fixture

    func test_idleFixture_hasNoRunActions_evenWhenPresentationSuppliesNone() {
        let presentation = PrinterRunActionPresentation.empty
        XCTAssertTrue(presentation.visibleKinds.isEmpty)
        for kind in PrinterRunActionKind.allCases {
            XCTAssertNil(presentation.descriptor(for: kind))
            XCTAssertFalse(presentation.shouldFireCallback(for: kind))
        }
    }

    func test_idleFixture_mayExposeOnlyEmergencyStop() {
        // The bar does not synthesize a "start" action for idle; the host is
        // free to expose Emergency Stop alone as a safety-reachable control.
        let presentation = PrinterRunActionPresentation(descriptors: [
            .init(kind: .emergencyStop),
        ])
        XCTAssertEqual(presentation.visibleKinds, [.emergencyStop])
        XCTAssertTrue(presentation.shouldFireCallback(for: .emergencyStop))
    }

    // MARK: - Offline / limited-permission fixture

    func test_offlineFixture_honorsHostSuppliedUnavailability_withoutInferring() {
        let presentation = PrinterRunActionPresentation(descriptors: [
            .init(
                kind: .pause,
                isEnabled: false,
                unavailableReason: "printer offline"
            ),
            .init(
                kind: .cancel,
                isEnabled: false,
                unavailableReason: "printer offline"
            ),
            .init(
                kind: .emergencyStop,
                isEnabled: false,
                unavailableReason: "printer offline"
            ),
        ])
        // Actions remain visible but disabled — the operator can still see them
        // and hear their unavailable reason.
        XCTAssertEqual(presentation.visibleKinds, [.pause, .cancel, .emergencyStop])
        XCTAssertFalse(presentation.shouldFireCallback(for: .pause))
        XCTAssertFalse(presentation.shouldFireCallback(for: .cancel))
        XCTAssertFalse(presentation.shouldFireCallback(for: .emergencyStop))
    }

    func test_limitedPermissionFixture_hidesForbiddenActions() {
        let presentation = PrinterRunActionPresentation(descriptors: [
            .init(kind: .pause),
            .init(
                kind: .stop,
                isVisible: false,
                unavailableReason: "insufficient permissions"
            ),
            .init(
                kind: .emergencyStop,
                isVisible: false,
                unavailableReason: "insufficient permissions"
            ),
        ])
        XCTAssertEqual(presentation.visibleKinds, [.pause])
        XCTAssertFalse(presentation.shouldFireCallback(for: .stop))
        XCTAssertFalse(presentation.shouldFireCallback(for: .emergencyStop))
    }

    // MARK: - Duplicate handling

    func test_duplicateDescriptors_forSameKind_collapseToLastSupplied() {
        let presentation = PrinterRunActionPresentation(descriptors: [
            .init(kind: .cancel, isEnabled: true),
            .init(kind: .cancel, isEnabled: false, unavailableReason: "gate flipped"),
        ])
        let descriptor = presentation.descriptor(for: .cancel)
        XCTAssertNotNil(descriptor)
        XCTAssertEqual(descriptor?.isEnabled, false)
        XCTAssertEqual(descriptor?.unavailableReason, "gate flipped")
        // And crucially it appears only once — no duplicate cancel slot.
        XCTAssertEqual(presentation.visibleKinds.filter { $0 == .cancel }.count, 1)
    }

    // MARK: - Render order

    func test_visibleDescriptors_areInStableRenderOrder_regardlessOfInputOrder() {
        // Feed them in reverse.
        let presentation = PrinterRunActionPresentation(descriptors: [
            .init(kind: .emergencyStop),
            .init(kind: .stop),
            .init(kind: .cancel),
            .init(kind: .resume),
        ])
        XCTAssertEqual(
            presentation.visibleKinds,
            [.resume, .cancel, .stop, .emergencyStop],
            "Render order must follow `PrinterRunActionPresentation.renderOrder`"
        )
    }

    func test_visibleDescriptors_omitsHiddenEvenWhenPresent() {
        let presentation = PrinterRunActionPresentation(descriptors: [
            .init(kind: .pause),
            .init(kind: .cancel, isVisible: false),
            .init(kind: .stop),
            .init(kind: .emergencyStop),
        ])
        XCTAssertEqual(presentation.visibleKinds, [.pause, .stop, .emergencyStop])
    }

    // MARK: - Pending semantics

    func test_pendingAction_doesNotFireCallback() {
        let presentation = PrinterRunActionPresentation(descriptors: [
            .init(kind: .cancel, isPending: true),
        ])
        XCTAssertFalse(presentation.shouldFireCallback(for: .cancel),
                       "Pending actions must not re-fire on redraws or repeat taps")
    }

    func test_enabledNonPendingAction_firesCallback() {
        let presentation = PrinterRunActionPresentation(descriptors: [
            .init(kind: .pause),
        ])
        XCTAssertTrue(presentation.shouldFireCallback(for: .pause))
    }

    // MARK: - Independent Emergency Stop gate

    func test_emergencyStop_isNotBlanketDisabled_whenAnotherActionIsPending() {
        // Cancel is pending, Emergency Stop remains enabled — the epic
        // explicitly forbids blanket-disabling Emergency Stop because of
        // an unrelated pending action.
        let presentation = PrinterRunActionPresentation(descriptors: [
            .init(kind: .cancel, isPending: true),
            .init(kind: .emergencyStop, isEnabled: true, isPending: false),
        ])
        XCTAssertFalse(presentation.shouldFireCallback(for: .cancel),
                       "Cancel is pending; must not re-fire")
        XCTAssertTrue(presentation.shouldFireCallback(for: .emergencyStop),
                      "Emergency Stop is independent of other pending actions")
    }

    func test_emergencyStop_honorsHostSuppliedDisable_independently() {
        let presentation = PrinterRunActionPresentation(descriptors: [
            .init(kind: .pause),
            .init(kind: .emergencyStop, isEnabled: false,
                  unavailableReason: "printer offline"),
        ])
        XCTAssertTrue(presentation.shouldFireCallback(for: .pause))
        XCTAssertFalse(presentation.shouldFireCallback(for: .emergencyStop))
    }

    // MARK: - Value semantics

    func test_equalDescriptorSets_produceEqualPresentations() {
        let a = PrinterRunActionPresentation(descriptors: [
            .init(kind: .pause),
            .init(kind: .cancel),
        ])
        let b = PrinterRunActionPresentation(descriptors: [
            .init(kind: .cancel),
            .init(kind: .pause),
        ])
        XCTAssertEqual(a, b, "Presentation equality is order-insensitive")
    }

    func test_differentDescriptorState_producesUnequalPresentations() {
        let a = PrinterRunActionPresentation(descriptors: [
            .init(kind: .cancel, isEnabled: true),
        ])
        let b = PrinterRunActionPresentation(descriptors: [
            .init(kind: .cancel, isEnabled: false),
        ])
        XCTAssertNotEqual(a, b)
    }

    // MARK: - Label contract (accessibility strings)

    func test_emergencyStop_hasDistinctAccessibilityLabel_notMatchingStop() {
        XCTAssertNotEqual(
            PrinterRunActionLabels.accessibilityLabel(for: .emergencyStop),
            PrinterRunActionLabels.accessibilityLabel(for: .stop),
            "VoiceOver must distinguish Emergency Stop from Stop"
        )
        XCTAssertEqual(
            PrinterRunActionLabels.accessibilityLabel(for: .emergencyStop),
            "Emergency stop printer"
        )
    }

    func test_allAccessibilityLabels_areDistinct_andNotGenericButton() {
        var labels: Set<String> = []
        for kind in PrinterRunActionKind.allCases {
            let label = PrinterRunActionLabels.accessibilityLabel(for: kind)
            XCTAssertFalse(label.isEmpty, "Kind \(kind) has empty accessibility label")
            XCTAssertFalse(label.lowercased() == "button",
                           "Kind \(kind) must not use the generic word 'button'")
            let (inserted, _) = labels.insert(label)
            XCTAssertTrue(inserted, "Duplicate accessibility label for \(kind): \(label)")
        }
    }

    func test_emergencyStopHint_declaresConfirmationAndNotASubstituteForSafetySwitch() {
        let hint = PrinterRunActionLabels.accessibilityHint(for: .emergencyStop)
        XCTAssertTrue(
            hint.lowercased().contains("confirmation"),
            "Emergency Stop hint must announce that confirmation is required: \"\(hint)\""
        )
        XCTAssertTrue(
            hint.lowercased().contains("not a substitute")
                && hint.lowercased().contains("physical safety"),
            "Emergency Stop hint must state it is not a substitute for the physical safety switch: \"\(hint)\""
        )
    }

    func test_destructiveKinds_areMarkedDestructive() {
        XCTAssertTrue(PrinterRunActionLabels.isDestructive(.cancel))
        XCTAssertTrue(PrinterRunActionLabels.isDestructive(.stop))
        XCTAssertTrue(PrinterRunActionLabels.isDestructive(.emergencyStop))
        XCTAssertFalse(PrinterRunActionLabels.isDestructive(.pause))
        XCTAssertFalse(PrinterRunActionLabels.isDestructive(.resume))
    }

    func test_accessibilityIdentifiers_matchReservedNamespace() {
        XCTAssertEqual(
            PrinterRunActionLabels.accessibilityIdentifier(for: .pause),
            "printer.detail.control.pause"
        )
        XCTAssertEqual(
            PrinterRunActionLabels.accessibilityIdentifier(for: .resume),
            "printer.detail.control.resume"
        )
        XCTAssertEqual(
            PrinterRunActionLabels.accessibilityIdentifier(for: .cancel),
            "printer.detail.control.cancel"
        )
        XCTAssertEqual(
            PrinterRunActionLabels.accessibilityIdentifier(for: .stop),
            "printer.detail.control.stop"
        )
        XCTAssertEqual(
            PrinterRunActionLabels.accessibilityIdentifier(for: .emergencyStop),
            "printer.detail.control.emergencyStop"
        )
        XCTAssertEqual(
            PrinterRunActionLabels.containerAccessibilityIdentifier,
            "printer.detail.runActions"
        )
    }

    // MARK: - Descriptor defaults

    func test_descriptorDefaults_areVisibleEnabledNotPendingNoReason() {
        let descriptor = PrinterRunActionDescriptor(kind: .pause)
        XCTAssertTrue(descriptor.isVisible)
        XCTAssertTrue(descriptor.isEnabled)
        XCTAssertFalse(descriptor.isPending)
        XCTAssertNil(descriptor.unavailableReason)
    }

    // MARK: - Resolved hint/value composition (matches #2519 cross-component pattern)

    /// When a descriptor is enabled the bar reads the static per-kind hint —
    /// what activating the button will do, plus confirmation notes for
    /// destructive actions and the physical-safety caveat for Emergency Stop.
    func test_resolvedHint_forEnabled_matchesStaticHintForKind() {
        for kind in PrinterRunActionKind.allCases {
            let descriptor = PrinterRunActionDescriptor(kind: kind)
            XCTAssertEqual(
                PrinterRunActionLabels.resolvedAccessibilityHint(for: descriptor),
                PrinterRunActionLabels.accessibilityHint(for: kind),
                "Enabled descriptor for \(kind) must expose the static hint"
            )
        }
    }

    /// When a descriptor is disabled with a host-supplied reason the bar
    /// **replaces** the hint with that reason so VoiceOver announces the
    /// dimmed control's cause (matching #2519's pattern in
    /// `PrinterControlsSection` so both surfaces on the printer-detail screen
    /// explain disable state the same way).
    func test_resolvedHint_forDisabledWithReason_isTheHostReason() {
        let descriptor = PrinterRunActionDescriptor(
            kind: .pause,
            isEnabled: false,
            unavailableReason: "printer offline"
        )
        XCTAssertEqual(
            PrinterRunActionLabels.resolvedAccessibilityHint(for: descriptor),
            "printer offline"
        )
    }

    /// When a descriptor is disabled without a reason the bar falls back to a
    /// generic "Unavailable" so VoiceOver never reads a stale enabled hint on
    /// a dimmed button.
    func test_resolvedHint_forDisabledWithoutReason_fallsBackToUnavailable() {
        let descriptor = PrinterRunActionDescriptor(
            kind: .stop,
            isEnabled: false,
            unavailableReason: nil
        )
        XCTAssertEqual(
            PrinterRunActionLabels.resolvedAccessibilityHint(for: descriptor),
            "Unavailable"
        )
    }

    /// An empty-string reason on a disabled descriptor still degrades to the
    /// generic fallback so a host bug can't cause VoiceOver to read a blank
    /// hint on a dimmed button.
    func test_resolvedHint_forDisabledWithEmptyReason_fallsBackToUnavailable() {
        let descriptor = PrinterRunActionDescriptor(
            kind: .cancel,
            isEnabled: false,
            unavailableReason: ""
        )
        XCTAssertEqual(
            PrinterRunActionLabels.resolvedAccessibilityHint(for: descriptor),
            "Unavailable"
        )
    }

    /// Pending descriptors keep the enabled hint — the action itself hasn't
    /// changed, only its in-flight state, and the value already carries
    /// "Pending" to announce that.
    func test_resolvedHint_forPendingEnabled_keepsStaticHint() {
        let descriptor = PrinterRunActionDescriptor(
            kind: .cancel,
            isEnabled: true,
            isPending: true
        )
        XCTAssertEqual(
            PrinterRunActionLabels.resolvedAccessibilityHint(for: descriptor),
            PrinterRunActionLabels.accessibilityHint(for: .cancel)
        )
    }

    /// A disabled Emergency Stop is a serious situation (the physical safety
    /// switch is the fallback). The host's reason must still surface as the
    /// hint so a VoiceOver operator hears the cause rather than the generic
    /// physical-safety hint on a control they can't fire.
    func test_resolvedHint_forDisabledEmergencyStopWithReason_isTheReason() {
        let descriptor = PrinterRunActionDescriptor(
            kind: .emergencyStop,
            isEnabled: false,
            unavailableReason: "printer offline"
        )
        XCTAssertEqual(
            PrinterRunActionLabels.resolvedAccessibilityHint(for: descriptor),
            "printer offline"
        )
    }

    // MARK: - Resolved value

    func test_resolvedValue_forPending_isPending() {
        let descriptor = PrinterRunActionDescriptor(kind: .cancel, isPending: true)
        XCTAssertEqual(
            PrinterRunActionLabels.resolvedAccessibilityValue(for: descriptor),
            "Pending"
        )
    }

    func test_resolvedValue_forEnabledNonPending_isEmpty() {
        let descriptor = PrinterRunActionDescriptor(kind: .pause)
        XCTAssertEqual(
            PrinterRunActionLabels.resolvedAccessibilityValue(for: descriptor),
            ""
        )
    }

    func test_resolvedValue_forDisabledWithReason_isEmpty() {
        // The reason belongs in the hint, not the value — cross-component
        // consistency with issue #2519 is what this locks in.
        let descriptor = PrinterRunActionDescriptor(
            kind: .stop,
            isEnabled: false,
            unavailableReason: "no active job"
        )
        XCTAssertEqual(
            PrinterRunActionLabels.resolvedAccessibilityValue(for: descriptor),
            ""
        )
    }
}
