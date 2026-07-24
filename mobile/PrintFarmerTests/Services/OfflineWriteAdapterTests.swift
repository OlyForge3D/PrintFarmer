import XCTest
@testable import PrintFarmer

// MARK: - #790 F10-Q2 — Task-complete and toolhead-bind queue adapters
//
// Deterministic proofs for the two typed adapters added on top of #787's SAME
// offline write queue engine. Everything is driven by a controlled clock, an
// in-memory store, and controllable task/printer service doubles — no sleeps,
// no polling, no elapsed-time pass criteria. Server effects are counted through
// explicit call records; persistence through the store; ordering through the
// recording transport.

// MARK: Controllable task service double

/// A controllable `ShiftTaskServiceProtocol` that returns a scripted snapshot,
/// can throw controlled errors from `loadSnapshot`/`complete`, and records every
/// `complete(taskID:idempotencyKey:)` so a test can count exact server effects
/// and assert the frozen key is reused across retries.
final class ControllableTaskService: ShiftTaskServiceProtocol, @unchecked Sendable {
    private let lock = NSLock()
    private var snapshot: ShiftTaskSnapshot
    private var loadError: Error?
    private var completeErrors: [Error?]
    private(set) var loadSnapshotCount = 0
    private(set) var completeCalls: [(taskID: String, idempotencyKey: String)] = []

    init(
        snapshot: ShiftTaskSnapshot = makeEmptyShiftTaskSnapshot(),
        loadError: Error? = nil,
        completeErrors: [Error?] = []
    ) {
        self.snapshot = snapshot
        self.loadError = loadError
        self.completeErrors = completeErrors
    }

    func setSnapshot(_ snapshot: ShiftTaskSnapshot) {
        lock.lock(); self.snapshot = snapshot; lock.unlock()
    }

    func setLoadError(_ error: Error?) {
        lock.lock(); self.loadError = error; lock.unlock()
    }

    func loadSnapshot(shiftPlanEnabled: Bool) async throws -> ShiftTaskSnapshot {
        let (error, snap): (Error?, ShiftTaskSnapshot) = lock.withLock {
            loadSnapshotCount += 1
            return (loadError, snapshot)
        }
        if let error { throw error }
        return snap
    }

    func complete(taskID: String, idempotencyKey: String) async throws {
        let error: Error? = lock.withLock {
            completeCalls.append((taskID, idempotencyKey))
            return completeErrors.isEmpty ? nil : completeErrors.removeFirst()
        }
        if let error { throw error }
    }

    func skip(taskID: String) async throws { XCTFail("skip must never be replayed by the offline queue") }
    func dismiss(taskID: String) async throws { XCTFail("dismiss must never be replayed by the offline queue") }

    var completeCount: Int { lock.withLock { completeCalls.count } }
    var completeKeys: [String] { lock.withLock { completeCalls.map { $0.idempotencyKey } } }
}

// MARK: - Bundle transport (drives the production executor)

/// Wraps a fixed `OfflineReplayServices` and runs each replay through the SAME
/// production `OfflineWriteReplayExecutor` the app uses — so these tests exercise
/// the real precondition/classification logic, not a re-implementation.
struct BundleReplayTransport: OfflineWriteReplayTransport {
    let services: OfflineReplayServices
    func replay(_ operation: OfflineWriteOperation) async -> OfflineWriteReplayOutcome {
        await OfflineWriteReplayExecutor.execute(operation, using: services)
    }
}

// MARK: - Fixture helpers

private func taskSnapshot(taskID: String, status: ShiftTaskStatus) -> ShiftTaskSnapshot {
    ShiftTaskSnapshot(
        groups: [ShiftTaskGroup(anchorKind: .now, tasks: [makeShiftTask(id: taskID, status: status)])],
        generatedAt: nil,
        mode: .grouped
    )
}

