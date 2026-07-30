import XCTest
@testable import PrintFarmer

// MARK: - AttentionFeedViewModel reconnect-gap refresh (#781, F2-R)

/// Deterministic proofs for the single canonical gap-closing refresh
/// that fires on exactly the real SignalR `.reconnecting -> .connected`
/// recovery edge (Epic #705, issue #781).
///
/// Every test drives the connection-state edge through #777's
/// `onConnectionStateChanged` contract (via `MockSignalRService`) and
/// the injectable `AttentionCallbackQueue`, so there are NO fixed
/// sleeps, `Task.yield`, polling, or elapsed-time pass criteria. The
/// callback queue is drained one op at a time (`runNext`) and load
/// counts are read from the actor-backed `ScriptedAttentionService`.
@MainActor
final class AttentionReconnectGapTests: XCTestCase {

    // MARK: - 1. Recovery edge → exactly one request + one applied generation

    func testReconnectRecoveryEdgeIssuesExactlyOneCanonicalRefresh() async {
        let recoveryFeed = makeAttentionFeed(
            items: [makeAttentionItem(id: "failure:1", title: "Recovered")],
            healthyPrinterCount: 2
        )
        let service = ScriptedAttentionService(steps: [.value(recoveryFeed)])
        let signalR = MockSignalRService()
        let callbackQueue = AttentionCallbackQueue()
        let vm = AttentionFeedViewModel(callbackEnqueuer: callbackQueue.enqueuer)
        vm.configure(
            attentionService: service,
            signalRService: signalR,
            attentionEnabled: true
        )

        // Exactly one connection-state observer registered (#777).
        XCTAssertEqual(signalR.connectionStateSubscriberCount, 1)

        // Full realistic lifecycle: cold connect (no refresh), drop,
        // then recover (exactly one refresh).
        signalR.simulateConnectionStateChange(.connected)     // disconnected -> connected (cold)
        signalR.simulateConnectionStateChange(.reconnecting)  // connected -> reconnecting (gap)
        signalR.simulateConnectionStateChange(.connected)     // reconnecting -> connected (recover)
        await callbackQueue.waitForCount(3)

        await callbackQueue.runNext() // cold connect edge — no-op for gap refresh
        var calls = await service.loadCallCount
        XCTAssertEqual(calls, 0, "Cold connect must not trigger a gap refresh")

        await callbackQueue.runNext() // connected -> reconnecting — no-op
        calls = await service.loadCallCount
        XCTAssertEqual(calls, 0, "Dropping to reconnecting must not fetch")

        await callbackQueue.runNext() // reconnecting -> connected — the recovery edge
        calls = await service.loadCallCount
        XCTAssertEqual(calls, 1, "Real reconnect must trigger exactly one canonical refresh")

        XCTAssertEqual(vm.phase, .loaded)
        XCTAssertEqual(vm.snapshot?.items.count, 1)
        XCTAssertEqual(vm.snapshot?.items.first?.title, "Recovered",
                       "The single recovery generation must be applied")
        XCTAssertEqual(callbackQueue.count, 0)
    }

    // MARK: - 2. Initial cold connect → zero reconnect requests

    func testInitialColdConnectIssuesZeroReconnectRefreshes() async {
        let service = ScriptedAttentionService(steps: [])
        let signalR = MockSignalRService()
        let callbackQueue = AttentionCallbackQueue()
        let vm = AttentionFeedViewModel(callbackEnqueuer: callbackQueue.enqueuer)
        vm.configure(
            attentionService: service,
            signalRService: signalR,
            attentionEnabled: true
        )

        // Cold bootstrap edge sequence: disconnected -> connecting -> connected.
        signalR.simulateConnectionStateChange(.connecting)
        signalR.simulateConnectionStateChange(.connected)
        await callbackQueue.waitForCount(2)
        await callbackQueue.runNext()
        await callbackQueue.runNext()

        let calls = await service.loadCallCount
        XCTAssertEqual(calls, 0, "Cold initial connection → zero reconnect refreshes")
        // Cold bootstrap stays owned by #779 — configure alone must not fetch.
        XCTAssertEqual(vm.phase, .idle)
        XCTAssertEqual(callbackQueue.count, 0)
    }

    // MARK: - 3. connected->connected & repeated recovery → no duplicate

