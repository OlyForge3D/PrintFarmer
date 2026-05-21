import Foundation

// MARK: - Statistics Service Protocol

protocol StatisticsServiceProtocol: Sendable {
    func getSummary(days: Int?) async throws -> StatisticsSummary
}

extension StatisticsServiceProtocol {
    func getSummary() async throws -> StatisticsSummary {
        try await getSummary(days: nil)
    }
}
