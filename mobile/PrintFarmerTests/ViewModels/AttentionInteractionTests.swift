import XCTest
@testable import PrintFarmer

@MainActor
final class AttentionInteractionTests: XCTestCase {
    private let printerA = UUID(
        uuidString: "78000000-0000-0000-0000-000000000001"
    )!
    private let printerB = UUID(
        uuidString: "78000000-0000-0000-0000-000000000002"
    )!
    private let jobA = UUID(
        uuidString: "78000000-0000-0000-0000-000000000003"
    )!
    private let jobB = UUID(
        uuidString: "78000000-0000-0000-0000-000000000004"
    )!
    private let fixedNow = Date(timeIntervalSince1970: 1_800_000_000)

    func testSupportedActionsRenderOnlyKnownServerSuppliedKinds() {
        let resume = AttentionAction(
            kind: .resume,
            label: "Resume now",
            requiresConfirmation: true
        )
        let unknown = AttentionAction(
            kind: .unknown,
            label: "Future action",
            requiresConfirmation: false
        )
        let duplicateResume = AttentionAction(
            kind: .resume,
            label: "Duplicate",
            requiresConfirmation: false
        )
        let item = makeAttentionItem(
            actions: [resume, unknown, duplicateResume]
        )

        XCTAssertEqual(
            AttentionFeedViewModel.supportedActions(in: item),
            [resume],
            "The UI must preserve supplied actions without inventing absent or unknown controls"
        )
    }

    func testResultGateTaskCancellationReleasesWaiter() async {
        let gate = AttentionResultGate<Int>()
        let entered = AttentionResultGate<Void>()
        defer {
            gate.cancel()
            entered.cancel()
        }
        let task = Task {
            await entered.succeed(())
            return try await gate.wait()
        }
        _ = try? await entered.wait()
        task.cancel()

        let result = await task.result
        guard case .failure(let error) = result else {
            return XCTFail("Cancelled result gate waiter must terminate")
        }
        XCTAssertTrue(error is CancellationError)
    }

    func testSnapshotGateTaskCancellationReleasesWaiter() async {
        let gate = AttentionSnapshotGate()
        let entered = AttentionResultGate<Void>()
        defer {
            gate.cancel()
            entered.cancel()
        }
        let task = Task {
            await entered.succeed(())
            return await gate.wait()
        }
        _ = try? await entered.wait()
        task.cancel()

        let outcome = await task.value
        guard case .nativeCancellation = outcome else {
            return XCTFail("Cancelled snapshot gate waiter must terminate as cancellation")
        }
    }

    func testCountBarrierTaskCancellationReleasesWaiter() async {
        let barrier = AttentionCountBarrier()
        let entered = AttentionResultGate<Void>()
        defer {
            barrier.close()
            entered.cancel()
        }
        let task = Task {
            await entered.succeed(())
            return await barrier.wait(for: 1)
        }
        _ = try? await entered.wait()
        task.cancel()

        let result = await task.value
        XCTAssertEqual(result, .cancelled)
        XCTAssertEqual(barrier.pendingWaiterCount, 0)
        XCTAssertEqual(barrier.pendingCompletionCount, 0)
    }

    func testCountBarrierReturnsReached() async {
        let barrier = AttentionCountBarrier()
        defer { barrier.close() }
        let ticket = barrier.register(target: 2)
        barrier.advance(to: 2)

        let result = await barrier.wait(for: ticket)
        XCTAssertEqual(result, .reached)
        XCTAssertEqual(barrier.pendingWaiterCount, 0)
        XCTAssertEqual(barrier.pendingCompletionCount, 0)
    }

    func testCountBarrierReturnsProducerFinishedWithoutCount() async {
        let barrier = AttentionCountBarrier()
        defer { barrier.close() }
        let ticket = barrier.register(target: 2)
        barrier.advance(to: 1)
        barrier.producerFinished(ticket)

        let result = await barrier.wait(for: ticket)
        XCTAssertEqual(result, .producerFinishedWithoutCount)
        XCTAssertEqual(barrier.pendingWaiterCount, 0)
        XCTAssertEqual(barrier.pendingCompletionCount, 0)
    }

    func testCountBarrierExplicitCancelAndCloseReleaseTickets() async {
        let barrier = AttentionCountBarrier()
        let cancelledTicket = barrier.register(target: 1)
        let closedTicket = barrier.register(target: 2)
        barrier.cancel(cancelledTicket)
        barrier.close()

        let cancelled = await barrier.wait(for: cancelledTicket)
        let closed = await barrier.wait(for: closedTicket)
        XCTAssertEqual(cancelled, .cancelled)
        XCTAssertEqual(closed, .closed)
        XCTAssertEqual(barrier.pendingWaiterCount, 0)
        XCTAssertEqual(barrier.pendingCompletionCount, 0)
    }

    func testCountBarrierWatchdogTimesOutAndCleansWaiter() async {
        let barrier = AttentionCountBarrier()
        defer { barrier.close() }
        let ticket = barrier.register(target: 1)

        let result = await barrier.wait(
            for: ticket,
            timeout: .milliseconds(10)
        )
        XCTAssertEqual(result, .timedOut)
        XCTAssertEqual(barrier.pendingWaiterCount, 0)
        XCTAssertEqual(barrier.pendingCompletionCount, 0)
    }

    func testScriptedServiceLoadTicketPropagatesProducerFinished() async {
        let service = ScriptedAttentionService()
        let ticket = await service.registerLoadCount(1)
        await service.finishLoadProducer(ticket)

        let result = await service.waitForLoadCount(ticket)
        XCTAssertEqual(result, .producerFinishedWithoutCount)
        await service.closeWaiters()
    }

    func testActionDispatchBlocksDuplicateAndRefreshesExactlyOnce() async {
        let action = AttentionAction(
            kind: .resume,
            label: "Resume",
            requiresConfirmation: true
        )
        let item = makeAttentionItem(
            id: "failure:dispatch",
            printerID: printerA,
            actions: [action],
            jobID: jobA
        )
        let gate = AttentionResultGate<AttentionActionResult>()
        defer { gate.cancel() }
        let service = ScriptedAttentionService(
            steps: [
                .value(makeAttentionFeed(items: [item])),
                .value(makeAttentionFeed()),
            ],
            actionSteps: [.gated(gate)]
        )
        let vm = configuredViewModel(service: service)
        let initialLoadSucceeded = await vm.refresh()
        XCTAssertTrue(initialLoadSucceeded)

        let first = Task {
            await vm.performAction(action, for: item.id)
        }
        await service.waitForActionCount(1)

        XCTAssertEqual(vm.actionState(for: item.id), .inProgress(.resume))
        let duplicateAccepted = await vm.performAction(action, for: item.id)
        XCTAssertFalse(
            duplicateAccepted,
            "A repeated tap while the item request is in flight must not dispatch"
        )
        let pendingActionCount = await service.actionCallCount
        let pendingLoadCount = await service.loadCallCount
        XCTAssertEqual(pendingActionCount, 1)
        XCTAssertEqual(pendingLoadCount, 1)

        await gate.succeed(AttentionActionResult(outcome: "Ok"))
        let firstSucceeded = await first.value
        XCTAssertTrue(firstSucceeded)

        let actionCalls = await service.actionCalls
        XCTAssertEqual(
            actionCalls,
            [AttentionActionCall(itemID: item.id, actionKind: .resume)]
        )
        let completedLoadCount = await service.loadCallCount
        XCTAssertEqual(
            completedLoadCount,
            2,
            "One initial GET plus exactly one action-success canonical refresh"
        )
        XCTAssertTrue(vm.snapshot?.items.isEmpty == true)
    }

    func testMutationInvalidationCoalescesIntoActionOwnedRefresh() async {
        let action = AttentionAction(
            kind: .resume,
            label: "Resume",
            requiresConfirmation: false
        )
        let item = makeAttentionItem(
            id: "failure:coalesced",
            printerID: printerA,
            actions: [action]
        )
        let actionGate = AttentionResultGate<AttentionActionResult>()
        defer { actionGate.cancel() }
        let service = ScriptedAttentionService(
            steps: [
                .value(makeAttentionFeed(items: [item])),
                .value(makeAttentionFeed()),
            ],
            actionSteps: [.gated(actionGate)]
        )
        let callbackQueue = AttentionCallbackQueue()
        let signalR = MockSignalRService()
        let vm = AttentionFeedViewModel(
            callbackEnqueuer: callbackQueue.enqueuer
        )
        vm.configure(
            attentionService: service,
            signalRService: signalR,
            attentionEnabled: true
        )
        let initialLoadSucceeded = await vm.refresh()
        XCTAssertTrue(initialLoadSucceeded)

        let actionTask = Task {
            await vm.performAction(action, for: item.id)
        }
        await service.waitForActionCount(1)

        signalR.simulateAttentionChanged(
            AttentionChangedEvent(
                itemId: item.id,
                changeKind: .resolved,
                occurredAt: fixedNow
            )
        )
        await callbackQueue.waitForCount(1)
        await callbackQueue.runNext()

        let loadsBeforeActionCompletion = await service.loadCallCount
        XCTAssertEqual(
            loadsBeforeActionCompletion,
            1,
            "The matching mutation event must wait for the action-owned canonical refresh"
        )

        await actionGate.succeed(AttentionActionResult(outcome: "Ok"))
        let actionSucceeded = await actionTask.value
        let completedLoadCount = await service.loadCallCount
        XCTAssertTrue(actionSucceeded)
        XCTAssertEqual(completedLoadCount, 2)
        XCTAssertEqual(callbackQueue.count, 0)
    }

    func testOverlappingRefreshPreservesMutationOwnedRefetch() async {
        let action = AttentionAction(
            kind: .resume,
            label: "Resume",
            requiresConfirmation: false
        )
        let item = makeAttentionItem(
            id: "failure:overlap",
            printerID: printerA,
            actions: [action]
        )
        let feed = makeAttentionFeed(items: [item])
        let actionGate = AttentionResultGate<AttentionActionResult>()
        let overlappingRefreshGate = AttentionResultGate<AttentionFeed>()
        defer {
            actionGate.cancel()
            overlappingRefreshGate.cancel()
        }
        let service = ScriptedAttentionService(
            steps: [
                .value(feed),
                .gated(overlappingRefreshGate),
                .value(makeAttentionFeed()),
            ],
            actionSteps: [.gated(actionGate)]
        )
        let callbackQueue = AttentionCallbackQueue()
        let signalR = MockSignalRService()
        let vm = AttentionFeedViewModel(
            callbackEnqueuer: callbackQueue.enqueuer
        )
        vm.configure(
            attentionService: service,
            signalRService: signalR,
            attentionEnabled: true
        )
        let initialLoadSucceeded = await vm.refresh()
        XCTAssertTrue(initialLoadSucceeded)

        let actionTask = Task {
            await vm.performAction(action, for: item.id)
        }
        await service.waitForActionCount(1)

        let overlappingRefresh = Task { await vm.refresh() }
        await service.waitForLoadCount(2)

        signalR.simulateAttentionChanged(
            AttentionChangedEvent(
                itemId: item.id,
                changeKind: .resolved,
                occurredAt: fixedNow
            )
        )
        await callbackQueue.waitForCount(1)
        await callbackQueue.runNext()

        await overlappingRefreshGate.succeed(feed)
        let overlappingRefreshSucceeded = await overlappingRefresh.value
        XCTAssertTrue(overlappingRefreshSucceeded)
        XCTAssertEqual(
            callbackQueue.count,
            0,
            "The overlapping refresh must not queue a competing follow-up"
        )

        await actionGate.succeed(AttentionActionResult(outcome: "Ok"))
        let actionSucceeded = await actionTask.value
        let loadCount = await service.loadCallCount
        XCTAssertTrue(actionSucceeded)
        XCTAssertEqual(
            loadCount,
            3,
            "Initial + unrelated overlap + exactly one action-owned canonical refresh"
        )
        XCTAssertTrue(vm.snapshot?.items.isEmpty == true)
        XCTAssertEqual(callbackQueue.count, 0)
    }

