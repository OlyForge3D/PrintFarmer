import XCTest
@testable import PrintFarmer

// MARK: - Farm Filament Coverage ViewModel Tests (F4-M / issue #778)
//
// Proves the frozen generation-authoritative contract with deterministic
// controlled service responses — no sleeps, no `Task.yield`, no elapsed-
// time observation. Each test drives the VM with a `ControlledFilamentCoverageService`
// whose completions the test explicitly releases.

@MainActor
final class FarmFilamentCoverageViewModelTests: XCTestCase {

    // MARK: - Idempotent SignalR configuration + subscription count

    func testConfigureSignalRIsIdempotent() {
        let signalR = MockSignalRService()
        let vm = FarmFilamentCoverageViewModel()
        let service = ControlledFilamentCoverageService()
        vm.configure(coverageService: service)

        vm.configureSignalR(signalR)
        XCTAssertEqual(signalR.filamentCoverageSubscriberCount, 1)
        XCTAssertEqual(signalR.connectionStateSubscriberCount, 1)

        vm.configureSignalR(signalR)
        XCTAssertEqual(signalR.filamentCoverageSubscriberCount, 1,
                       "Reconfiguration must cancel prior tokens before re-registering.")
        XCTAssertEqual(signalR.connectionStateSubscriberCount, 1)

        vm.tearDownSignalR()
        XCTAssertEqual(signalR.filamentCoverageSubscriberCount, 0)
        XCTAssertEqual(signalR.connectionStateSubscriberCount, 0)
    }

    // MARK: - Success + tombstone precedence

    func testStaleSuccessDoesNotOverwriteNewerFeatureDisabled() async throws {
        let service = ControlledFilamentCoverageService()
        let vm = FarmFilamentCoverageViewModel()
        vm.configure(coverageService: service)

        // Dispatch load-1 (will eventually succeed).
        async let firstLoad: Void = vm.load()

        // Dispatch load-2 (will eventually feature-disable).
        async let secondLoad: Void = vm.load()

        // Wait until both requests are actually in flight.
        await service.awaitPending(count: 2)

        // Complete newer disabled first, then older success.
        await service.completeFeatureDisabled(index: 1)
        await service.completeSuccess(index: 0, fleet: Self.oneCoverPrinterFleet())

        _ = await firstLoad
        _ = await secondLoad

        XCTAssertTrue(vm.isFeatureDisabled,
                      "Older success must NOT re-enable coverage after a newer disabled tombstone.")
        XCTAssertTrue(vm.coverageByPrinter.isEmpty)
    }

    func testStaleErrorDoesNotOverwriteNewerSuccess() async throws {
        let service = ControlledFilamentCoverageService()
        let vm = FarmFilamentCoverageViewModel()
        vm.configure(coverageService: service)

        async let l1: Void = vm.load()
        async let l2: Void = vm.load()
        await service.awaitPending(count: 2)

        await service.completeSuccess(index: 1, fleet: Self.oneCoverPrinterFleet())
        await service.completeError(index: 0, error: NetworkError.serverError(503))

        _ = await l1
        _ = await l2

        XCTAssertFalse(vm.isFeatureDisabled)
        XCTAssertEqual(vm.coverageByPrinter.count, 1,
                       "Newer success must remain visible even when a stale error resolves later.")
    }

    // MARK: - Equal-timestamp newer-generation wins

    func testEqualEvaluatedAtNewerGenerationWins() async throws {
        let service = ControlledFilamentCoverageService()
        let vm = FarmFilamentCoverageViewModel()
        vm.configure(coverageService: service)

        let sharedTimestamp = Date(timeIntervalSinceReferenceDate: 1_000_000)

        async let l1: Void = vm.load()
        async let l2: Void = vm.load()
        await service.awaitPending(count: 2)

        let fleetA = Self.fleet(evaluatedAt: sharedTimestamp, printerName: "A")
        let fleetB = Self.fleet(evaluatedAt: sharedTimestamp, printerName: "B")
        // Complete newer (gen=2) first with fleetB, then older (gen=1)
        // with fleetA. Both share the exact same evaluatedAtUtc.
        await service.completeSuccess(index: 1, fleet: fleetB)
        await service.completeSuccess(index: 0, fleet: fleetA)

        _ = await l1
        _ = await l2

        XCTAssertEqual(vm.coverageByPrinter.count, 1)
        let stored = vm.coverageByPrinter.values.first!
        XCTAssertEqual(stored.printerName, "B",
                       "Equal evaluatedAtUtc must resolve to the newer generation, not the newer wall-clock arrival.")
    }

    // MARK: - Invalidation → single refetch

