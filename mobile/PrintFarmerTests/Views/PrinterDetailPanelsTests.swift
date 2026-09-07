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

    func testRunActionMappingHidesEveryActionWhenOfflineWhilePrinting() {
        // The original `actionSection` (which held Pause/Resume/Cancel/Stop
        // and Emergency Stop together) only rendered at all `if
        // printer.isOnline`. A printer reporting offline while retaining a
        // stale `printing` state must not expose Stop (or any other run
        // action, including Emergency Stop) as if it were still reachable.
        let presentation = PrinterDetailRunActionMapping.presentation(
            isOnline: false, isPrinting: true, isPaused: false, isPerformingAction: false
        )
        XCTAssertTrue(presentation.visibleKinds.isEmpty)
        XCTAssertNil(presentation.descriptor(for: .stop))
        XCTAssertNil(presentation.descriptor(for: .cancel))
        XCTAssertNil(presentation.descriptor(for: .emergencyStop))
        XCTAssertFalse(presentation.shouldFireCallback(for: .stop))
        XCTAssertFalse(presentation.shouldFireCallback(for: .emergencyStop))
    }

    func testRunActionMappingHidesEveryActionWhenOfflineWhilePaused() {
        let presentation = PrinterDetailRunActionMapping.presentation(
            isOnline: false, isPrinting: false, isPaused: true, isPerformingAction: false
        )
        XCTAssertTrue(presentation.visibleKinds.isEmpty)
        XCTAssertNil(presentation.descriptor(for: .resume))
        XCTAssertNil(presentation.descriptor(for: .stop))
        XCTAssertNil(presentation.descriptor(for: .emergencyStop))
        XCTAssertFalse(presentation.shouldFireCallback(for: .resume))
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

    func testCoverageStateMappingLoadingWhenNothingConcludedYet() {
        let state = PrinterDetailFilamentCoverageStateMapping.coverageState(
            featureEnabled: true, isFeatureDisabled: false, isPrinterNotFound: false,
            hasCoverage: false, lastLoadError: nil
        )
        XCTAssertEqual(state, .loading)
    }

    // MARK: - Filament staleness mapping (Bishop review finding 6)

    func testFilamentStaleMappingFalseDuringWarmCacheHydrationBeforeConclusion() {
        // Cache hydrated but the canonical load has not concluded yet — must
        // NOT disable filament actions during ordinary warm-cache hydration.
        // This is exactly the bug: passing raw `isShowingStaleCache` here
        // would return true and empty every supported filament action.
        XCTAssertFalse(PrinterDetailFilamentStaleMapping.isStale(
            isShowingStaleCache: true, hasConcludedCanonicalLoad: false
        ))
    }

    func testFilamentStaleMappingTrueOnceCanonicalLoadConcludedWithoutClearingCache() {
        // A concluded pass (including one ending in a generic error, which
        // never clears `isShowingStaleCache`) that left the cache flag set
        // must genuinely disable filament actions — the data really is
        // unconfirmed.
        XCTAssertTrue(PrinterDetailFilamentStaleMapping.isStale(
            isShowingStaleCache: true, hasConcludedCanonicalLoad: true
        ))
    }

    func testFilamentStaleMappingFalseWhenNotShowingStaleCache() {
        XCTAssertFalse(PrinterDetailFilamentStaleMapping.isStale(
            isShowingStaleCache: false, hasConcludedCanonicalLoad: true
        ))
        XCTAssertFalse(PrinterDetailFilamentStaleMapping.isStale(
            isShowingStaleCache: false, hasConcludedCanonicalLoad: false
        ))
    }
}
