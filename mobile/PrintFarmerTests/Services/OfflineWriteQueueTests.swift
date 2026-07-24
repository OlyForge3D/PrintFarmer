import XCTest
@testable import PrintFarmer

// MARK: - Deterministic test doubles

/// A controllable clock so the 7-day boundary and expiry are driven by explicit
/// time, never wall-clock sleeps.
final class MutableOfflineQueueClock: OfflineQueueClock, @unchecked Sendable {
    private let lock = NSLock()
    private var current: Date
    init(_ start: Date) { current = start }
    func now() -> Date { lock.lock(); defer { lock.unlock() }; return current }
    func advance(_ interval: TimeInterval) { lock.lock(); current += interval; lock.unlock() }
    func set(_ date: Date) { lock.lock(); current = date; lock.unlock() }
}

/// A scripted, recording replay transport. Returns queued outcomes (then a
/// fallback), records every attempt's operation in order, and optionally
/// signals/holds an in-flight attempt through two `CallOrderGate`s so a test can
/// deterministically observe "exactly one owner / one in-flight attempt" with no
/// polling or sleeps.
actor ScriptedReplayTransport: OfflineWriteReplayTransport {
    private var scripted: [OfflineWriteReplayOutcome]
    private let fallback: OfflineWriteReplayOutcome
    private(set) var recorded: [OfflineWriteOperation] = []
    private var startedGate: CallOrderGate?
    private var holdGate: CallOrderGate?

    init(fallback: OfflineWriteReplayOutcome = .success, scripted: [OfflineWriteReplayOutcome] = []) {
        self.fallback = fallback
        self.scripted = scripted
    }

    func installGates(started: CallOrderGate, hold: CallOrderGate) {
        startedGate = started
        holdGate = hold
    }

    func replay(_ operation: OfflineWriteOperation) async -> OfflineWriteReplayOutcome {
        let index = recorded.count
        recorded.append(operation)
        if let startedGate { await startedGate.release(index) }
        if let holdGate { await holdGate.wait(index) }
        if !scripted.isEmpty { return scripted.removeFirst() }
        return fallback
    }

    var attemptCount: Int { recorded.count }
    func operations() -> [OfflineWriteOperation] { recorded }
    func keys() -> [String] { recorded.map { $0.idempotencyKey ?? "" } }
}

// MARK: - Fixtures

enum OfflineQueueFixtures {
    static let epoch = Date(timeIntervalSince1970: 1_700_000_000)
    static let sevenDays: TimeInterval = 7 * 24 * 60 * 60

    static func adjust(sku: String, key: String, delta: Int = -1) -> OfflineWriteOperation {
        .partAdjustment(
            sku: sku,
            request: AdjustPartInventoryRequest(
                delta: delta, reason: .qcReject, jobId: nil, binCode: nil, notes: nil, operationKey: key
            )
        )
    }

    static func harvest(jobId: UUID = UUID(), key: String) -> OfflineWriteOperation {
        .harvest(
            jobId: jobId,
            request: HarvestJobRequest(
                binCode: nil, quantityOverride: nil, outputs: nil,
                operationKey: key, outputBins: nil, allowWrongBin: false, overrideReason: nil
            )
        )
    }
}

// MARK: - Coordinator tests

final class OfflineWriteQueueTests: XCTestCase {
    private let serverA = UUID()
    private let serverB = UUID()
    private let userA = UUID()
    private let userB = UUID()

    private func makeQueue(
        store: OfflineWriteQueueStoring,
        transport: OfflineWriteReplayTransport,
        clock: OfflineQueueClock,
        config: OfflineWriteQueueConfiguration = .default
    ) -> OfflineWriteQueue {
        OfflineWriteQueue(store: store, transport: transport, clock: clock, configuration: config)
    }

    private func enqueuedItem(_ result: OfflineWriteEnqueueResult) -> OfflineWriteItem? {
        if case .enqueued(let item) = result { return item }
        return nil
    }

    // MARK: Persist-before-attempt + exactly-once across relaunch

