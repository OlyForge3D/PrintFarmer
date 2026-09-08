import XCTest
import SwiftUI
@testable import PrintFarmer

/// Unit tests for the pure logic extracted into `PrinterDetailPanelsHost.swift`
/// (issue #2522): panel availability/selection-safety, the run-action
/// presentation binding table, the filament-action mapping and the coverage
/// state mapping. All of these are plain static functions, so they are
/// exercised here without hosting a view, a live view model, or a simulator.
@MainActor
final class PrinterDetailPanelsTests: XCTestCase {

    func testControlsReflowRetainsSelectedJogAxisAndDistance() async throws {
        var printer = try TestData.decodePrinter(from: TestJSON.printer)
        printer.isOnline = true
        printer.state = "idle"
        let service = MockPrinterService()
        service.capabilitiesToReturn = PrinterBackendCapabilities(
            supportsMovement: true, supportsTemperatureControl: true,
            supportsBedTemperature: true, supportsFanControl: true,
            supportsHoming: true, supportedAxes: ["X", "Y", "Z"]
        )
        let model = PrinterControlsViewModel(printerService: service, printer: printer)
        await model.loadCapabilities()
        service.getBackendCapabilitiesCalledWith = nil

        func content(width: CGFloat, size: DynamicTypeSize) -> some View {
            PrinterSetupControlsContent(
                printer: printer, viewModel: model,
                usesColumns: PrinterDetailLayout.usesColumns(width: width, dynamicTypeSize: size)
            )
            .frame(width: width)
            .environment(\.horizontalSizeClass, .regular)
            .environment(\.dynamicTypeSize, size)
        }
        let controller = UIHostingController(rootView: AnyView(content(width: 900, size: .large)))
        let window = UIWindow(frame: CGRect(x: 0, y: 0, width: 1100, height: 1400))
        window.rootViewController = controller
        window.isHidden = false
        defer { window.isHidden = true }

        func settle() async throws {
            try await Task.sleep(for: .milliseconds(100))
            controller.view.setNeedsLayout()
            controller.view.layoutIfNeeded()
        }
        func segmentedControls(in view: UIView) -> [UISegmentedControl] {
            (view as? UISegmentedControl).map { [$0] }
                ?? view.subviews.flatMap { segmentedControls(in: $0) }
        }
        func pickers() throws -> (axis: UISegmentedControl, step: UISegmentedControl) {
            let controls = segmentedControls(in: controller.view)
            return (
                try XCTUnwrap(controls.first { $0.titleForSegment(at: 0) == "X" }),
                try XCTUnwrap(controls.first { $0.titleForSegment(at: 0) == "0.1" })
            )
        }
        try await settle()
        let initial = try pickers()
        initial.axis.selectedSegmentIndex = 2
        initial.axis.sendActions(for: .valueChanged)
        initial.step.selectedSegmentIndex = 2
        initial.step.sendActions(for: .valueChanged)
        try await settle()

        let layouts: [(CGFloat, DynamicTypeSize)] = [
            (700, .large), (900, .large), (900, .accessibility3), (900, .large)
        ]
        for (width, size) in layouts {
            controller.rootView = AnyView(content(width: width, size: size))
            try await settle()
            let current = try pickers()
            XCTAssertEqual(current.axis.selectedSegmentIndex, 2, "Reflow must preserve selected Z")
            XCTAssertEqual(current.step.selectedSegmentIndex, 2, "Reflow must preserve selected 10 mm")
        }
        XCTAssertNil(service.moveCalledWith)
        XCTAssertNil(service.getBackendCapabilitiesCalledWith)
    }

    func testDetailColumnsRequireUsableWidthAndNonAccessibilityText() {
        XCTAssertTrue(PrinterDetailLayout.usesColumns(width: 1024, dynamicTypeSize: .large))
        XCTAssertTrue(PrinterDetailLayout.usesColumns(width: 760, dynamicTypeSize: .xxxLarge))
        XCTAssertFalse(PrinterDetailLayout.usesColumns(width: 759, dynamicTypeSize: .large))
        XCTAssertFalse(PrinterDetailLayout.usesColumns(width: 375, dynamicTypeSize: .large))
        XCTAssertFalse(PrinterDetailLayout.usesColumns(width: 1366, dynamicTypeSize: .accessibility1))
        XCTAssertFalse(PrinterDetailLayout.usesColumns(width: 1366, dynamicTypeSize: .accessibility5))
    }

