import XCTest
@testable import PrintFarmer

final class CanonicalOwnerWeakReference<Value: AnyObject> {
    weak var value: Value?

    init(_ value: Value?) {
        self.value = value
    }
}

/// Tests for DashboardViewModel: loading states, data aggregation,
/// refresh behavior, and error handling.
/// Uses MockPrinterService and MockJobService via configure() DI pattern.
@MainActor
final class DashboardViewModelTests: XCTestCase {

    private var mockPrinterService: MockPrinterService!
    private var mockJobService: MockJobService!
    private var mockStatsService: MockStatisticsService!
    private var mockJobAnalyticsService: MockJobAnalyticsService!
    private var viewModel: DashboardViewModel!

    override func setUp() async throws {
        try await super.setUp()
        mockPrinterService = MockPrinterService()
        mockJobService = MockJobService()
        mockStatsService = MockStatisticsService()
        mockJobAnalyticsService = MockJobAnalyticsService()
        viewModel = DashboardViewModel()
        viewModel.configure(
            printerService: mockPrinterService,
            jobService: mockJobService,
            statisticsService: mockStatsService,
            jobAnalyticsService: mockJobAnalyticsService
        )
    }

    override func tearDown() async throws {
        viewModel = nil
        mockPrinterService = nil
        mockJobService = nil
        mockStatsService = nil
        mockJobAnalyticsService = nil
        try await super.tearDown()
    }

    // MARK: - Initial State

    func testInitialState() {
        XCTAssertTrue(viewModel.printers.isEmpty)
        XCTAssertTrue(viewModel.queueOverview.isEmpty)
        XCTAssertFalse(viewModel.isLoading)
        XCTAssertNil(viewModel.errorMessage)
        XCTAssertNil(viewModel.summary)
    }

    func testReconnectRecoveryRefreshesCanonicalDashboardOnceAndFencesStaleService() async throws {
        let callbackQueue = ShiftTaskCallbackQueue()
        let oldPrinterService = MockPrinterService()
        oldPrinterService.printersToReturn = [try TestData.decodePrinter()]
        let oldSignalR = MockSignalRService()
        let currentPrinterService = MockPrinterService()
        currentPrinterService.printersToReturn = [
            try TestData.decodePrinter(from: TestJSON.printerMinimal)
        ]
        let currentSignalR = MockSignalRService()
        let vm = DashboardViewModel(callbackEnqueuer: callbackQueue.enqueuer)
        vm.configure(
            printerService: oldPrinterService,
            jobService: mockJobService,
            statisticsService: mockStatsService,
            jobAnalyticsService: mockJobAnalyticsService
        )
        vm.configureSignalR(oldSignalR)

        oldSignalR.simulateConnectionStateChange(.connected)
        XCTAssertEqual(callbackQueue.count, 1)
        await callbackQueue.runNext()
        XCTAssertEqual(oldPrinterService.listPrintersCallCount, 0)

        oldSignalR.simulateConnectionStateChange(.reconnecting)
        XCTAssertEqual(callbackQueue.count, 1)
        await callbackQueue.runNext()
        XCTAssertEqual(oldPrinterService.listPrintersCallCount, 0)

        oldSignalR.simulateConnectionStateChange(.connected)
        XCTAssertEqual(callbackQueue.count, 1)
        await callbackQueue.runNext()
        await vm.waitForCanonicalLoadToBecomeIdle()
        XCTAssertEqual(oldPrinterService.listPrintersCallCount, 1)
        XCTAssertEqual(vm.printers.first?.name, "Prusa MK4")

        oldSignalR.simulateConnectionStateChange(.connected)
        XCTAssertEqual(callbackQueue.count, 0)
        XCTAssertEqual(oldPrinterService.listPrintersCallCount, 1)

        vm.configure(
            printerService: currentPrinterService,
            jobService: mockJobService,
            statisticsService: mockStatsService,
            jobAnalyticsService: mockJobAnalyticsService
        )
        vm.configureSignalR(currentSignalR)
        oldSignalR.simulateCapturedConnectionStateChange(at: 0, state: .reconnecting)
        oldSignalR.simulateCapturedConnectionStateChange(at: 0, state: .connected)
        XCTAssertEqual(callbackQueue.count, 2)
        await callbackQueue.runNext()
        await callbackQueue.runNext()
        XCTAssertEqual(oldPrinterService.listPrintersCallCount, 1)
        XCTAssertEqual(currentPrinterService.listPrintersCallCount, 0)

        currentSignalR.simulateConnectionStateChange(.connected)
        await callbackQueue.runNext()
        currentSignalR.simulateConnectionStateChange(.reconnecting)
        await callbackQueue.runNext()
        XCTAssertEqual(currentPrinterService.listPrintersCallCount, 0)

        currentSignalR.simulateConnectionStateChange(.connected)
        vm.isViewActive = false
        await callbackQueue.runNext()
        XCTAssertEqual(currentPrinterService.listPrintersCallCount, 0)

        vm.isViewActive = true
        vm.configureSignalR(currentSignalR)
        currentSignalR.simulateConnectionStateChange(.reconnecting)
        await callbackQueue.runNext()
        currentSignalR.simulateConnectionStateChange(.connected)
        await callbackQueue.runNext()
        await vm.waitForCanonicalLoadToBecomeIdle()

        XCTAssertEqual(currentPrinterService.listPrintersCallCount, 1)
        XCTAssertEqual(vm.printers.first?.name, "Ender 3")
        XCTAssertEqual(callbackQueue.count, 0)
    }

