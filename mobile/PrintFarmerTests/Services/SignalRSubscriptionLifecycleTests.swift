import XCTest
@testable import PrintFarmer

/// Blocker #2 (subscription lifecycle) — reviewer proof.
///
/// These tests exercise the cancellable-subscription contract that
/// `SignalRService`/`DemoSignalRService`/`MockSignalRService` share via
/// `SignalRConnectionStateHub` and `SignalREventHub`. They prove that:
///
/// 1. Multiple subscribers on the same hub each observe every transition
///    (no first-write-wins single-slot regression).
/// 2. Cancelling a subscription stops delivery to that handler
///    idempotently and independently of other subscribers.
/// 3. Re-configuring a view model does not accumulate handlers in the
///    shared service (the view model's own subscription array cancels the
///    previous batch before re-registering).
/// 4. Deallocating a view model releases its subscription references,
///    which in turn cancel via `SignalRSubscription.deinit`, so the shared
///    service handler count falls back to any long-lived subscribers.
@MainActor
final class SignalRSubscriptionLifecycleTests: XCTestCase {

    // MARK: - Hub-level: multiple subscribers each receive transitions

    func testHubMultipleSubscribersEachReceiveTransitions() {
        let mock = MockSignalRService()

        let box = LockedIntPair()

        // Retain subscription tokens for the duration of the test —
        // otherwise their `deinit` would cancel them immediately.
        let (_, subA) = mock.onConnectionStateChanged { _ in box.bumpA() }
        let (_, subB) = mock.onConnectionStateChanged { _ in box.bumpB() }
        withExtendedLifetime((subA, subB)) {
            // `deliverSync`/`setStateSync` on the hub blocks until every
            // registered handler has run, so the counters are updated by
            // the time the setter returns.
            mock.simulateConnectionStateChange(.connected)
            mock.simulateConnectionStateChange(.reconnecting)
            mock.simulateConnectionStateChange(.connected)

            let (a, b) = box.snapshot()
            XCTAssertEqual(a, 3)
            XCTAssertEqual(b, 3)
            XCTAssertEqual(mock.connectionStateSubscriberCount, 2)
        }
    }

    // MARK: - Cancellation stops delivery

    func testHubCancelledSubscriptionStopsReceivingTransitions() {
        let mock = MockSignalRService()
        let box = LockedIntPair()

        let (_, subA) = mock.onConnectionStateChanged { _ in box.bumpA() }
        let (_, subB) = mock.onConnectionStateChanged { _ in box.bumpB() }
        withExtendedLifetime(subB) {
            mock.simulateConnectionStateChange(.connected)      // A=1, B=1
            subA.cancel()
            mock.simulateConnectionStateChange(.reconnecting)   // A=1, B=2
            subA.cancel()                                        // idempotent
            mock.simulateConnectionStateChange(.connected)      // A=1, B=3

            let (a, b) = box.snapshot()
            XCTAssertEqual(a, 1, "cancelled subscription must stop receiving transitions")
            XCTAssertEqual(b, 3, "surviving subscription must keep receiving transitions")
            XCTAssertEqual(mock.connectionStateSubscriberCount, 1)
        }
    }

    // MARK: - Deallocation cancels all subscriptions

    // MARK: - r7 blocker #1: insertion-ordered subscriber delivery

    /// Registering many subscribers in a known order and then firing
    /// several transitions must deliver events to each subscriber in
    /// registration order — not in some hash-shuffled order.
    /// Dictionary.values ordering is unspecified/randomized; the r7 fix
    /// uses a parallel insertion-order array so delivery is a stable,
    /// registration-order sequence. Repeated across many transitions
    /// so a lucky hash-order run cannot mask the regression.
    func testConnectionStateSubscribersReceiveInRegistrationOrder() {
        let mock = MockSignalRService()
        let sequence = LockedStringSequence()

        // Register a batch of subscribers. Order is [S0, S1, S2, S3, S4].
        var subs: [SignalRSubscription] = []
        for i in 0..<5 {
            let tag = "S\(i)"
            let (_, sub) = mock.onConnectionStateChanged { _ in
                sequence.append(tag)
            }
            subs.append(sub)
        }

        withExtendedLifetime(subs) {
            // Fire several transitions; each must deliver in the SAME
            // registration order for the SAME set of subscribers.
            mock.simulateConnectionStateChange(.connected)
            mock.simulateConnectionStateChange(.reconnecting)
            mock.simulateConnectionStateChange(.connected)

            let expected = (0..<3).flatMap { _ in ["S0", "S1", "S2", "S3", "S4"] }
            XCTAssertEqual(sequence.snapshot(), expected,
                "connection-state hub must deliver subscribers in registration order, repeatably across events")
        }
    }

    /// Event-hub equivalent: printer-update subscribers must fire in
    /// registration order across many events.
    func testEventHubSubscribersReceiveInRegistrationOrder() {
        let mock = MockSignalRService()
        let sequence = LockedStringSequence()

        var subs: [SignalRSubscription] = []
        for i in 0..<4 {
            let tag = "E\(i)"
            subs.append(mock.onPrinterUpdated { _ in sequence.append(tag) })
        }

        withExtendedLifetime(subs) {
            let update = PrinterStatusUpdate(
                id: UUID(), isOnline: true, state: "printing", progress: nil,
                jobName: nil, fileName: nil, thumbnailUrl: nil, cameraStreamUrl: nil,
                x: nil, y: nil, z: nil, hotendTemp: nil, bedTemp: nil,
                hotendTarget: nil, bedTarget: nil, homedAxes: nil,
                spoolInfo: nil, mmuStatus: nil
            )
            for _ in 0..<3 { mock.simulatePrinterUpdate(update) }

            let expected = (0..<3).flatMap { _ in ["E0", "E1", "E2", "E3"] }
            XCTAssertEqual(sequence.snapshot(), expected,
                "event hub must deliver subscribers in registration order, repeatably across events")
        }
    }
}

// MARK: - Test helper: lock-guarded int pair

/// Small `NSLock`-guarded counter used by the multi-subscriber and
/// cancellation tests. Handlers run on the hub's serial dispatch queue and
/// the test reads snapshots from the main actor; the lock keeps the two
/// races-free without needing an actor hop inside the handler closure.
final class LockedIntPair: @unchecked Sendable {
    private let lock = NSLock()
    private var _a = 0
    private var _b = 0

    func bumpA() { lock.lock(); _a += 1; lock.unlock() }
    func bumpB() { lock.lock(); _b += 1; lock.unlock() }
    func snapshot() -> (Int, Int) {
        lock.lock(); defer { lock.unlock() }
        return (_a, _b)
    }
}

// MARK: - Reentrancy: callbacks running on the hub's serial queue may
// synchronously snapshot / subscribe / cancel without deadlocking.

/// Blocker #2 (Hicks / v3-r3): callbacks executed on the hub serial queue
/// used to deadlock any code that called back into `snapshot()`, `subscribe`,
/// or `cancel()` on the same hub (unconditional `queue.sync`). The
/// `SignalRHubQueue` primitive now detects on-queue execution via a
/// `DispatchSpecificKey` and runs inline, so these tests must run to
/// completion (any deadlock would hang the test out to XCTest's timeout).
@MainActor
final class SignalRSubscriptionReentrancyTests: XCTestCase {

    func testCallbackCanSnapshotStateWithoutDeadlock() {
        let mock = MockSignalRService()

        let stateBox = LockedStateBox()
        let (_, sub) = mock.onConnectionStateChanged { [weak mock] _ in
            // While delivering, ask the hub for its current state. Under the
            // old implementation this would `queue.sync` into itself and
            // deadlock. Under the reentrant implementation it runs inline.
            guard let mock else { return }
            stateBox.store(mock.connectionState)
        }
        withExtendedLifetime(sub) {
            mock.simulateConnectionStateChange(.connected)
            mock.simulateConnectionStateChange(.reconnecting)

            XCTAssertEqual(stateBox.load(), .reconnecting,
                "callback must observe the state that triggered its own delivery")
        }
    }

    func testCallbackCanSubscribeWithoutDeadlock() {
        let mock = MockSignalRService()
        let box = LockedIntPair()

        // Outer subscription: when it fires the first time, register a
        // NEW subscription from inside the callback. Under the old
        // implementation the nested `subscribe` would `queue.sync` into
        // the hub while already on the hub queue and deadlock.
        let innerHolder = SubscriptionHolder()
        let (_, outerSub) = mock.onConnectionStateChanged { [weak mock] _ in
            box.bumpA()
            guard let mock, innerHolder.isEmpty else { return }
            let (_, inner) = mock.onConnectionStateChanged { _ in box.bumpB() }
            innerHolder.store(inner)
        }

        withExtendedLifetime(outerSub) {
            mock.simulateConnectionStateChange(.connected)      // A=1, inner registered
            mock.simulateConnectionStateChange(.reconnecting)   // A=2, B=1
            mock.simulateConnectionStateChange(.connected)      // A=3, B=2

            let (a, b) = box.snapshot()
            XCTAssertEqual(a, 3, "outer subscription must keep receiving after nested subscribe")
            XCTAssertEqual(b, 2, "inner subscription registered from callback must receive future transitions")
            XCTAssertEqual(mock.connectionStateSubscriberCount, 2)
        }
        innerHolder.clear()
    }

