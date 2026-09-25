import XCTest
@testable import PrintFarmer

/// Deterministic coverage for the F10-C2 (#789) typed read-cache adapters layered
/// on the shipped #785 foundation. Every test is barrier/ACK/fake-clock driven —
/// no sleeps, no polling, no elapsed-time pass criteria.
///
/// Criterion map (issue #789 in-scope 1-9):
///  1 typed adapters share ONE #785 store/namespace  → all round-trip tests
///  2 only an atomic successful refresh writes        → `testNoImplicitWriteWithoutRecord`,
///                                                       `testPersistenceFailurePreservesPriorSnapshot`
///  3 offline ordering + dedupe + cursor fidelity      → `testAttentionSnapshotRoundTripOrderingDedupeHealthyCursor`
///  4 coverage fleet/detail unknown + stable ids       → `testCoverageFleetRoundTripUnknownRunoutCoversStableIds`,
///                                                       `testCoveragePrinterDetailRoundTrip`
///  5 (serverID,userID) isolation on switch/logout      → `testNamespaceIsolationOnServerUserSwitch`,
///                                                       `testCommitWithStaleCapturedSessionIsRejected`,
///                                                       `testHydrateDuringSwitchYieldsInactive`
///  6 monotonic — older cannot overwrite newer          → `testOlderSuccessCannotOverwriteNewer`,
///                                                       `testExplicitFetchTimestampIsDisplayOnlyNotOrdering`,
///                                                       `testErrorAfterSuccessNeverWrites`
///    #3007 clock-independent ordering                  → `testSameMillisecondConfirmedLiveWriteReplaces`,
///                                                       `testBackwardClockConfirmedLiveWriteReplaces`,
///                                                       `testNewLaunchSupersedesPriorLaunchRecordAndOrderIsDurable`,
///                                                       `testLegacyRecordWithoutWriteOrderIsSuperseded`
///  7 disabled tombstone beats older, not empty success → `testDisabledTombstoneBeatsOlderSnapshot`,
///                                                       `testReverseOrderNewerDisabledWins`,
///                                                       `testDisabledIsDistinctFromAbsent`
///  8 reconnect exactly-once                            → `FeatureReadCacheVMTests`
///                                                        (`…RefusesLoadMoreThenReconnectReplacesOnce`,
///                                                         `…PreservesUnknownThenReconnectReplaces`)
///  9 shared stale timestamp + a11y (iPhone AND iPad)   → `testSharedStaleBannerTextAndAccessibilitySizeClassAgnostic`
///  recovery via #785                                   → `testCorruptRecordIsRecovered`,
///                                                       `testOldSchemaRecordIsRecovered`
final class FeatureReadCacheTests: XCTestCase {

    // MARK: Deterministic clock

    /// Explicitly-advanced clock so the displayed `lastUpdatedAtMillis` is exact.
    /// Ordering never depends on it (#3007).
    private final class MutableClock: @unchecked Sendable {
        private let lock = NSLock()
        private var millis: Int64
        init(_ millis: Int64) { self.millis = millis }
        func set(_ value: Int64) { lock.lock(); millis = value; lock.unlock() }
        func now() -> Date {
            lock.lock(); defer { lock.unlock() }
            return Date(timeIntervalSince1970: Double(millis) / 1000.0)
        }
        var sendableNow: @Sendable () -> Date { { [self] in self.now() } }
    }

    /// Captures every adapter commit outcome (#3007 reporting seam).
    private final class CommitRecorder: @unchecked Sendable {
        struct Outcome: Equatable {
            let recordKey: String
            let result: FeatureReadCacheCommitResult
        }
        private let lock = NSLock()
        private var recorded: [Outcome] = []
        var outcomes: [Outcome] { lock.lock(); defer { lock.unlock() }; return recorded }
        var report: FeatureReadCacheCommitReporter {
            { [self] key, result in
                lock.lock(); recorded.append(Outcome(recordKey: key, result: result)); lock.unlock()
            }
        }
    }

    // MARK: Roots / teardown

    private var roots: [URL] = []

    override func tearDown() {
        for root in roots { try? FileManager.default.removeItem(at: root) }
        roots = []
        super.tearDown()
    }

    private func newRoot() -> URL {
        let root = FarmSnapshotFixtures.tempRoot()
        roots.append(root)
        return root
    }

    private func makeStore(
        root: URL,
        fileIO: FarmSnapshotFileIO = DiskFarmSnapshotFileIO()
    ) -> (FeatureReadCacheStore, FarmSnapshotAuthority) {
        let authority = FarmSnapshotFixtures.makeAuthority(
            tombstoneDefaults: UserDefaults(suiteName: trackedSuiteName("tomb"))!
        )
        let store = FeatureReadCacheStore(authority: authority, fileIO: fileIO, rootURL: root)
        return (store, authority)
    }

