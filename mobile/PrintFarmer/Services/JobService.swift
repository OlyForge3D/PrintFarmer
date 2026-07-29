import Foundation

// MARK: - Job Service

actor JobService: JobServiceProtocol {
    private let apiClient: APIClient

    init(apiClient: APIClient) {
        self.apiClient = apiClient
    }

    func list() async throws -> [QueueOverview] {
        try await apiClient.get("/api/job-queue")
    }

    func listAllJobs() async throws -> [QueuedPrintJobResponse] {
        try await apiClient.get("/api/job-queue-analytics?limit=200&offset=0")
    }

    func get(id: UUID) async throws -> PrintJob {
        try await apiClient.get("/api/job-queue/\(id)")
    }

    func create(_ request: CreatePrintJobRequest) async throws -> PrintJob {
        try await apiClient.post("/api/job-queue", body: request)
    }

    func update(
        id: UUID,
        _ request: UpdatePrintJobRequest,
        reviewedRowVersion: String
    ) async throws -> PrintJob {
        try await apiClient.put(
            "/api/job-queue/\(id)",
            body: request,
            headers: preconditionHeaders(reviewedRowVersion)
        )
    }

    func delete(id: UUID, reviewedRowVersion: String) async throws {
        try await apiClient.delete(
            "/api/job-queue/\(id)",
            headers: preconditionHeaders(reviewedRowVersion)
        )
    }

    func cancel(id: UUID, reviewedRowVersion: String) async throws {
        try await apiClient.postVoid(
            "/api/job-queue/\(id)/cancel",
            headers: preconditionHeaders(reviewedRowVersion)
        )
    }

    func dispatch(
        id: UUID,
        reviewedRowVersion: String
    ) async throws -> JobDispatchResult {
        let response: HTTPDecodedResponse<DispatchJobResponse> =
            try await apiClient.post(
            "/api/job-queue/\(id)/dispatch",
            headers: preconditionHeaders(reviewedRowVersion),
            accepting: [200, 202, 409]
        )
        guard response.value.dispatchResult != nil else {
            throw NetworkError.invalidResponse
        }
        switch response.statusCode {
        case 200:
            return .accepted(response.value)
        case 202:
            return .reconciliation(response.value)
        default:
            return .rejected(response.value)
        }
    }

    func abort(id: UUID, reviewedRowVersion: String) async throws {
        try await apiClient.postVoid(
            "/api/job-queue/\(id)/abort-print",
            headers: preconditionHeaders(reviewedRowVersion)
        )
    }

    func pause(id: UUID, reviewedRowVersion: String) async throws {
        try await apiClient.postVoid(
            "/api/job-queue-analytics/jobs/\(id)/pause",
            headers: preconditionHeaders(reviewedRowVersion)
        )
    }

    func resume(id: UUID, reviewedRowVersion: String) async throws {
        try await apiClient.postVoid(
            "/api/job-queue-analytics/jobs/\(id)/resume",
            headers: preconditionHeaders(reviewedRowVersion)
        )
    }

    func acknowledgeBedClearAndStart(
        job: PrintJob,
        printerId: UUID,
        dispatchStateETag: String,
        idempotencyKey: String
    ) async throws -> AcknowledgeBedClearResponse {
        guard let rowVersion = job.rowVersion, !rowVersion.isEmpty else {
            throw NetworkError.invalidResponse
        }
        let request = AcknowledgeBedClearRequest(
            printerId: printerId,
            expectedPrinterConfigRevision: job.pinnedPrinterConfigRevision
        )
        return try await apiClient.post(
            "/api/job-queue/\(job.id)/acknowledge-bed-clear-and-start",
            body: request,
            headers: [
                "If-Match": "\"\(rowVersion)\"",
                "X-Dispatch-State-If-Match": "\"\(dispatchStateETag)\"",
                "Idempotency-Key": idempotencyKey
            ]
        )
    }

    private func preconditionHeaders(_ rowVersion: String) -> [String: String] {
        return ["If-Match": "\"\(rowVersion)\""]
    }
}