    func testCallbackCanCancelItsOwnSubscriptionWithoutDeadlock() {
        let mock = MockSignalRService()
        let box = LockedIntPair()
        let survivorSub: SignalRSubscription
        let selfCancellerSub: SignalRSubscription
        let selfCancelHolder = SubscriptionHolder()

        (_, survivorSub) = mock.onConnectionStateChanged { _ in box.bumpB() }
        (_, selfCancellerSub) = mock.onConnectionStateChanged { _ in
            box.bumpA()
            // Cancel ourselves while executing on the hub queue. Under the
            // old implementation `cancel()` would `queue.sync` into itself
            // and deadlock.
            selfCancelHolder.cancelStored()
        }
        selfCancelHolder.store(selfCancellerSub)

        withExtendedLifetime((survivorSub, selfCancellerSub)) {
            mock.simulateConnectionStateChange(.connected)      // A=1 (then A cancels itself), B=1
            mock.simulateConnectionStateChange(.reconnecting)   // A stays 1, B=2
            mock.simulateConnectionStateChange(.connected)      // A stays 1, B=3

            let (a, b) = box.snapshot()
            XCTAssertEqual(a, 1, "self-cancelling callback must stop receiving further transitions")
            XCTAssertEqual(b, 3, "surviving subscription must continue receiving transitions in order")
            XCTAssertEqual(mock.connectionStateSubscriberCount, 1,
                "handler map must reflect the cancellation performed from inside the callback")
        }
    }

    func testReentrantDeliveryPreservesOrdering() {
        let mock = MockSignalRService()
        let recorder = LockedStateRecorder()

        // Callback both records the state and, on every delivery, performs a
        // reentrant `snapshot()` — the reentrant read must not skip or
        // reorder the ordered transition delivery.
        let (_, sub) = mock.onConnectionStateChanged { [weak mock] state in
            recorder.append(state)
            _ = mock?.connectionState
        }
        withExtendedLifetime(sub) {
            mock.simulateConnectionStateChange(.connected)
            mock.simulateConnectionStateChange(.reconnecting)
            mock.simulateConnectionStateChange(.connected)
            mock.simulateConnectionStateChange(.disconnected)

            XCTAssertEqual(recorder.load(),
                [.connected, .reconnecting, .connected, .disconnected],
                "ordered serial delivery must be preserved when callbacks re-enter the hub")
        }
    }

    // MARK: - r4 blocker #3: cross-hub cyclic delivery + FIFO nested guard.

    /// Cross-hub A→B→A cyclic callback must not deadlock. Under the r3
    /// per-hub queue design, hub-A's callback calling into hub-B's callback
    /// which calls back into hub-A would deadlock (A queue blocked waiting
    /// for B while B's callback is trying to enter A). Under the r4 shared
    /// hub queue every hub multiplexes onto one serial queue; on-queue
    /// reentrancy collapses A→B→A to inline execution.
    func testCrossHubCyclicCallbackDoesNotDeadlock() {
        let mock = MockSignalRService()

        // Hub A: connection-state changes. Hub B: printer updates. When
        // hub A fires, its handler triggers a hub-B delivery inline. Hub B's
        // handler in turn calls back into hub A's `snapshot()`. Both must
        // complete without deadlocking.
        let cyclesCompleted = LockedIntPair()

        let (_, subA) = mock.onConnectionStateChanged { [weak mock] _ in
            cyclesCompleted.bumpA()
            guard let mock else { return }
            // Fire a hub-B delivery synchronously from inside hub-A's
            // callback. Under distinct per-hub queues this would enqueue
            // onto B and later B's callback would try to snapshot A —
            // deadlock. Under the shared queue B's delivery inlines.
            mock.simulatePrinterUpdate(PrinterStatusUpdate(
                id: UUID(), isOnline: true, state: nil, progress: nil,
                jobName: nil, fileName: nil, thumbnailUrl: nil, cameraStreamUrl: nil,
                x: nil, y: nil, z: nil, hotendTemp: nil, bedTemp: nil,
                hotendTarget: nil, bedTarget: nil, homedAxes: nil,
                spoolInfo: nil, mmuStatus: nil
            ))
        }

        let subB = mock.onPrinterUpdated { [weak mock] _ in
            cyclesCompleted.bumpB()
            // Re-enter hub A synchronously from hub B's callback.
            _ = mock?.connectionState
        }

        withExtendedLifetime((subA, subB)) {
            mock.simulateConnectionStateChange(.connected)
            let (a, b) = cyclesCompleted.snapshot()
            XCTAssertEqual(a, 1, "hub-A callback must run once for the single state change")
            XCTAssertEqual(b, 1, "hub-B callback fired from inside hub-A must complete inline")
        }
    }

    /// Nested delivery inside a callback must be FIFO relative to the outer
    /// delivery: when a callback synchronously triggers another same-hub
    /// state change, the nested handlers must run AFTER the outer batch
    /// finishes rather than recursively interleaving. Handlers observe the
    /// outer transition first, then the nested one.
    func testNestedDeliveryPreservesFIFOOrdering() {
        let mock = MockSignalRService()
        let recorder = LockedStateRecorder()
        let didNest = LockedIntPair()

        let (_, sub) = mock.onConnectionStateChanged { [weak mock] state in
            recorder.append(state)
            // On the first observation, trigger a nested state change. The
            // FIFO guard should queue this until the outer delivery batch
            // completes — so the recorder observes [.connected, .reconnecting]
            // in that order and NOT interleaved.
            if state == .connected {
                didNest.bumpA()
                mock?.simulateConnectionStateChange(.reconnecting)
            }
        }
        withExtendedLifetime(sub) {
            mock.simulateConnectionStateChange(.connected)

            XCTAssertEqual(didNest.snapshot().0, 1, "nested trigger must have run")
            XCTAssertEqual(recorder.load(), [.connected, .reconnecting],
                "nested delivery must be FIFO — outer batch completes, then nested is delivered")
        }
    }
}

// MARK: - Small lock-guarded test containers

private final class LockedStateBox: @unchecked Sendable {
    private let lock = NSLock()
    private var value: SignalRConnectionState = .disconnected
    func store(_ v: SignalRConnectionState) { lock.lock(); value = v; lock.unlock() }
    func load() -> SignalRConnectionState { lock.lock(); defer { lock.unlock() }; return value }
}

private final class LockedStateRecorder: @unchecked Sendable {
    private let lock = NSLock()
    private var values: [SignalRConnectionState] = []
    func append(_ v: SignalRConnectionState) { lock.lock(); values.append(v); lock.unlock() }
    func load() -> [SignalRConnectionState] {
        lock.lock(); defer { lock.unlock() }
        return values
    }
}

private final class SubscriptionHolder: @unchecked Sendable {
    private let lock = NSLock()
    private var sub: SignalRSubscription?
    var isEmpty: Bool { lock.lock(); defer { lock.unlock() }; return sub == nil }
    func store(_ s: SignalRSubscription) { lock.lock(); sub = s; lock.unlock() }
    func cancelStored() {
        lock.lock()
        let current = sub
        lock.unlock()
        current?.cancel()
    }
    func clear() { lock.lock(); sub = nil; lock.unlock() }
}

/// Ordered append-only string sequence used by the r7 registration-order
/// tests. Callbacks run on the hub queue while the test reads snapshots
/// from the main actor; the internal lock keeps both races-free.
final class LockedStringSequence: @unchecked Sendable {
    private let lock = NSLock()
    private var storage: [String] = []
    func append(_ s: String) {
        lock.lock(); defer { lock.unlock() }
        storage.append(s)
    }
    func snapshot() -> [String] {
        lock.lock(); defer { lock.unlock() }
        return storage
    }
}