    func testCanonicalLoadRejectsReconfiguredInFlightDataAndSnapshotCommit() async throws {
        let oldGate = ShiftTaskResultGate<[Printer]>()
        let oldScript = ScriptedCanonicalResult<[Printer]>([.gated(oldGate)])
        mockPrinterService.listHandler = { _ in try await oldScript.next() }
        let currentService = MockPrinterService()
        currentService.printersToReturn = [
            try TestData.decodePrinter(from: TestJSON.printerMinimal)
        ]
        let store = FakeFarmSnapshotStore(
            session: FarmSnapshotSession(
                serverID: UUID(),
                userID: UUID(),
                generation: 1,
                token: 1
            )
        )
        viewModel.configureSnapshot(store: store)

        let oldRequest = Task { await viewModel.loadDashboard() }
        await oldScript.waitForCallCount(1)

        viewModel.configure(
            printerService: currentService,
            jobService: mockJobService,
            statisticsService: mockStatsService,
            jobAnalyticsService: mockJobAnalyticsService
        )
        await viewModel.loadDashboard()
        XCTAssertEqual(viewModel.printers.first?.name, "Ender 3")
        XCTAssertNil(viewModel.errorMessage)
        XCTAssertFalse(viewModel.isLoading)
        XCTAssertEqual(store.committedEnvelopes.count, 1)

        await oldGate.succeed([try TestData.decodePrinter()])
        await viewModel.waitForSupersededCanonicalLoads()
        await oldRequest.value

        XCTAssertEqual(viewModel.printers.first?.name, "Ender 3")
        XCTAssertNil(viewModel.errorMessage)
        XCTAssertFalse(viewModel.isLoading)
        XCTAssertEqual(store.committedEnvelopes.count, 1)
    }

    func testCanonicalLoadRejectsReconfiguredInFlightErrorPublication() async throws {
        let oldGate = ShiftTaskResultGate<[Printer]>()
        let oldScript = ScriptedCanonicalResult<[Printer]>([.gated(oldGate)])
        mockPrinterService.listHandler = { _ in try await oldScript.next() }
        let currentService = MockPrinterService()
        currentService.printersToReturn = [
            try TestData.decodePrinter(from: TestJSON.printerMinimal)
        ]

        let oldRequest = Task { await viewModel.loadDashboard() }
        await oldScript.waitForCallCount(1)
        viewModel.configure(
            printerService: currentService,
            jobService: mockJobService,
            statisticsService: mockStatsService,
            jobAnalyticsService: mockJobAnalyticsService
        )
        await viewModel.loadDashboard()
        await oldGate.fail(.forced("stale dashboard failure"))
        await viewModel.waitForSupersededCanonicalLoads()
        await oldRequest.value

        XCTAssertEqual(viewModel.printers.first?.name, "Ender 3")
        XCTAssertNil(viewModel.errorMessage)
        XCTAssertFalse(viewModel.isLoading)
    }

    func testManualLoadCoalescesReconnectIntoOneAuthoritativeFollowUp() async throws {
        let callbackQueue = ShiftTaskCallbackQueue()
        let firstGate = ShiftTaskResultGate<[Printer]>()
        let stale = [try TestData.decodePrinter()]
        let current = [try TestData.decodePrinter(from: TestJSON.printerMinimal)]
        let script = ScriptedCanonicalResult<[Printer]>([
            .gated(firstGate),
            .value(current)
        ])
        mockPrinterService.listHandler = { _ in try await script.next() }
        let signalR = MockSignalRService()
        let vm = DashboardViewModel(callbackEnqueuer: callbackQueue.enqueuer)
        vm.configure(
            printerService: mockPrinterService,
            jobService: mockJobService,
            statisticsService: mockStatsService,
            jobAnalyticsService: mockJobAnalyticsService
        )
        vm.configureSignalR(signalR)

        let manualRefresh = Task { await vm.loadDashboard() }
        await script.waitForCallCount(1)
        signalR.simulateConnectionStateChange(.reconnecting)
        await callbackQueue.runNext()
        signalR.simulateConnectionStateChange(.connected)
        await callbackQueue.runNext()
        var callCount = await script.callCount
        XCTAssertEqual(callCount, 1)

        await firstGate.succeed(stale)
        await script.waitForCallCount(2)
        await manualRefresh.value

        callCount = await script.callCount
        XCTAssertEqual(callCount, 2)
        XCTAssertEqual(vm.printers.first?.name, "Ender 3")
        signalR.simulateCapturedConnectionStateChange(at: 0, state: .connected)
        await callbackQueue.runNext()
        await vm.waitForCanonicalLoadToBecomeIdle()
        callCount = await script.callCount
        XCTAssertEqual(callCount, 2)
    }

    func testSameServiceReconfigurePreservesQueuedReconnectEdge() async throws {
        let callbackQueue = ShiftTaskCallbackQueue()
        let current = [try TestData.decodePrinter(from: TestJSON.printerMinimal)]
        let script = ScriptedCanonicalResult<[Printer]>([.value(current)])
        mockPrinterService.listHandler = { _ in try await script.next() }
        let signalR = MockSignalRService()
        let vm = DashboardViewModel(callbackEnqueuer: callbackQueue.enqueuer)
        vm.configure(
            printerService: mockPrinterService,
            jobService: mockJobService,
            statisticsService: mockStatsService,
            jobAnalyticsService: mockJobAnalyticsService
        )
        vm.configureSignalR(signalR)

        signalR.simulateConnectionStateChange(.reconnecting)
        signalR.simulateConnectionStateChange(.connected)
        XCTAssertEqual(callbackQueue.count, 2)
        vm.configureSignalR(signalR)
        await callbackQueue.runNext()
        await callbackQueue.runNext()
        await script.waitForCallCount(1)
        await vm.waitForCanonicalLoadToBecomeIdle()

        let callCount = await script.callCount
        XCTAssertEqual(callCount, 1)
        XCTAssertEqual(vm.printers.first?.name, "Ender 3")
    }

    func testDeactivationRejectsParkedCanonicalPublication() async throws {
        let gate = ShiftTaskResultGate<[Printer]>()
        let script = ScriptedCanonicalResult<[Printer]>([.gated(gate)])
        mockPrinterService.listHandler = { _ in try await script.next() }
        let store = FakeFarmSnapshotStore(
            session: FarmSnapshotSession(
                serverID: UUID(),
                userID: UUID(),
                generation: 1,
                token: 1
            )
        )
        viewModel.configureSnapshot(store: store)

        let request = Task { await viewModel.loadDashboard() }
        await script.waitForCallCount(1)
        viewModel.isViewActive = false
        await gate.succeed([try TestData.decodePrinter()])
        await viewModel.waitForSupersededCanonicalLoads()
        await request.value

        XCTAssertTrue(viewModel.printers.isEmpty)
        XCTAssertNil(viewModel.errorMessage)
        XCTAssertFalse(viewModel.isLoading)
        XCTAssertEqual(store.committedEnvelopes.count, 0)
    }