private func printerDetails(id: UUID, toolheadIndex: Int, currentSpoolId: Int?) -> PrinterDetails {
    PrinterDetails(
        id: id,
        name: "P",
        backend: .unknown,
        toolheads: [
            Toolhead(id: UUID(), name: "T\(toolheadIndex)", index: toolheadIndex, isPrimary: true, currentSpoolId: currentSpoolId)
        ]
    )
}

// MARK: - Tests

final class OfflineWriteAdapterTests: XCTestCase {
    private let serverA = UUID()
    private let userA = UUID()

    private func makeQueue(
        store: OfflineWriteQueueStoring,
        transport: OfflineWriteReplayTransport,
        clock: OfflineQueueClock,
        config: OfflineWriteQueueConfiguration = .default
    ) -> OfflineWriteQueue {
        OfflineWriteQueue(store: store, transport: transport, clock: clock, configuration: config)
    }

    private func services(
        tasks: ControllableTaskService? = nil,
        printers: MockPrinterService? = nil
    ) -> OfflineReplayServices {
        OfflineReplayServices(parts: nil, tasks: tasks, printers: printers)
    }

    // MARK: Exactly-once across relaunch — task complete

    func testTaskCompleteSurvivesRelaunchAndReplaysExactlyOnce() async {
        let store = InMemoryOfflineWriteQueueStore()
        let clock = MutableOfflineQueueClock(OfflineQueueFixtures.epoch)

        // First launch offline: loadSnapshot fails (no connectivity) → retryable.
        let offlineTasks = ControllableTaskService(loadError: NetworkError.noConnection)
        let first = makeQueue(store: store, transport: BundleReplayTransport(services: services(tasks: offlineTasks)), clock: clock)
        await first.bind(serverID: serverA, userID: userA)
        _ = await first.enqueue(OfflineQueueFixtures.taskComplete(taskID: "T-1", key: "k-task"))
        XCTAssertEqual(store.saveCount, 1, "enqueue persists before any attempt")
        await first.replayPending()
        XCTAssertEqual(offlineTasks.completeCount, 0, "offline: nothing applied")
        XCTAssertEqual(store.persistedItems.count, 1, "intent durably retained")

        // Relaunch, reconnect: task still pending server-side → one completion.
        let onlineTasks = ControllableTaskService(snapshot: taskSnapshot(taskID: "T-1", status: .pending))
        let second = makeQueue(store: store, transport: BundleReplayTransport(services: services(tasks: onlineTasks)), clock: clock)
        await second.bind(serverID: serverA, userID: userA)
        await second.replayPending()
        await second.replayPending() // duplicate signal — must not double-apply

        XCTAssertEqual(onlineTasks.completeCount, 1, "exactly one server effect")
        XCTAssertEqual(onlineTasks.completeKeys.first, "k-task", "frozen idempotency key reused")
        let remaining = await second.activeEntries()
        XCTAssertTrue(remaining.isEmpty, "canonical success removes exactly one item")
        XCTAssertEqual(store.persistedItems.count, 0)
    }

    // MARK: Exactly-once across relaunch — toolhead bind

    func testToolheadBindSurvivesRelaunchAndReplaysExactlyOnce() async {
        let store = InMemoryOfflineWriteQueueStore()
        let clock = MutableOfflineQueueClock(OfflineQueueFixtures.epoch)
        let printerID = UUID()

        let offlinePrinters = MockPrinterService()
        offlinePrinters.errorToThrow = NetworkError.noConnection
        let first = makeQueue(store: store, transport: BundleReplayTransport(services: services(printers: offlinePrinters)), clock: clock)
        await first.bind(serverID: serverA, userID: userA)
        _ = await first.enqueue(OfflineQueueFixtures.toolheadBind(
            printerID: printerID, toolheadIndex: 1, key: "k-bind", spoolId: 9, expectedPriorSpoolId: 3
        ))
        await first.replayPending()
        XCTAssertEqual(offlinePrinters.bindToolheadSpoolCalls.count, 0, "offline: no bind")
        XCTAssertEqual(store.persistedItems.count, 1)

        // Reconnect: canonical prior spool (3) still present, target (9) differs → replay.
        let onlinePrinters = MockPrinterService()
        onlinePrinters.detailsToReturn = printerDetails(id: printerID, toolheadIndex: 1, currentSpoolId: 3)
        let second = makeQueue(store: store, transport: BundleReplayTransport(services: services(printers: onlinePrinters)), clock: clock)
        await second.bind(serverID: serverA, userID: userA)
        await second.replayPending()
        await second.replayPending()

        XCTAssertEqual(onlinePrinters.bindToolheadSpoolCalls.count, 1, "exactly one bind effect")
        XCTAssertEqual(onlinePrinters.bindToolheadSpoolCalls.first?.idempotencyKey, "k-bind")
        XCTAssertEqual(onlinePrinters.bindToolheadSpoolCalls.first?.toolheadIndex, 1)
        XCTAssertEqual(onlinePrinters.bindToolheadSpoolCalls.first?.request.spoolId, 9)
        let remaining = await second.activeEntries()
        XCTAssertTrue(remaining.isEmpty)
    }