    func testTemperatureMeasurementNeverSubstitutesTarget() {
        let reading = PrinterDetailTemperatureReading(measured: 24, target: 200, isOnline: true)
        XCTAssertEqual(reading.measuredText, Double(24).temperatureFormatted)
        XCTAssertEqual(reading.targetText, Double(200).temperatureFormatted)
        let missing = PrinterDetailTemperatureReading(measured: nil, target: 200, isOnline: true)
        XCTAssertEqual(missing.measuredText, "Unavailable")
        XCTAssertEqual(missing.targetText, Double(200).temperatureFormatted)
    }

    func testOfflineTemperaturesDoNotPresentRetainedValuesAsLive() {
        let reading = PrinterDetailTemperatureReading(measured: 210, target: 220, isOnline: false)
        XCTAssertEqual(reading.measuredText, "Unavailable")
        XCTAssertEqual(reading.targetText, "Unknown")
    }

    func testMissingHardwareTelemetryIsNotZeroOrInferredHardwareAbsence() {
        let reading = PrinterDetailTemperatureReading(measured: nil, target: nil, isOnline: true)
        XCTAssertEqual(reading.measuredText, "Unavailable")
        XCTAssertEqual(reading.targetText, "Unknown")
        let heaterOff = PrinterDetailTemperatureReading(measured: 0, target: 0, isOnline: true)
        XCTAssertEqual(heaterOff.measuredText, Double(0).temperatureFormatted)
        XCTAssertEqual(heaterOff.targetText, "Off")
        let invalid = PrinterDetailTemperatureReading(measured: .nan, target: .infinity, isOnline: true)
        XCTAssertEqual(invalid.measuredText, "Unavailable")
        XCTAssertEqual(invalid.targetText, "Unknown")
    }

    func testEmergencyAndRoutinePresentationsPartitionWithoutChangingGates() {
        let presentation = PrinterDetailRunActionMapping.presentation(
            isOnline: true, isPrinting: true, isPaused: false,
            isPerformingAction: true, pendingKinds: [.pause]
        )
        XCTAssertEqual(presentation.routineActions.visibleKinds, [.pause, .cancel, .stop])
        XCTAssertEqual(presentation.emergencyAction.visibleKinds, [.emergencyStop])
        XCTAssertEqual(
            presentation.routineActions.descriptor(for: .pause),
            presentation.descriptor(for: .pause)
        )
        XCTAssertTrue(presentation.emergencyAction.shouldFireCallback(for: .emergencyStop))
        XCTAssertFalse(presentation.routineActions.shouldFireCallback(for: .emergencyStop))
    }

    // MARK: - Panel availability / safe selection

    func testAvailablePanelsAlwaysIncludesControlsRegardlessOfAuthorization() {
        XCTAssertEqual(
            PrinterDetailPanelsHost<EmptyView, EmptyView>.availablePanels(controlsAvailable: true),
            [.overview, .controls]
        )
        XCTAssertEqual(
            PrinterDetailPanelsHost<EmptyView, EmptyView>.availablePanels(controlsAvailable: false),
            [.overview, .controls]
        )
    }

    func testResolvedSelectionKeepsControlsWhenStillAvailable() {
        let resolved = PrinterDetailPanelsHost<EmptyView, EmptyView>.resolvedSelection(
            current: .controls,
            controlsAvailable: true
        )
        XCTAssertEqual(resolved, .controls)
    }

    func testResolvedSelectionRetainsControlsWhenAccessRevoked() {
        let resolved = PrinterDetailPanelsHost<EmptyView, EmptyView>.resolvedSelection(
            current: .controls,
            controlsAvailable: false
        )
        XCTAssertEqual(resolved, .controls)
    }