    func testFeedReplacementPreservesInFlightActionUntilReappendAndCompletion() async {
        let action = AttentionAction(
            kind: .resume,
            label: "Resume",
            requiresConfirmation: false
        )
        let item = makeAttentionItem(
            id: "failure:paginated-action",
            printerID: printerA,
            actions: [action]
        )
        let actionGate = AttentionResultGate<AttentionActionResult>()
        defer { actionGate.cancel() }
        let service = ScriptedAttentionService(
            steps: [
                .value(makeAttentionFeed(items: [item])),
                .value(makeAttentionFeed(nextCursor: "page-2")),
                .value(makeAttentionFeed(items: [item])),
                .value(makeAttentionFeed()),
            ],
            actionSteps: [.gated(actionGate)]
        )
        let vm = configuredViewModel(service: service)
        let initialLoadSucceeded = await vm.refresh()
        XCTAssertTrue(initialLoadSucceeded)

        let actionTask = Task {
            await vm.performAction(action, for: item.id)
        }
        await service.waitForActionCount(1)

        let replacementSucceeded = await vm.refresh()
        XCTAssertTrue(replacementSucceeded)
        XCTAssertTrue(vm.snapshot?.items.isEmpty == true)
        XCTAssertEqual(vm.actionState(for: item.id), .inProgress(.resume))

        let appendSucceeded = await vm.loadMore()
        XCTAssertTrue(appendSucceeded)
        XCTAssertEqual(vm.snapshot?.items.map(\.id), [item.id])
        XCTAssertEqual(vm.actionState(for: item.id), .inProgress(.resume))

        let duplicateAccepted = await vm.performAction(action, for: item.id)
        let pendingActionCount = await service.actionCallCount
        XCTAssertFalse(duplicateAccepted)
        XCTAssertEqual(pendingActionCount, 1)

        await actionGate.succeed(AttentionActionResult(outcome: "Ok"))
        let actionSucceeded = await actionTask.value
        let loadCount = await service.loadCallCount
        XCTAssertTrue(actionSucceeded)
        XCTAssertEqual(loadCount, 4)
        XCTAssertTrue(vm.snapshot?.items.isEmpty == true)
        XCTAssertEqual(vm.actionState(for: item.id), .idle)
    }

    func testSuccessfulMutationRefreshFailureRequiresRefreshOnlyRecovery() async {
        let action = AttentionAction(
            kind: .harvest,
            label: "Harvest",
            requiresConfirmation: true
        )
        let item = makeAttentionItem(
            id: "harvest:refresh-failure",
            kind: .harvest,
            severity: .info,
            printerID: printerA,
            actions: [action],
            jobID: jobA
        )
        let refreshRetryGate = AttentionResultGate<AttentionFeed>()
        defer { refreshRetryGate.cancel() }
        let service = ScriptedAttentionService(
            steps: [
                .value(makeAttentionFeed(items: [item])),
                .failure(.network("Canonical refresh failed")),
                .gated(refreshRetryGate),
            ],
            actionSteps: [
                .value(AttentionActionResult(outcome: "Ok")),
            ]
        )
        let vm = configuredViewModel(service: service)
        let initialLoadSucceeded = await vm.refresh()
        XCTAssertTrue(initialLoadSucceeded)

        let mutationSucceeded = await vm.performAction(action, for: item.id)
        XCTAssertTrue(mutationSucceeded)
        guard case .refreshPending(let pending) = vm.actionState(for: item.id) else {
            return XCTFail("Successful mutation with failed GET must remain refresh-pending")
        }
        XCTAssertEqual(pending.action.kind, .harvest)
        XCTAssertEqual(pending.message, "Canonical refresh failed")
        XCTAssertEqual(vm.snapshot?.items.map(\.id), [item.id])

        let duplicateAccepted = await vm.performAction(action, for: item.id)
        XCTAssertFalse(duplicateAccepted)
        let callsBeforeRefreshRetry = await service.actionCallCount
        let loadsBeforeRefreshRetry = await service.loadCallCount
        XCTAssertEqual(callsBeforeRefreshRetry, 1)
        XCTAssertEqual(loadsBeforeRefreshRetry, 2)

        let refreshRetryTask = Task {
            await vm.retryActionRefresh(pendingID: pending.id)
        }
        await service.waitForLoadCount(3)
        guard case .refreshPending(let retrying) = vm.actionState(for: item.id) else {
            return XCTFail("Refresh-only retry must retain pending authority")
        }
        XCTAssertNil(retrying.message)
        let callsWhileRetrying = await service.actionCallCount
        XCTAssertEqual(callsWhileRetrying, 1)

        await refreshRetryGate.succeed(makeAttentionFeed())
        let refreshRetrySucceeded = await refreshRetryTask.value
        XCTAssertTrue(refreshRetrySucceeded)
        let callsAfterRefreshRetry = await service.actionCallCount
        let loadsAfterRefreshRetry = await service.loadCallCount
        XCTAssertEqual(callsAfterRefreshRetry, 1)
        XCTAssertEqual(loadsAfterRefreshRetry, 3)
        XCTAssertTrue(vm.snapshot?.items.isEmpty == true)
        XCTAssertEqual(vm.actionState(for: item.id), .idle)
    }

    func testCompleteOmissionClearsRefreshRequirementWithNewerEvent() async {
        let action = AttentionAction(
            kind: .resume,
            label: "Resume",
            requiresConfirmation: false
        )
        let item = makeAttentionItem(
            id: "failure:complete-refresh-requirement",
            printerID: printerA,
            actions: [action]
        )
        let completeFeedGate = AttentionResultGate<AttentionFeed>()
        defer { completeFeedGate.cancel() }
        let callbackQueue = AttentionCallbackQueue()
        let signalR = MockSignalRService()
        let service = ScriptedAttentionService(
            steps: [
                .value(makeAttentionFeed(items: [item])),
                .failure(.network("Action refresh failed")),
                .gated(completeFeedGate),
            ],
            actionSteps: [
                .value(AttentionActionResult(outcome: "Ok")),
            ]
        )
        let vm = AttentionFeedViewModel(
            callbackEnqueuer: callbackQueue.enqueuer
        )
        vm.configure(
            attentionService: service,
            signalRService: signalR,
            attentionEnabled: true
        )
        let initialLoadSucceeded = await vm.refresh()
        XCTAssertTrue(initialLoadSucceeded)
        let mutationSucceeded = await vm.performAction(action, for: item.id)
        XCTAssertTrue(mutationSucceeded)
        guard case .refreshPending(let pending) = vm.actionState(for: item.id) else {
            return XCTFail("Failed action refresh must retain authority")
        }

        let retryTask = Task {
            await vm.retryActionRefresh(pendingID: pending.id)
        }
        await service.waitForLoadCount(3)
        signalR.simulateAttentionChanged(
            AttentionChangedEvent(
                itemId: item.id,
                changeKind: .resolved,
                occurredAt: fixedNow
            )
        )
        await callbackQueue.waitForCount(1)
        await callbackQueue.runNext()

        await completeFeedGate.succeed(makeAttentionFeed())
        let retrySucceeded = await retryTask.value
        XCTAssertTrue(retrySucceeded)
        XCTAssertEqual(vm.actionState(for: item.id), .idle)
        let loadCount = await service.loadCallCount
        XCTAssertEqual(loadCount, 3)
        XCTAssertEqual(callbackQueue.count, 0)
    }

    func testDeactivationPreservesPOSTOwnershipUntilReentryCanonicalApply() async {
        let action = AttentionAction(
            kind: .harvest,
            label: "Harvest",
            requiresConfirmation: true
        )
        let item = makeAttentionItem(
            id: "harvest:deactivation",
            kind: .harvest,
            severity: .info,
            printerID: printerA,
            actions: [action],
            jobID: jobA
        )
        let actionGate = AttentionResultGate<AttentionActionResult>()
        defer { actionGate.cancel() }
        let service = ScriptedAttentionService(
            steps: [
                .value(makeAttentionFeed(items: [item])),
                .value(makeAttentionFeed()),
            ],
            actionSteps: [.gated(actionGate)]
        )
        let signalR = MockSignalRService()
        let vm = AttentionFeedViewModel()
        vm.configure(
            attentionService: service,
            signalRService: signalR,
            attentionEnabled: true
        )
        let initialLoadSucceeded = await vm.refresh()
        XCTAssertTrue(initialLoadSucceeded)

        let actionTask = Task {
            await vm.performAction(action, for: item.id)
        }
        await service.waitForActionCount(1)
        vm.deactivate()
        XCTAssertEqual(vm.actionState(for: item.id), .inProgress(.harvest))

        await actionGate.succeed(AttentionActionResult(outcome: "Ok"))
        let actionSucceeded = await actionTask.value
        XCTAssertTrue(actionSucceeded)
        guard case .refreshPending = vm.actionState(for: item.id) else {
            return XCTFail("Inactive POST success must retain refresh authority")
        }
        let callsWhileInactive = await service.actionCallCount
        let loadsWhileInactive = await service.loadCallCount
        XCTAssertEqual(callsWhileInactive, 1)
        XCTAssertEqual(loadsWhileInactive, 1)

        let duplicateAccepted = await vm.performAction(action, for: item.id)
        XCTAssertFalse(duplicateAccepted)
        let callsAfterDuplicate = await service.actionCallCount
        XCTAssertEqual(callsAfterDuplicate, 1)

        let freshLifecycle = vm.currentLifecycleToken()
        let bootstrapApplied = await vm.bootstrap(
            attentionService: service,
            signalRService: signalR,
            attentionEnabled: true,
            lifecycleToken: freshLifecycle
        )
        XCTAssertTrue(bootstrapApplied)
        let finalLoadCount = await service.loadCallCount
        XCTAssertEqual(finalLoadCount, 2)
        XCTAssertEqual(vm.actionState(for: item.id), .idle)
        XCTAssertTrue(vm.snapshot?.items.isEmpty == true)
    }

    func testQualifyingNewerRefreshSatisfiesSupersededActionRefresh() async {
        let action = AttentionAction(
            kind: .resume,
            label: "Resume",
            requiresConfirmation: false
        )
        let item = makeAttentionItem(
            id: "failure:qualifying-supersession",
            printerID: printerA,
            actions: [action]
        )
        let actionRefreshGate = AttentionResultGate<AttentionFeed>()
        let qualifyingRefreshGate = AttentionResultGate<AttentionFeed>()
        defer {
            actionRefreshGate.cancel()
            qualifyingRefreshGate.cancel()
        }
        let service = ScriptedAttentionService(
            steps: [
                .value(makeAttentionFeed(items: [item])),
                .gated(actionRefreshGate),
                .gated(qualifyingRefreshGate),
            ],
            actionSteps: [
                .value(AttentionActionResult(outcome: "Ok")),
            ]
        )
        let vm = configuredViewModel(service: service)
        let initialLoadSucceeded = await vm.refresh()
        XCTAssertTrue(initialLoadSucceeded)

        let actionTask = Task {
            await vm.performAction(action, for: item.id)
        }
        await service.waitForLoadCount(2)

        let qualifyingRefreshTask = Task { await vm.refresh() }
        await service.waitForLoadCount(3)

        await actionRefreshGate.succeed(makeAttentionFeed(items: [item]))
        let actionSucceeded = await actionTask.value
        XCTAssertTrue(actionSucceeded)
        guard case .refreshPending = vm.actionState(for: item.id) else {
            return XCTFail("Superseded action refresh must retain mutation authority")
        }

        await qualifyingRefreshGate.succeed(makeAttentionFeed())
        let qualifyingRefreshSucceeded = await qualifyingRefreshTask.value
        XCTAssertTrue(qualifyingRefreshSucceeded)
        XCTAssertEqual(vm.actionState(for: item.id), .idle)
        let actionCallCount = await service.actionCallCount
        let loadCallCount = await service.loadCallCount
        XCTAssertEqual(actionCallCount, 1)
        XCTAssertEqual(loadCallCount, 3)
    }

