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

    // MARK: - Blocker 1: bounded ingress via the REAL coalescing mailbox

    /// PRODUCTION-PATH burst proof (scenario a). A synchronous burst of N supported
    /// invalidations delivered through the real `SignalREventHub` boundary BEFORE
    /// the MainActor drain runs must schedule EXACTLY ONE drain task and, once
    /// drained, produce exactly one canonical pass with one parked waiter and one
    /// load — regardless of N. The mailbox observes all N deposits but coalesces
    /// them to a single scheduled drain and a single unit of demand.
    func testBurstBeforeDrainSchedulesSingleDrainAndSingleLoad() async {
        let queue = ShiftTaskCallbackQueue()
        let barrier = RefreshWaiterRegistrationBarrier()
        let viewModel = ShiftTasksViewModel(callbackEnqueuer: queue.enqueuer)
        viewModel.refreshWaiterRegistrationObserver = barrier.signal

        let gate1 = ShiftTaskResultGate<ShiftTaskSnapshot>()
        let service = ScriptedShiftTaskService(loadSteps: [.gated(gate1)])
        let signalR = MockSignalRService()
        viewModel.configure(
            taskService: service,
            signalRService: signalR,
            shiftPlanEnabled: true
        )

        // Deliver N supported invalidations synchronously through the real hub
        // BEFORE any MainActor drain has run.
        let burst = 16
        for _ in 0..<burst {
            signalR.simulateTaskInvalidation(target: "taskupdated")
        }

        // N-INDEPENDENT SCHEDULING: the mailbox saw all N deposits but scheduled
        // exactly ONE drain hop task; no owner, waiter, pass, or load exists yet.
        XCTAssertEqual(viewModel.invalidationMailboxDepositCountForTesting, burst)
        XCTAssertEqual(viewModel.invalidationMailboxScheduleCountForTesting, 1)
        XCTAssertEqual(queue.count, 1)
        XCTAssertEqual(viewModel.pendingRefreshWaiterCountForTesting, 0)
        XCTAssertEqual(viewModel.scheduledPassCountForTesting, 0)
        let loadsBefore = await service.loadCallCount
        XCTAssertEqual(loadsBefore, 0)

        // Run the single drain: N coalesced deposits collapse to ONE
        // pass-initiating ingest — one waiter, one running pass, one load.
        let drain = Task { await queue.runNext() }
        await barrier.waitForCount(1)
        await service.waitForLoadCount(1)
        XCTAssertEqual(viewModel.pendingRefreshWaiterCountForTesting, 1)
        XCTAssertEqual(viewModel.scheduledPassCountForTesting, 1)
        XCTAssertEqual(viewModel.invalidationMailboxScheduleCountForTesting, 1)
        XCTAssertEqual(viewModel.refreshWaiterTargetGenerationsForTesting, [1])

        // Completing the pass drains the last demand and retires the owner.
        await gate1.succeed(makeShiftTaskSnapshot(title: "Burst pass"))
        await drain.value
        let loads = await service.loadCallCount
        XCTAssertEqual(loads, 1)
        XCTAssertEqual(viewModel.invalidationMailboxScheduleCountForTesting, 1)
        XCTAssertEqual(viewModel.pendingRefreshWaiterCountForTesting, 0)
        XCTAssertFalse(viewModel.isRefreshOwnerActiveForTesting)
    }

    /// Sustained one-per-GET stream proof (scenario b). While a single GET is gated,
    /// a sustained stream of supported invalidations must add NO new drain task, NO
    /// new waiter, and NO new pass — only bounded mailbox demand. When the gated
    /// pass completes, the SAME single drain services all coalesced demand as
    /// exactly one additional pass. N+1 invalidations therefore collapse to a fixed
    /// two canonical loads and a single drain task, independent of N.
    func testSustainedOnePerGetStreamCoalescesToOneDrainAndBoundedPasses() async {
        let queue = ShiftTaskCallbackQueue()
        let barrier = RefreshWaiterRegistrationBarrier()
        let viewModel = ShiftTasksViewModel(callbackEnqueuer: queue.enqueuer)
        viewModel.refreshWaiterRegistrationObserver = barrier.signal

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

        // Pass-initiating invalidation: one drain task, gen 1 running and gated.
        signalR.simulateTaskInvalidation(target: "taskupdated")
        await queue.waitForCount(1)
        let drain = Task { await queue.runNext() }
        await barrier.waitForCount(1)
        await service.waitForLoadCount(1)
        XCTAssertEqual(viewModel.invalidationMailboxScheduleCountForTesting, 1)
        XCTAssertEqual(viewModel.pendingRefreshWaiterCountForTesting, 1)
        XCTAssertEqual(viewModel.scheduledPassCountForTesting, 1)

        // Sustained storm while gen 1 is gated: each invalidation only raises
        // bounded mailbox demand — no new drain hop, no new waiter, no new pass.
        let storm = 12
        for index in 1...storm {
            signalR.simulateTaskInvalidation(target: "taskupdated")
            XCTAssertEqual(viewModel.invalidationMailboxScheduleCountForTesting, 1)
            XCTAssertEqual(
                viewModel.invalidationMailboxDepositCountForTesting,
                1 + index
            )
            XCTAssertEqual(queue.count, 0)
            XCTAssertEqual(viewModel.pendingRefreshWaiterCountForTesting, 1)
            XCTAssertEqual(viewModel.scheduledPassCountForTesting, 1)
            XCTAssertEqual(viewModel.refreshWaiterTargetGenerationsForTesting, [1])
        }

        // Complete gen 1: the parked pass-initiating waiter resolves once and the
        // SAME drain services all coalesced storm demand as exactly ONE more pass.
        await gate1.succeed(makeShiftTaskSnapshot(title: "Pass 1"))
        await barrier.waitForCount(2)
        await service.waitForLoadCount(2)
        XCTAssertEqual(viewModel.invalidationMailboxScheduleCountForTesting, 1)
        XCTAssertEqual(viewModel.pendingRefreshWaiterCountForTesting, 1)
        XCTAssertEqual(viewModel.runningGenerationSequenceForTesting, 2)

        // Complete gen 2: the drain finds no further demand and exits.
        await gate2.succeed(makeEmptyShiftTaskSnapshot())
        await drain.value
        let loads = await service.loadCallCount
        XCTAssertEqual(loads, 2) // 13 invalidations → 2 canonical passes
        XCTAssertEqual(viewModel.invalidationMailboxScheduleCountForTesting, 1)
        XCTAssertEqual(viewModel.pendingRefreshWaiterCountForTesting, 0)
        XCTAssertEqual(viewModel.coalescedDemandCountForTesting, 0)
        XCTAssertFalse(viewModel.isRefreshOwnerActiveForTesting)
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

        // Invalidation #1 occupies the owner with pass 1 (generation 1) via the
        // real mailbox drain.
        signalR.simulateTaskInvalidation(target: "taskupdated")
        await queue.waitForCount(1)
        let drain = Task { await queue.runNext() }
        await barrier.waitForCount(1)
        await service.waitForLoadCount(1)

        // The operator completes a task; its post-write refresh reserves gen 2.
        let mutation = Task { await viewModel.perform(.complete, taskID: taskID) }
        await barrier.waitForCount(2)
        XCTAssertEqual(viewModel.mutationActivity(for: taskID)?.isInFlight, true)
        XCTAssertEqual(viewModel.refreshWaiterTargetGenerationsForTesting, [1, 2])

        // A sustained storm keeps arriving while gen 1 is gated. It raises only
        // bounded mailbox demand — no new drain hop, no new waiter.
        signalR.simulateTaskInvalidation(target: "taskupdated")
        signalR.simulateTaskInvalidation(target: "taskupdated")
        XCTAssertEqual(viewModel.invalidationMailboxScheduleCountForTesting, 1)
        XCTAssertEqual(viewModel.pendingRefreshWaiterCountForTesting, 2)
        XCTAssertEqual(viewModel.refreshWaiterTargetGenerationsForTesting, [1, 2])

        // Pass 1 completes; generation 2 (the mutation's covering pass) begins.
        await gate1.succeed(makeShiftTaskSnapshot(title: "Pass 1"))
        await service.waitForLoadCount(2)

        // An explicit tail caller joins, reserving generation 3, so the owner will
        // NOT be quiescent when the mutation's generation completes.
        let tail = Task { await viewModel.refresh() }
        await barrier.waitForCount(3)
        XCTAssertEqual(viewModel.refreshWaiterTargetGenerationsForTesting, [2, 3])

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
        await drain.value
        XCTAssertEqual(viewModel.pendingRefreshWaiterCountForTesting, 0)
        XCTAssertEqual(viewModel.coalescedDemandCountForTesting, 0)
        XCTAssertFalse(viewModel.isRefreshOwnerActiveForTesting)
    }

    // MARK: - Blocker 2: register against the FIRST pass that begins after you

    /// Two REAL request callers that register BEFORE the owner's first
    /// `loadSnapshot` begins must both be covered by that first pass (same shared
    /// generation completion). A caller that registers AFTER the load has begun is
    /// covered by the next pass. Proven with real `refresh()` callers held in the
    /// reserved-but-not-begun window by the owner-begin hook — no debug reservation.
    func testTwoRealCallersBeforeOwnerBeginShareFirstPassPostBeginJoinsSecond() async {
        let barrier = RefreshWaiterRegistrationBarrier()
        let viewModel = ShiftTasksViewModel()
        viewModel.refreshWaiterRegistrationObserver = barrier.signal
        let counter = CompletionCounter()

        let beginGate = ShiftTaskResultGate<Void>()
        viewModel.ownerPassWillBeginHook = { _ = try? await beginGate.wait() }

        let gate1 = ShiftTaskResultGate<ShiftTaskSnapshot>()
        let gate2 = ShiftTaskResultGate<ShiftTaskSnapshot>()
        let service = ScriptedShiftTaskService(
            loadSteps: [.gated(gate1), .gated(gate2)]
        )
        viewModel.configure(
            taskService: service,
            signalRService: MockSignalRService(),
            shiftPlanEnabled: true
        )

        // Caller A starts the owner; the pass is held in the reserved-but-not-begun
        // window by the hook.
        let callerA = Task { await counter.record(await viewModel.refresh()) }
        await barrier.waitForCount(1)

        // Caller B registers while the owner is reserved but has NOT begun its load:
        // it must JOIN the first pass (same generation), not be pushed to pass 2.
        let callerB = Task { await counter.record(await viewModel.refresh()) }
        await barrier.waitForCount(2)

        XCTAssertEqual(viewModel.liveGenerationCompletionCountForTesting, 1)
        XCTAssertEqual(viewModel.refreshWaiterTargetGenerationsForTesting, [1, 1])
        XCTAssertNil(viewModel.pendingGenerationSequenceForTesting)
        let loadsBeforeBegin = await service.loadCallCount
        XCTAssertEqual(loadsBeforeBegin, 0) // pass has NOT begun yet

        // Release the pass: it begins and issues its single load.
        await beginGate.succeed(())
        await service.waitForLoadCount(1)

        // A caller registering AFTER the load began reserves the NEXT pass (gen 2).
        let callerC = Task { await counter.record(await viewModel.refresh()) }
        await barrier.waitForCount(3)
        XCTAssertEqual(viewModel.pendingGenerationSequenceForTesting, 2)
        XCTAssertEqual(viewModel.refreshWaiterTargetGenerationsForTesting, [1, 1, 2])

        // Complete pass 1: BOTH pre-begin callers resolve from it, exactly once.
        await gate1.succeed(makeShiftTaskSnapshot(title: "Pass 1"))
        _ = await callerA.value
        _ = await callerB.value
        let afterFirst = await counter.snapshot()
        XCTAssertEqual(afterFirst.total, 2)
        XCTAssertEqual(afterFirst.trueCount, 2)

        // Pass 2 covers the post-begin caller.
        await service.waitForLoadCount(2)
        await gate2.succeed(makeEmptyShiftTaskSnapshot())
        _ = await callerC.value
        let final = await counter.snapshot()
        XCTAssertEqual(final.total, 3)
        XCTAssertEqual(final.trueCount, 3)
        let loads = await service.loadCallCount
        XCTAssertEqual(loads, 2) // only two passes for three callers
    }

    // MARK: - Blocker 2: N explicit callers share ONE generation completion

    /// N explicit callers targeting one gated generation must add NO N-dependent
    /// coalescer state: exactly ONE shared generation completion, ONE load, and one
    /// shared result fanned out to all callers. Cancelling one caller returns
    /// `false` for it alone, without disturbing the shared result its peers receive.
    func testManyExplicitCallersShareOneGenerationCompletion() async {
        let barrier = RefreshWaiterRegistrationBarrier()
        let viewModel = ShiftTasksViewModel()
        viewModel.refreshWaiterRegistrationObserver = barrier.signal
        let counter = CompletionCounter()

        let beginGate = ShiftTaskResultGate<Void>()
        viewModel.ownerPassWillBeginHook = { _ = try? await beginGate.wait() }

        let gate1 = ShiftTaskResultGate<ShiftTaskSnapshot>()
        let service = ScriptedShiftTaskService(loadSteps: [.gated(gate1)])
        viewModel.configure(
            taskService: service,
            signalRService: MockSignalRService(),
            shiftPlanEnabled: true
        )

        // N explicit callers all register before the pass begins → all share gen 1.
        let callerCount = 20
        var callers: [Task<Void, Never>] = []
        for _ in 0..<callerCount {
            callers.append(Task { await counter.record(await viewModel.refresh()) })
        }
        await barrier.waitForCount(callerCount)

        // O(1) coalescer state: ONE generation completion holding N awaiters —
        // NOT N distinct coalescer entries.
        XCTAssertEqual(viewModel.liveGenerationCompletionCountForTesting, 1)
        XCTAssertEqual(viewModel.pendingRefreshWaiterCountForTesting, callerCount)
        XCTAssertNil(viewModel.pendingGenerationSequenceForTesting)

        // Cancel exactly ONE caller: it resolves false without disturbing peers or
        // the shared generation state.
        callers[0].cancel()
        _ = await callers[0].value
        let afterCancel = await counter.snapshot()
        XCTAssertEqual(afterCancel.total, 1)
        XCTAssertEqual(afterCancel.falseCount, 1)
        XCTAssertEqual(viewModel.pendingRefreshWaiterCountForTesting, callerCount - 1)
        XCTAssertEqual(viewModel.liveGenerationCompletionCountForTesting, 1)

        // Release + complete the single pass: the remaining N-1 peers all receive
        // the SAME success exactly once, from ONE load.
        await beginGate.succeed(())
        await service.waitForLoadCount(1)
        await gate1.succeed(makeShiftTaskSnapshot(title: "Shared pass"))
        for caller in callers.dropFirst() {
            _ = await caller.value
        }

        let final = await counter.snapshot()
        XCTAssertEqual(final.total, callerCount)
        XCTAssertEqual(final.trueCount, callerCount - 1)
        XCTAssertEqual(final.falseCount, 1)
        let loads = await service.loadCallCount
        XCTAssertEqual(loads, 1) // ONE load served all N callers
        XCTAssertEqual(viewModel.pendingRefreshWaiterCountForTesting, 0)
        XCTAssertEqual(viewModel.liveGenerationCompletionCountForTesting, 0)
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

    // MARK: - Blocker 3: renormalization through the REAL allocation path

    /// When the generation cursor fully saturates `(UInt64.max, UInt64.max)` the
    /// coalescer renormalizes its bounded LIVE set (running + pending +
    /// last-completed + shared completions) onto a compact low range through the
    /// REAL allocation path — no trap, no wrap. Relative order is preserved and
    /// every parked waiter stays bound to its (remapped) generation and resolves
    /// exactly once. Proven with real refresh callers parked across the rollover,
    /// not a debug comparator.
    func testRenormalizationAtSaturationPreservesOrderingAndWaiters() async {
        let barrier = RefreshWaiterRegistrationBarrier()
        let viewModel = ShiftTasksViewModel()
        viewModel.refreshWaiterRegistrationObserver = barrier.signal
        let counter = CompletionCounter()

        let gate1 = ShiftTaskResultGate<ShiftTaskSnapshot>()
        let gate2 = ShiftTaskResultGate<ShiftTaskSnapshot>()
        let gate3 = ShiftTaskResultGate<ShiftTaskSnapshot>()
        let service = ScriptedShiftTaskService(
            loadSteps: [.gated(gate1), .gated(gate2), .gated(gate3)]
        )
        viewModel.configure(
            taskService: service,
            signalRService: MockSignalRService(),
            shiftPlanEnabled: true
        )

        // Position the cursor two below full saturation so the THIRD real
        // allocation lands exactly on `(max, max)` and must renormalize.
        viewModel.debugSetGenerationCursor(epoch: .max, sequence: .max - 2)

        // Caller 1 starts the owner: generation (max, max-1) begins its load.
        let callerA = Task { await counter.record(await viewModel.refresh()) }
        await barrier.waitForCount(1)
        await service.waitForLoadCount(1)
        XCTAssertEqual(viewModel.runningGenerationTokenForTesting?.epoch, .max)
        XCTAssertEqual(viewModel.runningGenerationTokenForTesting?.sequence, .max - 1)

        // Caller 2 registers after the load began → pending generation (max, max).
        let callerB = Task { await counter.record(await viewModel.refresh()) }
        await barrier.waitForCount(2)
        XCTAssertEqual(viewModel.pendingGenerationTokenForTesting?.epoch, .max)
        XCTAssertEqual(viewModel.pendingGenerationTokenForTesting?.sequence, .max)

        // Complete pass 1: caller A resolves; the pending pass (max, max) is promoted
        // to running and begins its own load. Last-completed is now (max, max-1).
        await gate1.succeed(makeShiftTaskSnapshot(title: "Pass 1"))
        _ = await callerA.value
        await service.waitForLoadCount(2)

        // Caller 3 registers after pass 2 began → the reservation allocates the next
        // generation, which saturates the cursor and triggers renormalization. The
        // live set {last-completed (max,max-1), running (max,max)} remaps to
        // {(0,1), (0,2)} preserving order; the new pending becomes (0,3).
        let callerC = Task { await counter.record(await viewModel.refresh()) }
        await barrier.waitForCount(3)

        XCTAssertEqual(viewModel.runningGenerationTokenForTesting?.epoch, 0)
        XCTAssertEqual(viewModel.runningGenerationTokenForTesting?.sequence, 2)
        XCTAssertEqual(viewModel.pendingGenerationTokenForTesting?.epoch, 0)
        XCTAssertEqual(viewModel.pendingGenerationTokenForTesting?.sequence, 3)
        // Total ordering intact after renormalization: running < pending.
        XCTAssertTrue(
            viewModel.debugGenerationLess(
                lhsEpoch: 0, lhsSequence: 2,
                rhsEpoch: 0, rhsSequence: 3
            )
        )
        // Both parked callers' completions survived the key remap.
        let liveSequences = viewModel.liveGenerationCompletionTokensForTesting
            .map { $0.sequence }
            .sorted()
        XCTAssertEqual(liveSequences, [2, 3])
        XCTAssertEqual(viewModel.pendingRefreshWaiterCountForTesting, 2)

        // Complete pass 2 (running (0,2)): caller B — parked before the rollover —
        // resolves exactly once from its remapped completion. Pass 3 promotes.
        await gate2.succeed(makeShiftTaskSnapshot(title: "Pass 2"))
        _ = await callerB.value
        await service.waitForLoadCount(3)
        XCTAssertEqual(viewModel.runningGenerationTokenForTesting?.sequence, 3)

        // Complete pass 3: caller C resolves once. Every caller completed exactly
        // once with three real loads, none lost or double-resumed across the rollover.
        await gate3.succeed(makeEmptyShiftTaskSnapshot())
        _ = await callerC.value

        let final = await counter.snapshot()
        XCTAssertEqual(final.total, 3)
        XCTAssertEqual(final.trueCount, 3)
        let loads = await service.loadCallCount
        XCTAssertEqual(loads, 3)
        XCTAssertEqual(viewModel.pendingRefreshWaiterCountForTesting, 0)
        XCTAssertEqual(viewModel.liveGenerationCompletionCountForTesting, 0)
        XCTAssertFalse(viewModel.isRefreshOwnerActiveForTesting)
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

    // MARK: - Blocker 4: pass-initiating invalidation completes exactly once

    /// The single pass-initiating invalidation (the one that finds the owner idle
    /// and parks) must itself resolve EXACTLY ONCE — not only the explicit callers.
    /// Counted through the real mailbox drain via the invalidation-completion
    /// observer, proving the awaited invalidation path is exactly-once too.
    func testPassInitiatingInvalidationCompletesExactlyOnce() async {
        let queue = ShiftTaskCallbackQueue()
        let barrier = RefreshWaiterRegistrationBarrier()
        let viewModel = ShiftTasksViewModel(callbackEnqueuer: queue.enqueuer)
        viewModel.refreshWaiterRegistrationObserver = barrier.signal
        let log = MainActorResultLog()
        viewModel.invalidationCompletionObserver = { result in log.record(result) }

        let gate = ShiftTaskResultGate<ShiftTaskSnapshot>()
        let service = ScriptedShiftTaskService(loadSteps: [.gated(gate)])
        let signalR = MockSignalRService()
        viewModel.configure(
            taskService: service,
            signalRService: signalR,
            shiftPlanEnabled: true
        )

        // One invalidation initiates a pass and parks its awaiting drain.
        signalR.simulateTaskInvalidation(target: "taskupdated")
        await queue.waitForCount(1)
        let drain = Task { await queue.runNext() }
        await barrier.waitForCount(1)
        await service.waitForLoadCount(1)
        XCTAssertEqual(log.count, 0) // not resolved until the pass completes
        XCTAssertEqual(viewModel.pendingRefreshWaiterCountForTesting, 1)

        // Completing the pass resolves the parked invalidation exactly once.
        await gate.succeed(makeEmptyShiftTaskSnapshot())
        await drain.value

        XCTAssertEqual(log.results, [true])
        XCTAssertEqual(viewModel.pendingRefreshWaiterCountForTesting, 0)
        XCTAssertFalse(viewModel.isRefreshOwnerActiveForTesting)
    }

    // MARK: - Blocker 4: cancellation BEFORE registration resolves false, no park

    /// A caller cancelled before its continuation registers must resolve `false`
    /// immediately WITHOUT parking a waiter. Deterministic: the cancel flag is set
    /// in the current synchronous span before the task body runs, so
    /// `awaitGeneration`'s cancellation guard fires before any continuation parks.
    func testCallerCancelledBeforeRegistrationCompletesFalseWithoutParking() async {
        let viewModel = ShiftTasksViewModel()
        let counter = CompletionCounter()

        let service = ScriptedShiftTaskService(
            defaultSnapshot: makeEmptyShiftTaskSnapshot()
        )
        viewModel.configure(
            taskService: service,
            signalRService: MockSignalRService(),
            shiftPlanEnabled: true
        )

        let caller = Task { await counter.record(await viewModel.refresh()) }
        caller.cancel()
        _ = await caller.value

        let counts = await counter.snapshot()
        XCTAssertEqual(counts.total, 1)
        XCTAssertEqual(counts.falseCount, 1)
        // No waiter ever parked for the cancelled caller.
        XCTAssertEqual(viewModel.pendingRefreshWaiterCountForTesting, 0)

        // A clean subsequent refresh still succeeds and reaches quiescence,
        // confirming the cancelled caller left the coalescer in a good state.
        let ok = await viewModel.refresh()
        XCTAssertTrue(ok)
        XCTAssertEqual(viewModel.pendingRefreshWaiterCountForTesting, 0)
        XCTAssertEqual(viewModel.liveGenerationCompletionCountForTesting, 0)
        XCTAssertFalse(viewModel.isRefreshOwnerActiveForTesting)
    }

    // MARK: - Blocker 4: real deallocation after a gated refresh (no retain cycle)

    /// A gated in-flight service call must not retain the ViewModel forever after
    /// the last external strong reference drops. With only the suspended caller
    /// task holding it, the VM stays alive; once the gate releases and the pass
    /// completes, the owner and caller both release it and it deallocates — proven
    /// with a weak probe and NO polling/sleep.
    func testViewModelDeallocatesAfterGatedRefreshResolvesWithoutRetainCycle() async {
        weak var weakViewModel: ShiftTasksViewModel?
        let barrier = RefreshWaiterRegistrationBarrier()
        let gate = ShiftTaskResultGate<ShiftTaskSnapshot>()
        let service = ScriptedShiftTaskService(loadSteps: [.gated(gate)])
        let caller: Task<Bool, Never>

        do {
            let viewModel = ShiftTasksViewModel()
            viewModel.refreshWaiterRegistrationObserver = barrier.signal
            weakViewModel = viewModel
            viewModel.configure(
                taskService: service,
                signalRService: MockSignalRService(),
                shiftPlanEnabled: true
            )
            // Only this task captures the VM strongly once the do-scope exits.
            caller = Task { await viewModel.refresh() }
            await barrier.waitForCount(1)
            await service.waitForLoadCount(1)
        }

        // The suspended caller (and the in-flight owner) keep the VM alive.
        XCTAssertNotNil(weakViewModel)

        // Release the gate: the pass completes, the owner retires and drops its
        // self-reference, and the caller resumes and releases its capture.
        await gate.succeed(makeEmptyShiftTaskSnapshot())
        _ = await caller.value

        // With every strong reference gone, the VM deallocates deterministically.
        XCTAssertNil(weakViewModel)
    }
}

/// MainActor-isolated result log for the invalidation-completion observer. Being
/// global-actor isolated it is implicitly `Sendable`, so it can be captured by the
/// `@MainActor @Sendable` observer and read synchronously on the MainActor.
@MainActor
private final class MainActorResultLog {
    private(set) var results: [Bool] = []
    func record(_ value: Bool) { results.append(value) }
    var count: Int { results.count }
}
