import Foundation

// MARK: - Job Service Protocol

protocol JobServiceProtocol: Sendable {
    func list() async throws -> [QueueOverview]
    func listAllJobs() async throws -> [QueuedPrintJobResponse]
    func get(id: UUID) async throws -> PrintJob
    func create(_ request: CreatePrintJobRequest) async throws -> PrintJob
    func update(id: UUID, _ request: UpdatePrintJobRequest) async throws -> PrintJob
    func delete(id: UUID) async throws
    func dispatch(id: UUID) async throws
    func cancel(id: UUID) async throws
    func abort(id: UUID) async throws
    func pause(id: UUID) async throws
    func resume(id: UUID) async throws

    // MARK: - Dispatch (issue #712, F7)
    //
    // Thin clients over the existing job-queue routes. `getCandidates`
    // ranks every printer for a job (`GET /api/job-queue/{id}/candidates`);
    // `dispatchTo` assigns and dispatches the job to a chosen printer
    // (`POST /api/job-queue/{id}/dispatch-to`). No scoring is recomputed
    // on-device — the backend is the sole authority.
    func getCandidates(jobId: UUID) async throws -> [DispatchCandidate]
    func dispatchTo(jobId: UUID, printerId: UUID) async throws
}