    func testResolvedSelectionLeavesStatusUnaffectedByControlsAvailability() {
        XCTAssertEqual(
            PrinterDetailPanelsHost<EmptyView, EmptyView>.resolvedSelection(
                current: .overview, controlsAvailable: true
            ),
            .overview
        )
        XCTAssertEqual(
            PrinterDetailPanelsHost<EmptyView, EmptyView>.resolvedSelection(
                current: .overview, controlsAvailable: false
            ),
            .overview
        )
    }

    func testPanelAccessibilityIdentifiersMatchEpicReservation() {
        // Reserved by epic #2518: printer.detail.panel.selector / .overview / .controls.
        XCTAssertEqual(PrinterDetailPanel.overview.accessibilityIdentifier, "printer.detail.panel.overview")
        XCTAssertEqual(PrinterDetailPanel.controls.accessibilityIdentifier, "printer.detail.panel.controls")
    }

    // MARK: - Run-action presentation mapping

    func testRunActionMappingWhilePrintingShowsPauseCancelStopAndEmergency() {
        let presentation = PrinterDetailRunActionMapping.presentation(
            isOnline: true, isPrinting: true, isPaused: false, isPerformingAction: false
        )
        XCTAssertEqual(presentation.visibleKinds, [.pause, .cancel, .stop, .emergencyStop])
    }

    func testRunActionMappingWhilePausedShowsResumeCancelStopAndEmergency() {
        let presentation = PrinterDetailRunActionMapping.presentation(
            isOnline: true, isPrinting: false, isPaused: true, isPerformingAction: false
        )
        XCTAssertEqual(presentation.visibleKinds, [.resume, .cancel, .stop, .emergencyStop])
    }

    func testRunActionMappingWhileIdleOnlineShowsOnlyEmergencyStop() {
        let presentation = PrinterDetailRunActionMapping.presentation(
            isOnline: true, isPrinting: false, isPaused: false, isPerformingAction: false
        )
        XCTAssertEqual(presentation.visibleKinds, [.emergencyStop])
    }

    func testRunActionMappingExplainsUnavailableEmergencyStopWhenOffline() {
        let presentation = PrinterDetailRunActionMapping.presentation(
            isOnline: false, isPrinting: false, isPaused: false, isPerformingAction: false
        )
        XCTAssertTrue(presentation.visibleKinds.contains(.emergencyStop))
        XCTAssertFalse(presentation.shouldFireCallback(for: .emergencyStop))
        XCTAssertEqual(
            presentation.descriptor(for: .emergencyStop)?.unavailableReason,
            "Printer is offline. Use the physical safety switch if needed."
        )
    }

    func testRunActionMappingWhileOfflineAndPrintingKeepsPauseAndCancelButHidesStopAndEmergency() {
        // Hicks review finding 13: restores parity with the ORIGINAL
        // `primaryControlsRow` (header), which exposed Pause/Resume/Cancel
        // purely from print state and the pending guard, with NO online
        // check at all. Only the original `actionSection`'s Stop and
        // Emergency Stop lived behind `if printer.isOnline`. A blanket
        // `isOnline` gate over every descriptor (an earlier revision of
        // this mapping) was stricter than either original surface for
        // Pause/Resume/Cancel.
        let presentation = PrinterDetailRunActionMapping.presentation(
            isOnline: false, isPrinting: true, isPaused: false, isPerformingAction: false
        )
        XCTAssertEqual(presentation.visibleKinds, [.pause, .cancel, .emergencyStop])
        XCTAssertNil(presentation.descriptor(for: .stop))
        XCTAssertEqual(presentation.descriptor(for: .emergencyStop)?.isEnabled, false)
        XCTAssertTrue(presentation.shouldFireCallback(for: .pause))
        XCTAssertTrue(presentation.shouldFireCallback(for: .cancel))
        XCTAssertFalse(presentation.shouldFireCallback(for: .stop))
        XCTAssertFalse(presentation.shouldFireCallback(for: .emergencyStop))
    }

    func testRunActionMappingWhileOfflineAndPausedKeepsResumeAndCancelButHidesStopAndEmergency() {
        let presentation = PrinterDetailRunActionMapping.presentation(
            isOnline: false, isPrinting: false, isPaused: true, isPerformingAction: false
        )
        XCTAssertEqual(presentation.visibleKinds, [.resume, .cancel, .emergencyStop])
        XCTAssertNil(presentation.descriptor(for: .stop))
        XCTAssertEqual(presentation.descriptor(for: .emergencyStop)?.isEnabled, false)
    }

