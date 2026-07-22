import XCTest
@testable import PrintFarmer

// MARK: - AttentionFeedViewModel

@MainActor
final class AttentionFeedViewModelTests: XCTestCase {

    // Concrete Sendable identifiers used to seed distinct fixtures.
    private let printerA = "AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA"
    private let printerB = "BBBBBBBB-BBBB-BBBB-BBBB-BBBBBBBBBBBB"

    // MARK: - Loading & success

    func testInitialRefreshTransitionsLoadingToLoadedWithGroupedItems() async {
        let items = [
            makeAttentionItem(id: "failure:1", severity: .info, title: "Info"),
            makeAttentionItem(id: "runout:1", kind: .runout, severity: .critical, title: "Crit"),
            makeAttentionItem(id: "harvest:1", kind: .harvest, severity: .warning, title: "Warn"),
        ]
        let service = ScriptedAttentionService(
            steps: [.value(makeAttentionFeed(items: items, healthyPrinterCount: 3))]
        )
        let signalR = MockSignalRService()
        let vm = configuredViewModel(service: service, signalR: signalR)

        XCTAssertEqual(vm.phase, .idle)

        let ok = await vm.refresh()
        XCTAssertTrue(ok)

        XCTAssertEqual(vm.phase, .loaded)
        XCTAssertEqual(vm.snapshot?.items.count, 3)
        XCTAssertEqual(vm.snapshot?.healthyPrinterCount, 3)
        // Groups must be in canonical severity order.
        XCTAssertEqual(vm.groups.map(\.severity), [.critical, .warning, .info])
        // Within each group we preserve server order (just one item each here).
        XCTAssertEqual(vm.groups.first?.items.first?.title, "Crit")
        XCTAssertTrue(vm.shouldShowHealthySummary)
        XCTAssertFalse(vm.shouldShowEmpty)
        let loadCount = await service.loadCallCount
        XCTAssertEqual(loadCount, 1)
    }

    func testInitialLoadingIsExposedWhilePendingAndClearedOnSuccess() async {
        let gate = AttentionResultGate<AttentionFeed>()
        let service = ScriptedAttentionService(steps: [.gated(gate)])
        let signalR = MockSignalRService()
        let vm = configuredViewModel(service: service, signalR: signalR)

        let refreshTask = Task { await vm.refresh() }
        await service.waitForLoadCount(1)
        XCTAssertEqual(vm.phase, .loading, "Feed generation pending — must remain loading")
        XCTAssertNil(vm.snapshot)
        XCTAssertFalse(vm.shouldShowEmpty, "No all-clear flash before the first response")
        XCTAssertFalse(vm.shouldShowHealthySummary)

        await gate.succeed(makeAttentionFeed(healthyPrinterCount: 0))
        _ = await refreshTask.value

        XCTAssertEqual(vm.phase, .loaded)
        XCTAssertTrue(vm.shouldShowEmpty)
    }

    func testEmptyFeedWithZeroHealthySurfacesEmptyState() async {
        let service = ScriptedAttentionService(
            steps: [.value(makeAttentionFeed(items: [], healthyPrinterCount: 0))]
        )
        let vm = configuredViewModel(service: service)

        _ = await vm.refresh()

        XCTAssertEqual(vm.phase, .loaded)
        XCTAssertTrue(vm.shouldShowEmpty)
        XCTAssertFalse(vm.shouldShowHealthySummary)
        XCTAssertEqual(vm.groups.count, 0)
    }

    func testEmptyFeedWithHealthyPrintersRendersSummaryNotEmptyState() async {
        let service = ScriptedAttentionService(
            steps: [.value(makeAttentionFeed(items: [], healthyPrinterCount: 5))]
        )
        let vm = configuredViewModel(service: service)

        _ = await vm.refresh()

        XCTAssertEqual(vm.phase, .loaded)
        XCTAssertFalse(vm.shouldShowEmpty)
        XCTAssertTrue(vm.shouldShowHealthySummary)
        XCTAssertEqual(vm.snapshot?.healthyPrinterCount, 5)
    }

    // MARK: - Failure & retry

    func testFirstRefreshFailureTransitionsToErrorAndRetryRecovers() async throws {
        let service = ScriptedAttentionService(
            steps: [.failure(.forced("boom")), .value(makeAttentionFeed(healthyPrinterCount: 2))]
        )
        let vm = configuredViewModel(service: service)

        let firstOk = await vm.refresh()
        XCTAssertFalse(firstOk)
        XCTAssertEqual(vm.phase, .error)
        XCTAssertEqual(vm.loadFailure?.message, "boom")
        XCTAssertNil(vm.snapshot)

        let failureID = try XCTUnwrap(vm.loadFailure?.id)
        let retryOk = await vm.retryLoad(failureID: failureID)
        XCTAssertTrue(retryOk)
        XCTAssertEqual(vm.phase, .loaded)
        XCTAssertNil(vm.loadFailure)
        XCTAssertEqual(vm.snapshot?.healthyPrinterCount, 2)
    }

    func testRetryLoadWithStaleFailureIDIsRejected() async {
        let service = ScriptedAttentionService(steps: [.failure(.forced("boom"))])
        let vm = configuredViewModel(service: service)

        _ = await vm.refresh()
        XCTAssertEqual(vm.phase, .error)

        let stale = UUID()
        let rejected = await vm.retryLoad(failureID: stale)
        XCTAssertFalse(rejected)
        let calls = await service.loadCallCount
        XCTAssertEqual(calls, 1, "Stale retry must not trigger a new fetch")
    }

    func testRefreshFailureAfterLoadedKeepsSnapshotAndSurfacesInlineError() async {
        let firstFeed = makeAttentionFeed(
            items: [makeAttentionItem(id: "failure:1")],
            healthyPrinterCount: 1
        )
        let service = ScriptedAttentionService(
            steps: [.value(firstFeed), .failure(.forced("intermittent"))]
        )
        let vm = configuredViewModel(service: service)

        _ = await vm.refresh()
        XCTAssertEqual(vm.phase, .loaded)

        _ = await vm.refresh()
        // Phase stays .loaded so the shell keeps rendering the list;
        // the inline banner is driven by `loadFailure`.
        XCTAssertEqual(vm.phase, .loaded)
        XCTAssertEqual(vm.loadFailure?.message, "intermittent")
        XCTAssertNotNil(vm.snapshot)
        XCTAssertEqual(vm.snapshot?.items.count, 1)
    }

