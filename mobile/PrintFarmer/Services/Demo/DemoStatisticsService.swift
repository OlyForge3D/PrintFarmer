import Foundation

// MARK: - Demo Statistics Service

final class DemoStatisticsService: StatisticsServiceProtocol, @unchecked Sendable {
    func getSummary(days: Int?) async throws -> StatisticsSummary {
        DemoData.statisticsSummary
    }
}