    func testRedundantConnectedIsNoOpAndEachRecoveryFiresExactlyOnce() async {
        let service = ScriptedAttentionService(steps: [
            .value(makeAttentionFeed(healthyPrinterCount: 1)),
            .value(makeAttentionFeed(healthyPrinterCount: 2)),
        ])
        let signalR = MockSignalRService()
        let callbackQueue = AttentionCallbackQueue()
        let vm = AttentionFeedViewModel(callbackEnqueuer: callbackQueue.enqueuer)
        vm.configure(
            attentionService: service,
            signalRService: signalR,
            attentionEnabled: true
        )

        // Cold connect first (no refresh).
        signalR.simulateConnectionStateChange(.connected)
        await callbackQueue.waitForCount(1)
        await callbackQueue.runNext()
        var calls = await service.loadCallCount
        XCTAssertEqual(calls, 0)

        // Redundant connected -> connected: the hub dedupes identical
        // states, so no callback is even enqueued — zero gap refresh.
        signalR.simulateConnectionStateChange(.connected)
        XCTAssertEqual(callbackQueue.count, 0,
                       "connected -> connected must not enqueue a transition")
        calls = await service.loadCallCount
        XCTAssertEqual(calls, 0)

        // First recovery → exactly one refresh.
        signalR.simulateConnectionStateChange(.reconnecting)
        signalR.simulateConnectionStateChange(.connected)
        await callbackQueue.waitForCount(2)
        await callbackQueue.runNext() // connected -> reconnecting
        await callbackQueue.runNext() // reconnecting -> connected
        calls = await service.loadCallCount
        XCTAssertEqual(calls, 1, "One recovery edge → exactly one refresh, no dup")

        // Second recovery → exactly one more (two total).
        signalR.simulateConnectionStateChange(.reconnecting)
        signalR.simulateConnectionStateChange(.connected)
        await callbackQueue.waitForCount(2)
        await callbackQueue.runNext()
        await callbackQueue.runNext()
        calls = await service.loadCallCount
        XCTAssertEqual(calls, 2, "Second real recovery → one additional refresh")
        XCTAssertEqual(callbackQueue.count, 0)
    }

    // MARK: - 4. configure 3x / one service → one registration + one refresh

    func testRepeatedConfigureIsIdempotentSingleRegistrationSingleRefresh() async {
        let service = ScriptedAttentionService(steps: [
            .value(makeAttentionFeed(healthyPrinterCount: 3)),
        ])
        let signalR = MockSignalRService()
        let callbackQueue = AttentionCallbackQueue()
        let vm = AttentionFeedViewModel(callbackEnqueuer: callbackQueue.enqueuer)

        // Three configure calls with the SAME service + signalR instances.
        vm.configure(attentionService: service, signalRService: signalR, attentionEnabled: true)
        vm.configure(attentionService: service, signalRService: signalR, attentionEnabled: true)
        vm.configure(attentionService: service, signalRService: signalR, attentionEnabled: true)

        // Idempotent: exactly one live observer of each kind.
        XCTAssertEqual(signalR.connectionStateSubscriberCount, 1,
                       "Repeated same-instance configure must not stack observers")
        XCTAssertEqual(signalR.attentionSubscriberCount, 1)

        // Recovery → exactly one refresh.
        signalR.simulateConnectionStateChange(.connected)     // cold
        signalR.simulateConnectionStateChange(.reconnecting)
        signalR.simulateConnectionStateChange(.connected)     // recover
        await callbackQueue.waitForCount(3)
        await callbackQueue.runNext()
        await callbackQueue.runNext()
        await callbackQueue.runNext()

        let calls = await service.loadCallCount
        XCTAssertEqual(calls, 1, "One reconnect over an idempotent registration → one refresh")
        XCTAssertEqual(callbackQueue.count, 0)
    }

    // MARK: - 5. Configure-while-reconnecting → next connected one refresh, no storm

