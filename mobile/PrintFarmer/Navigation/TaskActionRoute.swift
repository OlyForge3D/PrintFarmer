import Foundation

/// The four actionable shift-task kinds routed by F8-M2 (#788). Every other
/// task type is non-actionable for routing purposes and only offers the
/// #782 lifecycle actions (complete / skip / dismiss).
enum TaskActionKind: Equatable, Sendable {
    case harvest
    case filamentSwap
    case maintenance

    /// Maps a server-owned task type to its routable action kind, or `nil`
    /// when the type is not one of the F8-M2 handoff targets. Failure-clear,
    /// restock, and every other type deliberately return `nil` (out of scope).
    init?(taskType: ShiftTaskType) {
        switch taskType {
        case .harvestReady:
            self = .harvest
        case .filamentRunout:
            self = .filamentSwap
        case .maintenanceDue, .maintenanceInIdleWindow:
            self = .maintenance
        default:
            return nil
        }
    }
}

/// Typed, stable-ID destination for an actionable checklist row (#788).
///
/// Derived exclusively from server-owned identity — task type, entity
/// type/id, source kind/id, and typed `metadataJson`. Titles, descriptions,
/// and display names are NEVER inspected to recover printer / job / toolhead /
/// component / alert identity. Duplicate display names therefore cannot cause
/// a misroute.
enum TaskActionDestination: Equatable, Sendable {
    /// Hands off to the shipped #714 harvest flow for the exact print job /
    /// output snapshot.
    case harvest(jobID: UUID)
    /// Hands off to the mobile #710 guided-swap flow for the exact printer /
    /// toolhead.
    case filamentSwap(printerID: UUID, toolheadID: String?)
    /// Hands off to the existing maintenance ack/log flow for the exact
    /// printer / component / toolhead / alert.
    case maintenance(
        printerID: UUID,
        alertID: UUID?,
        componentID: String?,
        toolheadID: String?
    )
}

/// Typed metadata carried on `ShiftTask.metadataJson` (#746 contract). Every
/// field is optional and decoded leniently; unknown keys are ignored. This is
/// the only sanctioned channel (besides the stable `entityType`/`entityId`
/// and `sourceId`) for recovering handoff identity — never the title/detail.
struct TaskActionMetadata: Decodable, Equatable, Sendable {
    var jobID: String?
    var outputID: String?
    var printerID: String?
    var toolheadID: String?
    var componentID: String?
    var maintenanceAlertID: String?

    private enum CodingKeys: String, CodingKey {
        case jobID = "jobId"
        case outputID = "outputId"
        case printerID = "printerId"
        case toolheadID = "toolheadId"
        case componentID = "componentId"
        case maintenanceAlertID = "maintenanceAlertId"
    }

    static let empty = TaskActionMetadata()

    init(
        jobID: String? = nil,
        outputID: String? = nil,
        printerID: String? = nil,
        toolheadID: String? = nil,
        componentID: String? = nil,
        maintenanceAlertID: String? = nil
    ) {
        self.jobID = jobID
        self.outputID = outputID
        self.printerID = printerID
        self.toolheadID = toolheadID
        self.componentID = componentID
        self.maintenanceAlertID = maintenanceAlertID
    }
}

/// Feature gates consulted at resolve time so a disabled destination fails
/// safely on the row instead of routing to a guessed destination.
struct TaskActionCapabilities: Equatable, Sendable {
    var harvestEnabled: Bool
    var guidedSwapEnabled: Bool
    var maintenanceEnabled: Bool

    init(
        harvestEnabled: Bool = true,
        guidedSwapEnabled: Bool = true,
        maintenanceEnabled: Bool = true
    ) {
        self.harvestEnabled = harvestEnabled
        self.guidedSwapEnabled = guidedSwapEnabled
        self.maintenanceEnabled = maintenanceEnabled
    }

    /// Convenience projection from the app's resolved capability snapshot.
    /// Maintenance has no dedicated gate and is always available.
    init(resolved: ResolvedSystemCapabilities) {
        self.harvestEnabled = resolved.printedPartsInventoryEnabled
        self.guidedSwapEnabled = resolved.guidedSwapEnabled
        self.maintenanceEnabled = true
    }
}

