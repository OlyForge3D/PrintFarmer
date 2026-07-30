import XCTest
@testable import PrintFarmer

@MainActor
final class ShiftTasksViewModelAuthorityTests: XCTestCase {
    func testReconnectRecoveryRefreshesCanonicalTasksOnceAndFencesStaleAuthority() async {
        let callbackQueue = ShiftTaskCallbackQueue()
        let viewModel = ShiftTasksViewModel(callbackEnqueuer: callbackQueue.enqueuer)
        let oldService = ScriptedShiftTaskService(
            defaultSnapshot: makeShiftTaskSnapshot(title: "Old canonical")
        )
        let oldSignalR = MockSignalRService()
        let currentService = ScriptedShiftTaskService(
            defaultSnapshot: makeShiftTaskSnapshot(title: "Current canonical")
        )
        let currentSignalR = MockSignalRService()
        viewModel.configure(
            taskService: oldService,
            signalRService: oldSignalR,
            shiftPlanEnabled: true
        )

        oldSignalR.simulateConnectionStateChange(.connected)
        await callbackQueue.runNext()
        var oldLoadCount = await oldService.loadCallCount
        XCTAssertEqual(oldLoadCount, 0)
        oldSignalR.simulateConnectionStateChange(.reconnecting)
        await callbackQueue.runNext()
        oldLoadCount = await oldService.loadCallCount
        XCTAssertEqual(oldLoadCount, 0)
        oldSignalR.simulateConnectionStateChange(.connected)
        await callbackQueue.runNext()
        oldLoadCount = await oldService.loadCallCount
        XCTAssertEqual(oldLoadCount, 1)
        XCTAssertEqual(
            viewModel.snapshot?.groups.first?.tasks.first?.title,
            "Old canonical"
        )

        oldSignalR.simulateConnectionStateChange(.connected)
        XCTAssertEqual(callbackQueue.count, 0)
        oldLoadCount = await oldService.loadCallCount
        XCTAssertEqual(oldLoadCount, 1)

        viewModel.configure(
            taskService: currentService,
            signalRService: currentSignalR,
            shiftPlanEnabled: true
        )
        oldSignalR.simulateCapturedConnectionStateChange(at: 0, state: .reconnecting)
        oldSignalR.simulateCapturedConnectionStateChange(at: 0, state: .connected)
        XCTAssertEqual(callbackQueue.count, 2)
        await callbackQueue.runNext()
        await callbackQueue.runNext()
        oldLoadCount = await oldService.loadCallCount
        var currentLoadCount = await currentService.loadCallCount
        XCTAssertEqual(oldLoadCount, 1)
        XCTAssertEqual(currentLoadCount, 0)

        currentSignalR.simulateConnectionStateChange(.connected)
        await callbackQueue.runNext()
        currentSignalR.simulateConnectionStateChange(.reconnecting)
        await callbackQueue.runNext()
        currentLoadCount = await currentService.loadCallCount
        XCTAssertEqual(currentLoadCount, 0)
        currentSignalR.simulateConnectionStateChange(.connected)
        await callbackQueue.runNext()
        currentLoadCount = await currentService.loadCallCount
        XCTAssertEqual(currentLoadCount, 1)
        XCTAssertEqual(
            viewModel.snapshot?.groups.first?.tasks.first?.title,
            "Current canonical"
        )

        currentSignalR.simulateConnectionStateChange(.reconnecting)
        await callbackQueue.runNext()
        currentSignalR.simulateConnectionStateChange(.connected)
        viewModel.deactivate()
        await callbackQueue.runNext()
        currentLoadCount = await currentService.loadCallCount
        XCTAssertEqual(currentLoadCount, 1)
        XCTAssertNil(viewModel.snapshot)
        XCTAssertEqual(callbackQueue.count, 0)
    }

