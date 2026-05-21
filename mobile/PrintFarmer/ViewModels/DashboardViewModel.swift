import Foundation
import os

@MainActor @Observable
final class DashboardViewModel {
    var printers: [Printer] = []
    var queueOverview: [QueueOverview] = []
    var activeJobs: [QueuedPrintJobResponse] = []
    var summary: StatisticsSummary?
    var queueStats: QueueStats?
    var modelStats: [QueuePrinterModelStats] = []
    var upcomingJobs: [QueuedJobWithMeta] = []
    var isLoading = false
    var errorMessage: String?
    var isViewActive = true

    private let logger = Logger(subsystem: "com.printfarmer.ios", category: "Dashboard")

    private var printerService: (any PrinterServiceProtocol)?
    private var jobService: (any JobServiceProtocol)?
    private var statisticsService: (any StatisticsServiceProtocol)?
    private var jobAnalyticsService: (any JobAnalyticsServiceProtocol)?
    private var signalRService: (any SignalRServiceProtocol)?

    func configure(
        printerService: any PrinterServiceProtocol,
        jobService: any JobServiceProtocol,
        statisticsService: any StatisticsServiceProtocol,
        jobAnalyticsService: any JobAnalyticsServiceProtocol
    ) {
        self.printerService = printerService
        self.jobService = jobService
        self.statisticsService = statisticsService
        self.jobAnalyticsService = jobAnalyticsService
    }

    func configureSignalR(_ service: any SignalRServiceProtocol) {
        self.signalRService = service
        service.onPrinterUpdated { [weak self] update in
            Task { @MainActor [weak self] in
                self?.applyPrinterUpdate(update)
            }
        }
    }

    private func applyPrinterUpdate(_ update: PrinterStatusUpdate) {
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

    func loadDashboard() async {
        guard let printerService, let jobService, isViewActive else { return }
        isLoading = true
        errorMessage = nil

        do {
            async let printersTask = printerService.list()
            async let queueTask = jobService.list()
            async let allJobsTask = jobService.listAllJobs()
            let p = try await printersTask
            let q = try await queueTask
            let allJobs = try await allJobsTask
            guard isViewActive else { return }
            printers = p
            queueOverview = q
            activeJobs = allJobs.filter {
                guard let status = $0.job.jobStatus else { return false }
                return [.printing, .starting, .paused].contains(status)
            }

            do {
                let s = try await statisticsService?.getSummary()
                guard isViewActive else { return }
                summary = s
            } catch {
                logger.warning("Failed to load statistics summary: \(error.localizedDescription)")
            }

            // Load farm status data
            do {
                async let statsTask = jobAnalyticsService?.getStats()
                async let modelStatsTask = jobAnalyticsService?.getModelStats()
                async let upcomingTask = jobAnalyticsService?.getQueuedJobs(
                    filterStatus: "queued",
                    filterModel: nil,
                    filterMaterial: nil,
                    limit: 5,
                    offset: 0
                )
                let qs = try await statsTask
                let ms = try await modelStatsTask ?? []
                let uj = try await upcomingTask ?? []
                guard isViewActive else { return }
                queueStats = qs
                modelStats = ms
                upcomingJobs = uj
            } catch {
                logger.warning("Failed to load farm status data: \(error.localizedDescription)")
            }
        } catch {
            guard isViewActive else { return }
            errorMessage = error.localizedDescription
        }

        guard isViewActive else { return }
        isLoading = false
    }

    // MARK: - Computed Summaries

    var onlineCount: Int { printers.filter(\.isOnline).count }

    var printingCount: Int {
        printers.filter { $0.state?.lowercased() == "printing" }.count
    }

    var pausedCount: Int {
        printers.filter { $0.state?.lowercased() == "paused" }.count
    }

    var offlineCount: Int { printers.filter { !$0.isOnline }.count }

    var errorCount: Int {
        printers.filter { $0.state?.lowercased() == "error" }.count
    }

    var maintenanceCount: Int {
        printers.filter(\.inMaintenance).count
    }

    var activeJobCount: Int {
        queueOverview.filter { $0.currentJobId != nil }.count
    }

    var queuedJobCount: Int {
        queueOverview.reduce(0) { $0 + $1.queuedJobsCount }
    }

    var hasMaintenanceAlerts: Bool { maintenanceCount > 0 }

    var printersInMaintenance: [Printer] {
        printers.filter(\.inMaintenance)
    }

    // MARK: - Farm Status Helpers

    func activeJobForPrinter(_ printerId: UUID) -> QueuedPrintJobResponse? {
        let idString = printerId.uuidString
        return activeJobs.first { $0.job.assignedPrinterId?.caseInsensitiveCompare(idString) == .orderedSame }
    }

    var activePrintingPrinters: [Printer] {
        printers.filter { $0.state?.lowercased() == "printing" }
            .sorted { sortPriority($0) < sortPriority($1) }
    }
    
    private func sortPriority(_ printer: Printer) -> Int {
        guard printer.isOnline else { return 100 }
        switch printer.state?.lowercased() {
        case "pendingready": return 0
        case "printing": return 1
        case "ready", "idle": return 2
        default: return 3
        }
    }
}