    func testRunActionMappingWhileOfflineAndPausedResumeAndCancelStillFireCallbacks() {
        let presentation = PrinterDetailRunActionMapping.presentation(
            isOnline: false, isPrinting: false, isPaused: true, isPerformingAction: false
        )
        XCTAssertTrue(presentation.shouldFireCallback(for: .resume))
        XCTAssertTrue(presentation.shouldFireCallback(for: .cancel))
        XCTAssertFalse(presentation.shouldFireCallback(for: .stop))
    }

    func testRunActionMappingDisablesPauseCancelStopWhilePerformingAction() {
        let presentation = PrinterDetailRunActionMapping.presentation(
            isOnline: true, isPrinting: true, isPaused: false, isPerformingAction: true
        )
        XCTAssertFalse(presentation.descriptor(for: .pause)?.isEnabled ?? true)
        XCTAssertFalse(presentation.descriptor(for: .cancel)?.isEnabled ?? true)
        XCTAssertFalse(presentation.descriptor(for: .stop)?.isEnabled ?? true)
    }

    func testRunActionMappingNeverDisablesEmergencyStopWhilePerformingAction() {
        // Epic #2518 acceptance criterion: do not blanket-disable Emergency
        // Stop because another action is pending.
        let presentation = PrinterDetailRunActionMapping.presentation(
            isOnline: true, isPrinting: true, isPaused: false, isPerformingAction: true
        )
        XCTAssertEqual(presentation.descriptor(for: .emergencyStop)?.isEnabled, true)
        XCTAssertTrue(presentation.shouldFireCallback(for: .emergencyStop))
    }

    // MARK: - isPending threading (issue #2522, Vasquez review finding)

    func testRunActionMappingMarksOnlyTheInFlightKindAsPending() {
        let presentation = PrinterDetailRunActionMapping.presentation(
            isOnline: true, isPrinting: true, isPaused: false, isPerformingAction: true,
            pendingKinds: [.pause]
        )
        XCTAssertEqual(presentation.descriptor(for: .pause)?.isPending, true)
        XCTAssertEqual(presentation.descriptor(for: .cancel)?.isPending, false)
        XCTAssertEqual(presentation.descriptor(for: .stop)?.isPending, false)
        XCTAssertEqual(presentation.descriptor(for: .emergencyStop)?.isPending, false)
    }

    func testRunActionMappingMarksEmergencyStopPendingWhileItselfInFlightEvenThoughAlwaysEnabled() {
        // Epic #2518's "never blanket-disable Emergency Stop" acceptance
        // criterion is about `isEnabled`, not `isPending` — the button must
        // still announce its OWN in-flight state and reject re-entrant taps
        // while genuinely dispatched.
        let presentation = PrinterDetailRunActionMapping.presentation(
            isOnline: true, isPrinting: true, isPaused: false, isPerformingAction: true,
            pendingKinds: [.emergencyStop]
        )
        let emergencyStop = presentation.descriptor(for: .emergencyStop)
        XCTAssertEqual(emergencyStop?.isEnabled, true)
        XCTAssertEqual(emergencyStop?.isPending, true)
        XCTAssertFalse(
            presentation.shouldFireCallback(for: .emergencyStop),
            "A pending Emergency Stop must reject a re-entrant tap even though isEnabled stays true"
        )
    }

    func testRunActionMappingDefaultsToNoPendingKindsWhenOmitted() {
        // The `pendingKinds` parameter defaults to empty so every call site
        // written before this parameter existed keeps compiling with
        // unchanged behavior.
        let presentation = PrinterDetailRunActionMapping.presentation(
            isOnline: true, isPrinting: true, isPaused: false, isPerformingAction: false
        )
        XCTAssertEqual(presentation.descriptor(for: .pause)?.isPending, false)
        XCTAssertEqual(presentation.descriptor(for: .emergencyStop)?.isPending, false)
    }