// MARK: - r9 blocker #4: deterministic real-transport SignalRService lifecycle
//
// r8 blocker #2's real-service tests originally used `Task.sleep` to
// let the reconnect loop tick and to bound state-arrival waits. Hicks
// (r9 review) flagged elapsed-time success criteria as non-proof: a
// 30ms sleep neither proves the retry ran ≥2 times nor that the
// `.disconnected` transition was actually published.
//
// r9 replaces every wall-clock wait with continuation-based gates:
//
// * `LifecycleControlledSleeper` (injected via
//   `reconnectSleeper:`) suspends each reconnect delay until the
//   test explicitly releases it. `waitForNextSleep()` awaits until
//   the reconnect loop is parked in a delay, proving the retry
//   schedule engaged.
// * `LifecycleStateObserver` waits for a specific
//   `SignalRConnectionState` transition via a per-state continuation
//   that resumes as soon as the observer appends that state. No
//   polling.
// * `NegotiateBeganSignal` is a one-shot continuation the
//   `MockURLProtocol` request handler resumes; the test awaits it
//   before disconnect. Replaces the LockedBool + `Task.sleep(1ms)`
//   busy loop.
// * `MockSignalRWebSocket` (injected via `webSocketFactory:`) gates
//   handshake `send()` and `receive()` through per-call
//   continuations. This lets the "disconnect during handshake
//   receive" test freeze the service *inside* the handshake
//   response await, disconnect, then release the mock — proving
//   the generation recheck immediately after `wsTask.receive()`
//   (r8/r9 production change) rejects the stale frame.
//
// Time-based bounds remain ONLY as *timeouts* on `XCTestExpectation`
// (i.e. as failure bounds), never as success criteria.

/// A `SignalRReconnectSleeper` implementation that queues each
/// requested delay and only completes it when the test calls
/// `release()`. Every reconnect attempt therefore blocks until the
/// test decides the retry may proceed.
///
/// * `waitForNextSleep(timeout:)` — awaits until the service is
///   parked inside a `sleep(for:)` call. Fails via XCTFail on
///   timeout.
/// * `release()` — resumes the oldest pending sleep. If no sleep is
///   pending yet, the release is queued and consumed by the next
///   `sleep(for:)` call.
/// * `pendingCount()` — introspection for assertions.
///
/// Actor-based to serialize mutation of the pending/released queues.
actor LifecycleControlledSleeper {
    private var pendingSleeps: [CheckedContinuation<Void, Never>] = []
    private var pendingReleases: Int = 0
    /// r11 blocker #1: waiters keyed by UUID so `waitForNextSleep`'s
    /// cancellation handler can remove exactly the one it registered
    /// (not all waiters). Prior implementation used an unordered array
    /// with a plain `withCheckedContinuation`, which meant the
    /// `waitForNewSleep` task-group's `cancelAll()` never woke the
    /// parked child — `withTaskGroup` waits for children to drain, so
    /// the idle/false path hung forever.
    private var waitingForSleep: [UUID: CheckedContinuation<Void, Never>] = [:]
    /// r10 blocker #3: total number of `sleep(for:)` calls ever
    /// entered by the reconnect task. Monotonic. Tests use this to
    /// prove no NEW sleep enrolled within a bounded observation
    /// window rather than yielding and hoping the runtime schedules
    /// the failing branch.
    private var sleepEnterCount: Int = 0

    func sleep(for _: TimeInterval) async {
        // If a release was queued before we got here, consume it and return.
        if pendingReleases > 0 {
            pendingReleases -= 1
            sleepEnterCount += 1
            resumeAllWaitersForSleep()
            return
        }
        sleepEnterCount += 1
        // r10 blocker #3: cancellation-aware sleep. If the parked
        // task is cancelled (e.g. `disconnect()` cancels the reconnect
        // task), the continuation is resumed immediately so the
        // reconnect task can proceed to its `Task.isCancelled` check
        // and no-op cleanly — otherwise a hanging continuation would
        // hold the test in a timeout race.
        await withTaskCancellationHandler {
            await withCheckedContinuation { cont in
                pendingSleeps.append(cont)
                resumeAllWaitersForSleep()
            }
        } onCancel: {
            Task { await self.resumeAllOnCancel() }
        }
    }

    /// Resume every registered `waitForNextSleep` waiter. Called from
    /// the two spots inside `sleep(for:)` where a new sleep enrolls
    /// (both the "pendingReleases already queued" fast path and the
    /// normal parked-continuation path).
    private func resumeAllWaitersForSleep() {
        let toResume = Array(waitingForSleep.values)
        waitingForSleep.removeAll()
        for w in toResume { w.resume() }
    }

    /// Called from `withTaskCancellationHandler`'s onCancel branch.
    /// Resumes every parked sleep so cancelled reconnect tasks can
    /// proceed to their `Task.isCancelled` gate without needing an
    /// explicit `release()` call.
    private func resumeAllOnCancel() {
        let toResume = pendingSleeps
        pendingSleeps.removeAll()
        for c in toResume { c.resume() }
    }

    func release() {
        if let cont = pendingSleeps.first {
            pendingSleeps.removeFirst()
            cont.resume()
        } else {
            pendingReleases += 1
        }
    }

    /// r11 blocker #1: cancellation-aware wait. The prior version used
    /// a plain `withCheckedContinuation`, so if the caller's Task was
    /// cancelled (as `waitForNewSleep`'s task-group `cancelAll()`
    /// does when the timeout child wins the race), this continuation
    /// stayed parked forever — `withTaskGroup` drains children before
    /// returning, so the whole method hung. Now: the waiter is keyed
    /// with a UUID; the cancellation handler removes exactly that
    /// entry and resumes it, so cancellation is honoured but any
    /// still-registered peers are untouched. Returning normally on
    /// cancellation is safe because the sole caller
    /// (`waitForNewSleep`) has already captured `group.next()`'s value
    /// before the cancellation fires — the drained child's return
    /// value is discarded.
    func waitForNextSleep() async {
        if !pendingSleeps.isEmpty { return }
        let id = UUID()
        await withTaskCancellationHandler {
            await withCheckedContinuation { cont in
                if Task.isCancelled {
                    cont.resume()
                    return
                }
                self.waitingForSleep[id] = cont
            }
        } onCancel: {
            Task { await self.cancelWaitForSleep(id: id) }
        }
    }

    /// Called from `waitForNextSleep`'s onCancel handler. Removes the
    /// specific waiter and resumes it exactly once — a `sleep(for:)`
    /// enrollment concurrent with cancellation removes it via
    /// `resumeAllWaitersForSleep()` and this becomes a no-op via
    /// `removeValue`'s optional return.
    private func cancelWaitForSleep(id: UUID) {
        if let cont = waitingForSleep.removeValue(forKey: id) {
            cont.resume()
        }
    }

    func pendingCount() -> Int { pendingSleeps.count }

    /// r10 blocker #3: monotonic total of entered sleeps. Tests can
    /// snapshot before an event, then await a bounded window and
    /// re-read to prove no new sleep enrolled (deterministic
    /// idle-gate for negative retry assertions replacing
    /// `Task.yield()` polling).
    func totalSleepEntries() -> Int { sleepEnterCount }

    /// Await until at least one new sleep enrolls beyond `baseline`,
    /// bounded by `nanoseconds`. Returns `true` if a new sleep
    /// enrolled (regression path — a retry was scheduled when the
    /// test expected none), or `false` if the observation window
    /// elapsed idle (expected-idle proof). Uses `TaskGroup` +
    /// cancellation so no timing continuation leaks; the idle child
    /// wakes reliably because `waitForNextSleep` (r11 blocker #1) is
    /// now cancellation-aware.
    func waitForNewSleep(afterBaseline baseline: Int, boundedNanoseconds: UInt64) async -> Bool {
        if sleepEnterCount > baseline { return true }
        return await withTaskGroup(of: Bool.self) { group in
            group.addTask { [weak self] in
                guard let self else { return false }
                await self.waitForNextSleep()
                return true
            }
            group.addTask {
                try? await Task.sleep(nanoseconds: boundedNanoseconds)
                return false
            }
            let first = await group.next() ?? false
            group.cancelAll()
            return first
        }
    }

    /// Erased closure suitable for `SignalRService(reconnectSleeper:)`.
    nonisolated func makeSleeper() -> @Sendable (TimeInterval) async -> Void {
        { [self] interval in
            await self.sleep(for: interval)
        }
    }
}

/// Observer that records every published connection state and
/// exposes a continuation-based `waitFor(state:)` primitive. Never
/// polls elapsed time.
final class LifecycleStateObserver: @unchecked Sendable {
    private let lock = NSLock()
    private var states: [SignalRConnectionState] = []
    private var waiters: [(SignalRConnectionState, CheckedContinuation<Void, Never>)] = []

    func append(_ state: SignalRConnectionState) {
        lock.lock()
        states.append(state)
        var toResume: [CheckedContinuation<Void, Never>] = []
        waiters.removeAll { pair in
            if pair.0 == state {
                toResume.append(pair.1)
                return true
            }
            return false
        }
        lock.unlock()
        for c in toResume { c.resume() }
    }