    func testCancellingOneDashboardCallerCompletesPromptlyAndPreservesPeerDemand() async throws {
        let firstGate = ShiftTaskResultGate<[Printer]>()
        let current = [try TestData.decodePrinter(from: TestJSON.printerMinimal)]
        let script = ScriptedCanonicalResult<[Printer]>([
            .gated(firstGate),
            .value(current)
        ])
        mockPrinterService.listHandler = { _ in try await script.next() }

        let cancelledCaller = Task { await viewModel.loadDashboard() }
        await script.waitForCallCount(1)
        let peerCaller = Task { await viewModel.loadDashboard() }
        await viewModel.waitForCanonicalWaiterCount(2)

        cancelledCaller.cancel()
        await cancelledCaller.value
        XCTAssertEqual(viewModel.canonicalWaiterCountForTesting, 1)
        XAssertEqual(await script.callCount, 1)

        await firstGate.succeed([try TestData.decodePrinter()])
        await peerCaller.value

        XAssertEqual(await script.callCount, 2)
        XCTAssertEqual(viewModel.printers.map(\.id), current.map(\.id))
        XCTAssertEqual(viewModel.canonicalWaiterCountForTesting, 0)
    }

    func testCancellingSoleDashboardCallerUnwindsDemandWithoutWaitingForService() async throws {
        let callbackQueue = ShiftTaskCallbackQueue()
        let gate = ShiftTaskResultGate<[Printer]>()
        let script = ScriptedCanonicalResult<[Printer]>([.gated(gate)])
        mockPrinterService.listHandler = { _ in try await script.next() }
        let vm = DashboardViewModel(callbackEnqueuer: callbackQueue.enqueuer)
        vm.configure(
            printerService: mockPrinterService,
            jobService: mockJobService,
            statisticsService: mockStatsService,
            jobAnalyticsService: mockJobAnalyticsService
        )

        let caller = Task { await vm.loadDashboard() }
        await script.waitForCallCount(1)
        caller.cancel()
        await caller.value
        XCTAssertEqual(callbackQueue.count, 1)
        await callbackQueue.runNext()

        XCTAssertEqual(vm.canonicalWaiterCountForTesting, 0)
        XCTAssertFalse(vm.isLoading)
        XCTAssertTrue(vm.printers.isEmpty)
        XAssertEqual(await script.callCount, 1)

        await gate.succeed([try TestData.decodePrinter()])
        await vm.waitForSupersededCanonicalLoads()
        XCTAssertTrue(vm.printers.isEmpty)
    }

    func testParkedDashboardServiceDoesNotRetainViewModelOrCaller() async {
        let gate = ShiftTaskResultGate<[Printer]>()
        let script = ScriptedCanonicalResult<[Printer]>([.gated(gate)])
        mockPrinterService.listHandler = { _ in try await script.next() }
        var vm: DashboardViewModel? = DashboardViewModel()
        vm?.configure(
            printerService: mockPrinterService,
            jobService: mockJobService,
            statisticsService: mockStatsService,
            jobAnalyticsService: mockJobAnalyticsService
        )
        let weakVM = CanonicalOwnerWeakReference(vm)
        let waiter = vm?.beginCanonicalLoadForTesting()
        let caller = Task { await waiter?.wait() }
        await script.waitForCallCount(1)

        vm = nil
        XCTAssertNil(weakVM.value)
        await caller.value

        await gate.succeed([])
    }

    func testCanonicalWaiterRegistryHandlesCancelBeforeAndDuringAttachment() async {
        let beforeFirstAttachment = AsyncBarrier()
        let firstCancelled = AsyncBarrier()
        defer {
            beforeFirstAttachment.close()
            firstCancelled.close()
        }
        let firstRegistry = CanonicalLoadWaiterRegistry(
            beforeAttachment: { await beforeFirstAttachment.arriveAndWait() }
        )
        let firstID = UUID()
        firstRegistry.register(firstID)
        let duringAttachment = Task {
            await firstRegistry.wait(for: firstID) {
                firstCancelled.signal()
            }
        }
        await beforeFirstAttachment.waitUntilArrived()
        duringAttachment.cancel()
        await firstCancelled.waitUntilArrived()
        XCTAssertEqual(firstRegistry.activeCount, 0)
        beforeFirstAttachment.release()
        await duringAttachment.value

        let beforeWait = AsyncBarrier()
        let secondCancelled = AsyncBarrier()
        defer {
            beforeWait.close()
            secondCancelled.close()
        }
        let secondRegistry = CanonicalLoadWaiterRegistry()
        let secondID = UUID()
        secondRegistry.register(secondID)
        let beforeAttachment = Task {
            await beforeWait.arriveAndWait()
            await secondRegistry.wait(for: secondID) {
                secondCancelled.signal()
            }
        }
        await beforeWait.waitUntilArrived()
        beforeAttachment.cancel()
        beforeWait.release()
        await secondCancelled.waitUntilArrived()
        await beforeAttachment.value
        XCTAssertEqual(secondRegistry.activeCount, 0)
    }

    // MARK: - Successful Load

    func testLoadDashboardPopulatesData() async throws {
        let printer = try TestData.decodePrinter()
        mockPrinterService.printersToReturn = [printer]

        await viewModel.loadDashboard()

        XCTAssertEqual(viewModel.printers.count, 1)
        XCTAssertTrue(mockPrinterService.listPrintersCalled)
        XCTAssertTrue(mockJobService.listJobsCalled)
        XCTAssertFalse(viewModel.isLoading)
        XCTAssertNil(viewModel.errorMessage)
    }

    // MARK: - Computed Summaries

