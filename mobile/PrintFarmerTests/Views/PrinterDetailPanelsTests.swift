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

    // MARK: - Panel availability / safe selection

    func testAvailablePanelsIncludesControlsOnlyWhenAvailable() {
        XCTAssertEqual(
            PrinterDetailPanelsHost<EmptyView, EmptyView>.availablePanels(controlsAvailable: true),
            [.status, .controls]
        )
        XCTAssertEqual(
            PrinterDetailPanelsHost<EmptyView, EmptyView>.availablePanels(controlsAvailable: false),
            [.status]
        )
    }

    func testResolvedSelectionKeepsControlsWhenStillAvailable() {
        let resolved = PrinterDetailPanelsHost<EmptyView, EmptyView>.resolvedSelection(
            current: .controls,
            controlsAvailable: true
        )
        XCTAssertEqual(resolved, .controls)
    }

    func testResolvedSelectionFallsBackToStatusWhenControlsRevoked() {
        // Mirrors the epic's "if revoked while selected, return safely to
        // Status without a stranded page" acceptance criterion.
        let resolved = PrinterDetailPanelsHost<EmptyView, EmptyView>.resolvedSelection(
            current: .controls,
            controlsAvailable: false
        )
        XCTAssertEqual(resolved, .status)
    }

    func testResolvedSelectionLeavesStatusUnaffectedByControlsAvailability() {
        XCTAssertEqual(
            PrinterDetailPanelsHost<EmptyView, EmptyView>.resolvedSelection(
                current: .status, controlsAvailable: true
            ),
            .status
        )
        XCTAssertEqual(
            PrinterDetailPanelsHost<EmptyView, EmptyView>.resolvedSelection(
                current: .status, controlsAvailable: false
            ),
            .status
        )
    }

    func testPanelAccessibilityIdentifiersMatchEpicReservation() {
        // Reserved by epic #2518: printer.detail.panel.selector / .status / .controls.
        XCTAssertEqual(PrinterDetailPanel.status.accessibilityIdentifier, "printer.detail.panel.status")
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

    func testRunActionMappingNeverShowsEmergencyStopWhenOfflineAndIdle() {
        let presentation = PrinterDetailRunActionMapping.presentation(
            isOnline: false, isPrinting: false, isPaused: false, isPerformingAction: false
        )
        XCTAssertFalse(presentation.visibleKinds.contains(.emergencyStop))
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
        XCTAssertEqual(presentation.visibleKinds, [.pause, .cancel])
        XCTAssertNil(presentation.descriptor(for: .stop))
        XCTAssertNil(presentation.descriptor(for: .emergencyStop))
        XCTAssertTrue(presentation.shouldFireCallback(for: .pause))
        XCTAssertTrue(presentation.shouldFireCallback(for: .cancel))
        XCTAssertFalse(presentation.shouldFireCallback(for: .stop))
        XCTAssertFalse(presentation.shouldFireCallback(for: .emergencyStop))
    }

    func testRunActionMappingWhileOfflineAndPausedKeepsResumeAndCancelButHidesStopAndEmergency() {
        let presentation = PrinterDetailRunActionMapping.presentation(
            isOnline: false, isPrinting: false, isPaused: true, isPerformingAction: false
        )
        XCTAssertEqual(presentation.visibleKinds, [.resume, .cancel])
        XCTAssertNil(presentation.descriptor(for: .stop))
        XCTAssertNil(presentation.descriptor(for: .emergencyStop))
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
        // Avoid a new capability request for a Status-only visit — the
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
            scenePhase: .active, selectedPanel: .status
        ))
    }

    func testCameraNotForegroundWhenControlsSelectedEvenIfSceneActive() {
        // The exact regression this mapping fixes: native `TabView` paging
        // keeps Status mounted alongside Controls for swipe animation, so
        // `scenePhase == .active` alone is not sufficient once Controls is
        // the page actually on screen.
        XCTAssertFalse(PrinterDetailCameraLifecycleMapping.isForeground(
            scenePhase: .active, selectedPanel: .controls
        ))
    }

    func testCameraNotForegroundWhenSceneInactiveOrBackgroundedEvenOnStatusPage() {
        XCTAssertFalse(PrinterDetailCameraLifecycleMapping.isForeground(
            scenePhase: .inactive, selectedPanel: .status
        ))
        XCTAssertFalse(PrinterDetailCameraLifecycleMapping.isForeground(
            scenePhase: .background, selectedPanel: .status
        ))
    }
}
