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

    func update(id: UUID, _ request: UpdatePrintJobRequest) async throws -> PrintJob {
        try await apiClient.put(
            "/api/job-queue/\(id)",
            body: request,
            headers: try await preconditionHeaders(id: id)
        )
    }

    func delete(id: UUID) async throws {
        try await apiClient.delete(
            "/api/job-queue/\(id)",
            headers: try await preconditionHeaders(id: id)
        )
    }

    func cancel(id: UUID) async throws {
        try await apiClient.postVoid(
            "/api/job-queue/\(id)/cancel",
            headers: try await preconditionHeaders(id: id)
        )
    }

    func dispatch(id: UUID) async throws {
        try await apiClient.postVoid(
            "/api/job-queue/\(id)/dispatch",
            headers: try await preconditionHeaders(id: id)
        )
    }

    func abort(id: UUID) async throws {
        try await apiClient.postVoid(
            "/api/job-queue/\(id)/abort-print",
            headers: try await preconditionHeaders(id: id)
        )
    }

    func pause(id: UUID) async throws {
        try await apiClient.postVoid(
            "/api/job-queue-analytics/jobs/\(id)/pause",
            headers: try await preconditionHeaders(id: id)
        )
    }

    func resume(id: UUID) async throws {
        try await apiClient.postVoid(
            "/api/job-queue-analytics/jobs/\(id)/resume",
            headers: try await preconditionHeaders(id: id)
        )
    }

    private func preconditionHeaders(id: UUID) async throws -> [String: String] {
        let job: PrintJob = try await apiClient.get("/api/job-queue/\(id)")
        guard let rowVersion = job.rowVersion, !rowVersion.isEmpty else {
            throw NetworkError.invalidResponse
        }

        return ["If-Match": "\"\(rowVersion)\""]
    }
}
