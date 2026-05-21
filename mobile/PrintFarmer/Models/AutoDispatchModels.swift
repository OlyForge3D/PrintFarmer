import Foundation

// MARK: - AutoDispatch Status

struct AutoDispatchStatus: Codable, Sendable {
    let printerId: UUID
    var printerName: String = ""
    var enabled: Bool
    var isReady: Bool = false
    var currentJobName: String?
    var queueDepth: Int
    var readyGateChecks: [ReadyGateCheck] = []
    var lastActivity: String?
    var state: String
    var bedPreConfirmed: Bool = false
    var attentionMessage: String?
}

// MARK: - Ready Gate Check

struct ReadyGateCheck: Codable, Sendable, Identifiable {
    var id: String { name }
    let name: String
    let passed: Bool
    let message: String
    let checkedAt: String
}

// MARK: - AutoDispatch Global Status

struct AutoDispatchGlobalStatus: Codable, Sendable {
    let globalEnabled: Bool
    let printers: [AutoDispatchStatus]
}

// MARK: - AutoDispatch Ready Result

struct AutoDispatchReadyResult: Codable, Sendable {
    let status: AutoDispatchStatus
    let nextJob: AutoDispatchNextJob?
    let filamentCheck: FilamentCheckResult?
}

// MARK: - AutoDispatch Next Job

struct AutoDispatchNextJob: Codable, Sendable, Identifiable {
    let id: UUID
    let name: String
    let estimatedFilamentUsageG: Double?
    let requiredMaterialType: String?
    let estimatedPrintTime: TimeInterval?
}

// MARK: - Filament Check Result

struct FilamentCheckResult: Codable, Sendable {
    let sufficient: Bool
    let remainingWeightG: Double?
    let requiredWeightG: Double?
    let loadedMaterial: String?
    let requiredMaterial: String?
    let materialMismatch: Bool
    let message: String?
}

// MARK: - Request Models

struct SetAutoDispatchEnabledRequest: Encodable, Sendable {
    let enabled: Bool
}
