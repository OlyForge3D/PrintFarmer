import XCTest
@testable import PrintFarmer

/// Issue #814 — deterministic proofs that the ShiftTasks refresh coalescer is
/// hardened against sustained supported invalidations.
///
/// The redesign coalesces invalidations at INGRESS: only the single
/// pass-initiating invalidation (and explicit refresh / retry / post-mutation
/// callers) parks a continuation. Every subsequent storm invalidation collapses
/// onto the already-scheduled work as fire-and-forget — it parks NO continuation
/// and spawns NO suspended task — so ingress tasks, parked waiters, scheduled
/// passes, and coalesced-demand entries all have a fixed, N-independent bound.
///
/// Callers register against the FIRST canonical pass whose `loadSnapshot` begins
/// AFTER their registration (reserved-vs-begun separation). Generations are a
/// total-ordered, overflow-safe `(epoch, sequence)` token. Continuations resolve
/// exactly once across success, failure, caller cancellation, reentrant
/// invalidation, teardown, and server replacement, with no stale publication.
///
/// Every barrier here is explicit (gated loads, callback-queue ACKs, a
/// waiter-registration barrier, a completion counter). There are no sleeps,
/// polling, retries, yields, `asyncAfter`, or elapsed-time criteria.
@MainActor
final class ShiftTasksCoalescerHardeningTests: XCTestCase {
    private let taskID = "78200000-0000-0000-0000-000000000001"

    /// Exact completion counter: each awaiting caller records its single result,
    /// so a test can assert the EXACT number of completions per terminal path
    /// (not merely the absence of a double-resume trap).
    private actor CompletionCounter {
        private(set) var results: [Bool] = []
        func record(_ value: Bool) { results.append(value) }
        var total: Int { results.count }
        var trueCount: Int { results.filter { $0 }.count }
        var falseCount: Int { results.filter { !$0 }.count }
        func snapshot() -> (total: Int, trueCount: Int, falseCount: Int) {
            (results.count, results.filter { $0 }.count, results.filter { !$0 }.count)
        }
    }

    // MARK: - Blocker 1: bounded ingress under a sustained one-per-GET storm