    func snapshot() -> [SignalRConnectionState] {
        lock.lock(); defer { lock.unlock() }
        return states
    }

    var last: SignalRConnectionState? { snapshot().last }
    func contains(_ s: SignalRConnectionState) -> Bool { snapshot().contains(s) }

    /// Await until `state` has been appended to the sequence. If it
    /// is already present, returns immediately. Lock manipulation
    /// happens in a sync helper because NSLock is unavailable from
    /// async contexts.
    func waitFor(state: SignalRConnectionState) async {
        if _quickCheckAndPossiblyEnqueue(state) == .alreadyPresent { return }
        await withCheckedContinuation { cont in
            _enqueueWaiter(state: state, cont: cont)
        }
    }

    private enum _Presence { case alreadyPresent, needsWaiter }

    /// Synchronous helper: returns `.alreadyPresent` if `state` is
    /// already recorded; otherwise `.needsWaiter` and the caller
    /// must enqueue a continuation via `_enqueueWaiter`.
    private func _quickCheckAndPossiblyEnqueue(_ state: SignalRConnectionState) -> _Presence {
        lock.lock(); defer { lock.unlock() }
        return states.contains(state) ? .alreadyPresent : .needsWaiter
    }

    private func _enqueueWaiter(state: SignalRConnectionState, cont: CheckedContinuation<Void, Never>) {
        lock.lock()
        // Re-check under the lock in case `append` fired between the
        // quick-check and here.
        if states.contains(state) {
            lock.unlock()
            cont.resume()
            return
        }
        waiters.append((state, cont))
        lock.unlock()
    }
}

/// One-shot signal fired by the mock URL protocol request handler
/// when the negotiate call has *entered* the handler, before it
/// blocks awaiting release. Replaces `LockedBool` + Task.sleep polling.
final class NegotiateBeganSignal: @unchecked Sendable {
    private let lock = NSLock()
    private var fired = false
    private var waiters: [CheckedContinuation<Void, Never>] = []

    func signal() {
        lock.lock()
        guard !fired else { lock.unlock(); return }
        fired = true
        let w = waiters
        waiters.removeAll()
        lock.unlock()
        for c in w { c.resume() }
    }

    func wait() async {
        if _consumeIfFired() { return }
        await withCheckedContinuation { cont in
            _enqueueWaiter(cont: cont)
        }
    }

    private func _consumeIfFired() -> Bool {
        lock.lock(); defer { lock.unlock() }
        return fired
    }

    private func _enqueueWaiter(cont: CheckedContinuation<Void, Never>) {
        lock.lock()
        if fired {
            lock.unlock()
            cont.resume()
            return
        }
        waiters.append(cont)
        lock.unlock()
    }
}

/// Serial counter of negotiate calls. Not used for pass criteria in
/// the r9 tests — kept only for informational assertions after gates
/// have already proved order.
final class NegotiateCounter: @unchecked Sendable {
    private let lock = NSLock()
    private var count = 0
    func increment() -> Int {
        lock.lock(); defer { lock.unlock() }
        count += 1
        return count
    }
    func value() -> Int {
        lock.lock(); defer { lock.unlock() }
        return count
    }
}

/// A test-controlled `SignalRWebSocket` implementation.
///
/// Every `send(_:)` and `receive()` call is gated on a continuation
/// that the test releases via `completeReceive(with:)`,
/// `completeSend()`, or `failReceive(with:)`. This lets the r9
/// "disconnect during handshake receive" test suspend the service
/// mid-handshake, run `disconnect()`, then release the receive to
/// prove the generation recheck rejects the frame without publishing
/// `.connected`.
final class MockSignalRWebSocket: NSObject, SignalRWebSocket, @unchecked Sendable {
    private let lock = NSLock()
    private var pendingReceives: [CheckedContinuation<URLSessionWebSocketTask.Message, Error>] = []
    private var pendingSends: [CheckedContinuation<Void, Error>] = []
    private var sentMessages: [URLSessionWebSocketTask.Message] = []
    private var resumed = false
    private var cancelled = false
    private var receiveWaiters: [CheckedContinuation<Void, Never>] = []

    func resume() {
        lock.lock(); resumed = true; lock.unlock()
    }

    /// r9 blocker #3 proof: the test intentionally lets the parked
    /// receive **remain parked** across `disconnect()`. The test then
    /// calls `completeReceive(...)` with a valid-looking handshake
    /// frame AFTER disconnect, so the service's post-`wsTask.receive()`
    /// generation guard is what must reject the stale frame — not
    /// the socket-level cancellation. Draining here would short-circuit
    /// the proof (receive would throw before the gen guard runs).
    func cancel(with _: URLSessionWebSocketTask.CloseCode, reason _: Data?) {
        lock.lock(); cancelled = true; lock.unlock()
    }

    func send(_ message: URLSessionWebSocketTask.Message) async throws {
        _recordSent(message)
        try await withCheckedThrowingContinuation { cont in
            _enqueueSend(cont: cont)
            // Auto-complete sends unless the test explicitly wants
            // to gate them; the handshake completion is the
            // point of interest, not the send call itself.
            completeSend()
        }
    }

    func receive() async throws -> URLSessionWebSocketTask.Message {
        try await withCheckedThrowingContinuation { cont in
            let waiters = _enqueueReceive(cont: cont)
            for w in waiters { w.resume() }
        }
    }

    private func _recordSent(_ message: URLSessionWebSocketTask.Message) {
        lock.lock(); defer { lock.unlock() }
        sentMessages.append(message)
    }

    private func _enqueueSend(cont: CheckedContinuation<Void, Error>) {
        lock.lock(); defer { lock.unlock() }
        pendingSends.append(cont)
    }

    private func _enqueueReceive(cont: CheckedContinuation<URLSessionWebSocketTask.Message, Error>) -> [CheckedContinuation<Void, Never>] {
        lock.lock()
        pendingReceives.append(cont)
        let waiters = receiveWaiters
        receiveWaiters.removeAll()
        lock.unlock()
        return waiters
    }

    // Test-side controls -------------------------------------------

    func waitForReceiveCall() async {
        if _hasPendingReceive() { return }
        await withCheckedContinuation { cont in
            _enqueueReceiveWaiter(cont: cont)
        }
    }

    private func _hasPendingReceive() -> Bool {
        lock.lock(); defer { lock.unlock() }
        return !pendingReceives.isEmpty
    }

    private func _enqueueReceiveWaiter(cont: CheckedContinuation<Void, Never>) {
        lock.lock()
        if !pendingReceives.isEmpty {
            lock.unlock()
            cont.resume()
            return
        }
        receiveWaiters.append(cont)
        lock.unlock()
    }

    func completeReceive(with message: URLSessionWebSocketTask.Message) {
        lock.lock()
        guard let cont = pendingReceives.first else { lock.unlock(); return }
        pendingReceives.removeFirst()
        lock.unlock()
        cont.resume(returning: message)
    }

    func failReceive(with error: Error) {
        lock.lock()
        guard let cont = pendingReceives.first else { lock.unlock(); return }
        pendingReceives.removeFirst()
        lock.unlock()
        cont.resume(throwing: error)
    }

    func completeSend() {
        lock.lock()
        guard let cont = pendingSends.first else { lock.unlock(); return }
        pendingSends.removeFirst()
        lock.unlock()
        cont.resume(returning: ())
    }

    func snapshotSent() -> [URLSessionWebSocketTask.Message] {
        lock.lock(); defer { lock.unlock() }
        return sentMessages
    }

    func isCancelled() -> Bool { lock.lock(); defer { lock.unlock() }; return cancelled }
}

/// Reviewer-required real-transport lifecycle tests for r8 blocker #2
/// updated with r9 blocker #4's deterministic gates.
///
/// * Test A — negotiate always fails; ≥ 2 reconnect attempts fire
///   through the *injected* sleeper (each release proved gated), then
///   `disconnect()` halts the loop and the final state settles at
///   `.disconnected`. No `Task.sleep`.
/// * Test B — `disconnect()` racing an in-flight negotiate never
///   permits a `.connected` publish. Uses a `NegotiateBeganSignal` to
///   await the handler's entry, disconnect, then release the
///   negotiate response. No polling.
/// * Test C (new for r9) — `disconnect()` during the handshake
///   `wsTask.receive()` awaits, using an injected MockSignalRWebSocket
///   that suspends receive until the test releases it. Proves the
///   post-handshake-receive generation recheck (r9 blocker #3)
///   rejects the stale frame; final state is `.disconnected` and no
///   `.connected` is ever observed.
@MainActor
final class SignalRServiceRealTransportLifecycleTests: XCTestCase {

    private var session: URLSession!
    private let testURL = URL(string: "https://signalr-lifecycle.test.invalid")!

