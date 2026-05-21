import Foundation

// MARK: - Demo Predictive Service

final class DemoPredictiveService: PredictiveServiceProtocol, @unchecked Sendable {

    func predictJobFailure(request: PredictionRequest) async throws -> JobFailurePrediction? {
        JobFailurePrediction(
            printerId: request.printerId,
            material: request.material ?? "PLA",
            estimatedDurationMinutes: Double(request.estimatedDurationSeconds ?? 7200) / 60.0,
            predictedFailureLikelihood: 0.12,
            riskLevel: "Low",
            factors: [
                PredictionFactor(name: "Print duration", value: 0.3, weight: 0.25),
                PredictionFactor(name: "Nozzle wear", value: 0.15, weight: 0.35),
                PredictionFactor(name: "Bed adhesion history", value: 0.08, weight: 0.20),
                PredictionFactor(name: "Ambient temperature", value: 0.05, weight: 0.20),
            ])
    }

    func getMaintenanceForecast(days: Int?, printerId: UUID? = nil) async throws -> [MaintenanceForecast] {
        let allForecasts = [
            MaintenanceForecast(
                printerId: DemoData.voron24_ID,
                printerName: "Voron 2.4",
                upcomingTasks: [
                    ForecastTask(taskName: "Nozzle replacement", estimatedDaysUntilDue: -3, priority: "Critical"),
                    ForecastTask(taskName: "Belt inspection", estimatedDaysUntilDue: 14, priority: "Medium"),
                ]),
            MaintenanceForecast(
                printerId: DemoData.prusaMK4_1_ID,
                printerName: "Prusa MK4 #1",
                upcomingTasks: [
                    ForecastTask(taskName: "Belt tension check", estimatedDaysUntilDue: 5, priority: "Medium"),
                ]),
            MaintenanceForecast(
                printerId: DemoData.bambuX1C_ID,
                printerName: "Bambu X1C",
                upcomingTasks: [
                    ForecastTask(taskName: "Linear rail lubrication", estimatedDaysUntilDue: 7, priority: "Low"),
                ]),
        ]
        if let printerId {
            return allForecasts.filter { $0.printerId == printerId }
        }
        return allForecasts
    }

    func getActiveAlerts(printerId: UUID? = nil) async throws -> [PredictiveAlert] {
        [
            PredictiveAlert(
                alertType: "FailureRisk",
                severity: "Warning",
                message: "Voron 2.4 shows elevated failure risk due to worn nozzle and 620 hours since last maintenance.",
                recommendedAction: "Replace nozzle and run calibration before next long print."),
        ]
    }
}
