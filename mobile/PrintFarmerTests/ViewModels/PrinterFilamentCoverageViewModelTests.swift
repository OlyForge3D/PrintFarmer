import XCTest
@testable import PrintFarmer

// MARK: - Printer Filament Coverage ViewModel Tests (F4-M / #778)
//
// Symmetric to `FarmFilamentCoverageViewModelTests` for the per-
// printer VM. Same deterministic discipline (cycle-3 blocker C):
// positive dispatches use `waitForCommittedGeneration`, absence
// dispatches use `waitForCallbackTick` (advanced INSIDE the callback
// body), and post-teardown absence uses the structural
// `subscriberCount == 0` proof.

@MainActor
final class PrinterFilamentCoverageViewModelTests: XCTestCase {

    private let printerA = UUID(uuidString: "AAAAAAAA-1111-1111-1111-111111111111")!
    private let printerB = UUID(uuidString: "BBBBBBBB-2222-2222-2222-222222222222")!

    // MARK: - Invalidation filtering

    /// A scoped invalidation for a DIFFERENT printer must not cause a
    /// refetch on this VM. Absence proved by callback-tick barrier.
    func testScopedInvalidationForOtherPrinterDoesNotRefetch() async {
        let service = ControlledFilamentCoverageService()
        let signalR = MockSignalRService()
        let vm = PrinterFilamentCoverageViewModel(printerId: printerA)
        vm.configure(coverageService: service)
        vm.configureSignalR(signalR)

        async let initial: Void = vm.load()
        await service.awaitPending(count: 1)
        await service.completeSuccess(index: 0, printer: Self.coverage(for: printerA, status: .covers))
        _ = await initial
        XCTAssertEqual(vm.dispatchedRequestCount, 1)

        let tickBefore = vm.callbackTickForTesting
        signalR.simulateFilamentCoverageChanged(FilamentCoverageChangedEvent(
            printerId: printerB, reason: "unrelated", occurredAt: Date()
        ))
        // Barrier: wait until the callback body has run and filtered out.
        await vm.waitForCallbackTick(atLeast: tickBefore + 1)

        XCTAssertEqual(vm.dispatchedRequestCount, 1,
                       "Detail VM must ignore invalidations scoped to a different printer.")
    }

    func testFleetScopedInvalidationRefetchesDetail() async {
        let service = ControlledFilamentCoverageService()
        let signalR = MockSignalRService()
        let vm = PrinterFilamentCoverageViewModel(printerId: printerA)
        vm.configure(coverageService: service)
        vm.configureSignalR(signalR)

        async let initial: Void = vm.load()
        await service.awaitPending(count: 1)
        await service.completeSuccess(index: 0, printer: Self.coverage(for: printerA, status: .covers))
        _ = await initial
        XCTAssertEqual(vm.dispatchedRequestCount, 1)

        signalR.simulateFilamentCoverageChanged(FilamentCoverageChangedEvent(
            printerId: nil, reason: "fleet-scope", occurredAt: Date()
        ))
        await service.awaitPending(count: 1)
        await service.completeSuccess(index: 0, printer: Self.coverage(for: printerA, status: .runout))
        await vm.waitForCommittedGeneration(atLeast: 2)
        XCTAssertEqual(vm.dispatchedRequestCount, 2)
        XCTAssertEqual(vm.coverage?.status, .runout)
    }

    func testMatchingPrinterInvalidationRefetches() async {
        let service = ControlledFilamentCoverageService()
        let signalR = MockSignalRService()
        let vm = PrinterFilamentCoverageViewModel(printerId: printerA)
        vm.configure(coverageService: service)
        vm.configureSignalR(signalR)

        async let initial: Void = vm.load()
        await service.awaitPending(count: 1)
        await service.completeSuccess(index: 0, printer: Self.coverage(for: printerA, status: .covers))
        _ = await initial

        signalR.simulateFilamentCoverageChanged(FilamentCoverageChangedEvent(
            printerId: printerA, reason: "match", occurredAt: Date()
        ))
        await service.awaitPending(count: 1)
        await service.completeSuccess(index: 0, printer: Self.coverage(for: printerA, status: .runout))
        await vm.waitForCommittedGeneration(atLeast: 2)
        XCTAssertEqual(vm.dispatchedRequestCount, 2)
    }

