import Foundation
@testable import PrintFarmer

final class MockJobAnalyticsService: JobAnalyticsServiceProtocol, @unchecked Sendable {
    var queuedJobsToReturn: [QueuedJobWithMeta] = []
    var statsToReturn: QueueStats?
    var modelStatsToReturn: [QueuePrinterModelStats] = []
    var historyPageToReturn: QueueHistoryPage?
    var timelineToReturn: [TimelineEvent] = []
    var jobStateHistoryToReturn: JobStateHistory?
    var durationAnalyticsToReturn: DurationAnalytics?
    var errorToThrow: Error?
    
    // Call tracking
    var getQueuedJobsCalledWith: (filterStatus: String?, filterModel: String?, filterMaterial: String?, limit: Int?, offset: Int?)?
    var getStatsCalled = false
    var getModelStatsCalled = false
    var getHistoryCalledWith: (limit: Int?, offset: Int?, sortBy: String?, statuses: String?, dateStart: Date?, dateEnd: Date?)?
    var getTimelineCalledWith: (dateFrom: Date?, dateTo: Date?, printerId: UUID?, filterStatus: String?, limit: Int?)?
    var getJobStateHistoryCalledWith: String?
    var getDurationAnalyticsCalledWith: (printerId: UUID?, dateFrom: Date?, dateTo: Date?)?
    
    func getQueuedJobs(filterStatus: String?, filterModel: String?, filterMaterial: String?, limit: Int?, offset: Int?) async throws -> [QueuedJobWithMeta] {
        getQueuedJobsCalledWith = (filterStatus, filterModel, filterMaterial, limit, offset)
        if let error = errorToThrow { throw error }
        return queuedJobsToReturn
    }
    
    func getStats() async throws -> QueueStats {
        getStatsCalled = true
        if let error = errorToThrow { throw error }
        return statsToReturn ?? QueueStats(totalQueued: 0, totalPrinting: 0, totalPaused: 0, averageWaitTimeMinutes: 0, byModel: [])
    }
    
    func getModelStats() async throws -> [QueuePrinterModelStats] {
        getModelStatsCalled = true
        if let error = errorToThrow { throw error }
        return modelStatsToReturn
    }
    
    func getHistory(limit: Int? = nil, offset: Int? = nil, sortBy: String? = nil, statuses: String? = nil, dateStart: Date? = nil, dateEnd: Date? = nil) async throws -> QueueHistoryPage {
        getHistoryCalledWith = (limit, offset, sortBy, statuses, dateStart, dateEnd)
        if let error = errorToThrow { throw error }
        return historyPageToReturn!
    }
    
    func getTimeline(dateFrom: Date?, dateTo: Date?, printerId: UUID?, filterStatus: String?, limit: Int?) async throws -> [TimelineEvent] {
        getTimelineCalledWith = (dateFrom, dateTo, printerId, filterStatus, limit)
        if let error = errorToThrow { throw error }
        return timelineToReturn
    }
    
    func getJobStateHistory(jobId: String) async throws -> JobStateHistory {
        getJobStateHistoryCalledWith = jobId
        if let error = errorToThrow { throw error }
        return jobStateHistoryToReturn!
    }
    
    func getDurationAnalytics(printerId: UUID?, dateFrom: Date?, dateTo: Date?) async throws -> DurationAnalytics {
        getDurationAnalyticsCalledWith = (printerId, dateFrom, dateTo)
        if let error = errorToThrow { throw error }
        return durationAnalyticsToReturn!
    }
}
