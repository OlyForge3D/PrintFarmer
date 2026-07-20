import XCTest
@testable import PrintFarmer

/// Exercises the `CompletionRegistry` primitive that backs every r4 coverage
/// and call-count observer. Covers the reviewer-blocked scenarios from
/// round three (continuation lifecycle) and round four (exact-generation
/// match, teardown, multi-waiter, register-after-fire via bounded LRU).
final class CompletionRegistryTests: XCTestCase {

    // MARK: - Exact-key match

    func testResolveOnlyFiresExactKeyWaiters() async {
        let registry = CompletionRegistry<Int>()
        guard let w2 = registry.register(2), let w3 = registry.register(3) else {
            XCTFail("register must succeed on a fresh registry"); return
        }
        registry.resolveAll(key: 3)

        // w3 fires; w2 must still be pending — a completion for key=3
        // must not satisfy a waiter for key=2 (r4 blocker #1).
        let w3result = await w3.wait(timeout: .seconds(1))
        XCTAssertTrue(w3result, "exact-key waiter for key=3 must resolve")

        // w2 must time out; if it were resolved by key=3, this would return true.
        let w2result = await w2.wait(timeout: .milliseconds(100))
        XCTAssertFalse(w2result, "waiter for key=2 must NOT be satisfied by resolve(key: 3)")
    }

    // MARK: - Register-after-fire via bounded recent LRU (opt-in only)

    func testDefaultRegistryDoesNotReplayResolvedKey() async {
        // Default (enableReplayCache=false): register AFTER resolve must NOT
        // pre-fire. This is the r5 blocker #2 fix — Dashboard uses default
        // so a later waiter for the same stable-key printer UUID cannot
        // pre-resolve from a stale prior update.
        let registry = CompletionRegistry<Int>()
        registry.resolveAll(key: 5)
        guard let handle = registry.register(5) else { XCTFail(); return }
        let result = await handle.wait(timeout: .milliseconds(100))
        XCTAssertFalse(result,
            "default (non-replaying) registry MUST NOT pre-ack a register after resolve — that is the stable-key false-ack bug")
    }

    func testRegisterAfterResolveViaRecentLruReturnsPreResolvedHandle() async {
        // With replay OPT-IN (monotonic-Int callers): register AFTER resolve
        // returns a pre-resolved handle via the bounded LRU.
        let registry = CompletionRegistry<Int>(enableReplayCache: true)
        registry.resolveAll(key: 5)
        guard let handle = registry.register(5) else {
            XCTFail("register after resolve must still return a handle"); return
        }
        let result = await handle.wait(timeout: .milliseconds(100))
        XCTAssertTrue(result, "register-after-fire within recent buffer must resolve immediately when replay is enabled")
    }

    func testRegisterAfterResolveForDifferentKeyDoesNotFire() async {
        let registry = CompletionRegistry<Int>(enableReplayCache: true)
        registry.resolveAll(key: 5)
        guard let handle = registry.register(6) else {
            XCTFail("register must succeed"); return
        }
        let result = await handle.wait(timeout: .milliseconds(100))
        XCTAssertFalse(result, "recent-LRU must only fire for matching keys, not neighbours")
    }

    func testRecentLruIsBoundedAndEvictsOldest() async {
        // Small capacity so we can prove eviction without allocating a lot.
        let registry = CompletionRegistry<Int>(enableReplayCache: true, recentResolutionCapacity: 3)
        registry.resolveAll(key: 1)
        registry.resolveAll(key: 2)
        registry.resolveAll(key: 3)
        registry.resolveAll(key: 4)  // evicts key 1

        guard let evictedWaiter = registry.register(1) else { XCTFail(); return }
        let evictedResult = await evictedWaiter.wait(timeout: .milliseconds(100))
        XCTAssertFalse(evictedResult, "evicted resolution must not fire the waiter")

        guard let cachedWaiter = registry.register(4) else { XCTFail(); return }
        let cachedResult = await cachedWaiter.wait(timeout: .milliseconds(100))
        XCTAssertTrue(cachedResult, "still-cached resolution must fire")
    }

    // MARK: - Dashboard-style stable-key false-ack scenario (r5 #2)

    func testStableKeyRegistryRequiresPostResolveForEachEvent() async {
        // Simulates the Dashboard update flow: printer UUID is a stable key
        // used across MANY distinct events. Two consecutive events for the
        // same printer must each require their own post-apply resolve —
        // the second waiter must not fire from the first event's residue.
        let registry = CompletionRegistry<UUID>()  // default = no replay
        let id = UUID()

        // First event: register → resolve → wait fires.
        guard let first = registry.register(id) else { XCTFail(); return }
        registry.resolveAll(key: id)
        let firstOK = await first.wait(timeout: .seconds(1))
        XCTAssertTrue(firstOK)

        // Second event: register a fresh waiter. Without a NEW resolve
        // arriving, it must NOT complete just because the same key was
        // resolved earlier.
        guard let second = registry.register(id) else { XCTFail(); return }
        let earlyResult = await second.wait(timeout: .milliseconds(100))
        XCTAssertFalse(earlyResult,
            "stable-key registry MUST require a fresh resolve per event — no stale replay")

        // Now the second event's apply fires: waiter must resolve.
        // We need a new waiter because `second` has already timed out.
        guard let third = registry.register(id) else { XCTFail(); return }
        registry.resolveAll(key: id)
        let thirdOK = await third.wait(timeout: .seconds(1))
        XCTAssertTrue(thirdOK)
    }

    // MARK: - Multi-waiter same key