    // MARK: - Feature-disabled tombstone precedence

    func testFeatureDisabledTombstoneBeatsOlderInflightSuccess() async {
        let service = ControlledFilamentCoverageService()
        let vm = PrinterFilamentCoverageViewModel(printerId: printerA)
        vm.configure(coverageService: service)

        async let l1: Void = vm.load()
        async let l2: Void = vm.load()
        await service.awaitPending(count: 2)
        await service.completeFeatureDisabled(index: 1)
        await service.completeSuccess(index: 0, printer: Self.coverage(for: printerA, status: .covers))
        _ = await l1
        _ = await l2
        XCTAssertTrue(vm.isFeatureDisabled)
        XCTAssertNil(vm.coverage)
    }

    // MARK: - Reconnect classification (reviewer blocker B + C)

    func testColdStartConnectedDoesNotRefetch() async {
        let service = ControlledFilamentCoverageService()
        let signalR = MockSignalRService()
        let vm = PrinterFilamentCoverageViewModel(printerId: printerA)
        vm.configure(coverageService: service)
        vm.configureSignalR(signalR)

        async let initial: Void = vm.load()
        await service.awaitPending(count: 1)
        await service.completeSuccess(index: 0, printer: Self.coverage(for: printerA, status: .covers))
        _ = await initial

        let tickBefore = vm.callbackTickForTesting
        signalR.simulateConnectionStateChange(.connected)
        await vm.waitForCallbackTick(atLeast: tickBefore + 1)
        XCTAssertEqual(vm.dispatchedRequestCount, 1,
                       "Cold-start `.connected` must not double-load.")
        XCTAssertTrue(vm.hasSeenAnyConnectedForTesting)
    }

    func testReconnectTransitionTriggersOneRefetch() async {
        let service = ControlledFilamentCoverageService()
        let signalR = MockSignalRService()
        let vm = PrinterFilamentCoverageViewModel(printerId: printerA)
        vm.configure(coverageService: service)
        vm.configureSignalR(signalR)

        async let initial: Void = vm.load()
        await service.awaitPending(count: 1)
        await service.completeSuccess(index: 0, printer: Self.coverage(for: printerA, status: .covers))
        _ = await initial

        // Cold `.connected` — no refetch, barrier-gated.
        let tick0 = vm.callbackTickForTesting
        signalR.simulateConnectionStateChange(.connected)
        await vm.waitForCallbackTick(atLeast: tick0 + 1)
        XCTAssertEqual(vm.dispatchedRequestCount, 1)

        // Recovery — exactly one refetch.
        signalR.simulateConnectionStateChange(.reconnecting)
        signalR.simulateConnectionStateChange(.connected)
        await service.awaitPending(count: 1)
        await service.completeSuccess(index: 0, printer: Self.coverage(for: printerA, status: .covers))
        await vm.waitForCommittedGeneration(atLeast: 2)
        XCTAssertEqual(vm.dispatchedRequestCount, 2)
    }

    /// Reviewer blocker B: configure while reconnecting → next
    /// `.connected` MUST refetch.
    func testConfigureWhileReconnectingArmsRecoveryOnNextConnected() async {
        let service = ControlledFilamentCoverageService()
        let signalR = MockSignalRService()
        signalR.simulateConnectionStateChange(.reconnecting)

        let vm = PrinterFilamentCoverageViewModel(printerId: printerA)
        vm.configure(coverageService: service)
        vm.configureSignalR(signalR)
        XCTAssertTrue(vm.hasSeenAnyConnectedForTesting)

        signalR.simulateConnectionStateChange(.connected)
        await service.awaitPending(count: 1)
        await service.completeSuccess(index: 0, printer: Self.coverage(for: printerA, status: .covers))
        await vm.waitForCommittedGeneration(atLeast: 1)
        XCTAssertEqual(vm.dispatchedRequestCount, 1,
                       "Configure-while-reconnecting must arm recovery for the pending .connected.")
    }

    // MARK: - printer not found vs feature-disabled distinction

