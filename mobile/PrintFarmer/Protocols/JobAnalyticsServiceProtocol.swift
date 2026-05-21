import Foundation

// MARK: - Job Analytics Service Protocol

protocol JobAnalyticsServiceProtocol: Sendable {
    func getQueuedJobs(filterStatus: String?, filterModel: String?, filterMaterial: String?, limit: Int?, offset: Int?) async throws -> [QueuedJobWithMeta]
    func getStats() async throws -> QueueStats
    func getModelStats() async throws -> [QueuePrinterModelStats]
    func getHistory(limit: Int?, offset: Int?, sortBy: String?, statuses: String?, dateStart: Date?, dateEnd: Date?) async throws -> QueueHistoryPage
    func getTimeline(dateFrom: Date?, dateTo: Date?, printerId: UUID?, filterStatus: String?, limit: Int?) async throws -> [TimelineEvent]
    func getJobStateHistory(jobId: String) async throws -> JobStateHistory
    func getDurationAnalytics(printerId: UUID?, dateFrom: Date?, dateTo: Date?) async throws -> DurationAnalytics
}

extension JobAnalyticsServiceProtocol {
    func getQueuedJobs() async throws -> [QueuedJobWithMeta] {
        try await getQueuedJobs(filterStatus: nil, filterModel: nil, filterMaterial: nil, limit: nil, offset: nil)
    }

    func getHistory() async throws -> QueueHistoryPage {
        try await getHistory(limit: nil, offset: nil, sortBy: nil, statuses: nil, dateStart: nil, dateEnd: nil)
    }

    func getTimeline() async throws -> [TimelineEvent] {
        try await getTimeline(dateFrom: nil, dateTo: nil, printerId: nil, filterStatus: nil, limit: nil)
    }

    func getDurationAnalytics() async throws -> DurationAnalytics {
        try await getDurationAnalytics(printerId: nil, dateFrom: nil, dateTo: nil)
    }
}
