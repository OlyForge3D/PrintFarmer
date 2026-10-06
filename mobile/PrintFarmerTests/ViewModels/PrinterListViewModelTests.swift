import XCTest
@testable import PrintFarmer

@MainActor
final class PrinterListViewModelTests: XCTestCase {
    func testInitialState() {
        let viewModel = PrinterListViewModel()

        XCTAssertTrue(viewModel.printers.isEmpty)
        XCTAssertEqual(viewModel.searchText, "")
        XCTAssertEqual(viewModel.selectedStatus, .all)
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
        var printing = try TestData.decodePrinter(from: TestJSON.printer)
        printing.state = "printing"
        printing.isOnline = true
        var idle = try TestData.decodePrinter(from: TestJSON.printerMinimal)
        idle.state = "idle"
        idle.isOnline = true
        let viewModel = PrinterListViewModel(initialPrinters: [printing, idle])

        viewModel.searchText = "prusa"
        XCTAssertEqual(viewModel.filteredPrinters.map(\.id), [printing.id])

        viewModel.searchText = ""
        viewModel.selectedStatus = .printing
        XCTAssertEqual(viewModel.filteredPrinters.map(\.id), [printing.id])
        viewModel.selectedStatus = .idle
        XCTAssertEqual(viewModel.filteredPrinters.map(\.id), [idle.id])
        XCTAssertEqual(viewModel.count(for: .all), 2)
        XCTAssertEqual(viewModel.count(for: .printing), 1)
        XCTAssertEqual(viewModel.count(for: .idle), 1)
    }

    func testFilteredPrintersPreserveFarmOrderAcrossStatusGroups() throws {
        func printer(id: String, state: String, isOnline: Bool = true) throws -> Printer {
            let json = TestJSON.printerMinimal.replacingOccurrences(
                of: "660e8400-29b-41d4-a716-446655440001",
                with: id
            )
            var printer = try TestData.decodePrinter(from: json)
            printer.state = state
            printer.isOnline = isOnline
            return printer
        }

        let printing = try printer(id: "660e8400-29b-41d4-a716-446655440010", state: "printing")
        let failed = try printer(id: "660e8400-29b-41d4-a716-446655440011", state: "error")
        let bedClear = try printer(id: "660e8400-29b-41d4-a716-446655440012", state: "completed")
        let paused = try printer(id: "660e8400-29b-41d4-a716-446655440013", state: "paused")
        let idle = try printer(id: "660e8400-29b-41d4-a716-446655440014", state: "idle")
        let offline = try printer(
            id: "660e8400-29b-41d4-a716-446655440015",
            state: "offline",
            isOnline: false
        )
        let ordered = [printing, failed, bedClear, paused, idle, offline]
        let viewModel = PrinterListViewModel(
            initialPrinters: ordered,
            pendingReadyPrinterIDs: [bedClear.id]
        )

        XCTAssertEqual(viewModel.filteredPrinters.map(\.id), ordered.map(\.id))
    }

    func testNeedsAttentionIncludesPendingReadyAndFeedPrintersButNotPausedPrinters() throws {
        let pending = try TestData.decodePrinter(from: TestJSON.printerMinimal)
        let feed = try TestData.decodePrinter(from: TestJSON.printer)
        let pausedFixture = TestJSON.printerMinimal.replacingOccurrences(
            of: "660e8400-e29b-41d4-a716-446655440001",
            with: "660e8400-e29b-41d4-a716-446655440003"
        )
        var paused = try TestData.decodePrinter(from: pausedFixture)
        paused.state = "paused"
        let viewModel = PrinterListViewModel(
            initialPrinters: [pending, feed, paused],
            pendingReadyPrinterIDs: [pending.id]
        )
        viewModel.selectedStatus = .needsAttention
        viewModel.attentionPrinterIDs = [feed.id]

        XCTAssertEqual(Set(viewModel.filteredPrinters.map(\.id)), [pending.id, feed.id])
        XCTAssertEqual(viewModel.filteredPrinters.first?.id, pending.id)
        XCTAssertEqual(viewModel.count(for: .needsAttention), 2)
    }
}
