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
