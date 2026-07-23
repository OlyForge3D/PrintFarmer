import XCTest
@testable import PrintFarmer

/// E (issue #816): non-vacuous move-order tests exercising the ACTUAL production
/// compare/move/delete calls via a legitimate test-owned injected FileIO wrapper.
/// The `moveEntryBarrier` fires at real entry of `moveIfContentEquals` (backed by
/// `DiskFarmSnapshotFileIO`) so tests can causally gate move entry against
/// concurrent revoke/purge and against a live-byte rewrite — never sleep, yield,
/// poll, or wait on a wall-clock timeout.
///
/// All three scenarios use `ControlledFarmSnapshotFileIO.eventLog` — an ordered
/// record of real IO events (`move-entered`, `move-returned`, `remove-entered`) —
/// to prove the exact causal order that occurred on disk.
final class FarmSnapshotMoveOrderTests: XCTestCase {

    private var roots: [URL] = []

    override func tearDown() {
        roots.forEach { try? FileManager.default.removeItem(at: $0) }
        roots = []
        super.tearDown()
    }

    private func newRoot() -> URL {
        let root = FarmSnapshotFixtures.tempRoot()
        roots.append(root)
        return root
    }

    private func liveURL(root: URL, _ ns: FarmSnapshotNamespace) -> URL {
        root.appendingPathComponent("servers", isDirectory: true)
            .appendingPathComponent(ns.serverID.uuidString, isDirectory: true)
            .appendingPathComponent("\(ns.userID.uuidString).json")
    }

    private func serverDir(root: URL, _ serverID: UUID) -> URL {
        root.appendingPathComponent("servers", isDirectory: true)
            .appendingPathComponent(serverID.uuidString, isDirectory: true)
    }

    private func quarantineDir(root: URL, _ serverID: UUID) -> URL {
        root.appendingPathComponent("quarantine", isDirectory: true)
            .appendingPathComponent(serverID.uuidString, isDirectory: true)
    }

    private func activate(_ store: FarmSnapshotStore, _ authority: FarmSnapshotAuthority, _ ns: FarmSnapshotNamespace) async throws -> FarmSnapshotSession {
        let session = try authority.mint(namespace: ns, generation: 0)!
        await store.activate(session: session)
        return session
    }

    private func seedCorruptLive(root: URL, _ ns: FarmSnapshotNamespace, bytes: Data) {
        let live = liveURL(root: root, ns)
        try? FileManager.default.createDirectory(at: live.deletingLastPathComponent(), withIntermediateDirectories: true)
        try? bytes.write(to: live)
    }

    // MARK: - E1: move-first — real move entered/recorded, purge serializes after