    func testConfigureWhileReconnectingRefreshesOnceOnNextConnected() async {
        let service = ScriptedAttentionService(steps: [
            .value(makeAttentionFeed(healthyPrinterCount: 5)),
        ])
        let signalR = MockSignalRService()
        // Service is ALREADY reconnecting when the VM configures.
        signalR.connectionState = .reconnecting

        let callbackQueue = AttentionCallbackQueue()
        let vm = AttentionFeedViewModel(callbackEnqueuer: callbackQueue.enqueuer)
        vm.configure(
            attentionService: service,
            signalRService: signalR,
            attentionEnabled: true
        )

        // Seeding the observed state must NOT fabricate a transition.
        XCTAssertEqual(callbackQueue.count, 0,
                       "Registering while reconnecting must not enqueue a fake edge")
        var calls = await service.loadCallCount
        XCTAssertEqual(calls, 0, "Seed must not fetch")

        // The NEXT connected is the real recovery edge → exactly one refresh.
        signalR.simulateConnectionStateChange(.connected)
        await callbackQueue.waitForCount(1)
        await callbackQueue.runNext()

        calls = await service.loadCallCount
        XCTAssertEqual(calls, 1, "First connected after a reconnecting-seed → one refresh")
        XCTAssertEqual(vm.snapshot?.healthyPrinterCount, 5)
        XCTAssertEqual(callbackQueue.count, 0, "No reload storm")
    }

    // MARK: - 6. Server swap → old cannot trigger, new triggers once

    func testServerSwapOldObserverCannotTriggerNewTriggersOnce() async {
        let serviceA = ScriptedAttentionService(steps: [])
        let signalRA = MockSignalRService()
        let serviceB = ScriptedAttentionService(steps: [
            .value(makeAttentionFeed(healthyPrinterCount: 9)),
        ])
        let signalRB = MockSignalRService()

        let callbackQueue = AttentionCallbackQueue()
        let vm = AttentionFeedViewModel(callbackEnqueuer: callbackQueue.enqueuer)

        vm.configure(attentionService: serviceA, signalRService: signalRA, attentionEnabled: true)
        XCTAssertEqual(signalRA.connectionStateSubscriberCount, 1)

        // Swap to a different service + signalR pair.
        vm.configure(attentionService: serviceB, signalRService: signalRB, attentionEnabled: true)

        // Old service's observer torn down; new service has exactly one.
        XCTAssertEqual(signalRA.connectionStateSubscriberCount, 0,
                       "Server swap must leave zero observers on the old service")
        XCTAssertEqual(signalRB.connectionStateSubscriberCount, 1,
                       "Server swap must register exactly one observer on the new service")

        // Old service recovery edge: must be inert (no observer, and any
        // late captured callback is authority-fenced).
        signalRA.simulateConnectionStateChange(.reconnecting)
        signalRA.simulateConnectionStateChange(.connected)
        XCTAssertEqual(callbackQueue.count, 0,
                       "Old service transitions must not reach the swapped VM")
        let oldCalls = await serviceA.loadCallCount
        XCTAssertEqual(oldCalls, 0, "Old service must never be fetched after swap")

        // New service recovery edge → exactly one refresh, routed to B.
        signalRB.simulateConnectionStateChange(.connected)     // cold
        signalRB.simulateConnectionStateChange(.reconnecting)
        signalRB.simulateConnectionStateChange(.connected)     // recover
        await callbackQueue.waitForCount(3)
        await callbackQueue.runNext()
        await callbackQueue.runNext()
        await callbackQueue.runNext()

        let newCalls = await serviceB.loadCallCount
        XCTAssertEqual(newCalls, 1, "New service reconnect → exactly one refresh")
        XCTAssertEqual(vm.snapshot?.healthyPrinterCount, 9)
    }

    // MARK: - 7. Recovery while inactive → drains exactly one on eligibility

    func testRecoveryWhileInactiveDrainsExactlyOneOnActivate() async {
        let service = ScriptedAttentionService(steps: [
            .value(makeAttentionFeed(healthyPrinterCount: 4)),
        ])
        let signalR = MockSignalRService()
        signalR.connectionState = .connected

        let callbackQueue = AttentionCallbackQueue()
        let vm = AttentionFeedViewModel(callbackEnqueuer: callbackQueue.enqueuer)
        vm.configure(
            attentionService: service,
            signalRService: signalR,
            attentionEnabled: true
        )

        // Leave the screen: observer preserved, prior state preserved.
        vm.deactivate()

        // Recovery edge while off-screen.
        signalR.simulateConnectionStateChange(.reconnecting)
        signalR.simulateConnectionStateChange(.connected)
        await callbackQueue.waitForCount(2)
        await callbackQueue.runNext() // connected -> reconnecting
        await callbackQueue.runNext() // reconnecting -> connected → refresh() sees inactive

        // Inactive: refresh routed through #779 queues a pending reload,
        // no GET fires while off-screen.
        var calls = await service.loadCallCount
        XCTAssertEqual(calls, 0, "Recovery must not fetch while inactive")

        // Re-entering the screen drains exactly one queued reload.
        vm.activate()
        await callbackQueue.waitForCount(1)
        await callbackQueue.runNext()

        calls = await service.loadCallCount
        XCTAssertEqual(calls, 1, "Exactly one queued recovery reload drains on activate")
        XCTAssertEqual(vm.snapshot?.healthyPrinterCount, 4)
        XCTAssertEqual(callbackQueue.count, 0)
    }

