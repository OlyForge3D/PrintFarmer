import Foundation

/// Per-service-instance lifecycle invariant counters.
///
/// **Task-lifetime instrumentation (not slot occupancy).**
/// `enter*`/`exit*` MUST be called from inside the actual body of the
/// receive loop / reconnect owner Task (enter at the top, exit in a
/// `defer` at the end). The transport counter is tied to the running
/// receive loop's lifetime — an "active transport" is one whose
/// receive loop is actually executing. This makes `max <= 1`
/// falsifiable: if a supersede ever fails to fully tear down the
/// outgoing task before the replacement's body begins running, the
/// counter will observe both alive simultaneously and `max >= 2`.
///
/// A previous version tied enter/exit to optional-slot occupancy
/// inside the lifecycle-queue setter. Because the setter's
/// nonnil→nonnil replacement path is `exit` then `enter` on the same
/// serialized queue, `active` was structurally capped at 1 and
/// `max <= 1` was vacuous. The lifetime model here counts concurrent
/// running tasks and cannot be circumvented that way.
///
/// Barrier waiters (`waitForReconnectOwnersZero()`,
/// `waitForReceiveLoopsZero()`, `waitForTransportsZero()`) let tests
/// synchronize on task-completion (not slot-clearing) without polling
/// elapsed time. They resume every parked waiter of THAT family when
/// its counter hits zero. Cancellation only wakes waiters belonging
/// to the cancelled Task, and only within their own family — a
/// cancelled transport waiter does not resume a parked receive-loop
/// or reconnect-owner waiter.
///
/// Access is protected by `NSLock` so tests can query snapshots from
/// any Task without serializing through the lifecycle queue.
final class SignalRLifecycleInvariants: @unchecked Sendable {
#if DEBUG
    enum DebugZeroWaiterFamily: Hashable, Sendable {
        case transport
        case receiveLoop
        case reconnectOwner
    }
#endif

    struct Snapshot: Equatable, Sendable {
        let activeTransports: Int
        let activeReceiveLoops: Int
        let activeReconnectOwners: Int
        let maxTransports: Int
        let maxReceiveLoops: Int
        let maxReconnectOwners: Int
        let transportEnterCount: Int
        let receiveLoopEnterCount: Int
        let reconnectOwnerEnterCount: Int
        let transportExitCount: Int
        let receiveLoopExitCount: Int
        let reconnectOwnerExitCount: Int
        /// r15 (Hicks item 4/8): number of retry attempts issued
        /// INSIDE the (single) reconnect-owner loop. This is the
        /// non-vacuity witness for retry-chain tests: with a single
        /// sequential retry-owner, `reconnectOwnerEnterCount == 1`
        /// but `reconnectAttemptCount` grows with each retry. Production
        /// runs the ladder unbounded; tests inject a finite
        /// `maxReconnectAttempts` cap to reach the terminal branch. Every
        /// attempt is bracketed by a `recordReconnectAttempt()` call at
        /// the top of the retry iteration.
        let reconnectAttemptCount: Int
    }

    private let lock = NSLock()

    private var activeTransports = 0
    private var activeReceiveLoops = 0
    private var activeReconnectOwners = 0

    private var maxTransports = 0
    private var maxReceiveLoops = 0
    private var maxReconnectOwners = 0

    private var transportEnterCount = 0
    private var receiveLoopEnterCount = 0
    private var reconnectOwnerEnterCount = 0

    private var transportZeroWaiters: [(id: UUID, cont: CheckedContinuation<Void, Never>)] = []
    private var receiveLoopZeroWaiters: [(id: UUID, cont: CheckedContinuation<Void, Never>)] = []
    private var reconnectOwnerZeroWaiters: [(id: UUID, cont: CheckedContinuation<Void, Never>)] = []

    private var transportExitCount = 0
    private var receiveLoopExitCount = 0
    private var reconnectOwnerExitCount = 0

    private var reconnectAttemptCount = 0

#if DEBUG
    private var debugZeroWaiterEnrollmentCallback:
        (@Sendable (DebugZeroWaiterFamily, UUID) -> Void)?
#endif