    /// A burst of N supported invalidations while a single GET is gated must not
    /// grow live ingress tasks, parked waiters, scheduled passes, or coalesced
    /// demand. All bounds stay constant regardless of N, and N+1 invalidations
    /// collapse to just two canonical passes.
    func testSustainedOnePerGetStormKeepsIngressBounded() async {
        let queue = ShiftTaskCallbackQueue()
        let barrier = RefreshWaiterRegistrationBarrier()
        let viewModel = ShiftTasksViewModel(callbackEnqueuer: queue.enqueuer)
        viewModel.refreshWaiterRegistrationObserver = barrier.signal

        let gate1 = ShiftTaskResultGate<ShiftTaskSnapshot>()
        let gate2 = ShiftTaskResultGate<ShiftTaskSnapshot>()
        let gate3 = ShiftTaskResultGate<ShiftTaskSnapshot>()
        let service = ScriptedShiftTaskService(
            loadSteps: [.gated(gate1), .gated(gate2), .gated(gate3)]
        )
        let signalR = MockSignalRService()
        viewModel.configure(
            taskService: service,
            signalRService: signalR,
            shiftPlanEnabled: true
        )

        // Pass-initiating invalidation: starts the owner and parks exactly one
        // continuation for generation 1.
        signalR.simulateTaskInvalidation(target: "taskupdated")
        await queue.waitForCount(1)
        let firstInvalidation = Task { await queue.runNext() }
        await barrier.waitForCount(1)
        await service.waitForLoadCount(1)

        XCTAssertEqual(viewModel.pendingRefreshWaiterCountForTesting, 1)
        XCTAssertEqual(viewModel.scheduledPassCountForTesting, 1)
        XCTAssertEqual(viewModel.coalescedDemandCountForTesting, 0)
        XCTAssertEqual(viewModel.runningGenerationSequenceForTesting, 1)
        XCTAssertNil(viewModel.pendingGenerationSequenceForTesting)

        // Sustained storm: one supported invalidation per gated GET. Each is
        // fire-and-forget, so `runNext()` returns without suspending.
        let stormCount = 8
        for _ in 0..<stormCount {
            signalR.simulateTaskInvalidation(target: "taskupdated")
            await queue.waitForCount(1)
            await queue.runNext()

            // N-INDEPENDENT BOUND: still exactly one parked waiter (gen 1), two
            // scheduled passes (gen 1 running + gen 2 pending), and a single
            // coalesced-demand entry — no growth with storm size.
            XCTAssertEqual(viewModel.pendingRefreshWaiterCountForTesting, 1)
            XCTAssertEqual(viewModel.scheduledPassCountForTesting, 2)
            XCTAssertEqual(viewModel.coalescedDemandCountForTesting, 1)
            XCTAssertEqual(viewModel.refreshWaiterTargetGenerationsForTesting, [1])
            XCTAssertEqual(viewModel.runningGenerationSequenceForTesting, 1)
            XCTAssertEqual(viewModel.pendingGenerationSequenceForTesting, 2)
        }

        // Release gen 1: its sole waiter resolves once and gen 2 is promoted.
        await gate1.succeed(makeShiftTaskSnapshot(title: "Pass 1"))
        await firstInvalidation.value
        await service.waitForLoadCount(2)

        XCTAssertEqual(viewModel.pendingRefreshWaiterCountForTesting, 0)
        XCTAssertEqual(viewModel.runningGenerationSequenceForTesting, 2)
        XCTAssertEqual(viewModel.scheduledPassCountForTesting, 1)

        // An explicit caller joins the tail so the storm's quiescence is
        // deterministically observable; it reserves gen 3.
        let tail = Task { await viewModel.refresh() }
        await barrier.waitForCount(2)
        await gate2.succeed(makeShiftTaskSnapshot(title: "Pass 2"))
        await service.waitForLoadCount(3)
        await gate3.succeed(makeEmptyShiftTaskSnapshot())
        _ = await tail.value

        // Nine invalidations collapsed to three canonical passes total (the third
        // exists only because the explicit tail caller demanded a fresh pass).
        XCTAssertEqual(viewModel.pendingRefreshWaiterCountForTesting, 0)
        XCTAssertEqual(viewModel.coalescedDemandCountForTesting, 0)
        XCTAssertEqual(viewModel.scheduledPassCountForTesting, 0)
        XCTAssertFalse(viewModel.isRefreshOwnerActiveForTesting)
        XCTAssertFalse(viewModel.isRefreshing)
        let loadCount = await service.loadCallCount
        XCTAssertEqual(loadCount, 3)
    }

    // MARK: - Blocker 1: mutation completes on FIRST covering pass, not quiescence

