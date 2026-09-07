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
        // Hicks-flag: assert enabled STATE, not only visibility. A "paused"
        // fixture that silently shipped disabled Resume/Cancel/Stop would
        // still pass a visibility-only check but be unusable.
        for kind in [PrinterRunActionKind.resume, .cancel, .stop, .emergencyStop] {
            let descriptor = presentation.descriptor(for: kind)
            XCTAssertNotNil(descriptor, "Paused fixture must expose \(kind)")
            XCTAssertTrue(
                descriptor?.isEnabled ?? false,
                "Paused fixture must expose \(kind) as ENABLED, not just visible"
            )
            XCTAssertFalse(
                descriptor?.isPending ?? true,
                "Paused fixture must not report \(kind) as pending"
            )
            XCTAssertTrue(
                presentation.shouldFireCallback(for: kind),
                "Paused fixture must allow \(kind) to fire"
            )
        }
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
        // Hicks-flag: assert the surviving visible action is actually
        // enabled — a limited-permission operator who can still Pause must
        // hear/see an enabled Pause, not a dimmed one.
        let pause = presentation.descriptor(for: .pause)
        XCTAssertNotNil(pause)
        XCTAssertTrue(
            pause?.isEnabled ?? false,
            "Limited-permission fixture must expose Pause as ENABLED"
        )
        XCTAssertTrue(presentation.shouldFireCallback(for: .pause))
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

    // MARK: - Hint honesty (Cancel / Stop / Emergency Stop confirmation semantics)

    /// Cancel today runs through `PrinterDetailViewModel.requestCancel()`,
    /// which flips `showConfirmation = true` before dispatching. The hint
    /// must announce that the confirmation gate exists — a VoiceOver
    /// operator planning a destructive action deserves to hear "requires
    /// confirmation" before activating.
    func test_cancelHint_declaresConfirmation() {
        let hint = PrinterRunActionLabels.accessibilityHint(for: .cancel)
        XCTAssertTrue(
            hint.lowercased().contains("confirmation"),
            "Cancel hint must announce that confirmation is required: \"\(hint)\""
        )
    }

    /// Stop today runs through `PrinterDetailViewModel.stopPrinter()`,
    /// which dispatches immediately with no confirmation gate. The hint
    /// must NOT promise a confirmation that the host does not perform —
    /// a false promise is worse than silence for a VoiceOver operator
    /// deciding whether it is safe to activate. If the integrator (#2522)
    /// later adds a confirmation gate for Stop, they can update this hint
    /// in the same change.
    func test_stopHint_doesNotFalselyPromiseConfirmation() {
        let hint = PrinterRunActionLabels.accessibilityHint(for: .stop)
        XCTAssertFalse(
            hint.lowercased().contains("confirmation"),
            "Stop hint must NOT promise confirmation because stopPrinter() dispatches immediately: \"\(hint)\""
        )
        // Positive — the hint still describes what Stop does.
        XCTAssertTrue(
            hint.lowercased().contains("stops"),
            "Stop hint must describe the action: \"\(hint)\""
        )
    }

    // MARK: - Resolved hint composition — combined pending + disabled

    /// A pending descriptor may also be disabled while the command flushes
    /// (host might dim it as a defensive measure). Composition must give
    /// pending precedence: the static hint describes what the button will
    /// do once the pending resolves, which is the useful thing to
    /// announce; the disable reason would drop that description while the
    /// "Pending" value already carries the in-flight signal.
    func test_resolvedHint_forPendingAndDisabled_keepsStaticHint_notReason() {
        let descriptor = PrinterRunActionDescriptor(
            kind: .cancel,
            isEnabled: false,
            isPending: true,
            unavailableReason: "printer offline"
        )
        XCTAssertEqual(
            PrinterRunActionLabels.resolvedAccessibilityHint(for: descriptor),
            PrinterRunActionLabels.accessibilityHint(for: .cancel),
            "Pending + disabled must keep the static per-kind hint so the description survives; the disable reason must not overwrite it."
        )
        // And the value still announces pending — that is where the
        // in-flight signal lives.
        XCTAssertEqual(
            PrinterRunActionLabels.resolvedAccessibilityValue(for: descriptor),
            "Pending"
        )
    }

    // MARK: - Resolved traits

    /// Every enabled non-pending descriptor must expose plain `.isButton`
    /// traits — never `.isSelected`, which would announce "chosen/on" on
    /// what is really just a normal button. Locks in the sibling pattern
    /// from `HomeSubgroup` / `JogSubgroup` / `PreheatSubgroup` (issue
    /// #2519) so both surfaces on the printer-detail screen read the same
    /// to a VoiceOver operator once #2522 integrates.
    func test_resolvedTraits_forEnabledNonPending_isJustIsButton_notSelected() {
        for kind in PrinterRunActionKind.allCases {
            let descriptor = PrinterRunActionDescriptor(kind: kind)
            let traits = PrinterRunActionLabels.resolvedAccessibilityTraits(for: descriptor)
            XCTAssertTrue(
                traits.contains(.isButton),
                "\(kind): enabled non-pending descriptor must carry .isButton"
            )
            XCTAssertFalse(
                traits.contains(.isSelected),
                "\(kind): enabled non-pending descriptor must NOT carry .isSelected"
            )
            XCTAssertFalse(
                traits.contains(.updatesFrequently),
                "\(kind): enabled non-pending descriptor must NOT carry .updatesFrequently"
            )
        }
    }

    /// Pending descriptors must expose `.updatesFrequently` so VoiceOver
    /// re-reads state as the pending resolves. Must NOT carry `.isSelected`
    /// — this was the review finding that produced this test.
    func test_resolvedTraits_forPending_isUpdatesFrequently_notSelected() {
        let descriptor = PrinterRunActionDescriptor(
            kind: .cancel,
            isEnabled: true,
            isPending: true
        )
        let traits = PrinterRunActionLabels.resolvedAccessibilityTraits(for: descriptor)
        XCTAssertTrue(
            traits.contains(.updatesFrequently),
            "Pending descriptor must carry .updatesFrequently so VoiceOver re-reads state"
        )
        XCTAssertFalse(
            traits.contains(.isSelected),
            "Pending descriptor must NOT carry .isSelected — pending is not chosen"
        )
    }

    /// A disabled descriptor is announced as "dimmed" by SwiftUI's own
    /// `.disabled(...)` modifier plus the host reason in the hint. It must
    /// NOT carry `.isSelected` — that trait means "chosen/on", which on a
    /// dimmed destructive action like a disabled Emergency Stop would read
    /// as "armed" to a VoiceOver operator. This was the review finding.
    func test_resolvedTraits_forDisabledNonPending_isJustIsButton_notSelected() {
        for kind in PrinterRunActionKind.allCases {
            let descriptor = PrinterRunActionDescriptor(
                kind: kind,
                isEnabled: false,
                unavailableReason: "printer offline"
            )
            let traits = PrinterRunActionLabels.resolvedAccessibilityTraits(for: descriptor)
            XCTAssertTrue(
                traits.contains(.isButton),
                "\(kind): disabled descriptor must still carry .isButton"
            )
            XCTAssertFalse(
                traits.contains(.isSelected),
                "\(kind): disabled descriptor must NOT carry .isSelected — a dimmed Emergency Stop reading as 'armed' is a safety concern"
            )
            XCTAssertFalse(
                traits.contains(.updatesFrequently),
                "\(kind): disabled non-pending descriptor must NOT carry .updatesFrequently"
            )
        }
    }
}
