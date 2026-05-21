import Foundation

// MARK: - Failure Detection Monitor Status

struct FailureDetectionMonitorStatus: Codable, Sendable {
    let monitoringEnabled: Bool
    let confidenceThreshold: Double
    let scanIntervalSeconds: Int
    let autoPauseOnFailure: Bool
    let configuredPrinterCount: Int
    let activelyMonitoredPrinterCount: Int
    let lastAnalyzedPrinterCount: Int
    let lastFailureCount: Int
    let lastScanStartedAt: String?
    let lastScanCompletedAt: String?
    let lastError: String?
    let printers: [FailureDetectionPrinterStatus]

    init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        monitoringEnabled = try c.decodeIfPresent(Bool.self, forKey: .monitoringEnabled) ?? false
        confidenceThreshold = try c.decodeIfPresent(Double.self, forKey: .confidenceThreshold) ?? 0.6
        scanIntervalSeconds = try c.decodeIfPresent(Int.self, forKey: .scanIntervalSeconds) ?? 30
        autoPauseOnFailure = try c.decodeIfPresent(Bool.self, forKey: .autoPauseOnFailure) ?? false
        configuredPrinterCount = try c.decodeIfPresent(Int.self, forKey: .configuredPrinterCount) ?? 0
        activelyMonitoredPrinterCount = try c.decodeIfPresent(Int.self, forKey: .activelyMonitoredPrinterCount) ?? 0
        lastAnalyzedPrinterCount = try c.decodeIfPresent(Int.self, forKey: .lastAnalyzedPrinterCount) ?? 0
        lastFailureCount = try c.decodeIfPresent(Int.self, forKey: .lastFailureCount) ?? 0
        lastScanStartedAt = try c.decodeIfPresent(String.self, forKey: .lastScanStartedAt)
        lastScanCompletedAt = try c.decodeIfPresent(String.self, forKey: .lastScanCompletedAt)
        lastError = try c.decodeIfPresent(String.self, forKey: .lastError)
        printers = try c.decodeIfPresent([FailureDetectionPrinterStatus].self, forKey: .printers) ?? []
    }
}

// MARK: - Per-Printer Failure Detection Status

struct FailureDetectionPrinterStatus: Codable, Sendable {
    let printerId: UUID
    let printerName: String
    let state: String
    let reason: String
    let isPrinting: Bool
    let detectionSource: String
    let detectionTarget: String?
    let snapshotUrl: String?
    let lastAnalyzedAt: String?
    let lastOutcome: String
    let lastConfidence: Double?
    let lastAutoPaused: Bool?
    let lastFailureDetectedAt: String?

    init(
        printerId: UUID, printerName: String, state: String, reason: String,
        isPrinting: Bool, detectionSource: String, detectionTarget: String? = nil,
        snapshotUrl: String? = nil, lastAnalyzedAt: String? = nil,
        lastOutcome: String = "none", lastConfidence: Double? = nil,
        lastAutoPaused: Bool? = nil, lastFailureDetectedAt: String? = nil
    ) {
        self.printerId = printerId
        self.printerName = printerName
        self.state = state
        self.reason = reason
        self.isPrinting = isPrinting
        self.detectionSource = detectionSource
        self.detectionTarget = detectionTarget
        self.snapshotUrl = snapshotUrl
        self.lastAnalyzedAt = lastAnalyzedAt
        self.lastOutcome = lastOutcome
        self.lastConfidence = lastConfidence
        self.lastAutoPaused = lastAutoPaused
        self.lastFailureDetectedAt = lastFailureDetectedAt
    }

    init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        printerId = try c.decode(UUID.self, forKey: .printerId)
        printerName = try c.decodeIfPresent(String.self, forKey: .printerName) ?? ""
        state = try c.decodeIfPresent(String.self, forKey: .state) ?? "disabled"
        reason = try c.decodeIfPresent(String.self, forKey: .reason) ?? ""
        isPrinting = try c.decodeIfPresent(Bool.self, forKey: .isPrinting) ?? false
        detectionSource = try c.decodeIfPresent(String.self, forKey: .detectionSource) ?? "none"
        detectionTarget = try c.decodeIfPresent(String.self, forKey: .detectionTarget)
        snapshotUrl = try c.decodeIfPresent(String.self, forKey: .snapshotUrl)
        lastAnalyzedAt = try c.decodeIfPresent(String.self, forKey: .lastAnalyzedAt)
        lastOutcome = try c.decodeIfPresent(String.self, forKey: .lastOutcome) ?? "none"
        lastConfidence = try c.decodeIfPresent(Double.self, forKey: .lastConfidence)
        lastAutoPaused = try c.decodeIfPresent(Bool.self, forKey: .lastAutoPaused)
        lastFailureDetectedAt = try c.decodeIfPresent(String.self, forKey: .lastFailureDetectedAt)
    }
}
