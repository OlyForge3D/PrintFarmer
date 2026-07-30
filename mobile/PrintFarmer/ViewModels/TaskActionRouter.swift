import Foundation
import Observation

/// Injected side-effect seam for `TaskActionRouter`. Production wires these to
/// the shared `AppRouter`, `ServiceContainer`, and the checklist view model;
/// tests inject controlled closures with explicit barriers so ordering,
/// authority, and refresh counts are deterministic (no sleeps / polling).
///
/// All members are invoked from the `@MainActor`-isolated router.
struct TaskActionRoutingEnvironment {
    /// Dismisses any active operator/legacy sheet and returns only once the
    /// dismissal has been acknowledged. Must complete BEFORE the router
    /// mutates the destination so an action never opens behind a sheet.
    var dismissActiveSheets: @MainActor () async -> Void
    /// Applies the guided-swap destination (tab + navigation path) via the
    /// shared router.
    var navigateToSwap: @MainActor (_ printerID: UUID, _ toolheadID: String?) -> Void
    /// A monotonic token identifying the current server/authority. A change
    /// mid-handoff (server switch / teardown) aborts destination application.
    var authoritySnapshot: @MainActor () -> Int
    /// Loads the exact `PrintJob` for a harvest handoff. Returns a typed
    /// routing error (mapped from unauthorized / unavailable transport
    /// failures) so a dependency failure fails safe on the row.
    var loadHarvestJob: @MainActor (_ jobID: UUID) async -> Result<PrintJob, TaskActionRouteError>
    /// Performs exactly one canonical task refresh after a successful harvest.
    var refreshTasks: @MainActor () async -> Void

    init(
        dismissActiveSheets: @escaping @MainActor () async -> Void,
        navigateToSwap: @escaping @MainActor (_ printerID: UUID, _ toolheadID: String?) -> Void,
        authoritySnapshot: @escaping @MainActor () -> Int,
        loadHarvestJob: @escaping @MainActor (_ jobID: UUID) async -> Result<PrintJob, TaskActionRouteError>,
        refreshTasks: @escaping @MainActor () async -> Void
    ) {
        self.dismissActiveSheets = dismissActiveSheets
        self.navigateToSwap = navigateToSwap
        self.authoritySnapshot = authoritySnapshot
        self.loadHarvestJob = loadHarvestJob
        self.refreshTasks = refreshTasks
    }
}

/// Coordinates the typed handoff from an actionable #782 checklist row to the
/// shipped harvest (#714), guided-swap (#710), and maintenance flows (#788).
///
/// Guarantees:
/// * **No title parsing** — destinations come only from `TaskActionRouteResolver`.
/// * **Dismiss-before-destination** — the active sheet is dismissed before any
///   tab/path/sheet mutation (#726).
/// * **Idempotent, newest-wins** — a monotonic generation supersedes any
///   in-flight handoff, so repeated taps or a newer action present exactly one
///   destination.
/// * **Server-switch safe** — an authority change mid-handoff aborts
///   application, so an old-server destination is never applied to a new server.
/// * **One canonical refresh, zero duplicate mutations** — a successful harvest
///   triggers a single task refresh; the router never issues a domain mutation.
/// * **Fail-safe on the row** — any resolution or dependency failure surfaces a
///   task-scoped, retryable error without navigating.
@MainActor
@Observable
final class TaskActionRouter {
    struct HarvestPresentation: Identifiable {
        let taskID: String
        let job: PrintJob
        var id: UUID { job.id }
    }

    struct MaintenancePresentation: Identifiable, Equatable {
        let taskID: String
        let printerID: UUID
        let alertID: UUID?
        let componentID: String?
        let toolheadID: String?
        var id: String { taskID }
    }

    struct RowError: Identifiable, Equatable {
        let id: UUID
        let taskID: String
        let error: TaskActionRouteError
        var message: String { error.message }
    }

    /// The active harvest handoff sheet, if any. Rendered by `ShiftTasksView`.
    private(set) var harvestPresentation: HarvestPresentation?
    /// The active maintenance handoff sheet, if any.
    private(set) var maintenancePresentation: MaintenancePresentation?
    /// Per-task retryable routing errors keyed by task id.
    private(set) var rowErrors: [String: RowError] = [:]
    /// The task whose handoff is currently in-flight (drives the row spinner
    /// and blocks duplicate concurrent taps of the same row).
    private(set) var routingTaskID: String?

