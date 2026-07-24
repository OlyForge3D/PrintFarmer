import XCTest
@testable import PrintFarmer

/// Issue #814 — deterministic proofs that the ShiftTasks refresh coalescer is
/// hardened against sustained supported invalidations: bounded, generation-
/// tagged coalescing; explicit callers resolve after the first pass covering
/// THEIR generation (not global quiescence); continuations resolve exactly once
/// across teardown and server replacement; and no stale publication.
///
/// Every barrier here is explicit (gated loads, callback-queue ACKs, a waiter-
/// registration barrier). There are no sleeps, polling, retries, or elapsed-time
/// criteria.
@MainActor
final class ShiftTasksCoalescerHardeningTests: XCTestCase {
    private let taskID = "78200000-0000-0000-0000-000000000001"

    // MARK: - Sustained storm: bounded waiters + generation-tagged coalescing

    func testSustainedOnePerGetStormKeepsWaitersBoundedAndGenerationTagged() async {
        let queue = ShiftTaskCallbackQueue()
        let barrier = RefreshWaiterRegistrationBarrier()
        let viewModel = ShiftTasksViewModel(callbackEnqueuer: queue.enqueuer)
        viewModel.refreshWaiterRegistrationObserver = barrier.signal

        let passCount = 6
        let gates = (0..<passCount).map { _ in ShiftTaskResultGate<ShiftTaskSnapshot>() }
        let service = ScriptedShiftTaskService(
            loadSteps: gates.map { ShiftTaskLoadStep.gated($0) }
        )
        let signalR = MockSignalRService()
        viewModel.configure(
            taskService: service,
            signalRService: signalR,
            shiftPlanEnabled: true
        )

        var invalidationTasks: [Task<Void, Never>] = []
        var registrations = 0
        var maxWaiters = 0

        func deliverInvalidation() async {
            signalR.simulateTaskInvalidation(target: "taskupdated")
            await queue.waitForCount(1)
            invalidationTasks.append(Task { await queue.runNext() })
            registrations += 1
            await barrier.waitForCount(registrations)
            maxWaiters = max(maxWaiters, viewModel.pendingRefreshWaiterCountForTesting)
        }

        // The first supported invalidation starts canonical pass 1 (generation 1).
        await deliverInvalidation()
        await service.waitForLoadCount(1)
        XCTAssertEqual(viewModel.refreshWaiterTargetGenerationsForTesting, [1])
        XCTAssertTrue(viewModel.isRefreshing)

        // Sustained storm: exactly one supported invalidation per in-flight GET.
        for pass in 1..<passCount {
            // A fresh invalidation arrives while pass `pass` is still gated.
            await deliverInvalidation()

            // Bounded: the in-flight generation's waiter plus at most one queued
            // pending generation — never one accumulating waiter per event.
            XCTAssertLessThanOrEqual(viewModel.pendingRefreshWaiterCountForTesting, 2)
            let generations = viewModel.refreshWaiterTargetGenerationsForTesting
            XCTAssertEqual(generations, [UInt64(pass), UInt64(pass + 1)])

            // Release the in-flight GET; its generation completes and the queued
            // pending generation is promoted into the next canonical pass.
            await gates[pass - 1].succeed(
                makeShiftTaskSnapshot(title: "Pass \(pass)")
            )
            await service.waitForLoadCount(pass + 1)
        }

        // Drain the final in-flight pass; the storm quiesces with no residual
        // waiters and the owner retires.
        await gates[passCount - 1].succeed(makeEmptyShiftTaskSnapshot())
        for task in invalidationTasks { await task.value }

        XCTAssertLessThanOrEqual(maxWaiters, 2)
        XCTAssertEqual(viewModel.pendingRefreshWaiterCountForTesting, 0)
        XCTAssertFalse(viewModel.isRefreshOwnerActiveForTesting)
        XCTAssertFalse(viewModel.isRefreshing)
        let loadCount = await service.loadCallCount
        XCTAssertEqual(loadCount, passCount)
    }

    // MARK: - Mutation completes on first covering pass, not global quiescence

