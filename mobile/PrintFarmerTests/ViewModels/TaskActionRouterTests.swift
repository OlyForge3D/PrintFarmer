import XCTest
@testable import PrintFarmer

/// Behavioral proofs for the F8-M2 (#788) task-action router. All ordering is
/// enforced with explicit state barriers (entered-counters + release gates) and
/// generation/authority guards — never fixed sleeps, `Task.yield`, or elapsed
/// time.
@MainActor
final class TaskActionRouterTests: XCTestCase {
    private let printerA = UUID(uuidString: "A0000000-0000-0000-0000-000000000001")!
    private let printerB = UUID(uuidString: "B0000000-0000-0000-0000-000000000002")!
    private let jobA = UUID(uuidString: "10000000-0000-0000-0000-000000000001")!

    // MARK: - Dismiss-before-destination

    func testSwapAppliesDestinationOnlyAfterDismissAck() async {
        let probe = RoutingProbe()
        probe.holdDismiss = true
        let router = makeRouter(probe)

        let task = Task { @MainActor in
            await router.activate(task: self.swapTask(printerID: self.printerA), capabilities: .init())
        }
        await probe.awaitDismissEntered(1)

        // The destination must NOT be applied while the dismissal is pending.
        XCTAssertTrue(probe.swapCalls.isEmpty)

        probe.releaseDismiss()
        await task.value

        XCTAssertEqual(probe.dismissCount, 1)
        XCTAssertEqual(probe.swapCalls.map(\.printerID), [printerA])
    }

    func testMaintenancePresentsAfterDismiss() async {
        let probe = RoutingProbe()
        probe.holdDismiss = true
        let router = makeRouter(probe)

        let task = Task { @MainActor in
            await router.activate(task: self.maintenanceTask(printerID: self.printerA), capabilities: .init())
        }
        await probe.awaitDismissEntered(1)
        XCTAssertNil(router.maintenancePresentation)

        probe.releaseDismiss()
        await task.value

        XCTAssertEqual(router.maintenancePresentation?.printerID, printerA)
    }

    func testHarvestPresentsAfterDismissAndLoad() async {
        let probe = RoutingProbe()
        probe.harvestResult = .success(makeJob(id: jobA))
        let router = makeRouter(probe)

        await router.activate(task: harvestTask(jobID: jobA), capabilities: .init())

        XCTAssertEqual(probe.dismissCount, 1)
        XCTAssertEqual(probe.harvestLoadCount, 1)
        XCTAssertEqual(router.harvestPresentation?.job.id, jobA)
    }

    // MARK: - Idempotent one-destination / newest-wins

    func testNewestActionSupersedesToSingleDestination() async {
        let probe = RoutingProbe()
        probe.holdDismiss = true
        let router = makeRouter(probe)

        let first = Task { @MainActor in
            await router.activate(task: self.swapTask(printerID: self.printerA), capabilities: .init())
        }
        await probe.awaitDismissEntered(1)
        let second = Task { @MainActor in
            await router.activate(task: self.swapTask(printerID: self.printerB), capabilities: .init())
        }
        await probe.awaitDismissEntered(2)

        probe.releaseDismiss()
        await first.value
        await second.value

        // Only the newest action's destination survives (#726 newest-wins).
        XCTAssertEqual(probe.swapCalls.map(\.printerID), [printerB])
        XCTAssertNil(router.harvestPresentation)
    }

    func testRepeatedTapsPresentSingleDestination() async {
        let probe = RoutingProbe()
        probe.holdDismiss = true
        let router = makeRouter(probe)
        let task = swapTask(printerID: printerA)

        let first = Task { @MainActor in await router.activate(task: task, capabilities: .init()) }
        await probe.awaitDismissEntered(1)
        let second = Task { @MainActor in await router.activate(task: task, capabilities: .init()) }
        await probe.awaitDismissEntered(2)

        probe.releaseDismiss()
        await first.value
        await second.value

        XCTAssertEqual(probe.swapCalls.count, 1)
        XCTAssertEqual(probe.swapCalls.first?.printerID, printerA)
    }

    // MARK: - Fail-safe on the row (no navigation)

    func testResolutionFailureStaysOnRowAndDoesNotDismissOrNavigate() async {
        let probe = RoutingProbe()
        let router = makeRouter(probe)

        await router.activate(task: nonActionableTask(), capabilities: .init())

        XCTAssertEqual(probe.dismissCount, 0)
        XCTAssertTrue(probe.swapCalls.isEmpty)
        XCTAssertNil(router.harvestPresentation)
        XCTAssertNil(router.maintenancePresentation)
    }

