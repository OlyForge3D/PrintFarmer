import Foundation

// MARK: - Queued Job With Meta

struct QueuedJobWithMeta: Codable, Sendable {
    let job: QueuedJobAnalytics
    let gcodeFile: GcodeFileMeta?
    let assignedPrinter: PrinterMeta?
    let estimatedStartTime: Date?
    let estimatedCompletionTime: Date?
}

// MARK: - Queued Job Analytics

struct QueuedJobAnalytics: Codable, Sendable, Identifiable {
    let id: String
    let name: String
    let status: String
    let priority: Int
    let queuePosition: Int
    let assignedPrinterId: String?
    let printerName: String?
    let printerModel: String?
    let fileName: String?
    let thumbnailUrl: String?
    let createdAt: Date
    let startedAt: Date?
    let completedAt: Date?

    enum CodingKeys: String, CodingKey {
        case id, name, status, priority, queuePosition
        case assignedPrinterId, printerName, printerModel, fileName
        case thumbnailUrl
        case createdAt = "createdAtUtc"
        case startedAt = "actualStartTimeUtc"
        case completedAt = "actualEndTimeUtc"
    }
}

// MARK: - GCode File Meta

struct GcodeFileMeta: Codable, Sendable, Identifiable {
    let id: String
    let fileName: String
    let materialType: String?
    let nozzleDiameter: Double?
    let thumbnailUrl: String?
}

// MARK: - Printer Meta

struct PrinterMeta: Codable, Sendable, Identifiable {
    let id: String
    let name: String
    let model: String?

    enum CodingKeys: String, CodingKey {
        case id, name
        case model = "modelName"
    }
}

// MARK: - Queue Stats

struct QueueStats: Codable, Sendable {
    let totalQueued: Int
    let totalPrinting: Int
    let totalPaused: Int
    let averageWaitTimeMinutes: Int
    let byModel: [QueuePrinterModelStats]
}

// MARK: - Queue Printer Model Stats

struct QueuePrinterModelStats: Codable, Sendable {
    let modelName: String
    let totalQueued: Int
    let currentlyPrinting: Int
    let oldestQueuedAtUtc: Date?
    let averageQueueWaitMinutes: Int
}

// MARK: - Queue History Page

struct QueueHistoryPage: Codable, Sendable {
    let entries: [QueueHistoryEntry]
    let totalCount: Int
    let currentPage: Int
    let pageSize: Int
    let stats: QueueHistoryStats?
}

// MARK: - Queue History Entry

struct QueueHistoryEntry: Codable, Sendable, Identifiable {
    let id: String
    let jobName: String
    let printerName: String?
    let status: String
    let completedAt: Date?
    let durationSeconds: Int?

    enum CodingKeys: String, CodingKey {
        case id, jobName, printerName, status
        case completedAt = "completedAtUtc"
        case durationSeconds = "actualPrintTimeSeconds"
    }
}

// MARK: - Queue History Stats

struct QueueHistoryStats: Codable, Sendable {
    let totalCompleted: Int
    let totalFailed: Int
    let averageDurationMinutes: Int?
}

// MARK: - Timeline Event

struct TimelineEvent: Codable, Sendable {
    let jobId: String
    let jobName: String
    let printerName: String
    let state: String
    let enteredAtUtc: Date
    let exitedAtUtc: Date?
    let durationSeconds: Int?
    let estimatedDurationSeconds: Int?
    let variancePercent: Double?
}

// MARK: - Job State History

struct JobStateHistory: Codable, Sendable {
    let jobId: String
    let jobName: String
    let transitions: [StateTransition]
    let totalDurationSeconds: Int?
    let estimatedDurationSeconds: Int?
    let variancePercent: Double?
}

// MARK: - State Transition

struct StateTransition: Codable, Sendable {
    let state: String
    let enteredAt: Date
    let exitedAt: Date?
    let durationSeconds: Int?
}

// MARK: - Duration Analytics

struct DurationAnalytics: Codable, Sendable {
    let totalJobs: Int
    let averageEstimatedSeconds: Double
    let averageActualSeconds: Double
    let overallAccuracyPercent: Double
    let overallVariancePercent: Double
}