    @discardableResult
    private func mint(
        _ authority: FarmSnapshotAuthority,
        _ namespace: FarmSnapshotNamespace,
        generation: Int = 0
    ) throws -> FarmSnapshotSession {
        try XCTUnwrap(try authority.mint(namespace: namespace, generation: generation))
    }

    // Test-side mirror of the store's on-disk layout for direct disk assertions.
    private func liveURL(root: URL, _ namespace: FarmSnapshotNamespace, _ recordKey: String) -> URL {
        root.appendingPathComponent("servers", isDirectory: true)
            .appendingPathComponent(namespace.serverID.uuidString, isDirectory: true)
            .appendingPathComponent("features", isDirectory: true)
            .appendingPathComponent(namespace.userID.uuidString, isDirectory: true)
            .appendingPathComponent("\(recordKey).json")
    }

    // MARK: Fixtures

    private func item(
        _ id: String,
        printer: UUID = UUID(),
        name: String = "Printer",
        kind: AttentionKind = .failure,
        severity: AttentionSeverity = .critical,
        occurred: TimeInterval = 1_000
    ) -> AttentionItem {
        AttentionItem(
            id: id,
            kind: kind,
            severity: severity,
            printerId: printer,
            printerName: name,
            title: "t-\(id)",
            detail: "d-\(id)",
            occurredAt: Date(timeIntervalSince1970: occurred),
            actions: []
        )
    }

    private func toolhead(
        index: Int,
        id: UUID? = nil,
        name: String,
        status: FilamentCoverageStatus,
        remaining: Double? = nil,
        runoutAt: Date? = nil,
        runoutLayer: Int? = nil
    ) -> ToolheadFilamentCoverage {
        ToolheadFilamentCoverage(
            toolheadIndex: index,
            toolheadId: id,
            toolheadName: name,
            remainingGrams: remaining,
            status: status,
            predictedRunoutAt: runoutAt,
            predictedRunoutLayer: runoutLayer
        )
    }

    private func printerCoverage(
        id: UUID,
        name: String = "P",
        status: FilamentCoverageStatus,
        toolheads: [ToolheadFilamentCoverage],
        evaluatedAt: TimeInterval = 5_000
    ) -> PrinterFilamentCoverage {
        PrinterFilamentCoverage(
            printerId: id,
            printerName: name,
            status: status,
            toolheads: toolheads,
            activeJobId: nil,
            activeJobName: nil,
            activeJobProgress: nil,
            earliestPredictedRunoutAt: nil,
            assignedQueuedJobCount: 0,
            evaluatedAtUtc: Date(timeIntervalSince1970: evaluatedAt)
        )
    }

    // MARK: 1 & 3 — Attention snapshot round-trip: ordering, dedupe, healthy, cursor

    func testAttentionSnapshotRoundTripOrderingDedupeHealthyCursor() async throws {
        let root = newRoot()
        let (store, authority) = makeStore(root: root)
        let ns = FarmSnapshotFixtures.namespace()
        let session = try mint(authority, ns)
        let clock = MutableClock(10_000)
        let adapter = AttentionReadCacheAdapter(store: store, now: clock.sendableNow)

        let a = item("failure:a", occurred: 3_000)
        let b = item("stall:b", occurred: 2_000)
        let c = item("failure:c", occurred: 1_000)
        // Duplicate id for `a` must be dropped, first-wins, order preserved.
        let dupA = item("failure:a", name: "OTHER", occurred: 9_999)

        let result = await adapter.recordRefresh(
            items: [a, b, c, dupA],
            nextCursor: "cursor-xyz",
            healthyPrinterCount: 7,
            capturedSession: session
        )
        XCTAssertEqual(result, .committed)

        let hydration = await adapter.loadCached()
        guard case let .snapshot(payload, millis) = hydration else {
            return XCTFail("expected snapshot, got \(hydration)")
        }
        XCTAssertEqual(payload.items.map(\.id), ["failure:a", "stall:b", "failure:c"], "ordering + dedupe")
        XCTAssertEqual(payload.items.first?.printerName, "Printer", "first-wins dedupe keeps the original item")
        XCTAssertEqual(payload.healthyPrinterCount, 7)
        XCTAssertEqual(payload.nextCursor, "cursor-xyz", "cursor carried for fidelity (load-more disabled offline)")
        XCTAssertEqual(millis, 10_000, "exact successful completion instant")
    }

    /// Criterion 2 — nothing is written unless a successful refresh is recorded.
    /// A failed/cancelled/partial generation simply does not call `recordRefresh`,
    /// so the cache stays `absent`. Proven by asserting absence after hydrate with
    /// no record call and no file on disk.
    func testNoImplicitWriteWithoutRecord() async throws {
        let root = newRoot()
        let (store, authority) = makeStore(root: root)
        let ns = FarmSnapshotFixtures.namespace()
        _ = try mint(authority, ns)
        let adapter = AttentionReadCacheAdapter(store: store)

        let hydration = await adapter.loadCached()
        XCTAssertEqual(hydration, .absent)
        XCTAssertFalse(
            FileManager.default.fileExists(atPath: liveURL(root: root, ns, "attention-feed").path),
            "no live record may exist without an explicit successful record call"
        )
    }

