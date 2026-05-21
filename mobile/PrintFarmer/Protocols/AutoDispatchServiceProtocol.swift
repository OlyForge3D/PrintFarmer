import Foundation

// MARK: - AutoDispatch Service Protocol

protocol AutoDispatchServiceProtocol: Sendable {
    func getAllStatus() async throws -> AutoDispatchGlobalStatus
    func getStatus(printerId: UUID) async throws -> AutoDispatchStatus
    func markReady(printerId: UUID) async throws -> AutoDispatchReadyResult
    func skip(printerId: UUID) async throws -> AutoDispatchStatus
    func cancel(printerId: UUID) async throws -> AutoDispatchStatus
    func preClear(printerId: UUID) async throws -> AutoDispatchStatus
    func setEnabled(printerId: UUID, request: SetAutoDispatchEnabledRequest) async throws -> AutoDispatchStatus
}
