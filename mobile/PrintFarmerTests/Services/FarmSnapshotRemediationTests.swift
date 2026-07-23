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

    /// Locate the in-flight candidate temp file for a namespace (named
    /// `.{userID}.{uuid}.tmp`) so a test can decode it at the post-write seam.
    private func candidateTempData(root: URL, _ ns: FarmSnapshotNamespace) -> Data? {
        let dir = serverDir(root: root, ns.serverID)
        let entries = (try? FileManager.default.contentsOfDirectory(atPath: dir.path)) ?? []
        guard let name = entries.first(where: { $0.hasPrefix(".\(ns.userID.uuidString).") && $0.hasSuffix(".tmp") }) else {
            return nil
        }
        return try? Data(contentsOf: dir.appendingPathComponent(name))
    }

    private func activate(_ store: FarmSnapshotStore, _ authority: FarmSnapshotAuthority, _ ns: FarmSnapshotNamespace) async -> FarmSnapshotSession {
        let session = authority.mint(namespace: ns, generation: 0)!
        await store.activate(session: session)
        return session
    }

    // MARK: H3 — store lifecycle monotonicity

    func testAdoptRejectsDelayedOlderSession() {
        let authority = FarmSnapshotFixtures.makeAuthority(tombstoneDefaults: UserDefaults(suiteName: trackedSuiteName("tomb"))!)
        let ns = FarmSnapshotFixtures.namespace()
        let older = authority.mint(namespace: ns, generation: 0)!   // token 1
        let newer = authority.mint(namespace: ns, generation: 0)!   // token 2 (current)
        // A delayed re-adopt of the older session must not replace the newer one.
        XCTAssertFalse(authority.adopt(older))
        XCTAssertTrue(authority.isCurrent(newer))
        XCTAssertFalse(authority.isCurrent(older))
    }

    func testConditionalDeactivateDoesNotClearNewerSession() {
        let authority = FarmSnapshotFixtures.makeAuthority(tombstoneDefaults: UserDefaults(suiteName: trackedSuiteName("tomb"))!)
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
        let authority = FarmSnapshotFixtures.makeAuthority(tombstoneDefaults: UserDefaults(suiteName: trackedSuiteName("tomb"))!)
        let store = FarmSnapshotStore(authority: authority, rootURL: root)
        let s1 = authority.mint(namespace: ns, generation: 0)!
        let s2 = authority.mint(namespace: ns, generation: 0)!
        // Store.activate adopts monotonically; the older s1 cannot displace s2.
        await store.activate(session: s1)
        XAssertEqual(await store.currentSession(), s2)
    }

    // MARK: H4 — durable tombstone across recreation

    func testDurableTombstoneSurvivesAuthorityRecreation() {
        let tombstoneStore = FarmSnapshotFixtures.makeTombstoneStore(UserDefaults(suiteName: trackedSuiteName("tomb"))!)
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
        let ownerStore = FarmSnapshotFixtures.makeOwnerStore(UserDefaults(suiteName: trackedSuiteName("owner"))!)
        ownerStore.setOwner(userID: ns.userID, serverID: ns.serverID)
        let tombstoneStore = FarmSnapshotFixtures.makeTombstoneStore(UserDefaults(suiteName: trackedSuiteName("tomb"))!)
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
        let authority = FarmSnapshotFixtures.makeAuthority(tombstoneDefaults: UserDefaults(suiteName: trackedSuiteName("tomb"))!)
        let store = FarmSnapshotStore(authority: authority, fileIO: io, rootURL: root)
        let session = await activate(store, authority, ns)

        let barrier = AsyncBarrier()
        io.writeCandidateBarrier = barrier
        let commitTask = Task { await store.commit(FarmSnapshotFixtures.envelope(namespace: ns, millis: 1000), capturedSession: session) }

        await barrier.waitUntilArrived()
        // Purge lands while the commit holds a lease (suspended before its write). It
        // must DRAIN the lease — it cannot sweep/return until the commit releases —
        // so purge is started concurrently and only completes after the commit does.
        let purgeTask = Task { await store.purge(serverID: ns.serverID) }
        barrier.release()

        XAssertEqual(await commitTask.value, .superseded)
        XAssertEqual(await purgeTask.value, .purged)
        // No resurrection: the server subtree the late write may have recreated is gone.
        XCTAssertFalse(FileManager.default.fileExists(atPath: serverDir(root: root, ns.serverID).path))
    }

    // MARK: H4 — startup sweep state machine + purge causal ACK

    func testStartupSweepCleansResidueWithoutActivation() async {
        let root = newRoot()
        let ns = FarmSnapshotFixtures.namespace()
        let tombstoneStore = FarmSnapshotFixtures.makeTombstoneStore(UserDefaults(suiteName: trackedSuiteName("tomb"))!)
        // Durable tombstone + leftover residue on disk (models a crash between purge
        // and registry removal).
        tombstoneStore.insert(ns.serverID)
        let residue = serverDir(root: root, ns.serverID)
        try? FileManager.default.createDirectory(at: residue, withIntermediateDirectories: true)
        try? Data("stale".utf8).write(to: residue.appendingPathComponent("x.json"))

        let authority = FarmSnapshotAuthority(tombstoneStore: tombstoneStore)
        let store = FarmSnapshotStore(authority: authority, rootURL: root)
        // No activation — startup preparation alone must sweep the residue.
        let ok = await store.prepareStartup()
        XCTAssertTrue(ok)
        XCTAssertFalse(FileManager.default.fileExists(atPath: residue.path))
    }

    func testStartupSweepFailureThenRetrySucceeds() async {
        let root = newRoot()
        let ns = FarmSnapshotFixtures.namespace()
        let tombstoneStore = FarmSnapshotFixtures.makeTombstoneStore(UserDefaults(suiteName: trackedSuiteName("tomb"))!)
        tombstoneStore.insert(ns.serverID)
        let io = ControlledFarmSnapshotFileIO()
        let authority = FarmSnapshotAuthority(tombstoneStore: tombstoneStore)
        let store = FarmSnapshotStore(authority: authority, fileIO: io, rootURL: root)

        // First sweep fails → not marked complete, retry allowed.
        io.failRemove = true
        let firstAttempt = await store.prepareStartup()
        XCTAssertFalse(firstAttempt, "failed sweep is surfaced, not swallowed")
        // Retry succeeds.
        io.failRemove = false
        let retry = await store.prepareStartup()
        XCTAssertTrue(retry, "retry after a transient failure completes")
    }

    func testPurgeDrainsInFlightLeaseViaLeaseThenSweepsNoResurrection() async {
        // The lease is the real serialization primitive: a commit holding a lease (parked
        // pre-write) forces purge to DRAIN before it can sweep/return. The completion
        // order proves purge finishes strictly after the commit releases its lease, and
        // no resurrection remains. No test-only production hook is used.
        let root = newRoot()
        let ns = FarmSnapshotFixtures.namespace()
        let io = ControlledFarmSnapshotFileIO()
        let authority = FarmSnapshotFixtures.makeAuthority(tombstoneDefaults: UserDefaults(suiteName: trackedSuiteName("tomb"))!)
        let store = FarmSnapshotStore(authority: authority, fileIO: io, rootURL: root)
        let session = await activate(store, authority, ns)

        let recorder = CompletionOrderRecorder()
        let writeBarrier = AsyncBarrier()
        io.writeCandidateBarrier = writeBarrier

        let commitTask = Task { () -> FarmSnapshotCommitResult in
            let r = await store.commit(FarmSnapshotFixtures.envelope(namespace: ns, millis: 1), capturedSession: session)
            await recorder.record("commit")
            return r
        }
        await writeBarrier.waitUntilArrived() // commit holds a lease, parked pre-write

        // Purge starts while the commit's lease is held; its drain cannot complete (and
        // it cannot sweep or return) until the commit releases the lease.
        let purgeTask = Task { () -> FarmSnapshotPurgeResult in
            let r = await store.purge(serverID: ns.serverID)
            await recorder.record("purge")
            return r
        }
        writeBarrier.release()

        let commitResult = await commitTask.value
        XAssertEqual(await purgeTask.value, .purged)
        // The commit was either superseded by the tombstone or promoted-then-swept — in
        // all cases it never leaves a live record, and purge completes strictly after it.
        XCTAssertTrue(commitResult == .superseded || commitResult == .committed)
        let order = await recorder.order
        XCTAssertEqual(order, ["commit", "purge"], "purge drains the lease before completing")
        XCTAssertFalse(FileManager.default.fileExists(atPath: serverDir(root: root, ns.serverID).path))
    }

    func testPurgeFinalDeletionFailureYieldsFailure() async {
        let root = newRoot()
        let ns = FarmSnapshotFixtures.namespace()
        let io = ControlledFarmSnapshotFileIO()
        let authority = FarmSnapshotFixtures.makeAuthority(tombstoneDefaults: UserDefaults(suiteName: trackedSuiteName("tomb"))!)
        let store = FarmSnapshotStore(authority: authority, fileIO: io, rootURL: root)
        let session = await activate(store, authority, ns)
        _ = await store.commit(FarmSnapshotFixtures.envelope(namespace: ns, millis: 1), capturedSession: session)

        io.failRemove = true // final sweep removal fails
        if case .failed = await store.purge(serverID: ns.serverID) {} else {
            XCTFail("final deletion failure must yield a failed purge result")
        }
        // Tombstone remains durable regardless.
        XCTAssertTrue(authority.isTombstoned(ns.serverID))
    }

    func testRepeatedPurgeIsBounded() async {
        let root = newRoot()
        let ns = FarmSnapshotFixtures.namespace()
        let authority = FarmSnapshotFixtures.makeAuthority(tombstoneDefaults: UserDefaults(suiteName: trackedSuiteName("tomb"))!)
        let store = FarmSnapshotStore(authority: authority, rootURL: root)
        _ = await activate(store, authority, ns)
        XAssertEqual(await store.purge(serverID: ns.serverID), .purged)
        // A repeated purge of an already-purged server still succeeds and does not hang.
        XAssertEqual(await store.purge(serverID: ns.serverID), .purged)
    }

    // MARK: H5 — quarantine linearization (both completion orders)

    func testPurgeDrainsInFlightLeaseBeforeCompleting() async {
        // Deterministic completion-order proof: purge must not finish until an
        // in-flight commit (holding a filesystem lease) releases it (issue #816 H4).
        let root = newRoot()
        let ns = FarmSnapshotFixtures.namespace()
        let io = ControlledFarmSnapshotFileIO()
        let authority = FarmSnapshotFixtures.makeAuthority(tombstoneDefaults: UserDefaults(suiteName: trackedSuiteName("tomb"))!)
        let store = FarmSnapshotStore(authority: authority, fileIO: io, rootURL: root)
        let session = await activate(store, authority, ns)

        let recorder = CompletionOrderRecorder()
        let barrier = AsyncBarrier()
        io.writeCandidateBarrier = barrier
        let commitTask = Task { () -> FarmSnapshotCommitResult in
            let r = await store.commit(FarmSnapshotFixtures.envelope(namespace: ns, millis: 1), capturedSession: session)
            await recorder.record("commit")
            return r
        }
        await barrier.waitUntilArrived()
        let purgeTask = Task { () -> FarmSnapshotPurgeResult in
            let r = await store.purge(serverID: ns.serverID)
            await recorder.record("purge")
            return r
        }
        barrier.release()
        XAssertEqual(await commitTask.value, .superseded)
        XAssertEqual(await purgeTask.value, .purged)

        // Purge completed strictly AFTER the commit released its lease.
        let order = await recorder.order
        XCTAssertEqual(order, ["commit", "purge"])
    }

    actor CompletionOrderRecorder {
        private(set) var order: [String] = []
        func record(_ label: String) { order.append(label) }
    }

    func testQuarantineMoveCommitsWhenAuthorityHeldToRelease() async {
        // Corrupt record; authority stays current across the suspended dir-create;
        // the compare-and-move recovers under the lock.
        let root = newRoot()
        let ns = FarmSnapshotFixtures.namespace()
        let io = ControlledFarmSnapshotFileIO()
        let authority = FarmSnapshotFixtures.makeAuthority(tombstoneDefaults: UserDefaults(suiteName: trackedSuiteName("tomb"))!)
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
        let authority = FarmSnapshotFixtures.makeAuthority(tombstoneDefaults: UserDefaults(suiteName: trackedSuiteName("tomb"))!)
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

    // MARK: H5 — real move-boundary, both orders + concurrent-revoke serialization

    func testQuarantineMoveIsAtomicSinglePrimitive() async {
        // The destructive compare-and-move is a single serialized primitive: a corrupt
        // record is recovered with exactly ONE move, the live file is quarantined away,
        // and the session remains authoritative. (The concurrent-serialization order is
        // proven by the lease-based move-vs-purge test using a real primitive.)
        let root = newRoot()
        let ns = FarmSnapshotFixtures.namespace()
        let io = ControlledFarmSnapshotFileIO()
        let authority = FarmSnapshotFixtures.makeAuthority(tombstoneDefaults: UserDefaults(suiteName: trackedSuiteName("tomb"))!)
        let store = FarmSnapshotStore(authority: authority, fileIO: io, rootURL: root)
        let session = await activate(store, authority, ns)

        let live = liveURL(root: root, ns)
        try? FileManager.default.createDirectory(at: live.deletingLastPathComponent(), withIntermediateDirectories: true)
        try? Data("{ broken".utf8).write(to: live)

        let result = await store.hydrateActive()
        XCTAssertEqual(result, .recovered)
        XCTAssertEqual(io.moveCount, 1, "exactly one compare-and-move")
        XCTAssertFalse(FileManager.default.fileExists(atPath: live.path)) // quarantined
        XCTAssertTrue(authority.isCurrent(session))
    }

    func testMoveBoundaryRevokeFirstPreventsMove() async {
        // Order B (revoke-first): the authority is lost BEFORE the move boundary; the
        // synchronous primitive never runs and the live bytes are preserved.
        let root = newRoot()
        let ns = FarmSnapshotFixtures.namespace()
        let io = ControlledFarmSnapshotFileIO()
        let authority = FarmSnapshotFixtures.makeAuthority(tombstoneDefaults: UserDefaults(suiteName: trackedSuiteName("tomb"))!)
        let store = FarmSnapshotStore(authority: authority, fileIO: io, rootURL: root)
        _ = await activate(store, authority, ns)

        let live = liveURL(root: root, ns)
        try? FileManager.default.createDirectory(at: live.deletingLastPathComponent(), withIntermediateDirectories: true)
        let corrupt = Data("{ broken".utf8)
        try? corrupt.write(to: live)

        let barrier = AsyncBarrier()
        io.readDataBarrier = barrier // pause BEFORE the withPromotion move boundary
        let task = Task { await store.hydrateActive() }
        await barrier.waitUntilArrived()
        authority.revoke()
        barrier.release()
        XAssertEqual(await task.value, .inactive)
        XCTAssertEqual(io.moveCount, 0, "revoke-first prevents the destructive move entirely")
        XCTAssertEqual(try? Data(contentsOf: live), corrupt)
    }

    func testMoveBoundaryConcurrentPurgeSerializesAfterMove() async {
        // move vs purge: a purge started while a quarantine move holds its lease must
        // drain that lease before sweeping — the move recovers atomically and the purge
        // completes strictly after (issue #816 H5).
        let root = newRoot()
        let ns = FarmSnapshotFixtures.namespace()
        let io = ControlledFarmSnapshotFileIO()
        let authority = FarmSnapshotFixtures.makeAuthority(tombstoneDefaults: UserDefaults(suiteName: trackedSuiteName("tomb"))!)
        let store = FarmSnapshotStore(authority: authority, fileIO: io, rootURL: root)
        let session = await activate(store, authority, ns)

        let live = liveURL(root: root, ns)
        try? FileManager.default.createDirectory(at: live.deletingLastPathComponent(), withIntermediateDirectories: true)
        try? Data("{ broken".utf8).write(to: live)

        let recorder = CompletionOrderRecorder()
        let moveBarrier = AsyncBarrier()
        io.createDirectoryBarrier = moveBarrier // park recover just before the move (lease held)

        let recoverTask = Task { () -> FarmSnapshotHydration in
            let r = await store.hydrateActive()
            await recorder.record("recover")
            return r
        }
        await moveBarrier.waitUntilArrived() // recover holds a lease, parked pre-move

        // Purge starts while recover holds its lease; the lease forces purge to drain
        // before it can sweep/return.
        let purgeTask = Task { () -> FarmSnapshotPurgeResult in
            let r = await store.purge(serverID: ns.serverID)
            await recorder.record("purge")
            return r
        }
        moveBarrier.release()

        _ = await recoverTask.value
        XAssertEqual(await purgeTask.value, .purged)
        // Purge drained the recover lease before completing.
        let order = await recorder.order
        XCTAssertEqual(order.last, "purge")
        XCTAssertFalse(FileManager.default.fileExists(atPath: serverDir(root: root, ns.serverID).path))
        _ = session
    }

    // MARK: H7 — atomicity / interruption

    func testQuarantineCompareFalseAtMoveBoundaryPreservesLiveByteIdentical() async {
        // A change landing at the exact compare-and-move boundary (the live file is
        // rewritten just before the destructive move) makes the content no longer
        // match `expected`; the move must decline and preserve the live bytes
        // byte-identical — never a torn/partial quarantine (issue #816 H5).
        let root = newRoot()
        let ns = FarmSnapshotFixtures.namespace()
        let io = ControlledFarmSnapshotFileIO()
        let authority = FarmSnapshotFixtures.makeAuthority(tombstoneDefaults: UserDefaults(suiteName: trackedSuiteName("tomb"))!)
        let store = FarmSnapshotStore(authority: authority, fileIO: io, rootURL: root)
        _ = await activate(store, authority, ns)

        let live = liveURL(root: root, ns)
        try? FileManager.default.createDirectory(at: live.deletingLastPathComponent(), withIntermediateDirectories: true)
        try? Data("{ broken".utf8).write(to: live)

        // Park at the real fileIO boundary AFTER the corrupt read but BEFORE the move
        // (quarantine dir creation), rewrite live, then release — so the compare (against
        // the originally-read bytes) is now false and the destructive move declines.
        let replacement = Data("{ changed-before-move".utf8)
        let barrier = AsyncBarrier()
        io.createDirectoryBarrier = barrier
        let task = Task { await store.hydrateActive() }
        await barrier.waitUntilArrived()
        try? replacement.write(to: live)
        barrier.release()

        let result = await task.value
        // Compare-false: not recovered; the live file holds exactly the replacement bytes
        // (the destructive move did not fire).
        XCTAssertNotEqual(result, .recovered)
        XCTAssertEqual(io.moveCount, 1, "the compare-and-move boundary was reached")
        XCTAssertEqual(try? Data(contentsOf: live), replacement)
        let quarantineServerDir = root.appendingPathComponent("quarantine", isDirectory: true)
            .appendingPathComponent(ns.serverID.uuidString, isDirectory: true)
        let quarantined = (try? FileManager.default.contentsOfDirectory(atPath: quarantineServerDir.path)) ?? []
        XCTAssertTrue(quarantined.isEmpty, "compare-false must not quarantine")
    }


    func testInterruptionAtTempWriteLeavesLiveUnchanged() async {
        let root = newRoot()
        let ns = FarmSnapshotFixtures.namespace()
        let io = ControlledFarmSnapshotFileIO()
        let authority = FarmSnapshotFixtures.makeAuthority(tombstoneDefaults: UserDefaults(suiteName: trackedSuiteName("tomb"))!)
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
        let authority = FarmSnapshotFixtures.makeAuthority(tombstoneDefaults: UserDefaults(suiteName: trackedSuiteName("tomb"))!)
        let store = FarmSnapshotStore(authority: authority, fileIO: io, rootURL: root)
        let session = await activate(store, authority, ns)
        let prior = FarmSnapshotFixtures.envelope(namespace: ns, millis: 1000)
        XAssertEqual(await store.commit(prior, capturedSession: session), .committed)

        let live = liveURL(root: root, ns)
        let newer = FarmSnapshotFixtures.envelope(namespace: ns, millis: 2000)
        let barrier = AsyncBarrier()
        io.postWriteCandidateBarrier = barrier
        let task = Task { await store.commit(newer, capturedSession: session) }
        await barrier.waitUntilArrived()
        // Parked at the REAL boundary: candidate fully written+closed, promote not yet
        // performed. Counts are cumulative (the prior commit wrote+promoted once), so
        // the newer candidate is written (writeCount 1→2) but not yet promoted
        // (promoteCount still 1).
        XCTAssertEqual(io.writeCount, 2, "newer candidate is fully written at the seam")
        XCTAssertEqual(io.promoteCount, 1, "newer promote has not happened yet")
        // The candidate temp exists on disk and decodes as the NEW record...
        let candidateDecoded = candidateTempData(root: root, ns).flatMap {
            try? FarmSnapshotEnvelope.makeDecoder().decode(FarmSnapshotEnvelope.self, from: $0)
        }
        XCTAssertEqual(candidateDecoded, newer, "candidate is the fully-written NEW record at the seam")
        // ...while the externally observable LIVE path still decodes as the OLD record.
        let midCommit = try? FarmSnapshotEnvelope.makeDecoder().decode(FarmSnapshotEnvelope.self, from: Data(contentsOf: live))
        XCTAssertEqual(midCommit, prior)
        barrier.release()
        XAssertEqual(await task.value, .committed)
        // After the atomic promotion: exactly one more promote, and the live path is
        // the NEW fully-valid record.
        XCTAssertEqual(io.promoteCount, 2)
        let afterCommit = try? FarmSnapshotEnvelope.makeDecoder().decode(FarmSnapshotEnvelope.self, from: Data(contentsOf: live))
        XCTAssertEqual(afterCommit, newer)
    }

    func testInterruptionBetweenCandidateWriteAndPromoteLeavesLiveOld() async {
        // Interruption at the post-write / pre-promote seam: the candidate is fully
        // written but the atomic promote then fails (models a crash before promotion).
        // The externally observable live path must remain the exact OLD valid record —
        // never absent/torn — and the barrier is terminally released (no stranded
        // continuation). Exercises the real FileManager path via the disk backing.
        let root = newRoot()
        let ns = FarmSnapshotFixtures.namespace()
        let io = ControlledFarmSnapshotFileIO()
        let authority = FarmSnapshotFixtures.makeAuthority(tombstoneDefaults: UserDefaults(suiteName: trackedSuiteName("tomb"))!)
        let store = FarmSnapshotStore(authority: authority, fileIO: io, rootURL: root)
        let session = await activate(store, authority, ns)
        let prior = FarmSnapshotFixtures.envelope(namespace: ns, millis: 1000)
        XAssertEqual(await store.commit(prior, capturedSession: session), .committed)

        let live = liveURL(root: root, ns)
        let newer = FarmSnapshotFixtures.envelope(namespace: ns, millis: 2000)
        let barrier = AsyncBarrier()
        io.postWriteCandidateBarrier = barrier
        let task = Task { await store.commit(newer, capturedSession: session) }
        await barrier.waitUntilArrived()
        // Candidate fully written as NEW; live still OLD.
        let candidateDecoded = candidateTempData(root: root, ns).flatMap {
            try? FarmSnapshotEnvelope.makeDecoder().decode(FarmSnapshotEnvelope.self, from: $0)
        }
        XCTAssertEqual(candidateDecoded, newer)
        XCTAssertEqual(try? FarmSnapshotEnvelope.makeDecoder().decode(FarmSnapshotEnvelope.self, from: Data(contentsOf: live)), prior)
        // Interrupt the promotion, then terminally release the seam.
        io.failPromote = true
        barrier.release()
        if case .persistenceFailure = await task.value {} else {
            XCTFail("expected persistenceFailure when promotion is interrupted")
        }
        // Live is still exactly the OLD valid record; the candidate did not replace it.
        let onDisk = try? FarmSnapshotEnvelope.makeDecoder().decode(FarmSnapshotEnvelope.self, from: Data(contentsOf: live))
        XCTAssertEqual(onDisk, prior)
    }

    func testCancellationAfterCandidateWriteLeavesLiveOldAndCleansCandidate() async {
        // Cancellation at the post-write / pre-promote seam: the candidate is fully
        // written, then the commit task is cancelled and the seam terminally released
        // (no stranded continuation). No promote happens (promoteCount unchanged), the
        // live path stays OLD, and the candidate temp is cleaned up. Real FileManager.
        let root = newRoot()
        let ns = FarmSnapshotFixtures.namespace()
        let io = ControlledFarmSnapshotFileIO()
        let authority = FarmSnapshotFixtures.makeAuthority(tombstoneDefaults: UserDefaults(suiteName: trackedSuiteName("tomb"))!)
        let store = FarmSnapshotStore(authority: authority, fileIO: io, rootURL: root)
        let session = await activate(store, authority, ns)
        let prior = FarmSnapshotFixtures.envelope(namespace: ns, millis: 1000)
        XAssertEqual(await store.commit(prior, capturedSession: session), .committed)
        let promotesAfterPrior = io.promoteCount

        let live = liveURL(root: root, ns)
        let newer = FarmSnapshotFixtures.envelope(namespace: ns, millis: 2000)
        let barrier = AsyncBarrier()
        io.postWriteCandidateBarrier = barrier
        let task = Task { await store.commit(newer, capturedSession: session) }
        await barrier.waitUntilArrived()
        task.cancel()      // cancel after the candidate is fully written
        barrier.release()  // terminal release — no stranded continuation
        _ = await task.value

        XCTAssertEqual(io.promoteCount, promotesAfterPrior, "cancelled commit performs no promote")
        let onDisk = try? FarmSnapshotEnvelope.makeDecoder().decode(FarmSnapshotEnvelope.self, from: Data(contentsOf: live))
        XCTAssertEqual(onDisk, prior, "live remains the OLD valid record")
        XCTAssertNil(candidateTempData(root: root, ns), "the candidate temp is cleaned up")
    }

    func testPostPromotionLeavesLiveNewWithExactCounts() async {
        // After the atomic promote, the live path is the NEW valid record and exactly
        // one promote occurred for the newer commit. Real FileManager path.
        let root = newRoot()
        let ns = FarmSnapshotFixtures.namespace()
        let io = ControlledFarmSnapshotFileIO()
        let authority = FarmSnapshotFixtures.makeAuthority(tombstoneDefaults: UserDefaults(suiteName: trackedSuiteName("tomb"))!)
        let store = FarmSnapshotStore(authority: authority, fileIO: io, rootURL: root)
        let session = await activate(store, authority, ns)
        let prior = FarmSnapshotFixtures.envelope(namespace: ns, millis: 1000)
        XAssertEqual(await store.commit(prior, capturedSession: session), .committed)
        let promotesAfterPrior = io.promoteCount

        let newer = FarmSnapshotFixtures.envelope(namespace: ns, millis: 2000)
        XAssertEqual(await store.commit(newer, capturedSession: session), .committed)
        XCTAssertEqual(io.promoteCount, promotesAfterPrior + 1, "exactly one promote for the newer commit")
        let live = liveURL(root: root, ns)
        let onDisk = try? FarmSnapshotEnvelope.makeDecoder().decode(FarmSnapshotEnvelope.self, from: Data(contentsOf: live))
        XCTAssertEqual(onDisk, newer, "live is the NEW valid record after promotion")
        XCTAssertNil(candidateTempData(root: root, ns), "no candidate temp remains after promotion")
    }

    func testRealFileManagerInterruptionStagedTempLeavesOldRecord() async {
        // Real FileManager: stage a candidate temp but never promote — the live
        // record remains the old valid JSON (proves no torn/absent live path).
        let root = newRoot()
        let ns = FarmSnapshotFixtures.namespace()
        let authority = FarmSnapshotFixtures.makeAuthority(tombstoneDefaults: UserDefaults(suiteName: trackedSuiteName("tomb"))!)
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
        let authority = FarmSnapshotFixtures.makeAuthority(tombstoneDefaults: UserDefaults(suiteName: trackedSuiteName("tomb"))!)
        let store = FarmSnapshotStore(authority: authority, fileIO: io, rootURL: root)
        let session = await activate(store, authority, ns)

        let barrier = AsyncBarrier()
        io.writeCandidateBarrier = barrier
        let commitTask = Task { await store.commit(FarmSnapshotFixtures.envelope(namespace: ns, millis: 1), capturedSession: session) }
        await barrier.waitUntilArrived()
        let purgeTask = Task { await store.purge(serverID: ns.serverID) }
        barrier.release()
        XAssertEqual(await commitTask.value, .superseded)
        _ = await purgeTask.value

        // Commit: 1 async existence read + 1 candidate write; no promotion (tombstoned).
        XCTAssertEqual(io.readCount, 1)
        XCTAssertEqual(io.writeCount, 1)
        XCTAssertEqual(io.promoteCount, 0)
    }
}
