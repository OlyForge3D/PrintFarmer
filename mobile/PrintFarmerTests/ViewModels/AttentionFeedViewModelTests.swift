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

    // MARK: - Cycle 4: strict request/event ordering

    func testEventQueuedBeforeRefreshDrainingDuringSuspensionYieldsExactlyOneGET() async {
        // Disputed timeline (Hicks blocker 1): an invalidation is
        // enqueued BEFORE a canonical refresh starts, but its
        // callback drains WHILE the refresh's fetch is suspended.
        // The refresh must cover it — total canonical GET count = 1.
        let feed = makeAttentionFeed(
            items: [makeAttentionItem(id: "failure:1")],
            healthyPrinterCount: 2
        )
        let gate = AttentionResultGate<AttentionFeed>()
        let service = ScriptedAttentionService(steps: [.gated(gate)])
        let signalR = MockSignalRService()
        let callbackQueue = AttentionCallbackQueue()
        let vm = AttentionFeedViewModel(callbackEnqueuer: callbackQueue.enqueuer)
        vm.configure(
            attentionService: service,
            signalRService: signalR,
            attentionEnabled: true
        )

        // Event enqueues BEFORE any refresh. eventSeq=1.
        signalR.simulateAttentionChanged(
            AttentionChangedEvent(
                itemId: "failure:1",
                changeKind: .updated,
                occurredAt: Date()
            )
        )
        await callbackQueue.waitForCount(1)

        // Refresh starts. Its start-cover watermark = 1 because the
        // event's sequence was already issued.
        let refreshTask = Task { await vm.refresh() }
        await service.waitForLoadCount(1)

        // Drain the queued callback while the refresh is suspended.
        // eventSeq(1) > lastCovered(0) so it latches pending, but
        // activeRefreshCount>0 so it MUST NOT dispatch.
        await callbackQueue.runNext()
        let midCount = await service.loadCallCount
        XCTAssertEqual(
            midCount, 1,
            "In-flight drain must not launch a concurrent duplicate GET"
        )

        // Resolve the refresh. cover(1) → lastCovered=1. pending(1)
        // is covered → clear. No follow-up.
        await gate.succeed(feed)
        _ = await refreshTask.value

        let finalCount = await service.loadCallCount
        XCTAssertEqual(
            finalCount, 1,
            "Event that preceded the refresh start is covered by it — total 1 GET"
        )
        XCTAssertEqual(vm.snapshot?.healthyPrinterCount, 2)
        XCTAssertEqual(
            callbackQueue.count, 0,
            "No follow-up should be scheduled when pending is covered"
        )
    }

    func testEventArrivingAfterRefreshStartDrainingDuringYieldsFollowupGET() async {
        // Disputed timeline (Hicks blocker 2): an invalidation arrives
        // AFTER a refresh has started, and its callback drains BEFORE
        // the refresh completes. Because the refresh's cover watermark
        // was captured before the event, the event is NOT covered by
        // that refresh — completion must launch exactly one follow-up.
        let firstFeed = makeAttentionFeed(
            items: [makeAttentionItem(id: "failure:1")],
            healthyPrinterCount: 1
        )
        let followUpFeed = makeAttentionFeed(
            items: [makeAttentionItem(id: "runout:2")],
            healthyPrinterCount: 5
        )
        let gate = AttentionResultGate<AttentionFeed>()
        let service = ScriptedAttentionService(steps: [
            .gated(gate),
            .value(followUpFeed),
        ])
        let signalR = MockSignalRService()
        let callbackQueue = AttentionCallbackQueue()
        let vm = AttentionFeedViewModel(callbackEnqueuer: callbackQueue.enqueuer)
        vm.configure(
            attentionService: service,
            signalRService: signalR,
            attentionEnabled: true
        )

        // Refresh starts FIRST — cover watermark = 0.
        let refreshTask = Task { await vm.refresh() }
        await service.waitForLoadCount(1)

        // Event arrives DURING the in-flight refresh. eventSeq=1.
        signalR.simulateAttentionChanged(
            AttentionChangedEvent(
                itemId: "failure:x",
                changeKind: .updated,
                occurredAt: Date()
            )
        )
        await callbackQueue.waitForCount(1)

        // Drain before refresh completes. Latches pending=1.
        // activeRefreshCount>0 → no dispatch.
        await callbackQueue.runNext()
        let midCount = await service.loadCallCount
        XCTAssertEqual(
            midCount, 1,
            "In-flight drain must not launch concurrent GET"
        )

        // Complete refresh. cover(0) → lastCovered=0. pending(1) > 0
        // → launch exactly one follow-up through the enqueuer.
        await gate.succeed(firstFeed)
        _ = await refreshTask.value

        await callbackQueue.waitForCount(1)
        await callbackQueue.runNext()

        let finalCount = await service.loadCallCount
        XCTAssertEqual(
            finalCount, 2,
            "Event uncovered by first refresh must yield exactly one follow-up GET"
        )
        XCTAssertEqual(
            vm.snapshot?.healthyPrinterCount, 5,
            "Follow-up must apply the fresh payload"
        )
        XCTAssertEqual(callbackQueue.count, 0)
    }

    func testEventArrivingAfterRefreshStartDrainingAfterCompletionYieldsSecondGET() async {
        // Complementary ordering to the previous test: event arrives
        // during the in-flight refresh but its callback drains AFTER
        // the refresh has completed. The completed refresh didn't
        // cover it, so the drain must dispatch a fresh GET.
        let firstFeed = makeAttentionFeed(healthyPrinterCount: 1)
        let secondFeed = makeAttentionFeed(healthyPrinterCount: 5)
        let gate = AttentionResultGate<AttentionFeed>()
        let service = ScriptedAttentionService(steps: [
            .gated(gate),
            .value(secondFeed),
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

        // Event during flight, callback NOT drained yet.
        signalR.simulateAttentionChanged(
            AttentionChangedEvent(
                itemId: "failure:x",
                changeKind: .updated,
                occurredAt: Date()
            )
        )
        await callbackQueue.waitForCount(1)

        // Complete refresh FIRST. cover(0), no pending yet → no
        // follow-up launched from completion.
        await gate.succeed(firstFeed)
        _ = await refreshTask.value
        let midCount = await service.loadCallCount
        XCTAssertEqual(midCount, 1)

        // Drain callback AFTER completion. eventSeq(1) > lastCovered(0)
        // → uncovered. Active, activeCount=0 → dispatch refresh
        // directly (not queued as a follow-up).
        await callbackQueue.runNext()

        let finalCount = await service.loadCallCount
        XCTAssertEqual(
            finalCount, 2,
            "Late-drained event uncovered by prior refresh must fire a fresh GET"
        )
        XCTAssertEqual(vm.snapshot?.healthyPrinterCount, 5)
    }

    func testMultipleEventsDuringInFlightRefreshCoalesceIntoOneFollowupGET() async {
        // Blocker requirement: "Multiple invalidations arriving during
        // one in-flight refresh coalesce into exactly one follow-up
        // GET, not N concurrent/sequential GETs."
        let firstFeed = makeAttentionFeed(healthyPrinterCount: 1)
        let followUpFeed = makeAttentionFeed(healthyPrinterCount: 7)
        let gate = AttentionResultGate<AttentionFeed>()
        let service = ScriptedAttentionService(steps: [
            .gated(gate),
            .value(followUpFeed),
        ])
        let signalR = MockSignalRService()
        let callbackQueue = AttentionCallbackQueue()
        let vm = AttentionFeedViewModel(callbackEnqueuer: callbackQueue.enqueuer)
        vm.configure(
            attentionService: service,
            signalRService: signalR,
            attentionEnabled: true
        )

        // Refresh starts (cover=0).
        let refreshTask = Task { await vm.refresh() }
        await service.waitForLoadCount(1)

        // THREE events during flight. Sequences 1, 2, 3.
        for _ in 0..<3 {
            signalR.simulateAttentionChanged(
                AttentionChangedEvent(
                    itemId: "failure:x",
                    changeKind: .updated,
                    occurredAt: Date()
                )
            )
        }
        await callbackQueue.waitForCount(3)

        // Drain all three. Each latches pending (max wins → 3). None
        // dispatch (activeCount>0).
        await callbackQueue.runNext()
        await callbackQueue.runNext()
        await callbackQueue.runNext()
        let midCount = await service.loadCallCount
        XCTAssertEqual(
            midCount, 1,
            "Three events during flight must not dispatch three GETs"
        )

        // Complete refresh. cover(0), pending(3) > 0 → launch EXACTLY
        // ONE follow-up (not three).
        await gate.succeed(firstFeed)
        _ = await refreshTask.value

        XCTAssertEqual(
            callbackQueue.count, 1,
            "Coalescing must produce exactly one follow-up, not one per event"
        )
        await callbackQueue.runNext()

        let finalCount = await service.loadCallCount
        XCTAssertEqual(
            finalCount, 2,
            "Total: initial GET + one coalesced follow-up = 2"
        )
        XCTAssertEqual(vm.snapshot?.healthyPrinterCount, 7)
        XCTAssertEqual(callbackQueue.count, 0)
    }

    func testFailedRefreshWithUncoveredEventDoesNotAutoLoop() async {
        // Blocker requirement: "A failed follow-up must not loop
        // without a newer event or explicit operator refresh." Applies
        // to failed refreshes generally, not just follow-ups.
        let gate = AttentionResultGate<AttentionFeed>()
        let service = ScriptedAttentionService(steps: [.gated(gate)])
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

        // Event during flight → latches pending, no dispatch.
        signalR.simulateAttentionChanged(
            AttentionChangedEvent(
                itemId: "failure:x",
                changeKind: .updated,
                occurredAt: Date()
            )
        )
        await callbackQueue.waitForCount(1)
        await callbackQueue.runNext()

        // Refresh FAILS. Must not launch a follow-up despite the
        // pending uncovered event. (User pull-to-refresh is the
        // recovery path.)
        await gate.fail(.forced("boom"))
        _ = await refreshTask.value

        XCTAssertEqual(
            callbackQueue.count, 0,
            "Failed refresh must NOT schedule a follow-up — that would create a retry loop"
        )
        let calls = await service.loadCallCount
        XCTAssertEqual(calls, 1, "Only the initial GET; no retry")
        XCTAssertEqual(vm.phase, .error)
        XCTAssertEqual(vm.loadFailure?.message, "boom")
    }

    func testUserRefreshAfterFailedRefreshCoversStrandedEvent() async {
        // Recovery half of the previous test: pending coverage from a
        // failed refresh remains latched, and the operator's next
        // canonical refresh (pull-to-refresh) covers it via its own
        // start-cover watermark.
        let recoveryFeed = makeAttentionFeed(healthyPrinterCount: 6)
        let gate = AttentionResultGate<AttentionFeed>()
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

        signalR.simulateAttentionChanged(
            AttentionChangedEvent(
                itemId: "failure:x",
                changeKind: .updated,
                occurredAt: Date()
            )
        )
        await callbackQueue.waitForCount(1)
        await callbackQueue.runNext()

        await gate.fail(.forced("boom"))
        _ = await refreshTask.value
        XCTAssertEqual(callbackQueue.count, 0, "No auto-follow-up after failure")

        // User pull-to-refresh: cover watermark = eventSequenceBox
        // current = 1. Success covers the stranded event.
        let userRefreshOK = await vm.refresh()
        XCTAssertTrue(userRefreshOK)
        XCTAssertEqual(vm.snapshot?.healthyPrinterCount, 6)
        XCTAssertEqual(
            callbackQueue.count, 0,
            "User refresh success covers pending — no further follow-up"
        )
        let calls = await service.loadCallCount
        XCTAssertEqual(calls, 2)
    }

    // MARK: - Cycle 5: reachable-stranding fix (last-completion drain)

    func testStaleCompletionAsLastRefreshTriggersFollowUpForPendingEvent() async {
        // Reachable stranding scenario (Hicks blocker on cycle 4):
        // Two concurrent refreshes R1 and R2 start. Event E arrives
        // after both start-cover watermarks. R2 (newer loadStamp)
        // succeeds first — activeCount 2→1 — cannot schedule
        // follow-up because another refresh is still active. R1
        // completes last, is stale by loadStamp (R2 advanced it),
        // decrements to 0.
        //
        // Pre-fix: R1's stale return path skipped the follow-up gate
        // entirely. Pending event E was stranded indefinitely.
        // Post-fix: every terminal completion (stale or applied) runs
        // through tryScheduleFollowupIfPending, which schedules
        // exactly one follow-up when the last active refresh
        // releases under a still-valid authority.
        let r1Gate = AttentionResultGate<AttentionFeed>()
        let r2Gate = AttentionResultGate<AttentionFeed>()
        let r2Feed = makeAttentionFeed(healthyPrinterCount: 3)
        let r3Feed = makeAttentionFeed(healthyPrinterCount: 9)
        let service = ScriptedAttentionService(steps: [
            .gated(r1Gate),
            .gated(r2Gate),
            .value(r3Feed),
        ])
        let signalR = MockSignalRService()
        let callbackQueue = AttentionCallbackQueue()
        let vm = AttentionFeedViewModel(callbackEnqueuer: callbackQueue.enqueuer)
        vm.configure(
            attentionService: service,
            signalRService: signalR,
            attentionEnabled: true
        )

        let r1 = Task { await vm.refresh() }
        await service.waitForLoadCount(1)
        let r2 = Task { await vm.refresh() }
        await service.waitForLoadCount(2)

        // Event E arrives AFTER both refresh start-cover watermarks.
        signalR.simulateAttentionChanged(
            AttentionChangedEvent(
                itemId: "failure:x",
                changeKind: .updated,
                occurredAt: Date()
            )
        )
        await callbackQueue.waitForCount(1)
        await callbackQueue.runNext()  // latch pending
        let midCount = await service.loadCallCount
        XCTAssertEqual(
            midCount, 2,
            "Callback drain during two in-flight refreshes must not launch a third concurrent GET"
        )

        // R2 succeeds first (its loadStamp is the newest). Applies.
        // activeCount 2→1 — cannot schedule follow-up.
        await r2Gate.succeed(r2Feed)
        _ = await r2.value
        XCTAssertEqual(vm.snapshot?.healthyPrinterCount, 3, "R2 applied")
        XCTAssertEqual(
            callbackQueue.count, 0,
            "No follow-up yet — R1 still in flight"
        )

        // R1 completes last with a "poison" payload — stale by
        // loadStamp. Must not overwrite R2. Cycle-5 fix: this stale
        // completion IS the last release, so it must schedule the
        // follow-up.
        await r1Gate.succeed(makeAttentionFeed(healthyPrinterCount: 999))
        _ = await r1.value
        XCTAssertEqual(
            vm.snapshot?.healthyPrinterCount, 3,
            "R1's stale payload must not overwrite R2"
        )
        XCTAssertEqual(
            callbackQueue.count, 1,
            "Stale-last completion under valid authority must schedule exactly one follow-up when pending uncovered"
        )

        await callbackQueue.runNext()  // R3 = follow-up
        let finalCount = await service.loadCallCount
        XCTAssertEqual(
            finalCount, 3,
            "Total: R1 + R2 + R3 (follow-up) = 3"
        )
        XCTAssertEqual(
            vm.snapshot?.healthyPrinterCount, 9,
            "R3 covers the stranded event"
        )
    }

    func testStaleCompletionFirstThenValidCompletionYieldsSingleFollowUp() async {
        // Reverse completion order: R1 (older, will be stale) completes
        // FIRST; R2 (newer, valid) completes LAST. Symmetric proof —
        // exactly one follow-up regardless of who completes last.
        let r1Gate = AttentionResultGate<AttentionFeed>()
        let r2Gate = AttentionResultGate<AttentionFeed>()
        let r2Feed = makeAttentionFeed(healthyPrinterCount: 3)
        let r3Feed = makeAttentionFeed(healthyPrinterCount: 9)
        let service = ScriptedAttentionService(steps: [
            .gated(r1Gate),
            .gated(r2Gate),
            .value(r3Feed),
        ])
        let signalR = MockSignalRService()
        let callbackQueue = AttentionCallbackQueue()
        let vm = AttentionFeedViewModel(callbackEnqueuer: callbackQueue.enqueuer)
        vm.configure(
            attentionService: service,
            signalRService: signalR,
            attentionEnabled: true
        )

        let r1 = Task { await vm.refresh() }
        await service.waitForLoadCount(1)
        let r2 = Task { await vm.refresh() }
        await service.waitForLoadCount(2)

        signalR.simulateAttentionChanged(
            AttentionChangedEvent(
                itemId: "failure:x",
                changeKind: .updated,
                occurredAt: Date()
            )
        )
        await callbackQueue.waitForCount(1)
        await callbackQueue.runNext()

        // R1 (stale by loadStamp) completes first. activeCount 2→1 —
        // does NOT schedule (not the last release).
        await r1Gate.succeed(makeAttentionFeed(healthyPrinterCount: 999))
        _ = await r1.value
        XCTAssertEqual(
            callbackQueue.count, 0,
            "Stale completion with another refresh still in flight must not schedule follow-up prematurely"
        )

        // R2 (valid) completes last. activeCount 1→0 — schedules
        // exactly one follow-up via the success path.
        await r2Gate.succeed(r2Feed)
        _ = await r2.value
        XCTAssertEqual(vm.snapshot?.healthyPrinterCount, 3)
        XCTAssertEqual(
            callbackQueue.count, 1,
            "Exactly one follow-up scheduled — regardless of stale-vs-valid completion order"
        )

        await callbackQueue.runNext()
        let finalCount = await service.loadCallCount
        XCTAssertEqual(finalCount, 3)
        XCTAssertEqual(vm.snapshot?.healthyPrinterCount, 9)
    }

    func testStaleCompletionAfterDeactivateDoesNotScheduleOldOwnerFollowup() async {
        // Authority-invalidation guard: R1 in flight, event E drained
        // (pending latched), then deactivate. R1's completion is
        // BOTH loadStamp-stale AND activation-stale. Its captured
        // activation != current activation → the follow-up scheduler
        // MUST NOT run (it would be old-owner work reactivating
        // under an off-screen authority).
        let r1Gate = AttentionResultGate<AttentionFeed>()
        let service = ScriptedAttentionService(steps: [.gated(r1Gate)])
        let signalR = MockSignalRService()
        let callbackQueue = AttentionCallbackQueue()
        let vm = AttentionFeedViewModel(callbackEnqueuer: callbackQueue.enqueuer)
        vm.configure(
            attentionService: service,
            signalRService: signalR,
            attentionEnabled: true
        )

        let r1 = Task { await vm.refresh() }
        await service.waitForLoadCount(1)

        signalR.simulateAttentionChanged(
            AttentionChangedEvent(
                itemId: "failure:x",
                changeKind: .updated,
                occurredAt: Date()
            )
        )
        await callbackQueue.waitForCount(1)
        await callbackQueue.runNext()  // latch pending; inactive-check
                                       // won't happen here (still active)

        // Deactivate BEFORE R1 completes. Bumps activationEpoch.
        vm.deactivate()

        await r1Gate.succeed(makeAttentionFeed(healthyPrinterCount: 5))
        _ = await r1.value

        XCTAssertEqual(
            callbackQueue.count, 0,
            "Stale-last completion after deactivate must NOT schedule follow-up (authority invalid)"
        )
        XCTAssertNil(
            vm.snapshot,
            "Nothing applied — view was inactive at completion time"
        )
    }

    func testStaleCompletionAfterServiceReplacementDoesNotScheduleOldOwnerFollowup() async {
        // Authority-invalidation via service replacement:
        // invalidateAuthority bumps activationEpoch AND resets the
        // pending state. Old-service refresh completion must not
        // schedule follow-up work under the new authority.
        let r1Gate = AttentionResultGate<AttentionFeed>()
        let oldService = ScriptedAttentionService(steps: [.gated(r1Gate)])
        let newService = ScriptedAttentionService(steps: [
            .value(makeAttentionFeed(healthyPrinterCount: 4)),
        ])
        let oldSignalR = MockSignalRService()
        let newSignalR = MockSignalRService()
        let callbackQueue = AttentionCallbackQueue()
        let vm = AttentionFeedViewModel(callbackEnqueuer: callbackQueue.enqueuer)
        vm.configure(
            attentionService: oldService,
            signalRService: oldSignalR,
            attentionEnabled: true
        )

        let r1 = Task { await vm.refresh() }
        await oldService.waitForLoadCount(1)

        oldSignalR.simulateAttentionChanged(
            AttentionChangedEvent(
                itemId: "failure:x",
                changeKind: .updated,
                occurredAt: Date()
            )
        )
        await callbackQueue.waitForCount(1)
        await callbackQueue.runNext()

        // Replace the service+signalR. invalidateAuthority bumps
        // activation and resets pending coverage.
        vm.configure(
            attentionService: newService,
            signalRService: newSignalR,
            attentionEnabled: true
        )

        await r1Gate.succeed(makeAttentionFeed(healthyPrinterCount: 5))
        _ = await r1.value

        XCTAssertEqual(
            callbackQueue.count, 0,
            "Stale completion under old authority must not schedule follow-up for the new authority"
        )
        // The new authority's own refresh path handles its lifecycle.
    }

    func testStaleTriggeredFollowupFailureDoesNotLoop() async {
        // Regression: the cycle-5 fix schedules a follow-up from a
        // stale completion when appropriate. If that follow-up
        // FAILS, the failure path must NOT schedule another
        // follow-up — preserving the no-auto-loop invariant even
        // under the new stranding-fix path.
        let r1Gate = AttentionResultGate<AttentionFeed>()
        let r2Gate = AttentionResultGate<AttentionFeed>()
        let service = ScriptedAttentionService(steps: [
            .gated(r1Gate),
            .gated(r2Gate),
            .failure(.forced("follow-up failed")),
        ])
        let signalR = MockSignalRService()
        let callbackQueue = AttentionCallbackQueue()
        let vm = AttentionFeedViewModel(callbackEnqueuer: callbackQueue.enqueuer)
        vm.configure(
            attentionService: service,
            signalRService: signalR,
            attentionEnabled: true
        )

        let r1 = Task { await vm.refresh() }
        await service.waitForLoadCount(1)
        let r2 = Task { await vm.refresh() }
        await service.waitForLoadCount(2)

        signalR.simulateAttentionChanged(
            AttentionChangedEvent(
                itemId: "failure:x",
                changeKind: .updated,
                occurredAt: Date()
            )
        )
        await callbackQueue.waitForCount(1)
        await callbackQueue.runNext()

        // R2 succeeds first, R1 stale last — stranding fix schedules
        // one follow-up.
        await r2Gate.succeed(makeAttentionFeed(healthyPrinterCount: 3))
        _ = await r2.value
        await r1Gate.succeed(makeAttentionFeed(healthyPrinterCount: 999))
        _ = await r1.value
        XCTAssertEqual(callbackQueue.count, 1)

        // Drain follow-up — the scripted step here is `.failure`.
        await callbackQueue.runNext()
        // Follow-up failed. Must NOT schedule another follow-up.
        XCTAssertEqual(
            callbackQueue.count, 0,
            "Failed follow-up must not schedule another — no auto-loop"
        )
        // Snapshot preserved from R2 (failure applyFailure keeps it).
        XCTAssertEqual(vm.snapshot?.healthyPrinterCount, 3)
        // Inline load failure surfaced.
        XCTAssertNotNil(vm.loadFailure)
        XCTAssertEqual(vm.loadFailure?.message, "follow-up failed")
    }

    // MARK: - Cycle 6: authority-scoped ownership + queued follow-up authority token

    func testOldAuthorityCompletionDoesNotCorruptNewAuthorityOwnership() async {
        // Blocker A: R1 is in flight under authority A when the
        // service is replaced. R2 starts under authority B. Event E
        // arrives on B and latches pending. A/R1 completes late.
        //
        // Old-authority completion must NOT: (1) release a B token
        // it doesn't own, (2) mutate B's coverage/pending, (3)
        // schedule follow-up work against B. B's own R2 must remain
        // the sole scheduler.
        let oldGate = AttentionResultGate<AttentionFeed>()
        let newGate = AttentionResultGate<AttentionFeed>()
        let newR3Feed = makeAttentionFeed(healthyPrinterCount: 9)
        let oldService = ScriptedAttentionService(steps: [.gated(oldGate)])
        let newService = ScriptedAttentionService(steps: [
            .gated(newGate),
            .value(newR3Feed),
        ])
        let oldSignalR = MockSignalRService()
        let newSignalR = MockSignalRService()
        let callbackQueue = AttentionCallbackQueue()
        let vm = AttentionFeedViewModel(callbackEnqueuer: callbackQueue.enqueuer)
        vm.configure(
            attentionService: oldService,
            signalRService: oldSignalR,
            attentionEnabled: true
        )

        let r1 = Task { await vm.refresh() }
        await oldService.waitForLoadCount(1)

        // Replace authority A → B. invalidateAuthority resets the
        // token set. A/R1's captured authority is now stale.
        vm.configure(
            attentionService: newService,
            signalRService: newSignalR,
            attentionEnabled: true
        )

        let r2 = Task { await vm.refresh() }
        await newService.waitForLoadCount(1)

        // Event E under B. Drain: activeRequestTokens contains B/R2's
        // token → latch pending, no dispatch.
        newSignalR.simulateAttentionChanged(
            AttentionChangedEvent(
                itemId: "failure:x",
                changeKind: .updated,
                occurredAt: Date()
            )
        )
        await callbackQueue.waitForCount(1)
        await callbackQueue.runNext()
        let midNewCount = await newService.loadCallCount
        XCTAssertEqual(
            midNewCount, 1,
            "Event drain during B/R2 in-flight must NOT launch concurrent GET on B"
        )

        // A/R1 completes as old-owner. Must be a total no-op wrt B.
        await oldGate.succeed(makeAttentionFeed(healthyPrinterCount: 999))
        _ = await r1.value
        let afterR1NewCount = await newService.loadCallCount
        XCTAssertEqual(
            afterR1NewCount, 1,
            "Old-authority completion must not trigger a new-authority GET"
        )
        XCTAssertEqual(
            callbackQueue.count, 0,
            "Old-authority completion must not schedule follow-up against new authority"
        )

        // B/R2 succeeds. cover(0) < E(seq=1). Pending remains
        // uncovered → schedules exactly one B/R3.
        await newGate.succeed(makeAttentionFeed(healthyPrinterCount: 3))
        _ = await r2.value
        XCTAssertEqual(vm.snapshot?.healthyPrinterCount, 3, "B/R2 applied")
        XCTAssertEqual(
            callbackQueue.count, 1,
            "Exactly one B/R3 scheduled after B/R2 terminal — B's ownership was intact"
        )

        await callbackQueue.runNext()  // B/R3 drains
        let finalNewCount = await newService.loadCallCount
        XCTAssertEqual(
            finalNewCount, 2,
            "Total on B: R2 + R3 = 2"
        )
        XCTAssertEqual(
            vm.snapshot?.healthyPrinterCount, 9,
            "B/R3 covers E"
        )
    }

    func testMultipleOldAuthorityCompletionsCannotUnderflowOrDrainNewAuthority() async {
        // Blocker A defense-in-depth: two concurrent A refreshes
        // outlive a replacement. If old-authority completions could
        // decrement a global counter, N old completions after B has
        // started could underflow (masked by max(0,...)) and make B
        // look permanently in-flight or make an event dispatch
        // during an "impossible idle" moment. With authority-scoped
        // tokens this is impossible — old completions have nothing
        // to remove from B's set.
        let a1Gate = AttentionResultGate<AttentionFeed>()
        let a2Gate = AttentionResultGate<AttentionFeed>()
        let bFirstFeed = makeAttentionFeed(healthyPrinterCount: 4)
        let bFollowupFeed = makeAttentionFeed(healthyPrinterCount: 9)
        let oldService = ScriptedAttentionService(steps: [
            .gated(a1Gate),
            .gated(a2Gate),
        ])
        let newService = ScriptedAttentionService(steps: [
            .value(bFirstFeed),
            .value(bFollowupFeed),
        ])
        let oldSignalR = MockSignalRService()
        let newSignalR = MockSignalRService()
        let callbackQueue = AttentionCallbackQueue()
        let vm = AttentionFeedViewModel(callbackEnqueuer: callbackQueue.enqueuer)
        vm.configure(
            attentionService: oldService,
            signalRService: oldSignalR,
            attentionEnabled: true
        )

        // Two concurrent A refreshes.
        let a1 = Task { await vm.refresh() }
        let a2 = Task { await vm.refresh() }
        await oldService.waitForLoadCount(2)

        // Replace to B.
        vm.configure(
            attentionService: newService,
            signalRService: newSignalR,
            attentionEnabled: true
        )

        // Complete BOTH old A refreshes while B has nothing in
        // flight. Neither may touch B's token set or coverage.
        await a1Gate.succeed(makeAttentionFeed(healthyPrinterCount: 111))
        _ = await a1.value
        await a2Gate.succeed(makeAttentionFeed(healthyPrinterCount: 222))
        _ = await a2.value

        let newAfterAs = await newService.loadCallCount
        XCTAssertEqual(
            newAfterAs, 0,
            "Two sequential old-authority completions must not touch B service"
        )
        XCTAssertEqual(
            callbackQueue.count, 0,
            "No follow-up scheduled from old-authority completions"
        )

        // B is now clean. A normal refresh + event cycle proves B's
        // ownership accounting is uncorrupted.
        let bRefreshOK = await vm.refresh()
        XCTAssertTrue(bRefreshOK)
        XCTAssertEqual(vm.snapshot?.healthyPrinterCount, 4)

        newSignalR.simulateAttentionChanged(
            AttentionChangedEvent(
                itemId: "failure:x",
                changeKind: .updated,
                occurredAt: Date()
            )
        )
        await callbackQueue.waitForCount(1)
        await callbackQueue.runNext()
        // Since B has no refresh in flight now, drain dispatches
        // directly.

        let finalCount = await newService.loadCallCount
        XCTAssertEqual(
            finalCount, 2,
            "Event drain on B dispatches normally: R2 + one drain-dispatched GET"
        )
        XCTAssertEqual(vm.snapshot?.healthyPrinterCount, 9)
    }

    func testOldAuthorityQueuedFollowUpNoOpsAfterServiceReplacement() async {
        // Blocker B: A/R1 completes with an uncovered pending event
        // and schedules a follow-up. Before the follow-up closure
        // drains, the service is replaced with B. The queued closure
        // captured authority A; at drain it must see the mismatch
        // and no-op — no old-owner GET against B's service, no
        // duplicate B bootstrap.
        let r1Gate = AttentionResultGate<AttentionFeed>()
        let r1Feed = makeAttentionFeed(healthyPrinterCount: 1)
        let bBootstrapFeed = makeAttentionFeed(healthyPrinterCount: 5)
        let oldService = ScriptedAttentionService(steps: [.gated(r1Gate)])
        let newService = ScriptedAttentionService(steps: [.value(bBootstrapFeed)])
        let oldSignalR = MockSignalRService()
        let newSignalR = MockSignalRService()
        let callbackQueue = AttentionCallbackQueue()
        let vm = AttentionFeedViewModel(callbackEnqueuer: callbackQueue.enqueuer)
        vm.configure(
            attentionService: oldService,
            signalRService: oldSignalR,
            attentionEnabled: true
        )

        let r1 = Task { await vm.refresh() }
        await oldService.waitForLoadCount(1)

        // Event during A/R1 flight → latches pending.
        oldSignalR.simulateAttentionChanged(
            AttentionChangedEvent(
                itemId: "failure:x",
                changeKind: .updated,
                occurredAt: Date()
            )
        )
        await callbackQueue.waitForCount(1)
        await callbackQueue.runNext()

        // A/R1 succeeds. cover(0) < event seq(1) → pending survives
        // → schedules follow-up (captured authorityEpoch = A).
        await r1Gate.succeed(r1Feed)
        _ = await r1.value
        XCTAssertEqual(
            callbackQueue.count, 1,
            "A/R1 success schedules a follow-up (captured authority = A)"
        )
        let oldCountAfterR1 = await oldService.loadCallCount
        XCTAssertEqual(oldCountAfterR1, 1)

        // Replace to B BEFORE the follow-up drains. invalidateAuthority
        // bumps authorityEpoch → the queued closure's captured
        // authority is now stale.
        vm.configure(
            attentionService: newService,
            signalRService: newSignalR,
            attentionEnabled: true
        )

        // Drain the follow-up closure. It must detect authority
        // mismatch and no-op — no call against old service (which
        // has been replaced) and no old-owner call against new
        // service (which would steal B's bootstrap slot).
        await callbackQueue.runNext()
        let oldCountAfterDrain = await oldService.loadCallCount
        XCTAssertEqual(
            oldCountAfterDrain, 1,
            "Follow-up must not fire against old service after replacement"
        )
        let newCountAfterDrain = await newService.loadCallCount
        XCTAssertEqual(
            newCountAfterDrain, 0,
            "Follow-up must not fire against new service (would be old-owner work)"
        )

        // Bootstrap B: exactly one canonical GET.
        _ = await vm.bootstrap(
            attentionService: newService,
            signalRService: newSignalR,
            attentionEnabled: true
        )
        let bCount = await newService.loadCallCount
        XCTAssertEqual(
            bCount, 1,
            "B bootstrap issues exactly one canonical GET, undisturbed by old-owner follow-up"
        )
        XCTAssertEqual(vm.snapshot?.healthyPrinterCount, 5)
    }

    func testQueuedFollowUpInvalidatedByDeactivateReactivate() async {
        // Blocker B, activation variant: same authority but the
        // activation epoch moves (deactivate+reactivate) between
        // scheduling and drain. Queued closure captured activation
        // A0; current is A0+2 → mismatch → no-op.
        let r1Gate = AttentionResultGate<AttentionFeed>()
        let r1Feed = makeAttentionFeed(healthyPrinterCount: 1)
        let service = ScriptedAttentionService(steps: [.gated(r1Gate)])
        let signalR = MockSignalRService()
        let callbackQueue = AttentionCallbackQueue()
        let vm = AttentionFeedViewModel(callbackEnqueuer: callbackQueue.enqueuer)
        vm.configure(
            attentionService: service,
            signalRService: signalR,
            attentionEnabled: true
        )

        let r1 = Task { await vm.refresh() }
        await service.waitForLoadCount(1)

        // Event during flight → latches pending.
        signalR.simulateAttentionChanged(
            AttentionChangedEvent(
                itemId: "failure:x",
                changeKind: .updated,
                occurredAt: Date()
            )
        )
        await callbackQueue.waitForCount(1)
        await callbackQueue.runNext()

        // R1 succeeds → schedules follow-up (captured activation = A0).
        await r1Gate.succeed(r1Feed)
        _ = await r1.value
        XCTAssertEqual(
            callbackQueue.count, 1,
            "R1 success schedules follow-up under current activation"
        )

        // Deactivate + reactivate BEFORE follow-up drains.
        vm.deactivate()
        vm.activate()
        // Activate did NOT enqueue a drain here: pendingReloadOnActivate
        // was never set (event drain saw activeRequestTokens non-empty,
        // took the "return" branch without setting the flag).
        XCTAssertEqual(
            callbackQueue.count, 1,
            "Activate must not enqueue a spurious drain when no pendingReloadOnActivate was set"
        )

        // Drain the follow-up. Activation mismatch → no-op.
        await callbackQueue.runNext()
        let countAfterDrain = await service.loadCallCount
        XCTAssertEqual(
            countAfterDrain, 1,
            "Follow-up must no-op after activation moved (deactivate+reactivate)"
        )
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
