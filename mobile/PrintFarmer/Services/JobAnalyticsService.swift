import Foundation

// MARK: - Job Analytics Service

actor JobAnalyticsService: JobAnalyticsServiceProtocol {
    private let apiClient: APIClient

    init(apiClient: APIClient) {
        self.apiClient = apiClient
    }

    func getQueuedJobs(filterStatus: String? = nil, filterModel: String? = nil, filterMaterial: String? = nil, limit: Int? = nil, offset: Int? = nil) async throws -> [QueuedJobWithMeta] {
        var params: [String] = []
        if let status = filterStatus { params.append("filterStatus=\(status)") }
        if let model = filterModel { params.append("filterModel=\(model)") }
        if let material = filterMaterial { params.append("filterMaterial=\(material)") }
        params.append("limit=\(limit ?? 100)")
        params.append("offset=\(offset ?? 0)")
        let query = "?\(params.joined(separator: "&"))"
        return try await apiClient.get("/api/job-queue-analytics\(query)")
    }

    func getStats() async throws -> QueueStats {
        try await apiClient.get("/api/job-queue-analytics/stats")
    }

    func getModelStats() async throws -> [QueuePrinterModelStats] {
        try await apiClient.get("/api/job-queue-analytics/stats/models")
    }

    func getHistory(limit: Int? = nil, offset: Int? = nil, sortBy: String? = nil, statuses: String? = nil, dateStart: Date? = nil, dateEnd: Date? = nil) async throws -> QueueHistoryPage {
        var params: [String] = []
        params.append("limit=\(limit ?? 50)")
        params.append("offset=\(offset ?? 0)")
        if let sort = sortBy { params.append("sortBy=\(sort)") }
        if let s = statuses { params.append("statuses=\(s)") }
        if let start = dateStart { params.append("dateStart=\(Self.iso8601String(start))") }
        if let end = dateEnd { params.append("dateEnd=\(Self.iso8601String(end))") }
        let query = "?\(params.joined(separator: "&"))"
        return try await apiClient.get("/api/job-queue-analytics/history\(query)")
    }

    func getTimeline(dateFrom: Date? = nil, dateTo: Date? = nil, printerId: UUID? = nil, filterStatus: String? = nil, limit: Int? = nil) async throws -> [TimelineEvent] {
        var params: [String] = []
        if let from = dateFrom { params.append("dateFrom=\(Self.iso8601String(from))") }
        if let to = dateTo { params.append("dateTo=\(Self.iso8601String(to))") }
        if let pid = printerId { params.append("printerId=\(pid)") }
        if let status = filterStatus { params.append("filterStatus=\(status)") }
        if let lim = limit { params.append("limit=\(lim)") }
        let query = params.isEmpty ? "" : "?\(params.joined(separator: "&"))"
        return try await apiClient.get("/api/job-queue-analytics/timeline\(query)")
    }

    func getJobStateHistory(jobId: String) async throws -> JobStateHistory {
        try await apiClient.get("/api/job-queue-analytics/jobs/\(jobId)/state-history")
    }

    func getDurationAnalytics(printerId: UUID? = nil, dateFrom: Date? = nil, dateTo: Date? = nil) async throws -> DurationAnalytics {
        var params: [String] = []
        if let pid = printerId { params.append("printerId=\(pid)") }
        if let from = dateFrom { params.append("dateFrom=\(Self.iso8601String(from))") }
        if let to = dateTo { params.append("dateTo=\(Self.iso8601String(to))") }
        let query = params.isEmpty ? "" : "?\(params.joined(separator: "&"))"
        return try await apiClient.get("/api/job-queue-analytics/duration-analytics\(query)")
    }

    private static func iso8601String(_ date: Date) -> String {
        APIClient.iso8601Plain.string(from: date)
    }
}
