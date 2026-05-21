import Foundation
import os

@MainActor @Observable
final class DispatchViewModel {
    var queueStatus: DispatchQueueStatus?
    var history: [DispatchHistoryEntry] = []
    var isLoading = false
    var error: String?
    var isViewActive = true

    private let logger = Logger(subsystem: "com.printfarmer.ios", category: "Dispatch")
    private var dispatchService: (any DispatchServiceProtocol)?

    func configure(dispatchService: any DispatchServiceProtocol) {
        self.dispatchService = dispatchService
    }

    func loadQueueStatus() async {
        guard let dispatchService, isViewActive else { return }
        isLoading = true
        error = nil

        do {
            let status = try await dispatchService.getQueueStatus()
            guard !Task.isCancelled else { return }
            queueStatus = status
        } catch {
            guard !Task.isCancelled else { return }
            self.error = error.localizedDescription
        }

        guard !Task.isCancelled else { return }
        isLoading = false
    }

    func loadHistory() async {
        guard let dispatchService, isViewActive else { return }
        do {
            let page = try await dispatchService.getHistory(page: 1, pageSize: 50)
            guard !Task.isCancelled else { return }
            history = page.items
        } catch {
            guard !Task.isCancelled else { return }
            logger.warning("Failed to load dispatch history: \(error.localizedDescription)")
        }
    }

    // MARK: - Computed

    var pendingJobCount: Int { queueStatus?.pendingUnassignedJobs ?? 0 }
    var totalQueuedJobs: Int { queueStatus?.totalQueuedJobs ?? 0 }
    var idlePrinterCount: Int { queueStatus?.idlePrinters ?? 0 }
    var busyPrinterCount: Int { queueStatus?.busyPrinters ?? 0 }
    var dispatchedLast24h: Int { queueStatus?.stats.dispatchesLast24Hours ?? 0 }
    var autoDispatchedLast24h: Int { queueStatus?.stats.autoDispatchesLast24Hours ?? 0 }
    var failedLast24h: Int { queueStatus?.stats.failedDispatchesLast24Hours ?? 0 }
    var averageScoreLast24h: Double { queueStatus?.stats.averageScoreLast24Hours ?? 0 }
}