    func testP1OldMutationCannotAbsorbCurrentInvalidation() async {
        let callbackQueue = ShiftTaskCallbackQueue()
        let viewModel = ShiftTasksViewModel(callbackEnqueuer: callbackQueue.enqueuer)
        let oldMutationGate = ShiftTaskResultGate<Void>()
        let oldService = ScriptedShiftTaskService(
            mutationSteps: [.complete: [.gated(oldMutationGate)]]
        )
        let oldSignalR = MockSignalRService()
        let currentService = ScriptedShiftTaskService(
            defaultSnapshot: makeShiftTaskSnapshot(title: "Current canonical")
        )
        let currentSignalR = MockSignalRService()
        let taskID = "78200000-0000-0000-0000-000000000001"

        viewModel.configure(
            taskService: oldService,
            signalRService: oldSignalR,
            shiftPlanEnabled: true
        )
        let oldMutation = Task {
            await viewModel.perform(.complete, taskID: taskID)
        }
        await oldService.waitForMutationCount(1)

        viewModel.configure(
            taskService: currentService,
            signalRService: currentSignalR,
            shiftPlanEnabled: true
        )
        currentSignalR.simulateTaskInvalidation(target: "taskupdated")
        await callbackQueue.waitForCount(1)
        await callbackQueue.runNext()

        await oldMutationGate.succeed(())
        await oldMutation.value

        let currentLoadCount = await currentService.loadCallCount
        let oldLoadCount = await oldService.loadCallCount
        XCTAssertEqual(currentLoadCount, 1)
        XCTAssertEqual(oldLoadCount, 0)
        XCTAssertEqual(
            viewModel.snapshot?.groups.first?.tasks.first?.title,
            "Current canonical"
        )
        XCTAssertEqual(viewModel.phase, .content)
        XCTAssertFalse(viewModel.isRefreshing)
    }

    func testP2SuccessResolvesEveryCoalescedWaiterAndLeavesPending() async {
        let firstPass = ShiftTaskResultGate<ShiftTaskSnapshot>()
        let service = ScriptedShiftTaskService(
            loadSteps: [
                .gated(firstPass),
                .value(makeShiftTaskSnapshot(title: "Second pass")),
            ]
        )
        let viewModel = configuredViewModel(service: service)

        let first = Task { await viewModel.refresh() }
        await service.waitForLoadCount(1)
        let second = Task { await viewModel.refresh() }
        await firstPass.succeed(makeShiftTaskSnapshot(title: "First pass"))

        let firstResult = await first.value
        let secondResult = await second.value
        let loadCount = await service.loadCallCount
        XCTAssertTrue(firstResult)
        XCTAssertTrue(secondResult)
        XCTAssertEqual(loadCount, 2)
        XCTAssertEqual(
            viewModel.snapshot?.groups.first?.tasks.first?.title,
            "Second pass"
        )
        XCTAssertEqual(viewModel.phase, .content)
        XCTAssertFalse(viewModel.isRefreshing)
    }

    func testP2FailureResolvesWaiterAndLeavesFailedPhase() async {
        let gate = ShiftTaskResultGate<ShiftTaskSnapshot>()
        let service = ScriptedShiftTaskService(loadSteps: [.gated(gate)])
        let viewModel = configuredViewModel(service: service)

        let refresh = Task { await viewModel.refresh() }
        await service.waitForLoadCount(1)
        await gate.fail(.forced("load failed"))

        let result = await refresh.value
        XCTAssertFalse(result)
        XCTAssertEqual(viewModel.phase, .failed)
        XCTAssertEqual(viewModel.loadFailure?.message, "load failed")
        XCTAssertFalse(viewModel.isRefreshing)
    }

    func testP2CancellationResolvesWaiterAndLeavesIdlePhase() async {
        let gate = ShiftTaskResultGate<ShiftTaskSnapshot>()
        let service = ScriptedShiftTaskService(loadSteps: [.gated(gate)])
        let viewModel = configuredViewModel(service: service)

        let refresh = Task { await viewModel.refresh() }
        await service.waitForLoadCount(1)
        refresh.cancel()

        let result = await refresh.value
        XCTAssertFalse(result)
        XCTAssertEqual(viewModel.phase, .idle)
        XCTAssertFalse(viewModel.isRefreshing)

        await gate.succeed(makeShiftTaskSnapshot(title: "Cancelled stale load"))
    }

    func testP2StaleLifecycleResolvesWaiterAndLeavesIdlePhase() async {
        let gate = ShiftTaskResultGate<ShiftTaskSnapshot>()
        let service = ScriptedShiftTaskService(loadSteps: [.gated(gate)])
        let viewModel = configuredViewModel(service: service)

        let refresh = Task { await viewModel.refresh() }
        await service.waitForLoadCount(1)
        viewModel.deactivate()

        let result = await refresh.value
        XCTAssertFalse(result)
        XCTAssertEqual(viewModel.phase, .idle)
        XCTAssertNil(viewModel.snapshot)
        XCTAssertFalse(viewModel.isRefreshing)

        await gate.succeed(makeShiftTaskSnapshot(title: "Off-screen stale load"))
    }