    // MARK: - Feature gating

    func testConfigureWithGateDisabledLandsInDisabledPhase() {
        let service = ScriptedAttentionService()
        let signalR = MockSignalRService()
        let vm = AttentionFeedViewModel()
        vm.configure(
            attentionService: service,
            signalRService: signalR,
            attentionEnabled: false
        )
        XCTAssertEqual(vm.phase, .disabled)
    }

    func testGatedResponseFromServerTransitionsToDisabled() async {
        let service = ScriptedAttentionService(steps: [.featureDisabled])
        let vm = configuredViewModel(service: service)

        _ = await vm.refresh()
        XCTAssertEqual(vm.phase, .disabled)
        XCTAssertNil(vm.snapshot)
        XCTAssertFalse(vm.shouldShowHealthySummary)
    }

    // MARK: - Generation authority

    func testReverseOrderCompletionsHonorOnlyNewestGeneration() async {
        let firstGate = AttentionResultGate<AttentionFeed>()
        let secondFeed = makeAttentionFeed(
            items: [makeAttentionItem(id: "runout:1", title: "Newest")],
            healthyPrinterCount: 7
        )
        let service = ScriptedAttentionService(
            steps: [.gated(firstGate), .value(secondFeed)]
        )
        let vm = configuredViewModel(service: service)

        let first = Task { await vm.refresh() }
        await service.waitForLoadCount(1)

        // Start a second refresh; it advances the load stamp.
        let second = Task { await vm.refresh() }
        await service.waitForLoadCount(2)

        // Resolve the older call last. It must be dropped.
        await firstGate.succeed(
            makeAttentionFeed(
                items: [makeAttentionItem(id: "failure:stale", title: "Stale")],
                healthyPrinterCount: 999
            )
        )

        let firstResult = await first.value
        let secondResult = await second.value
        XCTAssertFalse(firstResult, "Stale generation must not report success")
        XCTAssertTrue(secondResult)
        XCTAssertEqual(vm.snapshot?.items.first?.title, "Newest")
        XCTAssertEqual(vm.snapshot?.healthyPrinterCount, 7)
        XCTAssertEqual(vm.phase, .loaded)
    }

    func testStaleFailureCannotOverwriteNewerSuccess() async {
        let firstGate = AttentionResultGate<AttentionFeed>()
        let service = ScriptedAttentionService(
            steps: [
                .gated(firstGate),
                .value(makeAttentionFeed(healthyPrinterCount: 4)),
            ]
        )
        let vm = configuredViewModel(service: service)

        let first = Task { await vm.refresh() }
        await service.waitForLoadCount(1)
        let second = Task { await vm.refresh() }
        _ = await second.value

        await firstGate.fail(.forced("stale error"))
        _ = await first.value

        XCTAssertEqual(vm.phase, .loaded)
        XCTAssertNil(vm.loadFailure, "Stale failure must not surface")
        XCTAssertEqual(vm.snapshot?.healthyPrinterCount, 4)
    }

    func testStaleDisabledCannotOverwriteNewerSuccess() async {
        // Uses a real `NetworkError.featureDisabled` completion (the
        // shape produced by APIClient's #728/#725 gated-404 mapping) so
        // the assertion covers the actual disabled code path, not a
        // generic-error stand-in.
        let disabledGate = AttentionResultGate<Void>()
        let service = ScriptedAttentionService(
            steps: [
                .gatedFeatureDisabled(disabledGate),
                .value(makeAttentionFeed(healthyPrinterCount: 2)),
            ]
        )
        let vm = configuredViewModel(service: service)

        let first = Task { await vm.refresh() }
        await service.waitForLoadCount(1)
        _ = await vm.refresh() // newer generation

        // Older call now resolves as a real featureDisabled 404 after
        // the newer success has already applied. Must be dropped: the
        // shell must stay on the newer `.loaded` snapshot, not flip to
        // `.disabled`.
        await disabledGate.succeed(())
        _ = await first.value

        XCTAssertEqual(vm.phase, .loaded, "Stale featureDisabled must not flip newer success to disabled")
        XCTAssertEqual(vm.snapshot?.healthyPrinterCount, 2)
    }

    // MARK: - Activation-epoch fence

    func testActivationEpochFencesLoadAcrossDeactivateReactivate() async {
        // Activation A starts a refresh. View deactivates, then
        // reactivates (activation B). Activation-A's completion arrives
        // after the reactivation; it must not apply into B's state.
        let gateA = AttentionResultGate<AttentionFeed>()
        let feedB = makeAttentionFeed(
            items: [makeAttentionItem(id: "runout:B", title: "B")],
            healthyPrinterCount: 3
        )
        let service = ScriptedAttentionService(
            steps: [.gated(gateA), .value(feedB)]
        )
        let signalR = MockSignalRService()
        let vm = AttentionFeedViewModel()
        vm.configure(
            attentionService: service,
            signalRService: signalR,
            attentionEnabled: true
        )

        let activationATask = Task { await vm.refresh() }
        await service.waitForLoadCount(1)

        vm.deactivate()
        vm.activate() // activation B

        // Activation B issues its own refresh through the normal path.
        let activationBTask = Task { await vm.refresh() }
        await service.waitForLoadCount(2)

        // Now let activation-A finally return with a "poison" payload
        // that would look like a success if applied. It must drop.
        await gateA.succeed(
            makeAttentionFeed(
                items: [makeAttentionItem(id: "failure:poison", title: "Poison")],
                healthyPrinterCount: 999
            )
        )
        let aResult = await activationATask.value
        let bResult = await activationBTask.value

        XCTAssertFalse(aResult, "Activation A must not report success across the epoch fence")
        XCTAssertTrue(bResult)
        XCTAssertEqual(vm.snapshot?.items.first?.title, "B")
        XCTAssertEqual(vm.snapshot?.healthyPrinterCount, 3)
        XCTAssertEqual(vm.phase, .loaded)
    }