    func testPartAdjustmentSurvivesRelaunchAndReplaysExactlyOnce() async {
        let store = InMemoryOfflineWriteQueueStore()
        let clock = MutableOfflineQueueClock(OfflineQueueFixtures.epoch)

        // First launch: offline. Enqueue persists BEFORE any attempt; the one
        // replay attempt fails (offline) and the item is retained.
        let offlineTransport = ScriptedReplayTransport(fallback: .retryable)
        let first = makeQueue(store: store, transport: offlineTransport, clock: clock)
        await first.bind(serverID: serverA, userID: userA)
        let enqueue = await first.enqueue(OfflineQueueFixtures.adjust(sku: "SKU-A", key: "k1"))
        XCTAssertNotNil(enqueuedItem(enqueue))
        XCTAssertEqual(store.saveCount, 1, "enqueue must persist before any attempt")
        await first.replayPending()
        let attemptsWhileOffline = await offlineTransport.attemptCount
        XCTAssertEqual(attemptsWhileOffline, 1)
        XCTAssertEqual(store.persistedItems.count, 1, "the intent must remain durably persisted")

        // Relaunch: same durable store, fresh coordinator, connectivity restored.
        let onlineTransport = ScriptedReplayTransport(fallback: .success)
        let second = makeQueue(store: store, transport: onlineTransport, clock: clock)
        await second.bind(serverID: serverA, userID: userA)
        await second.replayPending()
        // Duplicate replay signal must not produce a second effect.
        await second.replayPending()

        let effects = await onlineTransport.attemptCount
        XCTAssertEqual(effects, 1, "exactly one server effect after reconnect")
        let remaining = await second.activeEntries()
        XCTAssertTrue(remaining.isEmpty, "a canonical success removes exactly one item")
        XCTAssertEqual(store.persistedItems.count, 0)
    }

    func testHarvestSurvivesRelaunchAndReplaysExactlyOnce() async {
        let store = InMemoryOfflineWriteQueueStore()
        let clock = MutableOfflineQueueClock(OfflineQueueFixtures.epoch)
        let jobId = UUID()

        let offlineTransport = ScriptedReplayTransport(fallback: .retryable)
        let first = makeQueue(store: store, transport: offlineTransport, clock: clock)
        await first.bind(serverID: serverA, userID: userA)
        _ = await first.enqueue(OfflineQueueFixtures.harvest(jobId: jobId, key: jobId.uuidString))
        await first.replayPending()
        XCTAssertEqual(store.persistedItems.count, 1)

        let onlineTransport = ScriptedReplayTransport(fallback: .success)
        let second = makeQueue(store: store, transport: onlineTransport, clock: clock)
        await second.bind(serverID: serverA, userID: userA)
        await second.replayPending()
        await second.replayPending()

        let effects = await onlineTransport.attemptCount
        XCTAssertEqual(effects, 1, "exactly one harvest effect after reconnect")
        let remaining = await second.activeEntries()
        XCTAssertTrue(remaining.isEmpty)
    }

    // MARK: Transport failure before-response and after-committed-response

    func testRetryReusesIdenticalKeyAndBodyAcrossReconnects() async {
        let store = InMemoryOfflineWriteQueueStore()
        let clock = MutableOfflineQueueClock(OfflineQueueFixtures.epoch)
        // Attempt 1: transport failure BEFORE a response (offline). Attempt 2:
        // success. Both must carry the identical frozen key + body.
        let transport = ScriptedReplayTransport(fallback: .success, scripted: [.retryable])
        let queue = makeQueue(store: store, transport: transport, clock: clock)
        await queue.bind(serverID: serverA, userID: userA)
        _ = await queue.enqueue(OfflineQueueFixtures.adjust(sku: "SKU-A", key: "stable-key"))

        await queue.replayPending() // attempt 1 → retryable, item retained
        XCTAssertEqual(store.persistedItems.count, 1)
        await queue.replayPending() // attempt 2 → success, removed

        let keys = await transport.keys()
        XCTAssertEqual(keys, ["stable-key", "stable-key"], "retries never mint a new key")
        let ops = await transport.operations()
        XCTAssertEqual(ops.first, ops.last, "the frozen body is resent byte-for-byte")
        let remaining = await queue.activeEntries()
        XCTAssertTrue(remaining.isEmpty, "exactly one removal despite two attempts")
    }