    func testNonQualifyingOlderRefreshCannotSatisfyMutationRequirement() async {
        let action = AttentionAction(
            kind: .resume,
            label: "Resume",
            requiresConfirmation: false
        )
        let item = makeAttentionItem(
            id: "failure:nonqualifying-supersession",
            printerID: printerA,
            actions: [action]
        )
        let actionPOSTGate = AttentionResultGate<AttentionActionResult>()
        let olderRefreshGate = AttentionResultGate<AttentionFeed>()
        let actionRefreshGate = AttentionResultGate<AttentionFeed>()
        defer {
            actionPOSTGate.cancel()
            olderRefreshGate.cancel()
            actionRefreshGate.cancel()
        }
        let service = ScriptedAttentionService(
            steps: [
                .value(makeAttentionFeed(items: [item])),
                .gated(olderRefreshGate),
                .gated(actionRefreshGate),
            ],
            actionSteps: [.gated(actionPOSTGate)]
        )
        let vm = configuredViewModel(service: service)
        let initialLoadSucceeded = await vm.refresh()
        XCTAssertTrue(initialLoadSucceeded)

        let actionTask = Task {
            await vm.performAction(action, for: item.id)
        }
        await service.waitForActionCount(1)

        let olderRefreshTask = Task { await vm.refresh() }
        await service.waitForLoadCount(2)

        await actionPOSTGate.succeed(AttentionActionResult(outcome: "Ok"))
        await service.waitForLoadCount(3)

        await olderRefreshGate.succeed(makeAttentionFeed())
        let olderRefreshSucceeded = await olderRefreshTask.value
        XCTAssertFalse(olderRefreshSucceeded)
        guard case .refreshPending = vm.actionState(for: item.id) else {
            return XCTFail("A request started before mutation success cannot release authority")
        }

        await actionRefreshGate.succeed(makeAttentionFeed())
        let actionSucceeded = await actionTask.value
        XCTAssertTrue(actionSucceeded)
        XCTAssertEqual(vm.actionState(for: item.id), .idle)
        let actionCallCount = await service.actionCallCount
        XCTAssertEqual(actionCallCount, 1)
    }

    func testEventDuringActionRefreshHasDeterministicFollowupBeforeCallbackDrain() async {
        await assertEventDuringActionRefresh(drainCallbackBeforeCompletion: true)
    }

    func testEventDuringActionRefreshHasDeterministicFollowupAfterCallbackDrain() async {
        await assertEventDuringActionRefresh(drainCallbackBeforeCompletion: false)
    }

    func testMixedInvalidationsUseSingleMutationOwnedFollowup() async {
        let action = AttentionAction(
            kind: .resume,
            label: "Resume",
            requiresConfirmation: false
        )
        let item = makeAttentionItem(
            id: "failure:mixed-invalidations",
            printerID: printerA,
            actions: [action]
        )
        let refreshGate = AttentionResultGate<AttentionFeed>()
        defer { refreshGate.cancel() }
        let callbackQueue = AttentionCallbackQueue()
        let signalR = MockSignalRService()
        let service = ScriptedAttentionService(
            steps: [
                .value(makeAttentionFeed(items: [item])),
                .gated(refreshGate),
                .value(makeAttentionFeed()),
            ],
            actionSteps: [
                .value(AttentionActionResult(outcome: "Ok")),
            ]
        )
        let vm = AttentionFeedViewModel(
            callbackEnqueuer: callbackQueue.enqueuer
        )
        vm.configure(
            attentionService: service,
            signalRService: signalR,
            attentionEnabled: true
        )
        let initialLoadSucceeded = await vm.refresh()
        XCTAssertTrue(initialLoadSucceeded)

        let actionTask = Task {
            await vm.performAction(action, for: item.id)
        }
        await service.waitForLoadCount(2)

        signalR.simulateAttentionChanged(
            AttentionChangedEvent(
                itemId: item.id,
                changeKind: .resolved,
                occurredAt: fixedNow
            )
        )
        signalR.simulateAttentionChanged(
            AttentionChangedEvent(
                itemId: "maintenance:unrelated",
                changeKind: .updated,
                occurredAt: fixedNow
            )
        )
        await callbackQueue.waitForCount(2)
        await callbackQueue.runNext()
        await callbackQueue.runNext()

        await refreshGate.succeed(makeAttentionFeed(items: [item]))
        let actionSucceeded = await actionTask.value
        let loadCount = await service.loadCallCount
        XCTAssertTrue(actionSucceeded)
        XCTAssertEqual(loadCount, 3)
        XCTAssertEqual(callbackQueue.count, 0)
        XCTAssertEqual(vm.actionState(for: item.id), .idle)
        XCTAssertTrue(vm.snapshot?.items.isEmpty == true)
    }

    func testStaleAuthorityEventsCannotAdvanceNewAuthoritySequences() async {
        let action = AttentionAction(
            kind: .resume,
            label: "Resume",
            requiresConfirmation: false
        )
        let item = makeAttentionItem(
            id: "failure:authority-sequence",
            printerID: printerA,
            actions: [action]
        )
        let callbackQueue = AttentionCallbackQueue()
        let oldSignalR = MockSignalRService()
        let newSignalR = MockSignalRService()
        let oldService = ScriptedAttentionService()
        let actionRefreshGate = AttentionResultGate<AttentionFeed>()
        defer { actionRefreshGate.cancel() }
        let newService = ScriptedAttentionService(
            steps: [
                .value(makeAttentionFeed(items: [item])),
                .gated(actionRefreshGate),
                .value(makeAttentionFeed()),
            ],
            actionSteps: [
                .value(AttentionActionResult(outcome: "Ok")),
            ]
        )
        let vm = AttentionFeedViewModel(
            callbackEnqueuer: callbackQueue.enqueuer
        )
        vm.configure(
            attentionService: oldService,
            signalRService: oldSignalR,
            attentionEnabled: true
        )
        XCTAssertEqual(oldSignalR.capturedAttentionHandlerCount, 1)

        let staleEvent = AttentionChangedEvent(
            itemId: item.id,
            changeKind: .updated,
            occurredAt: fixedNow
        )
        oldSignalR.simulateCapturedAttentionChanged(at: 0, event: staleEvent)
        await callbackQueue.waitForCount(1)

        vm.configure(
            attentionService: newService,
            signalRService: newSignalR,
            attentionEnabled: true
        )
        let bInitialSequence = vm.eventSequenceSnapshotForTesting()
        XCTAssertEqual(bInitialSequence.sequence, 0)
        XCTAssertEqual(bInitialSequence.itemSequences, [:])
        let initialLoadSucceeded = await vm.refresh()
        XCTAssertTrue(initialLoadSucceeded)

        let actionLoadTicket = await newService.registerLoadCount(2)
        let actionTask = Task {
            await vm.performAction(action, for: item.id)
        }
        let actionProducerFinished = Task {
            _ = await actionTask.value
            await newService.finishLoadProducer(actionLoadTicket)
        }
        let actionLoadResult = await newService.waitForLoadCount(
            actionLoadTicket
        )
        XCTAssertEqual(actionLoadResult, .reached)

        oldSignalR.simulateCapturedAttentionChanged(at: 0, event: staleEvent)
        XCTAssertEqual(
            vm.eventSequenceSnapshotForTesting(),
            bInitialSequence
        )
        XCTAssertEqual(
            callbackQueue.count,
            1,
            "Post-reset stale delivery must not enqueue or advance B"
        )

        await actionRefreshGate.succeed(makeAttentionFeed())
        let actionSucceeded = await actionTask.value
        _ = await actionProducerFinished.value
        XCTAssertTrue(actionSucceeded)
        let loadsAfterAction = await newService.loadCallCount
        XCTAssertEqual(loadsAfterAction, 2)
        XCTAssertEqual(
            vm.eventSequenceSnapshotForTesting(),
            bInitialSequence
        )

        await callbackQueue.runNext()
        XCTAssertEqual(callbackQueue.count, 0)
        XCTAssertEqual(
            vm.eventSequenceSnapshotForTesting(),
            bInitialSequence
        )
        let loadsAfterStaleDrain = await newService.loadCallCount
        XCTAssertEqual(loadsAfterStaleDrain, 2)

        let eventLoadTicket = await newService.registerLoadCount(3)
        newSignalR.simulateAttentionChanged(
            AttentionChangedEvent(
                itemId: item.id,
                changeKind: .updated,
                occurredAt: fixedNow
            )
        )
        let bEventSequence = vm.eventSequenceSnapshotForTesting()
        XCTAssertEqual(bEventSequence.sequence, 1)
        XCTAssertEqual(bEventSequence.itemSequences[item.id], 1)
        await callbackQueue.waitForCount(1)
        await callbackQueue.runNext()
        await newService.finishLoadProducer(eventLoadTicket)
        let eventLoadResult = await newService.waitForLoadCount(
            eventLoadTicket
        )
        XCTAssertEqual(eventLoadResult, .reached)
        let loadsAfterCurrentEvent = await newService.loadCallCount
        XCTAssertEqual(loadsAfterCurrentEvent, 3)
    }

    func testActionRefreshFailurePreservesEventForRefreshOnlyRetry() async {
        let action = AttentionAction(
            kind: .resume,
            label: "Resume",
            requiresConfirmation: false
        )
        let item = makeAttentionItem(
            id: "failure:event-refresh-failure",
            printerID: printerA,
            actions: [action]
        )
        let refreshGate = AttentionResultGate<AttentionFeed>()
        defer { refreshGate.cancel() }
        let callbackQueue = AttentionCallbackQueue()
        let signalR = MockSignalRService()
        let service = ScriptedAttentionService(
            steps: [
                .value(makeAttentionFeed(items: [item])),
                .gated(refreshGate),
                .value(makeAttentionFeed()),
            ],
            actionSteps: [
                .value(AttentionActionResult(outcome: "Ok")),
            ]
        )
        let vm = AttentionFeedViewModel(
            callbackEnqueuer: callbackQueue.enqueuer
        )
        vm.configure(
            attentionService: service,
            signalRService: signalR,
            attentionEnabled: true
        )
        let initialLoadSucceeded = await vm.refresh()
        XCTAssertTrue(initialLoadSucceeded)

        let actionTask = Task {
            await vm.performAction(action, for: item.id)
        }
        await service.waitForLoadCount(2)
        signalR.simulateAttentionChanged(
            AttentionChangedEvent(
                itemId: item.id,
                changeKind: .resolved,
                occurredAt: fixedNow
            )
        )
        await callbackQueue.waitForCount(1)

        await refreshGate.fail(.network("Post-action refresh failed"))
        let actionSucceeded = await actionTask.value
        XCTAssertTrue(actionSucceeded)
        await callbackQueue.runNext()

        guard case .refreshPending(let pending) = vm.actionState(for: item.id) else {
            return XCTFail("Failed action refresh must preserve refresh-only authority")
        }
        XCTAssertEqual(pending.message, "Post-action refresh failed")
        let callsBeforeRetry = await service.actionCallCount
        let loadsBeforeRetry = await service.loadCallCount
        XCTAssertEqual(callsBeforeRetry, 1)
        XCTAssertEqual(loadsBeforeRetry, 2)

        let duplicateAccepted = await vm.performAction(action, for: item.id)
        XCTAssertFalse(duplicateAccepted)
        let callsAfterDuplicate = await service.actionCallCount
        XCTAssertEqual(callsAfterDuplicate, 1)

        let refreshRetrySucceeded = await vm.retryActionRefresh(
            pendingID: pending.id
        )
        XCTAssertTrue(refreshRetrySucceeded)
        let callsAfterRetry = await service.actionCallCount
        let loadsAfterRetry = await service.loadCallCount
        XCTAssertEqual(callsAfterRetry, 1)
        XCTAssertEqual(loadsAfterRetry, 3)
        XCTAssertEqual(vm.actionState(for: item.id), .idle)
        XCTAssertEqual(callbackQueue.count, 0)
    }

    func testRemovedLiveActionDropsObsoleteFailure() async {
        let action = AttentionAction(
            kind: .resume,
            label: "Resume",
            requiresConfirmation: false
        )
        let original = makeAttentionItem(
            id: "failure:removed-action",
            printerID: printerA,
            actions: [action]
        )
        let replacement = makeAttentionItem(
            id: original.id,
            printerID: printerA,
            actions: []
        )
        let actionGate = AttentionResultGate<AttentionActionResult>()
        defer { actionGate.cancel() }
        let service = ScriptedAttentionService(
            steps: [
                .value(makeAttentionFeed(items: [original])),
                .value(makeAttentionFeed(items: [replacement])),
            ],
            actionSteps: [.gated(actionGate)]
        )
        let vm = configuredViewModel(service: service)
        let initialLoadSucceeded = await vm.refresh()
        XCTAssertTrue(initialLoadSucceeded)

        let actionTask = Task {
            await vm.performAction(action, for: original.id)
        }
        await service.waitForActionCount(1)
        let replacementSucceeded = await vm.refresh()
        XCTAssertTrue(replacementSucceeded)
        XCTAssertEqual(vm.actionState(for: original.id), .inProgress(.resume))

        await actionGate.fail(.network("Resume rejected"))
        let actionSucceeded = await actionTask.value
        XCTAssertFalse(actionSucceeded)
        XCTAssertEqual(vm.actionState(for: original.id), .idle)
        guard let liveItem = vm.snapshot?.items.first else {
            return XCTFail("Replacement item must remain visible")
        }
        XCTAssertTrue(AttentionFeedViewModel.supportedActions(in: liveItem).isEmpty)
    }