    @ObservationIgnored private var environment: TaskActionRoutingEnvironment?
    @ObservationIgnored private var generation: UInt64 = 0

    func configure(environment: TaskActionRoutingEnvironment) {
        self.environment = environment
    }

    func rowError(for taskID: String) -> RowError? {
        rowErrors[taskID]
    }

    func isRouting(taskID: String) -> Bool {
        routingTaskID == taskID
    }

    /// Resolves the task to a typed destination and performs the coordinated,
    /// sheet-safe handoff. Safe to call repeatedly for the same row: the
    /// generation guard collapses concurrent/duplicate activations to a single
    /// destination.
    func activate(task: ShiftTask, capabilities: TaskActionCapabilities) async {
        guard let environment else { return }

        rowErrors.removeValue(forKey: task.id)

        let destination: TaskActionDestination
        switch TaskActionRouteResolver.destination(for: task, capabilities: capabilities) {
        case .success(let resolved):
            destination = resolved
        case .failure(let error):
            setRowError(taskID: task.id, error: error)
            return
        }

        // Advance authority: newest activation wins. Any in-flight handoff
        // (older generation) is superseded and aborts at the guards below.
        generation &+= 1
        let gen = generation
        let authority = environment.authoritySnapshot()
        routingTaskID = task.id

        // A superseded destination must never linger behind the newest one.
        harvestPresentation = nil
        maintenancePresentation = nil

        // Dismiss the active operator/legacy sheet BEFORE mutating the
        // destination (#726). The router applies the destination only after
        // this acknowledged dismissal.
        await environment.dismissActiveSheets()
        guard isCurrent(gen: gen, authority: authority, environment: environment) else { return }

        switch destination {
        case .filamentSwap(let printerID, let toolheadID):
            environment.navigateToSwap(printerID, toolheadID)
            finishRouting(gen: gen)

        case .maintenance(let printerID, let alertID, let componentID, let toolheadID):
            maintenancePresentation = MaintenancePresentation(
                taskID: task.id,
                printerID: printerID,
                alertID: alertID,
                componentID: componentID,
                toolheadID: toolheadID
            )
            finishRouting(gen: gen)

        case .harvest(let jobID):
            let jobResult = await environment.loadHarvestJob(jobID)
            guard isCurrent(gen: gen, authority: authority, environment: environment) else { return }
            switch jobResult {
            case .success(let job):
                harvestPresentation = HarvestPresentation(taskID: task.id, job: job)
            case .failure(let error):
                setRowError(taskID: task.id, error: error)
            }
            finishRouting(gen: gen)
        }
    }

    /// Invoked by the harvest flow's once-only `onHarvested` callback. Performs
    /// exactly one canonical task refresh and issues no domain mutation. Guarded
    /// so a repeated callback cannot trigger a second refresh.
    func harvestDidComplete(taskID: String) async {
        guard let environment, harvestPresentation?.taskID == taskID else { return }
        harvestPresentation = nil
        routingTaskID = nil
        await environment.refreshTasks()
    }

    func harvestSheetDismissed() {
        harvestPresentation = nil
        routingTaskID = nil
    }

    func maintenanceSheetDismissed() {
        maintenancePresentation = nil
        routingTaskID = nil
    }

    func dismissRowError(taskID: String) {
        rowErrors.removeValue(forKey: taskID)
    }

    /// Server switch / teardown: invalidate any in-flight handoff so a stale
    /// destination cannot be applied to a new server, and clear presentations.
    func invalidate() {
        generation &+= 1
        harvestPresentation = nil
        maintenancePresentation = nil
        routingTaskID = nil
    }

    // MARK: - Guards

    private func isCurrent(
        gen: UInt64,
        authority: Int,
        environment: TaskActionRoutingEnvironment
    ) -> Bool {
        gen == generation && authority == environment.authoritySnapshot()
    }

    private func finishRouting(gen: UInt64) {
        guard gen == generation else { return }
        routingTaskID = nil
    }

    private func setRowError(taskID: String, error: TaskActionRouteError) {
        rowErrors[taskID] = RowError(id: UUID(), taskID: taskID, error: error)
        if routingTaskID == taskID {
            routingTaskID = nil
        }
    }
}