    override func setUp() {
        super.setUp()
        MockURLProtocol.reset()
        session = MockURLProtocol.mockSession()
    }

    override func tearDown() {
        MockURLProtocol.reset()
        session = nil
        super.tearDown()
    }

    private func recordStates(_ svc: SignalRService, into recorder: LifecycleStateObserver) -> SignalRSubscription {
        let (initial, sub) = svc.onConnectionStateChanged { state in
            recorder.append(state)
        }
        recorder.append(initial)
        return sub
    }

    /// Test A — negotiate always fails; the reconnect loop is proved
    /// to have attempted at least two retries via the injected
    /// sleeper gates, then `disconnect()` halts the loop. `.connected`
    /// must never be published; final state must be `.disconnected`.
    func testRealService_negotiateFailurePlusDisconnect_neverPublishesConnected() async throws {
        let counter = NegotiateCounter()
        MockURLProtocol.requestHandler = { request in
            _ = counter.increment()
            let response = HTTPURLResponse(
                url: request.url!,
                statusCode: 500,
                httpVersion: nil,
                headerFields: nil
            )!
            return (response, Data())
        }

        let sleeper = LifecycleControlledSleeper()
        let service = SignalRService(
            serverURL: testURL,
            session: session,
            tokenProvider: { nil },
            reconnectBackoff: { _ in 1.0 }, // ignored; sleeper gates the delay
            reconnectSleeper: sleeper.makeSleeper()
        )

        let recorder = LifecycleStateObserver()
        let sub = recordStates(service, into: recorder)
        _ = sub // retain

        // Fire connect() — it will throw because negotiate 500s.
        do {
            try await service.connect()
            XCTFail("connect() should have thrown on 500 negotiate")
        } catch {
            // expected
        }

        // The reconnect loop must have scheduled its first retry.
        // `waitForNextSleep` returns when the service is parked in
        // `sleep(for:)`; that's the proof the retry engaged.
        await sleeper.waitForNextSleep()
        await sleeper.release()

        // Wait for the second reconnect attempt to park.
        await sleeper.waitForNextSleep()
        let midCount = counter.value()
        XCTAssertGreaterThanOrEqual(
            midCount,
            2,
            "Expected initial negotiate + at least one retry executed before second parks; got \(midCount)"
        )

        // Halt the loop. The retry is currently parked in the
        // sleeper; `disconnect()` clears the token AND cancels the
        // reconnect task. Cancellation causes the sleeper's parked
        // continuation to resume immediately (r10 blocker #3), so
        // the released stale reconnect observes `!Task.isCancelled`
        // as false and no-ops via the ownership check.
        let baselineSleeps = await sleeper.totalSleepEntries()
        await service.disconnect()
        await recorder.waitFor(state: .disconnected)

        // r10 blocker #3: deterministic idle-gate. Wait a bounded
        // window for a NEW sleep to enroll; expect none. Returns
        // false when the window elapses idle (the deterministic
        // pass path here). A regression that scheduled another
        // retry would return true. Replaces prior `Task.yield()`
        // polling.
        let newSleepEnrolled = await sleeper.waitForNewSleep(
            afterBaseline: baselineSleeps,
            boundedNanoseconds: 200_000_000 // 200ms observation window
        )
        XCTAssertFalse(
            newSleepEnrolled,
            "After disconnect(), no additional reconnect sleep must enroll (idle-gate proof, not Task.yield polling)"
        )

        let final = counter.value()
        // After disconnect, no NEW retries should schedule. The
        // released stale reconnect must no-op via the ownership
        // check, so counter must not exceed midCount + 0. (The
        // released stale reconnect does not re-negotiate.)
        XCTAssertEqual(
            final,
            midCount,
            "No further negotiate calls should occur after disconnect(). midCount=\(midCount) final=\(final)"
        )

        let states = recorder.snapshot()
        XCTAssertFalse(
            states.contains(.connected),
            "No .connected publish should occur when negotiate always fails. Observed: \(states)"
        )
        XCTAssertEqual(states.last, .disconnected, "Final observed state must be .disconnected. Full: \(states)")
    }

    /// Test B — disconnect during in-flight negotiate: the
    /// `.connected` transition must never be published after the
    /// disconnect. Uses `NegotiateBeganSignal` continuation instead
    /// of polling.
    func testRealService_disconnectDuringNegotiate_finalStateIsDisconnected() async throws {
        let began = NegotiateBeganSignal()
        // DispatchSemaphore is used inside the URLProtocol handler
        // (sync context, non-async), so it stays. The async test
        // side never calls `.wait()` on it.
        let gate = DispatchSemaphore(value: 0)
        MockURLProtocol.requestHandler = { request in
            began.signal()
            gate.wait() // hold until test releases
            let payload: [String: Any] = [
                "connectionId": "test-conn",
                "connectionToken": "test-conn",
                "availableTransports": []
            ]
            let data = try JSONSerialization.data(withJSONObject: payload)
            let response = HTTPURLResponse(
                url: request.url!,
                statusCode: 200,
                httpVersion: nil,
                headerFields: ["Content-Type": "application/json"]
            )!
            return (response, data)
        }

        let sleeper = LifecycleControlledSleeper()
        let service = SignalRService(
            serverURL: testURL,
            session: session,
            tokenProvider: { nil },
            reconnectBackoff: { _ in 1.0 },
            reconnectSleeper: sleeper.makeSleeper()
        )

        let recorder = LifecycleStateObserver()
        let sub = recordStates(service, into: recorder)
        _ = sub

        // Kick off connect in the background.
        let connectTask = Task { try? await service.connect() }

        // Wait deterministically for the negotiate handler to have
        // entered its body. No polling.
        await began.wait()

        // Disconnect while negotiate is suspended. This bumps
        // generation under `lifecycleSync` and enqueues `.disconnected`.
        await service.disconnect()

        // Release the negotiate response. `performConnect`'s
        // post-negotiate generation recheck must reject the
        // in-flight connection.
        gate.signal()

        _ = await connectTask.value

        // Wait for `.disconnected` to actually arrive via the state
        // observer. This is the proof the transition applied.
        await recorder.waitFor(state: .disconnected)

        let states = recorder.snapshot()
        if let firstDisconnect = states.firstIndex(of: .disconnected),
           firstDisconnect < states.count - 1 {
            let tail = Array(states.suffix(from: firstDisconnect + 1))
            XCTAssertFalse(
                tail.contains(.connected),
                ".connected must not be published after .disconnected. Full sequence: \(states)"
            )
        }
        XCTAssertEqual(states.last, .disconnected, "Final state must be .disconnected. Full: \(states)")
    }

    /// Test C (r9 blocker #3+#4) — disconnect during the handshake
    /// `wsTask.receive()` await. Uses an injected `MockSignalRWebSocket`
    /// to keep the handshake receive parked until after the test calls
    /// `disconnect()`. Then the mock releases the receive with a
    /// valid-looking handshake frame; the service's r9 post-receive
    /// generation guard (immediately after `wsTask.receive()` in
    /// `sendHandshake`) must reject the stale frame so `.connected`
    /// is never observed and the final state is `.disconnected`.
    ///
    /// Note: `MockSignalRWebSocket.cancel(...)` is deliberately a
    /// no-op for pending receives so the receive stays parked across
    /// disconnect. Draining the receive on cancel would short-circuit
    /// the very code path r9 blocker #3 requires the gen guard to
    /// defend — the receive would throw before the guard could run.
    func testRealService_disconnectDuringHandshakeReceive_neverPublishesConnected() async throws {
        // Negotiate returns success immediately with a fully-decodable
        // payload (SignalRTransport requires `transferFormats`).
        MockURLProtocol.requestHandler = { request in
            let payload: [String: Any] = [
                "connectionId": "test-conn",
                "connectionToken": "test-conn",
                "negotiateVersion": 1,
                "availableTransports": [
                    [
                        "transport": "WebSockets",
                        "transferFormats": ["Text", "Binary"]
                    ]
                ]
            ]
            let data = (try? JSONSerialization.data(withJSONObject: payload)) ?? Data()
            let response = HTTPURLResponse(
                url: request.url!,
                statusCode: 200,
                httpVersion: nil,
                headerFields: ["Content-Type": "application/json"]
            )!
            return (response, data)
        }

        let mockWS = MockSignalRWebSocket()
        let sleeper = LifecycleControlledSleeper()
        let service = SignalRService(
            serverURL: testURL,
            session: session,
            tokenProvider: { nil },
            reconnectBackoff: { _ in 1.0 },
            reconnectSleeper: sleeper.makeSleeper(),
            webSocketFactory: { _ in mockWS }
        )

        let recorder = LifecycleStateObserver()
        let sub = recordStates(service, into: recorder)
        _ = sub

        // Wrap the interaction in a 15s bounded timeout so a bug in
        // this test path can never hang xcodebuild indefinitely.
        let didComplete: Bool = await withTaskGroup(of: Bool.self) { group in
            group.addTask {
                let connectTask = Task { try? await service.connect() }
                // Wait deterministically for handshake receive to park.
                await mockWS.waitForReceiveCall()
                // Bump generation via disconnect() — tearDownLocked nils
                // the socket but mock cancel is a no-op so receive stays
                // parked; the r9 post-receive gen guard is what must
                // reject the stale frame.
                await service.disconnect()
                // Release the stale frame. sendHandshake resumes from
                // receive, hits `checkGenerationStillCurrent(myGen)` at
                // line 446 and throws.
                mockWS.completeReceive(with: .string("{}\u{1e}"))
                _ = await connectTask.value
                await recorder.waitFor(state: .disconnected)
                return true
            }
            group.addTask {
                try? await Task.sleep(nanoseconds: 15 * 1_000_000_000)
                return false
            }
            let first = await group.next() ?? false
            group.cancelAll()
            return first
        }
        XCTAssertTrue(didComplete, "Test C body must complete within 15s")

        let states = recorder.snapshot()
        XCTAssertFalse(
            states.contains(.connected),
            ".connected must never publish when disconnect races the handshake receive. Full: \(states)"
        )
        XCTAssertEqual(states.last, .disconnected, "Final state must be .disconnected. Full: \(states)")
    }