    func testOnlineCountFiltersCorrectly() async throws {
        let onlinePrinter = try TestData.decodePrinter(from: TestJSON.printer)
        let offlinePrinter = try TestData.decodePrinter(from: TestJSON.printerMinimal)
        mockPrinterService.printersToReturn = [onlinePrinter, offlinePrinter]

        await viewModel.loadDashboard()

        XCTAssertEqual(viewModel.onlineCount, 1)
        XCTAssertEqual(viewModel.offlineCount, 1)
    }

    func testPrintingCountFiltersCorrectly() async throws {
        let printing = try TestData.decodePrinter(from: TestJSON.printer)  // state: "printing"
        let offline = try TestData.decodePrinter(from: TestJSON.printerMinimal) // state: nil
        mockPrinterService.printersToReturn = [printing, offline]

        await viewModel.loadDashboard()

        XCTAssertEqual(viewModel.printingCount, 1)
    }

    // MARK: - Empty Data

    func testLoadDashboardWithEmptyData() async {
        mockPrinterService.printersToReturn = []
        mockJobService.queueOverviewsToReturn = []

        await viewModel.loadDashboard()

        XCTAssertTrue(viewModel.printers.isEmpty)
        XCTAssertEqual(viewModel.onlineCount, 0)
        XCTAssertEqual(viewModel.printingCount, 0)
        XCTAssertEqual(viewModel.offlineCount, 0)
        XCTAssertNil(viewModel.errorMessage)
    }

    // MARK: - Error Handling

    func testLoadDashboardSetsErrorOnFailure() async {
        mockPrinterService.errorToThrow = NetworkError.noConnection

        await viewModel.loadDashboard()

        XCTAssertFalse(viewModel.isLoading)
        XCTAssertNotNil(viewModel.errorMessage)
    }

    func testLoadDashboardSetsErrorOnServerError() async {
        mockPrinterService.errorToThrow = NetworkError.serverError(500)

        await viewModel.loadDashboard()

        XCTAssertNotNil(viewModel.errorMessage)
    }

    // MARK: - Refresh

    func testRefreshReloadsData() async throws {
        // First load - empty
        mockPrinterService.printersToReturn = []
        await viewModel.loadDashboard()
        XCTAssertEqual(viewModel.printers.count, 0)

        // Refresh with data
        let printer = try TestData.decodePrinter()
        mockPrinterService.printersToReturn = [printer]
        await viewModel.loadDashboard()

        XCTAssertEqual(viewModel.printers.count, 1)
    }

    func testRefreshClearsErrorOnSuccess() async {
        // Fail first
        mockPrinterService.errorToThrow = NetworkError.noConnection
        await viewModel.loadDashboard()
        XCTAssertNotNil(viewModel.errorMessage)

        // Succeed on retry
        mockPrinterService.errorToThrow = nil
        mockPrinterService.printersToReturn = []
        await viewModel.loadDashboard()

        XCTAssertNil(viewModel.errorMessage)
    }

    // MARK: - Maintenance

    func testMaintenanceCountFiltersCorrectly() async throws {
        // The fixture printer has inMaintenance = false
        let printer = try TestData.decodePrinter()
        mockPrinterService.printersToReturn = [printer]

        await viewModel.loadDashboard()

        XCTAssertEqual(viewModel.maintenanceCount, 0)
        XCTAssertFalse(viewModel.hasMaintenanceAlerts)
    }

    // MARK: - Not Configured

    func testLoadWithoutConfigureDoesNotCrash() async {
        let unconfigured = DashboardViewModel()
        await unconfigured.loadDashboard()
        // Should silently return without setting error
        XCTAssertFalse(unconfigured.isLoading)
    }
}

// MARK: - Cold-offline farm snapshot (F10-C1b, #817)

/// Deterministic ViewModel tests for the cold-offline read-only farm shell.
/// All ordering is exercised through explicit `await` boundaries and an
/// injected clock — no sleeps, polling, or retries.
@MainActor
final class DashboardViewModelSnapshotTests: XCTestCase {

    private var mockPrinterService: MockPrinterService!
    private var mockJobService: MockJobService!
    private var mockStatsService: MockStatisticsService!
    private var mockJobAnalyticsService: MockJobAnalyticsService!
    private var mockAutoDispatch: MockAutoDispatchService!
    private var store: FakeFarmSnapshotStore!
    private var viewModel: DashboardViewModel!

    private let fixedNow = Date(timeIntervalSince1970: 1_700_000_500)
    private let namespace = FarmSnapshotFixtures.namespace(server: UUID(), user: UUID())

    override func setUp() async throws {
        try await super.setUp()
        mockPrinterService = MockPrinterService()
        mockJobService = MockJobService()
        mockStatsService = MockStatisticsService()
        mockJobAnalyticsService = MockJobAnalyticsService()
        mockAutoDispatch = MockAutoDispatchService()
        store = FakeFarmSnapshotStore(session: FarmSnapshotSession(namespace: namespace, generation: 1, token: 1))
        viewModel = DashboardViewModel()
        viewModel.configure(
            printerService: mockPrinterService,
            jobService: mockJobService,
            statisticsService: mockStatsService,
            jobAnalyticsService: mockJobAnalyticsService
        )
        viewModel.configureSnapshot(
            store: store,
            autoPrintService: mockAutoDispatch,
            now: { [fixedNow] in fixedNow }
        )
    }

    override func tearDown() async throws {
        viewModel = nil
        store = nil
        mockAutoDispatch = nil
        mockJobAnalyticsService = nil
        mockStatsService = nil
        mockJobService = nil
        mockPrinterService = nil
        try await super.tearDown()
    }

    private func cachedEnvelope(printers: [Printer], millis: Int64, pendingReady: Set<UUID> = []) -> FarmSnapshotEnvelope {
        FarmSnapshotEnvelope(
            namespace: namespace,
            printers: printers,
            pendingReadyPrinterIDs: pendingReady,
            lastUpdatedAtMillis: millis
        )
    }

    // MARK: Hydration