    func testResolutionFailureRecordsRowError() async {
        let probe = RoutingProbe()
        let router = makeRouter(probe)
        let task = nonActionableTask()

        await router.activate(task: task, capabilities: .init())

        XCTAssertEqual(router.rowError(for: task.id)?.error, .notActionable)
    }

    func testHarvestDependencyUnavailableFailsSafeAfterDismiss() async {
        let probe = RoutingProbe()
        probe.harvestResult = .failure(.dependencyUnavailable)
        let router = makeRouter(probe)
        let task = harvestTask(jobID: jobA)

        await router.activate(task: task, capabilities: .init())

        // The dismissal still happened, but no destination is applied and the
        // error is surfaced on the row for retry.
        XCTAssertEqual(probe.dismissCount, 1)
        XCTAssertNil(router.harvestPresentation)
        XCTAssertEqual(router.rowError(for: task.id)?.error, .dependencyUnavailable)
    }

    // MARK: - Server switch / teardown

    func testServerSwitchDuringDismissAbortsDestination() async {
        let probe = RoutingProbe()
        probe.holdDismiss = true
        probe.authority = 0
        let router = makeRouter(probe)

        let task = Task { @MainActor in
            await router.activate(task: self.swapTask(printerID: self.printerA), capabilities: .init())
        }
        await probe.awaitDismissEntered(1)

        // Simulate a server switch mid-handoff.
        probe.authority = 1
        probe.releaseDismiss()
        await task.value

        XCTAssertTrue(probe.swapCalls.isEmpty)
    }

    func testServerSwitchDuringHarvestLoadAbortsPresentation() async {
        let probe = RoutingProbe()
        probe.holdHarvestLoad = true
        probe.harvestResult = .success(makeJob(id: jobA))
        probe.authority = 0
        let router = makeRouter(probe)

        let task = Task { @MainActor in
            await router.activate(task: self.harvestTask(jobID: self.jobA), capabilities: .init())
        }
        await probe.awaitHarvestEntered(1)

        probe.authority = 1
        probe.releaseHarvestLoad()
        await task.value

        XCTAssertNil(router.harvestPresentation)
    }

    func testInvalidateAbortsInFlightHandoff() async {
        let probe = RoutingProbe()
        probe.holdDismiss = true
        let router = makeRouter(probe)

        let task = Task { @MainActor in
            await router.activate(task: self.swapTask(printerID: self.printerA), capabilities: .init())
        }
        await probe.awaitDismissEntered(1)

        router.invalidate()
        probe.releaseDismiss()
        await task.value

        XCTAssertTrue(probe.swapCalls.isEmpty)
    }

    // MARK: - One canonical refresh, zero duplicate mutation

    func testHarvestCompletionTriggersExactlyOneRefresh() async {
        let probe = RoutingProbe()
        probe.harvestResult = .success(makeJob(id: jobA))
        let router = makeRouter(probe)
        let task = harvestTask(jobID: jobA)

        await router.activate(task: task, capabilities: .init())
        XCTAssertNotNil(router.harvestPresentation)

        await router.harvestDidComplete(taskID: task.id)
        XCTAssertEqual(probe.refreshCount, 1)
        XCTAssertNil(router.harvestPresentation)

        // A duplicated completion callback must not trigger a second refresh.
        await router.harvestDidComplete(taskID: task.id)
        XCTAssertEqual(probe.refreshCount, 1)
    }

    // MARK: - Builders

    private func makeRouter(_ probe: RoutingProbe) -> TaskActionRouter {
        let router = TaskActionRouter()
        router.configure(environment: probe.environment())
        return router
    }

    private func swapTask(printerID: UUID) -> ShiftTask {
        makeTask(taskType: .filamentRunout, entityType: "Printer", entityId: printerID.uuidString)
    }

    private func maintenanceTask(printerID: UUID) -> ShiftTask {
        makeTask(taskType: .maintenanceInIdleWindow, entityType: "Printer", entityId: printerID.uuidString)
    }

    private func harvestTask(jobID: UUID) -> ShiftTask {
        makeTask(taskType: .harvestReady, entityType: "Job", entityId: jobID.uuidString)
    }

    private func nonActionableTask() -> ShiftTask {
        makeTask(taskType: .failureClear, entityType: "Printer", entityId: printerA.uuidString)
    }