    // MARK: 4 — Coverage fleet round-trip: unknown, runout±ETA, covers, stable ids

    func testCoverageFleetRoundTripUnknownRunoutCoversStableIds() async throws {
        let root = newRoot()
        let (store, authority) = makeStore(root: root)
        let ns = FarmSnapshotFixtures.namespace()
        let session = try mint(authority, ns)
        let clock = MutableClock(20_000)
        let adapter = FilamentCoverageReadCacheAdapter(store: store, now: clock.sendableNow)

        let p1 = UUID(), p2 = UUID(), p3 = UUID()
        let th0 = UUID(), th1 = UUID()
        // Multi-toolhead printer with DUPLICATE names but distinct stable ids.
        let multi = printerCoverage(
            id: p1, name: "Multi", status: .runout,
            toolheads: [
                toolhead(index: 0, id: th0, name: "AMS", status: .covers, remaining: 500),
                toolhead(index: 1, id: th1, name: "AMS", status: .runout, remaining: 10,
                         runoutAt: Date(timeIntervalSince1970: 8_888), runoutLayer: 42)
            ]
        )
        // Runout WITHOUT an ETA — the honest "runout but no prediction" case.
        let runoutNoEta = printerCoverage(
            id: p2, name: "NoEta", status: .runout,
            toolheads: [toolhead(index: 0, name: "T0", status: .runout, remaining: 1)]
        )
        // Unknown must be preserved HONESTLY, never coerced to covers/runout.
        let unknown = printerCoverage(
            id: p3, name: "Unk", status: .unknown,
            toolheads: [toolhead(index: 0, name: "T0", status: .unknown)]
        )
        let fleet = FleetFilamentCoverage(
            printers: [multi, runoutNoEta, unknown],
            evaluatedAtUtc: Date(timeIntervalSince1970: 20)
        )

        let committed = await adapter.recordFleet(fleet, capturedSession: session)
        XCTAssertEqual(committed, .committed)

        let hydration = await adapter.loadCachedFleet()
        guard case let .snapshot(payload, millis) = hydration else {
            return XCTFail("expected fleet snapshot, got \(hydration)")
        }
        XCTAssertEqual(payload, fleet, "fleet DTO round-trips byte-for-byte")
        XCTAssertEqual(millis, 20_000)

        let hydratedMulti = payload.printers[0]
        XCTAssertEqual(hydratedMulti.toolheads.map(\.id), ["id:\(th0.uuidString)", "id:\(th1.uuidString)"],
                       "stable ids derive from toolheadId, never the duplicate name")
        XCTAssertEqual(hydratedMulti.toolheads[1].predictedRunoutLayer, 42)
        XCTAssertEqual(hydratedMulti.toolheads[1].predictedRunoutAt, Date(timeIntervalSince1970: 8_888))
        XCTAssertNil(payload.printers[1].toolheads[0].predictedRunoutAt, "runout without ETA stays ETA-less")
        XCTAssertEqual(payload.printers[2].status, .unknown, "unknown preserved honestly")
        XCTAssertEqual(payload.printers[2].toolheads[0].status, .unknown)
    }

    func testCoveragePrinterDetailRoundTrip() async throws {
        let root = newRoot()
        let (store, authority) = makeStore(root: root)
        let ns = FarmSnapshotFixtures.namespace()
        let session = try mint(authority, ns)
        let clock = MutableClock(30_000)
        let adapter = FilamentCoverageReadCacheAdapter(store: store, now: clock.sendableNow)

        let pid = UUID()
        let detail = printerCoverage(
            id: pid, name: "Detail", status: .unknown,
            toolheads: [toolhead(index: 0, name: "T0", status: .unknown)]
        )
        let committed = await adapter.recordPrinter(detail, capturedSession: session)
        XCTAssertEqual(committed, .committed)

        // A DIFFERENT printer id must not read this record (per-printer isolation).
        let other = await adapter.loadCachedPrinter(id: UUID())
        XCTAssertEqual(other, .absent)

        let hydration = await adapter.loadCachedPrinter(id: pid)
        guard case let .snapshot(payload, millis) = hydration else {
            return XCTFail("expected printer snapshot, got \(hydration)")
        }
        XCTAssertEqual(payload, detail)
        XCTAssertEqual(payload.status, .unknown)
        XCTAssertEqual(millis, 30_000)
    }

    // MARK: 6 — Monotonic: older success/error cannot overwrite newer

