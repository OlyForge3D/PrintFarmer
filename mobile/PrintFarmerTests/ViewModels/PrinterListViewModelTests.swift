import XCTest
@testable import PrintFarmer

/// Tests for PrinterListViewModel: loading, error handling, filtering,
/// and search using MockPrinterService via configure() DI pattern.
@MainActor
final class PrinterListViewModelTests: XCTestCase {

    private var mockService: MockPrinterService!
    private var mockAutoDispatchService: MockAutoDispatchService!
    private var viewModel: PrinterListViewModel!

    override func setUp() {
        super.setUp()
        mockService = MockPrinterService()
        mockAutoDispatchService = MockAutoDispatchService()
        viewModel = PrinterListViewModel()
        viewModel.configure(printerService: mockService, autoPrintService: mockAutoDispatchService)
    }

    override func tearDown() {
        viewModel = nil
        mockAutoDispatchService = nil
        mockService = nil
        super.tearDown()
    }

    // MARK: - Initial State

    func testInitialState() {
        XCTAssertTrue(viewModel.printers.isEmpty)
        XCTAssertFalse(viewModel.isLoading)
        XCTAssertNil(viewModel.errorMessage)
        XCTAssertEqual(viewModel.searchText, "")
        XCTAssertEqual(viewModel.selectedStatus, .all)
    }

    // MARK: - Load Printers

    func testLoadPrintersSuccessPopulatesList() async throws {
        let printer = try TestData.decodePrinter()
        mockService.printersToReturn = [printer]

        await viewModel.loadPrinters()

        XCTAssertEqual(viewModel.printers.count, 1)
        XCTAssertTrue(mockService.listPrintersCalled)
        XCTAssertFalse(viewModel.isLoading)
        XCTAssertNil(viewModel.errorMessage)
    }

    func testLoadPrintersEmptyList() async {
        mockService.printersToReturn = []

        await viewModel.loadPrinters()

        XCTAssertTrue(viewModel.printers.isEmpty)
        XCTAssertNil(viewModel.errorMessage)
    }

    func testLoadPrintersError() async {
        mockService.errorToThrow = NetworkError.noConnection

        await viewModel.loadPrinters()

        XCTAssertTrue(viewModel.printers.isEmpty)
        XCTAssertNotNil(viewModel.errorMessage)
        XCTAssertFalse(viewModel.isLoading)
    }

    // MARK: - Search Filtering

    func testSearchFiltersByName() async throws {
        let mk4 = try TestData.decodePrinter(from: TestJSON.printer)
        let ender = try TestData.decodePrinter(from: TestJSON.printerMinimal)
        mockService.printersToReturn = [mk4, ender]
        await viewModel.loadPrinters()

        viewModel.searchText = "Prusa"
        XCTAssertEqual(viewModel.filteredPrinters.count, 1)
        XCTAssertEqual(viewModel.filteredPrinters.first?.name, "Prusa MK4")
    }

    func testSearchIsCaseInsensitive() async throws {
        let printer = try TestData.decodePrinter(from: TestJSON.printer)
        mockService.printersToReturn = [printer]
        await viewModel.loadPrinters()

        viewModel.searchText = "prusa"
        XCTAssertEqual(viewModel.filteredPrinters.count, 1)
    }

    func testEmptySearchReturnsAll() async throws {
        let mk4 = try TestData.decodePrinter(from: TestJSON.printer)
        let ender = try TestData.decodePrinter(from: TestJSON.printerMinimal)
        mockService.printersToReturn = [mk4, ender]
        await viewModel.loadPrinters()

        viewModel.searchText = ""
        XCTAssertEqual(viewModel.filteredPrinters.count, 2)
    }

    // MARK: - Status Filtering

    func testFilterByOnline() async throws {
        let online = try TestData.decodePrinter(from: TestJSON.printer)     // isOnline: true
        let offline = try TestData.decodePrinter(from: TestJSON.printerMinimal) // isOnline: false
        mockService.printersToReturn = [online, offline]
        await viewModel.loadPrinters()

        viewModel.selectedStatus = .online
        XCTAssertEqual(viewModel.filteredPrinters.count, 1)
        XCTAssertEqual(viewModel.filteredPrinters.first?.name, "Prusa MK4")
    }

    func testFilterByOffline() async throws {
        let online = try TestData.decodePrinter(from: TestJSON.printer)
        let offline = try TestData.decodePrinter(from: TestJSON.printerMinimal)
        mockService.printersToReturn = [online, offline]
        await viewModel.loadPrinters()

        viewModel.selectedStatus = .offline
        XCTAssertEqual(viewModel.filteredPrinters.count, 1)
        XCTAssertEqual(viewModel.filteredPrinters.first?.name, "Ender 3")
    }

    func testFilterByPrinting() async throws {
        let printing = try TestData.decodePrinter(from: TestJSON.printer) // state: "printing"
        let offline = try TestData.decodePrinter(from: TestJSON.printerMinimal) // state: nil
        mockService.printersToReturn = [printing, offline]
        await viewModel.loadPrinters()

        viewModel.selectedStatus = .printing
        XCTAssertEqual(viewModel.filteredPrinters.count, 1)
    }

    func testFilterAllShowsEverything() async throws {
        let mk4 = try TestData.decodePrinter(from: TestJSON.printer)
        let ender = try TestData.decodePrinter(from: TestJSON.printerMinimal)
        mockService.printersToReturn = [mk4, ender]
        await viewModel.loadPrinters()

        viewModel.selectedStatus = .all
        XCTAssertEqual(viewModel.filteredPrinters.count, 2)
    }

    // MARK: - Pull to Refresh

    func testPullToRefreshReloadsData() async throws {
        mockService.printersToReturn = []
        await viewModel.loadPrinters()
        XCTAssertEqual(viewModel.printers.count, 0)

        let printer = try TestData.decodePrinter()
        mockService.printersToReturn = [printer]
        await viewModel.loadPrinters()

        XCTAssertEqual(viewModel.printers.count, 1)
    }

    func testRefreshClearsError() async {
        mockService.errorToThrow = NetworkError.noConnection
        await viewModel.loadPrinters()
        XCTAssertNotNil(viewModel.errorMessage)

        mockService.errorToThrow = nil
        mockService.printersToReturn = []
        await viewModel.loadPrinters()

        XCTAssertNil(viewModel.errorMessage)
    }

    // MARK: - Not Configured

    func testLoadWithoutConfigureDoesNotCrash() async {
        let unconfigured = PrinterListViewModel()
        await unconfigured.loadPrinters()
        XCTAssertFalse(unconfigured.isLoading)
    }
}