    func testActivationEpochFencesErrorAcrossDeactivateReactivate() async {
        // Same fence but the deferred activation-A completion is a
        // failure. It must not apply into activation-B's `.loaded`.
        let gateA = AttentionResultGate<AttentionFeed>()
        let service = ScriptedAttentionService(
            steps: [
                .gated(gateA),
                .value(makeAttentionFeed(healthyPrinterCount: 5)),
            ]
        )
        let signalR = MockSignalRService()
        let vm = AttentionFeedViewModel()
        vm.configure(
            attentionService: service,
            signalRService: signalR,
            attentionEnabled: true
        )

        let activationATask = Task { await vm.refresh() }
        await service.waitForLoadCount(1)

        vm.deactivate()
        vm.activate()

        _ = await vm.refresh()

        await gateA.fail(.forced("stale error"))
        _ = await activationATask.value

        XCTAssertEqual(vm.phase, .loaded)
        XCTAssertNil(vm.loadFailure, "Stale failure across an activation boundary must not surface")
        XCTAssertEqual(vm.snapshot?.healthyPrinterCount, 5)
    }

    // MARK: - Inactive pagination must not wedge

    func testLoadMoreCompletingWhileInactiveClearsFlagAndAllowsFutureLoad() async {
        // Regression: reviewers flagged that an inactive-completion
        // never cleared `isLoadingMore`, so pagination stayed wedged
        // after reactivation.
        let firstPage = makeAttentionFeed(
            items: [makeAttentionItem(id: "failure:1")],
            nextCursor: "cursor-1"
        )
        let appendGate = AttentionResultGate<AttentionFeed>()
        let recoveryPage = makeAttentionFeed(
            items: [makeAttentionItem(id: "failure:2")],
            nextCursor: "cursor-2"
        )
        let secondAttempt = makeAttentionFeed(
            items: [makeAttentionItem(id: "failure:3")],
            nextCursor: nil
        )
        let service = ScriptedAttentionService(steps: [
            .value(firstPage),
            .gated(appendGate),
            .value(recoveryPage),
            .value(secondAttempt),
        ])
        let signalR = MockSignalRService()
        let vm = AttentionFeedViewModel()
        vm.configure(
            attentionService: service,
            signalRService: signalR,
            attentionEnabled: true
        )

        _ = await vm.refresh()
        XCTAssertTrue(vm.canLoadMore)

        let loadMoreTask = Task { await vm.loadMore() }
        await service.waitForLoadCount(2)
        XCTAssertTrue(vm.isLoadingMore)

        // Deactivate mid-flight.
        vm.deactivate()

        // Resolve the append after deactivate. The completion must
        // release its owned `isLoadingMore` even though the view is
        // inactive; otherwise the flag would wedge pagination forever.
        await appendGate.succeed(
            makeAttentionFeed(
                items: [makeAttentionItem(id: "failure:stale")],
                nextCursor: "cursor-stale"
            )
        )
        _ = await loadMoreTask.value

        XCTAssertFalse(
            vm.isLoadingMore,
            "Inactive completion must release isLoadingMore (no wedge)"
        )

        // Reactivate + refresh to re-establish a valid cursor.
        vm.activate()
        _ = await vm.refresh()
        XCTAssertTrue(vm.canLoadMore, "canLoadMore recovers on reactivation")

        // Pagination must now proceed exactly once.
        _ = await vm.loadMore()
        let ids = vm.snapshot?.items.map(\.id) ?? []
        XCTAssertEqual(ids, ["failure:2", "failure:3"])
        XCTAssertNil(vm.snapshot?.nextCursor)
        XCTAssertFalse(vm.isLoadingMore)
    }

    // MARK: - Inactive error queue drain

    func testFailureFinishingWhileInactiveQueuesOneReloadAndDoesNotSurfaceError() async {
        let gate = AttentionResultGate<AttentionFeed>()
        let recoveryFeed = makeAttentionFeed(healthyPrinterCount: 4)
        let service = ScriptedAttentionService(steps: [
            .gated(gate),
            .value(recoveryFeed),
        ])
        let signalR = MockSignalRService()
        let callbackQueue = AttentionCallbackQueue()
        let vm = AttentionFeedViewModel(callbackEnqueuer: callbackQueue.enqueuer)
        vm.configure(
            attentionService: service,
            signalRService: signalR,
            attentionEnabled: true
        )

        let refreshTask = Task { await vm.refresh() }
        await service.waitForLoadCount(1)
        XCTAssertEqual(vm.phase, .loading)

        vm.deactivate()

        // Failure lands while inactive: outcome dropped, loading flag
        // cleared, and exactly one queued reload flagged.
        await gate.fail(.forced("inactive failure"))
        let dropped = await refreshTask.value
        XCTAssertFalse(dropped)
        XCTAssertFalse(vm.isRefreshing)
        XCTAssertEqual(vm.phase, .idle)
        XCTAssertNil(
            vm.loadFailure,
            "Inactive failure must not surface as a load failure"
        )

        vm.activate()
        await callbackQueue.waitForCount(1)
        await callbackQueue.runNext()

        let calls = await service.loadCallCount
        XCTAssertEqual(calls, 2, "Exactly one queued reload drains on re-entry")
        XCTAssertEqual(vm.phase, .loaded)
        XCTAssertEqual(vm.snapshot?.healthyPrinterCount, 4)
        XCTAssertEqual(callbackQueue.count, 0)
    }

    // MARK: - Bootstrap and disabled recovery