    func testHydrateRendersCachedReadOnlyShell() async throws {
        let printer = try TestData.decodePrinter()
        store.hydration = .snapshot(cachedEnvelope(printers: [printer], millis: 1_699_999_000_000))

        await viewModel.hydrateFromCache()

        XCTAssertEqual(viewModel.printers.count, 1)
        XCTAssertEqual(viewModel.printers.first?.id, printer.id)
        XCTAssertTrue(viewModel.isStale)
        XCTAssertTrue(viewModel.isReadOnly)
        XCTAssertFalse(viewModel.hasNoCachedData)
        XCTAssertEqual(viewModel.lastUpdatedAt, Date(timeIntervalSince1970: 1_699_999_000))
    }

    func testHydratePresentEmptyIsDistinctFromAbsent() async {
        store.hydration = .snapshot(cachedEnvelope(printers: [], millis: 1_699_999_000_000))

        await viewModel.hydrateFromCache()

        // Present-but-empty cached snapshot: stale + read-only, but NOT absent.
        XCTAssertTrue(viewModel.printers.isEmpty)
        XCTAssertTrue(viewModel.isStale)
        XCTAssertFalse(viewModel.hasNoCachedData)
    }

    func testHydrateAbsentSetsAbsentState() async {
        store.hydration = .absent

        await viewModel.hydrateFromCache()

        XCTAssertTrue(viewModel.printers.isEmpty)
        XCTAssertFalse(viewModel.isStale)
        XCTAssertFalse(viewModel.isReadOnly)
        XCTAssertTrue(viewModel.hasNoCachedData)
    }

    func testHydrateInactiveLeavesShellUnloaded() async {
        store.hydration = .inactive

        await viewModel.hydrateFromCache()

        XCTAssertTrue(viewModel.printers.isEmpty)
        XCTAssertFalse(viewModel.isStale)
        XCTAssertFalse(viewModel.hasNoCachedData)
    }

    // MARK: Stale-banner reportability (cold-launch flash regression)

    /// THE REGRESSION: hydrating the cache sets `farmSource = .cached` before any
    /// canonical pass has concluded, so the cold-offline shell asserted "offline"
    /// during a window in which nothing was known about reachability. The shell
    /// itself must still render immediately (#817) — only the claim waits.
    func testStaleBannerIsNotReportableBeforeFirstCanonicalPass() async throws {
        let printer = try TestData.decodePrinter()
        store.hydration = .snapshot(cachedEnvelope(printers: [printer], millis: 1_699_999_000_000))

        await viewModel.hydrateFromCache()

        XCTAssertTrue(viewModel.isStale, "hydrated cache is still unconfirmed")
        XCTAssertTrue(viewModel.isReadOnly, "mutations stay denied while unconfirmed")
        XCTAssertFalse(viewModel.hasConcludedCanonicalLoad, "no canonical pass has concluded")
        XCTAssertFalse(
            viewModel.isStaleBannerReportable,
            "the offline banner must stay hidden while the first canonical pass is undecided"
        )
    }

    /// A healthy launch must never show the banner: the successful pass clears
    /// staleness, so there is no instant at which both inputs are true.
    func testStaleBannerNeverBecomesReportableOnHealthyLaunch() async throws {
        let cached = try TestData.decodePrinter()
        store.hydration = .snapshot(cachedEnvelope(printers: [cached], millis: 1_699_000_000_000))
        await viewModel.hydrateFromCache()
        XCTAssertFalse(viewModel.isStaleBannerReportable)

        mockPrinterService.printersToReturn = [try TestData.decodePrinter()]
        await viewModel.loadDashboard()

        XCTAssertFalse(viewModel.isStale, "a confirmed-live fleet is not stale")
        XCTAssertTrue(viewModel.hasConcludedCanonicalLoad, "the pass concluded")
        XCTAssertFalse(
            viewModel.isStaleBannerReportable,
            "a healthy launch must never report the offline banner"
        )
    }

    /// A genuinely unreachable backend must still surface the banner — the fix
    /// suppresses the startup flash, not the honest offline signal.
    func testStaleBannerBecomesReportableWhenFirstCanonicalPassFails() async throws {
        let cached = try TestData.decodePrinter()
        store.hydration = .snapshot(cachedEnvelope(printers: [cached], millis: 1_699_000_000_000))
        await viewModel.hydrateFromCache()
        XCTAssertFalse(viewModel.isStaleBannerReportable, "undecided before the attempt")

        mockPrinterService.listHandler = { _ in
            throw NetworkError.transportError(URLError(.notConnectedToInternet))
        }
        await viewModel.loadDashboard()

        XCTAssertTrue(viewModel.isStale, "cached fleet is still on screen and unconfirmed")
        XCTAssertTrue(viewModel.hasConcludedCanonicalLoad, "the attempt concluded, unsuccessfully")
        XCTAssertTrue(
            viewModel.isStaleBannerReportable,
            "a confirmed-unreachable backend must still show the offline banner"
        )
    }

    /// The "No Cached Fleet / Reconnect" dead-end is an equally strong claim: it
    /// tells the user the fleet is unreachable. Before a canonical pass concludes
    /// we only know nothing was cached, so the undecided window must not show it.
    func testAbsentFleetDeadEndIsNotReportableBeforeFirstCanonicalPass() async {
        store.hydration = .absent

        await viewModel.hydrateFromCache()

        XCTAssertTrue(viewModel.hasNoCachedData, "the underlying absent state is unchanged")
        XCTAssertFalse(viewModel.hasConcludedCanonicalLoad)
        XCTAssertFalse(
            viewModel.isAbsentFleetReportable,
            "the reconnect dead-end must wait for a concluded canonical pass"
        )
    }

    func testAbsentFleetDeadEndBecomesReportableAfterAFailedCanonicalPass() async {
        store.hydration = .absent
        await viewModel.hydrateFromCache()
        XCTAssertFalse(viewModel.isAbsentFleetReportable)

        mockPrinterService.listHandler = { _ in
            throw NetworkError.transportError(URLError(.notConnectedToInternet))
        }
        await viewModel.loadDashboard()

        XCTAssertTrue(viewModel.hasNoCachedData)
        XCTAssertTrue(viewModel.hasConcludedCanonicalLoad)
        XCTAssertTrue(
            viewModel.isAbsentFleetReportable,
            "once the pass concluded unreachable, the dead-end is honest"
        )
    }