    func testRetainedSnoozeActionRetriesOriginalDeadlineAfterFailure() async throws {
        let action = AttentionAction(
            kind: .snooze,
            label: "Snooze",
            requiresConfirmation: false
        )
        let item = makeAttentionItem(
            id: "runout:retained-snooze",
            kind: .runout,
            printerID: printerA,
            actions: [action]
        )
        let deadline = fixedNow.addingTimeInterval(
            AttentionFeedViewModel.defaultSnoozeInterval
        )
        let snoozeGate = AttentionResultGate<SnoozeAttentionResponse>()
        defer { snoozeGate.cancel() }
        let service = ScriptedAttentionService(
            steps: [
                .value(makeAttentionFeed(items: [item])),
                .value(makeAttentionFeed(items: [item])),
                .value(makeAttentionFeed()),
            ],
            snoozeSteps: [
                .gated(snoozeGate),
                .value(
                    SnoozeAttentionResponse(
                        snoozedUntilUtc: deadline,
                        attentionItemAnchorAtUtc: item.occurredAt
                    )
                ),
            ]
        )
        let vm = configuredViewModel(service: service, now: fixedNow)
        let initialLoadSucceeded = await vm.refresh()
        XCTAssertTrue(initialLoadSucceeded)

        let snoozeTask = Task {
            await vm.performAction(action, for: item.id)
        }
        await service.waitForSnoozeCount(1)
        let replacementSucceeded = await vm.refresh()
        XCTAssertTrue(replacementSucceeded)

        await snoozeGate.fail(.network("Snooze rejected"))
        let snoozeSucceeded = await snoozeTask.value
        XCTAssertFalse(snoozeSucceeded)
        let failure = try actionFailure(vm.actionState(for: item.id))
        XCTAssertEqual(failure.snoozedUntilUtc, deadline)

        let retrySucceeded = await vm.retryAction(failureID: failure.id)
        XCTAssertTrue(retrySucceeded)
        let snoozeDeadlines = await service.snoozeCalls.map(\.snoozedUntilUtc)
        XCTAssertEqual(
            snoozeDeadlines,
            [deadline, deadline]
        )
        let snoozeCallCount = await service.snoozeCallCount
        XCTAssertEqual(snoozeCallCount, 2)
        XCTAssertEqual(vm.actionState(for: item.id), .idle)
    }

    func testOmittedFailedSnoozeReappendPreservesExactRetryWithoutSynthesis() async throws {
        let action = AttentionAction(
            kind: .snooze,
            label: "Snooze",
            requiresConfirmation: false
        )
        let item = makeAttentionItem(
            id: "runout:omitted-snooze",
            kind: .runout,
            printerID: printerA,
            actions: [action]
        )
        let deadline = fixedNow.addingTimeInterval(
            AttentionFeedViewModel.defaultSnoozeInterval
        )
        let snoozeGate = AttentionResultGate<SnoozeAttentionResponse>()
        defer { snoozeGate.cancel() }
        let service = ScriptedAttentionService(
            steps: [
                .value(makeAttentionFeed(items: [item])),
                .value(makeAttentionFeed(nextCursor: "page-2")),
                .value(makeAttentionFeed(items: [item])),
                .value(makeAttentionFeed()),
            ],
            snoozeSteps: [
                .gated(snoozeGate),
                .value(
                    SnoozeAttentionResponse(
                        snoozedUntilUtc: deadline,
                        attentionItemAnchorAtUtc: item.occurredAt
                    )
                ),
            ]
        )
        let vm = configuredViewModel(service: service, now: fixedNow)
        let initialLoadSucceeded = await vm.refresh()
        XCTAssertTrue(initialLoadSucceeded)

        let snoozeTask = Task {
            await vm.performAction(action, for: item.id)
        }
        await service.waitForSnoozeCount(1)
        let omissionSucceeded = await vm.refresh()
        XCTAssertTrue(omissionSucceeded)
        XCTAssertTrue(vm.snapshot?.items.isEmpty == true)
        XCTAssertEqual(vm.actionState(for: item.id), .inProgress(.snooze))

        await snoozeGate.fail(.network("Snooze rejected"))
        let snoozeSucceeded = await snoozeTask.value
        XCTAssertFalse(snoozeSucceeded)
        let hiddenFailure = try actionFailure(vm.actionState(for: item.id))
        XCTAssertEqual(hiddenFailure.snoozedUntilUtc, deadline)

        let absentRetryAccepted = await vm.retryAction(
            failureID: hiddenFailure.id
        )
        XCTAssertFalse(absentRetryAccepted)
        let stillHiddenFailure = try actionFailure(
            vm.actionState(for: item.id)
        )
        XCTAssertEqual(stillHiddenFailure.id, hiddenFailure.id)
        let callsWhileAbsent = await service.snoozeCallCount
        XCTAssertEqual(callsWhileAbsent, 1)

        let appendSucceeded = await vm.loadMore()
        XCTAssertTrue(appendSucceeded)
        XCTAssertEqual(vm.snapshot?.items.map(\.id), [item.id])
        let visibleFailure = try actionFailure(vm.actionState(for: item.id))
        XCTAssertEqual(visibleFailure.id, hiddenFailure.id)

        let retrySucceeded = await vm.retryAction(failureID: visibleFailure.id)
        XCTAssertTrue(retrySucceeded)
        let snoozeDeadlines = await service.snoozeCalls.map(\.snoozedUntilUtc)
        XCTAssertEqual(
            snoozeDeadlines,
            [deadline, deadline]
        )
        let snoozeCallCount = await service.snoozeCallCount
        XCTAssertEqual(snoozeCallCount, 2)
    }

    func testCompleteCanonicalOmissionDropsLatePOSTFailure() async {
        let action = AttentionAction(
            kind: .resume,
            label: "Resume",
            requiresConfirmation: false
        )
        let item = makeAttentionItem(
            id: "failure:complete-omission",
            printerID: printerA,
            actions: [action]
        )
        let actionGate = AttentionResultGate<AttentionActionResult>()
        defer { actionGate.cancel() }
        let service = ScriptedAttentionService(
            steps: [
                .value(makeAttentionFeed(items: [item])),
                .value(makeAttentionFeed()),
            ],
            actionSteps: [.gated(actionGate)]
        )
        let vm = configuredViewModel(service: service)
        let initialLoadSucceeded = await vm.refresh()
        XCTAssertTrue(initialLoadSucceeded)

        let actionTask = Task {
            await vm.performAction(action, for: item.id)
        }
        await service.waitForActionCount(1)
        let omissionSucceeded = await vm.refresh()
        XCTAssertTrue(omissionSucceeded)
        XCTAssertEqual(vm.actionState(for: item.id), .idle)

        await actionGate.fail(.network("Old POST failed after omission"))
        let actionSucceeded = await actionTask.value
        XCTAssertFalse(actionSucceeded)
        XCTAssertEqual(vm.actionState(for: item.id), .idle)
        let actionCallCount = await service.actionCallCount
        XCTAssertEqual(actionCallCount, 1)
    }

    func testFinalPaginationOmissionClearsPreservedFailure() async throws {
        let action = AttentionAction(
            kind: .snooze,
            label: "Snooze",
            requiresConfirmation: false
        )
        let item = makeAttentionItem(
            id: "runout:final-omission",
            kind: .runout,
            printerID: printerA,
            actions: [action]
        )
        let service = ScriptedAttentionService(
            steps: [
                .value(makeAttentionFeed(items: [item])),
                .value(makeAttentionFeed(nextCursor: "final-page")),
                .value(makeAttentionFeed()),
            ],
            snoozeSteps: [
                .failure(.network("Snooze failed before pagination")),
            ]
        )
        let vm = configuredViewModel(service: service, now: fixedNow)
        let initialLoadSucceeded = await vm.refresh()
        XCTAssertTrue(initialLoadSucceeded)
        let snoozeSucceeded = await vm.performAction(action, for: item.id)
        XCTAssertFalse(snoozeSucceeded)
        let failure = try actionFailure(vm.actionState(for: item.id))

        let omissionSucceeded = await vm.refresh()
        XCTAssertTrue(omissionSucceeded)
        let preserved = try actionFailure(vm.actionState(for: item.id))
        XCTAssertEqual(preserved.id, failure.id)

        let finalPageSucceeded = await vm.loadMore()
        XCTAssertTrue(finalPageSucceeded)
        XCTAssertEqual(vm.actionState(for: item.id), .idle)
        let staleRetryAccepted = await vm.retryAction(failureID: failure.id)
        XCTAssertFalse(staleRetryAccepted)
        let snoozeCallCount = await service.snoozeCallCount
        XCTAssertEqual(snoozeCallCount, 1)
    }

    // MARK: - Incomplete-pagination media/action retention (Hicks blocker #1)
    //
    // Media state and action authority hang off the exact
    // ``AttentionOccurrenceFingerprint`` of the item that owns it. A
    // first-page canonical replacement (`replaceMedia: true`) that omits an
    // item is *not* enough evidence to prove the item is gone if there are
    // more pages to load — the canonical occurrence set is only complete
    // once the final page's `nextCursor` is nil. These tests pin the
    // "retain until fully known, reconcile once complete" contract.

    /// After a first-page refresh that omits an existing item with
    /// `hasMorePages == true`, the preserved media entry must be
    /// re-observable once a later page restores the same
    /// exact-fingerprint occurrence — with no fresh printer snapshot
    /// request, because the state should never have been dropped.
    func testIncompletePaginationOmissionPreservesMediaUntilLaterPageRestoresIt() async {
        let item = makeAttentionItem(
            id: "failure:media:incomplete-preserve",
            printerID: printerA,
            occurredAt: fixedNow,
            jobID: jobA
        )
        let preservedData = Data([0xAB, 0xCD])
        let source = ScriptedAttentionSnapshotSource(
            stepsByPrinterID: [
                printerA: [.outcome(.value(preservedData))],
            ]
        )
        let service = ScriptedAttentionService(
            steps: [
                .value(makeAttentionFeed(items: [item])),
                .value(makeAttentionFeed(nextCursor: "incomplete-omission-cursor")),
                .value(makeAttentionFeed(items: [item])),
            ]
        )
        let vm = configuredViewModel(
            service: service,
            printerService: snapshotPrinterService(source: source)
        )
        let initialLoadSucceeded = await vm.refresh()
        XCTAssertTrue(initialLoadSucceeded)
        let mediaLoaded = await vm.loadSnapshot(for: item.id)
        XCTAssertTrue(mediaLoaded)
        XCTAssertEqual(vm.mediaState(for: item.id), .available(preservedData))
        let initialSnapshotCallCount = await source.callCount(for: printerA)
        XCTAssertEqual(initialSnapshotCallCount, 1)

        // First-page canonical refresh omits the item with more pages
        // pending. The item disappears from the snapshot temporarily, but
        // its media entry must remain in the internal store so the same
        // fingerprint can bring it back below without a re-fetch.
        let incompleteOmissionSucceeded = await vm.refresh()
        XCTAssertTrue(incompleteOmissionSucceeded)
        XCTAssertTrue(vm.snapshot?.items.isEmpty == true,
            "The incomplete-omission refresh's first page is genuinely empty.")

        let restoringPageSucceeded = await vm.loadMore()
        XCTAssertTrue(restoringPageSucceeded)
        XCTAssertEqual(vm.mediaState(for: item.id), .available(preservedData),
            "The exact-fingerprint occurrence must restore the preserved media entry unchanged.")
        let restoredSnapshotCallCount = await source.callCount(for: printerA)
        XCTAssertEqual(restoredSnapshotCallCount, 1,
            "No fresh snapshot request should have been issued — the media was preserved through pagination.")
        let feedCallCount = await service.loadCallCount
        XCTAssertEqual(feedCallCount, 3,
            "Initial refresh + omission refresh + loadMore restoration = 3 feed calls.")
    }

