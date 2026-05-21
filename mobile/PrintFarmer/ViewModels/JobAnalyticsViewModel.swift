import Foundation
import os

@MainActor @Observable
final class JobAnalyticsViewModel {
    var jobs: [QueuedJobWithMeta] = []
    var stats: QueueStats?
    var modelStats: [QueuePrinterModelStats] = []
    var selectedStatus: String?
    var selectedModel: String?
    var selectedMaterial: String?
    var isLoading = false
    var error: String?
    var isViewActive = true

    private let logger = Logger(subsystem: "com.printfarmer.ios", category: "JobAnalytics")
    private var jobAnalyticsService: (any JobAnalyticsServiceProtocol)?

    func configure(jobAnalyticsService: any JobAnalyticsServiceProtocol) {
        self.jobAnalyticsService = jobAnalyticsService
    }

    func loadJobs() async {
        guard let jobAnalyticsService, isViewActive else { return }
        isLoading = true
        error = nil

        do {
            let result = try await jobAnalyticsService.getQueuedJobs(
                filterStatus: selectedStatus,
                filterModel: selectedModel,
                filterMaterial: selectedMaterial,
                limit: 50,
                offset: 0
            )
            guard isViewActive else { return }
            jobs = result
        } catch {
            guard isViewActive else { return }
            self.error = error.localizedDescription
        }

        guard isViewActive else { return }
        isLoading = false
    }

    func loadStats() async {
        guard let jobAnalyticsService, isViewActive else { return }

        do {
            async let statsTask = jobAnalyticsService.getStats()
            async let modelStatsTask = jobAnalyticsService.getModelStats()
            let s = try await statsTask
            let ms = try await modelStatsTask
            guard isViewActive else { return }
            stats = s
            modelStats = ms
        } catch {
            guard isViewActive else { return }
            logger.warning("Failed to load job stats: \(error.localizedDescription)")
        }
    }

    func applyFilters() async {
        await loadJobs()
    }

    func clearFilters() {
        selectedStatus = nil
        selectedModel = nil
        selectedMaterial = nil
    }
}