    /// An explicit mutation's post-write refresh must clear `isInFlight` as soon
    /// as the first pass covering ITS generation completes, even while the storm
    /// keeps the owner non-quiescent with a later in-flight pass.
    func testMutationCompletesOnFirstCoveringPassWhileStormContinues() async {
        let queue = ShiftTaskCallbackQueue()
        let barrier = RefreshWaiterRegistrationBarrier()
        let viewModel = ShiftTasksViewModel(callbackEnqueuer: queue.enqueuer)
        viewModel.refreshWaiterRegistrationObserver = barrier.signal

        let gate1 = ShiftTaskResultGate<ShiftTaskSnapshot>()
        let gate2 = ShiftTaskResultGate<ShiftTaskSnapshot>()
        let gate3 = ShiftTaskResultGate<ShiftTaskSnapshot>()
        let service = ScriptedShiftTaskService(
            loadSteps: [.gated(gate1), .gated(gate2), .gated(gate3)],
            mutationSteps: [.complete: [.success]]
        )
        let signalR = MockSignalRService()
        viewModel.configure(
            taskService: service,
            signalRService: signalR,
            shiftPlanEnabled: true
        )

        // Invalidation #1 occupies the owner with pass 1 (generation 1).
        signalR.simulateTaskInvalidation(target: "taskupdated")
        await queue.waitForCount(1)
        let firstInvalidation = Task { await queue.runNext() }
        await barrier.waitForCount(1)
        await service.waitForLoadCount(1)

        // The operator completes a task; its post-write refresh reserves gen 2.
        let mutation = Task { await viewModel.perform(.complete, taskID: taskID) }
        await barrier.waitForCount(2)
        XCTAssertEqual(viewModel.mutationActivity(for: taskID)?.isInFlight, true)
        XCTAssertEqual(viewModel.refreshWaiterTargetGenerationsForTesting, [1, 2])

        // Pass 1 completes; generation 2 (the mutation's covering pass) begins.
        await gate1.succeed(makeShiftTaskSnapshot(title: "Pass 1"))
        await firstInvalidation.value
        await service.waitForLoadCount(2)

        // The storm continues (fire-and-forget) AND an explicit tail caller joins,
        // both reserving generation 3, so the owner will NOT be quiescent when the
        // mutation's generation completes.
        signalR.simulateTaskInvalidation(target: "taskupdated")
        await queue.waitForCount(1)
        await queue.runNext()
        let tail = Task { await viewModel.refresh() }
        await barrier.waitForCount(3)
        XCTAssertEqual(viewModel.refreshWaiterTargetGenerationsForTesting, [2, 3])
        XCTAssertEqual(viewModel.coalescedDemandCountForTesting, 1)

        // Complete generation 2 — the FIRST canonical pass covering the mutation.
        await gate2.succeed(makeShiftTaskSnapshot(title: "Covering pass"))
        await mutation.value

        // The mutation cleared as soon as its covering pass landed, even though a
        // later generation is still in flight (global quiescence has NOT occurred).
        XCTAssertNil(viewModel.mutationActivity(for: taskID))
        XCTAssertTrue(viewModel.isRefreshOwnerActiveForTesting)
        XCTAssertTrue(viewModel.isRefreshing)
        XCTAssertEqual(viewModel.refreshWaiterTargetGenerationsForTesting, [3])
        XCTAssertEqual(viewModel.runningGenerationSequenceForTesting, 3)

        // Drain the trailing pass.
        await gate3.succeed(makeEmptyShiftTaskSnapshot())
        _ = await tail.value
        XCTAssertEqual(viewModel.pendingRefreshWaiterCountForTesting, 0)
        XCTAssertEqual(viewModel.coalescedDemandCountForTesting, 0)
        XCTAssertFalse(viewModel.isRefreshOwnerActiveForTesting)
    }

    // MARK: - Blocker 2: register against the FIRST pass that begins after you