    // MARK: - Filament action mapping

    func testFilamentSupportedActionsWithoutActiveSpoolOffersSetAndScanNFC() {
        let kinds = PrinterDetailFilamentActionMapping.supportedActions(hasActiveSpool: false)
        XCTAssertEqual(kinds, [.set, .scanNFC])
    }

    func testFilamentSupportedActionsWithActiveSpoolOffersChangeAndClear() {
        let kinds = PrinterDetailFilamentActionMapping.supportedActions(hasActiveSpool: true)
        XCTAssertEqual(kinds, [.change, .clearAssignment, .scanNFC])
    }

    func testFilamentActionsNeverIncludeGuidedSwapOrSlotTargets() {
        let printerID = UUID()
        let actions = PrinterDetailFilamentActionMapping.actions(
            printerID: printerID, hasActiveSpool: true, isPerformingAction: false, nfcAvailable: true
        )
        XCTAssertFalse(actions.contains { $0.kind == .guidedSwap })
        for action in actions {
            XCTAssertEqual(action.target, .printer(printerID))
        }
    }

    func testFilamentActionsWithoutActiveSpoolOffersSetNotChangeOrClear() {
        let actions = PrinterDetailFilamentActionMapping.actions(
            printerID: UUID(), hasActiveSpool: false, isPerformingAction: false, nfcAvailable: true
        )
        XCTAssertTrue(actions.contains { $0.kind == .set })
        XCTAssertFalse(actions.contains { $0.kind == .change })
        XCTAssertFalse(actions.contains { $0.kind == .clearAssignment })
    }

    func testFilamentActionsDisableChangeAndClearWhilePerformingAction() {
        let actions = PrinterDetailFilamentActionMapping.actions(
            printerID: UUID(), hasActiveSpool: true, isPerformingAction: true, nfcAvailable: true
        )
        let change = actions.first { $0.kind == .change }
        let clear = actions.first { $0.kind == .clearAssignment }
        XCTAssertNotNil(change?.disabledReason)
        XCTAssertNotNil(clear?.disabledReason)
    }

    func testFilamentActionsDisableSetWhilePerformingAction() {
        // Vasquez review finding 8: `.set` dispatches the same single-flight
        // `setActiveSpool` path as `.change`/`.clearAssignment` via the
        // spool-picker sheet, so it must be disabled while busy too, not
        // left tappable just because no spool is currently assigned.
        let actions = PrinterDetailFilamentActionMapping.actions(
            printerID: UUID(), hasActiveSpool: false, isPerformingAction: true, nfcAvailable: true
        )
        let set = actions.first { $0.kind == .set }
        XCTAssertNotNil(set?.disabledReason)
    }

    func testFilamentActionsEnableSetWhenNotPerformingAction() {
        let actions = PrinterDetailFilamentActionMapping.actions(
            printerID: UUID(), hasActiveSpool: false, isPerformingAction: false, nfcAvailable: true
        )
        let set = actions.first { $0.kind == .set }
        XCTAssertNil(set?.disabledReason)
    }

    func testFilamentActionsDisableScanNFCWhenUnavailable() {
        let actions = PrinterDetailFilamentActionMapping.actions(
            printerID: UUID(), hasActiveSpool: false, isPerformingAction: false, nfcAvailable: false
        )
        let scan = actions.first { $0.kind == .scanNFC }
        XCTAssertNotNil(scan?.disabledReason)
    }

    func testFilamentActionsDisableScanNFCWhilePerformingActionEvenWhenAvailable() {
        // Vasquez review finding 8: NFC availability and the single-flight
        // busy state are independent gates on the same action; a busy scan
        // must stay disabled even though NFC hardware is present.
        let actions = PrinterDetailFilamentActionMapping.actions(
            printerID: UUID(), hasActiveSpool: false, isPerformingAction: true, nfcAvailable: true
        )
        let scan = actions.first { $0.kind == .scanNFC }
        XCTAssertNotNil(scan?.disabledReason)
    }