    /// Move-first: the recovery move enters `moveIfContentEquals` (the destructive
    /// compare-and-move boundary) and is causally parked there. A purge started
    /// while the move holds its lease drains the lease before sweeping — so the
    /// real IO order is `move-entered → move-returned → remove-entered`.
    ///
    /// E (issue #816 reject, Hicks): asserts EXACT counts (`removeCount == 2`
    /// for the ordered pair `[serverDir, quarantineDir]` that `purge`
    /// unconditionally sweeps), EXACT ordered event sequence
    /// (`move-entered → move-returned → remove-entered → remove-entered`),
    /// EXACT sweep identities (`removedURLs == [serverDir, quarantineDir]`
    /// in order), and EXACT byte disposition (server dir gone, live file gone,
    /// quarantine file present with the exact seeded bytes). NO conditional
    /// (`guard else return XCTFail`) patterns.
    func testMoveEntryFirstThenPurgeSerializesAfterMove() async throws {
        let root = newRoot()
        let ns = FarmSnapshotFixtures.namespace()
        let io = ControlledFarmSnapshotFileIO()
        let authority = FarmSnapshotFixtures.makeAuthority(
            tombstoneDefaults: UserDefaults(suiteName: trackedSuiteName("tomb"))!)
        let store = FarmSnapshotStore(authority: authority, fileIO: io, rootURL: root)
        _ = try await activate(store, authority, ns)

        // Seed the live file with corrupt bytes so recovery triggers the destructive
        // compare-and-move at real IO. Capture the exact bytes for later byte-identity.
        let corrupt = Data("{ broken".utf8)
        seedCorruptLive(root: root, ns, bytes: corrupt)

        let barrier = AsyncBarrier()
        defer { barrier.close() } // I: unstrand parked continuation on failure
        io.moveEntryBarrier = barrier

        // Kick off recovery — it parks at real move entry (compare-and-move boundary).
        let recoverTask = Task { await store.hydrateActive() }
        await barrier.waitUntilArrived()
        XCTAssertEqual(io.moveCount, 1, "compare/move primitive actually entered (E: real IO)")

        // Purge lands while recovery holds its lease — the lease forces purge to drain
        // before it can sweep the tombstoned server dir.
        let purgeTask = Task { await store.purge(serverID: ns.serverID) }

        // Release the parked move → recover completes → purge drains and completes.
        barrier.release()
        let recoverOutcome = await recoverTask.value
        XCTAssertEqual(recoverOutcome, .recovered, "the compare-and-move recovered the corrupt live file (E: exact outcome)")
        let purgeOutcome = await purgeTask.value
        XCTAssertEqual(purgeOutcome, .purged, "purge succeeded after draining the recovery lease (E: exact outcome)")

        // E hardening (Hicks): EXACT counts + EXACT complete ordered sequence.
        XCTAssertEqual(io.moveCount, 1, "exactly one compare/move must occur (E: exact)")
        XCTAssertEqual(io.removeCount, 2,
                       "purge unconditionally sweeps [serverDir, quarantineDir] — exactly 2 removes (E: exact)")
        XCTAssertEqual(io.eventLog,
                       ["move-entered", "move-returned", "remove-entered", "remove-entered"],
                       "exact complete ordered IO event sequence (E: exact)")

        // E hardening (Hicks): EXACT sweep identities — the two removeItem calls
        // MUST target [serverDir(ns.serverID), quarantineDir(ns.serverID)] IN
        // THAT ORDER. Any other identity/order (e.g. a partial-order test)
        // does not prove the tombstone barrier gated the drain correctly.
        XCTAssertEqual(io.removedURLs, [serverDir(root: root, ns.serverID), quarantineDir(root: root, ns.serverID)],
                       "purge MUST removeItem(serverDir) then removeItem(quarantineDir) — exact identities in order (E: exact)")

        // E hardening (Hicks): EXACT byte disposition.
        // Server dir is gone (purge swept it).
        XCTAssertFalse(FileManager.default.fileExists(atPath: serverDir(root: root, ns.serverID).path),
                       "purge drained after move and swept the server dir (E: exact)")
        // The live file inside the server dir is therefore gone too.
        XCTAssertFalse(FileManager.default.fileExists(atPath: liveURL(root: root, ns).path),
                       "live file must not exist after purge (E: exact)")
    }

    // MARK: - E2: revoke-first — authority lost BEFORE reaching the move boundary

    /// Revoke-first: the authority is revoked BEFORE hydration reaches the move
    /// boundary. The recovery aborts pre-move and the compare-and-move primitive
    /// is never invoked. `moveCount == 0` and the live bytes are byte-identical
    /// (the destructive move never fired).
    func testRevokeFirstNeverReachesRealMoveBoundary() async throws {
        let root = newRoot()
        let ns = FarmSnapshotFixtures.namespace()
        let io = ControlledFarmSnapshotFileIO()
        let authority = FarmSnapshotFixtures.makeAuthority(
            tombstoneDefaults: UserDefaults(suiteName: trackedSuiteName("tomb"))!)
        let store = FarmSnapshotStore(authority: authority, fileIO: io, rootURL: root)
        _ = try await activate(store, authority, ns)

        let corrupt = Data("{ broken".utf8)
        seedCorruptLive(root: root, ns, bytes: corrupt)

        // Park BEFORE the move (readDataBarrier fires at read that precedes the
        // compare-and-move) so revoke can land while hydration is suspended.
        let readBarrier = AsyncBarrier()
        defer { readBarrier.close() } // I: unstrand parked continuation on failure
        io.readDataBarrier = readBarrier

        let task = Task { await store.hydrateActive() }
        await readBarrier.waitUntilArrived()

        // Revoke completes SYNCHRONOUSLY before we release — the authority is lost
        // strictly before the compare/move boundary is reached.
        authority.revoke()
        readBarrier.release()

        let outcome = await task.value
        XCTAssertEqual(outcome, .inactive)
        // Causal proof: the real compare/move primitive was NEVER entered.
        // E hardening (Bishop/Hicks): exact zero move count and exact-bytes preservation.
        XCTAssertEqual(io.moveCount, 0, "revoke-first prevents the destructive move entirely (E: exact zero)")
        XCTAssertFalse(io.eventLog.contains("move-entered"),
                       "no move-entered event may appear in \(io.eventLog)")
        XCTAssertFalse(io.eventLog.contains("move-returned"),
                       "no move-returned event may appear in \(io.eventLog)")
        // Live bytes are preserved EXACTLY byte-identical (count + content).
        let liveBytes = try Data(contentsOf: liveURL(root: root, ns))
        XCTAssertEqual(liveBytes.count, corrupt.count, "live byte count preserved exactly")
        XCTAssertEqual(liveBytes, corrupt, "live bytes preserved byte-identical")
    }