    // MARK: Commit / stale clearing

    /// Mirrors the coverage view models' wrapped-cancellation tests. Without
    /// this, reverting the `isCancellationError` guard in
    /// `loadCanonicalSnapshot`'s terminal catch passes CI: the existing
    /// cancellation tests only exercise the `Task.isCancelled` branch, which
    /// short-circuits first, so the guard behind it is never reached.
    func testDashboardWrappedCancellationDoesNotConclude() async throws {
        let cached = try TestData.decodePrinter()
        store.hydration = .snapshot(cachedEnvelope(printers: [cached], millis: 1_699_000_000_000))
        await viewModel.hydrateFromCache()
        XCTAssertTrue(viewModel.isStale, "precondition: showing unconfirmed cached data")

        mockPrinterService.listHandler = { _ in
            throw NetworkError.transportError(URLError(.cancelled))
        }
        await viewModel.loadDashboard()

        XCTAssertTrue(viewModel.isStale, "still unconfirmed: nothing was answered")
        XCTAssertFalse(
            viewModel.hasConcludedCanonicalLoad,
            "a cancelled pass concludes nothing, however the cancellation was wrapped"
        )
        XCTAssertFalse(
            viewModel.isStaleBannerReportable,
            "a cancelled pass must not flash the offline banner"
        )
    }

    /// Guards the #2400 `@Observable` trap: if `hasConcludedCanonicalLoad` were
    /// `@ObservationIgnored`, SwiftUI would never re-evaluate
    /// `isStaleBannerReportable` and the banner could never appear. The failing
    /// pass leaves `isStale` untouched, so the invalidation can only have come
    /// from the conclusion flag.
    func testStaleBannerReportabilityIsObservable() async throws {
        let cached = try TestData.decodePrinter()
        store.hydration = .snapshot(cachedEnvelope(printers: [cached], millis: 1_699_000_000_000))
        await viewModel.hydrateFromCache()
        XCTAssertTrue(viewModel.isStale, "precondition: stale is already true")

        let invalidated = expectation(description: "observation fired")
        withObservationTracking {
            _ = viewModel.isStaleBannerReportable
        } onChange: {
            invalidated.fulfill()
        }

        mockPrinterService.listHandler = { _ in
            throw NetworkError.transportError(URLError(.timedOut))
        }
        await viewModel.loadDashboard()

        await fulfillment(of: [invalidated], timeout: 2)
        XCTAssertTrue(
            viewModel.isStale,
            "guards the premise: staleness must NOT change across the conclusion"
        )
        XCTAssertTrue(viewModel.isStaleBannerReportable)
    }

    func testSuccessfulLoadCommitsSnapshotAndClearsStale() async throws {
        // Start stale from cache.
        let cached = try TestData.decodePrinter()
        store.hydration = .snapshot(cachedEnvelope(printers: [cached], millis: 1_699_000_000_000))
        await viewModel.hydrateFromCache()
        XCTAssertTrue(viewModel.isStale)

        // Canonical load succeeds with a fresh fleet.
        let fresh = try TestData.decodePrinter()
        mockPrinterService.printersToReturn = [fresh]

        await viewModel.loadDashboard()

        XCTAssertFalse(viewModel.isStale)
        XCTAssertFalse(viewModel.isReadOnly)
        XCTAssertNil(viewModel.errorMessage)
        XCTAssertEqual(viewModel.lastUpdatedAt, fixedNow)
        XCTAssertEqual(store.committedEnvelopes.count, 1)
        XCTAssertEqual(store.committedEnvelopes.first?.namespace, namespace)
        XCTAssertEqual(store.committedSessions.first?.namespace, namespace)
    }

    func testParkedRealStoreCommitIsRevokedAndSameMillisecondFollowUpPromotes() async throws {
        let root = FarmSnapshotFixtures.tempRoot()
        defer { try? FileManager.default.removeItem(at: root) }
        let io = ControlledFarmSnapshotFileIO()
        let authority = FarmSnapshotFixtures.makeAuthority(
            tombstoneDefaults: UserDefaults(
                suiteName: trackedSuiteName("dashboard-pass-authority-success")
            )!
        )
        let realStore = FarmSnapshotStore(authority: authority, fileIO: io, rootURL: root)
        let session = try XCTUnwrap(
            authority.mint(namespace: namespace, generation: 1)
        )
        XAssertTrue(await realStore.activate(session: session))

        let stale = try TestData.decodePrinter()
        let current = try TestData.decodePrinter(from: TestJSON.printerMinimal)
        let script = ScriptedCanonicalResult<[Printer]>([
            .value([stale]),
            .value([current])
        ])
        mockPrinterService.listHandler = { _ in try await script.next() }
        let vm = DashboardViewModel()
        vm.configure(
            printerService: mockPrinterService,
            jobService: mockJobService,
            statisticsService: mockStatsService,
            jobAnalyticsService: mockJobAnalyticsService
        )
        vm.configureSnapshot(
            store: realStore,
            autoPrintService: mockAutoDispatch,
            now: { [fixedNow] in fixedNow }
        )

        let parkedCommit = AsyncBarrier()
        defer { parkedCommit.close() }
        io.postWriteCandidateBarrier = parkedCommit
        let requestA = Task { await vm.loadDashboard() }
        await parkedCommit.waitUntilArrived()
        XCTAssertEqual(io.promoteCount, 0)

        let requestB = Task { await vm.loadDashboard() }
        await vm.waitForCanonicalWaiterCount(2)
        parkedCommit.release()
        await script.waitForCallCount(2)
        await requestA.value
        await requestB.value

        XCTAssertEqual(io.promoteCount, 1)
        XCTAssertEqual(vm.printers.map(\.id), [current.id])
        guard case .snapshot(let durable) = await realStore.hydrateActive() else {
            return XCTFail("expected authoritative B snapshot")
        }
        XCTAssertEqual(durable.payload.map(\.id), [current.id])
        XCTAssertEqual(
            durable.lastUpdatedAtMillis,
            Int64((fixedNow.timeIntervalSince1970 * 1000).rounded())
        )
    }