    // MARK: - 8. Recovery while loading → coalesce (at most one additional)

    func testRecoveryWhileLoadingCoalescesSupersedingInFlightRefresh() async {
        let inFlightGate = AttentionResultGate<AttentionFeed>()
        let recoveryFeed = makeAttentionFeed(
            items: [makeAttentionItem(id: "failure:2", title: "Recovery win")],
            healthyPrinterCount: 6
        )
        let service = ScriptedAttentionService(steps: [
            .gated(inFlightGate),        // load #1 (manual, in flight)
            .value(recoveryFeed),        // load #2 (recovery edge)
        ])
        let signalR = MockSignalRService()
        signalR.connectionState = .connected

        let callbackQueue = AttentionCallbackQueue()
        let vm = AttentionFeedViewModel(callbackEnqueuer: callbackQueue.enqueuer)
        vm.configure(
            attentionService: service,
            signalRService: signalR,
            attentionEnabled: true
        )

        // Kick off a manual refresh and park it in flight.
        let inFlight = Task { await vm.refresh() }
        await service.waitForLoadCount(1)
        XCTAssertEqual(vm.phase, .loading)

        // Recovery edge while a refresh is in flight.
        signalR.simulateConnectionStateChange(.reconnecting)
        signalR.simulateConnectionStateChange(.connected)
        await callbackQueue.waitForCount(2)
        await callbackQueue.runNext() // connected -> reconnecting
        await callbackQueue.runNext() // reconnecting -> connected → refresh() supersedes

        // The recovery refresh (load #2) applied its generation.
        var calls = await service.loadCallCount
        XCTAssertEqual(calls, 2, "Recovery edge issues exactly one additional request")
        XCTAssertEqual(vm.snapshot?.items.first?.title, "Recovery win")

        // The superseded in-flight load (#1) now resolves late — it is
        // generation-stale and must be dropped (no storm, no overwrite).
        await inFlightGate.succeed(makeAttentionFeed(
            items: [makeAttentionItem(id: "failure:99", title: "Stale")],
            healthyPrinterCount: 99
        ))
        let inFlightApplied = await inFlight.value
        XCTAssertFalse(inFlightApplied, "Superseded in-flight load must drop")

        calls = await service.loadCallCount
        XCTAssertEqual(calls, 2, "No third request — never a reload storm")
        XCTAssertEqual(vm.snapshot?.items.first?.title, "Recovery win",
                       "Recovery generation stays applied")
    }

    // MARK: - 9. Feature-disabled stays governed by #779 (recovery cannot re-enable)

    func testRecoveryCannotReEnableAFeatureDisabledFeed() async {
        let service = ScriptedAttentionService(steps: [])
        let signalR = MockSignalRService()
        let callbackQueue = AttentionCallbackQueue()
        let vm = AttentionFeedViewModel(callbackEnqueuer: callbackQueue.enqueuer)
        // Feature gate OFF.
        vm.configure(
            attentionService: service,
            signalRService: signalR,
            attentionEnabled: false
        )
        XCTAssertEqual(vm.phase, .disabled)

        // A real recovery edge must NOT re-enable or fetch.
        signalR.simulateConnectionStateChange(.connected)
        signalR.simulateConnectionStateChange(.reconnecting)
        signalR.simulateConnectionStateChange(.connected)
        await callbackQueue.waitForCount(3)
        await callbackQueue.runNext()
        await callbackQueue.runNext()
        await callbackQueue.runNext()

        let calls = await service.loadCallCount
        XCTAssertEqual(calls, 0, "Recovery cannot fetch a feature-disabled feed")
        XCTAssertEqual(vm.phase, .disabled, "Recovery cannot re-enable the feature")
        XCTAssertEqual(callbackQueue.count, 0)
    }