    func testP2ServiceReplacementResolvesOldWaiterAndAllowsCurrentLoad() async {
        let oldGate = ShiftTaskResultGate<ShiftTaskSnapshot>()
        let oldService = ScriptedShiftTaskService(loadSteps: [.gated(oldGate)])
        let viewModel = configuredViewModel(service: oldService)

        let oldRefresh = Task { await viewModel.refresh() }
        await oldService.waitForLoadCount(1)

        let currentService = ScriptedShiftTaskService(
            defaultSnapshot: makeShiftTaskSnapshot(title: "Replacement")
        )
        viewModel.configure(
            taskService: currentService,
            signalRService: MockSignalRService(),
            shiftPlanEnabled: true
        )

        let oldResult = await oldRefresh.value
        let currentResult = await viewModel.refresh()
        XCTAssertFalse(oldResult)
        XCTAssertTrue(currentResult)
        XCTAssertEqual(
            viewModel.snapshot?.groups.first?.tasks.first?.title,
            "Replacement"
        )
        XCTAssertFalse(viewModel.isRefreshing)

        await oldGate.succeed(makeShiftTaskSnapshot(title: "Old replacement"))
    }

    func testP5OverlappingMutationsAcrossEpochsKeepCurrentAuthority() async {
        let taskID = "78200000-0000-0000-0000-000000000001"
        let oldGate = ShiftTaskResultGate<Void>()
        let oldService = ScriptedShiftTaskService(
            mutationSteps: [.complete: [.gated(oldGate)]]
        )
        let currentGate = ShiftTaskResultGate<Void>()
        let currentService = ScriptedShiftTaskService(
            mutationSteps: [.complete: [.gated(currentGate)]],
            defaultSnapshot: makeShiftTaskSnapshot(title: "Current wins")
        )
        let viewModel = configuredViewModel(service: oldService)

        let oldMutation = Task {
            await viewModel.perform(.complete, taskID: taskID)
        }
        await oldService.waitForMutationCount(1)

        viewModel.configure(
            taskService: currentService,
            signalRService: MockSignalRService(),
            shiftPlanEnabled: true
        )
        let currentMutation = Task {
            await viewModel.perform(.complete, taskID: taskID)
        }
        await currentService.waitForMutationCount(1)

        await currentGate.succeed(())
        await currentMutation.value
        await oldGate.succeed(())
        await oldMutation.value

        let oldLoadCount = await oldService.loadCallCount
        let currentLoadCount = await currentService.loadCallCount
        let currentMutationCalls = await currentService.mutationCalls
        XCTAssertEqual(oldLoadCount, 0)
        XCTAssertEqual(currentLoadCount, 1)
        XCTAssertEqual(currentMutationCalls.count, 1)
        XCTAssertEqual(
            viewModel.snapshot?.groups.first?.tasks.first?.title,
            "Current wins"
        )
        XCTAssertNil(viewModel.mutationActivity(for: taskID))
    }

    func testP6RetryReusesIntentAndPublishesCanonicalRowsExactlyOnce() async {
        let taskID = "78200000-0000-0000-0000-000000000001"
        let service = ScriptedShiftTaskService(
            loadSteps: [
                .value(makeShiftTaskSnapshot()),
                .value(makeEmptyShiftTaskSnapshot()),
            ],
            mutationSteps: [
                .complete: [
                    .failure(.forced("first complete failed")),
                    .success,
                    .failure(.forced("duplicate retry")),
                ],
            ]
        )
        let viewModel = configuredViewModel(service: service)
        let initialRefresh = await viewModel.refresh()
        XCTAssertTrue(initialRefresh)

        await viewModel.perform(.complete, taskID: taskID)
        guard let failure = viewModel.mutationActivity(for: taskID)?.failure else {
            XCTFail("First mutation must publish a retryable error")
            return
        }
        XCTAssertEqual(failure.message, "first complete failed")

        let retryResult = await viewModel.retryMutation(failureID: failure.id)
        XCTAssertTrue(retryResult)

        let calls = await service.mutationCalls
        let loadCount = await service.loadCallCount
        XCTAssertEqual(calls.count, 2)
        XCTAssertEqual(calls.map(\.idempotencyKey).compactMap { $0 }.count, 2)
        XCTAssertEqual(calls[0].idempotencyKey, calls[1].idempotencyKey)
        XCTAssertEqual(loadCount, 2)
        XCTAssertEqual(viewModel.snapshot?.taskCount, 0)
        XCTAssertNil(viewModel.mutationActivity(for: taskID))
    }