    func testGenericNotFoundStopsAtNotFoundStateNotFeatureDisabled() async {
        let service = ControlledFilamentCoverageService()
        let vm = PrinterFilamentCoverageViewModel(printerId: printerA)
        vm.configure(coverageService: service)

        async let l: Void = vm.load()
        await service.awaitPending(count: 1)
        await service.completeError(index: 0, error: NetworkError.notFound)
        _ = await l

        XCTAssertTrue(vm.isPrinterNotFound)
        XCTAssertFalse(vm.isFeatureDisabled,
                       "Generic 404 (printer gone) must be distinct from feature-disabled.")
        XCTAssertNil(vm.coverage)
    }

    // MARK: - Subscription accounting on reconfiguration

    func testReconfigurationCancelsPriorSubscriptions() {
        let signalR = MockSignalRService()
        let service = ControlledFilamentCoverageService()
        let vm = PrinterFilamentCoverageViewModel(printerId: printerA)
        vm.configure(coverageService: service)

        vm.configureSignalR(signalR)
        vm.configureSignalR(signalR)
        vm.configureSignalR(signalR)
        XCTAssertEqual(signalR.filamentCoverageSubscriberCount, 1)
        XCTAssertEqual(signalR.connectionStateSubscriberCount, 1)
    }

    // MARK: - Teardown safety (reviewer blocker A + C)

    func testTeardownRemovesSubscribersAndBlocksInvalidationDelivery() async {
        let service = ControlledFilamentCoverageService()
        let signalR = MockSignalRService()
        let vm = PrinterFilamentCoverageViewModel(printerId: printerA)
        vm.configure(coverageService: service)
        vm.configureSignalR(signalR)

        async let initial: Void = vm.load()
        await service.awaitPending(count: 1)
        await service.completeSuccess(index: 0, printer: Self.coverage(for: printerA, status: .covers))
        _ = await initial

        vm.tearDownSignalR()
        XCTAssertEqual(signalR.filamentCoverageSubscriberCount, 0)
        XCTAssertEqual(signalR.connectionStateSubscriberCount, 0)

        signalR.simulateFilamentCoverageChanged(FilamentCoverageChangedEvent(
            printerId: printerA, reason: "post-teardown", occurredAt: Date()
        ))
        XCTAssertEqual(vm.dispatchedRequestCount, 1,
                       "Post-teardown invalidation cannot trigger a refetch (no subscriber to fire).")
    }

    // MARK: - Owner-epoch authority (reviewer blocker A)

    /// A queued OLD-service invalidation callback firing after the
    /// SignalR service has been replaced must not surface any GET.
    /// The old subscription is structurally CANCELLED by
    /// re-configure, so the callback body never runs — proven by
    /// subscriber counts (deterministic, no barrier needed).
    func testQueuedOldServiceInvalidationAfterReplaceCausesZeroGets() async {
        let oldCoverage = ControlledFilamentCoverageService()
        let oldSignalR = MockSignalRService()
        let vm = PrinterFilamentCoverageViewModel(printerId: printerA)
        vm.configure(coverageService: oldCoverage)
        vm.configureSignalR(oldSignalR)
        XCTAssertEqual(oldSignalR.filamentCoverageSubscriberCount, 1)

        let newSignalR = MockSignalRService()
        vm.configureSignalR(newSignalR)
        XCTAssertEqual(oldSignalR.filamentCoverageSubscriberCount, 0,
                       "Replacement configure must cancel the old subscription.")
        XCTAssertEqual(newSignalR.filamentCoverageSubscriberCount, 1)

        oldSignalR.simulateFilamentCoverageChanged(FilamentCoverageChangedEvent(
            printerId: printerA, reason: "stale", occurredAt: Date()
        ))

        let oldPending = await oldCoverage.pendingCount
        XCTAssertEqual(oldPending, 0,
                       "Stale invalidation MUST NOT dispatch a GET.")
        XCTAssertEqual(vm.dispatchedRequestCount, 0)
    }

    func testInflightOldServiceSuccessCannotOverwriteReplacement() async {
        let oldCoverage = ControlledFilamentCoverageService()
        let vm = PrinterFilamentCoverageViewModel(printerId: printerA)
        vm.configure(coverageService: oldCoverage)

        async let staleLoad: Void = vm.load()
        await oldCoverage.awaitPending(count: 1)

        let newCoverage = ControlledFilamentCoverageService()
        vm.configure(coverageService: newCoverage)

        async let freshLoad: Void = vm.load()
        await newCoverage.awaitPending(count: 1)
        await newCoverage.completeSuccess(index: 0, printer: Self.coverage(for: printerA, status: .runout))
        _ = await freshLoad
        await vm.waitForCommittedGeneration(atLeast: 2)

        await oldCoverage.completeSuccess(index: 0, printer: Self.coverage(for: printerA, status: .covers))
        _ = await staleLoad

        XCTAssertEqual(vm.coverage?.status, .runout,
                       "Stale in-flight success from OLD service MUST NOT overwrite replacement's snapshot.")
    }