    func testMutationCompletesOnFirstCoveringPassWhileStormContinues() async {
        let queue = ShiftTaskCallbackQueue()
        let barrier = RefreshWaiterRegistrationBarrier()
        let viewModel = ShiftTasksViewModel(callbackEnqueuer: queue.enqueuer)
        viewModel.refreshWaiterRegistrationObserver = barrier.signal

        let firstPass = ShiftTaskResultGate<ShiftTaskSnapshot>()
        let coveringPass = ShiftTaskResultGate<ShiftTaskSnapshot>()
        let stormPass = ShiftTaskResultGate<ShiftTaskSnapshot>()
        let service = ScriptedShiftTaskService(
            loadSteps: [.gated(firstPass), .gated(coveringPass), .gated(stormPass)],
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

        // The operator completes a task. Its post-write canonical refresh reserves
        // the next generation (2) and parks a waiter for it.
        let mutation = Task { await viewModel.perform(.complete, taskID: taskID) }
        await barrier.waitForCount(2)
        XCTAssertEqual(viewModel.mutationActivity(for: taskID)?.isInFlight, true)
        XCTAssertEqual(
            viewModel.refreshWaiterTargetGenerationsForTesting,
            [1, 2]
        )

        // Pass 1 completes; generation 2 (the mutation's covering pass) begins.
        await firstPass.succeed(makeShiftTaskSnapshot(title: "Pass 1"))
        await firstInvalidation.value
        await service.waitForLoadCount(2)

        // The storm continues: a new invalidation arrives during the covering
        // pass and reserves generation 3, so the owner will NOT be quiescent when
        // the mutation's generation completes.
        signalR.simulateTaskInvalidation(target: "taskupdated")
        await queue.waitForCount(1)
        let stormInvalidation = Task { await queue.runNext() }
        await barrier.waitForCount(3)
        XCTAssertEqual(
            viewModel.refreshWaiterTargetGenerationsForTesting,
            [2, 3]
        )

        // Complete generation 2 — the FIRST canonical pass covering the mutation.
        await coveringPass.succeed(makeShiftTaskSnapshot(title: "Covering pass"))
        await mutation.value

        // The mutation cleared as soon as its covering pass landed, even though a
        // later generation is still in flight (global quiescence has NOT occurred).
        XCTAssertNil(viewModel.mutationActivity(for: taskID))
        XCTAssertTrue(viewModel.isRefreshOwnerActiveForTesting)
        XCTAssertTrue(viewModel.isRefreshing)
        XCTAssertEqual(viewModel.refreshWaiterTargetGenerationsForTesting, [3])

        // Drain the trailing storm pass.
        await stormPass.succeed(makeEmptyShiftTaskSnapshot())
        await stormInvalidation.value
        XCTAssertEqual(viewModel.pendingRefreshWaiterCountForTesting, 0)
        XCTAssertFalse(viewModel.isRefreshOwnerActiveForTesting)
    }

    // MARK: - Teardown resolves every parked continuation exactly once

    func testTeardownResolvesAllStormContinuationsExactlyOnce() async {
        let queue = ShiftTaskCallbackQueue()
        let barrier = RefreshWaiterRegistrationBarrier()
        let viewModel = ShiftTasksViewModel(callbackEnqueuer: queue.enqueuer)
        viewModel.refreshWaiterRegistrationObserver = barrier.signal

        let firstPass = ShiftTaskResultGate<ShiftTaskSnapshot>()
        let service = ScriptedShiftTaskService(loadSteps: [.gated(firstPass)])
        let signalR = MockSignalRService()
        viewModel.configure(
            taskService: service,
            signalRService: signalR,
            shiftPlanEnabled: true
        )

        // Park a mix of waiter kinds across two generations.
        signalR.simulateTaskInvalidation(target: "taskupdated")
        await queue.waitForCount(1)
        let firstInvalidation = Task { await queue.runNext() }
        await barrier.waitForCount(1)
        await service.waitForLoadCount(1)

        signalR.simulateTaskInvalidation(target: "taskupdated")
        await queue.waitForCount(1)
        let secondInvalidation = Task { await queue.runNext() }
        await barrier.waitForCount(2)

        let explicitRefresh = Task { await viewModel.refresh() }
        await barrier.waitForCount(3)
        XCTAssertEqual(viewModel.pendingRefreshWaiterCountForTesting, 3)

        // Tear down mid-storm: every parked continuation must resolve once.
        viewModel.deactivate()

        await firstInvalidation.value
        await secondInvalidation.value
        let refreshResult = await explicitRefresh.value

        XCTAssertFalse(refreshResult)
        XCTAssertEqual(viewModel.pendingRefreshWaiterCountForTesting, 0)
        XCTAssertFalse(viewModel.isRefreshOwnerActiveForTesting)
        XCTAssertFalse(viewModel.isRefreshing)
        XCTAssertEqual(viewModel.phase, .idle)
        XCTAssertNil(viewModel.snapshot)

        // Resolve the abandoned in-flight GET: it must not publish onto the torn-
        // down authority (no stale publication, no second continuation resume).
        await firstPass.succeed(makeShiftTaskSnapshot(title: "Stale after teardown"))
        XCTAssertNil(viewModel.snapshot)
        XCTAssertEqual(viewModel.phase, .idle)
    }

    // MARK: - No stale publication after server replacement mid-storm

    func testServerReplacementDuringStormResolvesWaitersWithoutStalePublication() async {
        let queue = ShiftTaskCallbackQueue()
        let barrier = RefreshWaiterRegistrationBarrier()
        let viewModel = ShiftTasksViewModel(callbackEnqueuer: queue.enqueuer)
        viewModel.refreshWaiterRegistrationObserver = barrier.signal

        let oldPass = ShiftTaskResultGate<ShiftTaskSnapshot>()
        let oldService = ScriptedShiftTaskService(loadSteps: [.gated(oldPass)])
        let oldSignalR = MockSignalRService()
        viewModel.configure(
            taskService: oldService,
            signalRService: oldSignalR,
            shiftPlanEnabled: true
        )

        // Start pass 1 on the old server and pile a storm behind it.
        oldSignalR.simulateTaskInvalidation(target: "taskupdated")
        await queue.waitForCount(1)
        let firstInvalidation = Task { await queue.runNext() }
        await barrier.waitForCount(1)
        await oldService.waitForLoadCount(1)

        oldSignalR.simulateTaskInvalidation(target: "taskupdated")
        await queue.waitForCount(1)
        let secondInvalidation = Task { await queue.runNext() }
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
        await secondInvalidation.value
        XCTAssertEqual(viewModel.pendingRefreshWaiterCountForTesting, 0)

        // The replacement authority loads cleanly.
        let currentResult = await viewModel.refresh()
        XCTAssertTrue(currentResult)
        XCTAssertEqual(
            viewModel.snapshot?.groups.first?.tasks.first?.title,
            "Replacement authority"
        )

        // The old, abandoned GET now resolves — it must NOT publish onto the
        // current authority.
        await oldPass.succeed(makeShiftTaskSnapshot(title: "Stale old authority"))
        XCTAssertEqual(
            viewModel.snapshot?.groups.first?.tasks.first?.title,
            "Replacement authority"
        )
        let oldLoadCount = await oldService.loadCallCount
        XCTAssertEqual(oldLoadCount, 1)
    }
}