    func testFilamentCoverageChangedTriggersRefetch() async throws {
        let service = ControlledFilamentCoverageService()
        let signalR = MockSignalRService()
        let vm = FarmFilamentCoverageViewModel()
        vm.configure(coverageService: service)
        vm.configureSignalR(signalR)

        // Baseline load.
        async let initialLoad: Void = vm.load()
        await service.awaitPending(count: 1)
        await service.completeSuccess(index: 0, fleet: Self.oneCoverPrinterFleet())
        _ = await initialLoad
        XCTAssertEqual(vm.dispatchedRequestCount, 1)

        signalR.simulateFilamentCoverageChanged(FilamentCoverageChangedEvent(
            printerId: nil,
            reason: "test",
            occurredAt: Date()
        ))

        // The invalidation dispatches a fresh load; drain it.
        await service.awaitPending(count: 1)
        await service.completeSuccess(index: 0, fleet: Self.oneCoverPrinterFleet())
        // Wait deterministically until the second commit lands.
        await vm.waitForCommittedGeneration(atLeast: 2)
        // NOTE: single event ⇒ single refetch. `dispatchedRequestCount`
        // must equal exactly 2 (baseline + one invalidation refetch).
        XCTAssertEqual(vm.dispatchedRequestCount, 2)
    }

    // MARK: - Reconnect → single recovery refetch

    func testReconnectTriggersExactlyOneRecoveryRefetch() async throws {
        let service = ControlledFilamentCoverageService()
        let signalR = MockSignalRService()
        let vm = FarmFilamentCoverageViewModel()
        vm.configure(coverageService: service)
        vm.configureSignalR(signalR)

        // Perform the "initial" fetch (owned by the view in production).
        async let initialLoad: Void = vm.load()
        await service.awaitPending(count: 1)
        await service.completeSuccess(index: 0, fleet: Self.oneCoverPrinterFleet())
        _ = await initialLoad
        XCTAssertEqual(vm.dispatchedRequestCount, 1)

        // First observed connected transition (initial connect).
        // The hub already delivered `.disconnected` at subscription;
        // this simulates the first `.connected` — MUST NOT refetch.
        signalR.simulateConnectionStateChange(.connected)
        await Task.yield()
        XCTAssertEqual(vm.dispatchedRequestCount, 1,
                       "Cold-start `.connected` must not double-load.")

        // Subsequent reconnect (`.reconnecting → .connected`) MUST
        // trigger exactly one recovery refetch.
        signalR.simulateConnectionStateChange(.reconnecting)
        signalR.simulateConnectionStateChange(.connected)
        await service.awaitPending(count: 1)
        await service.completeSuccess(index: 0, fleet: Self.oneCoverPrinterFleet())
        await vm.waitForCommittedGeneration(atLeast: 2)
        XCTAssertEqual(vm.dispatchedRequestCount, 2,
                       "Exactly one recovery refetch per reconnect transition.")
    }

    // MARK: - Teardown safety

    func testTeardownStopsFurtherEventDelivery() async throws {
        let service = ControlledFilamentCoverageService()
        let signalR = MockSignalRService()
        let vm = FarmFilamentCoverageViewModel()
        vm.configure(coverageService: service)
        vm.configureSignalR(signalR)

        async let initialLoad: Void = vm.load()
        await service.awaitPending(count: 1)
        await service.completeSuccess(index: 0, fleet: Self.oneCoverPrinterFleet())
        _ = await initialLoad
        XCTAssertEqual(vm.dispatchedRequestCount, 1)

        vm.tearDownSignalR()

        signalR.simulateFilamentCoverageChanged(FilamentCoverageChangedEvent(
            printerId: nil, reason: "post-teardown", occurredAt: Date()
        ))
        await Task.yield()
        XCTAssertEqual(vm.dispatchedRequestCount, 1,
                       "After teardown, invalidations must not trigger refetches.")
    }

    // MARK: - Fixtures

    private static func oneCoverPrinterFleet() -> FleetFilamentCoverage {
        fleet(evaluatedAt: Date(), printerName: "Alpha")
    }

    private static func fleet(evaluatedAt: Date, printerName: String) -> FleetFilamentCoverage {
        FleetFilamentCoverage(
            printers: [
                PrinterFilamentCoverage(
                    printerId: UUID(uuidString: "11111111-1111-1111-1111-111111111111")!,
                    printerName: printerName,
                    status: .covers,
                    toolheads: [
                        ToolheadFilamentCoverage(
                            toolheadIndex: 0,
                            toolheadName: "Ext 1",
                            status: .covers
                        )
                    ],
                    activeJobId: nil,
                    activeJobName: nil,
                    activeJobProgress: nil,
                    earliestPredictedRunoutAt: nil,
                    assignedQueuedJobCount: 0,
                    evaluatedAtUtc: evaluatedAt
                )
            ],
            evaluatedAtUtc: evaluatedAt
        )
    }
}