    // MARK: Transport ambiguity reuses the exact key/body (no duplicate key)

    func testTaskCompleteRetryReusesSameKeyNoNewKeyMinted() async {
        let store = InMemoryOfflineWriteQueueStore()
        let clock = MutableOfflineQueueClock(OfflineQueueFixtures.epoch)
        // complete() throws a transient error on the first attempt (ambiguous
        // ACK), succeeds on the second. Same frozen key both times.
        let tasks = ControllableTaskService(
            snapshot: taskSnapshot(taskID: "T-1", status: .pending),
            completeErrors: [NetworkError.timeout]
        )
        let queue = makeQueue(store: store, transport: BundleReplayTransport(services: services(tasks: tasks)), clock: clock)
        await queue.bind(serverID: serverA, userID: userA)
        _ = await queue.enqueue(OfflineQueueFixtures.taskComplete(taskID: "T-1", key: "k-task"))

        await queue.replayPending() // attempt 1: complete throws timeout → retryable
        let afterFirst = await queue.activeEntries()
        XCTAssertEqual(afterFirst.count, 1, "retryable retains the intent")
        await queue.replayPending() // attempt 2: succeeds

        XCTAssertEqual(tasks.completeCount, 2, "two attempts")
        XCTAssertEqual(Set(tasks.completeKeys), ["k-task"], "same idempotency key reused — no new key minted")
        let afterSecond = await queue.activeEntries()
        XCTAssertTrue(afterSecond.isEmpty, "one canonical success removes the item")
    }

    // MARK: Task-complete classification table (production executor, direct)

    func testTaskAlreadyCompletedIsIdempotentSuccessWithNoRePost() async {
        let tasks = ControllableTaskService(snapshot: taskSnapshot(taskID: "T-1", status: .completed))
        let outcome = await OfflineWriteReplayExecutor.execute(
            OfflineQueueFixtures.taskComplete(taskID: "T-1", key: "k"),
            using: services(tasks: tasks)
        )
        XCTAssertEqual(outcome, .success)
        XCTAssertEqual(tasks.completeCount, 0, "already terminal: exactly one completion, no re-POST")
    }

    func testTaskSkippedOrDismissedSurfacesReviewWithoutMutation() async {
        for status in [ShiftTaskStatus.skipped, .dismissed] {
            let tasks = ControllableTaskService(snapshot: taskSnapshot(taskID: "T-1", status: status))
            let outcome = await OfflineWriteReplayExecutor.execute(
                OfflineQueueFixtures.taskComplete(taskID: "T-1", key: "k"),
                using: services(tasks: tasks)
            )
            guard case .conflict(let conflict) = outcome else {
                return XCTFail("\(status) must surface a conflict for review, got \(outcome)")
            }
            XCTAssertEqual(conflict.reason, .staleState)
            XCTAssertEqual(tasks.completeCount, 0, "incompatible terminal state: zero mutation")
        }
    }

