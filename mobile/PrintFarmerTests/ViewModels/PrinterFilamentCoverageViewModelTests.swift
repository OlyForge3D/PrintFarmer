import XCTest
@testable import PrintFarmer

// MARK: - Printer Filament Coverage ViewModel Tests (F4-M / issue #778)
//
// Same discipline as the fleet variant: deterministic controlled
// service, generation-authoritative completions, idempotent SignalR
// configuration, single reconnect refetch. The scoped-invalidation
// filter is proven here.

@MainActor
final class PrinterFilamentCoverageViewModelTests: XCTestCase {

    private let printerA = UUID(uuidString: "AAAAAAAA-1111-1111-1111-111111111111")!
    private let printerB = UUID(uuidString: "BBBBBBBB-2222-2222-2222-222222222222")!

    // MARK: - Invalidation filtering

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

        signalR.simulateFilamentCoverageChanged(FilamentCoverageChangedEvent(
            printerId: printerB, reason: "unrelated", occurredAt: Date()
        ))
        await Task.yield()
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

    // MARK: - Reconnect refetch behavior

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

        signalR.simulateConnectionStateChange(.connected)      // initial
        await Task.yield()
        XCTAssertEqual(vm.dispatchedRequestCount, 1)

        signalR.simulateConnectionStateChange(.reconnecting)
        signalR.simulateConnectionStateChange(.connected)      // recovery
        await service.awaitPending(count: 1)
        await service.completeSuccess(index: 0, printer: Self.coverage(for: printerA, status: .covers))
        await vm.waitForCommittedGeneration(atLeast: 2)
        XCTAssertEqual(vm.dispatchedRequestCount, 2)
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
