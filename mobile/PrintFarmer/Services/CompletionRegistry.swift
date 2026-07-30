import Foundation

// MARK: - CompletionRegistry

/// Generic exactly-once cancellable observer registry used by the SignalR /
/// coverage test seams. Solves the r3 continuation-lifecycle blocker AND the
/// r4 stale-replay blocker:
///
/// - `CheckedContinuation`/`AsyncStream.Continuation` must be resumed
///   exactly once. `register` installs a synchronous handle whose stream
///   `onTermination` hook is wired to synchronously remove the observer
///   entry, so every terminal path — success, timeout, explicit cancel,
///   registry teardown/deinit, or Task cancellation of the awaiter — leaves
///   the registry with zero outstanding observers.
///
/// - Replay of recently-resolved keys is **opt-in** at construction
///   (`enableReplayCache`). The default is OFF so callers whose keys are
///   stable identifiers (e.g. a printer UUID, where the SAME key can be
///   resolved for multiple distinct events across a VM's lifetime) cannot
///   falsely pre-ack a new event from a stale prior resolution. Callers
///   whose keys are unique-per-event (e.g. monotonic-integer generation
///   numbers) opt in explicitly; register-after-fire within the bounded
///   recent buffer window then returns a pre-resolved handle.
///
/// The registry is stored as a class-typed property so the owner can pass
/// `[weak registry]` into background timeout tasks — the timeout task
/// therefore cannot resurrect the owner, and its `?.` call after teardown is
/// a no-op.
final class CompletionRegistry<Key: Hashable & Sendable>: @unchecked Sendable {

    struct ObserverID: Hashable, Sendable {
        fileprivate let raw: UInt64
    }

    /// Synchronously-obtained handle returned by `register`. The waiter
    /// `await`s the handle's `wait(timeout:)` for the outcome.
    final class WaiterHandle: @unchecked Sendable {
        let id: ObserverID
        let key: Key
        fileprivate weak var registry: CompletionRegistry<Key>?
        fileprivate let stream: AsyncStream<Bool>
        fileprivate let continuation: AsyncStream<Bool>.Continuation

        fileprivate init(id: ObserverID, key: Key, registry: CompletionRegistry<Key>) {
            self.id = id
            self.key = key
            self.registry = registry
            var cont: AsyncStream<Bool>.Continuation!
            self.stream = AsyncStream<Bool> { c in cont = c }
            self.continuation = cont
        }

        /// Await the outcome. `true` = the key was resolved by the owner
        /// before `timeout` elapsed. `false` = timeout, explicit cancel,
        /// external Task cancellation, or registry teardown. Timeout is
        /// a failure boundary only; success is proven by the resume,
        /// not by elapsed time.
        ///
        /// External Task cancellation is handled via
        /// `withTaskCancellationHandler` — its `onCancel` closure runs
        /// **synchronously** when the awaiting Task is cancelled and
        /// synchronously finishes the observer, which invokes the
        /// registry's `finish` path and therefore removes the observer
        /// entry BEFORE any explicit `handle.cancel()` call. This makes
        /// the "external cancellation must clean up observers" contract
        /// deterministically provable without polling for attachment.
        func wait(timeout: Duration = .seconds(5)) async -> Bool {
            let id = self.id
            let timeoutTask = Task { [weak registry] in
                try? await Task.sleep(for: timeout)
                if !Task.isCancelled {
                    registry?.finish(id: id, value: false)
                }
            }
            return await withTaskCancellationHandler {
                defer { timeoutTask.cancel() }
                for await value in stream {
                    return value
                }
                return false
            } onCancel: { [weak registry] in
                // Runs synchronously the moment the awaiting Task is
                // cancelled — even before the for-await loop notices —
                // so the observer is removed from the registry as part
                // of the cancellation itself.
                registry?.finish(id: id, value: false)
            }
        }

        /// Explicit cancellation. Removes the observer and resumes with
        /// `false`. Idempotent; safe to call after resolve/timeout.
        func cancel() {
            registry?.finish(id: id, value: false)
        }
    }

    private let lock = NSLock()
    private var observers: [ObserverID: (key: Key, handle: WaiterHandle)] = [:]
    private var byKey: [Key: Set<ObserverID>] = [:]
    private var nextRaw: UInt64 = 0
    private var isTornDown = false
    private let enableReplayCache: Bool
    private var recentResolutions: [Key] = []
    private let recentResolutionCapacity: Int

    /// - Parameters:
    ///   - enableReplayCache: When `true`, `register(_:)` for a key that has
    ///     already been resolved (within the bounded LRU window) returns a
    ///     pre-resolved handle. Must ONLY be enabled for keys that are
    ///     unique per event (monotonic generation numbers). When `false`
    ///     (default), a `register` call always installs a fresh observer
    ///     that only fires on a subsequent `resolveAll(key:)` — safe for
    ///     stable-key callers whose same key can legitimately fire for
    ///     multiple distinct events.
    ///   - recentResolutionCapacity: Max keys retained in the replay LRU.
    ///     Ignored when `enableReplayCache == false`.
    init(enableReplayCache: Bool = false, recentResolutionCapacity: Int = 32) {
        self.enableReplayCache = enableReplayCache
        self.recentResolutionCapacity = recentResolutionCapacity
    }

    deinit {
        teardownLocked()
    }

