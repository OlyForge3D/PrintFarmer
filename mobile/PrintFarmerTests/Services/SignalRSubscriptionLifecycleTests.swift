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
    /// r10 blocker #3: waiters keyed by UUID so `waitForNextSleep`'s
    /// cancellation handler can remove exactly the one it registered
    /// (not all waiters). Prior implementation used an unordered array
    /// with a plain `withCheckedContinuation`, which meant a
    /// TaskGroup `cancelAll()` never woke the parked child — the
    /// idle/false path hung forever.
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

    /// r10 blocker #3: cancellation-aware wait. The prior version used
    /// a plain `withCheckedContinuation`, so if the caller's Task was
    /// cancelled, this continuation stayed parked forever. Now: the
    /// waiter is keyed with a UUID; the cancellation handler removes
    /// exactly that entry and resumes it, so cancellation is honoured
    /// but any still-registered peers are untouched. Returning
    /// normally on cancellation is safe because the caller
    /// (`waitForSleepAfter`) rechecks its condition in a loop.
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

    /// r12 blocker #2: bounded pure barrier. Parks the caller until
    /// `sleepEnterCount > baseline`. No wall-clock bound — the caller
    /// is expected to run inside an outer `withTaskGroup` timeout
    /// used **only as a failure ceiling** (never as evidence of
    /// absence). Uses a UUID-keyed waiter registered via the same
    /// `waitingForSleep` map used by `waitForNextSleep`, so it is
    /// cancellation-safe.
    func waitForSleepAfter(baseline: Int) async {
        while sleepEnterCount <= baseline {
            await waitForNextSleep()
            if Task.isCancelled { return }
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

/// r12 blocker #4 helper: one-shot latch that fires on the FIRST
/// `.disconnected` observation that occurs AFTER `.reconnecting`
/// has been observed. Used by the bounded-terminal test to avoid
/// tripping on the recorder's initial `.disconnected` state.
final class TerminalDisconnectLatch: @unchecked Sendable {
    private let lock = NSLock()
    private var sawReconnecting = false
    private var fired = false
    private var waiters: [(id: UUID, cont: CheckedContinuation<Void, Never>)] = []

    func observe(_ state: SignalRConnectionState) {
        lock.lock()
        if state == .reconnecting {
            sawReconnecting = true
            lock.unlock()
            return
        }
        if state == .disconnected, sawReconnecting, !fired {
            fired = true
            let toResume = waiters.map { $0.cont }
            waiters.removeAll()
            lock.unlock()
            for c in toResume { c.resume() }
            return
        }
        lock.unlock()
    }

    /// Cancellation-aware terminal wait. If the caller's Task is
    /// cancelled (e.g. by the enclosing task-group's `cancelAll()`
    /// after the failure-ceiling timeout child fires), the waiter is
    /// removed and resumed with `Void` so the task-group can unwind
    /// cleanly. Without this, `withThrowingTaskGroup` would await the
    /// terminal-waiter child indefinitely past the ceiling.
    ///
    /// Pre-enrollment cancellation race: `withTaskCancellationHandler`'s
    /// `onCancel` may fire before the operation closure enqueues its
    /// continuation (e.g. Task already cancelled at registration).
    /// `_enqueue` therefore also checks `Task.isCancelled` under the
    /// same lock as the append; if cancelled, it resumes immediately
    /// without enqueuing so no waiter can leak past the ceiling.
    func waitForTerminal() async {
        let id = UUID()
        await withTaskCancellationHandler {
            if _quickCheck() { return }
            await withCheckedContinuation { cont in
                _enqueue(id: id, cont: cont)
            }
        } onCancel: {
            self._cancelWaiter(id: id)
        }
    }

    private func _quickCheck() -> Bool {
        lock.lock(); defer { lock.unlock() }
        return fired
    }

    private func _enqueue(id: UUID, cont: CheckedContinuation<Void, Never>) {
        lock.lock()
        if fired {
            lock.unlock()
            cont.resume()
            return
        }
        if Task.isCancelled {
            lock.unlock()
            cont.resume()
            return
        }
        waiters.append((id: id, cont: cont))
        lock.unlock()
    }

    private func _cancelWaiter(id: UUID) {
        var toResume: CheckedContinuation<Void, Never>?
        lock.lock()
        if let idx = waiters.firstIndex(where: { $0.id == id }) {
            toResume = waiters.remove(at: idx).cont
        }
        lock.unlock()
        toResume?.resume()
    }

    func hasFired() -> Bool {
        lock.lock(); defer { lock.unlock() }
        return fired
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
    private var pendingReceives: [(id: UUID, cont: CheckedContinuation<URLSessionWebSocketTask.Message, Error>)] = []
    private var pendingSends: [CheckedContinuation<Void, Error>] = []
    private var sentMessages: [URLSessionWebSocketTask.Message] = []
    private var resumed = false
    private var cancelled = false
    private var receiveWaiters: [CheckedContinuation<Void, Never>] = []
    // r15 (Hicks item 6): cumulative enrollment counter + target
    // waiters. Every successful `_enqueueReceive` bumps
    // `receiveEnrolledCount`; a test that needs "receiver B has
    // actually enrolled its continuation" awaits
    // `waitForReceiveEnrollments(count: 2)` after enrolling A then B.
    // This is deterministically stronger than `waitForReceiveCall`,
    // which only fires on the FIRST arrival and can't distinguish
    // caller B's enrollment from caller A's.
    private var receiveEnrolledCount: Int = 0
    private var enrollmentWaiters: [(target: Int, cont: CheckedContinuation<Void, Never>)] = []

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
        // r13 (Hicks item 1): cancellation-aware receive. When the
        // caller's Task is cancelled (e.g. `tearDownLocked` calls
        // `receiveTask?.cancel()`), fail the pending continuation so
        // the receive-loop body can break, run its `defer`, and
        // exit — otherwise the task-lifetime counter would stay at 1
        // forever and `waitForReceiveLoopsZero()` would never fire.
        // This matches real `URLSessionWebSocketTask.receive()`
        // behavior: Task cancellation propagates to the pending
        // receive as a thrown error.
        //
        // r14 (Hicks final item 2): caller-specific cancellation
        // AND pre-enrollment race safety. Every pending receive is
        // keyed by a unique `UUID` allocated per-call. The
        // `onCancel` handler removes and fails ONLY that caller's
        // continuation — it does NOT resume the FIFO-first pending
        // receive, which could belong to an unrelated caller. Under
        // the same lock as the append, `_enqueueReceive` checks
        // `Task.isCancelled`; if the caller's Task was cancelled
        // before enrollment, the continuation is failed immediately
        // with `CancellationError()` without being enqueued.
        //
        // The r9 blocker #3 test (`disconnectDuringHandshakeReceive_
        // neverPublishesConnected`) is unaffected: the handshake's
        // `wsTask.receive()` is awaited by the outer `connect()`
        // Task, which is NOT cancelled by `service.disconnect()`.
        // Only `wsTask.cancel(with:reason:)` fires against the mock
        // there, and that call remains a no-op for pending receives.
        // Message-delivery FIFO order via `completeReceive` /
        // `failReceive` is preserved — they still resume the
        // first-in continuation.
        let id = UUID()
        return try await withTaskCancellationHandler {
            try await withCheckedThrowingContinuation { cont in
                let waiters = _enqueueReceive(id: id, cont: cont)
                for w in waiters { w.resume() }
            }
        } onCancel: { [weak self] in
            self?._failReceiveOnCancel(id: id)
        }
    }

    private func _failReceiveOnCancel(id: UUID) {
        var toFail: CheckedContinuation<URLSessionWebSocketTask.Message, Error>?
        lock.lock()
        if let idx = pendingReceives.firstIndex(where: { $0.id == id }) {
            toFail = pendingReceives.remove(at: idx).cont
        }
        lock.unlock()
        toFail?.resume(throwing: CancellationError())
    }

    private func _recordSent(_ message: URLSessionWebSocketTask.Message) {
        lock.lock(); defer { lock.unlock() }
        sentMessages.append(message)
    }

    private func _enqueueSend(cont: CheckedContinuation<Void, Error>) {
        lock.lock(); defer { lock.unlock() }
        pendingSends.append(cont)
    }

    private func _enqueueReceive(id: UUID, cont: CheckedContinuation<URLSessionWebSocketTask.Message, Error>) -> [CheckedContinuation<Void, Never>] {
        lock.lock()
        if Task.isCancelled {
            lock.unlock()
            cont.resume(throwing: CancellationError())
            return []
        }
        pendingReceives.append((id: id, cont: cont))
        receiveEnrolledCount += 1
        let currentEnrolled = receiveEnrolledCount
        // Wake `waitForReceiveCall` waiters (FIFO one-shot).
        let waiters = receiveWaiters
        receiveWaiters.removeAll()
        // r15 (Hicks item 6): wake enrollment-count waiters whose
        // target has now been reached, and remove them from the list.
        var enrollmentToWake: [CheckedContinuation<Void, Never>] = []
        var remainingEnrollment: [(target: Int, cont: CheckedContinuation<Void, Never>)] = []
        for entry in enrollmentWaiters {
            if entry.target <= currentEnrolled {
                enrollmentToWake.append(entry.cont)
            } else {
                remainingEnrollment.append(entry)
            }
        }
        enrollmentWaiters = remainingEnrollment
        lock.unlock()
        for w in enrollmentToWake { w.resume() }
        return waiters
    }

    // Test-side controls -------------------------------------------

    func waitForReceiveCall() async {
        if _hasPendingReceive() { return }
        await withCheckedContinuation { cont in
            _enqueueReceiveWaiter(cont: cont)
        }
    }

    /// r15 (Hicks item 6): deterministic wait for the Nth cumulative
    /// receive enrollment. Uses a monotonically-increasing counter
    /// (never decremented by completeReceive/failReceive/cancel), so
    /// this reflects "N callers have invoked receive() and
    /// successfully enrolled their continuations", regardless of
    /// intervening completions. Enables caller-specific proofs like
    /// "A enrolled, then B enrolled, cancel A, B stays parked".
    func waitForReceiveEnrollments(count target: Int) async {
        precondition(target >= 1, "target must be >= 1")
        if _enrollmentReached(target: target) { return }
        await withCheckedContinuation { (cont: CheckedContinuation<Void, Never>) in
            _enqueueEnrollmentWaiter(target: target, cont: cont)
        }
    }

    private func _enrollmentReached(target: Int) -> Bool {
        lock.lock(); defer { lock.unlock() }
        return receiveEnrolledCount >= target
    }

    private func _enqueueEnrollmentWaiter(target: Int, cont: CheckedContinuation<Void, Never>) {
        lock.lock()
        if receiveEnrolledCount >= target {
            lock.unlock()
            cont.resume()
            return
        }
        enrollmentWaiters.append((target: target, cont: cont))
        lock.unlock()
    }

    /// r15 (Hicks item 6): count of currently-parked receive
    /// continuations. Distinct from `receiveEnrolledCount` because
    /// completed/failed/cancelled receives are removed from
    /// `pendingReceives` but leave the cumulative counter alone.
    func pendingReceiveCount() -> Int {
        lock.lock(); defer { lock.unlock() }
        return pendingReceives.count
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
        guard !pendingReceives.isEmpty else { lock.unlock(); return }
        let entry = pendingReceives.removeFirst()
        lock.unlock()
        entry.cont.resume(returning: message)
    }

    func failReceive(with error: Error) {
        lock.lock()
        guard !pendingReceives.isEmpty else { lock.unlock(); return }
        let entry = pendingReceives.removeFirst()
        lock.unlock()
        entry.cont.resume(throwing: error)
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
        let priorMax = service.lifecycleInvariants.snapshot().maxReconnectOwners
        await service.disconnect()
        await recorder.waitFor(state: .disconnected)

        // r12 blocker #2: deterministic tear-down barrier. Instead
        // of "wait a wall-clock window and hope no new sleep
        // enrolls", we prove positively that the reconnect owner
        // Task has fully torn down (counter hits zero) and THEN
        // check that no additional sleep enrolled between disconnect
        // and tear-down. This is a positive completion event; the
        // outer test-case timeout is the only failure ceiling.
        await service.lifecycleInvariants.waitForReconnectOwnersZero()
        let sleepsAfterTearDown = await sleeper.totalSleepEntries()
        XCTAssertEqual(
            sleepsAfterTearDown,
            baselineSleeps,
            "After the reconnect owner has torn down (activeReconnectOwners == 0), no new reconnect sleep may have enrolled"
        )
        let inv = service.lifecycleInvariants.snapshot()
        XCTAssertEqual(inv.activeReconnectOwners, 0, "reconnect owner counter must be zero after tear-down barrier")
        XCTAssertLessThanOrEqual(inv.maxReconnectOwners, 1, "no interleaving may allow two concurrent reconnect owners; max seen: \(inv.maxReconnectOwners) (prior: \(priorMax))")
        XCTAssertLessThanOrEqual(inv.maxTransports, 1, "no interleaving may allow two concurrent transports; max seen: \(inv.maxTransports)")
        XCTAssertLessThanOrEqual(inv.maxReceiveLoops, 1, "no interleaving may allow two concurrent receive loops; max seen: \(inv.maxReceiveLoops)")

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

        // r12 blocker #6a: prove the stale negotiate failure is
        // inert. After disconnect() bumped generation and the
        // released negotiate response landed, no reconnect owner
        // may have been scheduled off the stale failure, no
        // transport may have been installed, no receive loop may
        // have started. All counters must remain at zero-max <= 1.
        await service.lifecycleInvariants.waitForReconnectOwnersZero()
        let inv = service.lifecycleInvariants.snapshot()
        XCTAssertEqual(inv.activeReconnectOwners, 0, "no reconnect owner may remain live after stale negotiate failure")
        XCTAssertEqual(inv.activeTransports, 0, "no transport may remain installed after stale negotiate failure")
        XCTAssertEqual(inv.activeReceiveLoops, 0, "no receive loop may remain live after stale negotiate failure")
        XCTAssertLessThanOrEqual(inv.maxReconnectOwners, 1, "max concurrent reconnect owners must be <= 1")
        XCTAssertLessThanOrEqual(inv.maxTransports, 1, "max concurrent transports must be <= 1")
        XCTAssertLessThanOrEqual(inv.maxReceiveLoops, 1, "max concurrent receive loops must be <= 1")

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

        // r12 blocker #6b: prove the stale generation frame is inert.
        // A frame was delivered to the mock socket AFTER disconnect
        // bumped generation. The service's post-receive generation
        // guard must have rejected it — no transport install, no
        // receive-loop start, no state mutation to `.connected`, no
        // callbacks fired. Counters must show all activity <= 1 and
        // fully torn down.
        await service.lifecycleInvariants.waitForReceiveLoopsZero()
        await service.lifecycleInvariants.waitForTransportsZero()
        let inv = service.lifecycleInvariants.snapshot()
        XCTAssertEqual(inv.activeTransports, 0, "no transport may remain installed after stale-generation frame")
        XCTAssertEqual(inv.activeReceiveLoops, 0, "no receive loop may remain live after stale-generation frame")
        XCTAssertLessThanOrEqual(inv.maxTransports, 1, "max concurrent transports must be <= 1; observed \(inv.maxTransports)")
        XCTAssertLessThanOrEqual(inv.maxReceiveLoops, 1, "max concurrent receive loops must be <= 1; observed \(inv.maxReceiveLoops)")

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

        // r12 blocker #2: deterministic tear-down barrier. Prove
        // the reconnect owner Task actually reached exit
        // (activeReconnectOwners == 0), then check no further sleep
        // enrollment occurred. No wall-clock idle-window is used as
        // pass evidence.
        await service.lifecycleInvariants.waitForReconnectOwnersZero()
        let sleepsAfterTearDown = await sleeper.totalSleepEntries()
        XCTAssertEqual(
            sleepsAfterTearDown,
            baselineSleeps,
            "After the reconnect owner has torn down, no new reconnect sleep may have enrolled"
        )
        let inv = service.lifecycleInvariants.snapshot()
        XCTAssertEqual(inv.activeReconnectOwners, 0)
        // r15 (Hicks item 2): single sequential retry-owner loop —
        // one enter/exit per retry chain regardless of attempts.
        XCTAssertLessThanOrEqual(inv.maxReconnectOwners, 1,
                                 "bounded retry chain: max concurrent reconnect owners must be <= 1. r15 single-loop retry-owner design guarantees exactly one enterReconnectOwner/exitReconnectOwner pair per chain. Observed \(inv.maxReconnectOwners).")
        // Non-vacuity witness: the retry LOOP actually iterated,
        // proved by `reconnectAttemptCount` regardless of the
        // one-owner structural bound.
        XCTAssertGreaterThanOrEqual(inv.reconnectAttemptCount, 2,
                                    "retry chain must have iterated at least twice; observed reconnectAttemptCount=\(inv.reconnectAttemptCount)")
        // r15 (Hicks item 4): balanced enter==exit across families.
        XCTAssertEqual(inv.transportEnterCount, inv.transportExitCount,
                       "transport enter/exit must be balanced; observed enter=\(inv.transportEnterCount) exit=\(inv.transportExitCount)")
        XCTAssertEqual(inv.receiveLoopEnterCount, inv.receiveLoopExitCount,
                       "receive-loop enter/exit must be balanced; observed enter=\(inv.receiveLoopEnterCount) exit=\(inv.receiveLoopExitCount)")
        XCTAssertEqual(inv.reconnectOwnerEnterCount, inv.reconnectOwnerExitCount,
                       "reconnect-owner enter/exit must be balanced; observed enter=\(inv.reconnectOwnerEnterCount) exit=\(inv.reconnectOwnerExitCount)")
        XCTAssertLessThanOrEqual(inv.maxTransports, 1)
        XCTAssertLessThanOrEqual(inv.maxReceiveLoops, 1)

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

        // r12 blocker #3 + r15 (Hicks item 4/8): prove the type-7
        // interleaving never ran with two active transports, receive
        // loops, or reconnect owners. `reconnectAttemptCount` proves
        // the retry loop iterated non-vacuously off the type-7 →
        // scheduleReconnect path.
        let inv = service.lifecycleInvariants.snapshot()
        XCTAssertGreaterThanOrEqual(inv.reconnectAttemptCount, 1,
                                    "retry loop must have iterated >= 1 time via type-7 close; observed \(inv.reconnectAttemptCount)")
        XCTAssertEqual(inv.reconnectOwnerEnterCount, 1,
                       "type-7 must have entered exactly one reconnect-owner Task (r15 single-loop); observed \(inv.reconnectOwnerEnterCount)")
        XCTAssertEqual(inv.reconnectOwnerEnterCount, inv.reconnectOwnerExitCount,
                       "reconnect-owner enter/exit must be balanced; observed enter=\(inv.reconnectOwnerEnterCount) exit=\(inv.reconnectOwnerExitCount)")
        XCTAssertEqual(inv.transportEnterCount, inv.transportExitCount,
                       "transport enter/exit must be balanced; observed enter=\(inv.transportEnterCount) exit=\(inv.transportExitCount)")
        XCTAssertEqual(inv.receiveLoopEnterCount, inv.receiveLoopExitCount,
                       "receive-loop enter/exit must be balanced; observed enter=\(inv.receiveLoopEnterCount) exit=\(inv.receiveLoopExitCount)")
        XCTAssertLessThanOrEqual(inv.maxReconnectOwners, 1,
                                 "max concurrent reconnect owners must be <= 1 across type-7 interleaving; observed \(inv.maxReconnectOwners)")
        XCTAssertLessThanOrEqual(inv.maxTransports, 1,
                                 "max concurrent transports must be <= 1; observed \(inv.maxTransports)")
        XCTAssertLessThanOrEqual(inv.maxReceiveLoops, 1,
                                 "max concurrent receive loops must be <= 1; observed \(inv.maxReceiveLoops)")

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

                // 5. Deterministic barrier: a NEW sleep MUST enroll
                //    past baseline; that is the positive proof the
                //    nested `scheduleReconnect` installed a fresh
                //    slot instead of being dropped by stale
                //    ownership. `waitForSleepAfter` parks purely on
                //    enrollment — the outer 15s task-group timeout
                //    is used only as a FAILURE ceiling, never as
                //    evidence of absence.
                await sleeper.waitForSleepAfter(baseline: baselineSleeps)
                await recorder.waitFor(state: .reconnecting)

                // r12 blocker #5: receive-error handoff proof. At
                // THIS point the reconnect owner has enrolled and
                // is parked in `sleep(for:)` (proved by
                // `waitForSleepAfter`); the receive task that just
                // failed must have exited (its Task body threw and
                // returned). Snapshot invariants: exactly one
                // reconnect owner active, zero receive loops
                // active, zero transports installed. This proves
                // the registered receive task was cancelled/torn
                // down BEFORE the detached reconnect continues.
                let handoffInv = service.lifecycleInvariants.snapshot()
                XCTAssertEqual(handoffInv.activeReconnectOwners, 1,
                               "exactly one reconnect owner must be alive during handoff; observed \(handoffInv.activeReconnectOwners)")
                XCTAssertEqual(handoffInv.activeReceiveLoops, 0,
                               "receive task must have exited before reconnect owner continues; observed \(handoffInv.activeReceiveLoops)")
                XCTAssertEqual(handoffInv.activeTransports, 0,
                               "prior transport must be torn down during handoff; observed \(handoffInv.activeTransports)")

                // Non-vacuous invariant assertion: exactly one
                // reconnect owner is active at this point (the fresh
                // one just enrolled), and at no point during the
                // full type-7-plus-recv-error interleaving did the
                // service ever have two concurrent transports,
                // receive loops, or reconnect owners.
                let inv = service.lifecycleInvariants.snapshot()
                XCTAssertGreaterThanOrEqual(inv.reconnectOwnerEnterCount, 1)
                XCTAssertLessThanOrEqual(inv.maxReconnectOwners, 1, "max concurrent reconnect owners must be <= 1 (r12 blocker #1); observed \(inv.maxReconnectOwners)")
                XCTAssertLessThanOrEqual(inv.maxTransports, 1, "max concurrent transports must be <= 1; observed \(inv.maxTransports)")
                XCTAssertLessThanOrEqual(inv.maxReceiveLoops, 1, "max concurrent receive loops must be <= 1; observed \(inv.maxReceiveLoops)")

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

    // MARK: - r12 blocker #2 direct sleeper-primitive proof

    /// r12 blocker #3 composite proof: type-7 close-frame on the
    /// **first** transport forces `scheduleReconnect`; the reconnect
    /// flow installs a **second** transport, completes handshake and
    /// publishes `.connected`; that new transport's **first**
    /// connected-loop receive throws an immediate URL error. The
    /// combined interleaving must never allow two active transports,
    /// receive loops, or reconnect owners at any observed point.
    ///
    /// The proof uses:
    /// * `MockWebSocketSwitcher` → deterministic two-socket handover.
    /// * `LifecycleControlledSleeper` → gates each retry.
    /// * `LifecycleStateObserver` → per-state continuation barrier.
    /// * `SignalRLifecycleInvariants.snapshot()` at each phase edge.
    func testRealService_type7CloseThenImmediateReceiveErrorOnNewTransport_singleOwnerSingleTransport() async throws {
        let negotiatePayload: [String: Any] = [
            "connectionId": "conn-r12-b3",
            "connectionToken": "tok-r12-b3",
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

        let mockA = MockSignalRWebSocket()
        let mockB = MockSignalRWebSocket()
        let switcher = MockWebSocketSwitcher([mockA, mockB])
        let sleeper = LifecycleControlledSleeper()
        let service = SignalRService(
            serverURL: testURL,
            session: session,
            tokenProvider: { nil },
            reconnectBackoff: { _ in 1.0 },
            reconnectSleeper: sleeper.makeSleeper(),
            webSocketFactory: { _ in switcher.next() }
        )

        let recorder = LifecycleStateObserver()
        let sub = recordStates(service, into: recorder)
        _ = sub

        let didComplete: Bool = await withTaskGroup(of: Bool.self) { group in
            group.addTask {
                // Phase 1: initial connect handshake → connected via mockA.
                let connectTask = Task { try? await service.connect() }
                await mockA.waitForReceiveCall()
                mockA.completeReceive(with: .string("{}\u{1e}"))
                _ = await connectTask.value
                await recorder.waitFor(state: .connected)

                // Phase 2: type-7 close frame on mockA → scheduleReconnect.
                await mockA.waitForReceiveCall()
                mockA.completeReceive(with: .string("{\"type\":7,\"error\":\"server-close\"}\u{1e}"))
                await recorder.waitFor(state: .reconnecting)
                await sleeper.waitForNextSleep()

                // Snapshot at handoff-mid: previous transport torn
                // down, receive loop exited, reconnect owner parked.
                let midInv = service.lifecycleInvariants.snapshot()
                XCTAssertEqual(midInv.activeReconnectOwners, 1,
                               "one reconnect owner must be parked after type-7 close; observed \(midInv.activeReconnectOwners)")
                XCTAssertEqual(midInv.activeTransports, 0,
                               "previous transport must be torn down before reconnect flow installs its own; observed \(midInv.activeTransports)")
                XCTAssertEqual(midInv.activeReceiveLoops, 0,
                               "previous receive loop must have exited; observed \(midInv.activeReceiveLoops)")

                // Phase 3: release sleeper → reconnect performConnect
                // installs mockB, completes handshake, publishes
                // `.connected` again.
                await sleeper.release()
                await mockB.waitForReceiveCall()
                mockB.completeReceive(with: .string("{}\u{1e}"))
                await recorder.waitFor(state: .connected)

                // Phase 4: mockB's connected receive-loop parks → fail
                // it with URLError. This is the "immediate receive
                // error on the new transport" leg.
                let baselineSleeps = await sleeper.totalSleepEntries()
                await mockB.waitForReceiveCall()
                mockB.failReceive(with: URLError(.networkConnectionLost))

                // Deterministic barrier: another reconnect enrolls.
                await sleeper.waitForSleepAfter(baseline: baselineSleeps)
                await recorder.waitFor(state: .reconnecting)

                // Snapshot at second handoff: r15 (Hicks item 8) —
                // exact deltas, not lower bounds. Two reconnect
                // sequences fired (type-7 close + recv-error), so
                // exactly two reconnect-owner enters, two transport
                // enters (mockA + mockB), and two receive-loop enters.
                // All balanced except the second owner, which is
                // currently parked in the sleeper (exit count = 1).
                // No duplicate socket may have coexisted.
                let secondInv = service.lifecycleInvariants.snapshot()
                XCTAssertEqual(secondInv.reconnectOwnerEnterCount, 2,
                               "exactly two reconnect owners must have fired (type-7 + recv-error); observed \(secondInv.reconnectOwnerEnterCount)")
                XCTAssertEqual(secondInv.reconnectOwnerExitCount, 1,
                               "the first (type-7) reconnect owner must have exited on success; the second (recv-error) is currently parked; observed exit=\(secondInv.reconnectOwnerExitCount)")
                XCTAssertEqual(secondInv.transportEnterCount, 2,
                               "exactly two transports (mockA initial + mockB replacement); observed \(secondInv.transportEnterCount)")
                XCTAssertEqual(secondInv.transportExitCount, 2,
                               "both transports must be torn down by the current handoff-mid snapshot; observed \(secondInv.transportExitCount)")
                XCTAssertEqual(secondInv.receiveLoopEnterCount, 2,
                               "exactly two receive loops (mockA + mockB); observed \(secondInv.receiveLoopEnterCount)")
                XCTAssertEqual(secondInv.receiveLoopExitCount, 2,
                               "both receive loops must have exited; observed \(secondInv.receiveLoopExitCount)")
                XCTAssertEqual(secondInv.maxReconnectOwners, 1,
                               "max concurrent reconnect owners must be exactly 1 (no duplicate owner ever coexisted); observed \(secondInv.maxReconnectOwners)")
                XCTAssertEqual(secondInv.maxTransports, 1,
                               "max concurrent transports must be exactly 1 (no duplicate socket); observed \(secondInv.maxTransports)")
                XCTAssertEqual(secondInv.maxReceiveLoops, 1,
                               "max concurrent receive loops must be exactly 1; observed \(secondInv.maxReceiveLoops)")

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
        XCTAssertTrue(didComplete, "Type-7 + immediate receive-error interleaving must complete within 15s")
    }

    /// r12 blocker #4 proof: repeated failed reconnects reach the
    /// bounded deterministic terminal (`reconnectAttempt >= 10 →
    /// publish `.disconnected`, stop retrying`) required by #777 —
    /// **without** the test manually disconnecting to end the loop.
    ///
    /// A driver task auto-releases the sleeper on every enrollment
    /// so the retry loop can advance under always-failing negotiate.
    /// The pass criterion is a positive completion event: a
    /// one-shot terminal latch fires only after `.reconnecting` was
    /// observed AND then `.disconnected` published again, plus
    /// `activeReconnectOwners == 0` when the owner task exits.
    func testRealService_boundedTerminalAfterMaxReconnectAttempts_publishesDisconnected() async throws {
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
            reconnectBackoff: { _ in 0.001 }, // ignored, sleeper is injected
            reconnectSleeper: sleeper.makeSleeper()
        )

        let recorder = LifecycleStateObserver()
        let sub = recordStates(service, into: recorder)
        _ = sub

        // Terminal latch: fires on the FIRST `.disconnected`
        // observed AFTER `.reconnecting` has been observed. The
        // recorder's initial `.disconnected` therefore does not
        // trigger the latch — only the bounded-terminal publish
        // from `giveUp` (which necessarily follows a `.reconnecting`
        // transition) does.
        let terminalLatch = TerminalDisconnectLatch()
        let latchSub = service.onConnectionStateChanged { state in
            terminalLatch.observe(state)
        }.1
        _ = latchSub

        let didComplete: Bool = await withTaskGroup(of: Bool.self) { group in
            // Driver: on every new sleep enrollment, immediately
            // release it so the retry loop advances. Terminates
            // when the enclosing group cancels this task.
            group.addTask {
                while !Task.isCancelled {
                    await sleeper.waitForNextSleep()
                    if Task.isCancelled { return true }
                    await sleeper.release()
                }
                return true
            }
            group.addTask {
                do {
                    try await service.connect()
                    XCTFail("connect() should have thrown on 500 negotiate")
                } catch {}
                // Wait for the bounded terminal via the latch: this
                // is the positive completion event when giveUp
                // publishes `.disconnected` after
                // `reconnectAttempt >= 10`.
                await terminalLatch.waitForTerminal()
                // Wait for the reconnect owner Task to actually
                // exit — this is the positive completion event.
                await service.lifecycleInvariants.waitForReconnectOwnersZero()
                return true
            }
            group.addTask {
                // Failure ceiling only; never the pass path.
                try? await Task.sleep(nanoseconds: 30 * 1_000_000_000)
                return false
            }
            let first = await group.next() ?? false
            group.cancelAll()
            return first
        }
        XCTAssertTrue(didComplete, "Bounded terminal must be reached deterministically without wall-clock idle")

        let attempts = counter.value()
        // Initial connect + up to 10 retries = 11 negotiate calls.
        // The exact upper bound is the production cap; we assert
        // at least 11 because the loop must have exhausted its cap.
        XCTAssertGreaterThanOrEqual(attempts, 11,
                                    "loop must reach the bounded terminal after >= 10 retries; observed \(attempts) negotiate calls")

        // r13 (Hicks item 3): explicit terminal-branch proof. Prove
        // that the pass path took the giveUp branch — i.e. we
        // observed `.reconnecting` at least once AND then a terminal
        // `.disconnected` fired via the latch. The latch itself
        // enforces the ordering (only fires on `.disconnected` after
        // `.reconnecting`), so `hasFired()` here is the causal
        // completion signal for the giveUp branch, not merely any
        // `.disconnected` publish.
        XCTAssertTrue(terminalLatch.hasFired(),
                      "terminal latch must have fired — giveUp branch's `.disconnected` publish is the causal completion event")

        let inv = service.lifecycleInvariants.snapshot()
        XCTAssertEqual(inv.activeReconnectOwners, 0,
                       "no reconnect owner may remain live after bounded terminal; observed \(inv.activeReconnectOwners)")
        XCTAssertEqual(inv.activeTransports, 0,
                       "no transport may remain live after bounded terminal; observed \(inv.activeTransports)")
        XCTAssertEqual(inv.activeReceiveLoops, 0,
                       "no receive loop may remain live after bounded terminal; observed \(inv.activeReceiveLoops)")
        // r15 (Hicks item 2): single sequential retry-owner. The
        // owner Task enters ONCE per retry chain regardless of how
        // many attempts iterated. Non-vacuity is proved by
        // `reconnectAttemptCount` (see item 4/8) not by
        // `reconnectOwnerEnterCount`.
        XCTAssertEqual(inv.reconnectOwnerEnterCount, 1,
                       "bounded retry chain must have entered the reconnect-owner Task exactly ONCE (r15 single-loop design); observed \(inv.reconnectOwnerEnterCount)")
        XCTAssertEqual(inv.reconnectOwnerEnterCount, inv.reconnectOwnerExitCount,
                       "every entered reconnect-owner Task must have exited via its `defer`; observed enter=\(inv.reconnectOwnerEnterCount) exit=\(inv.reconnectOwnerExitCount)")
        // r15 (Hicks item 4/8): non-vacuity witness — the retry LOOP
        // actually iterated >= 10 times. This is the causal proof
        // that the terminal branch was reached, not merely a
        // one-shot exit.
        XCTAssertGreaterThanOrEqual(inv.reconnectAttemptCount, 10,
                                    "bounded loop must have recorded >= 10 retry attempts; observed \(inv.reconnectAttemptCount)")
        XCTAssertLessThanOrEqual(inv.maxReconnectOwners, 1,
                                "bounded retry chain: max concurrent reconnect owners must be <= 1. r15 single-loop retry-owner design guarantees exactly one enterReconnectOwner/exitReconnectOwner pair per chain. Observed \(inv.maxReconnectOwners).")
        // r15 (Hicks item 4): balanced enter==exit across transport
        // and receive-loop families as well.
        XCTAssertEqual(inv.transportEnterCount, inv.transportExitCount,
                       "transport enter/exit must be balanced; observed enter=\(inv.transportEnterCount) exit=\(inv.transportExitCount)")
        XCTAssertEqual(inv.receiveLoopEnterCount, inv.receiveLoopExitCount,
                       "receive-loop enter/exit must be balanced; observed enter=\(inv.receiveLoopEnterCount) exit=\(inv.receiveLoopExitCount)")
        XCTAssertLessThanOrEqual(inv.maxTransports, 1,
                                "max concurrent transports must be <= 1; observed \(inv.maxTransports)")

        let states = recorder.snapshot()
        XCTAssertTrue(states.contains(.reconnecting),
                      "terminal branch must have passed through .reconnecting; observed \(states)")
        XCTAssertFalse(states.contains(.connected),
                       ".connected must never publish under always-failing negotiate; observed \(states)")
        XCTAssertEqual(states.last, .disconnected,
                       "final state must be .disconnected via giveUp branch; observed \(states)")
    }

    /// r12 blocker #6d proof: two `SignalRService` instances have
    /// independent lifecycle counters, transports, receive loops,
    /// and per-instance connection-state subscribers. Activity on
    /// service A must not surface as counter increments, callback
    /// invocations, or state transitions on service B.
    func testTwoServiceInstances_haveIndependentLifecycleCountersAndCallbacks() async throws {
        MockURLProtocol.requestHandler = { request in
            let payload: [String: Any] = [
                "connectionId": "conn-\(UUID().uuidString)",
                "connectionToken": "tok-\(UUID().uuidString)",
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

        let mockA = MockSignalRWebSocket()
        let mockB = MockSignalRWebSocket()
        let sleeperA = LifecycleControlledSleeper()
        let sleeperB = LifecycleControlledSleeper()
        let serviceA = SignalRService(
            serverURL: URL(string: "https://a.test.invalid")!,
            session: session,
            tokenProvider: { nil },
            reconnectBackoff: { _ in 1.0 },
            reconnectSleeper: sleeperA.makeSleeper(),
            webSocketFactory: { _ in mockA }
        )
        let serviceB = SignalRService(
            serverURL: URL(string: "https://b.test.invalid")!,
            session: session,
            tokenProvider: { nil },
            reconnectBackoff: { _ in 1.0 },
            reconnectSleeper: sleeperB.makeSleeper(),
            webSocketFactory: { _ in mockB }
        )

        // Independent recorders proved by identity: appending to A's
        // must not affect B's snapshot.
        let recorderA = LifecycleStateObserver()
        let recorderB = LifecycleStateObserver()
        let subA = recordStates(serviceA, into: recorderA)
        let subB = recordStates(serviceB, into: recorderB)
        _ = (subA, subB)

        let didComplete: Bool = await withTaskGroup(of: Bool.self) { group in
            group.addTask {
                let connectTask = Task { try? await serviceA.connect() }
                await mockA.waitForReceiveCall()
                mockA.completeReceive(with: .string("{}\u{1e}"))
                _ = await connectTask.value
                await recorderA.waitFor(state: .connected)
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
        XCTAssertTrue(didComplete, "Service A connect must complete within 15s")

        // Service A: counters must show activity.
        let invA = serviceA.lifecycleInvariants.snapshot()
        XCTAssertGreaterThanOrEqual(invA.transportEnterCount, 1,
                                    "service A transport counter must have fired; observed \(invA.transportEnterCount)")
        XCTAssertGreaterThanOrEqual(invA.receiveLoopEnterCount, 1,
                                    "service A receive-loop counter must have fired; observed \(invA.receiveLoopEnterCount)")

        // Service B: independent counters — must be untouched.
        let invB = serviceB.lifecycleInvariants.snapshot()
        XCTAssertEqual(invB.activeTransports, 0, "service B must have no active transport")
        XCTAssertEqual(invB.activeReceiveLoops, 0, "service B must have no active receive loop")
        XCTAssertEqual(invB.activeReconnectOwners, 0, "service B must have no active reconnect owner")
        XCTAssertEqual(invB.transportEnterCount, 0,
                       "service B transport counter must be zero (A's activity must not surface on B); observed \(invB.transportEnterCount)")
        XCTAssertEqual(invB.receiveLoopEnterCount, 0,
                       "service B receive-loop counter must be zero; observed \(invB.receiveLoopEnterCount)")
        XCTAssertEqual(invB.reconnectOwnerEnterCount, 0,
                       "service B reconnect-owner counter must be zero; observed \(invB.reconnectOwnerEnterCount)")

        // Callback isolation: recorder B must not have observed any
        // transition induced by service A's connect.
        XCTAssertFalse(recorderB.snapshot().contains(.connected),
                       "service B's subscriber must NOT observe .connected from A's activity; observed \(recorderB.snapshot())")
        XCTAssertTrue(recorderA.snapshot().contains(.connected),
                      "service A's own subscriber must observe .connected; observed \(recorderA.snapshot())")

        // Independent teardown: disconnect A, B still untouched.
        await serviceA.disconnect()
        await recorderA.waitFor(state: .disconnected)
        await serviceA.lifecycleInvariants.waitForTransportsZero()
        let invBFinal = serviceB.lifecycleInvariants.snapshot()
        XCTAssertEqual(invBFinal.transportEnterCount, 0,
                       "service B counters must remain untouched after A's disconnect")
        XCTAssertFalse(recorderB.snapshot().contains(.disconnected) && recorderB.snapshot().count > 1,
                       "service B recorder must not observe A's disconnect transition")

        // Cleanly release any parked sleepers so no dangling
        // continuations leak into the next test.
        await sleeperA.release()
        await sleeperB.release()
    }

    // MARK: - r12 blocker #2 direct sleeper-primitive proof

    /// r12 direct proof: `waitForSleepAfter(baseline:)` returns as
    /// soon as a new `sleep(for:)` enrolls past `baseline`. Uses a
    /// bounded outer task-group timeout only as a FAILURE ceiling;
    /// the pass path is a positive completion event, not the
    /// elapsed-time observation the removed `waitForNewSleep`
    /// helper relied on. If the primitive regressed to hang, the
    /// 5s ceiling would fail the assertion — never the pass path.
    func testLifecycleControlledSleeper_waitForSleepAfter_wakesOnEnrollment() async throws {
        let sleeper = LifecycleControlledSleeper()
        let baseline = await sleeper.totalSleepEntries()

        let observed: Bool = await withTaskGroup(of: Bool.self) { group in
            group.addTask {
                await sleeper.waitForSleepAfter(baseline: baseline)
                return true
            }
            group.addTask {
                await sleeper.sleep(for: 0)
                return true
            }
            group.addTask {
                // Failure ceiling only; never the pass path.
                try? await Task.sleep(nanoseconds: 5_000_000_000)
                return false
            }
            let first = await group.next() ?? false
            // Release the parked sleeper so the "sleep child" and
            // any pending waiters exit cleanly before we tear down
            // the group.
            await sleeper.release()
            group.cancelAll()
            return first
        }

        XCTAssertTrue(observed, "waitForSleepAfter must wake on a new sleep enrollment (positive completion, not elapsed-time absence)")
    }
}

/// r15 (Hicks items 5, 6, 7): deterministic pre-enrollment
/// cancellation, caller-specific mock-receive cancellation, and
/// owner-teardown/reconfiguration proofs.
///
/// These tests prove:
///   * Every quiescence waiter (`waitForTransportsZero`,
///     `waitForReceiveLoopsZero`, `waitForReconnectOwnersZero`) and
///     `TerminalDisconnectLatch.waitForTerminal()` cannot leak/hang
///     when its Task is cancelled at/before enrollment. r15 adds
///     explicit per-family pre-enrollment barrier hooks
///     (`setTransportPreEnrollBarrier` etc.) so the test can park
///     the waiter deterministically at the pre-enroll boundary,
///     cancel, then release — proving no continuation was enqueued
///     (via `waiterCounts()`).
///   * Cancellation of one waiter family cannot resume a waiter of
///     another family (per-family barriers + independent counters).
///   * `MockSignalRWebSocket.receive()` cancellation is
///     caller-specific: each call keyed by UUID, cancellation
///     removes and resumes only that caller. Deterministic
///     enrollment ACK via `waitForReceiveEnrollments(count:)`
///     replaces the FIFO-first / aggregate-call-count ambiguity of
///     `waitForReceiveCall()`.
///   * Owner teardown after `disconnect()` prevents delivery of any
///     later frame; reconfigure (disconnect + fresh connect) does
///     not stack or leak subscription registrations.
///
/// Timing is used ONLY as failure ceilings.
final class SignalRPreEnrollmentCancellationRaceTests: XCTestCase {

    /// r15 (Hicks item 5): deterministic barrier-based proof.
    /// Install a barrier that fires the moment a waiter reaches the
    /// pre-enroll boundary. The test arms the barrier, spawns a
    /// waiter Task, awaits "arrived" confirmation via the barrier's
    /// signal, cancels the Task, releases the gate, then asserts:
    ///   - the waiter Task returned (no hang),
    ///   - `waiterCounts().receiveLoops == 0` (no continuation was
    ///     enqueued after cancellation).
    /// Failure ceiling: 5s outer race.
    func testInvariantsWaiter_receiveLoops_preCancelledAtBarrier_doesNotEnqueue() async throws {
        let inv = SignalRLifecycleInvariants()
        inv.enterReceiveLoop() // Force counter non-zero so a non-cancelled waiter would park.

        // Barrier signal: fires once, when the waiter reaches the
        // pre-enroll boundary. Test awaits this to know cancellation
        // is being applied at the right moment.
        let arrived = LifecycleSignal()
        let gate = LifecycleSignal()
        inv.setReceiveLoopPreEnrollBarrier {
            arrived.signal()
            await gate.wait()
        }

        let t = Task<Bool, Never> {
            await inv.waitForReceiveLoopsZero()
            return true
        }
        // Wait until the waiter is parked at the pre-enroll barrier.
        await arrived.wait()
        t.cancel()
        gate.signal() // Release the barrier so enrollment path runs.

        let didComplete = await raceComplete(seconds: 5) { await t.value }
        XCTAssertTrue(didComplete, "waitForReceiveLoopsZero must not hang when cancelled at the pre-enroll barrier")
        let counts = inv.waiterCounts()
        XCTAssertEqual(counts.receiveLoops, 0,
                       "cancelled task must not have enqueued a continuation; observed receiveLoops waiters=\(counts.receiveLoops)")
        inv.setReceiveLoopPreEnrollBarrier(nil)
        inv.exitReceiveLoop()
    }

    /// r15 (Hicks item 5): per-family isolation. Cancelling a
    /// receiveLoops waiter must NOT resume a parked transports
    /// waiter. Tests independence of family-specific barriers /
    /// continuation lists.
    func testInvariantsWaiters_perFamily_cancelOneDoesNotResumeAnother() async throws {
        let inv = SignalRLifecycleInvariants()
        inv.enterReceiveLoop()
        inv.enterTransport()

        // Spawn waiterB on transports family; it will park because
        // active transports > 0.
        let waiterB = Task<Bool, Never> {
            await inv.waitForTransportsZero()
            return true
        }

        // Deterministically wait until waiterB's enrollment is
        // observable via `waiterCounts().transports >= 1`. Bounded
        // failure ceiling; the pass path is a positive observation
        // of the enrolled continuation count (not a wall-clock
        // idle-window).
        let deadline = Date().addingTimeInterval(5.0)
        while inv.waiterCounts().transports < 1 {
            if Date() >= deadline {
                XCTFail("waiterB failed to enroll on transports family within 5s")
                inv.exitReceiveLoop(); inv.exitTransport()
                waiterB.cancel(); _ = await waiterB.value
                return
            }
            try? await Task.sleep(nanoseconds: 1_000_000) // 1ms poll, failure ceiling only
        }

        // Spawn waiterA on a DIFFERENT family and cancel it.
        let waiterA = Task<Bool, Never> {
            await inv.waitForReceiveLoopsZero()
            return true
        }
        waiterA.cancel()
        _ = await waiterA.value

        // waiterB must still be parked on transports (its family was
        // never disturbed by waiterA's cancellation).
        XCTAssertGreaterThanOrEqual(inv.waiterCounts().transports, 1,
                                    "cancelling a receiveLoops waiter must not resume transports waiters; observed transports waiters=\(inv.waiterCounts().transports)")
        XCTAssertFalse(waiterB.isCancelled,
                       "waiterB Task must not have been touched by the receiveLoops-family cancellation")

        // Release transports family and prove waiterB wakes.
        inv.exitTransport()
        _ = await waiterB.value

        inv.exitReceiveLoop()
    }

    /// r15 (Hicks item 5): same barrier-based race applied to
    /// `TerminalDisconnectLatch.waitForTerminal()`.
    func testTerminalDisconnectLatch_preCancelledTask_doesNotHang() async throws {
        let latch = TerminalDisconnectLatch()

        let t = Task<Bool, Never> {
            await latch.waitForTerminal()
            return true
        }
        t.cancel()

        let didComplete = await raceComplete(seconds: 5) { await t.value }
        XCTAssertTrue(didComplete, "TerminalDisconnectLatch.waitForTerminal() must not hang when its Task was cancelled at/before enrollment")
        XCTAssertFalse(latch.hasFired(), "Cancellation must not synthesise a terminal-branch observation")
    }

    /// r15 (Hicks item 6): caller-specific mock-receive cancellation
    /// with deterministic enrollment ACK. Enrolls B, then A, cancels
    /// A specifically, asserts:
    ///   - A observes its own CancellationError,
    ///   - `pendingReceiveCount() == 1` (only B remains),
    ///   - `completeReceive` delivers to B.
    /// No FIFO-first / global-call-count ambiguity.
    func testMockReceive_cancelIsCallerSpecific_withEnrollmentACK() async throws {
        let mock = MockSignalRWebSocket()

        // B first — must survive A's cancellation.
        let receiverB = Task<URLSessionWebSocketTask.Message, Error> {
            try await mock.receive()
        }
        await mock.waitForReceiveEnrollments(count: 1)

        // A: doomed receiver.
        let receiverA = Task<Bool, Never> {
            do {
                _ = try await mock.receive()
                return false
            } catch is CancellationError {
                return true
            } catch {
                return false
            }
        }
        await mock.waitForReceiveEnrollments(count: 2)

        // Deterministic: exactly two enrollments observed. Cancel A.
        receiverA.cancel()
        let aWasCancelled = await raceComplete(seconds: 5) { await receiverA.value }
        XCTAssertTrue(aWasCancelled, "Caller-A must observe its own CancellationError")

        // Only B remains parked.
        XCTAssertEqual(mock.pendingReceiveCount(), 1,
                       "exactly one pending receive must remain (B); observed \(mock.pendingReceiveCount())")

        // Deliver to B; verify.
        mock.completeReceive(with: .string("delivered-to-B"))
        let receivedByB = try await receiverB.value
        switch receivedByB {
        case .string(let s):
            XCTAssertEqual(s, "delivered-to-B",
                           "completeReceive must deliver to the surviving receiver B, not to A's cancelled slot")
        case .data:
            XCTFail("Expected string frame")
        @unknown default:
            XCTFail("Unexpected message case")
        }
    }

    /// r15 (Hicks item 7): owner teardown proof — after a
    /// subscription is cancelled (the owner-teardown analogue at the
    /// hub level), no subsequent publish reaches it, and a fresh
    /// subscription registered afterwards fires exactly once per
    /// publish (no stacking / no leaked registrations).
    @MainActor
    func testOwnerTeardown_afterSubscriptionCancel_noFurtherCallbacksAndNoStacking() async throws {
        let mock = MockSignalRService()
        let seenA = LockedStringSequence()
        let seenB = LockedStringSequence()

        let (_, subA) = mock.onConnectionStateChanged { state in
            seenA.append("A:\(state)")
        }
        mock.simulateConnectionStateChange(.connected)
        subA.cancel()
        // Post-cancel publish: A must NOT observe it.
        mock.simulateConnectionStateChange(.disconnected)

        let snapA = seenA.snapshot()
        XCTAssertEqual(snapA, ["A:connected"],
                       "cancelled subscription must not receive post-cancel events; observed \(snapA)")

        // Reconfigure: fresh subscription B, publish, verify exactly
        // one delivery (no stacking with cancelled A).
        let (_, subB) = mock.onConnectionStateChanged { state in
            seenB.append("B:\(state)")
        }
        withExtendedLifetime(subB) {
            mock.simulateConnectionStateChange(.reconnecting)
        }
        let snapB = seenB.snapshot()
        XCTAssertEqual(snapB, ["B:reconnecting"],
                       "fresh subscription must fire exactly once per publish (no stacking); observed \(snapB)")
        let snapAAfter = seenA.snapshot()
        XCTAssertEqual(snapAAfter, ["A:connected"],
                       "cancelled subscription A must remain untouched by post-reconfigure publishes; observed \(snapAAfter)")
    }
}

/// r15 helper: deterministic one-shot signal for pre-enroll barrier
/// tests. `signal()` unblocks any current or future `wait()`.
final class LifecycleSignal: @unchecked Sendable {
    private let lock = NSLock()
    private var fired = false
    private var waiters: [CheckedContinuation<Void, Never>] = []

    func signal() {
        lock.lock()
        let toWake = fired ? [] : waiters
        if !fired {
            fired = true
            waiters.removeAll()
        }
        lock.unlock()
        for w in toWake { w.resume() }
    }

    func wait() async {
        if _isFired() { return }
        await withCheckedContinuation { (cont: CheckedContinuation<Void, Never>) in
            _enqueueWaiter(cont: cont)
        }
    }

    private func _isFired() -> Bool {
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

/// r15 helper: race a work closure against a wall-clock ceiling.
/// Returns `true` if the work completed within `seconds`.
private func raceComplete(seconds: UInt64, work: @Sendable @escaping () async -> Bool) async -> Bool {
    await withTaskGroup(of: Bool.self) { group in
        group.addTask { await work() }
        group.addTask {
            do { try await Task.sleep(nanoseconds: seconds * 1_000_000_000); return false }
            catch { return false }
        }
        let first = await group.next() ?? false
        group.cancelAll()
        return first
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