    func testCommittedThenLostResponseDoesNotDuplicate() async {
        let store = InMemoryOfflineWriteQueueStore()
        let clock = MutableOfflineQueueClock(OfflineQueueFixtures.epoch)
        // The server committed, but the response was lost (timeout/decoding) →
        // classified retryable. The idempotent retry then succeeds. Net: the
        // queue removes exactly one item, having re-sent the same key/body.
        let transport = ScriptedReplayTransport(fallback: .success, scripted: [.retryable])
        let queue = makeQueue(store: store, transport: transport, clock: clock)
        await queue.bind(serverID: serverA, userID: userA)
        _ = await queue.enqueue(OfflineQueueFixtures.harvest(key: "job-key"))

        await queue.replayPending()
        await queue.replayPending()

        let remaining = await queue.activeEntries()
        XCTAssertTrue(remaining.isEmpty)
        let attempts = await transport.attemptCount
        XCTAssertEqual(attempts, 2)
    }

    // MARK: Single replay owner under concurrent reconnect signals

    func testConcurrentReconnectSignalsYieldOneOwnerAndOneAttempt() async {
        let store = InMemoryOfflineWriteQueueStore()
        let clock = MutableOfflineQueueClock(OfflineQueueFixtures.epoch)
        let transport = ScriptedReplayTransport(fallback: .success)
        let started = CallOrderGate()
        let hold = CallOrderGate()
        await transport.installGates(started: started, hold: hold)

        let queue = makeQueue(store: store, transport: transport, clock: clock)
        await queue.bind(serverID: serverA, userID: userA)
        _ = await queue.enqueue(OfflineQueueFixtures.adjust(sku: "SKU-A", key: "k1"))

        // Owner drain starts and parks in-flight on the hold gate.
        let owner = Task { await queue.replayPending() }
        await started.wait(0) // attempt 0 is now in flight

        // Duplicate reconnect signals while a drain owns the queue: each must be
        // a no-op (single owner), adding no further in-flight attempt.
        await queue.replayPending()
        await queue.replayPending()
        let attemptsWhileInFlight = await transport.attemptCount
        XCTAssertEqual(attemptsWhileInFlight, 1, "exactly one in-flight attempt despite duplicate signals")

        await hold.release(0)
        await owner.value

        let total = await transport.attemptCount
        XCTAssertEqual(total, 1, "one owner produced exactly one attempt")
        let remaining = await queue.activeEntries()
        XCTAssertTrue(remaining.isEmpty)
    }

    // MARK: FIFO / per-entity ordering + namespace independence

    func testReplayPreservesPerEntityCreationOrder() async {
        let store = InMemoryOfflineWriteQueueStore()
        let clock = MutableOfflineQueueClock(OfflineQueueFixtures.epoch)
        let transport = ScriptedReplayTransport(fallback: .success)
        let queue = makeQueue(store: store, transport: transport, clock: clock)
        await queue.bind(serverID: serverA, userID: userA)

        // Two adjustments for the SAME sku (must stay ordered) + one harvest.
        _ = await queue.enqueue(OfflineQueueFixtures.adjust(sku: "SKU-A", key: "a1"))
        clock.advance(1)
        _ = await queue.enqueue(OfflineQueueFixtures.adjust(sku: "SKU-A", key: "a2"))
        clock.advance(1)
        _ = await queue.enqueue(OfflineQueueFixtures.harvest(key: "h1"))

        await queue.replayPending()

        let keys = await transport.keys()
        let a1 = keys.firstIndex(of: "a1")!
        let a2 = keys.firstIndex(of: "a2")!
        XCTAssertLessThan(a1, a2, "same-entity adjustments replay in creation order")
        XCTAssertTrue(keys.contains("h1"))
        let remaining = await queue.activeEntries()
        XCTAssertTrue(remaining.isEmpty)
    }