    func testFilamentActionsEnableScanNFCWhenAvailable() {
        let actions = PrinterDetailFilamentActionMapping.actions(
            printerID: UUID(), hasActiveSpool: false, isPerformingAction: false, nfcAvailable: true
        )
        let scan = actions.first { $0.kind == .scanNFC }
        XCTAssertNil(scan?.disabledReason)
    }

    // MARK: - Coverage state mapping

    func testCoverageStateMappingDisabledWhenCapabilityOff() {
        let state = PrinterDetailFilamentCoverageStateMapping.coverageState(
            featureEnabled: false, isFeatureDisabled: false, isPrinterNotFound: false,
            hasCoverage: true, lastLoadError: nil
        )
        XCTAssertEqual(state, .disabled)
    }

    func testCoverageStateMappingDisabledWhenViewModelReportsFeatureDisabled() {
        let state = PrinterDetailFilamentCoverageStateMapping.coverageState(
            featureEnabled: true, isFeatureDisabled: true, isPrinterNotFound: false,
            hasCoverage: false, lastLoadError: nil
        )
        XCTAssertEqual(state, .disabled)
    }

    func testCoverageStateMappingUnavailableWhenPrinterNotFound() {
        let state = PrinterDetailFilamentCoverageStateMapping.coverageState(
            featureEnabled: true, isFeatureDisabled: false, isPrinterNotFound: true,
            hasCoverage: false, lastLoadError: nil
        )
        XCTAssertEqual(state, .unavailable)
    }

    func testCoverageStateMappingFailedWhenErrorAndNoCoverage() {
        let state = PrinterDetailFilamentCoverageStateMapping.coverageState(
            featureEnabled: true, isFeatureDisabled: false, isPrinterNotFound: false,
            hasCoverage: false, lastLoadError: "network down"
        )
        XCTAssertEqual(state, .failed("network down"))
    }

    func testCoverageStateMappingFailedWhenRetainedCoverageAndLatestRefreshErrored() {
        // Hicks review finding 9: in the real view model, `commitSuccess`
        // always clears `lastLoadError`, so `hasCoverage: true` with
        // `lastLoadError` non-nil can only mean a RETAINED snapshot from an
        // earlier success plus a LATER canonical refresh that failed via
        // `commitError` (which never clears `coverage`). That must report
        // `.failed`, not `.available` — presenting retained data as current
        // with every action enabled would be dishonest. `.failed` in turn
        // makes `PrinterFilamentPresentation` mark the printer stale
        // ("Last confirmed" wording, no enabled actions).
        let state = PrinterDetailFilamentCoverageStateMapping.coverageState(
            featureEnabled: true, isFeatureDisabled: false, isPrinterNotFound: false,
            hasCoverage: true, lastLoadError: "network down on refresh"
        )
        XCTAssertEqual(state, .failed("network down on refresh"))
    }

    func testCoverageStateMappingFailedWhenFeatureDisabledFlagStaleAfterLaterNetworkError() {
        // Hicks review finding 12: `commitError` clears neither
        // `isFeatureDisabled` nor `isPrinterNotFound`, so a sticky
        // `isFeatureDisabled` left over from an OLDER commit must not
        // override a LATER network failure. disabled -> network failure
        // must report `.failed`, not resurrect the stale `.disabled`.
        let state = PrinterDetailFilamentCoverageStateMapping.coverageState(
            featureEnabled: true, isFeatureDisabled: true, isPrinterNotFound: false,
            hasCoverage: false, lastLoadError: "network down after feature-disabled commit"
        )
        XCTAssertEqual(state, .failed("network down after feature-disabled commit"))
    }

    func testCoverageStateMappingFailedWhenNotFoundFlagStaleAfterLaterNetworkError() {
        // Hicks review finding 12: not-found -> network failure must report
        // `.failed`, not resurrect the stale `.unavailable`.
        let state = PrinterDetailFilamentCoverageStateMapping.coverageState(
            featureEnabled: true, isFeatureDisabled: false, isPrinterNotFound: true,
            hasCoverage: false, lastLoadError: "network down after not-found commit"
        )
        XCTAssertEqual(state, .failed("network down after not-found commit"))
    }