    func testMultipleWaitersSameKeyAllReceiveResolution() async {
        let registry = CompletionRegistry<Int>()
        guard let a = registry.register(7), let b = registry.register(7), let c = registry.register(7) else {
            XCTFail("register must succeed"); return
        }
        registry.resolveAll(key: 7)
        let (ra, rb, rc) = await (a.wait(timeout: .seconds(1)), b.wait(timeout: .seconds(1)), c.wait(timeout: .seconds(1)))
        XCTAssertTrue(ra && rb && rc, "every same-key waiter must resolve")
    }

    // MARK: - Continuation lifecycle: teardown / cancel / timeout

    func testTeardownResumesOutstandingWaitersWithFalse() async {
        let registry = CompletionRegistry<Int>()
        guard let handle = registry.register(1) else { XCTFail(); return }
        registry.teardown()
        let result = await handle.wait(timeout: .seconds(1))
        XCTAssertFalse(result, "teardown must resume outstanding waiters with false, not leak the continuation")
    }

    func testDeinitResumesOutstandingWaitersWithFalse() async {
        // Weak references to detect deallocation.
        weak var weakRegistry: CompletionRegistry<Int>?
        let handle: CompletionRegistry<Int>.WaiterHandle
        do {
            let registry = CompletionRegistry<Int>()
            weakRegistry = registry
            guard let h = registry.register(1) else { XCTFail(); return }
            handle = h
        }
        // Registry is out of scope. `handle` retains the continuation but
        // the registry-owned observer entry has been torn down.
        _ = weakRegistry  // touch to silence unused warning
        let result = await handle.wait(timeout: .seconds(1))
        XCTAssertFalse(result, "registry deinit must resume outstanding waiters with false")
    }

    func testCancelResumesExactlyOnce() async {
        let registry = CompletionRegistry<Int>()
        guard let handle = registry.register(1) else { XCTFail(); return }
        handle.cancel()
        handle.cancel() // idempotent; must not double-resume
        let result = await handle.wait(timeout: .seconds(1))
        XCTAssertFalse(result, "cancel must resume with false")
    }

    func testTimeoutFiresWithoutRaceAgainstResolve() async {
        let registry = CompletionRegistry<Int>()
        guard let handle = registry.register(1) else { XCTFail(); return }
        // No resolve — timeout path must fire without hanging.
        let result = await handle.wait(timeout: .milliseconds(50))
        XCTAssertFalse(result, "timeout must resume with false")
    }

    // MARK: - r5 blocker #1: cancel/timeout/deinit must leave observer count at 0

    func testCancelRemovesObserverFromRegistry() async {
        let registry = CompletionRegistry<Int>()
        guard let handle = registry.register(1) else { XCTFail(); return }
        XCTAssertEqual(registry.outstandingObserverCount, 1)
        handle.cancel()
        _ = await handle.wait(timeout: .milliseconds(50))
        XCTAssertEqual(registry.outstandingObserverCount, 0,
            "cancel MUST synchronously remove the observer entry — leftover entries are the r5 leak")
    }

    func testTimeoutRemovesObserverFromRegistry() async {
        let registry = CompletionRegistry<Int>()
        guard let handle = registry.register(1) else { XCTFail(); return }
        XCTAssertEqual(registry.outstandingObserverCount, 1)
        _ = await handle.wait(timeout: .milliseconds(50))
        XCTAssertEqual(registry.outstandingObserverCount, 0,
            "timeout MUST remove the observer entry")
    }

    func testResolveRemovesObserverFromRegistry() async {
        let registry = CompletionRegistry<Int>()
        guard let handle = registry.register(1) else { XCTFail(); return }
        registry.resolveAll(key: 1)
        _ = await handle.wait(timeout: .seconds(1))
        XCTAssertEqual(registry.outstandingObserverCount, 0,
            "successful resolve MUST remove the observer entry")
    }

    func testAsyncStreamOnTerminationCleansUpObserver() async {
        // Reviewer contract (r7 blocker #3): external Task cancellation
        // MUST synchronously remove the observer entry via
        // `withTaskCancellationHandler.onCancel` — WITHOUT any explicit
        // `handle.cancel()` and WITHOUT a fixed-wait attachment sleep.
        // `onCancel` runs synchronously the moment `task.cancel()` is
        // called and finishes the observer, so `await task.value`
        // returns only after the observer has been removed.
        let registry = CompletionRegistry<Int>()
        guard let handle = registry.register(1) else { XCTFail(); return }
        XCTAssertEqual(registry.outstandingObserverCount, 1)

        let task = Task { await handle.wait(timeout: .seconds(30)) }
        task.cancel()
        _ = await task.value

        XCTAssertEqual(registry.outstandingObserverCount, 0,
            "external Task cancellation MUST remove the observer entry synchronously — no explicit handle.cancel() required")
        _ = handle  // keep alive across await
    }

    // MARK: - resolveAll(where:) for monotonic-counter observers

    func testResolveWherePredicateFiresMatchingWaiters() async {
        let registry = CompletionRegistry<Int>()
        guard let w1 = registry.register(1), let w3 = registry.register(3), let w5 = registry.register(5) else {
            XCTFail(); return
        }
        // Monotonic-counter semantics: resolve every waiter whose target ≤ 3.
        registry.resolveAll(where: { $0 <= 3 })
        let (r1, r3) = await (w1.wait(timeout: .seconds(1)), w3.wait(timeout: .seconds(1)))
        XCTAssertTrue(r1 && r3, "waiters matching the predicate must fire")

        // w5 must remain pending; a second resolve for target ≤ 5 fires it.
        registry.resolveAll(where: { $0 <= 5 })
        let r5 = await w5.wait(timeout: .seconds(1))
        XCTAssertTrue(r5, "waiter matching subsequent predicate must fire")
    }
}