    func testBootstrapIssuesExactlyOneCanonicalFetchOnFreshEntry() async {
        let service = ScriptedAttentionService(steps: [
            .value(makeAttentionFeed(healthyPrinterCount: 1)),
        ])
        let signalR = MockSignalRService()
        let vm = AttentionFeedViewModel()

        _ = await vm.bootstrap(
            attentionService: service,
            signalRService: signalR,
            attentionEnabled: true
        )

        let calls = await service.loadCallCount
        XCTAssertEqual(calls, 1, "Fresh bootstrap → exactly one canonical GET")
        XCTAssertEqual(vm.phase, .loaded)
    }

    func testBootstrapCoalescesQueuedDrainIntoASingleFetch() async {
        let service = ScriptedAttentionService(steps: [
            .value(makeAttentionFeed(healthyPrinterCount: 1)),
            .value(makeAttentionFeed(healthyPrinterCount: 7)),
        ])
        let signalR = MockSignalRService()
        let callbackQueue = AttentionCallbackQueue()
        let vm = AttentionFeedViewModel(callbackEnqueuer: callbackQueue.enqueuer)

        // First bootstrap does the initial load.
        _ = await vm.bootstrap(
            attentionService: service,
            signalRService: signalR,
            attentionEnabled: true
        )
        let baseline = await service.loadCallCount
        XCTAssertEqual(baseline, 1)

        // Deactivate; simulate a signalR event while off-screen that
        // sets pendingReloadOnActivate=true.
        vm.deactivate()
        signalR.simulateAttentionChanged(
            AttentionChangedEvent(
                itemId: "failure:1",
                changeKind: .updated,
                occurredAt: Date()
            )
        )
        await callbackQueue.waitForCount(1)
        await callbackQueue.runNext()
        // The handler saw inactive and set the pending flag; no fetch
        // ran while off-screen.
        let stillBaseline = await service.loadCallCount
        XCTAssertEqual(stillBaseline, baseline)

        // Re-entering with the same authority: bootstrap must issue
        // exactly ONE canonical GET, not two (drain + refresh).
        _ = await vm.bootstrap(
            attentionService: service,
            signalRService: signalR,
            attentionEnabled: true
        )
        let after = await service.loadCallCount
        XCTAssertEqual(
            after, baseline + 1,
            "Bootstrap must coalesce a queued drain and its own refresh into ONE fetch"
        )
        XCTAssertEqual(callbackQueue.count, 0)
        XCTAssertEqual(vm.snapshot?.healthyPrinterCount, 7)
    }

    func testDisabledRecoveryRecomputesGateAndRefetches() async {
        // Server initially responds with featureDisabled, then the
        // gate is flipped back on and the recovery button on the view
        // path (VM: retryDisabledRecovery + refresh) picks up fresh
        // canonical content. Without the VM-side latch clear, the VM
        // would stay `.disabled` forever after the server response.
        let service = ScriptedAttentionService(steps: [
            .featureDisabled,
            .value(makeAttentionFeed(healthyPrinterCount: 6)),
        ])
        let signalR = MockSignalRService()
        let vm = AttentionFeedViewModel()
        vm.configure(
            attentionService: service,
            signalRService: signalR,
            attentionEnabled: true
        )

        _ = await vm.refresh()
        XCTAssertEqual(vm.phase, .disabled)

        // Without recovery: a naive re-refresh returns immediately
        // because the internal gate latched false. Prove that first.
        _ = await vm.refresh()
        let afterNaive = await service.loadCallCount
        XCTAssertEqual(
            afterNaive, 1,
            "Naive refresh after latched disabled must not hit the network"
        )
        XCTAssertEqual(vm.phase, .disabled)

        // Recovery entry: the view has re-resolved capabilities and
        // now reports enabled=true. VM clears its latch and the next
        // refresh actually fires.
        vm.retryDisabledRecovery(attentionEnabled: true)
        XCTAssertNotEqual(vm.phase, .disabled)

        _ = await vm.refresh()
        let afterRecovery = await service.loadCallCount
        XCTAssertEqual(
            afterRecovery, 2,
            "Recovery entry must permit exactly one fresh canonical GET"
        )
        XCTAssertEqual(vm.phase, .loaded)
        XCTAssertEqual(vm.snapshot?.healthyPrinterCount, 6)
    }

    // MARK: - Inactive-mid-load

    func testLoadFinishingWhileInactiveClearsLoadingAndQueuesOneReload() async {
        let gate = AttentionResultGate<AttentionFeed>()
        let deferredFeed = makeAttentionFeed(
            items: [makeAttentionItem(id: "runout:1", title: "First")]
        )
        let queuedFeed = makeAttentionFeed(
            items: [makeAttentionItem(id: "runout:2", title: "Second")]
        )
        let service = ScriptedAttentionService(
            steps: [.gated(gate), .value(queuedFeed)]
        )
        let signalR = MockSignalRService()
        let callbackQueue = AttentionCallbackQueue()
        let vm = AttentionFeedViewModel(callbackEnqueuer: callbackQueue.enqueuer)
        vm.configure(
            attentionService: service,
            signalRService: signalR,
            attentionEnabled: true
        )

        let refresh = Task { await vm.refresh() }
        await service.waitForLoadCount(1)
        XCTAssertEqual(vm.phase, .loading)

        // View deactivates mid-flight.
        vm.deactivate()

        // Load completes while inactive. The result must be dropped, the
        // loading flag cleared, and a queued reload flagged.
        await gate.succeed(deferredFeed)
        let dropped = await refresh.value
        XCTAssertFalse(dropped)
        XCTAssertEqual(vm.phase, .idle, "Loading flag must clear once inactive load finishes")
        XCTAssertFalse(vm.isRefreshing)
        XCTAssertNil(vm.snapshot, "Stale completion must not paint snapshot")

        // Re-entry drains exactly one canonical refresh through the
        // callback enqueuer; await it deterministically.
        vm.activate()
        await callbackQueue.waitForCount(1)
        await callbackQueue.runNext()

        let totalCalls = await service.loadCallCount
        XCTAssertEqual(totalCalls, 2, "Exactly one queued reload must drain on re-entry")
        XCTAssertEqual(vm.snapshot?.items.first?.title, "Second")
        XCTAssertEqual(vm.phase, .loaded)
        // No additional queued callback — the drain is exactly one.
        XCTAssertEqual(callbackQueue.count, 0)
    }

