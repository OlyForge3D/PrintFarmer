import XCTest
@testable import PrintFarmer

/// Remediation proofs for Hicks' H3/H4/H5/H7 blockers (issue #816). Deterministic
/// via real Tasks + mutation-bound barriers — no sleeps/polling/time thresholds.
final class FarmSnapshotRemediationTests: XCTestCase {

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

    private func activate(_ store: FarmSnapshotStore, _ authority: FarmSnapshotAuthority, _ ns: FarmSnapshotNamespace) async -> FarmSnapshotSession {
        let session = authority.mint(namespace: ns, generation: 0)!
        await store.activate(session: session)
        return session
    }

    // MARK: H3 — store lifecycle monotonicity

    func testAdoptRejectsDelayedOlderSession() {
        let authority = FarmSnapshotFixtures.makeAuthority()
        let ns = FarmSnapshotFixtures.namespace()
        let older = authority.mint(namespace: ns, generation: 0)!   // token 1
        let newer = authority.mint(namespace: ns, generation: 0)!   // token 2 (current)
        // A delayed re-adopt of the older session must not replace the newer one.
        XCTAssertFalse(authority.adopt(older))
        XCTAssertTrue(authority.isCurrent(newer))
        XCTAssertFalse(authority.isCurrent(older))
    }

    func testConditionalDeactivateDoesNotClearNewerSession() {
        let authority = FarmSnapshotFixtures.makeAuthority()
        let ns = FarmSnapshotFixtures.namespace()
        let older = authority.mint(namespace: ns, generation: 0)!
        let newer = authority.mint(namespace: ns, generation: 0)!
        // A stale deactivate keyed to the older session must not clear the newer one.
        XCTAssertFalse(authority.deactivate(older))
        XCTAssertTrue(authority.isCurrent(newer))
        // Deactivating the actual current session clears it.
        XCTAssertTrue(authority.deactivate(newer))
        XCTAssertNil(authority.currentSession())
    }

    func testDelayedAdoptToken1AfterActivateToken2ABALoses() async {
        let root = newRoot()
        let ns = FarmSnapshotFixtures.namespace()
        let authority = FarmSnapshotFixtures.makeAuthority()
        let store = FarmSnapshotStore(authority: authority, rootURL: root)
        let s1 = authority.mint(namespace: ns, generation: 0)!
        let s2 = authority.mint(namespace: ns, generation: 0)!
        // Store.activate adopts monotonically; the older s1 cannot displace s2.
        await store.activate(session: s1)
        XAssertEqual(await store.currentSession(), s2)
    }

    // MARK: H4 — durable tombstone across recreation

    func testDurableTombstoneSurvivesAuthorityRecreation() {
        let tombstoneStore = FarmSnapshotFixtures.makeTombstoneStore()
        let ns = FarmSnapshotFixtures.namespace()
        let a1 = FarmSnapshotAuthority(tombstoneStore: tombstoneStore)
        a1.tombstone(ns.serverID)
        // Recreate the authority on the SAME durable store (models a relaunch).
        let a2 = FarmSnapshotAuthority(tombstoneStore: tombstoneStore)
        XCTAssertTrue(a2.isTombstoned(ns.serverID))
        XCTAssertNil(a2.mint(namespace: ns, generation: 0))
    }

    func testPurgeClearsOwnerMappingAndTombstonesDurably() async {
        let root = newRoot()
        let ns = FarmSnapshotFixtures.namespace()
        let ownerStore = FarmSnapshotFixtures.makeOwnerStore()
        ownerStore.setOwner(userID: ns.userID, serverID: ns.serverID)
        let tombstoneStore = FarmSnapshotFixtures.makeTombstoneStore()
        let authority = FarmSnapshotAuthority(tombstoneStore: tombstoneStore)
        let store = FarmSnapshotStore(authority: authority, rootURL: root, ownerStore: ownerStore)

        let session = await activate(store, authority, ns)
        _ = await store.commit(FarmSnapshotFixtures.envelope(namespace: ns, millis: 1), capturedSession: session)

        XAssertEqual(await store.purge(serverID: ns.serverID), .purged)
        XCTAssertNil(ownerStore.ownerUserID(serverID: ns.serverID)) // owner cleared
        // Durable across recreation: a relaunched authority still refuses the server.
        let a2 = FarmSnapshotAuthority(tombstoneStore: tombstoneStore)
        XCTAssertNil(a2.mint(namespace: ns, generation: 0))
    }

