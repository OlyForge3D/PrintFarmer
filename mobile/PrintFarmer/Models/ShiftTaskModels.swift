import Foundation

enum ShiftTaskType: Codable, Hashable, Sendable {
    case none
    case profileImport
    case maintenanceDue
    case firmwareUpdate
    case calibrationNeeded
    case custom
    case failureClear
    case harvestReady
    case filamentRunout
    case maintenanceInIdleWindow
    case spoolRestock
    case printedPartRestock
    case unknown(String)

    init(from decoder: Decoder) throws {
        let value = try decoder.singleValueContainer().decode(String.self)
        self = switch value {
        case "None": .none
        case "ProfileImport": .profileImport
        case "MaintenanceDue": .maintenanceDue
        case "FirmwareUpdate": .firmwareUpdate
        case "CalibrationNeeded": .calibrationNeeded
        case "Custom": .custom
        case "FailureClear": .failureClear
        case "HarvestReady": .harvestReady
        case "FilamentRunout": .filamentRunout
        case "MaintenanceInIdleWindow": .maintenanceInIdleWindow
        case "SpoolRestock": .spoolRestock
        case "PrintedPartRestock": .printedPartRestock
        default: .unknown(value)
        }
    }

    func encode(to encoder: Encoder) throws {
        var container = encoder.singleValueContainer()
        try container.encode(wireValue)
    }

    var wireValue: String {
        switch self {
        case .none: "None"
        case .profileImport: "ProfileImport"
        case .maintenanceDue: "MaintenanceDue"
        case .firmwareUpdate: "FirmwareUpdate"
        case .calibrationNeeded: "CalibrationNeeded"
        case .custom: "Custom"
        case .failureClear: "FailureClear"
        case .harvestReady: "HarvestReady"
        case .filamentRunout: "FilamentRunout"
        case .maintenanceInIdleWindow: "MaintenanceInIdleWindow"
        case .spoolRestock: "SpoolRestock"
        case .printedPartRestock: "PrintedPartRestock"
        case .unknown(let value): value
        }
    }

    var displayName: String {
        switch self {
        case .none: "Task"
        case .profileImport: "Profile import"
        case .maintenanceDue: "Maintenance"
        case .firmwareUpdate: "Firmware update"
        case .calibrationNeeded: "Calibration"
        case .custom: "Custom task"
        case .failureClear: "Clear failure"
        case .harvestReady: "Harvest"
        case .filamentRunout: "Filament runout"
        case .maintenanceInIdleWindow: "Maintenance window"
        case .spoolRestock: "Spool restock"
        case .printedPartRestock: "Part restock"
        case .unknown: "Task"
        }
    }

    var supportsKnownLifecycleActions: Bool {
        if case .unknown = self { return false }
        return true
    }
}

enum ShiftTaskStatus: Codable, Hashable, Sendable {
    case pending
    case inProgress
    case completed
    case dismissed
    case skipped
    case unknown(String)

    init(from decoder: Decoder) throws {
        let value = try decoder.singleValueContainer().decode(String.self)
        self = switch value {
        case "Pending": .pending
        case "InProgress": .inProgress
        case "Completed": .completed
        case "Dismissed": .dismissed
        case "Skipped": .skipped
        default: .unknown(value)
        }
    }

    func encode(to encoder: Encoder) throws {
        var container = encoder.singleValueContainer()
        try container.encode(wireValue)
    }

    var wireValue: String {
        switch self {
        case .pending: "Pending"
        case .inProgress: "InProgress"
        case .completed: "Completed"
        case .dismissed: "Dismissed"
        case .skipped: "Skipped"
        case .unknown(let value): value
        }
    }
}

enum ShiftTaskPriority: Codable, Hashable, Sendable {
    case low
    case normal
    case high
    case unknown(String)

    init(from decoder: Decoder) throws {
        let value = try decoder.singleValueContainer().decode(String.self)
        self = switch value {
        case "Low": .low
        case "Normal": .normal
        case "High": .high
        default: .unknown(value)
        }
    }

    func encode(to encoder: Encoder) throws {
        var container = encoder.singleValueContainer()
        try container.encode(wireValue)
    }

    var wireValue: String {
        switch self {
        case .low: "Low"
        case .normal: "Normal"
        case .high: "High"
        case .unknown(let value): value
        }
    }

    var displayName: String {
        switch self {
        case .low: "Low priority"
        case .normal: "Normal priority"
        case .high: "High priority"
        case .unknown: "Priority unavailable"
        }
    }
}

