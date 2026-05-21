import Foundation

// MARK: - Predictive Service Protocol

protocol PredictiveServiceProtocol: Sendable {
    func predictJobFailure(request: PredictionRequest) async throws -> JobFailurePrediction?
    func getMaintenanceForecast(days: Int?, printerId: UUID?) async throws -> [MaintenanceForecast]
    func getActiveAlerts(printerId: UUID?) async throws -> [PredictiveAlert]
}

extension PredictiveServiceProtocol {
    func getMaintenanceForecast() async throws -> [MaintenanceForecast] {
        try await getMaintenanceForecast(days: nil, printerId: nil)
    }
}