    /// A completion confirmed EARLIER (lower write order) that reaches the store
    /// after a later-confirmed one is refused — the real out-of-order race,
    /// independent of any clock (#3007).
    func testOlderSuccessCannotOverwriteNewer() async throws {
        let root = newRoot()
        let (store, authority) = makeStore(root: root)
        let ns = FarmSnapshotFixtures.namespace()
        let session = try mint(authority, ns)
        let clock = MutableClock(5_000)
        let recorder = CommitRecorder()
        let adapter = AttentionReadCacheAdapter(store: store, now: clock.sendableNow, reportCommit: recorder.report)
        let source = FeatureReadCacheWriteOrderSource()
        let olderOrder = source.next()
        let newerOrder = source.next()

        let newer = await adapter.recordRefresh(items: [item("failure:new")], nextCursor: nil,
                                                healthyPrinterCount: 3, writeOrder: newerOrder,
                                                capturedSession: session)
        XCTAssertEqual(newer, .committed)
        // Clock moves FORWARD, yet the earlier-confirmed write must still lose.
        clock.set(6_000)
        let older = await adapter.recordRefresh(items: [item("failure:old")], nextCursor: nil,
                                                healthyPrinterCount: 99, writeOrder: olderOrder,
                                                capturedSession: session)
        XCTAssertEqual(older, .notNewer)
        XCTAssertEqual(recorder.outcomes, [
            .init(recordKey: AttentionReadCacheAdapter.recordKey, result: .committed),
            .init(recordKey: AttentionReadCacheAdapter.recordKey, result: .notNewer)
        ], "a refused confirmed-live write must be reported, not discarded silently")

        let hydration = await adapter.loadCached()
        guard case let .snapshot(payload, millis) = hydration else {
            return XCTFail("expected snapshot")
        }
        XCTAssertEqual(payload.items.map(\.id), ["failure:new"])
        XCTAssertEqual(payload.healthyPrinterCount, 3)
        XCTAssertEqual(millis, 5_000)
    }

    /// An explicit fetch timestamp (startup prefetch) is the displayed "last
    /// updated" instant only; it never decides ordering (#3007).
    func testExplicitFetchTimestampIsDisplayOnlyNotOrdering() async throws {
        let root = newRoot()
        let (store, authority) = makeStore(root: root)
        let ns = FarmSnapshotFixtures.namespace()
        let session = try mint(authority, ns)
        let clock = MutableClock(5_000)
        let adapter = AttentionReadCacheAdapter(store: store, now: clock.sendableNow)
        let source = FeatureReadCacheWriteOrderSource()
        let earlierOrder = source.next()

        let first = await adapter.recordRefresh(
            items: [item("failure:first")],
            nextCursor: nil,
            healthyPrinterCount: 3,
            writeOrder: source.next(),
            capturedSession: session
        )
        XCTAssertEqual(first, .committed)

        // Earlier-confirmed write with a LATER explicit timestamp is still refused.
        let stale = await adapter.recordRefresh(
            items: [item("failure:stale")],
            nextCursor: nil,
            healthyPrinterCount: 99,
            lastUpdatedAtMillis: 9_000,
            writeOrder: earlierOrder,
            capturedSession: session
        )
        XCTAssertEqual(stale, .notNewer)

        // Later-confirmed write with an EARLIER explicit timestamp replaces, and
        // keeps its own display instant.
        let prefetched = await adapter.recordRefresh(
            items: [item("failure:prefetched")],
            nextCursor: nil,
            healthyPrinterCount: 4,
            lastUpdatedAtMillis: 4_000,
            writeOrder: source.next(),
            capturedSession: session
        )
        XCTAssertEqual(prefetched, .committed)

        let hydration = await adapter.loadCached()
        guard case let .snapshot(payload, millis) = hydration else {
            return XCTFail("expected snapshot")
        }
        XCTAssertEqual(payload.items.map(\.id), ["failure:prefetched"])
        XCTAssertEqual(millis, 4_000)
    }

    /// #3007 regression: two confirmed-live writes in the SAME millisecond. The
    /// wall-clock rule refused the second as `.notNewer` and left the older
    /// snapshot on disk while the VM showed the newer one.
    func testSameMillisecondConfirmedLiveWriteReplaces() async throws {
        let root = newRoot()
        let (store, authority) = makeStore(root: root)
        let ns = FarmSnapshotFixtures.namespace()
        let session = try mint(authority, ns)
        let clock = MutableClock(5_000) // frozen for both writes
        let adapter = AttentionReadCacheAdapter(store: store, now: clock.sendableNow)

        let seed = await adapter.recordRefresh(items: [item("failure:seed")], nextCursor: nil,
                                               healthyPrinterCount: 1, capturedSession: session)
        XCTAssertEqual(seed, .committed)
        let live = await adapter.recordRefresh(items: [item("failure:live")], nextCursor: nil,
                                               healthyPrinterCount: 2, capturedSession: session)
        XCTAssertEqual(live, .committed, "a same-millisecond confirmed-live write must not be refused")

        let hydration = await adapter.loadCached()
        guard case let .snapshot(payload, millis) = hydration else {
            return XCTFail("expected snapshot")
        }
        XCTAssertEqual(payload.items.map(\.id), ["failure:live"])
        XCTAssertEqual(millis, 5_000)
    }