    /// When an omission arrives on the *final* page (`hasMorePages == false`)
    /// the canonical occurrence set is complete. Media, action state, and
    /// any per-fingerprint bookkeeping must all clear so a later
    /// reappearance of the same id/printer/job/toolhead observes idle
    /// state and triggers a fresh snapshot request.
    func testCompleteFinalPaginationOmissionClearsMediaAndReappearanceIsFresh() async {
        let item = makeAttentionItem(
            id: "failure:media:complete-clear",
            printerID: printerA,
            occurredAt: fixedNow,
            jobID: jobA
        )
        let originalData = Data([0x11])
        let freshData = Data([0x22])
        let source = ScriptedAttentionSnapshotSource(
            stepsByPrinterID: [
                printerA: [
                    .outcome(.value(originalData)),
                    .outcome(.value(freshData)),
                ],
            ]
        )
        let service = ScriptedAttentionService(
            steps: [
                .value(makeAttentionFeed(items: [item])),
                .value(makeAttentionFeed()),
                .value(makeAttentionFeed(items: [item])),
            ]
        )
        let vm = configuredViewModel(
            service: service,
            printerService: snapshotPrinterService(source: source)
        )
        let initialLoadSucceeded = await vm.refresh()
        XCTAssertTrue(initialLoadSucceeded)
        let mediaLoaded = await vm.loadSnapshot(for: item.id)
        XCTAssertTrue(mediaLoaded)
        XCTAssertEqual(vm.mediaState(for: item.id), .available(originalData))

        // Complete omission: page is empty AND has no next cursor, so the
        // canonical set is fully known. The stored media entry must be
        // dropped, matching the action-authority revoke path.
        let completeOmissionSucceeded = await vm.refresh()
        XCTAssertTrue(completeOmissionSucceeded)
        XCTAssertTrue(vm.snapshot?.items.isEmpty == true)

        // Reappearance: item comes back with an unchanged fingerprint. The
        // media entry was dropped, so `.mediaState(for:)` reads `.idle` and
        // a fresh snapshot load is required.
        let reappearanceSucceeded = await vm.refresh()
        XCTAssertTrue(reappearanceSucceeded)
        XCTAssertEqual(vm.mediaState(for: item.id), .idle,
            "Complete omission must have cleared the preserved media before reappearance.")

        let freshLoadSucceeded = await vm.loadSnapshot(for: item.id)
        XCTAssertTrue(freshLoadSucceeded)
        XCTAssertEqual(vm.mediaState(for: item.id), .available(freshData))
        let totalSnapshotCallCount = await source.callCount(for: printerA)
        XCTAssertEqual(totalSnapshotCallCount, 2,
            "Complete omission must force a fresh printer snapshot request on reappearance.")
        let feedCallCount = await service.loadCallCount
        XCTAssertEqual(feedCallCount, 3)
    }

    /// A gated action whose POST is still in flight when a *complete*
    /// canonical omission arrives (empty page, `hasMorePages == false`)
    /// must have its authority revoked. When the POST later succeeds, the
    /// success branch's `matchesActionOperation` check must fail (token was
    /// cleared by the revoke) so the completion produces no state
    /// mutation and, critically, does not trigger a post-success canonical
    /// refresh — the revoke has already established that the item is gone.
    func testLateActionCompletionAfterCompleteOmissionIsNoOp() async {
        let action = AttentionAction(
            kind: .acknowledge,
            label: "Acknowledge",
            requiresConfirmation: false
        )
        let item = makeAttentionItem(
            id: "failure:late-completion-after-omission",
            printerID: printerA,
            occurredAt: fixedNow,
            actions: [action],
            jobID: jobA
        )
        let actionGate = AttentionResultGate<AttentionActionResult>()
        defer { actionGate.cancel() }
        let service = ScriptedAttentionService(
            steps: [
                .value(makeAttentionFeed(items: [item])),
                .value(makeAttentionFeed()),
            ],
            actionSteps: [.gated(actionGate)]
        )
        let vm = configuredViewModel(service: service, now: fixedNow)
        let initialLoadSucceeded = await vm.refresh()
        XCTAssertTrue(initialLoadSucceeded)

        let actionTask = Task { await vm.performAction(action, for: item.id) }
        await service.waitForActionCount(1)
        XCTAssertEqual(vm.actionState(for: item.id), .inProgress(.acknowledge))

        // Complete canonical omission arrives while the POST is still in
        // flight. The `!hasMorePages` branch of `reconcileItemScopedState`
        // must revoke the pending fingerprint's authority immediately.
        let omissionRefreshSucceeded = await vm.refresh()
        XCTAssertTrue(omissionRefreshSucceeded)
        XCTAssertTrue(vm.snapshot?.items.isEmpty == true)
        XCTAssertEqual(vm.actionState(for: item.id), .idle,
            "Complete omission must revoke the in-flight action's authority.")

        // Late POST success: the token was cleared by the revoke, so
        // `matchesActionOperation` must reject the completion and return
        // false without publishing state or triggering a follow-up refresh.
        await actionGate.succeed(AttentionActionResult(outcome: "Ok"))
        let lateCompletionAccepted = await actionTask.value
        XCTAssertFalse(lateCompletionAccepted,
            "A POST success that lands after complete omission must produce no state mutation.")
        XCTAssertEqual(vm.actionState(for: item.id), .idle)

        let finalFeedCallCount = await service.loadCallCount
        XCTAssertEqual(finalFeedCallCount, 2,
            "Revoked completions must not trigger the post-success canonical refresh.")
        let finalActionCallCount = await service.actionCallCount
        XCTAssertEqual(finalActionCallCount, 1)
    }

    func testSameIDNewOccurrenceClearsOldActionFailure() async throws {
        let action = AttentionAction(
            kind: .resume,
            label: "Resume",
            requiresConfirmation: false
        )
        let original = makeAttentionItem(
            id: "failure:reused-failure",
            printerID: printerA,
            occurredAt: fixedNow,
            actions: [action],
            jobID: jobA
        )
        let replacement = makeAttentionItem(
            id: original.id,
            printerID: printerA,
            occurredAt: fixedNow.addingTimeInterval(60),
            actions: [action],
            jobID: jobA
        )
        let service = ScriptedAttentionService(
            steps: [
                .value(makeAttentionFeed(items: [original])),
                .value(makeAttentionFeed(items: [replacement])),
            ],
            actionSteps: [
                .failure(.network("Old occurrence failed")),
            ]
        )
        let vm = configuredViewModel(service: service)
        let initialLoadSucceeded = await vm.refresh()
        let oldActionSucceeded = await vm.performAction(
            action,
            for: original.id
        )
        XCTAssertTrue(initialLoadSucceeded)
        XCTAssertFalse(oldActionSucceeded)
        let oldFailure = try actionFailure(vm.actionState(for: original.id))

        let replacementLoadSucceeded = await vm.refresh()
        XCTAssertTrue(replacementLoadSucceeded)
        XCTAssertEqual(vm.actionState(for: replacement.id), .idle)
        let oldRetryAccepted = await vm.retryAction(failureID: oldFailure.id)
        XCTAssertFalse(oldRetryAccepted)
        let actionCallCount = await service.actionCallCount
        XCTAssertEqual(actionCallCount, 1)
    }

    func testDelayedTapCannotTargetSameIDReplacementOccurrence() async {
        let action = AttentionAction(
            kind: .resume,
            label: "Resume",
            requiresConfirmation: true
        )
        let original = makeAttentionItem(
            id: "failure:delayed-tap",
            printerID: printerA,
            occurredAt: fixedNow,
            actions: [action],
            jobID: jobA
        )
        let replacement = makeAttentionItem(
            id: original.id,
            printerID: printerA,
            occurredAt: fixedNow.addingTimeInterval(30),
            actions: [action],
            jobID: jobA
        )
        let service = ScriptedAttentionService(
            steps: [
                .value(makeAttentionFeed(items: [original])),
                .value(makeAttentionFeed(items: [replacement])),
            ]
        )
        let vm = configuredViewModel(service: service)
        let initialLoadSucceeded = await vm.refresh()
        XCTAssertTrue(initialLoadSucceeded)
        let tappedFingerprint = AttentionOccurrenceFingerprint(item: original)

        let replacementLoadSucceeded = await vm.refresh()
        XCTAssertTrue(replacementLoadSucceeded)
        let delayedTapAccepted = await vm.performAction(
            action,
            for: tappedFingerprint
        )
        XCTAssertFalse(delayedTapAccepted)
        let actionCallCount = await service.actionCallCount
        XCTAssertEqual(actionCallCount, 0)
        XCTAssertEqual(vm.actionState(for: replacement.id), .idle)
    }

    func testSameIDNewOccurrenceClearsOldRefreshPendingAuthority() async {
        let action = AttentionAction(
            kind: .resume,
            label: "Resume",
            requiresConfirmation: false
        )
        let original = makeAttentionItem(
            id: "failure:reused-refresh",
            printerID: printerA,
            occurredAt: fixedNow,
            actions: [action],
            jobID: jobA
        )
        let replacement = makeAttentionItem(
            id: original.id,
            printerID: printerA,
            occurredAt: fixedNow.addingTimeInterval(120),
            actions: [action],
            jobID: jobA
        )
        let service = ScriptedAttentionService(
            steps: [
                .value(makeAttentionFeed(items: [original])),
                .failure(.network("Old canonical refresh failed")),
                .value(makeAttentionFeed(items: [replacement])),
            ],
            actionSteps: [
                .value(AttentionActionResult(outcome: "Ok")),
            ]
        )
        let vm = configuredViewModel(service: service)
        let initialLoadSucceeded = await vm.refresh()
        let mutationSucceeded = await vm.performAction(
            action,
            for: original.id
        )
        XCTAssertTrue(initialLoadSucceeded)
        XCTAssertTrue(mutationSucceeded)
        guard case .refreshPending(let oldPending) = vm.actionState(
            for: original.id
        ) else {
            return XCTFail("Original occurrence must hold refresh authority")
        }

        let replacementLoadSucceeded = await vm.refresh()
        XCTAssertTrue(replacementLoadSucceeded)
        XCTAssertEqual(vm.actionState(for: replacement.id), .idle)
        let oldRefreshRetryAccepted = await vm.retryActionRefresh(
            pendingID: oldPending.id
        )
        XCTAssertFalse(oldRefreshRetryAccepted)
        let actionCallCount = await service.actionCallCount
        XCTAssertEqual(actionCallCount, 1)
    }

    func testLateOldActionSuccessCannotAffectSameIDNewOccurrence() async {
        let action = AttentionAction(
            kind: .resume,
            label: "Resume",
            requiresConfirmation: false
        )
        let original = makeAttentionItem(
            id: "failure:reused-late-success",
            printerID: printerA,
            occurredAt: fixedNow,
            actions: [action],
            jobID: jobA
        )
        let replacement = makeAttentionItem(
            id: original.id,
            printerID: printerA,
            occurredAt: fixedNow.addingTimeInterval(180),
            actions: [action],
            jobID: jobA
        )
        let gate = AttentionResultGate<AttentionActionResult>()
        defer { gate.cancel() }
        let service = ScriptedAttentionService(
            steps: [
                .value(makeAttentionFeed(items: [original])),
                .value(makeAttentionFeed(items: [replacement])),
            ],
            actionSteps: [.gated(gate)]
        )
        let vm = configuredViewModel(service: service)
        let initialLoadSucceeded = await vm.refresh()
        XCTAssertTrue(initialLoadSucceeded)

        let oldAction = Task {
            await vm.performAction(action, for: original.id)
        }
        await service.waitForActionCount(1)
        let replacementLoadSucceeded = await vm.refresh()
        XCTAssertTrue(replacementLoadSucceeded)
        XCTAssertEqual(vm.actionState(for: replacement.id), .idle)

        await gate.succeed(AttentionActionResult(outcome: "Ok"))
        let oldActionSucceeded = await oldAction.value
        XCTAssertFalse(oldActionSucceeded)
        XCTAssertEqual(vm.actionState(for: replacement.id), .idle)
        let actionCallCount = await service.actionCallCount
        let loadCallCount = await service.loadCallCount
        XCTAssertEqual(actionCallCount, 1)
        XCTAssertEqual(loadCallCount, 2)
    }