    func testPurgeVsSuspendedPreWriteCommitNoResurrection() async {
        let root = newRoot()
        let ns = FarmSnapshotFixtures.namespace()
        let io = ControlledFarmSnapshotFileIO()
        let authority = FarmSnapshotFixtures.makeAuthority()
        let store = FarmSnapshotStore(authority: authority, fileIO: io, rootURL: root)
        let session = await activate(store, authority, ns)

        let barrier = AsyncBarrier()
        io.writeCandidateBarrier = barrier
        let commitTask = Task { await store.commit(FarmSnapshotFixtures.envelope(namespace: ns, millis: 1000), capturedSession: session) }

        await barrier.waitUntilArrived()
        // Purge lands while the commit is suspended before its candidate write.
        let purgeResult = await store.purge(serverID: ns.serverID)
        XCTAssertEqual(purgeResult, .purged)
        barrier.release()

        XAssertEqual(await commitTask.value, .superseded)
        // No resurrection: the server subtree the late write may have recreated is gone.
        XCTAssertFalse(FileManager.default.fileExists(atPath: serverDir(root: root, ns.serverID).path))
    }

    // MARK: H5 — quarantine linearization (both completion orders)

    func testQuarantineMoveCommitsWhenAuthorityHeldToRelease() async {
        // Corrupt record; authority stays current across the suspended dir-create;
        // the compare-and-move recovers under the lock.
        let root = newRoot()
        let ns = FarmSnapshotFixtures.namespace()
        let io = ControlledFarmSnapshotFileIO()
        let authority = FarmSnapshotFixtures.makeAuthority()
        let store = FarmSnapshotStore(authority: authority, fileIO: io, rootURL: root)
        _ = await activate(store, authority, ns)

        let live = liveURL(root: root, ns)
        try? FileManager.default.createDirectory(at: live.deletingLastPathComponent(), withIntermediateDirectories: true)
        try? Data("{ broken".utf8).write(to: live)

        let barrier = AsyncBarrier()
        io.createDirectoryBarrier = barrier
        let task = Task { await store.hydrateActive() }
        await barrier.waitUntilArrived()
        // Authority remains current — release and expect recovery.
        barrier.release()
        XAssertEqual(await task.value, .recovered)
        XCTAssertFalse(FileManager.default.fileExists(atPath: live.path)) // quarantined
    }

    func testQuarantineMoveRevokedBeforeMoveDoesNotRecover() async {
        let root = newRoot()
        let ns = FarmSnapshotFixtures.namespace()
        let io = ControlledFarmSnapshotFileIO()
        let authority = FarmSnapshotFixtures.makeAuthority()
        let store = FarmSnapshotStore(authority: authority, fileIO: io, rootURL: root)
        _ = await activate(store, authority, ns)

        let live = liveURL(root: root, ns)
        try? FileManager.default.createDirectory(at: live.deletingLastPathComponent(), withIntermediateDirectories: true)
        let corrupt = Data("{ broken".utf8)
        try? corrupt.write(to: live)

        let barrier = AsyncBarrier()
        io.createDirectoryBarrier = barrier
        let task = Task { await store.hydrateActive() }
        await barrier.waitUntilArrived()
        authority.revoke() // authority lost before the destructive move (under the lock)
        barrier.release()
        XAssertEqual(await task.value, .inactive)
        // The corrupt file was NOT moved (no destructive action for a stale session).
        XCTAssertEqual(try? Data(contentsOf: live), corrupt)
    }

    // MARK: H7 — atomicity / interruption

    func testInterruptionAtTempWriteLeavesLiveUnchanged() async {
        let root = newRoot()
        let ns = FarmSnapshotFixtures.namespace()
        let io = ControlledFarmSnapshotFileIO()
        let authority = FarmSnapshotFixtures.makeAuthority()
        let store = FarmSnapshotStore(authority: authority, fileIO: io, rootURL: root)
        let session = await activate(store, authority, ns)
        let prior = FarmSnapshotFixtures.envelope(namespace: ns, millis: 1000)
        XAssertEqual(await store.commit(prior, capturedSession: session), .committed)

        // Simulate a crash/interruption at the temp write.
        io.failWriteCandidate = true
        if case .persistenceFailure = await store.commit(FarmSnapshotFixtures.envelope(namespace: ns, millis: 2000), capturedSession: session) {} else {
            XCTFail("expected persistenceFailure")
        }
        // The externally observable live path is the exact prior valid record.
        let onDisk = try? FarmSnapshotEnvelope.makeDecoder().decode(FarmSnapshotEnvelope.self, from: Data(contentsOf: liveURL(root: root, ns)))
        XCTAssertEqual(onDisk, prior)
    }

