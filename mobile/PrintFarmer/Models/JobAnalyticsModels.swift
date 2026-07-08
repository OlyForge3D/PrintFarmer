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
    let completionPercentage: Double?
    let materialCostUsd: Decimal?
    let totalCostUsd: Decimal?
    let costIsEstimated: Bool?
    let materialType: String?
    let filamentName: String?
    let filamentColor: String?
    let actualFilamentUsageGrams: Double?
    let estimatedFilamentUsageGrams: Double?
    let actualCost: Decimal?
    let failureReason: String?
    let toolheadUsages: [QueueHistoryToolheadUsage]?
    let tags: [QueueHistoryTag]?
    let startedAt: Date?
    let deadlineAt: Date?

    init(
        id: String,
        jobName: String,
        printerName: String?,
        status: String,
        completedAt: Date?,
        durationSeconds: Int?,
        completionPercentage: Double? = nil,
        materialCostUsd: Decimal? = nil,
        totalCostUsd: Decimal? = nil,
        costIsEstimated: Bool? = nil,
        materialType: String? = nil,
        filamentName: String? = nil,
        filamentColor: String? = nil,
        actualFilamentUsageGrams: Double? = nil,
        estimatedFilamentUsageGrams: Double? = nil,
        actualCost: Decimal? = nil,
        failureReason: String? = nil,
        toolheadUsages: [QueueHistoryToolheadUsage]? = nil,
        tags: [QueueHistoryTag]? = nil,
        startedAt: Date? = nil,
        deadlineAt: Date? = nil
    ) {
        self.id = id
        self.jobName = jobName
        self.printerName = printerName
        self.status = status
        self.completedAt = completedAt
        self.durationSeconds = durationSeconds
        self.completionPercentage = completionPercentage
        self.materialCostUsd = materialCostUsd
        self.totalCostUsd = totalCostUsd
        self.costIsEstimated = costIsEstimated
        self.materialType = materialType
        self.filamentName = filamentName
        self.filamentColor = filamentColor
        self.actualFilamentUsageGrams = actualFilamentUsageGrams
        self.estimatedFilamentUsageGrams = estimatedFilamentUsageGrams
        self.actualCost = actualCost
        self.failureReason = failureReason
        self.toolheadUsages = toolheadUsages
        self.tags = tags
        self.startedAt = startedAt
        self.deadlineAt = deadlineAt
    }

    enum CodingKeys: String, CodingKey {
        case id, jobName, printerName, status, completionPercentage
        case materialCostUsd, totalCostUsd, costIsEstimated
        case materialType, filamentName, filamentColor
        case actualFilamentUsageGrams, estimatedFilamentUsageGrams
        case actualCost, failureReason, toolheadUsages, tags
        case startedAt = "startedAtUtc"
        case deadlineAt = "deadlineAtUtc"
        case completedAt = "completedAtUtc"
        case durationSeconds = "actualPrintTimeSeconds"
    }
}

extension QueueHistoryEntry {
    var shouldShowPartialCompletionBadge: Bool {
        let normalizedStatus = status.lowercased()
        guard normalizedStatus == "failed" || normalizedStatus == "cancelled",
              let completionPercentage,
              completionPercentage > 0,
              completionPercentage < 100 else {
            return false
        }
        return true
    }

    var statusBadgeText: String {
        let baseText: String
        switch status.lowercased() {
        case "completed":
            baseText = "Completed"
        case "failed":
            baseText = "Failed"
        case "cancelled":
            baseText = "Cancelled"
        default:
            baseText = status.capitalized
        }

        guard shouldShowPartialCompletionBadge, let completionPercentage else {
            return baseText
        }
        return "\(baseText) @ \(Int(completionPercentage.rounded()))%"
    }

    var displayMaterialCostUsd: Decimal? {
        if let toolheadUsages, !toolheadUsages.isEmpty {
            let total = toolheadUsages.compactMap(\.materialCostUsd).reduce(Decimal(0), +)
            if total > 0 { return total }
        }

        if let materialCostUsd, materialCostUsd > 0 { return materialCostUsd }
        if let totalCostUsd, totalCostUsd > 0 { return totalCostUsd }
        return nil
    }

    var displayFilamentUsageGrams: Double? {
        if let toolheadUsages, !toolheadUsages.isEmpty {
            let total = toolheadUsages.reduce(0) { sum, usage in
                if let actualGrams = usage.filamentUsageGrams, actualGrams > 0 {
                    return sum + actualGrams
                }
                return sum + (usage.slicerEstimateGrams ?? 0)
            }
            if total > 0 { return total }
        }

        if let actualFilamentUsageGrams, actualFilamentUsageGrams > 0 {
            return actualFilamentUsageGrams
        }

        if let estimatedFilamentUsageGrams, estimatedFilamentUsageGrams > 0 {
            return estimatedFilamentUsageGrams
        }

        return nil
    }

    var displayFilamentUsageIsEstimated: Bool {
        if let toolheadUsages, !toolheadUsages.isEmpty {
            let actualTotal = toolheadUsages.reduce(0) { sum, usage in
                guard let actualGrams = usage.filamentUsageGrams, actualGrams > 0 else { return sum }
                return sum + actualGrams
            }
            let displayedTotal = toolheadUsages.reduce(0) { sum, usage in
                if let actualGrams = usage.filamentUsageGrams, actualGrams > 0 {
                    return sum + actualGrams
                }
                return sum + (usage.slicerEstimateGrams ?? 0)
            }
            if displayedTotal > 0 {
                return actualTotal <= 0
            }
        }

        return (actualFilamentUsageGrams ?? 0) <= 0 && (estimatedFilamentUsageGrams ?? 0) > 0
    }
}

// MARK: - Queue History Toolhead Usage

struct QueueHistoryToolheadUsage: Codable, Sendable {
    let id: String?
    let printJobId: String?
    let toolheadIndex: Int?
    let spoolmanSpoolId: Int?
    let filamentUsageGrams: Double?
    let slicerEstimateGrams: Double?
    let filamentName: String?
    let filamentColor: String?
    let materialCostUsd: Decimal?
}

// MARK: - Queue History Tag

struct QueueHistoryTag: Codable, Sendable {
    let id: String?
    let name: String?
    let category: String?
    let isAutoGenerated: Bool?
    let color: String?
    let description: String?
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
