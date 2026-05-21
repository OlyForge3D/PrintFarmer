import Foundation

// MARK: - Statistics Service

actor StatisticsService: StatisticsServiceProtocol {
    private let apiClient: APIClient

    init(apiClient: APIClient) {
        self.apiClient = apiClient
    }

    func getSummary(days: Int? = nil) async throws -> StatisticsSummary {
        let query = days.map { "?days=\($0)" } ?? ""
        return try await apiClient.get("/api/statistics/summary\(query)")
    }
}
