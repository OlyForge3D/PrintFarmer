import Foundation

actor FailureDetectionService: FailureDetectionServiceProtocol {
    private let apiClient: APIClient

    init(apiClient: APIClient) {
        self.apiClient = apiClient
    }

    func getStatus() async throws -> FailureDetectionMonitorStatus {
        let result: FailureDetectionMonitorStatus? = try await apiClient.get("/api/failure-detection/status")
        guard let result else { throw URLError(.badServerResponse) }
        return result
    }
}