    /// Install a waiter under `key`. Returns `nil` iff the registry has
    /// already been torn down. When replay is enabled AND the key is in the
    /// recent-resolutions buffer, the returned handle is pre-resolved with
    /// `true`; otherwise a fresh observer is installed and only fires on a
    /// subsequent `resolveAll(key:)`.
    func register(_ key: Key) -> WaiterHandle? {
        lock.lock()
        if isTornDown {
            lock.unlock()
            return nil
        }
        nextRaw &+= 1
        let id = ObserverID(raw: nextRaw)
        let handle = WaiterHandle(id: id, key: key, registry: self)
        let alreadyResolved = enableReplayCache && recentResolutions.contains(key)
        if !alreadyResolved {
            observers[id] = (key, handle)
            byKey[key, default: []].insert(id)
            // AsyncStream `onTermination` fires whenever the stream is
            // terminated: explicit `.finish()`, continuation dropped, or
            // the awaiting Task is cancelled. This hook synchronously
            // removes the observer entry so cancel/timeout/deinit/Task-
            // cancel ALL converge on zero outstanding observers.
            handle.continuation.onTermination = { [weak self] _ in
                self?.removeIfPresent(id: id)
            }
        }
        lock.unlock()
        if alreadyResolved {
            handle.continuation.yield(true)
            handle.continuation.finish()
        }
        return handle
    }

    /// Resolve every observer registered under `key` with `value`. Any
    /// observer registered for a DIFFERENT key is untouched. If replay is
    /// enabled, records `key` in the bounded LRU so subsequent
    /// `register(key)` calls within the window return pre-resolved handles.
    func resolveAll(key: Key, value: Bool = true) {
        lock.lock()
        if enableReplayCache {
            if let existing = recentResolutions.firstIndex(of: key) {
                recentResolutions.remove(at: existing)
            }
            recentResolutions.append(key)
            if recentResolutions.count > recentResolutionCapacity {
                recentResolutions.removeFirst()
            }
        }
        guard let ids = byKey.removeValue(forKey: key) else {
            lock.unlock()
            return
        }
        var handles: [WaiterHandle] = []
        handles.reserveCapacity(ids.count)
        for id in ids {
            if let entry = observers.removeValue(forKey: id) {
                handles.append(entry.handle)
            }
        }
        lock.unlock()
        for h in handles {
            h.continuation.yield(value)
            h.continuation.finish()
        }
    }

    /// Resolve every observer whose key satisfies `predicate` with `value`.
    /// Used by monotonic-counter observers (e.g. mock call-count seams).
    /// Matched keys are NOT added to the replay LRU because the intended
    /// use is monotonic — a subsequent register-after-fire caller resolves
    /// via its own post-register snapshot check.
    func resolveAll(where predicate: (Key) -> Bool, value: Bool = true) {
        lock.lock()
        let matchingKeys = byKey.keys.filter(predicate)
        var handles: [WaiterHandle] = []
        for k in matchingKeys {
            guard let ids = byKey.removeValue(forKey: k) else { continue }
            for id in ids {
                if let entry = observers.removeValue(forKey: id) {
                    handles.append(entry.handle)
                }
            }
        }
        lock.unlock()
        for h in handles {
            h.continuation.yield(value)
            h.continuation.finish()
        }
    }

    /// Internal single-observer finish path shared by cancel + timeout.
    /// Removes the observer entries AND resumes exactly once.
    fileprivate func finish(id: ObserverID, value: Bool) {
        lock.lock()
        guard let entry = observers.removeValue(forKey: id) else {
            lock.unlock()
            return
        }
        if var ids = byKey[entry.key] {
            ids.remove(id)
            if ids.isEmpty {
                byKey.removeValue(forKey: entry.key)
            } else {
                byKey[entry.key] = ids
            }
        }
        lock.unlock()
        entry.handle.continuation.yield(value)
        entry.handle.continuation.finish()
    }

    /// AsyncStream onTermination hook: called when the stream is finished
    /// via any path (explicit finish, task cancel, continuation drop). We
    /// remove the entry if it is still there; this is a no-op after an
    /// explicit `finish(id:)` already ran.
    fileprivate func removeIfPresent(id: ObserverID) {
        lock.lock()
        defer { lock.unlock() }
        guard let entry = observers.removeValue(forKey: id) else { return }
        if var ids = byKey[entry.key] {
            ids.remove(id)
            if ids.isEmpty {
                byKey.removeValue(forKey: entry.key)
            } else {
                byKey[entry.key] = ids
            }
        }
    }

    /// Cancel every outstanding observer with `false` WITHOUT marking the
    /// registry as torn down. Used by test-scaffold `reset()` methods.
    func cancelAll() {
        lock.lock()
        let snapshot = observers
        observers.removeAll()
        byKey.removeAll()
        lock.unlock()
        for (_, entry) in snapshot {
            entry.handle.continuation.yield(false)
            entry.handle.continuation.finish()
        }
    }

    /// Cancel every outstanding observer with `false` and refuse future
    /// registrations. Idempotent.
    func teardown() {
        teardownLocked()
    }

    private func teardownLocked() {
        lock.lock()
        if isTornDown {
            lock.unlock()
            return
        }
        isTornDown = true
        let snapshot = observers
        observers.removeAll()
        byKey.removeAll()
        recentResolutions.removeAll()
        lock.unlock()
        for (_, entry) in snapshot {
            entry.handle.continuation.yield(false)
            entry.handle.continuation.finish()
        }
    }

    /// Test/inspection helper: number of observers currently registered.
    /// Race-free.
    var outstandingObserverCount: Int {
        lock.lock(); defer { lock.unlock() }
        return observers.count
    }

    /// Test helper: whether replay LRU currently records this key.
    func replayCacheContainsForTesting(_ key: Key) -> Bool {
        lock.lock(); defer { lock.unlock() }
        return recentResolutions.contains(key)
    }
}
