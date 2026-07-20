import Foundation

enum DemoShiftTaskScenario {
    case standard
    case mutationFailureThenSuccess
}

enum DemoShiftTaskError: LocalizedError, Sendable {
    case forcedMutationFailure
    case duplicateRetry

    var errorDescription: String? {
        switch self {
        case .forcedMutationFailure:
            "The demo task action failed."
        case .duplicateRetry:
            "The retry operation was invoked more than once."
        }
    }
}

actor DemoShiftTaskService: ShiftTaskServiceProtocol {
    private let scenario: DemoShiftTaskScenario
    private var tasks: [ShiftTask]
    private var completeAttemptCount = 0

    init(scenario: DemoShiftTaskScenario = .standard) {
        self.scenario = scenario
        self.tasks = scenario == .standard
            ? Self.standardTasks
            : [Self.mutationTask]
    }

    func loadSnapshot(shiftPlanEnabled: Bool) async throws -> ShiftTaskSnapshot {
        guard shiftPlanEnabled else {
            return .flat(tasks, mode: .featureDisabled)
        }

        let groups = [
            ShiftTaskGroup(
                anchorKind: .now,
                tasks: tasks.filter { $0.anchorKind == .now }
            ),
            ShiftTaskGroup(
                anchorKind: .timeline,
                tasks: tasks.filter { $0.anchorKind == .at || $0.anchorKind == .window }
            ),
            ShiftTaskGroup(
                anchorKind: .anytimeToday,
                tasks: tasks.filter {
                    $0.anchorKind == .anytimeToday || $0.anchorKind == .unspecified
                }
            ),
        ]
        .filter { !$0.tasks.isEmpty }

        return ShiftTaskSnapshot(
            groups: groups,
            generatedAt: Date(timeIntervalSince1970: 1_784_553_600),
            mode: .grouped
        )
    }

    func complete(taskID: String, idempotencyKey: String) async throws {
        _ = idempotencyKey
        guard scenario == .mutationFailureThenSuccess else {
            tasks.removeAll { $0.id == taskID }
            return
        }

        completeAttemptCount += 1
        switch completeAttemptCount {
        case 1:
            throw DemoShiftTaskError.forcedMutationFailure
        case 2:
            tasks.removeAll { $0.id == taskID }
        default:
            tasks = [Self.mutationTask]
            throw DemoShiftTaskError.duplicateRetry
        }
    }

    func skip(taskID: String) async throws {
        tasks.removeAll { $0.id == taskID }
    }

    func dismiss(taskID: String) async throws {
        tasks.removeAll { $0.id == taskID }
    }

    private static let mutationTask = ShiftTask(
        id: "78200000-0000-0000-0000-000000000001",
        taskType: .harvestReady,
        entityType: "Job",
        entityId: "30000000-0003-0000-0000-000000000001",
        title: "Harvest completed plate",
        description: "Remove the completed plate from Prusa MK4 #1.",
        status: .pending,
        priority: .high,
        createdAt: Date(timeIntervalSince1970: 1_784_510_400),
        dueAt: nil,
        completedAt: nil,
        relatedEntityCount: 1,
        metadataJson: nil,
        anchorKind: .now,
        anchorAtUtc: nil,
        windowStartUtc: nil,
        windowEndUtc: nil,
        sourceKind: .harvest,
        sourceId: "harvest:demo"
    )

    private static let standardTasks = [
        mutationTask,
        ShiftTask(
            id: "78200000-0000-0000-0000-000000000002",
            taskType: .filamentRunout,
            entityType: "Printer",
            entityId: "10000000-0001-0000-0000-000000000003",
            title: "Prepare PETG replacement",
            description: "Bambu X1C is approaching its filament warning point.",
            status: .pending,
            priority: .normal,
            createdAt: Date(timeIntervalSince1970: 1_784_510_460),
            dueAt: Date(timeIntervalSince1970: 1_784_541_800),
            completedAt: nil,
            relatedEntityCount: 1,
            metadataJson: nil,
            anchorKind: .at,
            anchorAtUtc: Date(timeIntervalSince1970: 1_784_541_800),
            windowStartUtc: nil,
            windowEndUtc: nil,
            sourceKind: .filamentCoverage,
            sourceId: "coverage:demo"
        ),
        ShiftTask(
            id: "78200000-0000-0000-0000-000000000003",
            taskType: .spoolRestock,
            entityType: "Spool",
            entityId: "78200000-0000-0000-0000-000000000100",
            title: "Review PLA stock",
            description: "PLA inventory is below the configured reorder threshold.",
            status: .pending,
            priority: .low,
            createdAt: Date(timeIntervalSince1970: 1_784_510_520),
            dueAt: nil,
            completedAt: nil,
            relatedEntityCount: 2,
            metadataJson: nil,
            anchorKind: .anytimeToday,
            anchorAtUtc: nil,
            windowStartUtc: nil,
            windowEndUtc: nil,
            sourceKind: .spoolReorder,
            sourceId: "spool:demo"
        ),
    ]
}