/// Why an actionable row could not be routed. Every case is retryable — the
/// operator stays on the checklist with a task-scoped, actionable error and
/// no navigation occurs.
enum TaskActionRouteError: Error, Equatable, Sendable {
    /// The task type is not one of the F8-M2 handoff targets.
    case notActionable
    /// Required typed identity was absent.
    case missingIdentity(field: String)
    /// Required typed identity was present but not a well-formed identifier.
    case malformedIdentity(field: String)
    /// `metadataJson` was present but was not valid JSON.
    case malformedMetadata
    /// The destination's feature gate is disabled for this deployment.
    case featureDisabled
    /// The current user is not permitted to open the destination.
    case unauthorized
    /// The destination dependency (e.g. the harvest job) was unavailable.
    case dependencyUnavailable

    /// Operator-facing message for the row-scoped error banner.
    var message: String {
        switch self {
        case .notActionable:
            return "This task type has no guided action."
        case .missingIdentity:
            return "This task is missing the details needed to open it. Refresh and try again."
        case .malformedIdentity:
            return "This task's details are invalid. Refresh and try again."
        case .malformedMetadata:
            return "This task's details are invalid. Refresh and try again."
        case .featureDisabled:
            return "This action is disabled on the current server."
        case .unauthorized:
            return "You do not have permission to open this action."
        case .dependencyUnavailable:
            return "This action is temporarily unavailable. Try again."
        }
    }
}

/// Pure, deterministic resolver from a server-owned `ShiftTask` to a typed,
/// stable-ID destination. Contains no UI, no navigation, and no title/detail
/// parsing. This is the single seam that guarantees exact-stable-ID routing.
enum TaskActionRouteResolver {
    /// Structured source-id prefix (#746) identifying a maintenance alert,
    /// e.g. `maintenancealert:{uuid}`. This is a stable machine identifier —
    /// not a display string.
    static let maintenanceAlertSourcePrefix = "maintenancealert:"

    static func destination(
        for task: ShiftTask,
        capabilities: TaskActionCapabilities
    ) -> Result<TaskActionDestination, TaskActionRouteError> {
        guard let kind = TaskActionKind(taskType: task.taskType) else {
            return .failure(.notActionable)
        }

        let metadata: TaskActionMetadata
        switch decodeMetadata(task.metadataJson) {
        case .success(let value):
            metadata = value
        case .failure(let error):
            return .failure(error)
        }

        switch kind {
        case .harvest:
            return resolveHarvest(task: task, metadata: metadata, capabilities: capabilities)
        case .filamentSwap:
            return resolveSwap(task: task, metadata: metadata, capabilities: capabilities)
        case .maintenance:
            return resolveMaintenance(task: task, metadata: metadata, capabilities: capabilities)
        }
    }

    // MARK: - Per-kind resolution

    private static func resolveHarvest(
        task: ShiftTask,
        metadata: TaskActionMetadata,
        capabilities: TaskActionCapabilities
    ) -> Result<TaskActionDestination, TaskActionRouteError> {
        guard capabilities.harvestEnabled else { return .failure(.featureDisabled) }

        // The exact job/output snapshot comes from typed metadata first, then
        // falls back to the stable entity when the task's entity IS the job.
        if let raw = metadata.jobID {
            guard let jobID = UUID(uuidString: raw) else {
                return .failure(.malformedIdentity(field: "jobId"))
            }
            return .success(.harvest(jobID: jobID))
        }

        if task.entityType.caseInsensitiveEquals("Job") {
            guard let jobID = UUID(uuidString: task.entityId) else {
                return .failure(.malformedIdentity(field: "entityId"))
            }
            return .success(.harvest(jobID: jobID))
        }

        return .failure(.missingIdentity(field: "jobId"))
    }

