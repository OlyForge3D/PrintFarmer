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
            reconnectOwnerExitCount: reconnectOwnerExitCount
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
                transportZeroWaiters.append((id: id, cont: cont))
                lock.unlock()
            }
        } onCancel: {
            self.cancelTransportWaiter(id: id)
        }
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
                receiveLoopZeroWaiters.append((id: id, cont: cont))
                lock.unlock()
            }
        } onCancel: {
            self.cancelReceiveLoopWaiter(id: id)
        }
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
                reconnectOwnerZeroWaiters.append((id: id, cont: cont))
                lock.unlock()
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
