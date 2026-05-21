import Foundation

// MARK: - Predictive Service

actor PredictiveService: PredictiveServiceProtocol {
    private let apiClient: APIClient

    init(apiClient: APIClient) {
        self.apiClient = apiClient
    }

    func predictJobFailure(request: PredictionRequest) async throws -> JobFailurePrediction? {
        try await apiClient.post("/api/predictive-analytics/predict-job-failure", body: request)
    }

    func getMaintenanceForecast(days: Int? = nil, printerId: UUID? = nil) async throws -> [MaintenanceForecast] {
        var query = ""
        var params: [String] = []
        if let days { params.append("days=\(days)") }
        if let printerId { params.append("printerId=\(printerId.uuidString)") }
        if !params.isEmpty { query = "?" + params.joined(separator: "&") }
        let result: [MaintenanceForecast]? = try await apiClient.get("/api/predictive-analytics/maintenance-forecast\(query)")
        return result ?? []
    }

    func getActiveAlerts(printerId: UUID? = nil) async throws -> [PredictiveAlert] {
        var query = ""
        if let printerId { query = "?printerId=\(printerId.uuidString)" }
        let result: [PredictiveAlert]? = try await apiClient.get("/api/predictive-analytics/active-alerts\(query)")
        return result ?? []
    }
}