    func testParkedRealStoreCommitCannotReplacePriorWhenFollowUpFails() async throws {
        let root = FarmSnapshotFixtures.tempRoot()
        defer { try? FileManager.default.removeItem(at: root) }
        let io = ControlledFarmSnapshotFileIO()
        let authority = FarmSnapshotFixtures.makeAuthority(
            tombstoneDefaults: UserDefaults(
                suiteName: trackedSuiteName("dashboard-pass-authority-failure")
            )!
        )
        let realStore = FarmSnapshotStore(authority: authority, fileIO: io, rootURL: root)
        let session = try XCTUnwrap(
            authority.mint(namespace: namespace, generation: 1)
        )
        XAssertTrue(await realStore.activate(session: session))
        let priorPrinter = FarmSnapshotPrinter(
            try TestData.decodePrinter(from: TestJSON.printerMinimal),
            isPendingReady: false
        )
        let sameMillis = Int64((fixedNow.timeIntervalSince1970 * 1000).rounded())
        let prior = FarmSnapshotEnvelope(
            namespace: namespace,
            payload: [priorPrinter],
            lastUpdatedAtMillis: sameMillis
        )
        XAssertEqual(await realStore.commit(prior, capturedSession: session), .committed)

        let stale = try TestData.decodePrinter()
        let script = ScriptedCanonicalResult<[Printer]>([
            .value([stale]),
            .failure(.forced("authoritative follow-up failed"))
        ])
        mockPrinterService.listHandler = { _ in try await script.next() }
        let vm = DashboardViewModel()
        vm.configure(
            printerService: mockPrinterService,
            jobService: mockJobService,
            statisticsService: mockStatsService,
            jobAnalyticsService: mockJobAnalyticsService
        )
        vm.configureSnapshot(
            store: realStore,
            autoPrintService: mockAutoDispatch,
            now: { [fixedNow] in fixedNow }
        )

        let parkedCommit = AsyncBarrier()
        defer { parkedCommit.close() }
        io.postWriteCandidateBarrier = parkedCommit
        let requestA = Task { await vm.loadDashboard() }
        await parkedCommit.waitUntilArrived()
        XCTAssertEqual(io.promoteCount, 1)

        let requestB = Task { await vm.loadDashboard() }
        await vm.waitForCanonicalWaiterCount(2)
        parkedCommit.release()
        await script.waitForCallCount(2)
        await requestA.value
        await requestB.value

        XCTAssertEqual(io.promoteCount, 1)
        guard case .snapshot(let durable) = await realStore.hydrateActive() else {
            return XCTFail("expected prior authoritative snapshot")
        }
        XCTAssertEqual(durable, prior)
        XCTAssertNotEqual(durable.payload.map(\.id), [stale.id])
    }

    func testOfflineLoadPreservesCachedShellWithoutError() async throws {
        let cached = try TestData.decodePrinter()
        store.hydration = .snapshot(cachedEnvelope(printers: [cached], millis: 1_699_000_000_000))
        await viewModel.hydrateFromCache()

        // Canonical load fails offline.
        mockPrinterService.errorToThrow = NetworkError.noConnection

        await viewModel.loadDashboard()

        // Cached fleet remains on screen, still read-only, no blocking error, no commit.
        XCTAssertEqual(viewModel.printers.count, 1)
        XCTAssertEqual(viewModel.printers.first?.id, cached.id)
        XCTAssertTrue(viewModel.isStale)
        XCTAssertTrue(viewModel.isReadOnly)
        XCTAssertNil(viewModel.errorMessage)
        XCTAssertTrue(store.committedEnvelopes.isEmpty)
    }

    func testOfflineLoadWithoutCacheSurfacesError() async {
        store.hydration = .absent
        await viewModel.hydrateFromCache()
        mockPrinterService.errorToThrow = NetworkError.noConnection

        await viewModel.loadDashboard()

        XCTAssertNotNil(viewModel.errorMessage)
        XCTAssertFalse(viewModel.isStale)
        XCTAssertTrue(store.committedEnvelopes.isEmpty)
    }

    func testHydrateNeverDowngradesConfirmedLive() async throws {
        // A confirmed canonical response is already on screen.
        let fresh = try TestData.decodePrinter()
        mockPrinterService.printersToReturn = [fresh]
        await viewModel.loadDashboard()
        XCTAssertFalse(viewModel.isStale)

        // A late hydrate must NOT replace the live fleet with cached data.
        store.hydration = .snapshot(cachedEnvelope(printers: [], millis: 1))
        await viewModel.hydrateFromCache()

        XCTAssertFalse(viewModel.isStale)
        XCTAssertEqual(viewModel.printers.first?.id, fresh.id)
    }

    // MARK: Namespace A -> B -> A no-flash (generation-keyed remount)

    func testNamespaceSwitchNeverFlashesPriorNamespace() async throws {
        // Namespace A cached fleet.
        let printerA = try TestData.decodePrinter(from: TestJSON.printer)
        store.hydration = .snapshot(cachedEnvelope(printers: [printerA], millis: 1))
        await viewModel.hydrateFromCache()
        XCTAssertEqual(viewModel.printers.map(\.id), [printerA.id])

        // Switching servers remounts a FRESH shell (ContentView is `.id(generation)`),
        // so a new view model hydrates ONLY namespace B's record.
        let nsB = FarmSnapshotFixtures.namespace(server: UUID(), user: UUID())
        let storeB = FakeFarmSnapshotStore(session: FarmSnapshotSession(namespace: nsB, generation: 2, token: 2))
        let printerB = try TestData.decodePrinter(from: TestJSON.printerMinimal)
        storeB.hydration = .snapshot(
            FarmSnapshotEnvelope(namespace: nsB, printers: [printerB], pendingReadyPrinterIDs: [], lastUpdatedAtMillis: 2)
        )
        let vmB = DashboardViewModel()
        vmB.configure(
            printerService: mockPrinterService,
            jobService: mockJobService,
            statisticsService: mockStatsService,
            jobAnalyticsService: mockJobAnalyticsService
        )
        vmB.configureSnapshot(store: storeB, autoPrintService: mockAutoDispatch, now: { [fixedNow] in fixedNow })

        await vmB.hydrateFromCache()

        XCTAssertEqual(vmB.printers.map(\.id), [printerB.id])
        XCTAssertFalse(vmB.printers.map(\.id).contains(printerA.id))
    }

