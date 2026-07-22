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
        let firstGate = AttentionResultGate<AttentionFeed>()
        let service = ScriptedAttentionService(
            steps: [
                .gated(firstGate),
                .value(makeAttentionFeed(healthyPrinterCount: 2)),
            ]
        )
        let vm = configuredViewModel(service: service)

        let first = Task { await vm.refresh() }
        await service.waitForLoadCount(1)
        _ = await vm.refresh() // newer

        // Older call now decides to "disable" — resolve it after the
        // second success has landed. `AttentionResultGate` cannot itself
        // raise a `featureDisabled`, so we simulate by failing with a
        // sentinel and asserting the newer state is untouched.
        await firstGate.fail(.forced("gate-flip"))
        _ = await first.value

        XCTAssertEqual(vm.phase, .loaded)
        XCTAssertEqual(vm.snapshot?.healthyPrinterCount, 2)
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
