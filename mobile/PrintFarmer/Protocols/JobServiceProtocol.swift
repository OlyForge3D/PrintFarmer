import Foundation

enum QueuePositionNeighbor: Sendable, Equatable {
    case before(id: UUID, rowVersion: String)
    case after(id: UUID, rowVersion: String)
}

struct MoveQueuedJobRequest: Encodable, Sendable {
    let neighbor: QueuePositionNeighbor

    private enum CodingKeys: String, CodingKey {
        case beforeJobId
        case beforeJobETag
        case afterJobId
        case afterJobETag
    }

    func encode(to encoder: Encoder) throws {
        var container = encoder.container(keyedBy: CodingKeys.self)
        switch neighbor {
        case .before(let id, let rowVersion):
            try container.encode(id, forKey: .beforeJobId)
            try container.encode(rowVersion, forKey: .beforeJobETag)
        case .after(let id, let rowVersion):
            try container.encode(id, forKey: .afterJobId)
            try container.encode(rowVersion, forKey: .afterJobETag)
        }
    }
}

struct MoveQueuedJobResponse: Decodable, Sendable, Equatable {
    let id: String
    let rowVersion: String?
}

// MARK: - Job Service Protocol

protocol JobServiceProtocol: Sendable {
    func list() async throws -> [QueueOverview]
    func listAllJobs() async throws -> [QueuedPrintJobResponse]
    func moveQueuedJob(
        id: UUID,
        reviewedRowVersion: String,
        neighbor: QueuePositionNeighbor
    ) async throws -> MoveQueuedJobResponse
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
    // on-device — the backend is the sole authority.
    func getCandidates(jobId: UUID) async throws -> [DispatchCandidate]
    func dispatchTo(jobId: UUID, printerId: UUID) async throws
}
