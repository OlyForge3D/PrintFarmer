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

    private func makePrinter(
        _ suffix: String,
        name: String,
        state: String,
        isOnline: Bool = true,
        inMaintenance: Bool = false
    ) throws -> Printer {
        let json = """
        {
            "id": "660e8400-e29b-41d4-a716-4466554400\(suffix)",
            "rowVersion": "AQIDBA==",
            "name": "\(name)",
            "backend": "Moonraker",
            "backendPort": 7125,
            "inMaintenance": \(inMaintenance),
            "isEnabled": true,
            "isOnline": \(isOnline)
        }
        """
        var printer = try TestData.decodePrinter(from: json)
        printer.state = state
        return printer
    }

    func testAllViewSortsByStateGroupThenName() throws {
        let offlineA = try makePrinter("10", name: "alpha", state: "printing", isOnline: false)
        let readyZ = try makePrinter("11", name: "Zeta", state: "idle")
        let maintenance = try makePrinter("12", name: "Bravo", state: "idle", inMaintenance: true)
        let pausedC = try makePrinter("13", name: "charlie", state: "paused")
        let readyB = try makePrinter("14", name: "bravo 2", state: "ready")
        let failed = try makePrinter("15", name: "Delta", state: "error")
        let printingA = try makePrinter("16", name: "Alpha", state: "printing")
        let bedClear = try makePrinter("17", name: "Echo", state: "printing")
        let offlineB = try makePrinter("18", name: "Beta", state: "idle", isOnline: false)

        let source = [offlineA, readyZ, maintenance, pausedC, readyB, failed, printingA, bedClear, offlineB]
        let viewModel = PrinterListViewModel(
            initialPrinters: source,
            pendingReadyPrinterIDs: [bedClear.id]
        )

        let expected = [printingA, pausedC, bedClear, failed, readyB, readyZ, maintenance, offlineA, offlineB]
        XCTAssertEqual(viewModel.filteredPrinters.map(\.name), expected.map(\.name))

        // Stable: reshuffled input (e.g. a SignalR refresh) yields the same order.
        viewModel.setFarmData(source.reversed(), pendingReadyPrinterIDs: [bedClear.id])
        XCTAssertEqual(viewModel.filteredPrinters.map(\.name), expected.map(\.name))

        // A real state change moves the card to its new group.
        var finished = printingA
        finished.state = "idle"
        viewModel.setFarmData(source.map { $0.id == finished.id ? finished : $0 },
                              pendingReadyPrinterIDs: [bedClear.id])
        XCTAssertEqual(viewModel.filteredPrinters.map(\.name),
                       ["charlie", "Echo", "Delta", "Alpha", "bravo 2", "Zeta", "Bravo", "alpha", "Beta"])
    }

    func testAllViewBreaksDuplicateNameTiesById() throws {
        let second = try makePrinter("31", name: "Twin", state: "idle")
        let first = try makePrinter("30", name: "twin", state: "idle")
        let viewModel = PrinterListViewModel(initialPrinters: [second, first])
        XCTAssertEqual(viewModel.filteredPrinters.map(\.id), [first.id, second.id])
        viewModel.setFarmData([first, second], pendingReadyPrinterIDs: [])
        XCTAssertEqual(viewModel.filteredPrinters.map(\.id), [first.id, second.id])
    }

    func testAllViewGroupsMaintenanceUnknownAndCompletedStates() throws {
        let errorMaintenance = try makePrinter("40", name: "A error maint", state: "error", inMaintenance: true)
        let pausedMaintenance = try makePrinter("41", name: "B paused maint", state: "paused", inMaintenance: true)
        let unknown = try makePrinter("42", name: "C unknown", state: "calibrating")
        let completed = try makePrinter("43", name: "D completed", state: "completed")
        let error = try makePrinter("44", name: "E error", state: "error")
        let viewModel = PrinterListViewModel(
            initialPrinters: [errorMaintenance, pausedMaintenance, unknown, completed, error]
        )

        XCTAssertEqual(viewModel.stateGroup(pausedMaintenance), 0)
        XCTAssertEqual(viewModel.stateGroup(error), 2)
        XCTAssertEqual(viewModel.stateGroup(unknown), 3)
        XCTAssertEqual(viewModel.stateGroup(completed), 3)
        XCTAssertEqual(viewModel.stateGroup(errorMaintenance), 4)
        XCTAssertEqual(viewModel.filteredPrinters.map(\.name),
                       ["B paused maint", "E error", "C unknown", "D completed", "A error maint"])
    }

    func testNonAllFiltersKeepSourceOrder() throws {
        let zeta = try makePrinter("20", name: "Zeta", state: "idle")
        let alpha = try makePrinter("21", name: "Alpha", state: "idle")
        let viewModel = PrinterListViewModel(initialPrinters: [zeta, alpha])
        viewModel.selectedStatus = .idle
        XCTAssertEqual(viewModel.filteredPrinters.map(\.name), ["Zeta", "Alpha"])
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