    /// Two callers that register BEFORE the owner's first `loadSnapshot` begins
    /// must both be covered by that first pass (same generation). A caller that
    /// registers AFTER the load has begun must be covered by the next pass.
    func testCallersBeforeOwnerStartsShareFirstPassBegunAfterThem() async {
        let viewModel = ShiftTasksViewModel()
        let gate1 = ShiftTaskResultGate<ShiftTaskSnapshot>()
        let gate2 = ShiftTaskResultGate<ShiftTaskSnapshot>()
        let service = ScriptedShiftTaskService(
            loadSteps: [.gated(gate1), .gated(gate2)]
        )
        let signalR = MockSignalRService()
        viewModel.configure(
            taskService: service,
            signalRService: signalR,
            shiftPlanEnabled: true
        )

        // First caller finds the owner idle and starts it. The owner Task is
        // scheduled but has NOT begun its load yet (no await has happened).
        guard let first = viewModel.debugReserveCoveringGeneration() else {
            return XCTFail("expected an active authority")
        }
        XCTAssertTrue(first.didStartOwner)

        // Second caller registers in the SAME run-loop turn, still before the
        // owner's load begins: it must JOIN the first pass, not be pushed to the
        // next one.
        guard let second = viewModel.debugReserveCoveringGeneration() else {
            return XCTFail("expected an active authority")
        }
        XCTAssertFalse(second.didStartOwner)
        XCTAssertEqual(second.epoch, first.epoch)
        XCTAssertEqual(second.sequence, first.sequence)

        // Let the owner's first load actually begin.
        await service.waitForLoadCount(1)

        // A caller registering AFTER the load has begun cannot be covered by the
        // running pass; it reserves the next (pending) generation.
        guard let third = viewModel.debugReserveCoveringGeneration() else {
            return XCTFail("expected an active authority")
        }
        XCTAssertFalse(third.didStartOwner)
        XCTAssertTrue(
            (third.epoch, third.sequence) > (first.epoch, first.sequence),
            "post-begin caller must reserve a strictly later generation"
        )
        XCTAssertEqual(viewModel.pendingGenerationSequenceForTesting, third.sequence)

        // Drain deterministically (no continuations were parked by the debug seam).
        await gate1.succeed(makeShiftTaskSnapshot())
        await service.waitForLoadCount(2)
        await gate2.succeed(makeEmptyShiftTaskSnapshot())
    }

    // MARK: - Blocker 3: overflow-safe, totally-ordered generation token

    /// Generation ordering must remain a correct TOTAL order across the sequence
    /// saturation boundary: when `sequence` hits `UInt64.max`, allocation rolls
    /// into the next epoch and the new generation still compares strictly greater
    /// than the pre-rollover one. Proven via a test seam, not brute force.
    func testGenerationOrderingIsOverflowSafeAcrossSaturation() {
        let viewModel = ShiftTasksViewModel()

        // Normal monotonic allocation within an epoch.
        viewModel.debugSetGenerationCursor(epoch: 0, sequence: 0)
        let g1 = viewModel.debugAllocateGeneration()
        let g2 = viewModel.debugAllocateGeneration()
        XCTAssertEqual(g1.epoch, 0)
        XCTAssertEqual(g1.sequence, 1)
        XCTAssertEqual(g2.epoch, 0)
        XCTAssertEqual(g2.sequence, 2)
        XCTAssertTrue(
            viewModel.debugGenerationLess(
                lhsEpoch: g1.epoch, lhsSequence: g1.sequence,
                rhsEpoch: g2.epoch, rhsSequence: g2.sequence
            )
        )

        // Saturation boundary: sequence at UInt64.max rolls into the next epoch
        // instead of wrapping back to a smaller value.
        viewModel.debugSetGenerationCursor(epoch: 7, sequence: .max)
        let rolled = viewModel.debugAllocateGeneration()
        XCTAssertEqual(rolled.epoch, 8)
        XCTAssertEqual(rolled.sequence, 1)

        // Total ordering preserved across the rollover: pre-rollover max < rolled.
        XCTAssertTrue(
            viewModel.debugGenerationLess(
                lhsEpoch: 7, lhsSequence: .max,
                rhsEpoch: rolled.epoch, rhsSequence: rolled.sequence
            )
        )
        XCTAssertFalse(
            viewModel.debugGenerationLess(
                lhsEpoch: rolled.epoch, lhsSequence: rolled.sequence,
                rhsEpoch: 7, rhsSequence: .max
            )
        )

        // Allocation continues monotonically within the new epoch.
        let afterRoll = viewModel.debugAllocateGeneration()
        XCTAssertEqual(afterRoll.epoch, 8)
        XCTAssertEqual(afterRoll.sequence, 2)
        XCTAssertTrue(
            viewModel.debugGenerationLess(
                lhsEpoch: rolled.epoch, lhsSequence: rolled.sequence,
                rhsEpoch: afterRoll.epoch, rhsSequence: afterRoll.sequence
            )
        )
    }

    // MARK: - Blocker 4: exactly-once on success