    /// #3007 regression: the wall clock steps BACKWARD (NTP step / manual change)
    /// between two confirmed-live writes. The later-confirmed write must win.
    func testBackwardClockConfirmedLiveWriteReplaces() async throws {
        let root = newRoot()
        let (store, authority) = makeStore(root: root)
        let ns = FarmSnapshotFixtures.namespace()
        let session = try mint(authority, ns)
        let clock = MutableClock(9_000)
        let adapter = FilamentCoverageReadCacheAdapter(store: store, now: clock.sendableNow)
        let printerID = UUID()

        let seed = await adapter.recordPrinter(
            printerCoverage(id: printerID, name: "Seed", status: .covers, toolheads: []),
            capturedSession: session
        )
        XCTAssertEqual(seed, .committed)
        clock.set(4_000)
        let live = await adapter.recordPrinter(
            printerCoverage(id: printerID, name: "Live", status: .runout, toolheads: []),
            capturedSession: session
        )
        XCTAssertEqual(live, .committed, "a backward clock step must not refuse a confirmed-live write")

        let hydration = await adapter.loadCachedPrinter(id: printerID)
        guard case let .snapshot(coverage, millis) = hydration else {
            return XCTFail("expected snapshot")
        }
        XCTAssertEqual(coverage.printerName, "Live")
        XCTAssertEqual(millis, 4_000, "the displayed instant is the honest wall clock of the write")
    }

    /// Cross-launch soundness: a record written by a PRIOR launch (even with a
    /// much higher sequence and a later wall clock, e.g. after a reboot reset any
    /// uptime-based counter) is superseded by the first write of this launch,
    /// while in-launch ordering still holds against the durable record.
    func testNewLaunchSupersedesPriorLaunchRecordAndOrderIsDurable() async throws {
        let root = newRoot()
        let ns = FarmSnapshotFixtures.namespace()
        let clock = MutableClock(9_000)

        let (priorStore, priorAuthority) = makeStore(root: root)
        let priorSession = try mint(priorAuthority, ns)
        let priorAdapter = AttentionReadCacheAdapter(store: priorStore, now: clock.sendableNow)
        let priorLaunch = FeatureReadCacheWriteOrder(launchID: UUID(), sequence: 1_000_000)
        let prior = await priorAdapter.recordRefresh(items: [item("failure:prior")], nextCursor: nil,
                                                     healthyPrinterCount: 1, writeOrder: priorLaunch,
                                                     capturedSession: priorSession)
        XCTAssertEqual(prior, .committed)

        // "Relaunch": a new store over the same root, a fresh order source, and a
        // wall clock that is now EARLIER than the prior launch's write.
        clock.set(1_000)
        let (store, authority) = makeStore(root: root)
        let session = try mint(authority, ns)
        let adapter = AttentionReadCacheAdapter(store: store, now: clock.sendableNow)
        let launch = FeatureReadCacheWriteOrderSource()
        let beforeFirst = launch.next()
        let first = await adapter.recordRefresh(items: [item("failure:relaunch")], nextCursor: nil,
                                                healthyPrinterCount: 2, writeOrder: launch.next(),
                                                capturedSession: session)
        XCTAssertEqual(first, .committed, "any confirmed-live write of this launch supersedes a prior launch")

        // The order persisted with the record: an earlier-confirmed write of this
        // launch, arriving late, is refused against the on-disk stamp.
        let late = await adapter.recordRefresh(items: [item("failure:late")], nextCursor: nil,
                                               healthyPrinterCount: 3, writeOrder: beforeFirst,
                                               capturedSession: session)
        XCTAssertEqual(late, .notNewer)

        let hydration = await adapter.loadCached()
        guard case let .snapshot(payload, _) = hydration else {
            return XCTFail("expected snapshot")
        }
        XCTAssertEqual(payload.items.map(\.id), ["failure:relaunch"])
    }