    func testLateOldActionFailureCannotAttachToSameIDNewOccurrence() async {
        let action = AttentionAction(
            kind: .resume,
            label: "Resume",
            requiresConfirmation: false
        )
        let original = makeAttentionItem(
            id: "failure:reused-late-failure",
            printerID: printerA,
            occurredAt: fixedNow,
            actions: [action],
            jobID: jobA
        )
        let replacement = makeAttentionItem(
            id: original.id,
            printerID: printerA,
            occurredAt: fixedNow.addingTimeInterval(240),
            actions: [action],
            jobID: jobA
        )
        let gate = AttentionResultGate<AttentionActionResult>()
        defer { gate.cancel() }
        let service = ScriptedAttentionService(
            steps: [
                .value(makeAttentionFeed(items: [original])),
                .value(makeAttentionFeed(items: [replacement])),
            ],
            actionSteps: [.gated(gate)]
        )
        let vm = configuredViewModel(service: service)
        let initialLoadSucceeded = await vm.refresh()
        XCTAssertTrue(initialLoadSucceeded)

        let oldAction = Task {
            await vm.performAction(action, for: original.id)
        }
        await service.waitForActionCount(1)
        let replacementLoadSucceeded = await vm.refresh()
        XCTAssertTrue(replacementLoadSucceeded)

        await gate.fail(.network("Old occurrence failed late"))
        let oldActionSucceeded = await oldAction.value
        XCTAssertFalse(oldActionSucceeded)
        XCTAssertEqual(vm.actionState(for: replacement.id), .idle)
        let actionCallCount = await service.actionCallCount
        XCTAssertEqual(actionCallCount, 1)
    }

    func testSameIDNewOccurrenceRejectsOldSnoozeRetryPayload() async throws {
        let action = AttentionAction(
            kind: .snooze,
            label: "Snooze",
            requiresConfirmation: false
        )
        let original = makeAttentionItem(
            id: "runout:reused-snooze",
            kind: .runout,
            printerID: printerA,
            occurredAt: fixedNow,
            actions: [action],
            jobID: jobA
        )
        let replacement = makeAttentionItem(
            id: original.id,
            kind: .runout,
            printerID: printerA,
            occurredAt: fixedNow.addingTimeInterval(300),
            actions: [action],
            jobID: jobA
        )
        let service = ScriptedAttentionService(
            steps: [
                .value(makeAttentionFeed(items: [original])),
                .value(makeAttentionFeed(items: [replacement])),
            ],
            snoozeSteps: [
                .failure(.network("Old snooze failed")),
            ]
        )
        let vm = configuredViewModel(service: service, now: fixedNow)
        let initialLoadSucceeded = await vm.refresh()
        let oldSnoozeSucceeded = await vm.performAction(
            action,
            for: original.id
        )
        XCTAssertTrue(initialLoadSucceeded)
        XCTAssertFalse(oldSnoozeSucceeded)
        let oldFailure = try actionFailure(vm.actionState(for: original.id))
        let oldDeadline = try XCTUnwrap(oldFailure.snoozedUntilUtc)

        let replacementLoadSucceeded = await vm.refresh()
        XCTAssertTrue(replacementLoadSucceeded)
        XCTAssertEqual(vm.actionState(for: replacement.id), .idle)
        let oldRetryAccepted = await vm.retryAction(failureID: oldFailure.id)
        XCTAssertFalse(oldRetryAccepted)
        let snoozeCalls = await service.snoozeCalls
        XCTAssertEqual(snoozeCalls.map(\.snoozedUntilUtc), [oldDeadline])
    }

    func testActionFailureIsItemScopedAndRetryRefreshesOnce() async throws {
        let action = AttentionAction(
            kind: .acknowledge,
            label: "Acknowledge",
            requiresConfirmation: false
        )
        let itemA = makeAttentionItem(
            id: "maintenance:A",
            kind: .maintenance,
            severity: .warning,
            printerID: printerA,
            actions: [action]
        )
        let itemB = makeAttentionItem(
            id: "maintenance:B",
            kind: .maintenance,
            severity: .warning,
            printerID: printerB,
            actions: [action]
        )
        let feed = makeAttentionFeed(items: [itemA, itemB])
        let service = ScriptedAttentionService(
            steps: [.value(feed), .value(feed)],
            actionSteps: [
                .failure(.network("Maintenance service unavailable")),
                .value(AttentionActionResult(outcome: "Ok")),
            ]
        )
        let vm = configuredViewModel(service: service)
        let initialLoadSucceeded = await vm.refresh()
        XCTAssertTrue(initialLoadSucceeded)

        let firstActionSucceeded = await vm.performAction(action, for: itemA.id)
        XCTAssertFalse(firstActionSucceeded)
        guard case .failed(let failure) = vm.actionState(for: itemA.id) else {
            return XCTFail("The failing item must expose a retryable error")
        }
        XCTAssertEqual(failure.message, "Maintenance service unavailable")
        XCTAssertEqual(vm.actionState(for: itemB.id), .idle)
        let failedLoadCount = await service.loadCallCount
        XCTAssertEqual(failedLoadCount, 1)

        let retrySucceeded = await vm.retryAction(failureID: failure.id)
        XCTAssertTrue(retrySucceeded)
        XCTAssertEqual(vm.actionState(for: itemA.id), .idle)
        XCTAssertEqual(vm.actionState(for: itemB.id), .idle)
        let actionCount = await service.actionCallCount
        let loadCount = await service.loadCallCount
        XCTAssertEqual(actionCount, 2)
        XCTAssertEqual(loadCount, 2)
    }

    func testSnoozeKeepsSingleFieldDeadlineAndRemovesOnlyAfterRefresh() async throws {
        let action = AttentionAction(
            kind: .snooze,
            label: "Snooze",
            requiresConfirmation: false
        )
        let item = makeAttentionItem(
            id: "runout:snooze",
            kind: .runout,
            printerID: printerA,
            actions: [action]
        )
        let expectedDeadline = fixedNow.addingTimeInterval(
            AttentionFeedViewModel.defaultSnoozeInterval
        )
        let service = ScriptedAttentionService(
            steps: [
                .value(makeAttentionFeed(items: [item])),
                .value(makeAttentionFeed()),
            ],
            snoozeSteps: [
                .failure(.network("Snooze rejected")),
                .value(
                    SnoozeAttentionResponse(
                        snoozedUntilUtc: expectedDeadline,
                        attentionItemAnchorAtUtc: item.occurredAt
                    )
                ),
            ]
        )
        let vm = configuredViewModel(service: service, now: fixedNow)
        let initialLoadSucceeded = await vm.refresh()
        XCTAssertTrue(initialLoadSucceeded)

        let firstSnoozeSucceeded = await vm.performAction(action, for: item.id)
        XCTAssertFalse(firstSnoozeSucceeded)
        XCTAssertEqual(
            vm.snapshot?.items.map(\.id),
            [item.id],
            "A snooze error must not remove local server truth"
        )
        guard case .failed(let failure) = vm.actionState(for: item.id) else {
            return XCTFail("Snooze errors must be retryable on the item")
        }
        XCTAssertEqual(failure.snoozedUntilUtc, expectedDeadline)
        let failedLoadCount = await service.loadCallCount
        XCTAssertEqual(failedLoadCount, 1)

        let retrySucceeded = await vm.retryAction(failureID: failure.id)
        XCTAssertTrue(retrySucceeded)
        let snoozeCalls = await service.snoozeCalls
        XCTAssertEqual(
            snoozeCalls,
            [
                AttentionSnoozeCall(
                    itemID: item.id,
                    snoozedUntilUtc: expectedDeadline
                ),
                AttentionSnoozeCall(
                    itemID: item.id,
                    snoozedUntilUtc: expectedDeadline
                ),
            ],
            "Retry must repeat the same shipped single-field snooze request"
        )
        let actionCount = await service.actionCallCount
        let completedLoadCount = await service.loadCallCount
        XCTAssertEqual(actionCount, 0)
        XCTAssertEqual(completedLoadCount, 2)
        XCTAssertTrue(vm.snapshot?.items.isEmpty == true)

        let requestData = try JSONEncoder().encode(
            SnoozeAttentionRequest(snoozedUntilUtc: expectedDeadline)
        )
        let object = try XCTUnwrap(
            JSONSerialization.jsonObject(with: requestData)
                as? [String: Any]
        )
        XCTAssertEqual(Set(object.keys), ["snoozedUntilUtc"])
    }

    func testOnePendingItemDoesNotWedgeAnotherItem() async {
        let resume = AttentionAction(
            kind: .resume,
            label: "Resume",
            requiresConfirmation: false
        )
        let acknowledge = AttentionAction(
            kind: .acknowledge,
            label: "Acknowledge",
            requiresConfirmation: false
        )
        let itemA = makeAttentionItem(
            id: "failure:isolated",
            printerID: printerA,
            actions: [resume]
        )
        let itemB = makeAttentionItem(
            id: "maintenance:isolated",
            kind: .maintenance,
            severity: .warning,
            printerID: printerB,
            actions: [acknowledge]
        )
        let feed = makeAttentionFeed(items: [itemA, itemB])
        let gate = AttentionResultGate<AttentionActionResult>()
        defer { gate.cancel() }
        let service = ScriptedAttentionService(
            steps: [.value(feed), .value(feed)],
            actionSteps: [
                .gated(gate),
                .value(AttentionActionResult(outcome: "Ok")),
            ]
        )
        let vm = configuredViewModel(service: service)
        let initialLoadSucceeded = await vm.refresh()
        XCTAssertTrue(initialLoadSucceeded)

        let itemATask = Task {
            await vm.performAction(resume, for: itemA.id)
        }
        await service.waitForActionCount(1)
        XCTAssertEqual(vm.actionState(for: itemA.id), .inProgress(.resume))

        let itemBSucceeded = await vm.performAction(acknowledge, for: itemB.id)
        XCTAssertTrue(itemBSucceeded)
        XCTAssertEqual(vm.actionState(for: itemB.id), .idle)
        XCTAssertEqual(vm.actionState(for: itemA.id), .inProgress(.resume))
        let loadCount = await service.loadCallCount
        XCTAssertEqual(loadCount, 2)

        await gate.fail(.network("Resume failed"))
        let itemASucceeded = await itemATask.value
        XCTAssertFalse(itemASucceeded)
        guard case .failed = vm.actionState(for: itemA.id) else {
            return XCTFail("The first item should own its failure")
        }
        XCTAssertEqual(vm.actionState(for: itemB.id), .idle)
        let actionCount = await service.actionCallCount
        XCTAssertEqual(actionCount, 2)
    }

    func testSnapshotLoadsAreOnePerLiveItemAndIndependent() async {
        let itemA = makeAttentionItem(
            id: "failure:media:A",
            printerID: printerA
        )
        let itemB = makeAttentionItem(
            id: "failure:media:B",
            printerID: printerB
        )
        let gateA = AttentionSnapshotGate()
        let gateB = AttentionSnapshotGate()
        defer {
            gateA.cancel()
            gateB.cancel()
        }
        let source = ScriptedAttentionSnapshotSource(
            stepsByPrinterID: [
                printerA: [.gated(gateA)],
                printerB: [.gated(gateB)],
            ]
        )
        let printerService = snapshotPrinterService(source: source)
        let service = ScriptedAttentionService(
            steps: [.value(makeAttentionFeed(items: [itemA, itemB]))]
        )
        let vm = configuredViewModel(
            service: service,
            printerService: printerService
        )
        let initialLoadSucceeded = await vm.refresh()
        XCTAssertTrue(initialLoadSucceeded)

        let loadA = Task { await vm.loadSnapshot(for: itemA.id) }
        await source.waitForCallCount(1)
        let duplicateAccepted = await vm.loadSnapshot(for: itemA.id)
        let pendingPrinterACount = await source.callCount(for: printerA)
        XCTAssertFalse(duplicateAccepted)
        XCTAssertEqual(pendingPrinterACount, 1)

        let loadB = Task { await vm.loadSnapshot(for: itemB.id) }
        await source.waitForCallCount(2)
        XCTAssertEqual(vm.mediaState(for: itemA.id), .loading)
        XCTAssertEqual(vm.mediaState(for: itemB.id), .loading)

        let imageB = Data([0x42])
        await gateB.resolve(.value(imageB))
        let loadBSucceeded = await loadB.value
        XCTAssertTrue(loadBSucceeded)
        XCTAssertEqual(vm.mediaState(for: itemB.id), .available(imageB))
        XCTAssertEqual(vm.mediaState(for: itemA.id), .loading)

        await gateA.resolve(.failure(.network("Camera A unavailable")))
        let loadASucceeded = await loadA.value
        XCTAssertFalse(loadASucceeded)
        XCTAssertEqual(
            vm.mediaState(for: itemA.id),
            .unavailable("Camera A unavailable")
        )
        XCTAssertEqual(vm.mediaState(for: itemB.id), .available(imageB))
        let printerACount = await source.callCount(for: printerA)
        let printerBCount = await source.callCount(for: printerB)
        XCTAssertEqual(printerACount, 1)
        XCTAssertEqual(printerBCount, 1)
    }

