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

    // MARK: - Dispatch (issue #712, F7)
    //
    // Thin clients over the existing job-queue routes. `getCandidates`
    // ranks every printer for a job (`GET /api/job-queue/{id}/candidates`);
    // `dispatchTo` assigns and dispatches the job to a chosen printer
    // (`POST /api/job-queue/{id}/dispatch-to`). No scoring is recomputed
    // on-device — the backend is the sole authority. `dispatch-to` is If-Match
    // protected, so `reviewedRowVersion` (the job's ETag) is mandatory.
    func getCandidates(jobId: UUID) async throws -> [DispatchCandidate]
    func dispatchTo(jobId: UUID, printerId: UUID, reviewedRowVersion: String) async throws
}