    /// A record written before #3007 carries no `writeOrder` (and may carry a
    /// wall-clock stamp from the future). It stays readable and is superseded by
    /// the next confirmed-live write.
    func testLegacyRecordWithoutWriteOrderIsSuperseded() async throws {
        let root = newRoot()
        let (store, authority) = makeStore(root: root)
        let ns = FarmSnapshotFixtures.namespace()
        let session = try mint(authority, ns)
        let clock = MutableClock(1_000)
        let adapter = AttentionReadCacheAdapter(store: store, now: clock.sendableNow)

        let legacy = FeatureReadCacheEnvelope<AttentionCacheSnapshot>(
            featureKey: AttentionReadCacheAdapter.recordKey,
            namespace: ns,
            lastUpdatedAtMillis: 9_999_999_999_999,
            writeOrder: nil,
            kind: .snapshot,
            payload: AttentionCacheSnapshot(items: [item("failure:legacy")], nextCursor: nil, healthyPrinterCount: 1)
        )
        let data = try FeatureReadCacheEnvelope<AttentionCacheSnapshot>.makeEncoder().encode(legacy)
        XCTAssertFalse(String(decoding: data, as: UTF8.self).contains("writeOrder"),
                       "fixture must match the pre-#3007 on-disk layout")
        let live = liveURL(root: root, ns, AttentionReadCacheAdapter.recordKey)
        try FileManager.default.createDirectory(at: live.deletingLastPathComponent(), withIntermediateDirectories: true)
        try data.write(to: live)

        guard case let .snapshot(before, _) = await adapter.loadCached() else {
            return XCTFail("legacy record must remain readable")
        }
        XCTAssertEqual(before.items.map(\.id), ["failure:legacy"])

        let fresh = await adapter.recordRefresh(items: [item("failure:fresh")], nextCursor: nil,
                                                healthyPrinterCount: 2, capturedSession: session)
        XCTAssertEqual(fresh, .committed)
        guard case let .snapshot(after, millis) = await adapter.loadCached() else {
            return XCTFail("expected snapshot")
        }
        XCTAssertEqual(after.items.map(\.id), ["failure:fresh"])
        XCTAssertEqual(millis, 1_000)
    }

    /// An error completion never calls a record method, so the last-good snapshot
    /// is preserved (criterion 6). Modeled by simply not recording on error.
    func testErrorAfterSuccessNeverWrites() async throws {
        let root = newRoot()
        let (store, authority) = makeStore(root: root)
        let ns = FarmSnapshotFixtures.namespace()
        let session = try mint(authority, ns)
        let clock = MutableClock(7_000)
        let adapter = AttentionReadCacheAdapter(store: store, now: clock.sendableNow)

        let committed = await adapter.recordRefresh(items: [item("failure:good")], nextCursor: nil,
                                                    healthyPrinterCount: 1, capturedSession: session)
        XCTAssertEqual(committed, .committed)
        // (error path records nothing)
        let hydration = await adapter.loadCached()
        guard case let .snapshot(payload, _) = hydration else {
            return XCTFail("expected last-good snapshot preserved")
        }
        XCTAssertEqual(payload.items.map(\.id), ["failure:good"])
    }

    // MARK: 7 — Disabled tombstone beats older; not an empty success

    func testDisabledTombstoneBeatsOlderSnapshot() async throws {
        let root = newRoot()
        let (store, authority) = makeStore(root: root)
        let ns = FarmSnapshotFixtures.namespace()
        let session = try mint(authority, ns)
        let clock = MutableClock(0)
        let adapter = AttentionReadCacheAdapter(store: store, now: clock.sendableNow)
        let source = FeatureReadCacheWriteOrderSource()

        clock.set(1_000)
        let s1 = await adapter.recordRefresh(items: [item("failure:x")], nextCursor: nil,
                                             healthyPrinterCount: 2, writeOrder: source.next(),
                                             capturedSession: session)
        XCTAssertEqual(s1, .committed)
        // A snapshot confirmed BEFORE the tombstone but still in flight.
        let zombieOrder = source.next()
        clock.set(2_000)
        let disabled = await adapter.recordDisabled(writeOrder: source.next(), capturedSession: session)
        XCTAssertEqual(disabled, .committed)

        // Disabled tombstone now hides the older snapshot.
        let afterDisable = await adapter.loadCached()
        XCTAssertEqual(afterDisable, .disabled(lastUpdatedAtMillis: 2_000))

        // The earlier-confirmed snapshot cannot resurface past the tombstone, even
        // though it reaches the store later on a later clock.
        clock.set(3_000)
        let zombie = await adapter.recordRefresh(items: [item("failure:zombie")], nextCursor: nil,
                                                 healthyPrinterCount: 5, writeOrder: zombieOrder,
                                                 capturedSession: session)
        XCTAssertEqual(zombie, .notNewer)
        let stillDisabled = await adapter.loadCached()
        XCTAssertEqual(stillDisabled, .disabled(lastUpdatedAtMillis: 2_000))
    }

    /// Reverse arrival: the disabled completion is confirmed last, so it wins even
    /// though an earlier-confirmed success reaches the store afterward.
    func testReverseOrderNewerDisabledWins() async throws {
        let root = newRoot()
        let (store, authority) = makeStore(root: root)
        let ns = FarmSnapshotFixtures.namespace()
        let session = try mint(authority, ns)
        let clock = MutableClock(9_000)
        let adapter = FilamentCoverageReadCacheAdapter(store: store, now: clock.sendableNow)
        let source = FeatureReadCacheWriteOrderSource()
        let successOrder = source.next()

        let disabled = await adapter.recordFleetDisabled(writeOrder: source.next(), capturedSession: session)
        XCTAssertEqual(disabled, .committed)

        clock.set(10_000) // earlier-confirmed success lands late
        let fleet = FleetFilamentCoverage(printers: [], evaluatedAtUtc: Date(timeIntervalSince1970: 1))
        let late = await adapter.recordFleet(fleet, writeOrder: successOrder, capturedSession: session)
        XCTAssertEqual(late, .notNewer)

        let hydration = await adapter.loadCachedFleet()
        XCTAssertEqual(hydration, .disabled(lastUpdatedAtMillis: 9_000))
    }