    func testInactiveThenReactivateWithoutPendingDoesNotRefetch() async {
        let service = ScriptedAttentionService(
            steps: [.value(makeAttentionFeed(healthyPrinterCount: 1))]
        )
        let signalR = MockSignalRService()
        let callbackQueue = AttentionCallbackQueue()
        let vm = AttentionFeedViewModel(callbackEnqueuer: callbackQueue.enqueuer)
        vm.configure(
            attentionService: service,
            signalRService: signalR,
            attentionEnabled: true
        )

        _ = await vm.refresh()
        XCTAssertEqual(vm.phase, .loaded)
        let baselineCalls = await service.loadCallCount

        vm.deactivate()
        vm.activate()
        XCTAssertEqual(
            callbackQueue.count, 0,
            "No pending reload → activate must not enqueue a drain"
        )

        let afterCalls = await service.loadCallCount
        XCTAssertEqual(afterCalls, baselineCalls, "No pending reload → no extra fetch")
    }

    // MARK: - Pagination

    func testLoadMoreAppendsInStableOrderAndDedupesIDs() async {
        let firstPage = makeAttentionFeed(
            items: [
                makeAttentionItem(id: "failure:1"),
                makeAttentionItem(id: "failure:2"),
            ],
            nextCursor: "cursor-1"
        )
        let secondPage = makeAttentionFeed(
            items: [
                // Duplicate id from first page — must be deduped.
                makeAttentionItem(id: "failure:2"),
                makeAttentionItem(id: "failure:3"),
                makeAttentionItem(id: "failure:4"),
            ],
            nextCursor: nil
        )
        let service = ScriptedAttentionService(steps: [
            .value(firstPage),
            .value(secondPage),
        ])
        let vm = configuredViewModel(service: service)

        _ = await vm.refresh()
        XCTAssertEqual(vm.canLoadMore, true)

        _ = await vm.loadMore()

        let ids = vm.snapshot?.items.map(\.id) ?? []
        XCTAssertEqual(ids, ["failure:1", "failure:2", "failure:3", "failure:4"])
        XCTAssertNil(vm.snapshot?.nextCursor)
        XCTAssertFalse(vm.canLoadMore)

        let calls = await service.loadCalls
        XCTAssertEqual(calls.count, 2)
        XCTAssertNil(calls[0].cursor, "First load must be cursor-less")
        XCTAssertEqual(calls[1].cursor, "cursor-1", "Second load must echo server cursor")
    }

    func testLoadMoreIsNoopWhenAlreadyInFlight() async {
        let firstPage = makeAttentionFeed(
            items: [makeAttentionItem(id: "failure:1")],
            nextCursor: "cursor-1"
        )
        let gate = AttentionResultGate<AttentionFeed>()
        let service = ScriptedAttentionService(steps: [
            .value(firstPage),
            .gated(gate),
        ])
        let vm = configuredViewModel(service: service)

        _ = await vm.refresh()
        let firstMore = Task { await vm.loadMore() }
        await service.waitForLoadCount(2)
        XCTAssertTrue(vm.isLoadingMore)

        // Second call while the first is in flight must return false
        // and not touch the service.
        let secondMore = await vm.loadMore()
        XCTAssertFalse(secondMore)

        await gate.succeed(
            makeAttentionFeed(items: [makeAttentionItem(id: "failure:2")], nextCursor: nil)
        )
        _ = await firstMore.value

        let calls = await service.loadCallCount
        XCTAssertEqual(calls, 2, "Duplicate loadMore taps must not spawn a third call")
        XCTAssertEqual(vm.snapshot?.items.count, 2)
    }

    func testRefreshResetsPaginationAtomicallyDroppingInFlightAppend() async {
        let firstPage = makeAttentionFeed(
            items: [makeAttentionItem(id: "failure:1")],
            nextCursor: "cursor-1"
        )
        let appendGate = AttentionResultGate<AttentionFeed>()
        let refreshFeed = makeAttentionFeed(
            items: [makeAttentionItem(id: "runout:1", title: "Fresh")],
            nextCursor: nil,
            healthyPrinterCount: 3
        )
        let service = ScriptedAttentionService(steps: [
            .value(firstPage),
            .gated(appendGate),
            .value(refreshFeed),
        ])
        let vm = configuredViewModel(service: service)

        _ = await vm.refresh()
        let loadMoreTask = Task { await vm.loadMore() }
        await service.waitForLoadCount(2)

        _ = await vm.refresh()

        // Late append tries to arrive after refresh applied. Must drop.
        await appendGate.succeed(
            makeAttentionFeed(
                items: [makeAttentionItem(id: "failure:stale", title: "Stale append")],
                nextCursor: "cursor-late"
            )
        )
        _ = await loadMoreTask.value

        XCTAssertEqual(vm.snapshot?.items.map(\.id), ["runout:1"])
        XCTAssertNil(vm.snapshot?.nextCursor, "Refresh must have replaced cursor atomically")
        XCTAssertEqual(vm.snapshot?.healthyPrinterCount, 3)
    }

    // MARK: - SignalR wiring

    func testAttentionChangedInvocationTriggersExactlyOneCanonicalRefetch() async {
        let callbackQueue = AttentionCallbackQueue()
        let service = ScriptedAttentionService(
            steps: [
                .value(makeAttentionFeed(healthyPrinterCount: 1)),
                .value(makeAttentionFeed(healthyPrinterCount: 9)),
            ]
        )
        let signalR = MockSignalRService()
        let vm = AttentionFeedViewModel(callbackEnqueuer: callbackQueue.enqueuer)
        vm.configure(
            attentionService: service,
            signalRService: signalR,
            attentionEnabled: true
        )
        _ = await vm.refresh()
        let baselineCalls = await service.loadCallCount
        XCTAssertEqual(baselineCalls, 1)

        signalR.simulateAttentionChanged(
            AttentionChangedEvent(
                itemId: "failure:1",
                changeKind: .updated,
                occurredAt: Date(timeIntervalSince1970: 1_700_000_100)
            )
        )
        await callbackQueue.waitForCount(1)
        await callbackQueue.runNext()

        let afterCalls = await service.loadCallCount
        XCTAssertEqual(afterCalls, 2, "Exactly one canonical refetch per event")
        XCTAssertEqual(vm.snapshot?.healthyPrinterCount, 9)
    }