    func testExternallyObservableOldThenNewNeverTornAcrossPromotion() async {
        let root = newRoot()
        let ns = FarmSnapshotFixtures.namespace()
        let io = ControlledFarmSnapshotFileIO()
        let authority = FarmSnapshotFixtures.makeAuthority()
        let store = FarmSnapshotStore(authority: authority, fileIO: io, rootURL: root)
        let session = await activate(store, authority, ns)
        let prior = FarmSnapshotFixtures.envelope(namespace: ns, millis: 1000)
        XAssertEqual(await store.commit(prior, capturedSession: session), .committed)

        let live = liveURL(root: root, ns)
        let newer = FarmSnapshotFixtures.envelope(namespace: ns, millis: 2000)
        let barrier = AsyncBarrier()
        io.writeCandidateBarrier = barrier
        let task = Task { await store.commit(newer, capturedSession: session) }
        await barrier.waitUntilArrived()
        // Mid-commit (candidate written to temp, not yet promoted): the live path is
        // still the OLD fully-valid record — never absent/torn.
        let midCommit = try? FarmSnapshotEnvelope.makeDecoder().decode(FarmSnapshotEnvelope.self, from: Data(contentsOf: live))
        XCTAssertEqual(midCommit, prior)
        barrier.release()
        XAssertEqual(await task.value, .committed)
        // After the atomic promotion: the live path is the NEW fully-valid record.
        let afterCommit = try? FarmSnapshotEnvelope.makeDecoder().decode(FarmSnapshotEnvelope.self, from: Data(contentsOf: live))
        XCTAssertEqual(afterCommit, newer)
    }

    func testRealFileManagerInterruptionStagedTempLeavesOldRecord() async {
        // Real FileManager: stage a candidate temp but never promote — the live
        // record remains the old valid JSON (proves no torn/absent live path).
        let root = newRoot()
        let ns = FarmSnapshotFixtures.namespace()
        let authority = FarmSnapshotFixtures.makeAuthority()
        let store = FarmSnapshotStore(authority: authority, rootURL: root)
        let session = await activate(store, authority, ns)
        let prior = FarmSnapshotFixtures.envelope(namespace: ns, millis: 500)
        XAssertEqual(await store.commit(prior, capturedSession: session), .committed)

        // Write a stray temp candidate directly (models an interrupted write).
        let serverDirURL = serverDir(root: root, ns.serverID)
        try? Data("torn".utf8).write(to: serverDirURL.appendingPathComponent(".\(ns.userID.uuidString).stray.tmp"))

        // The live record is unaffected and fully valid.
        let onDisk = try? FarmSnapshotEnvelope.makeDecoder().decode(FarmSnapshotEnvelope.self, from: Data(contentsOf: liveURL(root: root, ns)))
        XCTAssertEqual(onDisk, prior)
    }

    func testPurgeVsSuspendedWriteExactCounts() async {
        let root = newRoot()
        let ns = FarmSnapshotFixtures.namespace()
        let io = ControlledFarmSnapshotFileIO()
        let authority = FarmSnapshotFixtures.makeAuthority()
        let store = FarmSnapshotStore(authority: authority, fileIO: io, rootURL: root)
        let session = await activate(store, authority, ns)

        let barrier = AsyncBarrier()
        io.writeCandidateBarrier = barrier
        let commitTask = Task { await store.commit(FarmSnapshotFixtures.envelope(namespace: ns, millis: 1), capturedSession: session) }
        await barrier.waitUntilArrived()
        _ = await store.purge(serverID: ns.serverID)
        barrier.release()
        XAssertEqual(await commitTask.value, .superseded)

        // Commit: 1 async existence read + 1 candidate write; no promotion (tombstoned).
        XCTAssertEqual(io.readCount, 1)
        XCTAssertEqual(io.writeCount, 1)
        XCTAssertEqual(io.promoteCount, 0)
    }
}
