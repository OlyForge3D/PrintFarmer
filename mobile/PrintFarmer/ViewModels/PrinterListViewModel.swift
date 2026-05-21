import Foundation

@MainActor @Observable
final class PrinterListViewModel {
    var printers: [Printer] = []
    var locations: [Location] = []
    var autoDispatchStatuses: [UUID: AutoDispatchStatus] = [:]
    var isLoading = false
    var errorMessage: String?
    var searchText: String = ""
    var selectedStatus: StatusFilter = .all
    var selectedLocationId: UUID?

    enum StatusFilter: String, CaseIterable, Identifiable {
        case all = "All"
        case online = "Online"
        case printing = "Printing"
        case offline = "Offline"
        case error = "Error"

        var id: String { rawValue }
    }

    private var printerService: (any PrinterServiceProtocol)?
    private var autoPrintService: (any AutoDispatchServiceProtocol)?
    private var signalRService: (any SignalRServiceProtocol)?

    func configure(printerService: any PrinterServiceProtocol, autoPrintService: any AutoDispatchServiceProtocol) {
        self.printerService = printerService
        self.autoPrintService = autoPrintService
    }

    func configureSignalR(_ service: any SignalRServiceProtocol) {
        self.signalRService = service
        service.onPrinterUpdated { [weak self] update in
            Task { @MainActor [weak self] in
                self?.applyListUpdate(update)
            }
        }
    }

    private func applyListUpdate(_ update: PrinterStatusUpdate) {
        guard let idx = printers.firstIndex(where: { $0.id == update.id }) else { return }
        printers[idx].isOnline = update.isOnline
        if let s = update.state { printers[idx].state = s }
        if let prog = update.progress { printers[idx].progress = prog / 100.0 }
        if let name = update.jobName { printers[idx].jobName = name }
        if let fn = update.fileName { printers[idx].fileName = fn }
        if let hotend = update.hotendTemp { printers[idx].hotendTemp = hotend }
        if let bed = update.bedTemp { printers[idx].bedTemp = bed }
        if let ht = update.hotendTarget { printers[idx].hotendTarget = ht }
        if let bt = update.bedTarget { printers[idx].bedTarget = bt }
        if let spool = update.spoolInfo { printers[idx].spoolInfo = spool }
    }

    func loadPrinters() async {
        guard let printerService else { return }
        isLoading = true
        errorMessage = nil

        do {
            printers = try await printerService.list()
            await loadAutoDispatchStatuses()
        } catch {
            errorMessage = error.localizedDescription
        }

        isLoading = false
    }

    func loadAutoDispatchStatuses() async {
        guard let autoPrintService else { return }
        do {
            let statuses = try await autoPrintService.getAllStatus()
            autoDispatchStatuses = Dictionary(uniqueKeysWithValues: statuses.printers.map { ($0.printerId, $0) })
        } catch {
            // Non-critical — cards will fall back to printer state
        }
    }

    // MARK: - Filtered Results

    var filteredPrinters: [Printer] {
        printers.filter { printer in
            matchesSearch(printer) && matchesStatus(printer) && matchesLocation(printer)
        }
        .sorted { sortPriority($0) < sortPriority($1) }
    }

    func isPendingReady(_ printer: Printer) -> Bool {
        autoDispatchStatuses[printer.id]?.state == "PendingReady"
    }

    private func sortPriority(_ printer: Printer) -> Int {
        // PendingReady always sorts to top regardless of isOnline
        if isPendingReady(printer) { return 0 }
        guard printer.isOnline else { return 100 }
        switch printer.state?.lowercased() {
        case "printing": return 1
        case "ready", "idle": return 2
        default: return 3
        }
    }

    private func matchesSearch(_ printer: Printer) -> Bool {
        guard !searchText.isEmpty else { return true }
        return printer.name.localizedCaseInsensitiveContains(searchText)
    }

    private func matchesStatus(_ printer: Printer) -> Bool {
        switch selectedStatus {
        case .all: return true
        case .online: return printer.isOnline
        case .printing: return printer.state?.lowercased() == "printing"
        case .offline: return !printer.isOnline
        case .error: return printer.state?.lowercased() == "error"
        }
    }

    private func matchesLocation(_ printer: Printer) -> Bool {
        guard let locationId = selectedLocationId else { return true }
        return printer.location?.id == locationId
    }

    /// Unique locations from loaded printers.
    var availableLocations: [LocationSummary] {
        let seen = NSMutableSet()
        return printers.compactMap(\.location).filter { loc in
            guard !seen.contains(loc.id) else { return false }
            seen.add(loc.id)
            return true
        }
    }
}
