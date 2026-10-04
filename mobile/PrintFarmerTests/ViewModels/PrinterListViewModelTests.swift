import XCTest
@testable import PrintFarmer

@MainActor
final class PrinterListViewModelTests: XCTestCase {
    func testInitialState() {
        let viewModel = PrinterListViewModel()

        XCTAssertTrue(viewModel.printers.isEmpty)
        XCTAssertEqual(viewModel.searchText, "")
        XCTAssertEqual(viewModel.selectedStatus, .all)
        XCTAssertNil(viewModel.selectedLocationId)
        XCTAssertTrue(viewModel.filteredPrinters.isEmpty)
    }

    func testFarmDataReplacesPrinterAndPendingReadyProjections() throws {
        let printer = try TestData.decodePrinter()
        let viewModel = PrinterListViewModel()

        viewModel.setFarmData([printer], pendingReadyPrinterIDs: [printer.id])
        XCTAssertEqual(viewModel.printers.map(\.id), [printer.id])
        XCTAssertTrue(viewModel.isPendingReady(printer))

        viewModel.setFarmData([], pendingReadyPrinterIDs: [])
        XCTAssertTrue(viewModel.printers.isEmpty)
        XCTAssertFalse(viewModel.isPendingReady(printer))
    }

    func testSearchIsCaseInsensitiveAndStatusFilterUsesFarmProjection() throws {
        let online = try TestData.decodePrinter(from: TestJSON.printer)
        let offline = try TestData.decodePrinter(from: TestJSON.printerMinimal)
        let viewModel = PrinterListViewModel(initialPrinters: [online, offline])

        viewModel.searchText = "prusa"
        XCTAssertEqual(viewModel.filteredPrinters.map(\.id), [online.id])

        viewModel.searchText = ""
        viewModel.selectedStatus = .offline
        XCTAssertEqual(viewModel.filteredPrinters.map(\.id), [offline.id])
    }

    func testNeedsAttentionIncludesPendingReadyAndFeedPrinters() throws {
        let pending = try TestData.decodePrinter(from: TestJSON.printerMinimal)
        let feed = try TestData.decodePrinter(from: TestJSON.printer)
        let viewModel = PrinterListViewModel(
            initialPrinters: [pending, feed],
            pendingReadyPrinterIDs: [pending.id]
        )
        viewModel.selectedStatus = .needsAttention
        viewModel.attentionPrinterIDs = [feed.id]

        XCTAssertEqual(Set(viewModel.filteredPrinters.map(\.id)), [pending.id, feed.id])
        XCTAssertEqual(viewModel.filteredPrinters.first?.id, pending.id)
    }
}
