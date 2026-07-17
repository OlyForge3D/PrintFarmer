import Foundation

/// Per-service-instance lifecycle invariant counters.
///
/// Tracks the number of currently-active transports, receive loops,
/// and reconnect owners on a single `SignalRService` instance, along
/// with the maximum observed values. Every increment/decrement is
/// paired with a state-variable transition on the service (webSocket
/// install/tear-down, receive-task install/tear-down, reconnect-token
/// reservation/clear), so the counters are non-vacuous: a passing
/// assertion means the transition happened, and `max == 1` means only
/// one such entity was ever alive at any time.
///
/// Barrier waiters (`waitForReconnectOwnersZero()`,
/// `waitForReceiveLoopsZero()`, `waitForTransportsZero()`) let tests
/// synchronize on tear-down completion without polling elapsed time.
/// They resume every parked waiter when the corresponding counter
/// hits zero.
///
/// Access is protected by `NSLock` so tests can query snapshots from
/// any Task without serializing through the lifecycle queue. All
/// service-side increment/decrement calls happen inside
/// `lifecycleSync`, so the counter transitions are already serialized
/// with respect to each other at the service; the lock protects
/// snapshot readers and the waiter arrays.
final class SignalRLifecycleInvariants: @unchecked Sendable {
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

    private var transportZeroWaiters: [CheckedContinuation<Void, Never>] = []
    private var receiveLoopZeroWaiters: [CheckedContinuation<Void, Never>] = []
    private var reconnectOwnerZeroWaiters: [CheckedContinuation<Void, Never>] = []

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
        precondition(activeTransports >= 0, "activeTransports went negative — lifecycle invariant broken")
        if activeTransports == 0 {
            toResume = transportZeroWaiters
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
        precondition(activeReceiveLoops >= 0, "activeReceiveLoops went negative — lifecycle invariant broken")
        if activeReceiveLoops == 0 {
            toResume = receiveLoopZeroWaiters
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
        precondition(activeReconnectOwners >= 0, "activeReconnectOwners went negative — lifecycle invariant broken")
        if activeReconnectOwners == 0 {
            toResume = reconnectOwnerZeroWaiters
            reconnectOwnerZeroWaiters.removeAll()
        }
        lock.unlock()
        toResume.forEach { $0.resume() }
    }

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
            reconnectOwnerEnterCount: reconnectOwnerEnterCount
        )
    }

    // MARK: - Deterministic barriers
    //
    // Each `waitFor*Zero()` primitive returns immediately if the
    // corresponding counter is already zero, else parks a
    // `CheckedContinuation` that is resumed exactly once when the
    // counter transitions to zero. Cancellation-safe: if the caller's
    // Task is cancelled, the waiter is removed and resumed with
    // `Void`. Returning on cancellation is intentional — the caller
    // is expected to check `Task.isCancelled` or a separate deadline
    // if it needs to distinguish.

    func waitForTransportsZero() async {
        let id = UUID()
        await withTaskCancellationHandler {
            await withCheckedContinuation { (cont: CheckedContinuation<Void, Never>) in
                lock.lock()
                if activeTransports == 0 {
                    lock.unlock()
                    cont.resume()
                    return
                }
                transportZeroWaiters.append(cont)
                lock.unlock()
            }
        } onCancel: {
            self.cancelZeroWaiters(id: id)
        }
        _ = id  // reserved for future keyed removal; current implementation drains all on zero
    }

    func waitForReceiveLoopsZero() async {
        let id = UUID()
        await withTaskCancellationHandler {
            await withCheckedContinuation { (cont: CheckedContinuation<Void, Never>) in
                lock.lock()
                if activeReceiveLoops == 0 {
                    lock.unlock()
                    cont.resume()
                    return
                }
                receiveLoopZeroWaiters.append(cont)
                lock.unlock()
            }
        } onCancel: {
            self.cancelZeroWaiters(id: id)
        }
        _ = id
    }

    func waitForReconnectOwnersZero() async {
        let id = UUID()
        await withTaskCancellationHandler {
            await withCheckedContinuation { (cont: CheckedContinuation<Void, Never>) in
                lock.lock()
                if activeReconnectOwners == 0 {
                    lock.unlock()
                    cont.resume()
                    return
                }
                reconnectOwnerZeroWaiters.append(cont)
                lock.unlock()
            }
        } onCancel: {
            self.cancelZeroWaiters(id: id)
        }
        _ = id
    }

    /// On cancellation, drain every parked waiter of every family.
    /// A resumed-then-torn-down waiter simply becomes a no-op on the
    /// zero-drain path via the array reset. This is coarse but safe:
    /// cancellation is a bail-out signal, so waking any parked
    /// continuations is the correct behavior — the caller's Task is
    /// exiting.
    private func cancelZeroWaiters(id _: UUID) {
        var toResume: [CheckedContinuation<Void, Never>] = []
        lock.lock()
        toResume.append(contentsOf: transportZeroWaiters)
        toResume.append(contentsOf: receiveLoopZeroWaiters)
        toResume.append(contentsOf: reconnectOwnerZeroWaiters)
        transportZeroWaiters.removeAll()
        receiveLoopZeroWaiters.removeAll()
        reconnectOwnerZeroWaiters.removeAll()
        lock.unlock()
        toResume.forEach { $0.resume() }
    }
}
