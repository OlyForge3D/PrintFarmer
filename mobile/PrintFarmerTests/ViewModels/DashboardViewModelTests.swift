import XCTest
@testable import PrintFarmer

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

    override func setUp() {
        super.setUp()
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

    override func tearDown() {
        viewModel = nil
        mockPrinterService = nil
        mockJobService = nil
        mockStatsService = nil
        mockJobAnalyticsService = nil
        super.tearDown()
    }

    // MARK: - Initial State

    func testInitialState() {
        XCTAssertTrue(viewModel.printers.isEmpty)
        XCTAssertTrue(viewModel.queueOverview.isEmpty)
        XCTAssertFalse(viewModel.isLoading)
        XCTAssertNil(viewModel.errorMessage)
        XCTAssertNil(viewModel.summary)
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

    override func setUp() {
        super.setUp()
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

    override func tearDown() {
        viewModel = nil
        store = nil
        mockAutoDispatch = nil
        mockJobAnalyticsService = nil
        mockStatsService = nil
        mockJobService = nil
        mockPrinterService = nil
        super.tearDown()
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

    // MARK: Commit / stale clearing

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
