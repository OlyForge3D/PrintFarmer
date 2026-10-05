import Foundation

@MainActor @Observable
final class PrinterListViewModel {
    var printers: [Printer]
    var searchText = ""
    var selectedStatus: StatusFilter = .all
    var attentionPrinterIDs: Set<UUID> = []
    private(set) var pendingReadyPrinterIDs: Set<UUID>

    enum StatusFilter: String, CaseIterable, Identifiable {
        case all = "All"
        case printing = "Printing"
        case needsAttention = "Needs attention"
        case idle = "Idle"

        var id: String { rawValue }
    }

    init(
        initialPrinters: [Printer] = [],
        pendingReadyPrinterIDs: Set<UUID> = []
    ) {
        printers = initialPrinters
        self.pendingReadyPrinterIDs = pendingReadyPrinterIDs
    }

    func setFarmData(_ printers: [Printer], pendingReadyPrinterIDs: Set<UUID>) {
        self.printers = printers
        self.pendingReadyPrinterIDs = pendingReadyPrinterIDs
    }

    var filteredPrinters: [Printer] {
        printers.filter { printer in
            matchesSearch(printer) && matchesStatus(printer)
        }
        .sorted { sortPriority($0) < sortPriority($1) }
    }

    func count(for filter: StatusFilter) -> Int {
        printers.filter { matchesStatus($0, filter: filter) }.count
    }

    func isPendingReady(_ printer: Printer) -> Bool {
        pendingReadyPrinterIDs.contains(printer.id)
    }

    var availableLocations: [LocationSummary] {
        var seen = Set<UUID>()
        return printers.compactMap(\.location).filter { seen.insert($0.id).inserted }
    }

    private func sortPriority(_ printer: Printer) -> Int {
        if isPendingReady(printer) { return 0 }
        guard printer.isOnline else { return 100 }
        switch printer.state?.lowercased() {
        case "printing": return 1
        case "ready", "idle": return 2
        default: return 3
        }
    }

    private func matchesSearch(_ printer: Printer) -> Bool {
        searchText.isEmpty || printer.name.localizedCaseInsensitiveContains(searchText)
    }

    private func matchesStatus(_ printer: Printer) -> Bool {
        matchesStatus(printer, filter: selectedStatus)
    }

    private func matchesStatus(_ printer: Printer, filter: StatusFilter) -> Bool {
        switch filter {
        case .all: true
        case .printing: printer.state?.lowercased() == "printing"
        case .needsAttention:
            attentionPrinterIDs.contains(printer.id)
                || isPendingReady(printer)
                || ["error", "paused"].contains(printer.state?.lowercased() ?? "")
        case .idle:
            printer.isOnline && ["ready", "idle"].contains(printer.state?.lowercased() ?? "")
        }
    }
}