    private static func resolveSwap(
        task: ShiftTask,
        metadata: TaskActionMetadata,
        capabilities: TaskActionCapabilities
    ) -> Result<TaskActionDestination, TaskActionRouteError> {
        guard capabilities.guidedSwapEnabled else { return .failure(.featureDisabled) }

        guard let printerID = resolvePrinterID(task: task, metadata: metadata) else {
            return failurePrinter(task: task, metadata: metadata)
        }
        return .success(.filamentSwap(printerID: printerID, toolheadID: metadata.toolheadID.nonEmpty))
    }

    private static func resolveMaintenance(
        task: ShiftTask,
        metadata: TaskActionMetadata,
        capabilities: TaskActionCapabilities
    ) -> Result<TaskActionDestination, TaskActionRouteError> {
        guard capabilities.maintenanceEnabled else { return .failure(.featureDisabled) }

        guard let printerID = resolvePrinterID(task: task, metadata: metadata) else {
            return failurePrinter(task: task, metadata: metadata)
        }

        let alertID = resolveMaintenanceAlertID(task: task, metadata: metadata)
        // A maintenance alert id, when present in either channel, must be
        // well-formed; a malformed one fails safe rather than routing blind.
        if case .malformed(let field) = alertID {
            return .failure(.malformedIdentity(field: field))
        }

        return .success(.maintenance(
            printerID: printerID,
            alertID: alertID.value,
            componentID: metadata.componentID.nonEmpty,
            toolheadID: metadata.toolheadID.nonEmpty
        ))
    }

    // MARK: - Shared identity helpers

    private static func resolvePrinterID(
        task: ShiftTask,
        metadata: TaskActionMetadata
    ) -> UUID? {
        if let raw = metadata.printerID, let id = UUID(uuidString: raw) {
            return id
        }
        if task.entityType.caseInsensitiveEquals("Printer"), let id = UUID(uuidString: task.entityId) {
            return id
        }
        return nil
    }

    private static func failurePrinter(
        task: ShiftTask,
        metadata: TaskActionMetadata
    ) -> Result<TaskActionDestination, TaskActionRouteError> {
        // Distinguish "present but malformed" from "absent" so the operator
        // gets an accurate, actionable message.
        if let raw = metadata.printerID, UUID(uuidString: raw) == nil {
            return .failure(.malformedIdentity(field: "printerId"))
        }
        if task.entityType.caseInsensitiveEquals("Printer"), UUID(uuidString: task.entityId) == nil {
            return .failure(.malformedIdentity(field: "entityId"))
        }
        return .failure(.missingIdentity(field: "printerId"))
    }

    private enum MaintenanceAlertResolution {
        case value(UUID?)
        case malformed(field: String)

        var value: UUID? {
            if case .value(let id) = self { return id }
            return nil
        }
    }

    private static func resolveMaintenanceAlertID(
        task: ShiftTask,
        metadata: TaskActionMetadata
    ) -> MaintenanceAlertResolution {
        if let raw = metadata.maintenanceAlertID {
            guard let id = UUID(uuidString: raw) else {
                return .malformed(field: "maintenanceAlertId")
            }
            return .value(id)
        }

        if let sourceId = task.sourceId,
           let range = sourceId.range(of: maintenanceAlertSourcePrefix),
           range.lowerBound == sourceId.startIndex {
            let raw = String(sourceId[range.upperBound...])
            guard let id = UUID(uuidString: raw) else {
                return .malformed(field: "sourceId")
            }
            return .value(id)
        }

        return .value(nil)
    }

    // MARK: - Metadata decoding

    private static func decodeMetadata(
        _ json: String?
    ) -> Result<TaskActionMetadata, TaskActionRouteError> {
        guard let json, !json.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
            return .success(.empty)
        }
        guard let data = json.data(using: .utf8) else {
            return .failure(.malformedMetadata)
        }
        do {
            return .success(try JSONDecoder().decode(TaskActionMetadata.self, from: data))
        } catch {
            return .failure(.malformedMetadata)
        }
    }
}

private extension String {
    func caseInsensitiveEquals(_ other: String) -> Bool {
        caseInsensitiveCompare(other) == .orderedSame
    }
}

private extension Optional where Wrapped == String {
    /// Treats an empty/whitespace string as absent.
    var nonEmpty: String? {
        guard let self, !self.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
            return nil
        }
        return self
    }
}