    func testTaskMissingSurfacesUnavailableReview() async {
        let tasks = ControllableTaskService(snapshot: makeEmptyShiftTaskSnapshot())
        let outcome = await OfflineWriteReplayExecutor.execute(
            OfflineQueueFixtures.taskComplete(taskID: "does-not-exist", key: "k"),
            using: services(tasks: tasks)
        )
        guard case .conflict(let conflict) = outcome else { return XCTFail("expected conflict, got \(outcome)") }
        XCTAssertEqual(conflict.reason, .unavailable)
        XCTAssertEqual(tasks.completeCount, 0)
    }

    func testTaskUnauthorizedStopsReplayAsIdentityChanged() async {
        let tasks = ControllableTaskService(loadError: NetworkError.unauthorized)
        let outcome = await OfflineWriteReplayExecutor.execute(
            OfflineQueueFixtures.taskComplete(taskID: "T-1", key: "k"),
            using: services(tasks: tasks)
        )
        XCTAssertEqual(outcome, .identityChanged)
        XCTAssertEqual(tasks.completeCount, 0)
    }

    func testTaskPreconditionTransientFailureIsRetryable() async {
        let tasks = ControllableTaskService(loadError: NetworkError.serverError(503))
        let outcome = await OfflineWriteReplayExecutor.execute(
            OfflineQueueFixtures.taskComplete(taskID: "T-1", key: "k"),
            using: services(tasks: tasks)
        )
        XCTAssertEqual(outcome, .retryable)
        XCTAssertEqual(tasks.completeCount, 0)
    }

    // MARK: Toolhead-bind classification table

    func testToolheadUnchangedPriorPermitsReplay() async {
        let printerID = UUID()
        let printers = MockPrinterService()
        printers.detailsToReturn = printerDetails(id: printerID, toolheadIndex: 0, currentSpoolId: 2)
        let outcome = await OfflineWriteReplayExecutor.execute(
            OfflineQueueFixtures.toolheadBind(printerID: printerID, toolheadIndex: 0, key: "k", spoolId: 5, expectedPriorSpoolId: 2),
            using: services(printers: printers)
        )
        XCTAssertEqual(outcome, .success)
        XCTAssertEqual(printers.bindToolheadSpoolCalls.count, 1, "unchanged prior state → exactly one bind")
    }

    func testToolheadAlreadyBoundToTargetIsIdempotentSuccessZeroMutation() async {
        let printerID = UUID()
        let printers = MockPrinterService()
        printers.detailsToReturn = printerDetails(id: printerID, toolheadIndex: 0, currentSpoolId: 5)
        let outcome = await OfflineWriteReplayExecutor.execute(
            OfflineQueueFixtures.toolheadBind(printerID: printerID, toolheadIndex: 0, key: "k", spoolId: 5, expectedPriorSpoolId: 2),
            using: services(printers: printers)
        )
        XCTAssertEqual(outcome, .success)
        XCTAssertEqual(printers.bindToolheadSpoolCalls.count, 0, "already bound to target → idempotent, ZERO mutation")
    }

    func testToolheadChangedBindingSurfacesReviewWithZeroMutation() async {
        let printerID = UUID()
        let printers = MockPrinterService()
        // Neither the target (5) nor the expected prior (2): a newer binding (9).
        printers.detailsToReturn = printerDetails(id: printerID, toolheadIndex: 0, currentSpoolId: 9)
        let outcome = await OfflineWriteReplayExecutor.execute(
            OfflineQueueFixtures.toolheadBind(printerID: printerID, toolheadIndex: 0, key: "k", spoolId: 5, expectedPriorSpoolId: 2),
            using: services(printers: printers)
        )
        guard case .conflict(let conflict) = outcome else { return XCTFail("expected review conflict, got \(outcome)") }
        XCTAssertEqual(conflict.reason, .staleState)
        XCTAssertEqual(printers.bindToolheadSpoolCalls.count, 0, "changed binding → review, NEVER overwrite")
    }