    func testExplicitRefreshSuccessCompletesExactlyOnce() async {
        let viewModel = ShiftTasksViewModel()
        let counter = CompletionCounter()
        let service = ScriptedShiftTaskService(
            defaultSnapshot: makeShiftTaskSnapshot(title: "Loaded")
        )
        viewModel.configure(
            taskService: service,
            signalRService: MockSignalRService(),
            shiftPlanEnabled: true
        )

        await counter.record(await viewModel.refresh())

        let counts = await counter.snapshot()
        XCTAssertEqual(counts.total, 1)
        XCTAssertEqual(counts.trueCount, 1)
        XCTAssertEqual(viewModel.pendingRefreshWaiterCountForTesting, 0)
        XCTAssertFalse(viewModel.isRefreshOwnerActiveForTesting)
        XCTAssertEqual(
            viewModel.snapshot?.groups.first?.tasks.first?.title,
            "Loaded"
        )
    }

    // MARK: - Blocker 4: exactly-once on failure

    func testExplicitRefreshFailureCompletesExactlyOnce() async {
        let viewModel = ShiftTasksViewModel()
        let counter = CompletionCounter()
        let service = ScriptedShiftTaskService(
            loadSteps: [.failure(.forced("boom"))]
        )
        viewModel.configure(
            taskService: service,
            signalRService: MockSignalRService(),
            shiftPlanEnabled: true
        )

        await counter.record(await viewModel.refresh())

        let counts = await counter.snapshot()
        XCTAssertEqual(counts.total, 1)
        XCTAssertEqual(counts.falseCount, 1)
        XCTAssertEqual(viewModel.pendingRefreshWaiterCountForTesting, 0)
        XCTAssertFalse(viewModel.isRefreshOwnerActiveForTesting)
        XCTAssertNotNil(viewModel.loadFailure)
    }

    // MARK: - Blocker 4: exactly-once on caller cancellation (+ idle owner abandon)

    func testCallerCancellationCompletesExactlyOnceAndAbandonsOwner() async {
        let barrier = RefreshWaiterRegistrationBarrier()
        let viewModel = ShiftTasksViewModel()
        viewModel.refreshWaiterRegistrationObserver = barrier.signal
        let counter = CompletionCounter()

        let gate1 = ShiftTaskResultGate<ShiftTaskSnapshot>()
        let service = ScriptedShiftTaskService(loadSteps: [.gated(gate1)])
        viewModel.configure(
            taskService: service,
            signalRService: MockSignalRService(),
            shiftPlanEnabled: true
        )

        let refresh = Task { await counter.record(await viewModel.refresh()) }
        await barrier.waitForCount(1)
        await service.waitForLoadCount(1)
        XCTAssertEqual(viewModel.pendingRefreshWaiterCountForTesting, 1)

        // Cancel the sole caller: its continuation resolves once (false) and, with
        // no other waiter and no coalesced demand, the owner is abandoned.
        refresh.cancel()
        _ = await refresh.value

        let counts = await counter.snapshot()
        XCTAssertEqual(counts.total, 1)
        XCTAssertEqual(counts.falseCount, 1)
        XCTAssertEqual(viewModel.pendingRefreshWaiterCountForTesting, 0)
        XCTAssertFalse(viewModel.isRefreshOwnerActiveForTesting)

        // Resolving the abandoned in-flight GET must publish nothing (no stale
        // publication onto the retired owner, no second resume).
        await gate1.succeed(makeShiftTaskSnapshot(title: "Stale after cancel"))
        XCTAssertNil(viewModel.snapshot)
        XCTAssertEqual(viewModel.pendingRefreshWaiterCountForTesting, 0)
    }

    // MARK: - Blocker 4: exactly-once under reentrant invalidation during a pass