    func testDisabledIsDistinctFromAbsent() async throws {
        let root = newRoot()
        let (store, authority) = makeStore(root: root)
        let ns = FarmSnapshotFixtures.namespace()
        let session = try mint(authority, ns)
        let adapter = AttentionReadCacheAdapter(store: store)

        let before = await adapter.loadCached()
        XCTAssertEqual(before, .absent, "no record yet")
        let disabled = await adapter.recordDisabled(capturedSession: session)
        XCTAssertEqual(disabled, .committed)
        // Disabled is a first-class tombstone, NOT modeled as empty success/absent.
        let after = await adapter.loadCached()
        guard case .disabled = after else {
            return XCTFail("disabled tombstone must be distinct from absent/empty success")
        }
    }

    // MARK: 5 — (serverID, userID) namespace isolation

    func testNamespaceIsolationOnServerUserSwitch() async throws {
        let root = newRoot()
        let (store, authority) = makeStore(root: root)
        let nsA = FarmSnapshotFixtures.namespace()
        let sessionA = try mint(authority, nsA)
        let adapter = AttentionReadCacheAdapter(store: store)

        let committed = await adapter.recordRefresh(items: [item("failure:a")], nextCursor: nil,
                                                    healthyPrinterCount: 1, capturedSession: sessionA)
        XCTAssertEqual(committed, .committed)

        // Switch server/user (logout+login into a different namespace).
        authority.revoke()
        let nsB = FarmSnapshotFixtures.namespace()
        _ = try mint(authority, nsB)

        // The new namespace cannot read A's record.
        let bHydration = await adapter.loadCached()
        XCTAssertEqual(bHydration, .absent)

        // Switch back to A: its record is intact and readable.
        authority.revoke()
        _ = try mint(authority, nsA)
        let aHydration = await adapter.loadCached()
        guard case let .snapshot(payload, _) = aHydration else {
            return XCTFail("A's record should be intact after returning")
        }
        XCTAssertEqual(payload.items.map(\.id), ["failure:a"])
    }

    /// A refresh that started under namespace A but completes after a switch to B
    /// must NOT write — its captured session is no longer current (criteria 5, 6).
    func testCommitWithStaleCapturedSessionIsRejected() async throws {
        let root = newRoot()
        let (store, authority) = makeStore(root: root)
        let nsA = FarmSnapshotFixtures.namespace()
        let sessionA = try mint(authority, nsA)
        let adapter = AttentionReadCacheAdapter(store: store)

        authority.revoke()
        let nsB = FarmSnapshotFixtures.namespace()
        _ = try mint(authority, nsB)

        // Completing under the stale captured A session is refused.
        let result = await adapter.recordRefresh(
            items: [item("failure:leak")], nextCursor: nil,
            healthyPrinterCount: 1, capturedSession: sessionA
        )
        XCTAssertTrue(result == .namespaceMismatch || result == .superseded, "stale session cannot write; got \(result)")

        // Neither namespace received the leaked write.
        XCTAssertFalse(FileManager.default.fileExists(atPath: liveURL(root: root, nsA, "attention-feed").path))
        XCTAssertFalse(FileManager.default.fileExists(atPath: liveURL(root: root, nsB, "attention-feed").path))
    }

    /// A hydrate whose read is parked across a namespace switch resolves to
    /// `inactive` — it never applies the prior namespace's bytes (criterion 5).
    func testHydrateDuringSwitchYieldsInactive() async throws {
        let root = newRoot()
        let io = ControlledFarmSnapshotFileIO()
        let (store, authority) = makeStore(root: root, fileIO: io)
        let nsA = FarmSnapshotFixtures.namespace()
        let sessionA = try mint(authority, nsA)
        let adapter = AttentionReadCacheAdapter(store: store)

        let committed = await adapter.recordRefresh(items: [item("failure:a")], nextCursor: nil,
                                                    healthyPrinterCount: 1, capturedSession: sessionA)
        XCTAssertEqual(committed, .committed)

        // Park the hydrate read, switch namespace, then release.
        let barrier = AsyncBarrier()
        io.readDataBarrier = barrier
        addTeardownBlock { barrier.close() }

        async let hydration = adapter.loadCached()
        await barrier.waitUntilArrived()
        authority.revoke()
        _ = try mint(authority, FarmSnapshotFixtures.namespace())
        barrier.release()

        let result = await hydration
        XCTAssertEqual(result, .inactive, "a read that lost authority mid-flight never applies prior bytes")
    }

    // MARK: Recovery via #785