    func testToolheadMissingSurfacesUnavailableReviewZeroMutation() async {
        let printerID = UUID()
        let printers = MockPrinterService()
        printers.detailsToReturn = printerDetails(id: printerID, toolheadIndex: 0, currentSpoolId: 2)
        let outcome = await OfflineWriteReplayExecutor.execute(
            OfflineQueueFixtures.toolheadBind(printerID: printerID, toolheadIndex: 7, key: "k", spoolId: 5, expectedPriorSpoolId: 2),
            using: services(printers: printers)
        )
        guard case .conflict(let conflict) = outcome else { return XCTFail("expected conflict, got \(outcome)") }
        XCTAssertEqual(conflict.reason, .unavailable)
        XCTAssertEqual(printers.bindToolheadSpoolCalls.count, 0)
    }

    func testToolheadPreconditionTransientFailureIsRetryable() async {
        let printers = MockPrinterService()
        printers.errorToThrow = NetworkError.timeout
        let outcome = await OfflineWriteReplayExecutor.execute(
            OfflineQueueFixtures.toolheadBind(printerID: UUID(), toolheadIndex: 0, key: "k", spoolId: 5, expectedPriorSpoolId: 2),
            using: services(printers: printers)
        )
        XCTAssertEqual(outcome, .retryable)
        XCTAssertEqual(printers.bindToolheadSpoolCalls.count, 0)
    }

    // MARK: Linked domain-write-before-task-complete ordering (point 8)

    func testLinkedDomainWriteReplaysBeforeTaskCompletion() async {
        let store = InMemoryOfflineWriteQueueStore()
        let clock = MutableOfflineQueueClock(OfflineQueueFixtures.epoch)
        let transport = ScriptedReplayTransport(fallback: .success)
        let queue = makeQueue(store: store, transport: transport, clock: clock)
        await queue.bind(serverID: serverA, userID: userA)

        // Domain write D enqueued first; task completion T depends on D.
        let jobId = UUID()
        let dResult = await queue.enqueue(OfflineQueueFixtures.harvest(jobId: jobId, key: "k-domain"))
        guard case .enqueued(let dItem) = dResult else { return XCTFail("domain enqueue failed") }
        clock.advance(1)
        _ = await queue.enqueue(OfflineQueueFixtures.taskComplete(taskID: "T-1", key: "k-task"), prerequisiteItemID: dItem.id)

        await queue.replayPending()

        let recorded = await transport.operations()
        XCTAssertEqual(recorded.map { $0.kind }, [.harvest, .taskComplete], "domain write applied strictly before the linked task completion")
        let remaining = await queue.activeEntries()
        XCTAssertTrue(remaining.isEmpty, "both applied once")
    }

    func testDomainConflictPreventsLinkedTaskCompletion() async {
        let store = InMemoryOfflineWriteQueueStore()
        let clock = MutableOfflineQueueClock(OfflineQueueFixtures.epoch)
        // The domain write conflicts; the linked task completion must NOT run.
        let transport = ScriptedReplayTransport(
            fallback: .success,
            scripted: [.conflict(OfflineWriteConflict(reason: .businessConflict, message: "domain rejected"))]
        )
        let queue = makeQueue(store: store, transport: transport, clock: clock)
        await queue.bind(serverID: serverA, userID: userA)

        let jobId = UUID()
        let dResult = await queue.enqueue(OfflineQueueFixtures.harvest(jobId: jobId, key: "k-domain"))
        guard case .enqueued(let dItem) = dResult else { return XCTFail("domain enqueue failed") }
        clock.advance(1)
        _ = await queue.enqueue(OfflineQueueFixtures.taskComplete(taskID: "T-1", key: "k-task"), prerequisiteItemID: dItem.id)

        await queue.replayPending()

        let recorded = await transport.operations()
        XCTAssertEqual(recorded.map { $0.kind }, [.harvest], "only the domain write is attempted; the completion is held")
        let entries = await queue.activeEntries()
        let taskEntry = entries.first { $0.item.kind == .taskComplete }
        XCTAssertEqual(taskEntry?.item.status.isPending, true, "linked completion remains pending while its domain write is unresolved")
    }
}