    // r15 (Hicks item 5): deterministic pre-enrollment barrier hooks.
    // Tests set these to controlled `@Sendable` closures. Each
    // `waitFor*Zero()` awaits its family's barrier BEFORE taking the
    // enrollment lock; the test's barrier resolves an "arrived at
    // pre-enroll" signal and then awaits a release. This lets tests
    // cancel the waiter Task while it is deterministically parked at
    // the pre-enrollment point, prove no continuation was ever
    // enqueued (family's waiter list stays empty AFTER the release),
    // and prove no hang. Barriers are per-family so a cancel or
    // resume in one family CANNOT resume waiters in another.
    private var transportPreEnrollBarrier: (@Sendable () async -> Void)?
    private var receiveLoopPreEnrollBarrier: (@Sendable () async -> Void)?
    private var reconnectOwnerPreEnrollBarrier: (@Sendable () async -> Void)?

    // MARK: - Transport counters

    func enterTransport() {
        lock.lock()
        activeTransports += 1
        transportEnterCount += 1
        if activeTransports > maxTransports { maxTransports = activeTransports }
        lock.unlock()
    }

    func exitTransport() {
        var toResume: [CheckedContinuation<Void, Never>] = []
        lock.lock()
        activeTransports -= 1
        transportExitCount += 1
        precondition(activeTransports >= 0, "activeTransports went negative — lifecycle invariant broken")
        if activeTransports == 0 {
            toResume = transportZeroWaiters.map { $0.cont }
            transportZeroWaiters.removeAll()
        }
        lock.unlock()
        toResume.forEach { $0.resume() }
    }

    // MARK: - Receive-loop counters

    func enterReceiveLoop() {
        lock.lock()
        activeReceiveLoops += 1
        receiveLoopEnterCount += 1
        if activeReceiveLoops > maxReceiveLoops { maxReceiveLoops = activeReceiveLoops }
        lock.unlock()
    }

    func exitReceiveLoop() {
        var toResume: [CheckedContinuation<Void, Never>] = []
        lock.lock()
        activeReceiveLoops -= 1
        receiveLoopExitCount += 1
        precondition(activeReceiveLoops >= 0, "activeReceiveLoops went negative — lifecycle invariant broken")
        if activeReceiveLoops == 0 {
            toResume = receiveLoopZeroWaiters.map { $0.cont }
            receiveLoopZeroWaiters.removeAll()
        }
        lock.unlock()
        toResume.forEach { $0.resume() }
    }

    // MARK: - Reconnect-owner counters

    func enterReconnectOwner() {
        lock.lock()
        activeReconnectOwners += 1
        reconnectOwnerEnterCount += 1
        if activeReconnectOwners > maxReconnectOwners { maxReconnectOwners = activeReconnectOwners }
        lock.unlock()
    }

    func exitReconnectOwner() {
        var toResume: [CheckedContinuation<Void, Never>] = []
        lock.lock()
        activeReconnectOwners -= 1
        reconnectOwnerExitCount += 1
        precondition(activeReconnectOwners >= 0, "activeReconnectOwners went negative — lifecycle invariant broken")
        if activeReconnectOwners == 0 {
            toResume = reconnectOwnerZeroWaiters.map { $0.cont }
            reconnectOwnerZeroWaiters.removeAll()
        }
        lock.unlock()
        toResume.forEach { $0.resume() }
    }

    /// r15 (Hicks item 4/8): record one retry ATTEMPT inside the single
    /// sequential reconnect-owner loop. Distinct from
    /// `enterReconnectOwner()`, which is called ONCE per retry chain.
    /// Together the two make the retry-chain proof non-vacuous:
    /// `reconnectOwnerEnterCount == 1` guarantees no overlap (structural),
    /// `reconnectAttemptCount == 10` (bounded terminal, via an injected
    /// `maxReconnectAttempts` cap) or `>= 3`
    /// (repeated-failed-reconnects) guarantees the retry loop actually
    /// iterated.
    func recordReconnectAttempt() {
        lock.lock()
        reconnectAttemptCount += 1
        lock.unlock()
    }

    // MARK: - Pre-enrollment barrier hooks (r15 Hicks item 5)

