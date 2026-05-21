import Foundation

protocol FailureDetectionServiceProtocol: Sendable {
    func getStatus() async throws -> FailureDetectionMonitorStatus
}
