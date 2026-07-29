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
    var dispatchStateETag: String?
    var printerETag: String?
    var nextJobId: UUID?
    var nextJobName: String?
    var nextJobETag: String?
    var nextJobKind: String?
    var nextJobPrinterConfigRevision: Int64?
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
    var acknowledgementOutcome: BedClearAcknowledgementOutcome? = nil
}

enum BedClearAcknowledgementOutcome: String, Codable, Sendable {
    case accepted
    case replayed
}

enum BedClearAcknowledgementError: LocalizedError, Sendable {
    case conflict(code: String, detail: String?)
    case stale(code: String, detail: String?)
    case preconditionRequired(code: String, detail: String?)
    case incompatible(code: String, detail: String?)
    case unavailable(code: String, detail: String?)

    var requiresReview: Bool {
        switch self {
        case .stale, .preconditionRequired: return true
        default: return false
        }
    }

    var errorDescription: String? {
        switch self {
        case .conflict(let code, let detail):
            return detail ?? "Bed-clear conflict (\(code)). Review the current job before retrying."
        case .stale(_, let detail):
            return detail ?? "The job changed after review. Refresh and confirm again."
        case .preconditionRequired(_, let detail):
            return detail ?? "A reviewed job revision is required. Refresh and confirm again."
        case .incompatible(let code, let detail):
            return detail ?? "Calibration cannot start (\(code))."
        case .unavailable(let code, let detail):
            return detail ?? "The printer is unavailable (\(code)). Retry after its status is current."
        }
    }
}

// MARK: - AutoDispatch Next Job

struct AutoDispatchNextJob: Codable, Sendable, Identifiable {
    let id: UUID
    let name: String
    let estimatedFilamentUsageG: Double?
    let requiredMaterialType: String?
    let estimatedPrintTime: TimeInterval?
    var jobKind: String = "Standard"
    var jobETag: String?
    var expectedPrinterConfigRevision: Int64?
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
