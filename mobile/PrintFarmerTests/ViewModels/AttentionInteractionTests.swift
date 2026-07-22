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
            itemID: item.id,
            action: resume,
            snoozedUntilUtc: nil,
            message: "Printer refused the command"
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
