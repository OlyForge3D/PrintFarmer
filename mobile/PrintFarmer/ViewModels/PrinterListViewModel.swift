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
        let matches = printers.filter { printer in
            matchesSearch(printer) && matchesStatus(printer)
        }
        guard selectedStatus == .all else { return matches }
        return matches.sorted(by: farmOrder)
    }

    /// Farm "All" ordering: active → ready to harvest → error → ready to seed → maintenance → offline,
    /// then name.
    func stateGroup(_ printer: Printer) -> Int {
        guard printer.isOnline else { return 5 }
        if isPendingReady(printer) { return 1 }
        let state = printer.state?.lowercased() ?? ""
        if ["printing", "paused"].contains(state) { return 0 }
        if state == "error" { return 2 }
        if printer.inMaintenance { return 4 }
        return 3
    }

    private func farmOrder(_ lhs: Printer, _ rhs: Printer) -> Bool {
        let lhsGroup = stateGroup(lhs), rhsGroup = stateGroup(rhs)
        if lhsGroup != rhsGroup { return lhsGroup < rhsGroup }
        switch lhs.name.localizedCaseInsensitiveCompare(rhs.name) {
        case .orderedAscending: return true
        case .orderedDescending: return false
        case .orderedSame: return lhs.id.uuidString < rhs.id.uuidString
        }
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
                || printer.state?.lowercased() == "error"
        case .idle:
            printer.isOnline && ["ready", "idle"].contains(printer.state?.lowercased() ?? "")
        }
    }
}