    func testConflictBlocksOnlyItsOwnEntity() async {
        let store = InMemoryOfflineWriteQueueStore()
        let clock = MutableOfflineQueueClock(OfflineQueueFixtures.epoch)
        // First attempt (SKU-A #1) → business conflict; SKU-A #2 must be blocked
        // (per-entity order), but SKU-B must still replay.
        let transport = ScriptedReplayTransport(
            fallback: .success,
            scripted: [.conflict(OfflineWriteConflict(reason: .businessConflict, message: "nope"))]
        )
        let queue = makeQueue(store: store, transport: transport, clock: clock)
        await queue.bind(serverID: serverA, userID: userA)
        _ = await queue.enqueue(OfflineQueueFixtures.adjust(sku: "SKU-A", key: "a1"))
        clock.advance(1)
        _ = await queue.enqueue(OfflineQueueFixtures.adjust(sku: "SKU-A", key: "a2"))
        clock.advance(1)
        _ = await queue.enqueue(OfflineQueueFixtures.adjust(sku: "SKU-B", key: "b1"))

        await queue.replayPending()

        let keys = await transport.keys()
        XCTAssertTrue(keys.contains("a1"))
        XCTAssertFalse(keys.contains("a2"), "a younger same-entity item must not jump a parked one")
        XCTAssertTrue(keys.contains("b1"), "an unrelated entity is never blocked")

        let entries = await queue.activeEntries()
        let a1Entry = entries.first { $0.item.idempotencyKey == "a1" }
        let a2Entry = entries.first { $0.item.idempotencyKey == "a2" }
        XCTAssertEqual(a1Entry?.item.status, .conflict(OfflineWriteConflict(reason: .businessConflict, message: "nope")))
        XCTAssertEqual(a2Entry?.item.status.isPending, true)
        XCTAssertNil(entries.first { $0.item.idempotencyKey == "b1" }, "b1 succeeded and was removed")
    }

    func testUnrelatedNamespaceIsNeverReplayed() async {
        let store = InMemoryOfflineWriteQueueStore()
        let clock = MutableOfflineQueueClock(OfflineQueueFixtures.epoch)
        let transport = ScriptedReplayTransport(fallback: .success)
        let queue = makeQueue(store: store, transport: transport, clock: clock)

        // Enqueue under (A, userA), then bind a DIFFERENT user.
        await queue.bind(serverID: serverA, userID: userA)
        _ = await queue.enqueue(OfflineQueueFixtures.adjust(sku: "SKU-A", key: "a1"))

        await queue.bind(serverID: serverA, userID: userB)
        let entriesForB = await queue.activeEntries()
        XCTAssertTrue(entriesForB.isEmpty, "another user's items are not visible")
        await queue.replayPending()
        let attempts = await transport.attemptCount
        XCTAssertEqual(attempts, 0, "a foreign namespace's writes are never replayed under a new identity")

        // The other namespace's item is retained untouched.
        let retained = await queue.items(forServer: serverA, user: userA)
        XCTAssertEqual(retained.count, 1)
    }

    // MARK: Exact 7-day boundary + expired = zero network

    func testExactlySevenDayBoundaryStillReplays() async {
        let store = InMemoryOfflineWriteQueueStore()
        let clock = MutableOfflineQueueClock(OfflineQueueFixtures.epoch)
        let transport = ScriptedReplayTransport(fallback: .success)
        let queue = makeQueue(store: store, transport: transport, clock: clock)
        await queue.bind(serverID: serverA, userID: userA)
        _ = await queue.enqueue(OfflineQueueFixtures.adjust(sku: "SKU-A", key: "a1"))

        clock.set(OfflineQueueFixtures.epoch + OfflineQueueFixtures.sevenDays) // exactly 7 days
        await queue.replayPending()

        let attempts = await transport.attemptCount
        XCTAssertEqual(attempts, 1, "an item at exactly the 7-day boundary still replays")
        let remaining = await queue.activeEntries()
        XCTAssertTrue(remaining.isEmpty)
    }

