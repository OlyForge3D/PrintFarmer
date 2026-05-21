import Foundation

// MARK: - AutoDispatch Service

actor AutoDispatchService: AutoDispatchServiceProtocol {
    private let apiClient: APIClient

    init(apiClient: APIClient) {
        self.apiClient = apiClient
    }

    func getAllStatus() async throws -> AutoDispatchGlobalStatus {
        try await apiClient.get("/api/auto-dispatch/status")
    }

    func getStatus(printerId: UUID) async throws -> AutoDispatchStatus {
        try await apiClient.get("/api/auto-dispatch/\(printerId)/status")
    }

    func markReady(printerId: UUID) async throws -> AutoDispatchReadyResult {
        try await apiClient.post("/api/auto-dispatch/\(printerId)/ready")
    }

    func skip(printerId: UUID) async throws -> AutoDispatchStatus {
        try await apiClient.post("/api/auto-dispatch/\(printerId)/skip")
    }

    func cancel(printerId: UUID) async throws -> AutoDispatchStatus {
        try await apiClient.post("/api/auto-dispatch/\(printerId)/cancel")
    }

    func preClear(printerId: UUID) async throws -> AutoDispatchStatus {
        try await apiClient.post("/api/auto-dispatch/\(printerId)/pre-clear")
    }

    func setEnabled(printerId: UUID, request: SetAutoDispatchEnabledRequest) async throws -> AutoDispatchStatus {
        try await apiClient.put("/api/auto-dispatch/\(printerId)/enabled", body: request)
    }
}