    func testP7DismissClearsOnlyCurrentErrorWithoutMutationSideEffect() async {
        let taskID = "78200000-0000-0000-0000-000000000001"
        let service = ScriptedShiftTaskService(
            mutationSteps: [
                .complete: [
                    .failure(.forced("first error")),
                    .failure(.forced("newer error")),
                ],
            ]
        )
        let viewModel = configuredViewModel(service: service)

        await viewModel.perform(.complete, taskID: taskID)
        let firstID = viewModel.mutationActivity(for: taskID)?.failure?.id
        if let firstID {
            let retryResult = await viewModel.retryMutation(failureID: firstID)
            XCTAssertTrue(retryResult)
        }
        let currentID = viewModel.mutationActivity(for: taskID)?.failure?.id

        XCTAssertNotEqual(firstID, currentID)
        if let firstID {
            XCTAssertFalse(viewModel.dismissMutationError(failureID: firstID))
        }
        XCTAssertEqual(
            viewModel.mutationActivity(for: taskID)?.failure?.message,
            "newer error"
        )

        let countBeforeDismiss = await service.mutationCalls.count
        if let currentID {
            XCTAssertTrue(viewModel.dismissMutationError(failureID: currentID))
        }
        XCTAssertNil(viewModel.mutationActivity(for: taskID))
        let countAfterDismiss = await service.mutationCalls.count
        XCTAssertEqual(countAfterDismiss, countBeforeDismiss)
    }

    func testSuccessfulRetryWithFailedRefreshClearsStaleMutationError() async {
        let taskID = "78200000-0000-0000-0000-000000000001"
        let service = ScriptedShiftTaskService(
            loadSteps: [
                .value(makeShiftTaskSnapshot()),
                .failure(.forced("canonical refresh failed")),
            ],
            mutationSteps: [
                .complete: [
                    .failure(.forced("mutation failed")),
                    .success,
                ],
            ]
        )
        let viewModel = configuredViewModel(service: service)
        let initialRefresh = await viewModel.refresh()
        XCTAssertTrue(initialRefresh)

        await viewModel.perform(.complete, taskID: taskID)
        guard let failureID = viewModel
            .mutationActivity(for: taskID)?.failure?.id else {
            XCTFail("Initial mutation failure must be retryable")
            return
        }

        let retried = await viewModel.retryMutation(failureID: failureID)
        let calls = await service.mutationCalls

        XCTAssertTrue(retried)
        XCTAssertEqual(calls.count, 2)
        XCTAssertEqual(calls[0].idempotencyKey, calls[1].idempotencyKey)
        XCTAssertNil(viewModel.mutationActivity(for: taskID))
        XCTAssertEqual(
            viewModel.loadFailure?.message,
            "canonical refresh failed"
        )
        XCTAssertEqual(viewModel.phase, .content)
    }

    func testStaleLoadRetryCannotReplaceNewerError() async {
        let service = ScriptedShiftTaskService(
            loadSteps: [
                .failure(.forced("first load error")),
                .failure(.forced("newer load error")),
            ]
        )
        let viewModel = configuredViewModel(service: service)

        let firstResult = await viewModel.refresh()
        XCTAssertFalse(firstResult)
        let staleID = viewModel.loadFailure?.id
        let secondResult = await viewModel.refresh()
        XCTAssertFalse(secondResult)

        if let staleID {
            let retryResult = await viewModel.retryLoad(failureID: staleID)
            XCTAssertFalse(retryResult)
        }
        XCTAssertEqual(viewModel.loadFailure?.message, "newer load error")
        let loadCount = await service.loadCallCount
        XCTAssertEqual(loadCount, 2)
    }

    private func configuredViewModel(
        service: ScriptedShiftTaskService
    ) -> ShiftTasksViewModel {
        let viewModel = ShiftTasksViewModel()
        viewModel.configure(
            taskService: service,
            signalRService: MockSignalRService(),
            shiftPlanEnabled: true
        )
        return viewModel
    }
}