enum ShiftTaskAnchorKind: Codable, Hashable, Sendable {
    case unspecified
    case now
    case at
    case window
    case anytimeToday
    case timeline
    case unknown(String)

    init(from decoder: Decoder) throws {
        let value = try decoder.singleValueContainer().decode(String.self)
        self = switch value {
        case "unspecified": .unspecified
        case "now": .now
        case "at": .at
        case "window": .window
        case "anytimeToday": .anytimeToday
        case "timeline": .timeline
        default: .unknown(value)
        }
    }

    func encode(to encoder: Encoder) throws {
        var container = encoder.singleValueContainer()
        try container.encode(wireValue)
    }

    var wireValue: String {
        switch self {
        case .unspecified: "unspecified"
        case .now: "now"
        case .at: "at"
        case .window: "window"
        case .anytimeToday: "anytimeToday"
        case .timeline: "timeline"
        case .unknown(let value): value
        }
    }

    var groupTitle: String {
        switch self {
        case .now: "Now"
        case .at, .window, .timeline: "Timeline"
        case .unspecified, .anytimeToday: "Anytime Today"
        case .unknown: "Other Tasks"
        }
    }
}

enum ShiftTaskSourceKind: Codable, Hashable, Sendable {
    case unspecified
    case attention
    case failureIncident
    case harvest
    case filamentCoverage
    case maintenance
    case spoolReorder
    case printedPartStock
    case unknown(String)

    init(from decoder: Decoder) throws {
        let value = try decoder.singleValueContainer().decode(String.self)
        self = switch value {
        case "unspecified": .unspecified
        case "attention": .attention
        case "failureIncident": .failureIncident
        case "harvest": .harvest
        case "filamentCoverage": .filamentCoverage
        case "maintenance": .maintenance
        case "spoolReorder": .spoolReorder
        case "printedPartStock": .printedPartStock
        default: .unknown(value)
        }
    }

    func encode(to encoder: Encoder) throws {
        var container = encoder.singleValueContainer()
        try container.encode(wireValue)
    }

    var wireValue: String {
        switch self {
        case .unspecified: "unspecified"
        case .attention: "attention"
        case .failureIncident: "failureIncident"
        case .harvest: "harvest"
        case .filamentCoverage: "filamentCoverage"
        case .maintenance: "maintenance"
        case .spoolReorder: "spoolReorder"
        case .printedPartStock: "printedPartStock"
        case .unknown(let value): value
        }
    }

    var displayName: String {
        switch self {
        case .unspecified: "Manual"
        case .attention: "Attention"
        case .failureIncident: "Failure detection"
        case .harvest: "Harvest"
        case .filamentCoverage: "Filament coverage"
        case .maintenance: "Maintenance"
        case .spoolReorder: "Spool inventory"
        case .printedPartStock: "Parts inventory"
        case .unknown: "Other source"
        }
    }
}

struct ShiftTask: Codable, Hashable, Sendable, Identifiable {
    let id: String
    let taskType: ShiftTaskType
    let entityType: String
    let entityId: String
    let title: String
    let description: String?
    let status: ShiftTaskStatus
    let priority: ShiftTaskPriority
    let createdAt: Date
    let dueAt: Date?
    let completedAt: Date?
    let relatedEntityCount: Int
    let metadataJson: String?
    let anchorKind: ShiftTaskAnchorKind
    let anchorAtUtc: Date?
    let windowStartUtc: Date?
    let windowEndUtc: Date?
    let sourceKind: ShiftTaskSourceKind
    let sourceId: String?
}

struct ShiftTaskGroup: Codable, Hashable, Sendable, Identifiable {
    let anchorKind: ShiftTaskAnchorKind
    let tasks: [ShiftTask]

    var id: String {
        "\(anchorKind.wireValue):\(tasks.first?.id ?? "empty")"
    }
}

struct ShiftPlan: Codable, Hashable, Sendable {
    let groups: [ShiftTaskGroup]
    let generatedAt: Date
}

enum ShiftTaskSnapshotMode: Hashable, Sendable {
    case grouped
    case legacyFallback
    case featureDisabled
}

struct ShiftTaskSnapshot: Hashable, Sendable {
    let groups: [ShiftTaskGroup]
    let generatedAt: Date?
    let mode: ShiftTaskSnapshotMode
    let compatibilityErrorMessage: String?