    private func makeTask(taskType: ShiftTaskType, entityType: String, entityId: String) -> ShiftTask {
        ShiftTask(
            id: "task-\(entityId)-\(taskType.wireValue)",
            taskType: taskType,
            entityType: entityType,
            entityId: entityId,
            title: "Task",
            description: nil,
            status: .pending,
            priority: .high,
            createdAt: Date(timeIntervalSince1970: 1_773_000_000),
            dueAt: nil,
            completedAt: nil,
            relatedEntityCount: 1,
            metadataJson: nil,
            anchorKind: .now,
            anchorAtUtc: nil,
            windowStartUtc: nil,
            windowEndUtc: nil,
            sourceKind: .unspecified,
            sourceId: nil
        )
    }

    private func makeJob(id: UUID) -> PrintJob {
        PrintJob(
            id: id, rowVersion: nil, dispatchStateRowVersion: nil,
            status: .completed, priority: .normal, queuePosition: 0,
            gcodeFileId: nil, gcodeFileName: "part.gcode",
            assignedPrinterId: nil, assignedPrinterName: nil,
            createdAt: .now, updatedAt: .now, actualStartTime: nil, actualEndTime: nil,
            estimatedPrintTime: nil, actualPrintTime: nil,
            estimatedFilamentUsage: nil, actualFilamentUsage: nil,
            estimatedCost: nil, actualCost: nil, failureReason: nil,
            requiredNozzleDiameter: nil, requiredMaterialType: nil,
            spoolmanFilamentId: nil, filamentName: nil, filamentVendor: nil, filamentColor: nil,
            copies: 1, completedCopies: 1, remainingCopies: 0,
            projectFileId: nil, thumbnailUrl: nil
        )
    }
}

/// Deterministic side-effect probe for `TaskActionRouter`. Records calls and
/// exposes entered-counter barriers plus release gates so tests control
/// interleaving without any timing assumptions.
@MainActor
private final class RoutingProbe {
    var authority = 0
    var harvestResult: Result<PrintJob, TaskActionRouteError> = .failure(.dependencyUnavailable)

    private(set) var dismissCount = 0
    private(set) var swapCalls: [(printerID: UUID, toolheadID: String?)] = []
    private(set) var harvestLoadCount = 0
    private(set) var refreshCount = 0

    var holdDismiss = false
    var holdHarvestLoad = false

    private var dismissReleaseWaiters: [CheckedContinuation<Void, Never>] = []
    private var harvestReleaseWaiters: [CheckedContinuation<Void, Never>] = []
    private var dismissEnteredWaiters: [(Int, CheckedContinuation<Void, Never>)] = []
    private var harvestEnteredWaiters: [(Int, CheckedContinuation<Void, Never>)] = []

    func environment() -> TaskActionRoutingEnvironment {
        TaskActionRoutingEnvironment(
            dismissActiveSheets: { [weak self] in await self?.handleDismiss() },
            navigateToSwap: { [weak self] printerID, toolheadID in
                self?.swapCalls.append((printerID, toolheadID))
            },
            authoritySnapshot: { [weak self] in self?.authority ?? 0 },
            loadHarvestJob: { [weak self] _ in
                await self?.handleHarvestLoad() ?? .failure(.dependencyUnavailable)
            },
            refreshTasks: { [weak self] in self?.refreshCount += 1 }
        )
    }

    // MARK: dismiss

    private func handleDismiss() async {
        dismissCount += 1
        resume(&dismissEnteredWaiters, count: dismissCount)
        guard holdDismiss else { return }
        await withCheckedContinuation { dismissReleaseWaiters.append($0) }
    }

    func releaseDismiss() {
        let waiters = dismissReleaseWaiters
        dismissReleaseWaiters = []
        waiters.forEach { $0.resume() }
    }

    func awaitDismissEntered(_ target: Int) async {
        if dismissCount >= target { return }
        await withCheckedContinuation { dismissEnteredWaiters.append((target, $0)) }
    }

    // MARK: harvest load

    private func handleHarvestLoad() async -> Result<PrintJob, TaskActionRouteError> {
        harvestLoadCount += 1
        resume(&harvestEnteredWaiters, count: harvestLoadCount)
        if holdHarvestLoad {
            await withCheckedContinuation { harvestReleaseWaiters.append($0) }
        }
        return harvestResult
    }

    func releaseHarvestLoad() {
        let waiters = harvestReleaseWaiters
        harvestReleaseWaiters = []
        waiters.forEach { $0.resume() }
    }

    func awaitHarvestEntered(_ target: Int) async {
        if harvestLoadCount >= target { return }
        await withCheckedContinuation { harvestEnteredWaiters.append((target, $0)) }
    }

    private func resume(
        _ waiters: inout [(Int, CheckedContinuation<Void, Never>)],
        count: Int
    ) {
        let ready = waiters.filter { count >= $0.0 }
        waiters.removeAll { count >= $0.0 }
        ready.forEach { $0.1.resume() }
    }
}