    func testAvailableMediaClearsWhenSameIDChangesProvenance() async {
        let original = makeAttentionItem(
            id: "failure:media:available-replacement",
            printerID: printerA,
            occurredAt: fixedNow,
            jobID: jobA
        )
        let replacement = makeAttentionItem(
            id: original.id,
            printerID: printerB,
            occurredAt: fixedNow.addingTimeInterval(60),
            jobID: jobB
        )
        let originalData = Data([0xA1])
        let replacementData = Data([0xB2])
        let source = ScriptedAttentionSnapshotSource(
            stepsByPrinterID: [
                printerA: [.outcome(.value(originalData))],
                printerB: [.outcome(.value(replacementData))],
            ]
        )
        let service = ScriptedAttentionService(
            steps: [
                .value(makeAttentionFeed(items: [original])),
                .value(makeAttentionFeed(items: [replacement])),
            ]
        )
        let vm = configuredViewModel(
            service: service,
            printerService: snapshotPrinterService(source: source)
        )
        let initialLoadSucceeded = await vm.refresh()
        let originalLoadSucceeded = await vm.loadSnapshot(for: original.id)
        XCTAssertTrue(initialLoadSucceeded)
        XCTAssertTrue(originalLoadSucceeded)
        XCTAssertEqual(vm.mediaState(for: original.id), .available(originalData))

        let replacementRefreshSucceeded = await vm.refresh()
        XCTAssertTrue(replacementRefreshSucceeded)
        XCTAssertEqual(vm.mediaState(for: replacement.id), .idle)
        let originalCallCount = await source.callCount(for: printerA)
        let replacementCountBeforeLoad = await source.callCount(for: printerB)
        XCTAssertEqual(originalCallCount, 1)
        XCTAssertEqual(replacementCountBeforeLoad, 0)

        let replacementLoadSucceeded = await vm.loadSnapshot(
            for: replacement.id
        )
        XCTAssertTrue(replacementLoadSucceeded)
        XCTAssertEqual(
            vm.mediaState(for: replacement.id),
            .available(replacementData)
        )
        let replacementCallCount = await source.callCount(for: printerB)
        XCTAssertEqual(replacementCallCount, 1)
    }

    func testUnavailableMediaClearsWhenSameIDOccurrenceChanges() async {
        let original = makeAttentionItem(
            id: "failure:media:unavailable-replacement",
            printerID: printerA,
            occurredAt: fixedNow,
            jobID: jobA
        )
        let replacement = makeAttentionItem(
            id: original.id,
            printerID: printerA,
            occurredAt: fixedNow.addingTimeInterval(120),
            jobID: jobB
        )
        let replacementData = Data([0xC3])
        let source = ScriptedAttentionSnapshotSource(
            stepsByPrinterID: [
                printerA: [
                    .outcome(.failure(.network("Old occurrence unavailable"))),
                    .outcome(.value(replacementData)),
                ],
            ]
        )
        let service = ScriptedAttentionService(
            steps: [
                .value(makeAttentionFeed(items: [original])),
                .value(makeAttentionFeed(items: [replacement])),
            ]
        )
        let vm = configuredViewModel(
            service: service,
            printerService: snapshotPrinterService(source: source)
        )
        let initialLoadSucceeded = await vm.refresh()
        let originalLoadSucceeded = await vm.loadSnapshot(for: original.id)
        XCTAssertTrue(initialLoadSucceeded)
        XCTAssertFalse(originalLoadSucceeded)
        XCTAssertEqual(
            vm.mediaState(for: original.id),
            .unavailable("Old occurrence unavailable")
        )

        let replacementRefreshSucceeded = await vm.refresh()
        XCTAssertTrue(replacementRefreshSucceeded)
        XCTAssertEqual(vm.mediaState(for: replacement.id), .idle)
        let replacementLoadSucceeded = await vm.loadSnapshot(
            for: replacement.id
        )
        XCTAssertTrue(replacementLoadSucceeded)
        XCTAssertEqual(
            vm.mediaState(for: replacement.id),
            .available(replacementData)
        )
        let callCount = await source.callCount(for: printerA)
        XCTAssertEqual(callCount, 2)
    }

    func testInFlightOldOccurrenceCannotApplyAfterSameIDReplacement() async {
        let original = makeAttentionItem(
            id: "failure:media:inflight-replacement",
            printerID: printerA,
            occurredAt: fixedNow,
            jobID: jobA
        )
        let replacement = makeAttentionItem(
            id: original.id,
            printerID: printerA,
            occurredAt: fixedNow.addingTimeInterval(180),
            jobID: jobB
        )
        let oldGate = AttentionSnapshotGate()
        defer { oldGate.cancel() }
        let replacementData = Data([0xD4])
        let source = ScriptedAttentionSnapshotSource(
            stepsByPrinterID: [
                printerA: [
                    .gated(oldGate),
                    .outcome(.value(replacementData)),
                ],
            ]
        )
        let service = ScriptedAttentionService(
            steps: [
                .value(makeAttentionFeed(items: [original])),
                .value(makeAttentionFeed(items: [replacement])),
            ]
        )
        let vm = configuredViewModel(
            service: service,
            printerService: snapshotPrinterService(source: source)
        )
        let initialLoadSucceeded = await vm.refresh()
        XCTAssertTrue(initialLoadSucceeded)

        let oldLoad = Task { await vm.loadSnapshot(for: original.id) }
        await source.waitForCallCount(1)
        let replacementRefreshSucceeded = await vm.refresh()
        XCTAssertTrue(replacementRefreshSucceeded)
        XCTAssertEqual(vm.mediaState(for: replacement.id), .idle)

        let replacementLoad = Task {
            await vm.loadSnapshot(for: replacement.id)
        }
        await source.waitForCallCount(2)
        let replacementLoadSucceeded = await replacementLoad.value
        XCTAssertTrue(replacementLoadSucceeded)
        XCTAssertEqual(
            vm.mediaState(for: replacement.id),
            .available(replacementData)
        )

        await oldGate.resolve(.value(Data([0xE5])))
        let oldLoadSucceeded = await oldLoad.value
        XCTAssertFalse(oldLoadSucceeded)
        XCTAssertEqual(
            vm.mediaState(for: replacement.id),
            .available(replacementData)
        )
        let callCount = await source.callCount(for: printerA)
        XCTAssertEqual(callCount, 2)
    }

    func testNativeSnapshotCancellationReturnsIdleAndRetries() async {
        await assertSnapshotCancellationRetries(.nativeCancellation)
    }

    func testBareURLSnapshotCancellationReturnsIdleAndRetries() async {
        await assertSnapshotCancellationRetries(.urlCancellation)
    }

    func testWrappedSnapshotCancellationReturnsIdleAndRetries() async {
        await assertSnapshotCancellationRetries(.wrappedCancellation)
    }

    func testWrappedTimeoutCachesUnavailableUntilExplicitRetry() async {
        let item = makeAttentionItem(
            id: "failure:media:timeout",
            printerID: printerA
        )
        let recovered = Data([0xAA])
        let source = ScriptedAttentionSnapshotSource(
            stepsByPrinterID: [
                printerA: [
                    .outcome(.wrappedTimeout),
                    .outcome(.value(recovered)),
                ],
            ]
        )
        let service = ScriptedAttentionService(
            steps: [.value(makeAttentionFeed(items: [item]))]
        )
        let vm = configuredViewModel(
            service: service,
            printerService: snapshotPrinterService(source: source)
        )
        let initialLoadSucceeded = await vm.refresh()
        XCTAssertTrue(initialLoadSucceeded)

        let firstLoadSucceeded = await vm.loadSnapshot(for: item.id)
        XCTAssertFalse(firstLoadSucceeded)
        guard case .unavailable = vm.mediaState(for: item.id) else {
            return XCTFail("A genuine wrapped transport failure must cache unavailable")
        }
        let repeatedDisplayAccepted = await vm.loadSnapshot(for: item.id)
        XCTAssertFalse(repeatedDisplayAccepted)
        let unavailableCallCount = await source.callCount(for: printerA)
        XCTAssertEqual(
            unavailableCallCount,
            1,
            "Repeated display requests must not thrash an unavailable camera"
        )

        let retrySucceeded = await vm.retrySnapshot(for: item.id)
        XCTAssertTrue(retrySucceeded)
        XCTAssertEqual(vm.mediaState(for: item.id), .available(recovered))
        let recoveredCallCount = await source.callCount(for: printerA)
        XCTAssertEqual(recoveredCallCount, 2)
    }

    func testExplicitRetryCanReplaceUndecodableAvailableData() async {
        let item = makeAttentionItem(
            id: "failure:media:decode",
            printerID: printerA
        )
        let undecodable = Data()
        let replacement = Data([0x89, 0x50, 0x4E, 0x47])
        let source = ScriptedAttentionSnapshotSource(
            stepsByPrinterID: [
                printerA: [
                    .outcome(.value(undecodable)),
                    .outcome(.value(replacement)),
                ],
            ]
        )
        let service = ScriptedAttentionService(
            steps: [.value(makeAttentionFeed(items: [item]))]
        )
        let vm = configuredViewModel(
            service: service,
            printerService: snapshotPrinterService(source: source)
        )
        let initialLoadSucceeded = await vm.refresh()
        XCTAssertTrue(initialLoadSucceeded)

        let firstLoadSucceeded = await vm.loadSnapshot(for: item.id)
        XCTAssertTrue(firstLoadSucceeded)
        XCTAssertEqual(vm.mediaState(for: item.id), .available(undecodable))

        let retrySucceeded = await vm.retrySnapshot(for: item.id)
        let callCount = await source.callCount(for: printerA)
        XCTAssertTrue(retrySucceeded)
        XCTAssertEqual(vm.mediaState(for: item.id), .available(replacement))
        XCTAssertEqual(callCount, 2)
    }

    func testFeedReplacementRejectsStaleSnapshotCompletion() async {
        let item = makeAttentionItem(
            id: "failure:media:stale",
            printerID: printerA
        )
        let oldGate = AttentionSnapshotGate()
        defer { oldGate.cancel() }
        let freshData = Data([0x22])
        let source = ScriptedAttentionSnapshotSource(
            stepsByPrinterID: [
                printerA: [
                    .gated(oldGate),
                    .outcome(.value(freshData)),
                ],
            ]
        )
        let feed = makeAttentionFeed(items: [item])
        let service = ScriptedAttentionService(
            steps: [.value(feed), .value(feed)]
        )
        let vm = configuredViewModel(
            service: service,
            printerService: snapshotPrinterService(source: source)
        )
        let initialLoadSucceeded = await vm.refresh()
        XCTAssertTrue(initialLoadSucceeded)

        let staleLoad = Task { await vm.loadSnapshot(for: item.id) }
        await source.waitForCallCount(1)
        let generationBeforeReplacement = vm.mediaGeneration

        let replacementLoadSucceeded = await vm.refresh()
        XCTAssertTrue(replacementLoadSucceeded)
        XCTAssertGreaterThan(vm.mediaGeneration, generationBeforeReplacement)
        XCTAssertEqual(vm.mediaState(for: item.id), .idle)

        await oldGate.resolve(.value(Data([0x11])))
        let staleLoadSucceeded = await staleLoad.value
        XCTAssertFalse(staleLoadSucceeded)
        XCTAssertEqual(
            vm.mediaState(for: item.id),
            .idle,
            "The old live-item generation must not publish media"
        )

        let retrySucceeded = await vm.retrySnapshot(for: item.id)
        XCTAssertTrue(retrySucceeded)
        XCTAssertEqual(vm.mediaState(for: item.id), .available(freshData))
        let callCount = await source.callCount(for: printerA)
        XCTAssertEqual(callCount, 2)
    }