    func testCorruptRecordIsRecovered() async throws {
        let root = newRoot()
        let (store, authority) = makeStore(root: root)
        let ns = FarmSnapshotFixtures.namespace()
        _ = try mint(authority, ns)
        let adapter = AttentionReadCacheAdapter(store: store)

        let live = liveURL(root: root, ns, "attention-feed")
        try FileManager.default.createDirectory(at: live.deletingLastPathComponent(), withIntermediateDirectories: true)
        try Data("{ not json".utf8).write(to: live)

        let hydration = await adapter.loadCached()
        XCTAssertEqual(hydration, .recovered)
        XCTAssertFalse(FileManager.default.fileExists(atPath: live.path), "corrupt live record is quarantined away")
    }

    func testOldSchemaRecordIsRecovered() async throws {
        let root = newRoot()
        let (store, authority) = makeStore(root: root)
        let ns = FarmSnapshotFixtures.namespace()
        _ = try mint(authority, ns)
        let adapter = AttentionReadCacheAdapter(store: store)

        // Hand-craft a future/unsupported schema envelope.
        let future: [String: Any] = [
            "schemaVersion": 999,
            "featureKey": "attention-feed",
            "namespace": ["serverID": ns.serverID.uuidString, "userID": ns.userID.uuidString],
            "lastUpdatedAtMillis": 1,
            "kind": "snapshot",
            "payload": ["items": [], "nextCursor": NSNull(), "healthyPrinterCount": 0]
        ]
        let live = liveURL(root: root, ns, "attention-feed")
        try FileManager.default.createDirectory(at: live.deletingLastPathComponent(), withIntermediateDirectories: true)
        try JSONSerialization.data(withJSONObject: future).write(to: live)

        let hydration = await adapter.loadCached()
        XCTAssertEqual(hydration, .recovered)
    }

    /// Criterion 2 robustness — a durable write failure preserves the prior
    /// snapshot (never a torn/empty overwrite).
    func testPersistenceFailurePreservesPriorSnapshot() async throws {
        let root = newRoot()
        let io = ControlledFarmSnapshotFileIO()
        let (store, authority) = makeStore(root: root, fileIO: io)
        let ns = FarmSnapshotFixtures.namespace()
        let session = try mint(authority, ns)
        let clock = MutableClock(1_000)
        let adapter = AttentionReadCacheAdapter(store: store, now: clock.sendableNow)

        let good = await adapter.recordRefresh(items: [item("failure:good")], nextCursor: nil,
                                               healthyPrinterCount: 1, capturedSession: session)
        XCTAssertEqual(good, .committed)

        io.failPromote = true
        clock.set(2_000)
        let torn = await adapter.recordRefresh(
            items: [item("failure:torn")], nextCursor: nil,
            healthyPrinterCount: 9, capturedSession: session
        )
        XCTAssertEqual(torn, .persistenceFailure)

        io.failPromote = false
        let hydration = await adapter.loadCached()
        guard case let .snapshot(payload, millis) = hydration else {
            return XCTFail("prior good snapshot must survive a failed overwrite")
        }
        XCTAssertEqual(payload.items.map(\.id), ["failure:good"])
        XCTAssertEqual(millis, 1_000)
    }

    // MARK: 9 — Shared stale banner text + accessibility (size-class agnostic)

    /// The Attention/coverage stale shells reuse the SHIPPED
    /// `ConnectionStatusPresentation`. Its derivation has NO size-class branch, so
    /// the same words render identically on iPhone and iPad — proven by evaluating
    /// the pure model (the single source of truth both layouts read) directly.
    func testSharedStaleBannerTextAndAccessibilitySizeClassAgnostic() {
        let confirmed = Date(timeIntervalSince1970: 1_000)
        let now = Date(timeIntervalSince1970: 1_000 + 120) // 2 min later

        let offline = ConnectionStatusPresentation(
            status: .offline, lastConfirmedAt: confirmed, hasCache: true, now: now
        )
        XCTAssertTrue(offline.isStale)
        XCTAssertEqual(offline.label, "Offline · Showing cached fleet")
        XCTAssertEqual(offline.timestampText, "Last updated 2 min ago")
        XCTAssertTrue(offline.accessibilityLabel.contains("cached, read-only"),
                      "staleness is spoken in words, never color alone")
        XCTAssertTrue(offline.accessibilityLabel.contains("2 min ago"))

        let degraded = ConnectionStatusPresentation(
            status: .degraded, lastConfirmedAt: confirmed, hasCache: true, now: now
        )
        XCTAssertTrue(degraded.isStale)
        XCTAssertTrue(degraded.accessibilityLabel.contains("cached, read-only"))

        // Identical inputs → identical derivation regardless of device idiom.
        let repeatOffline = ConnectionStatusPresentation(
            status: .offline, lastConfirmedAt: confirmed, hasCache: true, now: now
        )
        XCTAssertEqual(offline, repeatOffline)
    }
}
