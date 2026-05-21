import Foundation
import os

@MainActor @Observable
final class PredictiveViewModel {
    var prediction: JobFailurePrediction?
    var alerts: [PredictiveAlert] = []
    var forecasts: [MaintenanceForecast] = []
    var isLoading = false
    var error: String?
    var isViewActive = true

    private let logger = Logger(subsystem: "com.printfarmer.ios", category: "Predictive")
    private var predictiveService: (any PredictiveServiceProtocol)?

    func configure(predictiveService: any PredictiveServiceProtocol) {
        self.predictiveService = predictiveService
    }

    func predictFailure(printerId: UUID, material: String?, duration: TimeInterval?) async {
        guard let predictiveService, isViewActive else { return }
        isLoading = true
        error = nil

        do {
            let request = PredictionRequest(
                printerId: printerId,
                material: material,
                estimatedDurationSeconds: duration.map { Int($0) }
            )
            let result = try await predictiveService.predictJobFailure(request: request)
            guard isViewActive else { return }
            prediction = result
        } catch {
            guard isViewActive else { return }
            logger.warning("Failed to predict failure: \(error.localizedDescription)")
            prediction = nil
        }

        guard isViewActive else { return }
        isLoading = false
    }

    func loadAlerts(printerId: UUID) async {
        guard let predictiveService, isViewActive else { return }
        do {
            let result = try await predictiveService.getActiveAlerts(printerId: printerId)
            guard isViewActive else { return }
            alerts = result
        } catch {
            logger.warning("Failed to load predictive alerts: \(error.localizedDescription)")
        }
    }

    func loadForecasts(printerId: UUID) async {
        guard let predictiveService, isViewActive else { return }
        do {
            let result = try await predictiveService.getMaintenanceForecast(days: 30, printerId: printerId)
            guard isViewActive else { return }
            forecasts = result
        } catch {
            logger.warning("Failed to load forecasts: \(error.localizedDescription)")
        }
    }

    // MARK: - Computed

    var riskPercentage: Int {
        Int(prediction?.predictedFailureLikelihood ?? 0)
    }

    var riskLevel: String {
        switch riskPercentage {
        case 0..<25: return "Low"
        case 25..<50: return "Moderate"
        case 50..<75: return "High"
        default: return "Critical"
        }
    }
}