    func testRepeatedSameInstanceConfigureDoesNotStackHandlers() async {
        let signalR = MockSignalRService()
        let service = ScriptedAttentionService(
            steps: [
                .value(makeAttentionFeed(healthyPrinterCount: 1)),
                .value(makeAttentionFeed(healthyPrinterCount: 2)),
            ]
        )
        let callbackQueue = AttentionCallbackQueue()
        let vm = AttentionFeedViewModel(callbackEnqueuer: callbackQueue.enqueuer)

        vm.configure(
            attentionService: service,
            signalRService: signalR,
            attentionEnabled: true
        )
        vm.configure(
            attentionService: service,
            signalRService: signalR,
            attentionEnabled: true
        )
        vm.configure(
            attentionService: service,
            signalRService: signalR,
            attentionEnabled: true
        )
        XCTAssertEqual(
            signalR.attentionSubscriberCount, 1,
            "Repeated configure with same identities must not stack handlers"
        )

        _ = await vm.refresh()
        signalR.simulateAttentionChanged(
            AttentionChangedEvent(
                itemId: "failure:1",
                changeKind: .updated,
                occurredAt: Date()
            )
        )
        await callbackQueue.waitForCount(1)
        await callbackQueue.runNext()

        let calls = await service.loadCallCount
        XCTAssertEqual(calls, 2, "Exactly one refetch, not three")
    }

    func testServiceReplacementRegistersOneNewHandlerAndOldDeliveryIsDropped() async {
        let oldSignalR = MockSignalRService()
        let newSignalR = MockSignalRService()
        let oldService = ScriptedAttentionService(
            steps: [.value(makeAttentionFeed(healthyPrinterCount: 1))]
        )
        let newService = ScriptedAttentionService(
            steps: [.value(makeAttentionFeed(healthyPrinterCount: 5))]
        )
        let callbackQueue = AttentionCallbackQueue()
        let vm = AttentionFeedViewModel(callbackEnqueuer: callbackQueue.enqueuer)

        vm.configure(
            attentionService: oldService,
            signalRService: oldSignalR,
            attentionEnabled: true
        )
        XCTAssertEqual(oldSignalR.attentionSubscriberCount, 1)

        vm.configure(
            attentionService: newService,
            signalRService: newSignalR,
            attentionEnabled: true
        )
        XCTAssertEqual(
            oldSignalR.attentionSubscriberCount, 0,
            "Old subscription must be cancelled on service replacement"
        )
        XCTAssertEqual(
            newSignalR.attentionSubscriberCount, 1,
            "Exactly one handler on the new signalR instance"
        )

        // Deliver on the OLD hub — must not trigger any fetch.
        oldSignalR.simulateAttentionChanged(
            AttentionChangedEvent(
                itemId: "failure:stale",
                changeKind: .updated,
                occurredAt: Date()
            )
        )
        // Give an opportunity for a mistaken callback to enqueue.
        XCTAssertEqual(callbackQueue.count, 0, "Stale hub must not enqueue callbacks")

        // Deliver on the NEW hub — triggers exactly one fetch.
        _ = await vm.refresh()
        let baseline = await newService.loadCallCount
        newSignalR.simulateAttentionChanged(
            AttentionChangedEvent(
                itemId: "failure:new",
                changeKind: .updated,
                occurredAt: Date()
            )
        )
        await callbackQueue.waitForCount(1)
        await callbackQueue.runNext()

        let after = await newService.loadCallCount
        XCTAssertEqual(after, baseline + 1)
        let oldServiceCalls = await oldService.loadCallCount
        XCTAssertEqual(oldServiceCalls, 0, "Old service must not receive fetches after replacement")
    }

    // MARK: - Severity ordering within a group

    func testItemsWithinSeverityGroupPreserveServerOrder() async {
        let critical = [
            makeAttentionItem(id: "failure:c1", severity: .critical, title: "C1"),
            makeAttentionItem(id: "runout:c2", kind: .runout, severity: .critical, title: "C2"),
            makeAttentionItem(id: "harvest:c3", kind: .harvest, severity: .critical, title: "C3"),
        ]
        let warning = [
            makeAttentionItem(id: "failure:w1", severity: .warning, title: "W1"),
            makeAttentionItem(id: "runout:w2", kind: .runout, severity: .warning, title: "W2"),
        ]
        // Interleave severities on the wire; grouping must not re-order
        // items within a bucket.
        let payload: [AttentionItem] = [
            critical[0], warning[0], critical[1], warning[1], critical[2],
        ]
        let service = ScriptedAttentionService(
            steps: [.value(makeAttentionFeed(items: payload))]
        )
        let vm = configuredViewModel(service: service)

        _ = await vm.refresh()

        XCTAssertEqual(vm.groups.map(\.severity), [.critical, .warning])
        XCTAssertEqual(
            vm.groups[0].items.map(\.title),
            ["C1", "C2", "C3"],
            "Server order must be preserved within a severity group"
        )
        XCTAssertEqual(vm.groups[1].items.map(\.title), ["W1", "W2"])
    }

    func testHealthySummaryExpansionStatePersistsAcrossRefresh() async {
        let service = ScriptedAttentionService(
            steps: [
                .value(makeAttentionFeed(healthyPrinterCount: 3)),
                .value(makeAttentionFeed(healthyPrinterCount: 4)),
            ]
        )
        let vm = configuredViewModel(service: service)

        _ = await vm.refresh()
        XCTAssertFalse(vm.isHealthySummaryExpanded)
        vm.isHealthySummaryExpanded = true

        _ = await vm.refresh()
        XCTAssertTrue(
            vm.isHealthySummaryExpanded,
            "Local expansion state must survive canonical refresh"
        )
        XCTAssertEqual(vm.snapshot?.healthyPrinterCount, 4)
    }

