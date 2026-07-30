import Foundation

/// Deterministic ordering primitive for concurrent mock invocations.
///
/// Replaces `Task.sleep`-based ordering in the #709 coverage race regression
/// tests. Each parked handler awaits `wait(_:)` for its own call number; the
/// test drives ordering by calling `release(_:)` on those numbers in the exact
/// sequence it wants observed. Releases arriving before their matching wait are
/// remembered so the ordering is independent of scheduling of the mock and
/// caller. All state is guarded by the actor's own isolation — no locks, no
/// timing, no fixed sleeps.
actor CallOrderGate {
    private var pendingWaiters: [Int: CheckedContinuation<Void, Never>] = [:]
    private var pendingReleases: Set<Int> = []

    /// Suspend until the matching `release(call)` is invoked. If the release
    /// has already been issued, returns immediately.
    func wait(_ call: Int) async {
        if pendingReleases.remove(call) != nil { return }
        await withCheckedContinuation { continuation in
            pendingWaiters[call] = continuation
        }
    }

    /// Wake the handler currently parked on `wait(call)`. If no handler is
    /// parked yet, the release is buffered and consumed by the next matching
    /// `wait(call)`.
    func release(_ call: Int) {
        if let waiter = pendingWaiters.removeValue(forKey: call) {
            waiter.resume()
        } else {
            pendingReleases.insert(call)
        }
    }
}