    func testNavigationTargetsUseStableIDsWithDuplicateNames() {
        let first = makeAttentionItem(
            id: "failure:navigation:A",
            printerID: printerA,
            printerName: "Duplicate name",
            jobID: jobA
        )
        let second = makeAttentionItem(
            id: "failure:navigation:B",
            printerID: printerB,
            printerName: "Duplicate name",
            jobID: jobB
        )

        let firstTargets = AttentionNavigationTargets(item: first)
        let secondTargets = AttentionNavigationTargets(item: second)
        XCTAssertEqual(firstTargets.printer, .printerDetail(id: printerA))
        XCTAssertEqual(firstTargets.job, .jobDetail(id: jobA))
        XCTAssertEqual(secondTargets.printer, .printerDetail(id: printerB))
        XCTAssertEqual(secondTargets.job, .jobDetail(id: jobB))
        XCTAssertNotEqual(
            firstTargets,
            secondTargets
        )
    }

    func testAccessibilityDescriptorsCoverLabelsHintsStatesAndOrder() {
        let deadline = fixedNow.addingTimeInterval(600)
        let resume = AttentionAction(
            kind: .resume,
            label: "Resume",
            requiresConfirmation: true
        )
        let snooze = AttentionAction(
            kind: .snooze,
            label: "Snooze",
            requiresConfirmation: false
        )
        let item = makeAttentionItem(
            id: "failure:accessibility",
            printerID: printerA,
            printerName: "Duplicate name",
            actions: [resume, snooze],
            deadlineAt: deadline,
            jobID: jobA
        )
        let failure = AttentionActionFailure(
            id: UUID(),
            fingerprint: AttentionOccurrenceFingerprint(item: item),
            action: resume,
            snoozedUntilUtc: nil,
            message: "Printer refused the command"
        )
        let refreshPending = AttentionActionRefreshPending(
            id: UUID(),
            fingerprint: AttentionOccurrenceFingerprint(item: item),
            action: resume,
            message: "Canonical refresh failed"
        )

        let card = AttentionAccessibility.card(item)
        let severity = AttentionAccessibility.severity(item)
        let deadlineDescriptor = AttentionAccessibility.deadline(
            item,
            deadline: deadline
        )
        let image = AttentionAccessibility.media(
            item: item,
            state: .available(Data([0x01]))
        )
        let fallback = AttentionAccessibility.media(
            item: item,
            state: .unavailable("offline")
        )
        let progress = AttentionAccessibility.actionProgress(
            item: item,
            actionKind: .resume
        )
        let error = AttentionAccessibility.actionError(
            item: item,
            failure: failure
        )

        for descriptor in [
            card,
            AttentionAccessibility.summary(item),
            severity,
            deadlineDescriptor,
            image,
            fallback,
            progress,
            error,
            AttentionAccessibility.actionRefresh(
                item: item,
                pending: refreshPending
            ),
            AttentionAccessibility.navigation(
                item: item,
                destination: .printerDetail(id: printerA)
            ),
            AttentionAccessibility.navigation(
                item: item,
                destination: .jobDetail(id: jobA)
            ),
            AttentionAccessibility.action(item: item, action: resume),
            AttentionAccessibility.action(item: item, action: snooze),
        ] {
            XCTAssertFalse(descriptor.identifier.isEmpty)
            XCTAssertFalse(descriptor.label.isEmpty)
            XCTAssertFalse(descriptor.hint.isEmpty)
        }
        XCTAssertEqual(severity.label, "Severity, Critical")
        XCTAssertTrue(deadlineDescriptor.label.hasPrefix("Deadline, "))
        XCTAssertEqual(
            image.identifier,
            "attention.item.\(item.id).media.image"
        )
        XCTAssertEqual(
            fallback.identifier,
            "attention.item.\(item.id).media.unavailable"
        )
        XCTAssertEqual(progress.label, "Resume in progress")
        XCTAssertTrue(error.label.contains("Printer refused the command"))
        let refreshError = AttentionAccessibility.actionRefresh(
            item: item,
            pending: refreshPending
        )
        XCTAssertEqual(
            refreshError.identifier,
            "attention.item.\(item.id).action.refreshError"
        )
        XCTAssertTrue(refreshError.hint.contains("without repeating"))

        let collapsed = AttentionAccessibility.healthySummary(
            count: 4,
            expanded: false
        )
        let expanded = AttentionAccessibility.healthySummary(
            count: 4,
            expanded: true
        )
        XCTAssertEqual(collapsed.label, expanded.label)
        XCTAssertNotEqual(collapsed.hint, expanded.hint)

        XCTAssertEqual(
            AttentionAccessibility.orderedIdentifiers(
                item: item,
                mediaState: .available(Data([0x01])),
                actions: [resume, snooze],
                actionState: .failed(failure)
            ),
            [
                "attention.item.\(item.id).severity",
                "attention.item.\(item.id).summary",
                "attention.item.\(item.id).deadline",
                "attention.item.\(item.id).media.image",
                "attention.item.\(item.id).navigation.printer",
                "attention.item.\(item.id).navigation.job",
                "attention.item.\(item.id).action.resume",
                "attention.item.\(item.id).action.snooze",
                "attention.item.\(item.id).action.error",
                "attention.item.\(item.id).action.retry",
            ]
        )
        XCTAssertEqual(
            Array(
                AttentionAccessibility.orderedIdentifiers(
                    item: item,
                    mediaState: nil,
                    actions: [resume],
                    actionState: .refreshPending(refreshPending)
                ).suffix(2)
            ),
            [
                "attention.item.\(item.id).action.refreshError",
                "attention.item.\(item.id).action.refreshRetry",
            ]
        )

        let harvest = AttentionAction(
            kind: .harvest,
            label: "Harvest",
            requiresConfirmation: true
        )
        let harvestItem = makeAttentionItem(
            id: "harvest:scan-bin",
            kind: .harvest,
            severity: .warning,
            printerID: printerA,
            actions: [harvest],
            jobID: jobA
        )
        let harvestIdentifiers = AttentionAccessibility.orderedIdentifiers(
            item: harvestItem,
            mediaState: nil,
            actions: [harvest],
            actionState: .idle
        )
        XCTAssertTrue(
            harvestIdentifiers.contains(
                "attention.item.\(harvestItem.id).action.scanBin"
            )
        )
        XCTAssertFalse(
            harvestIdentifiers.contains(
                "attention.item.\(harvestItem.id).action.harvest"
            )
        )

        let fallbackHarvestItem = makeAttentionItem(
            id: "harvest:server-fallback",
            kind: .harvest,
            severity: .warning,
            printerID: printerA,
            actions: [harvest],
            jobID: nil
        )
        XCTAssertTrue(
            AttentionAccessibility.orderedIdentifiers(
                item: fallbackHarvestItem,
                mediaState: nil,
                actions: [harvest],
                actionState: .idle
            ).contains(
                "attention.item.\(fallbackHarvestItem.id).action.harvest"
            ),
            "A harvest item without job identity must keep its server action fallback"
        )
    }

    private func assertEventDuringActionRefresh(
        drainCallbackBeforeCompletion: Bool
    ) async {
        let action = AttentionAction(
            kind: .resume,
            label: "Resume",
            requiresConfirmation: false
        )
        let item = makeAttentionItem(
            id: drainCallbackBeforeCompletion
                ? "failure:event-during-before-drain"
                : "failure:event-during-after-drain",
            printerID: printerA,
            actions: [action]
        )
        let refreshGate = AttentionResultGate<AttentionFeed>()
        defer { refreshGate.cancel() }
        let callbackQueue = AttentionCallbackQueue()
        let signalR = MockSignalRService()
        let service = ScriptedAttentionService(
            steps: [
                .value(makeAttentionFeed(items: [item])),
                .gated(refreshGate),
                .value(makeAttentionFeed()),
            ],
            actionSteps: [
                .value(AttentionActionResult(outcome: "Ok")),
            ]
        )
        let vm = AttentionFeedViewModel(
            callbackEnqueuer: callbackQueue.enqueuer
        )
        vm.configure(
            attentionService: service,
            signalRService: signalR,
            attentionEnabled: true
        )
        let initialLoadSucceeded = await vm.refresh()
        XCTAssertTrue(initialLoadSucceeded)

        let actionTask = Task {
            await vm.performAction(action, for: item.id)
        }
        await service.waitForLoadCount(2)

        signalR.simulateAttentionChanged(
            AttentionChangedEvent(
                itemId: item.id,
                changeKind: .resolved,
                occurredAt: fixedNow
            )
        )
        await callbackQueue.waitForCount(1)
        if drainCallbackBeforeCompletion {
            await callbackQueue.runNext()
        }

        await refreshGate.succeed(makeAttentionFeed(items: [item]))
        let actionSucceeded = await actionTask.value
        XCTAssertTrue(actionSucceeded)

        if !drainCallbackBeforeCompletion {
            await callbackQueue.runNext()
        }

        let loadCount = await service.loadCallCount
        let actionCount = await service.actionCallCount
        XCTAssertEqual(loadCount, 3)
        XCTAssertEqual(actionCount, 1)
        XCTAssertEqual(callbackQueue.count, 0)
        XCTAssertTrue(vm.snapshot?.items.isEmpty == true)
        XCTAssertEqual(vm.actionState(for: item.id), .idle)
    }

    private func actionFailure(
        _ state: AttentionItemActionState
    ) throws -> AttentionActionFailure {
        guard case .failed(let failure) = state else {
            throw AttentionProofError.forced("Expected failed action state")
        }
        return failure
    }

    private func assertSnapshotCancellationRetries(
        _ cancellation: AttentionSnapshotOutcome
    ) async {
        let item = makeAttentionItem(
            id: "failure:media:cancellation",
            printerID: printerA
        )
        let recovered = Data([0x7A])
        let source = ScriptedAttentionSnapshotSource(
            stepsByPrinterID: [
                printerA: [
                    .outcome(cancellation),
                    .outcome(.value(recovered)),
                ],
            ]
        )
        let service = ScriptedAttentionService(
            steps: [.value(makeAttentionFeed(items: [item]))]
        )
        let vm = configuredViewModel(
            service: service,
            printerService: snapshotPrinterService(source: source)
        )
        let initialLoadSucceeded = await vm.refresh()
        XCTAssertTrue(initialLoadSucceeded)

        let cancelledLoadSucceeded = await vm.loadSnapshot(for: item.id)
        XCTAssertFalse(cancelledLoadSucceeded)
        XCTAssertEqual(vm.mediaState(for: item.id), .idle)
        let cancelledCallCount = await source.callCount(for: printerA)
        XCTAssertEqual(cancelledCallCount, 1)

        let retrySucceeded = await vm.retrySnapshot(for: item.id)
        XCTAssertTrue(retrySucceeded)
        XCTAssertEqual(vm.mediaState(for: item.id), .available(recovered))
        let recoveredCallCount = await source.callCount(for: printerA)
        XCTAssertEqual(recoveredCallCount, 2)
    }

    private func configuredViewModel(
        service: ScriptedAttentionService,
        printerService: MockPrinterService? = nil,
        now: Date? = nil
    ) -> AttentionFeedViewModel {
        let vm = now.map { fixed in
            AttentionFeedViewModel(now: { fixed })
        } ?? AttentionFeedViewModel()
        vm.configure(
            attentionService: service,
            signalRService: MockSignalRService(),
            attentionEnabled: true
        )
        if let printerService {
            vm.configureSnapshotService(printerService)
        }
        return vm
    }

    private func snapshotPrinterService(
        source: ScriptedAttentionSnapshotSource
    ) -> MockPrinterService {
        let service = MockPrinterService()
        service.snapshotHandler = { printerID in
            try await source.load(printerID: printerID)
        }
        return service
    }
}

private extension AttentionFeedViewModel {
    func performAction(
        _ action: AttentionAction,
        for itemID: String
    ) async -> Bool {
        guard let item = snapshot?.items.first(where: { $0.id == itemID }) else {
            return false
        }
        return await performAction(
            action,
            for: AttentionOccurrenceFingerprint(item: item)
        )
    }
}