    func testReentrantInvalidationDuringPassResolvesWaiterExactlyOnce() async {
        let queue = ShiftTaskCallbackQueue()
        let barrier = RefreshWaiterRegistrationBarrier()
        let viewModel = ShiftTasksViewModel(callbackEnqueuer: queue.enqueuer)
        viewModel.refreshWaiterRegistrationObserver = barrier.signal
        let counter = CompletionCounter()

        let gate1 = ShiftTaskResultGate<ShiftTaskSnapshot>()
        let gate2 = ShiftTaskResultGate<ShiftTaskSnapshot>()
        let service = ScriptedShiftTaskService(
            loadSteps: [.gated(gate1), .gated(gate2)]
        )
        let signalR = MockSignalRService()
        viewModel.configure(
            taskService: service,
            signalRService: signalR,
            shiftPlanEnabled: true
        )

        // Explicit refresh starts generation 1 and parks a single waiter.
        let refresh = Task { await counter.record(await viewModel.refresh()) }
        await barrier.waitForCount(1)
        await service.waitForLoadCount(1)

        // A reentrant supported invalidation arrives mid-pass. It is fire-and-
        // forget: it records demand and reserves gen 2 without parking a waiter.
        signalR.simulateTaskInvalidation(target: "taskupdated")
        await queue.waitForCount(1)
        await queue.runNext()
        XCTAssertEqual(viewModel.coalescedDemandCountForTesting, 1)
        XCTAssertEqual(viewModel.refreshWaiterTargetGenerationsForTesting, [1])

        // Complete gen 1: the explicit waiter resolves exactly once (true).
        await gate1.succeed(makeShiftTaskSnapshot(title: "Pass 1"))
        _ = await refresh.value
        let counts = await counter.snapshot()
        XCTAssertEqual(counts.total, 1)
        XCTAssertEqual(counts.trueCount, 1)

        // The reentrant demand promoted gen 2, which now runs; drain it.
        await service.waitForLoadCount(2)
        await gate2.succeed(makeEmptyShiftTaskSnapshot())
        XCTAssertEqual(viewModel.pendingRefreshWaiterCountForTesting, 0)
    }

    // MARK: - Blocker 4: teardown resolves every continuation exactly once

    /// Teardown mid-storm must resolve every parked continuation exactly once and
    /// leave no residual, and the abandoned in-flight GET must not publish.
    func testTeardownResolvesEveryContinuationExactlyOnceWithoutStalePublication() async {
        let queue = ShiftTaskCallbackQueue()
        let barrier = RefreshWaiterRegistrationBarrier()
        let viewModel = ShiftTasksViewModel(callbackEnqueuer: queue.enqueuer)
        viewModel.refreshWaiterRegistrationObserver = barrier.signal
        let counter = CompletionCounter()

        let gate1 = ShiftTaskResultGate<ShiftTaskSnapshot>()
        let service = ScriptedShiftTaskService(loadSteps: [.gated(gate1)])
        let signalR = MockSignalRService()
        viewModel.configure(
            taskService: service,
            signalRService: signalR,
            shiftPlanEnabled: true
        )

        // Pass-initiating invalidation parks the gen-1 waiter.
        signalR.simulateTaskInvalidation(target: "taskupdated")
        await queue.waitForCount(1)
        let firstInvalidation = Task { await queue.runNext() }
        await barrier.waitForCount(1)
        await service.waitForLoadCount(1)

        // Two explicit refreshes join the pending generation (gen 2).
        let refreshA = Task { await counter.record(await viewModel.refresh()) }
        await barrier.waitForCount(2)
        let refreshB = Task { await counter.record(await viewModel.refresh()) }
        await barrier.waitForCount(3)
        XCTAssertEqual(viewModel.pendingRefreshWaiterCountForTesting, 3)
        XCTAssertEqual(viewModel.refreshWaiterTargetGenerationsForTesting, [1, 2, 2])

        // Tear down mid-storm.
        viewModel.deactivate()
        await firstInvalidation.value
        _ = await refreshA.value
        _ = await refreshB.value

        // Both explicit callers completed exactly once (false); the invalidation
        // waiter also resolved (a double-resume would trap the checked
        // continuation). No residual waiters or demand remain.
        let counts = await counter.snapshot()
        XCTAssertEqual(counts.total, 2)
        XCTAssertEqual(counts.falseCount, 2)
        XCTAssertEqual(viewModel.pendingRefreshWaiterCountForTesting, 0)
        XCTAssertEqual(viewModel.coalescedDemandCountForTesting, 0)
        XCTAssertFalse(viewModel.isRefreshOwnerActiveForTesting)
        XCTAssertFalse(viewModel.isRefreshing)
        XCTAssertEqual(viewModel.phase, .idle)
        XCTAssertNil(viewModel.snapshot)

        // Resolving the abandoned GET must not publish onto the torn-down authority.
        await gate1.succeed(makeShiftTaskSnapshot(title: "Stale after teardown"))
        XCTAssertNil(viewModel.snapshot)
        XCTAssertEqual(viewModel.phase, .idle)
        XCTAssertEqual(viewModel.pendingRefreshWaiterCountForTesting, 0)
    }

