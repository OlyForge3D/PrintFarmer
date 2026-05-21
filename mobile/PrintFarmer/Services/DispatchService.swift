import Foundation

// MARK: - Dispatch Service

actor DispatchService: DispatchServiceProtocol {
    private let apiClient: APIClient

    init(apiClient: APIClient) {
        self.apiClient = apiClient
    }

    func getQueueStatus() async throws -> DispatchQueueStatus {
        try await apiClient.get("/api/dispatch/queue-status")
    }

    func getHistory(page: Int? = nil, pageSize: Int? = nil) async throws -> DispatchHistoryPage {
        var params: [String] = []
        if let p = page { params.append("page=\(p)") }
        if let ps = pageSize { params.append("pageSize=\(ps)") }
        let query = params.isEmpty ? "" : "?\(params.joined(separator: "&"))"
        return try await apiClient.get("/api/dispatch/history\(query)")
    }
}