    init(
        groups: [ShiftTaskGroup],
        generatedAt: Date?,
        mode: ShiftTaskSnapshotMode,
        compatibilityErrorMessage: String? = nil
    ) {
        self.groups = groups
        self.generatedAt = generatedAt
        self.mode = mode
        self.compatibilityErrorMessage = compatibilityErrorMessage
    }

    var taskCount: Int {
        groups.reduce(0) { $0 + $1.tasks.count }
    }

    static func grouped(_ plan: ShiftPlan) -> ShiftTaskSnapshot {
        ShiftTaskSnapshot(groups: plan.groups, generatedAt: plan.generatedAt, mode: .grouped)
    }

    static func flat(_ tasks: [ShiftTask], mode: ShiftTaskSnapshotMode) -> ShiftTaskSnapshot {
        let groups = tasks.isEmpty
            ? []
            : [ShiftTaskGroup(anchorKind: .unspecified, tasks: tasks)]
        return ShiftTaskSnapshot(groups: groups, generatedAt: nil, mode: mode)
    }
}

enum ShiftTaskMutationOperation: String, Hashable, Sendable {
    case complete
    case skip
    case dismiss

    var displayName: String {
        switch self {
        case .complete: "Complete"
        case .skip: "Skip"
        case .dismiss: "Dismiss task"
        }
    }
}

struct ShiftTaskMutationIntent: Hashable, Sendable {
    let taskID: String
    let operation: ShiftTaskMutationOperation
    let idempotencyKey: String?

    static func make(taskID: String, operation: ShiftTaskMutationOperation) -> ShiftTaskMutationIntent {
        ShiftTaskMutationIntent(
            taskID: taskID,
            operation: operation,
            idempotencyKey: operation == .complete ? UUID().uuidString : nil
        )
    }
}

struct ShiftTaskInvalidation: Hashable, Sendable {
    static let supportedTargets = Set(["taskcreated", "taskupdated", "pendingtaskcount"])

    let target: String
}

struct ShiftTaskTimeFormatter {
    let locale: Locale
    let calendar: Calendar
    let timeZone: TimeZone
    private let formatStyle: Date.FormatStyle

    init(
        locale: Locale = .autoupdatingCurrent,
        calendar: Calendar = .autoupdatingCurrent,
        timeZone: TimeZone = .autoupdatingCurrent
    ) {
        self.locale = locale
        self.calendar = calendar
        self.timeZone = timeZone
        self.formatStyle = Date.FormatStyle(
            date: .omitted,
            time: .shortened,
            locale: locale,
            calendar: calendar,
            timeZone: timeZone
        )
    }

    func string(for task: ShiftTask) -> String {
        switch task.anchorKind {
        case .now:
            return "Now"
        case .at:
            return task.anchorAtUtc.map { "At \(time($0))" } ?? "Scheduled"
        case .window:
            guard let start = task.windowStartUtc, let end = task.windowEndUtc else {
                return "Scheduled window"
            }
            return "\(time(start)) to \(time(end))"
        case .anytimeToday, .unspecified:
            return "Anytime today"
        case .timeline:
            return "Timeline"
        case .unknown:
            return "Schedule unavailable"
        }
    }

    private func time(_ date: Date) -> String {
        date.formatted(formatStyle)
    }
}

struct ShiftTaskRowPresentation: Equatable {
    let typeText: String
    let priorityText: String
    let sourceText: String
    let timeText: String
    let accessibilityLabel: String

    init(task: ShiftTask, formatter: ShiftTaskTimeFormatter = ShiftTaskTimeFormatter()) {
        let timeText = formatter.string(for: task)
        self.typeText = task.taskType.displayName
        self.priorityText = task.priority.displayName
        self.sourceText = task.sourceKind.displayName
        self.timeText = timeText
        self.accessibilityLabel = [
            task.title,
            task.description,
            task.priority.displayName,
            timeText,
            task.sourceKind.displayName,
            task.taskType.displayName,
        ]
        .compactMap { $0 }
        .joined(separator: ", ")
    }
}

struct ShiftTaskMutationErrorAccessibility: Equatable {
    let retryLabel: String
    let retryHint: String
    let dismissLabel: String
    let dismissHint: String

    init(task: ShiftTask, operation: ShiftTaskMutationOperation) {
        self.retryLabel = "Retry"
        self.retryHint = "Retries \(operation.displayName) for \(task.title)."
        self.dismissLabel = "Dismiss"
        self.dismissHint = "Dismisses this error without changing the task."
    }
}