    // MARK: - Cycle 3: lifecycle-token fence

    func testBootstrapWithStaleLifecycleTokenIsANoOp() async {
        // Reviewer's blocker 1: if the view captures the lifecycle
        // token BEFORE a pre-bootstrap `await` (capability refresh)
        // and the view deactivates during the await, bootstrap must
        // not reactivate the VM, subscribe, or fetch.
        let service = ScriptedAttentionService(steps: [
            .value(makeAttentionFeed(healthyPrinterCount: 1)),
        ])
        let signalR = MockSignalRService()
        let vm = AttentionFeedViewModel()

        // Fresh VM: capture the initial lifecycle token before ever
        // reaching bootstrap. This mirrors the view's pattern of
        // capturing the token before `await capabilities.refresh()`.
        let stale = vm.currentLifecycleToken()

        // Simulate the view disappearing during the await (or the VM
        // otherwise being deactivated). Deactivate bumps the token.
        vm.deactivate()

        let applied = await vm.bootstrap(
            attentionService: service,
            signalRService: signalR,
            attentionEnabled: true,
            lifecycleToken: stale
        )
        XCTAssertFalse(applied, "Stale token must cause bootstrap to abort")

        // Full observable proof: nothing mutated.
        let calls = await service.loadCallCount
        XCTAssertEqual(calls, 0, "Stale bootstrap must not fetch")
        XCTAssertEqual(vm.phase, .idle, "Stale bootstrap must not change phase")
        XCTAssertEqual(
            signalR.attentionSubscriberCount, 0,
            "Stale bootstrap must not subscribe"
        )
    }

    func testBootstrapWithFreshTokenAfterDeactivateWorks() async {
        // Compare-and-contrast for the previous test: after a
        // deactivate, capturing the token AFRESH and re-bootstrapping
        // must succeed. Prevents the fence from being a permanent
        // wedge.
        let service = ScriptedAttentionService(steps: [
            .value(makeAttentionFeed(healthyPrinterCount: 2)),
        ])
        let signalR = MockSignalRService()
        let vm = AttentionFeedViewModel()

        vm.deactivate() // bump token
        let fresh = vm.currentLifecycleToken()

        let applied = await vm.bootstrap(
            attentionService: service,
            signalRService: signalR,
            attentionEnabled: true,
            lifecycleToken: fresh
        )
        XCTAssertTrue(applied)

        let calls = await service.loadCallCount
        XCTAssertEqual(calls, 1)
        XCTAssertEqual(vm.phase, .loaded)
    }

    // MARK: - Cycle 3: queued-invalidation coalescing

    func testInvalidationQueuedBeforeBootstrapAndDrainingAfterIsSkipped() async {
        // Reviewer's blocker 2: the SignalR handler enqueues a
        // callback. Bootstrap starts and runs a canonical refresh to
        // completion. The queued callback then drains. Without the
        // refresh-completion sequence fence, the drained callback
        // would fire a duplicate refresh — this test proves the fence
        // catches it.
        let service = ScriptedAttentionService(steps: [
            .value(makeAttentionFeed(healthyPrinterCount: 1)),
            .value(makeAttentionFeed(healthyPrinterCount: 9)),
        ])
        let signalR = MockSignalRService()
        let callbackQueue = AttentionCallbackQueue()
        let vm = AttentionFeedViewModel(callbackEnqueuer: callbackQueue.enqueuer)

        // Prime the VM with a first successful bootstrap so a
        // subscription is registered.
        _ = await vm.bootstrap(
            attentionService: service,
            signalRService: signalR,
            attentionEnabled: true
        )
        let baseline = await service.loadCallCount
        XCTAssertEqual(baseline, 1)

        // Enqueue an invalidation. This snapshots the current
        // refresh-completion sequence at enqueue time.
        signalR.simulateAttentionChanged(
            AttentionChangedEvent(
                itemId: "failure:1",
                changeKind: .updated,
                occurredAt: Date()
            )
        )
        await callbackQueue.waitForCount(1)

        // Bootstrap runs before the queued callback drains — its
        // refresh advances the refresh-completion sequence.
        _ = await vm.bootstrap(
            attentionService: service,
            signalRService: signalR,
            attentionEnabled: true
        )
        let afterBootstrap = await service.loadCallCount
        XCTAssertEqual(
            afterBootstrap, baseline + 1,
            "Bootstrap issues exactly one canonical GET"
        )

        // Drain the queued callback. Its captured sequence is stale;
        // the fence must cause it to no-op instead of dispatching a
        // duplicate refresh.
        await callbackQueue.runNext()
        let afterDrain = await service.loadCallCount
        XCTAssertEqual(
            afterDrain, baseline + 1,
            "Queued invalidation whose refresh completed since enqueue must be a no-op"
        )
        XCTAssertEqual(vm.snapshot?.healthyPrinterCount, 9)
    }

    func testGenuinelyLaterInvalidationStillFires() async {
        // Complements the previous test: an invalidation that arrives
        // AFTER the last refresh completed must still fire a fresh
        // refresh. The fence must not swallow legitimate events.
        let service = ScriptedAttentionService(steps: [
            .value(makeAttentionFeed(healthyPrinterCount: 1)),
            .value(makeAttentionFeed(healthyPrinterCount: 2)),
        ])
        let signalR = MockSignalRService()
        let callbackQueue = AttentionCallbackQueue()
        let vm = AttentionFeedViewModel(callbackEnqueuer: callbackQueue.enqueuer)

        _ = await vm.bootstrap(
            attentionService: service,
            signalRService: signalR,
            attentionEnabled: true
        )
        let baseline = await service.loadCallCount
        XCTAssertEqual(baseline, 1)

        // Bootstrap has already completed. NOW an invalidation
        // arrives — captured sequence == current sequence, no skip.
        signalR.simulateAttentionChanged(
            AttentionChangedEvent(
                itemId: "failure:new",
                changeKind: .updated,
                occurredAt: Date()
            )
        )
        await callbackQueue.waitForCount(1)
        await callbackQueue.runNext()

        let afterEvent = await service.loadCallCount
        XCTAssertEqual(
            afterEvent, baseline + 1,
            "A genuinely later invalidation must still refetch"
        )
        XCTAssertEqual(vm.snapshot?.healthyPrinterCount, 2)
    }