    func testBeyondSevenDaysMakesNoNetworkRequestAndNeedsReview() async {
        let store = InMemoryOfflineWriteQueueStore()
        let clock = MutableOfflineQueueClock(OfflineQueueFixtures.epoch)
        let transport = ScriptedReplayTransport(fallback: .success)
        let queue = makeQueue(store: store, transport: transport, clock: clock)
        await queue.bind(serverID: serverA, userID: userA)
        _ = await queue.enqueue(OfflineQueueFixtures.adjust(sku: "SKU-A", key: "a1"))

        clock.set(OfflineQueueFixtures.epoch + OfflineQueueFixtures.sevenDays + 1) // one second past
        await queue.replayPending()

        let attempts = await transport.attemptCount
        XCTAssertEqual(attempts, 0, "an expired item makes ZERO automatic network requests")
        let entries = await queue.activeEntries()
        XCTAssertEqual(entries.count, 1, "the expired item remains visible")
        XCTAssertEqual(entries.first?.item.status, .expiredNeedsReview)
    }

    // MARK: Identity switch / logout cancellation

    func testServerSwitchDuringReplayDiscardsTheInFlightResult() async {
        let store = InMemoryOfflineWriteQueueStore()
        let clock = MutableOfflineQueueClock(OfflineQueueFixtures.epoch)
        let transport = ScriptedReplayTransport(fallback: .success)
        let started = CallOrderGate()
        let hold = CallOrderGate()
        await transport.installGates(started: started, hold: hold)

        let queue = makeQueue(store: store, transport: transport, clock: clock)
        await queue.bind(serverID: serverA, userID: userA)
        _ = await queue.enqueue(OfflineQueueFixtures.adjust(sku: "SKU-A", key: "a1"))

        let owner = Task { await queue.replayPending() }
        await started.wait(0) // attempt in flight under (A, userA)

        // Switch identity mid-flight: the in-flight success must NOT remove the
        // old namespace's item once the drain resumes and sees the new identity.
        await queue.bind(serverID: serverB, userID: userB)
        await hold.release(0)
        await owner.value

        let retained = await queue.items(forServer: serverA, user: userA)
        XCTAssertEqual(retained.count, 1, "a switch cancels the in-flight replay's effect on the old namespace")
        XCTAssertEqual(retained.first?.status.isPending, true)
    }

    func testLogoutDuringReplayDiscardsTheInFlightResult() async {
        let store = InMemoryOfflineWriteQueueStore()
        let clock = MutableOfflineQueueClock(OfflineQueueFixtures.epoch)
        let transport = ScriptedReplayTransport(fallback: .success)
        let started = CallOrderGate()
        let hold = CallOrderGate()
        await transport.installGates(started: started, hold: hold)

        let queue = makeQueue(store: store, transport: transport, clock: clock)
        await queue.bind(serverID: serverA, userID: userA)
        _ = await queue.enqueue(OfflineQueueFixtures.adjust(sku: "SKU-A", key: "a1"))

        let owner = Task { await queue.replayPending() }
        await started.wait(0)
        await queue.unbind()
        await hold.release(0)
        await owner.value

        let retained = await queue.items(forServer: serverA, user: userA)
        XCTAssertEqual(retained.count, 1, "logout cancels the in-flight replay; the item is retained")
    }

    // MARK: Gate disable / pause / re-enable

    func testDisableReplayPausesRetainsAndRefusesNewEnqueue() async {
        let store = InMemoryOfflineWriteQueueStore()
        let clock = MutableOfflineQueueClock(OfflineQueueFixtures.epoch)
        let transport = ScriptedReplayTransport(fallback: .success)
        let queue = makeQueue(store: store, transport: transport, clock: clock)
        await queue.bind(serverID: serverA, userID: userA)
        _ = await queue.enqueue(OfflineQueueFixtures.adjust(sku: "SKU-A", key: "a1"))
        _ = await queue.enqueue(OfflineQueueFixtures.adjust(sku: "SKU-B", key: "b1"))

        await queue.setReplayEnabled(false)
        let paused = await queue.activeEntries()
        XCTAssertEqual(paused.count, 2, "disabling retains items")
        XCTAssertTrue(paused.allSatisfy { $0.item.status == .paused }, "items are paused, not discarded")

        // No replay while disabled.
        await queue.replayPending()
        let attemptsWhileDisabled = await transport.attemptCount
        XCTAssertEqual(attemptsWhileDisabled, 0)

        // New offline enqueue is refused (caller must use direct-online only).
        let refused = await queue.enqueue(OfflineQueueFixtures.adjust(sku: "SKU-C", key: "c1"))
        XCTAssertEqual(refused, .replayDisabled)

        // Re-enable resumes and replays the retained items.
        await queue.setReplayEnabled(true)
        await queue.replayPending()
        let attemptsAfterReenable = await transport.attemptCount
        XCTAssertEqual(attemptsAfterReenable, 2, "re-enable resumes exactly the retained items")
        let remaining = await queue.activeEntries()
        XCTAssertTrue(remaining.isEmpty)
    }