    func testCoverageStateMappingLoadingWhenNothingConcludedYet() {
        let state = PrinterDetailFilamentCoverageStateMapping.coverageState(
            featureEnabled: true, isFeatureDisabled: false, isPrinterNotFound: false,
            hasCoverage: false, lastLoadError: nil
        )
        XCTAssertEqual(state, .loading)
    }

    // MARK: - Controls owner mapping (Hicks review finding 15)

    func testControlsOwnerMappingSkipsBuildWhenControlsUnavailable() {
        // Avoid a new capability request for a Overview-only visit — the
        // common case, since Advanced Printer Controls defaults off.
        XCTAssertFalse(PrinterDetailControlsOwnerMapping.shouldBuildOwner(
            existingOwnerPrinterID: nil, printerID: UUID(), controlsAvailable: false
        ))
    }

    func testControlsOwnerMappingBuildsWhenControlsAvailableAndNoExistingOwner() {
        let printerID = UUID()
        XCTAssertTrue(PrinterDetailControlsOwnerMapping.shouldBuildOwner(
            existingOwnerPrinterID: nil, printerID: printerID, controlsAvailable: true
        ))
    }

    func testControlsOwnerMappingRetainsExistingOwnerForSamePrinterEvenWhenAvailableAgain() {
        // An owner already built for this printer must survive a transition
        // back to unavailable (offline, or the safety toggle revoked) and
        // must not be rebuilt just because availability flips back to true.
        let printerID = UUID()
        XCTAssertFalse(PrinterDetailControlsOwnerMapping.shouldBuildOwner(
            existingOwnerPrinterID: printerID, printerID: printerID, controlsAvailable: true
        ))
    }

    func testControlsOwnerMappingReplacesOwnerWhenPrinterTargetChanges() {
        let oldID = UUID()
        let newID = UUID()
        XCTAssertTrue(PrinterDetailControlsOwnerMapping.shouldBuildOwner(
            existingOwnerPrinterID: oldID, printerID: newID, controlsAvailable: true
        ))
    }

    // MARK: - Filament staleness mapping (Hicks review finding 16, reversing
    // the interim fix from Bishop review finding 6)

    func testFilamentStaleMappingTrueWhileShowingStaleCacheEvenBeforeCanonicalLoadConcludes() {
        // While a canonical refresh is still in flight, on-screen coverage
        // is UNCONFIRMED cached data. It must be treated as stale — last-
        // confirmed wording, no enabled mutation actions — for the whole
        // time the cache flag is set, not only once the refresh concludes.
        // An earlier revision ANDed this with `hasConcludedCanonicalLoad`
        // and asserted `false` here, which let mutation actions stay
        // enabled against data the current session had not yet confirmed.
        XCTAssertTrue(PrinterDetailFilamentStaleMapping.isStale(isShowingStaleCache: true))
    }

    func testFilamentStaleMappingFalseWhenNotShowingStaleCache() {
        XCTAssertFalse(PrinterDetailFilamentStaleMapping.isStale(isShowingStaleCache: false))
    }

    // MARK: - Camera lifecycle mapping (Hicks review finding 19)

    func testCameraForegroundTrueOnlyWhenSceneActiveAndStatusSelected() {
        XCTAssertTrue(PrinterDetailCameraLifecycleMapping.isForeground(
            scenePhase: .active, selectedPanel: .overview
        ))
    }

    func testCameraNotForegroundWhenControlsSelectedEvenIfSceneActive() {
        // The exact regression this mapping fixes: native `TabView` paging
        // keeps Overview mounted alongside Controls for swipe animation, so
        // `scenePhase == .active` alone is not sufficient once Controls is
        // the page actually on screen.
        XCTAssertFalse(PrinterDetailCameraLifecycleMapping.isForeground(
            scenePhase: .active, selectedPanel: .controls
        ))
    }

    func testCameraNotForegroundWhenSceneInactiveOrBackgroundedEvenOnStatusPage() {
        XCTAssertFalse(PrinterDetailCameraLifecycleMapping.isForeground(
            scenePhase: .inactive, selectedPanel: .overview
        ))
        XCTAssertFalse(PrinterDetailCameraLifecycleMapping.isForeground(
            scenePhase: .background, selectedPanel: .overview
        ))
    }
}
