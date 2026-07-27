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
        try await apiClient.post(
            "/api/auto-dispatch/\(printerId)/ready",
            headers: try await preconditionHeaders(printerId: printerId)
        )
    }

    func skip(printerId: UUID) async throws -> AutoDispatchStatus {
        let status = try await getStatus(printerId: printerId)
        guard let dispatchETag = status.dispatchStateETag,
              !dispatchETag.isEmpty,
              let jobETag = status.nextJobETag,
              !jobETag.isEmpty else {
            throw NetworkError.invalidResponse
        }
        let response: AutoDispatchStatus = try await apiClient.post(
            "/api/auto-dispatch/\(printerId)/skip",
            headers: [
                "If-Match": "\"\(dispatchETag)\"",
                "X-Job-If-Match": "\"\(jobETag)\""
            ]
        )
        return response
    }

    func cancel(printerId: UUID) async throws -> AutoDispatchStatus {
        try await apiClient.post(
            "/api/auto-dispatch/\(printerId)/cancel",
            headers: try await preconditionHeaders(printerId: printerId)
        )
    }

    func preClear(printerId: UUID) async throws -> AutoDispatchStatus {
        try await apiClient.post(
            "/api/auto-dispatch/\(printerId)/pre-clear",
            headers: try await preconditionHeaders(printerId: printerId)
        )
    }

    func setEnabled(printerId: UUID, request: SetAutoDispatchEnabledRequest) async throws -> AutoDispatchStatus {
        let status = try await getStatus(printerId: printerId)
        guard let dispatchETag = status.dispatchStateETag,
              !dispatchETag.isEmpty,
              let printerETag = status.printerETag,
              !printerETag.isEmpty else {
            throw NetworkError.invalidResponse
        }
        let response: AutoDispatchStatus = try await apiClient.put(
            "/api/auto-dispatch/\(printerId)/enabled",
            body: request,
            headers: [
                "If-Match": "\"\(dispatchETag)\"",
                "X-Printer-If-Match": "\"\(printerETag)\""
            ]
        )
        return response
    }

    private func preconditionHeaders(printerId: UUID) async throws -> [String: String] {
        let status = try await getStatus(printerId: printerId)
        guard let etag = status.dispatchStateETag, !etag.isEmpty else {
            throw NetworkError.invalidResponse
        }

        return ["If-Match": "\"\(etag)\""]
    }
}