    // MARK: - Blocker 4: server replacement resolves once, no stale publication

    func testServerReplacementDuringStormResolvesWaitersOnceWithoutStalePublication() async {
        let queue = ShiftTaskCallbackQueue()
        let barrier = RefreshWaiterRegistrationBarrier()
        let viewModel = ShiftTasksViewModel(callbackEnqueuer: queue.enqueuer)
        viewModel.refreshWaiterRegistrationObserver = barrier.signal
        let counter = CompletionCounter()

        let oldPass = ShiftTaskResultGate<ShiftTaskSnapshot>()
        let oldService = ScriptedShiftTaskService(loadSteps: [.gated(oldPass)])
        let oldSignalR = MockSignalRService()
        viewModel.configure(
            taskService: oldService,
            signalRService: oldSignalR,
            shiftPlanEnabled: true
        )

        // Start pass 1 on the old server and pile explicit demand behind it.
        oldSignalR.simulateTaskInvalidation(target: "taskupdated")
        await queue.waitForCount(1)
        let firstInvalidation = Task { await queue.runNext() }
        await barrier.waitForCount(1)
        await oldService.waitForLoadCount(1)

        let oldRefresh = Task { await counter.record(await viewModel.refresh()) }
        await barrier.waitForCount(2)
        XCTAssertEqual(viewModel.pendingRefreshWaiterCountForTesting, 2)

        // Replace the server mid-storm. Old continuations resolve once (false).
        let currentService = ScriptedShiftTaskService(
            defaultSnapshot: makeShiftTaskSnapshot(title: "Replacement authority")
        )
        viewModel.configure(
            taskService: currentService,
            signalRService: MockSignalRService(),
            shiftPlanEnabled: true
        )

        await firstInvalidation.value
        _ = await oldRefresh.value
        let counts = await counter.snapshot()
        XCTAssertEqual(counts.total, 1)
        XCTAssertEqual(counts.falseCount, 1)
        XCTAssertEqual(viewModel.pendingRefreshWaiterCountForTesting, 0)
        XCTAssertEqual(viewModel.coalescedDemandCountForTesting, 0)

        // The replacement authority loads cleanly.
        let currentResult = await viewModel.refresh()
        XCTAssertTrue(currentResult)
        XCTAssertEqual(
            viewModel.snapshot?.groups.first?.tasks.first?.title,
            "Replacement authority"
        )

        // The old, abandoned GET now resolves — it must NOT publish onto the
        // current authority nor resume any continuation a second time.
        await oldPass.succeed(makeShiftTaskSnapshot(title: "Stale old authority"))
        XCTAssertEqual(
            viewModel.snapshot?.groups.first?.tasks.first?.title,
            "Replacement authority"
        )
        let oldLoadCount = await oldService.loadCallCount
        XCTAssertEqual(oldLoadCount, 1)
    }
}