    // MARK: - 10. Stale-generation stays governed (recovery cannot apply stale)

    func testRecoveryRefreshCannotApplyStaleWhenSuperseded() async {
        let recoveryGate = AttentionResultGate<AttentionFeed>()
        let newerFeed = makeAttentionFeed(
            items: [makeAttentionItem(id: "failure:3", title: "Newer wins")],
            healthyPrinterCount: 7
        )
        let service = ScriptedAttentionService(steps: [
            .gated(recoveryGate),        // load #1 (recovery, parked)
            .value(newerFeed),           // load #2 (newer, supersedes)
        ])
        let signalR = MockSignalRService()
        signalR.connectionState = .connected

        let callbackQueue = AttentionCallbackQueue()
        let vm = AttentionFeedViewModel(callbackEnqueuer: callbackQueue.enqueuer)
        vm.configure(
            attentionService: service,
            signalRService: signalR,
            attentionEnabled: true
        )

        // Recovery edge → recovery refresh (load #1) parks on the gate.
        signalR.simulateConnectionStateChange(.reconnecting)
        signalR.simulateConnectionStateChange(.connected)
        await callbackQueue.waitForCount(2)
        await callbackQueue.runNext() // connected -> reconnecting
        let recoveryDrain = Task { await callbackQueue.runNext() } // recovery refresh, parks
        await service.waitForLoadCount(1)

        // A newer refresh supersedes the parked recovery refresh.
        let newer = Task { await vm.refresh() }
        await service.waitForLoadCount(2)
        _ = await newer.value
        XCTAssertEqual(vm.snapshot?.items.first?.title, "Newer wins")

        // The recovery load now resolves late — stale, must be dropped.
        await recoveryGate.succeed(makeAttentionFeed(
            items: [makeAttentionItem(id: "failure:0", title: "Stale recovery")],
            healthyPrinterCount: 1
        ))
        await recoveryDrain.value

        XCTAssertEqual(vm.snapshot?.items.first?.title, "Newer wins",
                       "Stale recovery generation must not overwrite the newer feed")
        let calls = await service.loadCallCount
        XCTAssertEqual(calls, 2)
    }

    // MARK: - 11. Gap integration — new item appears solely from refetch

    func testGapMutationBecomesVisibleSolelyThroughRecoveryRefetch() async {
        let itemA = makeAttentionItem(id: "failure:A", title: "A")
        let itemB = makeAttentionItem(id: "failure:B", title: "B")
        let feedBefore = makeAttentionFeed(items: [itemA], healthyPrinterCount: 1)
        // The canonical feed gains item B during the disconnect gap. No
        // SignalR payload is ever delivered for it — the ONLY way it can
        // become visible is a refetch on reconnect.
        let feedAfter = makeAttentionFeed(items: [itemA, itemB], healthyPrinterCount: 1)
        let service = ScriptedAttentionService(steps: [
            .value(feedBefore),
            .value(feedAfter),
        ])
        let signalR = MockSignalRService()
        signalR.connectionState = .connected

        let callbackQueue = AttentionCallbackQueue()
        let vm = AttentionFeedViewModel(callbackEnqueuer: callbackQueue.enqueuer)
        vm.configure(
            attentionService: service,
            signalRService: signalR,
            attentionEnabled: true
        )

        // Initial load reflects the pre-gap feed.
        _ = await vm.refresh()
        XCTAssertEqual(vm.snapshot?.items.map(\.id), ["failure:A"])

        // Gap: drop and recover. No `simulateAttentionChanged` is ever
        // called, so item B cannot arrive via payload insertion.
        signalR.simulateConnectionStateChange(.reconnecting)
        signalR.simulateConnectionStateChange(.connected)
        await callbackQueue.waitForCount(2)
        await callbackQueue.runNext() // connected -> reconnecting
        await callbackQueue.runNext() // reconnecting -> connected → recovery refetch

        // Item B is now visible — solely because the recovery refetch
        // pulled the mutated canonical feed.
        XCTAssertEqual(vm.snapshot?.items.map(\.id), ["failure:A", "failure:B"],
                       "Gap mutation must surface via refetch, not payload insertion")
        let calls = await service.loadCallCount
        XCTAssertEqual(calls, 2, "Initial load + exactly one recovery refetch")
    }
}