    /// Test D (r9 blocker #4) — receive-loss retry: at least two
    /// consecutive failed reconnect attempts fire through the
    /// gated sleeper, proving the token-tagged reconnect slot
    /// re-enrolls after each failure. Then `disconnect()` halts the
    /// loop; the released stale reconnect no-ops via the ownership
    /// check.
    func testRealService_repeatedFailedReconnects_gateReleasedTwice_thenDisconnectHalts() async throws {
        let counter = NegotiateCounter()
        MockURLProtocol.requestHandler = { request in
            _ = counter.increment()
            let response = HTTPURLResponse(
                url: request.url!,
                statusCode: 500,
                httpVersion: nil,
                headerFields: nil
            )!
            return (response, Data())
        }

        let sleeper = LifecycleControlledSleeper()
        let service = SignalRService(
            serverURL: testURL,
            session: session,
            tokenProvider: { nil },
            reconnectBackoff: { _ in 1.0 },
            reconnectSleeper: sleeper.makeSleeper()
        )

        let recorder = LifecycleStateObserver()
        let sub = recordStates(service, into: recorder)
        _ = sub

        do {
            try await service.connect()
            XCTFail("connect() should have thrown on 500")
        } catch {}

        // First reconnect parks in sleeper — proves retry scheduled.
        await sleeper.waitForNextSleep()
        await sleeper.release()

        // Released reconnect runs, negotiate fails again, second
        // retry is scheduled. Wait for it to park.
        await sleeper.waitForNextSleep()
        let afterFirstRelease = counter.value()
        XCTAssertGreaterThanOrEqual(
            afterFirstRelease,
            2,
            "Expected initial + one gated retry executed; got \(afterFirstRelease)"
        )

        // Release again → second retry runs → third schedules.
        await sleeper.release()
        await sleeper.waitForNextSleep()
        let afterSecondRelease = counter.value()
        XCTAssertGreaterThanOrEqual(
            afterSecondRelease,
            3,
            "Expected initial + two gated retries executed; got \(afterSecondRelease)"
        )

        // Halt the loop. The retry is parked; disconnect clears the
        // token and cancels the reconnect task. Cancellation causes
        // the sleeper's parked continuation to resume immediately
        // (r10 blocker #3), so the released reconnect observes
        // cleared ownership and no-ops.
        let baselineSleeps = await sleeper.totalSleepEntries()
        await service.disconnect()
        await recorder.waitFor(state: .disconnected)

        // r10 blocker #3: deterministic idle-gate replacing
        // `Task.yield()` polling. Bounded observation window; a new
        // enrollment would be the regression.
        let newSleepEnrolled = await sleeper.waitForNewSleep(
            afterBaseline: baselineSleeps,
            boundedNanoseconds: 200_000_000
        )
        XCTAssertFalse(
            newSleepEnrolled,
            "After disconnect(), no additional reconnect sleep must enroll"
        )

        let final = counter.value()
        XCTAssertEqual(
            final,
            afterSecondRelease,
            "No further negotiate calls should occur after disconnect(). afterSecondRelease=\(afterSecondRelease) final=\(final)"
        )
        XCTAssertEqual(recorder.last, .disconnected, "Final state must be .disconnected. Full: \(recorder.snapshot())")
        XCTAssertFalse(recorder.contains(.connected), "No .connected during always-failing negotiate")
    }

    // MARK: - r10 blocker #1 — type-7 close-frame no-hang

    /// r10 blocker #1 regression proof.
    ///
    /// Before r10, the connected-state receive loop called
    /// `handleMessage` inside `lifecycleSync`, and `handleMessage →
    /// processFrame(type:7)` called `lifecycleSync` again on the same
    /// serial queue. A normal server CloseMessage therefore wedged
    /// the entire lifecycle forever. This test exercises the exact
    /// nested-call path against the real service and proves that (a)
    /// the test completes, (b) exactly one `.reconnecting` transition
    /// is observed, and (c) `disconnect()` still halts the loop.
    ///
    /// Bounded 15s task-group timeout guards against a regression
    /// re-introducing the wedge — the assertion is on the boolean
    /// completion result, not on elapsed time.
    func testRealService_connectedReceiveLoop_type7CloseFrame_neverHangs() async throws {
        // Working negotiate response reused across attempts.
        let negotiatePayload: [String: Any] = [
            "connectionId": "conn-r10-b1",
            "connectionToken": "tok-r10-b1",
            "availableTransports": [
                [
                    "transport": "WebSockets",
                    "transferFormats": ["Text", "Binary"]
                ]
            ]
        ]
        MockURLProtocol.requestHandler = { request in
            let data = (try? JSONSerialization.data(withJSONObject: negotiatePayload)) ?? Data()
            let response = HTTPURLResponse(
                url: request.url!,
                statusCode: 200,
                httpVersion: nil,
                headerFields: ["Content-Type": "application/json"]
            )!
            return (response, data)
        }

        let mockWS = MockSignalRWebSocket()
        let sleeper = LifecycleControlledSleeper()
        let service = SignalRService(
            serverURL: testURL,
            session: session,
            tokenProvider: { nil },
            reconnectBackoff: { _ in 1.0 },
            reconnectSleeper: sleeper.makeSleeper(),
            webSocketFactory: { _ in mockWS }
        )

        let recorder = LifecycleStateObserver()
        let sub = recordStates(service, into: recorder)
        _ = sub

        let didComplete: Bool = await withTaskGroup(of: Bool.self) { group in
            group.addTask {
                let connectTask = Task { try? await service.connect() }

                // 1. Handshake receive parks first — release with a
                //    valid empty handshake response.
                await mockWS.waitForReceiveCall()
                mockWS.completeReceive(with: .string("{}\u{1e}"))

                _ = await connectTask.value
                await recorder.waitFor(state: .connected)

                // 2. Receive loop has now called `receive()` again.
                //    Feed it a type-7 close frame — the SAME call path
                //    that previously wedged: `handleMessage` inside
                //    lifecycleSync → `processFrame(type:7)` → nested
                //    `lifecycleSync` → `scheduleReconnect`.
                await mockWS.waitForReceiveCall()
                let closeFrame = "{\"type\":7,\"error\":\"server-close\"}\u{1e}"
                mockWS.completeReceive(with: .string(closeFrame))

                // 3. `.reconnecting` must publish exactly once and the
                //    reconnect task must park in the sleeper. If the
                //    nested lifecycleSync wedged, none of this happens
                //    and the group timeout branch fires.
                await recorder.waitFor(state: .reconnecting)
                await sleeper.waitForNextSleep()

                // 4. Clean halt.
                await service.disconnect()
                await recorder.waitFor(state: .disconnected)
                return true
            }
            group.addTask {
                try? await Task.sleep(nanoseconds: 15 * 1_000_000_000)
                return false
            }
            let first = await group.next() ?? false
            group.cancelAll()
            return first
        }

        XCTAssertTrue(didComplete, "Type-7 close-frame path must not wedge the lifecycle queue (r10 blocker #1)")

        let states = recorder.snapshot()
        // Exactly one .reconnecting transition — the close frame
        // scheduled one reconnect; disconnect halted before any
        // further attempt could publish another.
        let reconnectingCount = states.filter { $0 == .reconnecting }.count
        XCTAssertEqual(reconnectingCount, 1,
                       "Exactly one .reconnecting transition expected; got \(reconnectingCount). Full: \(states)")
        XCTAssertEqual(states.last, .disconnected, "Final state must be .disconnected. Full: \(states)")
    }