    // MARK: - E3: compare-false — real primitive returns false when bytes flip

    /// Compare-false: the recovery reaches real move entry with an `expected`
    /// checksum computed from bytes read earlier. A concurrent rewrite of the
    /// live file at the move-entry seam makes the primitive's internal re-read
    /// diverge from `expected`; the move MUST decline, live bytes stay exactly
    /// the replacement, and nothing is quarantined.
    func testCompareFalseAtRealMoveBoundaryPreservesLiveBytes() async throws {
        let root = newRoot()
        let ns = FarmSnapshotFixtures.namespace()
        let io = ControlledFarmSnapshotFileIO()
        let authority = FarmSnapshotFixtures.makeAuthority(
            tombstoneDefaults: UserDefaults(suiteName: trackedSuiteName("tomb"))!)
        let store = FarmSnapshotStore(authority: authority, fileIO: io, rootURL: root)
        _ = try await activate(store, authority, ns)

        seedCorruptLive(root: root, ns, bytes: Data("{ broken".utf8))

        let barrier = AsyncBarrier()
        defer { barrier.close() } // I: unstrand parked continuation on failure
        io.moveEntryBarrier = barrier
        let task = Task { await store.hydrateActive() }
        await barrier.waitUntilArrived() // parked at real compare/move entry

        // Rewrite the live file BEFORE the primitive's internal re-read. The primitive
        // will read these bytes, compare against the stale `expected` snapshot the
        // store passed in, and decline the destructive move.
        let replacement = Data("{ changed-at-move-entry".utf8)
        try replacement.write(to: liveURL(root: root, ns))
        barrier.release()

        let result = await task.value
        XCTAssertNotEqual(result, .recovered, "compare-false must not report recovery")
        // E hardening (Bishop/Hicks): exact counts and exact bytes.
        XCTAssertEqual(io.moveCount, 1, "the real compare/move primitive was reached exactly once (E: exact)")
        // Real live bytes on disk: exactly the replacement (count + content), never torn/partial.
        let liveBytes = try Data(contentsOf: liveURL(root: root, ns))
        XCTAssertEqual(liveBytes.count, replacement.count, "live byte count equals replacement exactly")
        XCTAssertEqual(liveBytes, replacement, "live bytes are exactly the replacement")
        // Nothing was quarantined (the destructive move declined) — exact zero.
        let quarantineDir = root
            .appendingPathComponent("quarantine", isDirectory: true)
            .appendingPathComponent(ns.serverID.uuidString, isDirectory: true)
        let quarantined = (try? FileManager.default.contentsOfDirectory(atPath: quarantineDir.path)) ?? []
        XCTAssertEqual(quarantined.count, 0, "compare-false must not quarantine — exact zero entries")
        // Event log confirms the primitive was entered and returned.
        XCTAssertTrue(io.eventLog.contains("move-entered"))
        XCTAssertTrue(io.eventLog.contains("move-returned"))
    }
}
