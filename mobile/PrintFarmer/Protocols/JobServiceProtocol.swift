import Foundation

// MARK: - Job Service Protocol

protocol JobServiceProtocol: Sendable {
    func list() async throws -> [QueueOverview]
    func listAllJobs() async throws -> [QueuedPrintJobResponse]
    func get(id: UUID) async throws -> PrintJob
    func create(_ request: CreatePrintJobRequest) async throws -> PrintJob
    func update(
        id: UUID,
        _ request: UpdatePrintJobRequest,
        reviewedRowVersion: String
    ) async throws -> PrintJob
    func delete(id: UUID, reviewedRowVersion: String) async throws
    func dispatch(
        id: UUID,
        reviewedRowVersion: String
    ) async throws -> JobDispatchResult
    func cancel(id: UUID, reviewedRowVersion: String) async throws
    func abort(id: UUID, reviewedRowVersion: String) async throws
    func pause(id: UUID, reviewedRowVersion: String) async throws
    func resume(id: UUID, reviewedRowVersion: String) async throws
    func acknowledgeBedClearAndStart(
        job: PrintJob,
        printerId: UUID,
        dispatchStateETag: String,
        idempotencyKey: String
    ) async throws -> AcknowledgeBedClearResponse
}