    func setTransportPreEnrollBarrier(_ b: (@Sendable () async -> Void)?) {
        lock.lock()
        transportPreEnrollBarrier = b
        lock.unlock()
    }

    func setReceiveLoopPreEnrollBarrier(_ b: (@Sendable () async -> Void)?) {
        lock.lock()
        receiveLoopPreEnrollBarrier = b
        lock.unlock()
    }

    func setReconnectOwnerPreEnrollBarrier(_ b: (@Sendable () async -> Void)?) {
        lock.lock()
        reconnectOwnerPreEnrollBarrier = b
        lock.unlock()
    }

    /// Test-only introspection: waiter counts per family. Used to
    /// prove pre-enrollment cancellation left no leaked continuation.
    func waiterCounts() -> (transports: Int, receiveLoops: Int, reconnectOwners: Int) {
        lock.lock()
        defer { lock.unlock() }
        return (transportZeroWaiters.count, receiveLoopZeroWaiters.count, reconnectOwnerZeroWaiters.count)
    }

#if DEBUG
    func setDebugZeroWaiterEnrollmentCallback(
        _ callback: @escaping @Sendable (DebugZeroWaiterFamily, UUID) -> Void
    ) {
        lock.lock()
        precondition(
            debugZeroWaiterEnrollmentCallback == nil,
            "Debug zero-waiter enrollment callback is already installed"
        )
        debugZeroWaiterEnrollmentCallback = callback
        lock.unlock()
    }

    func resetDebugZeroWaiterEnrollmentCallback() {
        lock.lock()
        debugZeroWaiterEnrollmentCallback = nil
        lock.unlock()
    }
#endif

    // MARK: - Snapshot

    func snapshot() -> Snapshot {
        lock.lock()
        defer { lock.unlock() }
        return Snapshot(
            activeTransports: activeTransports,
            activeReceiveLoops: activeReceiveLoops,
            activeReconnectOwners: activeReconnectOwners,
            maxTransports: maxTransports,
            maxReceiveLoops: maxReceiveLoops,
            maxReconnectOwners: maxReconnectOwners,
            transportEnterCount: transportEnterCount,
            receiveLoopEnterCount: receiveLoopEnterCount,
            reconnectOwnerEnterCount: reconnectOwnerEnterCount,
            transportExitCount: transportExitCount,
            receiveLoopExitCount: receiveLoopExitCount,
            reconnectOwnerExitCount: reconnectOwnerExitCount,
            reconnectAttemptCount: reconnectAttemptCount
        )
    }

    // MARK: - Deterministic barriers (task-completion, per family)
    //
    // Each `waitFor*Zero()` primitive returns immediately if the
    // corresponding counter is already zero, else parks a
    // `CheckedContinuation` keyed by a unique `UUID` in ITS family's
    // waiter list. When the family's counter transitions to zero
    // (via `exit*`, which is called from the actual task body's
    // `defer`), all parked waiters in THAT family are resumed. When
    // the caller's Task is cancelled, only that specific waiter is
    // removed from that specific family and resumed. Cross-family
    // cancellation cannot resume waiters in another family, so
    // "receive loops are quiescent" cannot be spuriously satisfied
    // because a reconnect-owner waiter's Task was cancelled.
    //
    // Pre-enrollment cancellation race:
    // `withTaskCancellationHandler`'s `onCancel` block may run BEFORE
    // the operation closure has had a chance to enqueue its
    // continuation (either because the calling Task was already
    // cancelled at handler registration, or because cancellation
    // wins the race with enrollment on another thread). If we
    // enqueued anyway, the `onCancel`'s UUID-keyed removal would
    // have already run against an empty waiter list, and the
    // waiter would then be enqueued with no future cancel handler
    // to resume it — leaking/hanging on the next `exit*` failing
    // to fire while the counter is non-zero.
    //
    // Fix: under the SAME lock as the enqueue, check
    // `Task.isCancelled` first. If cancelled at enrollment time,
    // resume immediately without enqueuing. The cancel handler
    // remains idempotent — finding no matching UUID is a no-op.

