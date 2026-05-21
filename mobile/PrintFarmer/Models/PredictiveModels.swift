import Foundation

// MARK: - Prediction Request

struct PredictionRequest: Encodable, Sendable {
    let printerId: UUID
    let material: String?
    let estimatedDurationSeconds: Int?
}

// MARK: - Job Failure Prediction

struct JobFailurePrediction: Codable, Sendable {
    let printerId: UUID?
    let material: String?
    let estimatedDurationMinutes: Double?
    let predictedFailureLikelihood: Double?
    let riskLevel: String
    let factors: [PredictionFactor]

    init(printerId: UUID? = nil, material: String? = nil, estimatedDurationMinutes: Double? = nil,
         predictedFailureLikelihood: Double? = nil,
         riskLevel: String, factors: [PredictionFactor] = []) {
        self.printerId = printerId
        self.material = material
        self.estimatedDurationMinutes = estimatedDurationMinutes
        self.predictedFailureLikelihood = predictedFailureLikelihood
        self.riskLevel = riskLevel
        self.factors = factors
    }

    init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        printerId = try c.decodeIfPresent(UUID.self, forKey: .printerId)
        material = try c.decodeIfPresent(String.self, forKey: .material)
        estimatedDurationMinutes = try c.decodeIfPresent(Double.self, forKey: .estimatedDurationMinutes)
        predictedFailureLikelihood = try c.decodeIfPresent(Double.self, forKey: .predictedFailureLikelihood)
        riskLevel = try c.decodeIfPresent(String.self, forKey: .riskLevel) ?? "Unknown"
        factors = try c.decodeIfPresent([PredictionFactor].self, forKey: .factors) ?? []
    }
}

// MARK: - Prediction Factor

struct PredictionFactor: Codable, Sendable {
    let name: String
    let value: Double
    let weight: Double

    init(name: String, value: Double, weight: Double) {
        self.name = name
        self.value = value
        self.weight = weight
    }

    init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        name = try c.decodeIfPresent(String.self, forKey: .name) ?? "Unknown"
        value = try c.decodeIfPresent(Double.self, forKey: .value) ?? 0
        weight = try c.decodeIfPresent(Double.self, forKey: .weight) ?? 0
    }
}

// MARK: - Maintenance Forecast

struct MaintenanceForecast: Codable, Sendable {
    let printerId: UUID?
    let printerName: String
    let upcomingTasks: [ForecastTask]

    init(printerId: UUID? = nil, printerName: String, upcomingTasks: [ForecastTask] = []) {
        self.printerId = printerId
        self.printerName = printerName
        self.upcomingTasks = upcomingTasks
    }

    init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        printerId = try c.decodeIfPresent(UUID.self, forKey: .printerId)
        printerName = try c.decodeIfPresent(String.self, forKey: .printerName) ?? "Unknown Printer"
        upcomingTasks = try c.decodeIfPresent([ForecastTask].self, forKey: .upcomingTasks) ?? []
    }
}

// MARK: - Forecast Task

struct ForecastTask: Codable, Sendable {
    let taskName: String
    let estimatedDaysUntilDue: Int
    let priority: String

    init(taskName: String, estimatedDaysUntilDue: Int, priority: String) {
        self.taskName = taskName
        self.estimatedDaysUntilDue = estimatedDaysUntilDue
        self.priority = priority
    }

    init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        taskName = try c.decodeIfPresent(String.self, forKey: .taskName) ?? "Unknown"
        estimatedDaysUntilDue = try c.decodeIfPresent(Int.self, forKey: .estimatedDaysUntilDue) ?? 0
        priority = try c.decodeIfPresent(String.self, forKey: .priority) ?? "Low"
    }
}

// MARK: - Predictive Alert

struct PredictiveAlert: Codable, Sendable {
    let alertType: String
    let severity: String
    let message: String
    let recommendedAction: String

    init(alertType: String, severity: String, message: String, recommendedAction: String) {
        self.alertType = alertType
        self.severity = severity
        self.message = message
        self.recommendedAction = recommendedAction
    }

    init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        alertType = try c.decodeIfPresent(String.self, forKey: .alertType) ?? "Unknown"
        severity = try c.decodeIfPresent(String.self, forKey: .severity) ?? "Info"
        message = try c.decodeIfPresent(String.self, forKey: .message) ?? ""
        recommendedAction = try c.decodeIfPresent(String.self, forKey: .recommendedAction) ?? ""
    }
}