    // MARK: - Cycle 3: pagination-failure retry ownership

    func testFailedPaginationDoesNotAutoRetryOnRepeatSentinelTrigger() async {
        // Reviewer's blocker 3: after loadMore fails, the sentinel
        // must NOT auto-retry — `.onAppear` may re-fire on every list
        // rebuild. Repeated calls to `loadMore()` are the harness's
        // model of that behaviour.
        let firstPage = makeAttentionFeed(
            items: [makeAttentionItem(id: "failure:1")],
            nextCursor: "cursor-1"
        )
        let service = ScriptedAttentionService(steps: [
            .value(firstPage),
            .failure(.forced("network flake")),
        ])
        let signalR = MockSignalRService()
        let vm = AttentionFeedViewModel()
        vm.configure(
            attentionService: service,
            signalRService: signalR,
            attentionEnabled: true
        )

        _ = await vm.refresh()
        XCTAssertTrue(vm.canLoadMore)

        // First attempt: fails.
        _ = await vm.loadMore()
        XCTAssertNotNil(vm.paginationFailure)
        XCTAssertEqual(vm.paginationFailure?.cursor, "cursor-1")
        XCTAssertEqual(vm.paginationFailure?.message, "network flake")
        XCTAssertFalse(
            vm.canLoadMore,
            "canLoadMore must be false while the current cursor is latched failed"
        )

        // Simulate many sentinel-onAppear-driven retries. None must
        // hit the network — the latch keeps the request quiescent.
        for _ in 0..<10 {
            _ = await vm.loadMore()
        }
        let calls = await service.loadCallCount
        XCTAssertEqual(
            calls, 2,
            "Auto-retry storm must be suppressed: expected 2 calls (refresh + one failed loadMore)"
        )
    }

    func testExplicitRetryLoadMoreClearsLatchAndRetriesExactlyOnce() async throws {
        // After the auto-retry latch has locked the sentinel, an
        // explicit `retryLoadMore` must clear the latch and re-run
        // loadMore exactly once. A subsequent implicit `loadMore` on
        // the newly-loaded cursor must succeed normally.
        let firstPage = makeAttentionFeed(
            items: [makeAttentionItem(id: "failure:1")],
            nextCursor: "cursor-1"
        )
        let recoveredPage = makeAttentionFeed(
            items: [makeAttentionItem(id: "failure:2")],
            nextCursor: nil
        )
        let service = ScriptedAttentionService(steps: [
            .value(firstPage),
            .failure(.forced("network flake")),
            .value(recoveredPage),
        ])
        let signalR = MockSignalRService()
        let vm = AttentionFeedViewModel()
        vm.configure(
            attentionService: service,
            signalRService: signalR,
            attentionEnabled: true
        )

        _ = await vm.refresh()
        _ = await vm.loadMore()
        let failureID = try XCTUnwrap(vm.paginationFailure?.id)

        // Stale retry with a different failure ID must not fire.
        let staleID = UUID()
        let staleResult = await vm.retryLoadMore(failureID: staleID)
        XCTAssertFalse(staleResult, "Stale retry ID must be rejected")

        // Real retry: clears the latch, fires exactly one loadMore
        // that succeeds.
        let ok = await vm.retryLoadMore(failureID: failureID)
        XCTAssertTrue(ok)
        XCTAssertNil(vm.paginationFailure)
        XCTAssertEqual(vm.snapshot?.items.map(\.id), ["failure:1", "failure:2"])
        XCTAssertNil(vm.snapshot?.nextCursor)
        XCTAssertFalse(vm.canLoadMore)

        let calls = await service.loadCallCount
        XCTAssertEqual(
            calls, 3,
            "Total calls: initial refresh + failed loadMore + successful retry = 3"
        )
    }

    func testCanonicalRefreshClearsLatchedPaginationFailure() async {
        // A subsequent canonical refresh (pull-to-refresh, signalR
        // invalidation) must clear a latched pagination failure so
        // pagination resumes automatically on the fresh cursor. If it
        // did not, the operator would need to explicitly retry-loadMore
        // even after a full refresh, which is unnecessary friction.
        let firstPage = makeAttentionFeed(
            items: [makeAttentionItem(id: "failure:1")],
            nextCursor: "cursor-1"
        )
        let refreshedPage = makeAttentionFeed(
            items: [makeAttentionItem(id: "runout:fresh")],
            nextCursor: "cursor-fresh"
        )
        let service = ScriptedAttentionService(steps: [
            .value(firstPage),
            .failure(.forced("network flake")),
            .value(refreshedPage),
        ])
        let signalR = MockSignalRService()
        let vm = AttentionFeedViewModel()
        vm.configure(
            attentionService: service,
            signalRService: signalR,
            attentionEnabled: true
        )

        _ = await vm.refresh()
        _ = await vm.loadMore()
        XCTAssertNotNil(vm.paginationFailure)

        _ = await vm.refresh()
        XCTAssertNil(
            vm.paginationFailure,
            "Canonical refresh must clear the latched pagination failure"
        )
        XCTAssertTrue(vm.canLoadMore, "Fresh cursor is not latched-failed")
        XCTAssertEqual(vm.snapshot?.items.first?.id, "runout:fresh")
    }

    // MARK: - Helpers

    private func configuredViewModel(
        service: ScriptedAttentionService,
        signalR: MockSignalRService = MockSignalRService(),
        attentionEnabled: Bool = true
    ) -> AttentionFeedViewModel {
        let vm = AttentionFeedViewModel()
        vm.configure(
            attentionService: service,
            signalRService: signalR,
            attentionEnabled: attentionEnabled
        )
        return vm
    }
}