    func waitForTransportsZero() async {
        // r15 (Hicks item 5): deterministic pre-enroll barrier for
        // tests. Awaited BEFORE the enrollment lock so the caller Task
        // can be cancelled while parked at a known point; the post-
        // barrier `Task.isCancelled` check then returns without ever
        // enqueuing a waiter.
        let barrier: (@Sendable () async -> Void)? = {
            lock.lock(); defer { lock.unlock() }
            return transportPreEnrollBarrier
        }()
        if let barrier { await barrier() }
        let id = UUID()
        await withTaskCancellationHandler {
            await withCheckedContinuation { (cont: CheckedContinuation<Void, Never>) in
                lock.lock()
                if Task.isCancelled {
                    lock.unlock()
                    cont.resume()
                    return
                }
                if activeTransports == 0 {
                    lock.unlock()
                    cont.resume()
                    return
                }
                transportZeroWaiters.append((id: id, cont: cont))
#if DEBUG
                let enrollmentCallback = debugZeroWaiterEnrollmentCallback
#endif
                lock.unlock()
#if DEBUG
                enrollmentCallback?(.transport, id)
#endif
            }
        } onCancel: {
            self.cancelTransportWaiter(id: id)
        }
    }

    func waitForReceiveLoopsZero() async {
        let barrier: (@Sendable () async -> Void)? = {
            lock.lock(); defer { lock.unlock() }
            return receiveLoopPreEnrollBarrier
        }()
        if let barrier { await barrier() }
        let id = UUID()
        await withTaskCancellationHandler {
            await withCheckedContinuation { (cont: CheckedContinuation<Void, Never>) in
                lock.lock()
                if Task.isCancelled {
                    lock.unlock()
                    cont.resume()
                    return
                }
                if activeReceiveLoops == 0 {
                    lock.unlock()
                    cont.resume()
                    return
                }
                receiveLoopZeroWaiters.append((id: id, cont: cont))
#if DEBUG
                let enrollmentCallback = debugZeroWaiterEnrollmentCallback
#endif
                lock.unlock()
#if DEBUG
                enrollmentCallback?(.receiveLoop, id)
#endif
            }
        } onCancel: {
            self.cancelReceiveLoopWaiter(id: id)
        }
    }

    func waitForReconnectOwnersZero() async {
        let barrier: (@Sendable () async -> Void)? = {
            lock.lock(); defer { lock.unlock() }
            return reconnectOwnerPreEnrollBarrier
        }()
        if let barrier { await barrier() }
        let id = UUID()
        await withTaskCancellationHandler {
            await withCheckedContinuation { (cont: CheckedContinuation<Void, Never>) in
                lock.lock()
                if Task.isCancelled {
                    lock.unlock()
                    cont.resume()
                    return
                }
                if activeReconnectOwners == 0 {
                    lock.unlock()
                    cont.resume()
                    return
                }
                reconnectOwnerZeroWaiters.append((id: id, cont: cont))
#if DEBUG
                let enrollmentCallback = debugZeroWaiterEnrollmentCallback
#endif
                lock.unlock()
#if DEBUG
                enrollmentCallback?(.reconnectOwner, id)
#endif
            }
        } onCancel: {
            self.cancelReconnectOwnerWaiter(id: id)
        }
    }

    private func cancelTransportWaiter(id: UUID) {
        var toResume: CheckedContinuation<Void, Never>?
        lock.lock()
        if let idx = transportZeroWaiters.firstIndex(where: { $0.id == id }) {
            toResume = transportZeroWaiters.remove(at: idx).cont
        }
        lock.unlock()
        toResume?.resume()
    }

    private func cancelReceiveLoopWaiter(id: UUID) {
        var toResume: CheckedContinuation<Void, Never>?
        lock.lock()
        if let idx = receiveLoopZeroWaiters.firstIndex(where: { $0.id == id }) {
            toResume = receiveLoopZeroWaiters.remove(at: idx).cont
        }
        lock.unlock()
        toResume?.resume()
    }

    private func cancelReconnectOwnerWaiter(id: UUID) {
        var toResume: CheckedContinuation<Void, Never>?
        lock.lock()
        if let idx = reconnectOwnerZeroWaiters.firstIndex(where: { $0.id == id }) {
            toResume = reconnectOwnerZeroWaiters.remove(at: idx).cont
        }
        lock.unlock()
        toResume?.resume()
    }
}