    func testReenableDoesNotResumeExpiredItems() async {
        let store = InMemoryOfflineWriteQueueStore()
        let clock = MutableOfflineQueueClock(OfflineQueueFixtures.epoch)
        let transport = ScriptedReplayTransport(fallback: .success)
        let queue = makeQueue(store: store, transport: transport, clock: clock)
        await queue.bind(serverID: serverA, userID: userA)
        _ = await queue.enqueue(OfflineQueueFixtures.adjust(sku: "SKU-A", key: "old"))

        await queue.setReplayEnabled(false)
        // Age past the window while paused, then re-enable.
        clock.set(OfflineQueueFixtures.epoch + OfflineQueueFixtures.sevenDays + 1)
        await queue.setReplayEnabled(true)

        let entries = await queue.activeEntries()
        XCTAssertEqual(entries.first?.item.status, .expiredNeedsReview, "a paused item that aged out resumes to needs-review, not pending")
        await queue.replayPending()
        let attempts = await transport.attemptCount
        XCTAssertEqual(attempts, 0, "an expired resumed item makes no network request")
    }

    // MARK: Enqueue guards (identity / key)

    func testEnqueueRequiresIdentity() async {
        let store = InMemoryOfflineWriteQueueStore()
        let clock = MutableOfflineQueueClock(OfflineQueueFixtures.epoch)
        let queue = makeQueue(store: store, transport: ScriptedReplayTransport(), clock: clock)
        let result = await queue.enqueue(OfflineQueueFixtures.adjust(sku: "SKU-A", key: "k1"))
        XCTAssertEqual(result, .noIdentity)
    }

    func testEnqueueRequiresIdempotencyKey() async {
        let store = InMemoryOfflineWriteQueueStore()
        let clock = MutableOfflineQueueClock(OfflineQueueFixtures.epoch)
        let queue = makeQueue(store: store, transport: ScriptedReplayTransport(), clock: clock)
        await queue.bind(serverID: serverA, userID: userA)
        let keyless = OfflineWriteOperation.partAdjustment(
            sku: "SKU-A",
            request: AdjustPartInventoryRequest(delta: -1, reason: .qcReject, jobId: nil, binCode: nil, notes: nil, operationKey: nil)
        )
        let result = await queue.enqueue(keyless)
        XCTAssertEqual(result, .missingIdempotencyKey)
    }

    // MARK: Identity-changed outcome stops replay without dropping the item

    func testIdentityChangedOutcomeStopsWithoutMutating() async {
        let store = InMemoryOfflineWriteQueueStore()
        let clock = MutableOfflineQueueClock(OfflineQueueFixtures.epoch)
        let transport = ScriptedReplayTransport(fallback: .success, scripted: [.identityChanged])
        let queue = makeQueue(store: store, transport: transport, clock: clock)
        await queue.bind(serverID: serverA, userID: userA)
        _ = await queue.enqueue(OfflineQueueFixtures.adjust(sku: "SKU-A", key: "a1"))

        await queue.replayPending()

        let entries = await queue.activeEntries()
        XCTAssertEqual(entries.count, 1, "a session-lost outcome does not drop the intent")
        XCTAssertEqual(entries.first?.item.status.isPending, true)
    }
}