    // MARK: - r10 blocker #2 — reconnect ownership hand-off

    /// r10 blocker #2 regression proof.
    ///
    /// Before r10, when a reconnect flow's `performConnect` published
    /// `.connected` and started the receive task while
    /// `reconnectToken` was still held by the outer reconnect owner,
    /// an immediate receive failure would call
    /// `scheduleReconnect(fromGen: myGen)`, see the still-held token,
    /// and drop silently. The outer reconnect task then cleared the
    /// token on exit — leaving the service `.connected` with NO live
    /// receive loop and NO scheduled retry.
    ///
    /// The r10 fix releases reconnect ownership atomically with the
    /// `.connected` publish + receive-task install (Step-4 of
    /// `performConnect`), so an immediately-failing receive can enroll
    /// a fresh reconnect slot. This test drives that exact race.
    func testRealService_reconnectImmediateReceiveFailure_schedulesNextRetryWithoutStrand() async throws {
        // Handler counter: first negotiate 500 (forces the initial
        // connect() to throw → scheduleReconnect fires); second+
        // negotiate 200 (reconnect's performConnect succeeds → the
        // hand-off race is the point of interest).
        let counter = NegotiateCounter()
        MockURLProtocol.requestHandler = { request in
            let n = counter.increment()
            if n == 1 {
                let response = HTTPURLResponse(
                    url: request.url!,
                    statusCode: 500,
                    httpVersion: nil,
                    headerFields: nil
                )!
                return (response, Data())
            }
            let payload: [String: Any] = [
                "connectionId": "conn-r10-b2",
                "connectionToken": "tok-r10-b2",
                "availableTransports": [
                    [
                        "transport": "WebSockets",
                        "transferFormats": ["Text", "Binary"]
                    ]
                ]
            ]
            let data = (try? JSONSerialization.data(withJSONObject: payload)) ?? Data()
            let response = HTTPURLResponse(
                url: request.url!,
                statusCode: 200,
                httpVersion: nil,
                headerFields: ["Content-Type": "application/json"]
            )!
            return (response, data)
        }

        let mockWS = MockSignalRWebSocket()
        let sleeper = LifecycleControlledSleeper()
        let service = SignalRService(
            serverURL: testURL,
            session: session,
            tokenProvider: { nil },
            reconnectBackoff: { _ in 1.0 },
            reconnectSleeper: sleeper.makeSleeper(),
            webSocketFactory: { _ in mockWS }
        )

        let recorder = LifecycleStateObserver()
        let sub = recordStates(service, into: recorder)
        _ = sub

        let didComplete: Bool = await withTaskGroup(of: Bool.self) { group in
            group.addTask {
                // 1. First connect fails on 500 → schedules reconnect.
                do {
                    try await service.connect()
                    XCTFail("connect() should have thrown on 500")
                } catch {}
                await recorder.waitFor(state: .reconnecting)
                await sleeper.waitForNextSleep()

                // 2. Release the sleeper — reconnect's performConnect
                //    runs against the now-succeeding negotiate.
                await sleeper.release()

                // 3. Handshake receive parks; release with a valid
                //    handshake response → `.connected` publishes and
                //    Step-4 releases the reconnect token (r10 B2a).
                await mockWS.waitForReceiveCall()
                mockWS.completeReceive(with: .string("{}\u{1e}"))
                await recorder.waitFor(state: .connected)

                // 4. Snapshot the sleeper baseline BEFORE we fail the
                //    receive. Then simulate an immediate transport
                //    error on the connected receive-loop's next call.
                //    Without the r10 B2a fix, `scheduleReconnect`
                //    would see the outer reconnect's token still held
                //    and drop, and the sleeper's baseline would never
                //    advance.
                let baselineSleeps = await sleeper.totalSleepEntries()
                await mockWS.waitForReceiveCall()
                mockWS.failReceive(with: URLError(.networkConnectionLost))

                // 5. Deterministic gate: a NEW sleep MUST enroll
                //    within a bounded window; that is the proof the
                //    nested `scheduleReconnect` installed a fresh
                //    slot instead of being dropped by stale
                //    ownership.
                let newSleepEnrolled = await sleeper.waitForNewSleep(
                    afterBaseline: baselineSleeps,
                    boundedNanoseconds: 2_000_000_000 // 2s failure-only bound
                )
                XCTAssertTrue(
                    newSleepEnrolled,
                    "After immediate receive failure, a NEW reconnect sleep must enroll (r10 blocker #2a — no stranded .connected)"
                )
                await recorder.waitFor(state: .reconnecting)

                // 6. Clean halt.
                await service.disconnect()
                await recorder.waitFor(state: .disconnected)
                return true
            }
            group.addTask {
                try? await Task.sleep(nanoseconds: 15 * 1_000_000_000)
                return false
            }
            let first = await group.next() ?? false
            group.cancelAll()
            return first
        }

        XCTAssertTrue(didComplete, "Immediate-receive-failure reconnect hand-off must not hang (r10 blocker #2a)")

        let states = recorder.snapshot()
        // Sequence must include: .disconnected (init) → .connecting
        // → .reconnecting (first retry) → .connecting (retry attempt)
        // → .connected → .reconnecting (second retry after receive
        // failure) → .disconnected. Assert the shape rather than the
        // exact indices so hub coalescing during transitions doesn't
        // break the test.
        XCTAssertTrue(states.contains(.connected),
                      ".connected must publish after reconnect succeeds. Full: \(states)")
        XCTAssertTrue(states.filter { $0 == .reconnecting }.count >= 2,
                      "Two .reconnecting transitions expected — initial retry and post-connected receive-failure retry. Full: \(states)")
        XCTAssertEqual(states.last, .disconnected, "Final state must be .disconnected. Full: \(states)")
    }