    // MARK: Pending-ready projection (H6)

    func testCommitUsesPendingReadyFromAutoDispatch() async throws {
        let printer = try TestData.decodePrinter()
        mockPrinterService.printersToReturn = [printer]
        mockAutoDispatch.globalStatusToReturn = AutoDispatchGlobalStatus(
            globalEnabled: true,
            printers: [AutoDispatchStatus(printerId: printer.id, enabled: true, queueDepth: 0, state: "PendingReady")]
        )

        await viewModel.loadDashboard()

        XCTAssertTrue(viewModel.isPendingReady(printer))
        let committed = try XCTUnwrap(store.committedEnvelopes.first)
        let projected = try XCTUnwrap(committed.payload.first { $0.id == printer.id })
        XCTAssertTrue(projected.isPendingReady)
    }

    // MARK: Projection

    func testReconstructPrinterRoundTripsProgress() throws {
        let printer = try TestData.decodePrinter()
        let projection = FarmSnapshotPrinter(printer, isPendingReady: false)
        let rebuilt = try XCTUnwrap(DashboardViewModel.reconstructPrinter(from: projection))
        if let original = printer.progress {
            XCTAssertEqual(rebuilt.progress ?? -1, original, accuracy: 0.0001)
        }
        XCTAssertEqual(rebuilt.id, printer.id)
        XCTAssertEqual(rebuilt.name, printer.name)
        XCTAssertEqual(rebuilt.isOnline, printer.isOnline)
    }

    // MARK: Error classification

    func testClassifyMapsNetworkErrors() {
        XCTAssertEqual(DashboardViewModel.classify(NetworkError.noConnection), .offline)
        XCTAssertEqual(DashboardViewModel.classify(NetworkError.timeout), .offline)
        XCTAssertEqual(DashboardViewModel.classify(NetworkError.unauthorized), .unauthorized)
        XCTAssertEqual(DashboardViewModel.classify(NetworkError.forbidden), .forbidden)
        let decodeFailure = ResponseDecodingFailure(error: NSError(domain: "t", code: 1), targetType: Printer.self)
        XCTAssertEqual(DashboardViewModel.classify(NetworkError.decodingFailed(decodeFailure)), .decodeFailure)
        XCTAssertEqual(DashboardViewModel.classify(CancellationError()), .cancelled)
        XCTAssertEqual(DashboardViewModel.classify(NetworkError.notFound), .serverError)
        XCTAssertEqual(DashboardViewModel.classify(NetworkError.preconditionFailed(nil)), .serverError)
        XCTAssertEqual(DashboardViewModel.classify(NetworkError.preconditionRequired(nil)), .serverError)
    }
}

// MARK: - Connection / stale banner presentation (#817)

/// Pure banner-state tests covering connected, degraded, offline-with-cache, and
/// offline-without-cache — asserting text + accessibility (staleness is never
/// conveyed by color alone) with a fixed clock.
final class ConnectionStatusPresentationTests: XCTestCase {

    private let now = Date(timeIntervalSince1970: 1_700_000_000)
    private var calendar: Calendar { Calendar(identifier: .gregorian) }

    func testConnectedBanner() {
        let p = ConnectionStatusPresentation(status: .connected, now: now, calendar: calendar)
        XCTAssertEqual(p.label, "Connected")
        XCTAssertNil(p.timestampText)
        XCTAssertFalse(p.isStale)
        XCTAssertTrue(p.accessibilityLabel.contains("Connected"))
    }

    func testDegradedWithoutCacheBanner() {
        let p = ConnectionStatusPresentation(status: .degraded, hasCache: false, now: now, calendar: calendar)
        XCTAssertEqual(p.label, "Live updates paused")
        XCTAssertNil(p.timestampText)
        XCTAssertFalse(p.isStale)
    }

    func testOfflineWithCacheShowsTimestampAndStaleText() {
        let confirmed = now.addingTimeInterval(-300) // 5 min ago
        let p = ConnectionStatusPresentation(status: .offline, lastConfirmedAt: confirmed, hasCache: true, now: now, calendar: calendar)
        XCTAssertTrue(p.isStale)
        XCTAssertTrue(p.label.contains("cached"))
        let ts = p.timestampText ?? ""
        XCTAssertTrue(ts.contains("Last updated"))
        XCTAssertTrue(ts.contains("5 min ago"))
        // Staleness must be spoken, not color-only.
        XCTAssertTrue(p.accessibilityLabel.lowercased().contains("read-only"))
        XCTAssertTrue(p.accessibilityLabel.contains("5 min ago"))
    }

    func testOfflineWithoutCacheBanner() {
        let p = ConnectionStatusPresentation(status: .offline, lastConfirmedAt: nil, hasCache: false, now: now, calendar: calendar)
        XCTAssertFalse(p.isStale)
        XCTAssertEqual(p.label, "Offline")
        XCTAssertEqual(p.timestampText, "No cached data")
        XCTAssertTrue(p.accessibilityLabel.lowercased().contains("no cached fleet data"))
    }

    func testFormatConfirmedRelativeVariants() {
        let cal = calendar
        XCTAssertEqual(ConnectionStatusPresentation.formatConfirmed(now.addingTimeInterval(-30), now: now, calendar: cal), "just now")
        XCTAssertEqual(ConnectionStatusPresentation.formatConfirmed(now.addingTimeInterval(-600), now: now, calendar: cal), "10 min ago")
        // Older-same-day uses "at <time>"; different-day uses "on <date> <time>".
        XCTAssertTrue(ConnectionStatusPresentation.formatConfirmed(now.addingTimeInterval(-7200), now: now, calendar: cal).hasPrefix("at "))
        XCTAssertTrue(ConnectionStatusPresentation.formatConfirmed(now.addingTimeInterval(-172800), now: now, calendar: cal).hasPrefix("on "))
    }
}
