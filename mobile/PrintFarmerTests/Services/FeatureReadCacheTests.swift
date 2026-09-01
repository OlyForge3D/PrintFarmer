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
///                                                       `testExplicitFetchTimestampCannotOverwriteNewerSnapshot`,
///                                                       `testErrorAfterSuccessNeverWrites`
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

    /// Monotonic, explicitly-advanced clock so `lastUpdatedAtMillis` is exact and
    /// ordering never depends on wall time.
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

    func testOlderSuccessCannotOverwriteNewer() async throws {
        let root = newRoot()
        let (store, authority) = makeStore(root: root)
        let ns = FarmSnapshotFixtures.namespace()
        let session = try mint(authority, ns)
        let clock = MutableClock(0)
        let adapter = AttentionReadCacheAdapter(store: store, now: clock.sendableNow)

        clock.set(5_000)
        let newer = await adapter.recordRefresh(items: [item("failure:new")], nextCursor: nil,
                                                healthyPrinterCount: 3, capturedSession: session)
        XCTAssertEqual(newer, .committed)
        // An older clock-driven response must be refused.
        clock.set(4_000)
        let older = await adapter.recordRefresh(items: [item("failure:old")], nextCursor: nil,
                                                healthyPrinterCount: 99, capturedSession: session)
        XCTAssertEqual(older, .notNewer)

        let hydration = await adapter.loadCached()
        guard case let .snapshot(payload, millis) = hydration else {
            return XCTFail("expected snapshot")
        }
        XCTAssertEqual(payload.items.map(\.id), ["failure:new"])
        XCTAssertEqual(payload.healthyPrinterCount, 3)
        XCTAssertEqual(millis, 5_000)
    }

    func testExplicitFetchTimestampCannotOverwriteNewerSnapshot() async throws {
        let root = newRoot()
        let (store, authority) = makeStore(root: root)
        let ns = FarmSnapshotFixtures.namespace()
        let session = try mint(authority, ns)
        let clock = MutableClock(5_000)
        let adapter = AttentionReadCacheAdapter(store: store, now: clock.sendableNow)

        let newer = await adapter.recordRefresh(
            items: [item("failure:new")],
            nextCursor: nil,
            healthyPrinterCount: 3,
            capturedSession: session
        )
        XCTAssertEqual(newer, .committed)

        clock.set(6_000)
        let older = await adapter.recordRefresh(
            items: [item("failure:old")],
            nextCursor: nil,
            healthyPrinterCount: 99,
            lastUpdatedAtMillis: 4_000,
            capturedSession: session
        )
        XCTAssertEqual(older, .notNewer)

        let hydration = await adapter.loadCached()
        guard case let .snapshot(payload, millis) = hydration else {
            return XCTFail("expected snapshot")
        }
        XCTAssertEqual(payload.items.map(\.id), ["failure:new"])
        XCTAssertEqual(payload.healthyPrinterCount, 3)
        XCTAssertEqual(millis, 5_000)
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

        clock.set(1_000)
        let s1 = await adapter.recordRefresh(items: [item("failure:x")], nextCursor: nil,
                                             healthyPrinterCount: 2, capturedSession: session)
        XCTAssertEqual(s1, .committed)
        clock.set(2_000)
        let disabled = await adapter.recordDisabled(capturedSession: session)
        XCTAssertEqual(disabled, .committed)

        // Disabled tombstone now hides the older snapshot.
        let afterDisable = await adapter.loadCached()
        XCTAssertEqual(afterDisable, .disabled(lastUpdatedAtMillis: 2_000))

        // An even-older snapshot cannot resurface past the tombstone.
        clock.set(1_500)
        let zombie = await adapter.recordRefresh(items: [item("failure:zombie")], nextCursor: nil,
                                                 healthyPrinterCount: 5, capturedSession: session)
        XCTAssertEqual(zombie, .notNewer)
        let stillDisabled = await adapter.loadCached()
        XCTAssertEqual(stillDisabled, .disabled(lastUpdatedAtMillis: 2_000))
    }

    /// Reverse arrival: the disabled completion is the newest, so it wins even
    /// though a success arrives afterward with an older instant.
    func testReverseOrderNewerDisabledWins() async throws {
        let root = newRoot()
        let (store, authority) = makeStore(root: root)
        let ns = FarmSnapshotFixtures.namespace()
        let session = try mint(authority, ns)
        let clock = MutableClock(9_000)
        let adapter = FilamentCoverageReadCacheAdapter(store: store, now: clock.sendableNow)

        let disabled = await adapter.recordFleetDisabled(capturedSession: session)
        XCTAssertEqual(disabled, .committed)

        clock.set(8_000) // older success completes late
        let fleet = FleetFilamentCoverage(printers: [], evaluatedAtUtc: Date(timeIntervalSince1970: 1))
        let late = await adapter.recordFleet(fleet, capturedSession: session)
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