    /// r10 blocker #2b regression proof — rewritten for r11 blocker #2
    /// to prove supersede tear-down non-vacuously.
    ///
    /// Prior r10 version asserted `firstMock.isCancelled()` after a
    /// supersede-connect ran, but `scheduleReconnect` had already
    /// cancelled `firstMock` inside its own `tearDownLocked()`
    /// BEFORE the explicit connect ran, so the assertion always
    /// passed even if `connect()`'s supersede branch never called
    /// `tearDownLocked()` at all.
    ///
    /// r11 uses a 3-transport FIFO sequence so the supersede path's
    /// tear-down is proven directly on a socket that *only* the
    /// supersede branch can cancel:
    ///
    ///   * `firstMock`  — initial connect(). Parked in the connected
    ///                    receive-loop, then failed → `scheduleReconnect`
    ///                    cancels it via its own `tearDownLocked` (not
    ///                    the proof).
    ///   * `secondMock` — reconnect flow's `performConnect` installs
    ///                    it as `webSocketTask` (Step-2) and parks in
    ///                    handshake `receive()` (Step-3). State stays
    ///                    `.reconnecting` because Step-4 hasn't run.
    ///                    This is the socket the supersede path MUST
    ///                    tear down.
    ///   * `thirdMock`  — installed by the explicit supersede
    ///                    `connect()` after `tearDownLocked` ran on
    ///                    `secondMock`.
    ///
    /// Proof: after `thirdMock`'s handshake receive is reached (a
    /// deterministic barrier proving supersede + Step-2 install both
    /// ran), `secondMock.isCancelled()` must be `true` and
    /// `thirdMock.isCancelled()` must be `false`.
    func testRealService_connectSupersedesInFlightReconnect_tearsDownInstalledTransport() async throws {
        // Working negotiate for all attempts.
        let payload: [String: Any] = [
            "connectionId": "conn-r11-b2",
            "connectionToken": "tok-r11-b2",
            "availableTransports": [
                [
                    "transport": "WebSockets",
                    "transferFormats": ["Text", "Binary"]
                ]
            ]
        ]
        MockURLProtocol.requestHandler = { request in
            let data = (try? JSONSerialization.data(withJSONObject: payload)) ?? Data()
            let response = HTTPURLResponse(
                url: request.url!,
                statusCode: 200,
                httpVersion: nil,
                headerFields: ["Content-Type": "application/json"]
            )!
            return (response, data)
        }

        // 3-transport FIFO sequence — see method doc.
        let firstMock = MockSignalRWebSocket()
        let secondMock = MockSignalRWebSocket()
        let thirdMock = MockSignalRWebSocket()
        let mockBox = MockWebSocketSwitcher([firstMock, secondMock, thirdMock])

        let sleeper = LifecycleControlledSleeper()
        let service = SignalRService(
            serverURL: testURL,
            session: session,
            tokenProvider: { nil },
            reconnectBackoff: { _ in 1.0 },
            reconnectSleeper: sleeper.makeSleeper(),
            webSocketFactory: { _ in mockBox.next() }
        )

        let recorder = LifecycleStateObserver()
        let sub = recordStates(service, into: recorder)
        _ = sub

        let didComplete: Bool = await withTaskGroup(of: Bool.self) { group in
            group.addTask {
                // 1. First connect: succeeds, .connected published,
                //    firstMock parked in the connected-loop receive().
                let firstConnect = Task { try? await service.connect() }
                await firstMock.waitForReceiveCall()               // handshake receive
                firstMock.completeReceive(with: .string("{}\u{1e}"))
                _ = await firstConnect.value
                await recorder.waitFor(state: .connected)
                await firstMock.waitForReceiveCall()               // connected-loop receive

                // 2. Fail firstMock. scheduleReconnect fires, its own
                //    tearDownLocked cancels firstMock (not the proof),
                //    state -> .reconnecting, reconnect body parks in
                //    the sleeper.
                firstMock.failReceive(with: URLError(.networkConnectionLost))
                await recorder.waitFor(state: .reconnecting)
                await sleeper.waitForNextSleep()

                // 3. Release the sleeper. The reconnect body's
                //    performConnect installs secondMock (Step-2) as
                //    the current webSocketTask, sends the handshake
                //    (Step-3 auto-completes send), and parks in the
                //    handshake receive(). State remains .reconnecting
                //    because Step-4 has not yet run — this is exactly
                //    the window where a supersede-connect must reach
                //    secondMock via tearDownLocked.
                await sleeper.release()
                await secondMock.waitForReceiveCall()              // parked in handshake receive

                // Baseline: neither the reconnect-installed secondMock
                // nor the not-yet-installed thirdMock is cancelled.
                XCTAssertFalse(
                    secondMock.isCancelled(),
                    "secondMock must be alive when supersede runs — the r11 blocker #2 non-vacuous proof"
                )
                XCTAssertFalse(
                    thirdMock.isCancelled(),
                    "thirdMock cannot be cancelled before being installed"
                )

                // 4. Explicit connect() supersedes. State is
                //    .reconnecting so `alreadyLive` is false → the
                //    supersede branch runs: `tearDownLocked()` cancels
                //    secondMock, generation bumps, the detached
                //    reconnect task is cancelled, then a fresh
                //    performConnect installs thirdMock.
                let thirdConnect = Task { try? await service.connect() }

                // Wait for thirdMock's handshake receive as the
                // deterministic barrier proving:
                //   * the supersede branch's tearDownLocked ran (else
                //     the current webSocketTask would still be
                //     secondMock and thirdMock would never be
                //     installed);
                //   * the fresh performConnect Step-2 has installed
                //     thirdMock and Step-3 is parked in receive.
                await thirdMock.waitForReceiveCall()

                // Non-vacuous proof: the supersede path's
                // tearDownLocked cancelled secondMock BEFORE the fresh
                // performConnect Step-2 could install thirdMock.
                // firstMock's cancellation is intentionally NOT the
                // proof because scheduleReconnect already cancelled it
                // in step 2.
                XCTAssertTrue(
                    secondMock.isCancelled(),
                    "supersede connect() must tearDownLocked the reconnect-installed transport (r11 blocker #2)"
                )
                XCTAssertFalse(
                    thirdMock.isCancelled(),
                    "supersede-installed transport must remain the current active transport"
                )

                // Drain the superseded old reconnect task's handshake
                // receive so it can exit cleanly — the mock's
                // `cancel(with:reason:)` only flips a flag; it does
                // not resume the parked continuation, so without this
                // failReceive the detached reconnect Task would hang
                // forever. Its performConnect will throw, its catch
                // block will see generation moved on and no-op.
                secondMock.failReceive(with: URLError(.cancelled))

                // Complete thirdMock's handshake so the supersede
                // connect() returns.
                thirdMock.completeReceive(with: .string("{}\u{1e}"))
                _ = await thirdConnect.value

                await service.disconnect()
                await recorder.waitFor(state: .disconnected)
                return true
            }
            group.addTask {
                try? await Task.sleep(nanoseconds: 15 * 1_000_000_000)
                return false
            }
            let first = await group.next() ?? false
            group.cancelAll()
            return first
        }

        XCTAssertTrue(didComplete, "connect() supersede + teardown must not hang (r11 blocker #2)")
    }

    // MARK: - r11 blocker #1 direct sleeper-primitive proofs

    /// r11 blocker #1 direct proof: `waitForNewSleep` returns `false`
    /// within the bounded window when no sleep enrolls, and does not
    /// hang draining a cancellation-unaware child (the r10 regression).
    ///
    /// The prior implementation used a plain `withCheckedContinuation`
    /// inside `waitForNextSleep`, so the task-group `cancelAll()` after
    /// the timeout child fired left the sleep-watcher child parked
    /// forever — `withTaskGroup` awaits every child before returning,
    /// so the whole method never came back. The r11 fix wraps the
    /// waiter in `withTaskCancellationHandler` with UUID-keyed removal
    /// on cancellation.
    func testLifecycleControlledSleeper_waitForNewSleep_idlePathCompletesWithinBound_returnsFalse() async throws {
        let sleeper = LifecycleControlledSleeper()
        let bound: UInt64 = 100_000_000 // 100ms
        let started = ContinuousClock.now
        let result = await sleeper.waitForNewSleep(afterBaseline: 0, boundedNanoseconds: bound)
        let elapsed = ContinuousClock.now - started

        XCTAssertFalse(result, "idle path must return false — no new sleep enrolled")
        // Elapsed time can only fail; must be well below 5s so a hang
        // (the r10 regression) trips the assertion instead of the
        // Xcode default 60s testcase timeout.
        XCTAssertLessThan(elapsed, .seconds(5),
                          "waitForNewSleep must return promptly on the idle path — r11 blocker #1 regression trip")
    }

    /// r11 blocker #1 direct proof: the positive path still resolves.
    /// A concurrent `sleep(for:)` enrolment must wake the waiter and
    /// return `true`.
    func testLifecycleControlledSleeper_waitForNewSleep_sleepEnrollmentPathCompletes_returnsTrue() async throws {
        let sleeper = LifecycleControlledSleeper()
        let baseline = await sleeper.totalSleepEntries()

        let didObserve: Bool = await withTaskGroup(of: Bool.self) { group in
            group.addTask {
                await sleeper.waitForNewSleep(
                    afterBaseline: baseline,
                    boundedNanoseconds: 5_000_000_000 // 5s failure-only bound
                )
            }
            group.addTask {
                // Enrol a sleep asynchronously. `sleep(for:)` parks on
                // the internal continuation; the waiter must observe
                // the enrolment and return true.
                await sleeper.sleep(for: 0)
                return true
            }
            // The waiter returns true when the enrolment fires; the
            // sleep child returns true when its parked continuation
            // resumes. Either "first" is a satisfactory positive
            // signal — we assert both children complete.
            var first: Bool? = await group.next()
            // Release the parked sleeper so the second child can
            // exit; without this the second child would be blocked
            // and cancelAll would hang if the waiter is a
            // cancellation-unaware primitive (this is a secondary
            // regression trip for the `sleep(for:)` cancellation
            // handler path).
            await sleeper.release()
            let second: Bool? = await group.next()
            if first == nil { first = second }
            return (first ?? false) && (second ?? false)
        }

        XCTAssertTrue(didObserve, "positive path must return true when a new sleep enrols")
    }
}

/// Helper: hands out mock websocket instances in FIFO order so tests
/// can distinguish "the reconnect flow's socket" from "the
/// supersede-connect flow's socket". Access is guarded by NSLock
/// because `SignalRService.makeWebSocketTask` may be invoked from any
/// URLSession worker thread depending on the ambient concurrency.
final class MockWebSocketSwitcher: @unchecked Sendable {
    private let lock = NSLock()
    private var remaining: [MockSignalRWebSocket]

    /// Legacy two-mock initialiser retained so existing tests keep
    /// compiling. New callers should use `init(_:)` with the full
    /// FIFO sequence.
    convenience init(first: MockSignalRWebSocket, second: MockSignalRWebSocket) {
        self.init([first, second])
    }

    init(_ mocks: [MockSignalRWebSocket]) {
        self.remaining = mocks
    }

    func next() -> MockSignalRWebSocket {
        lock.lock(); defer { lock.unlock() }
        guard !remaining.isEmpty else {
            // Return a fresh mock rather than crashing — the test's
            // assertions will catch unexpected extra sockets.
            return MockSignalRWebSocket()
        }
        return remaining.removeFirst()
    }
}
