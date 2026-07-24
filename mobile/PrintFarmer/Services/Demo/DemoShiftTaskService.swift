import Foundation

enum DemoShiftTaskScenario: Equatable {
    case standard
    #if DEBUG
    case mutationFailureThenSuccess
    case initialLoadFailureThenSuccess
    case taskActionRouting
    #endif
}

#if DEBUG
enum DemoShiftTaskError: LocalizedError, Sendable {
    case forcedMutationFailure
    case duplicateRetry
    case forcedInitialLoadFailure

    var errorDescription: String? {
        switch self {
        case .forcedMutationFailure:
            "The demo task action failed."
        case .duplicateRetry:
            "The retry operation was invoked more than once."
        case .forcedInitialLoadFailure:
            "The demo shift plan failed to load."
        }
    }
}
#endif

actor DemoShiftTaskService: ShiftTaskServiceProtocol {
    private let scenario: DemoShiftTaskScenario
    private var tasks: [ShiftTask]
    private var completeAttemptCount = 0
    #if DEBUG
    private var loadAttemptCount = 0
    #endif

    init(scenario: DemoShiftTaskScenario = .standard) {
        self.scenario = scenario
        #if DEBUG
        switch scenario {
        case .standard, .initialLoadFailureThenSuccess:
            self.tasks = Self.standardTasks
        case .mutationFailureThenSuccess:
            self.tasks = [Self.mutationTask]
        case .taskActionRouting:
            self.tasks = Self.routingTasks
        }
        #else
        self.tasks = Self.standardTasks
        #endif
    }

    func loadSnapshot(shiftPlanEnabled: Bool) async throws -> ShiftTaskSnapshot {
        #if DEBUG
        if scenario == .initialLoadFailureThenSuccess {
            loadAttemptCount += 1
            // The first canonical load fails so the view lands in its
            // `.failed` terminal state; the next load (triggered by the
            // failed-state pull-to-refresh) recovers to the grouped plan.
            if loadAttemptCount == 1 {
                throw DemoShiftTaskError.forcedInitialLoadFailure
            }
        }
        #endif

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
        #if DEBUG
        if scenario == .mutationFailureThenSuccess {
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
            return
        }
        #endif
        tasks.removeAll { $0.id == taskID }
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

    #if DEBUG
    /// Deterministic, dedicated seed for #788 task-action routing XCUI. Each
    /// actionable row targets a REAL demo entity so the shipped destination
    /// flow can load its context: harvest → an existing completed job, swap →
    /// an existing printer, maintenance → an existing printer with a structured
    /// `maintenancealert:` source id. Duplicate display names across rows prove
    /// routing keys off stable IDs, never titles.
    static let routingHarvestTaskID = "78810000-0000-0000-0000-000000000001"
    static let routingSwapTaskID = "78810000-0000-0000-0000-000000000002"
    static let routingMaintenanceTaskID = "78810000-0000-0000-0000-000000000003"
    static let routingHarvestJobID = "30000000-0003-0000-0000-000000000007"
    static let routingSwapPrinterID = "10000000-0001-0000-0000-000000000003"
    static let routingMaintenancePrinterID = "10000000-0001-0000-0000-000000000001"

    private static let routingTasks = [
        ShiftTask(
            id: routingHarvestTaskID,
            taskType: .harvestReady,
            entityType: "Job",
            entityId: routingHarvestJobID,
            title: "Handle the plate",
            description: "Completed print is ready to clear.",
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
            sourceId: "harvest:routing"
        ),
        ShiftTask(
            id: routingSwapTaskID,
            taskType: .filamentRunout,
            entityType: "Printer",
            entityId: routingSwapPrinterID,
            title: "Handle the plate",
            description: "Filament is about to run out.",
            status: .pending,
            priority: .high,
            createdAt: Date(timeIntervalSince1970: 1_784_510_460),
            dueAt: nil,
            completedAt: nil,
            relatedEntityCount: 1,
            metadataJson: nil,
            anchorKind: .now,
            anchorAtUtc: nil,
            windowStartUtc: nil,
            windowEndUtc: nil,
            sourceKind: .filamentCoverage,
            sourceId: "coverage:routing"
        ),
        ShiftTask(
            id: routingMaintenanceTaskID,
            taskType: .maintenanceInIdleWindow,
            entityType: "Printer",
            entityId: routingMaintenancePrinterID,
            title: "Handle the plate",
            description: "Scheduled maintenance is due in the idle window.",
            status: .pending,
            priority: .normal,
            createdAt: Date(timeIntervalSince1970: 1_784_510_520),
            dueAt: nil,
            completedAt: nil,
            relatedEntityCount: 1,
            metadataJson: nil,
            anchorKind: .now,
            anchorAtUtc: nil,
            windowStartUtc: nil,
            windowEndUtc: nil,
            sourceKind: .maintenance,
            sourceId: "maintenancealert:22220000-0000-0000-0000-000000000001"
        ),
    ]
    #endif
}
