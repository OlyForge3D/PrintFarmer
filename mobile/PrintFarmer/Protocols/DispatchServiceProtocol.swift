import Foundation

// MARK: - Dispatch Service Protocol

protocol DispatchServiceProtocol: Sendable {
    func getQueueStatus() async throws -> DispatchQueueStatus
    func getHistory(page: Int?, pageSize: Int?) async throws -> DispatchHistoryPage
}

extension DispatchServiceProtocol {
    func getHistory() async throws -> DispatchHistoryPage {
        try await getHistory(page: nil, pageSize: nil)
    }
}
