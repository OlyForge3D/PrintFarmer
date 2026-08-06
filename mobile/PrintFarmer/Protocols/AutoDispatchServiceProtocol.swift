import Foundation

// MARK: - AutoDispatch Service Protocol

protocol AutoDispatchServiceProtocol: Sendable {
    func getAllStatus() async throws -> AutoDispatchGlobalStatus
    func getStatus(printerId: UUID) async throws -> AutoDispatchStatus
    func markReady(status: AutoDispatchStatus) async throws -> AutoDispatchReadyResult
    func confirmFilamentOverride(
        challenge: AutoDispatchReadyResult
    ) async throws -> AutoDispatchReadyResult
    func skip(status: AutoDispatchStatus) async throws -> AutoDispatchStatus
    func cancel(status: AutoDispatchStatus) async throws -> AutoDispatchStatus
    func preClear(status: AutoDispatchStatus) async throws -> AutoDispatchStatus
    func setEnabled(
        status: AutoDispatchStatus,
        request: SetAutoDispatchEnabledRequest
    ) async throws -> AutoDispatchStatus
}
