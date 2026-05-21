import Foundation
@testable import PrintFarmer

final class MockStatisticsService: StatisticsServiceProtocol, @unchecked Sendable {
    var summaryToReturn: StatisticsSummary?
    var errorToThrow: Error?
    var summaryCalledWithDays: Int?
    var summaryCalled = false

    func getSummary(days: Int? = nil) async throws -> StatisticsSummary {
        summaryCalled = true
        summaryCalledWithDays = days
        if let error = errorToThrow { throw error }
        guard let summary = summaryToReturn else {
            throw NetworkError.notFound
        }
        return summary
    }
}