    func testInflightOldServiceFeatureDisabledCannotTombstoneReplacement() async {
        let oldCoverage = ControlledFilamentCoverageService()
        let vm = PrinterFilamentCoverageViewModel(printerId: printerA)
        vm.configure(coverageService: oldCoverage)

        async let staleLoad: Void = vm.load()
        await oldCoverage.awaitPending(count: 1)

        let newCoverage = ControlledFilamentCoverageService()
        vm.configure(coverageService: newCoverage)

        async let freshLoad: Void = vm.load()
        await newCoverage.awaitPending(count: 1)
        await newCoverage.completeSuccess(index: 0, printer: Self.coverage(for: printerA, status: .covers))
        _ = await freshLoad
        await vm.waitForCommittedGeneration(atLeast: 2)
        XCTAssertFalse(vm.isFeatureDisabled)

        await oldCoverage.completeFeatureDisabled(index: 0)
        _ = await staleLoad

        XCTAssertFalse(vm.isFeatureDisabled,
                       "Stale featureDisabled from OLD service MUST NOT tombstone the replacement.")
        XCTAssertNotNil(vm.coverage)
    }

    func testInflightOldServiceErrorCannotOverwriteReplacement() async {
        let oldCoverage = ControlledFilamentCoverageService()
        let vm = PrinterFilamentCoverageViewModel(printerId: printerA)
        vm.configure(coverageService: oldCoverage)

        async let staleLoad: Void = vm.load()
        await oldCoverage.awaitPending(count: 1)

        let newCoverage = ControlledFilamentCoverageService()
        vm.configure(coverageService: newCoverage)

        async let freshLoad: Void = vm.load()
        await newCoverage.awaitPending(count: 1)
        await newCoverage.completeSuccess(index: 0, printer: Self.coverage(for: printerA, status: .covers))
        _ = await freshLoad
        await vm.waitForCommittedGeneration(atLeast: 2)
        XCTAssertNil(vm.lastLoadError)

        await oldCoverage.completeError(index: 0, error: NetworkError.serverError(503))
        _ = await staleLoad

        XCTAssertNil(vm.lastLoadError,
                     "Stale error from OLD service MUST NOT overwrite the replacement's clean state.")
    }

    func testTeardownBeforeDrainCausesNoCommit() async {
        let coverage = ControlledFilamentCoverageService()
        let vm = PrinterFilamentCoverageViewModel(printerId: printerA)
        vm.configure(coverageService: coverage)

        async let staleLoad: Void = vm.load()
        await coverage.awaitPending(count: 1)

        // Bump the authority epoch (mimic teardown) without
        // touching SignalR — the in-flight load's captured epoch is
        // now stale.
        vm.configure(coverageService: coverage)
        await coverage.completeSuccess(index: 0, printer: Self.coverage(for: printerA, status: .covers))
        _ = await staleLoad

        XCTAssertNil(vm.coverage,
                     "Teardown-during-in-flight-load must prevent commit.")
        XCTAssertEqual(vm.lastCommittedGenerationForTesting, 0)
    }

    // MARK: - Fixtures

    private static func coverage(for id: UUID, status: FilamentCoverageStatus) -> PrinterFilamentCoverage {
        PrinterFilamentCoverage(
            printerId: id,
            printerName: "Printer-\(id.uuidString.prefix(4))",
            status: status,
            toolheads: [
                ToolheadFilamentCoverage(
                    toolheadIndex: 0,
                    toolheadName: "Ext 1",
                    material: "PLA",
                    remainingGrams: 500,
                    status: status,
                    predictedRunoutAt: status == .runout ? Date() : nil
                )
            ],
            activeJobId: nil,
            activeJobName: nil,
            activeJobProgress: nil,
            earliestPredictedRunoutAt: status == .runout ? Date() : nil,
            assignedQueuedJobCount: 0,
            evaluatedAtUtc: Date()
        )
    }
}