// MARK: - Producer: ShiftTasksViewModel enqueues task-complete on offline failure

@MainActor
final class ShiftTasksViewModelOfflineProducerTests: XCTestCase {
    private let taskID = "78200000-0000-0000-0000-000000000001"

    private func boundQueue() async -> OfflineWriteQueue {
        let queue = OfflineWriteQueue(
            store: InMemoryOfflineWriteQueueStore(),
            transport: ScriptedReplayTransport(fallback: .retryable),
            clock: MutableOfflineQueueClock(OfflineQueueFixtures.epoch)
        )
        await queue.bind(serverID: UUID(), userID: UUID())
        return queue
    }

    func testCompletionEnqueuesFrozenTaskCompleteOnOfflineFailure() async {
        let queue = await boundQueue()
        let service = ControllableTaskService(
            snapshot: taskSnapshot(taskID: taskID, status: .pending),
            completeErrors: [NetworkError.noConnection]
        )
        let vm = ShiftTasksViewModel()
        vm.configure(
            taskService: service,
            signalRService: MockSignalRService(),
            shiftPlanEnabled: true,
            offlineQueue: queue
        )

        await vm.perform(.complete, taskID: taskID)

        // The offline-class completion is durably queued, not surfaced as error.
        XCTAssertNil(vm.mutationActivities[taskID], "queued completion clears in-flight state without an error banner")
        let items = await queue.activeEntries()
        XCTAssertEqual(items.count, 1)
        guard case .taskComplete(let queuedTaskID, let key) = items.first?.item.operation else {
            return XCTFail("expected a queued task completion")
        }
        XCTAssertEqual(queuedTaskID, taskID)
        XCTAssertEqual(service.completeKeys.first, key, "the queued intent reuses the frozen idempotency key from the failed attempt")
    }

    func testTerminalCompletionFailureIsNotEnqueued() async {
        let queue = await boundQueue()
        let service = ControllableTaskService(
            snapshot: taskSnapshot(taskID: taskID, status: .pending),
            completeErrors: [NetworkError.clientError(422, nil)]
        )
        let vm = ShiftTasksViewModel()
        vm.configure(
            taskService: service,
            signalRService: MockSignalRService(),
            shiftPlanEnabled: true,
            offlineQueue: queue
        )

        await vm.perform(.complete, taskID: taskID)

        XCTAssertNotNil(vm.mutationActivities[taskID]?.failure, "a terminal rejection surfaces immediately")
        let items = await queue.activeEntries()
        XCTAssertTrue(items.isEmpty, "a terminal failure must never be queued")
    }

    func testSkipIsNeverEnqueued() async {
        let queue = await boundQueue()
        // ControllableTaskService.skip fails the test if ever called through the
        // replay path; here the ONLINE skip simply succeeds (no error), so the
        // producer has nothing to queue. Proves skip has no offline encoding.
        let service = SucceedingSkipTaskService()
        let vm = ShiftTasksViewModel()
        vm.configure(
            taskService: service,
            signalRService: MockSignalRService(),
            shiftPlanEnabled: true,
            offlineQueue: queue
        )

        await vm.perform(.skip, taskID: taskID)

        let items = await queue.activeEntries()
        XCTAssertTrue(items.isEmpty, "skip is never queued — it has no offline adapter")
    }
}

/// A task service whose `skip` succeeds online (so the producer path has nothing
/// to enqueue) while `complete`/`dismiss` are unused.
private final class SucceedingSkipTaskService: ShiftTaskServiceProtocol, @unchecked Sendable {
    func loadSnapshot(shiftPlanEnabled: Bool) async throws -> ShiftTaskSnapshot { makeEmptyShiftTaskSnapshot() }
    func complete(taskID: String, idempotencyKey: String) async throws {}
    func skip(taskID: String) async throws {}
    func dismiss(taskID: String) async throws {}
}
