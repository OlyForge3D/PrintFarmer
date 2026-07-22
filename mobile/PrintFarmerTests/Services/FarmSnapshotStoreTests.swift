import XCTest
@testable import PrintFarmer

/// Core store behavior (issue #816, Gates C/D/F/G) driven on a real temp root.
final class FarmSnapshotStoreTests: XCTestCase {

    private var roots: [URL] = []

    override func tearDown() {
        for root in roots {
            try? FileManager.default.removeItem(at: root)
        }
        roots = []
        super.tearDown()
    }

    private func newRoot() -> URL {
        let root = FarmSnapshotFixtures.tempRoot()
        roots.append(root)
        return root
    }

    /// Test-side mirror of the store's on-disk layout for direct disk assertions.
    private func liveURL(root: URL, _ namespace: FarmSnapshotNamespace) -> URL {
        root.appendingPathComponent("servers", isDirectory: true)
            .appendingPathComponent(namespace.serverID.uuidString, isDirectory: true)
            .appendingPathComponent("\(namespace.userID.uuidString).json")
    }

    private func makeStore(
        root: URL,
        fileIO: FarmSnapshotFileIO = DiskFarmSnapshotFileIO()
    ) -> (FarmSnapshotStore, FarmSnapshotAuthority) {
        let authority = FarmSnapshotFixtures.makeAuthority()
        let store = FarmSnapshotStore(authority: authority, fileIO: fileIO, rootURL: root)
        return (store, authority)
    }

    private func activate(
        _ store: FarmSnapshotStore,
        _ authority: FarmSnapshotAuthority,
        _ namespace: FarmSnapshotNamespace,
        generation: Int = 0
    ) async -> FarmSnapshotSession {
        let session = authority.mint(namespace: namespace, generation: generation)!
        await store.activate(session: session)
        return session
    }

    // MARK: Hydrate distinctions

    func testHydrateInactiveWithNoSession() async {
        let (store, _) = makeStore(root: newRoot())
        let hydration = await store.hydrateActive()
        XCTAssertEqual(hydration, .inactive)
    }

    func testHydrateAbsentWhenActiveButNoRecord() async {
        let root = newRoot()
        let (store, authority) = makeStore(root: root)
        _ = await activate(store, authority, FarmSnapshotFixtures.namespace())
        let hydration = await store.hydrateActive()
        XCTAssertEqual(hydration, .absent)
    }

    func testCommitThenHydrateReturnsSnapshot() async {
        let root = newRoot()
        let namespace = FarmSnapshotFixtures.namespace()
        let (store, authority) = makeStore(root: root)
        let session = await activate(store, authority, namespace)

        let envelope = FarmSnapshotFixtures.envelope(namespace: namespace, millis: 1000)
        let result = await store.commit(envelope, capturedSession: session)
        XCTAssertEqual(result, .committed)

        let hydration = await store.hydrateActive()
        XCTAssertEqual(hydration, .snapshot(envelope))
    }

    func testPresentEmptyIsDistinctFromAbsent() async {
        let root = newRoot()
        let namespace = FarmSnapshotFixtures.namespace()
        let (store, authority) = makeStore(root: root)
        let session = await activate(store, authority, namespace)

        // Commit a present-but-empty farm.
        let empty = FarmSnapshotFixtures.envelope(namespace: namespace, millis: 500, printers: [])
        XAssertEqual(await store.commit(empty, capturedSession: session), .committed)

        if case .snapshot(let env) = await store.hydrateActive() {
            XCTAssertEqual(env.payload.count, 0)
        } else {
            XCTFail("expected present-empty snapshot, not absent")
        }
    }

    // MARK: Monotonic guard

    func testCommitOlderIsPreserved() async {
        let root = newRoot()
        let namespace = FarmSnapshotFixtures.namespace()
        let (store, authority) = makeStore(root: root)
        let session = await activate(store, authority, namespace)

        let newer = FarmSnapshotFixtures.envelope(namespace: namespace, millis: 2000)
        XAssertEqual(await store.commit(newer, capturedSession: session), .committed)
        let older = FarmSnapshotFixtures.envelope(namespace: namespace, millis: 1000)
        XAssertEqual(await store.commit(older, capturedSession: session), .notNewer)

        XAssertEqual(await store.hydrateActive(), .snapshot(newer))
    }

    func testCommitEqualTimestampIsPreserved() async {
        let root = newRoot()
        let namespace = FarmSnapshotFixtures.namespace()
        let (store, authority) = makeStore(root: root)
        let session = await activate(store, authority, namespace)

        let first = FarmSnapshotFixtures.envelope(namespace: namespace, millis: 2000, printers: [FarmSnapshotPrinter(FarmSnapshotFixtures.printerWithSecrets())])
        XAssertEqual(await store.commit(first, capturedSession: session), .committed)
        let equalStamp = FarmSnapshotFixtures.envelope(namespace: namespace, millis: 2000, printers: [])
        XAssertEqual(await store.commit(equalStamp, capturedSession: session), .notNewer)

        XAssertEqual(await store.hydrateActive(), .snapshot(first))
    }

    func testMonotonicOrderingSurvivesStoreAndAuthorityRecreation() async {
        let root = newRoot()
        let namespace = FarmSnapshotFixtures.namespace()

        let (store1, authority1) = makeStore(root: root)
        let session1 = await activate(store1, authority1, namespace)
        let committed = FarmSnapshotFixtures.envelope(namespace: namespace, millis: 100_900)
        XAssertEqual(await store1.commit(committed, capturedSession: session1), .committed)

        // Recreate store + authority on the same disk (models a process relaunch).
        let (store2, authority2) = makeStore(root: root)
        let session2 = await activate(store2, authority2, namespace)
        let stale = FarmSnapshotFixtures.envelope(namespace: namespace, millis: 100_500)
        XAssertEqual(await store2.commit(stale, capturedSession: session2), .notNewer)
        XAssertEqual(await store2.hydrateActive(), .snapshot(committed))

        let fresh = FarmSnapshotFixtures.envelope(namespace: namespace, millis: 100_901)
        XAssertEqual(await store2.commit(fresh, capturedSession: session2), .committed)
        XAssertEqual(await store2.hydrateActive(), .snapshot(fresh))
    }

    // MARK: Schema + namespace + integrity

    func testCommitUnsupportedSchemaRejectedBeforeWrite() async {
        let root = newRoot()
        let namespace = FarmSnapshotFixtures.namespace()
        let (store, authority) = makeStore(root: root)
        let session = await activate(store, authority, namespace)

        let bad = FarmSnapshotEnvelope(
            schemaVersion: FarmSnapshotEnvelope.currentSchemaVersion + 5,
            namespace: namespace,
            payload: [],
            lastUpdatedAtMillis: 1000
        )
        XAssertEqual(await store.commit(bad, capturedSession: session), .schemaUnsupported)
        // Nothing was written.
        XCTAssertFalse(FileManager.default.fileExists(atPath: liveURL(root: root, namespace).path))
        XAssertEqual(await store.hydrateActive(), .absent)
    }

    func testCommitNamespaceMismatchRejected() async {
        let root = newRoot()
        let namespace = FarmSnapshotFixtures.namespace()
        let (store, authority) = makeStore(root: root)
        let session = await activate(store, authority, namespace)

        let other = FarmSnapshotFixtures.envelope(namespace: FarmSnapshotFixtures.namespace(), millis: 1000)
        XAssertEqual(await store.commit(other, capturedSession: session), .namespaceMismatch)
    }

    func testCommitOverCorruptExistingFailsClosed() async {
        let root = newRoot()
        let namespace = FarmSnapshotFixtures.namespace()
        let (store, authority) = makeStore(root: root)
        let session = await activate(store, authority, namespace)

        // Seed a corrupt live record.
        let live = liveURL(root: root, namespace)
        try? FileManager.default.createDirectory(at: live.deletingLastPathComponent(), withIntermediateDirectories: true)
        let corruptBytes = Data("{ not json".utf8)
        try? corruptBytes.write(to: live)

        let envelope = FarmSnapshotFixtures.envelope(namespace: namespace, millis: 9999)
        XAssertEqual(await store.commit(envelope, capturedSession: session), .integrityFailure)
        // The exact prior (corrupt) bytes are untouched — never treated as absence.
        XCTAssertEqual(try? Data(contentsOf: live), corruptBytes)
    }

    func testCommitReadErrorFailsClosed() async {
        let root = newRoot()
        let namespace = FarmSnapshotFixtures.namespace()
        let io = ControlledFarmSnapshotFileIO()
        let (store, authority) = makeStore(root: root, fileIO: io)
        let session = await activate(store, authority, namespace)

        io.failReadDataSync = false
        // Make the early async read throw.
        final class ThrowingReadIO: FarmSnapshotFileIO, @unchecked Sendable {
            struct E: Error {}
            func readData(at url: URL) async throws -> Data? { throw E() }
            func writeCandidate(_ data: Data, to url: URL) async throws {}
            func removeItem(at url: URL) async throws {}
            func createDirectory(at url: URL) async throws {}
            func readDataSync(at url: URL) throws -> Data? { nil }
            func promoteAtomically(candidate: URL, to live: URL) throws {}
            func moveIfContentEquals(from: URL, to: URL, expected: Data) throws -> Bool { false }
        }
        let (throwStore, throwAuth) = makeStore(root: root, fileIO: ThrowingReadIO())
        let throwSession = await activate(throwStore, throwAuth, namespace)
        let envelope = FarmSnapshotFixtures.envelope(namespace: namespace, millis: 1)
        XAssertEqual(await throwStore.commit(envelope, capturedSession: throwSession), .integrityFailure)
        _ = (store, session)
    }

    // MARK: Persistence faults preserve prior bytes

    func testWriteCandidateFailurePreservesOldEnvelope() async {
        let root = newRoot()
        let namespace = FarmSnapshotFixtures.namespace()
        let io = ControlledFarmSnapshotFileIO()
        let (store, authority) = makeStore(root: root, fileIO: io)
        let session = await activate(store, authority, namespace)

        let prior = FarmSnapshotFixtures.envelope(namespace: namespace, millis: 1000)
        XAssertEqual(await store.commit(prior, capturedSession: session), .committed)

        io.failWriteCandidate = true
        let attempt = FarmSnapshotFixtures.envelope(namespace: namespace, millis: 2000)
        if case .persistenceFailure = await store.commit(attempt, capturedSession: session) {
            // expected
        } else {
            XCTFail("expected persistenceFailure")
        }
        XAssertEqual(await store.hydrateActive(), .snapshot(prior))
    }

    func testPromoteFailurePreservesOldEnvelopeAndLeavesNoLiveCandidate() async {
        let root = newRoot()
        let namespace = FarmSnapshotFixtures.namespace()
        let io = ControlledFarmSnapshotFileIO()
        let (store, authority) = makeStore(root: root, fileIO: io)
        let session = await activate(store, authority, namespace)

        let prior = FarmSnapshotFixtures.envelope(namespace: namespace, millis: 1000)
        XAssertEqual(await store.commit(prior, capturedSession: session), .committed)

        io.failPromote = true
        let attempt = FarmSnapshotFixtures.envelope(namespace: namespace, millis: 2000)
        if case .persistenceFailure = await store.commit(attempt, capturedSession: session) {
        } else {
            XCTFail("expected persistenceFailure")
        }
        // Prior bytes are the live record; the rejected candidate never became live.
        XAssertEqual(await store.hydrateActive(), .snapshot(prior))
    }

    func testFirstWritePromoteFailureLeavesAbsent() async {
        let root = newRoot()
        let namespace = FarmSnapshotFixtures.namespace()
        let io = ControlledFarmSnapshotFileIO()
        io.failPromote = true
        let (store, authority) = makeStore(root: root, fileIO: io)
        let session = await activate(store, authority, namespace)

        let attempt = FarmSnapshotFixtures.envelope(namespace: namespace, millis: 2000)
        if case .persistenceFailure = await store.commit(attempt, capturedSession: session) {
        } else {
            XCTFail("expected persistenceFailure")
        }
        XAssertEqual(await store.hydrateActive(), .absent)
        XCTAssertFalse(FileManager.default.fileExists(atPath: liveURL(root: root, namespace).path))
    }

    // MARK: Quarantine / recovery

    func testHydrateQuarantinesCorruptRecordThenValidCommitSurvives() async {
        let root = newRoot()
        let namespace = FarmSnapshotFixtures.namespace()
        let (store, authority) = makeStore(root: root)
        let session = await activate(store, authority, namespace)

        // Seed corrupt live bytes.
        let live = liveURL(root: root, namespace)
        try? FileManager.default.createDirectory(at: live.deletingLastPathComponent(), withIntermediateDirectories: true)
        try? Data("{ broken".utf8).write(to: live)

        XAssertEqual(await store.hydrateActive(), .recovered)
        // The corrupt live path is cleared after quarantine.
        XCTAssertFalse(FileManager.default.fileExists(atPath: live.path))
        XAssertEqual(await store.hydrateActive(), .absent)

        // A subsequent valid commit hydrates as the newest.
        let envelope = FarmSnapshotFixtures.envelope(namespace: namespace, millis: 3000)
        XAssertEqual(await store.commit(envelope, capturedSession: session), .committed)
        XAssertEqual(await store.hydrateActive(), .snapshot(envelope))
    }

    func testHydrateUnknownSchemaQuarantined() async {
        let root = newRoot()
        let namespace = FarmSnapshotFixtures.namespace()
        let (store, authority) = makeStore(root: root)
        _ = await activate(store, authority, namespace)

        let live = liveURL(root: root, namespace)
        try? FileManager.default.createDirectory(at: live.deletingLastPathComponent(), withIntermediateDirectories: true)
        let future = FarmSnapshotEnvelope(
            schemaVersion: 999,
            namespace: namespace,
            payload: [],
            lastUpdatedAtMillis: 1
        )
        try? FarmSnapshotEnvelope.makeEncoder().encode(future).write(to: live)

        XAssertEqual(await store.hydrateActive(), .recovered)
    }

    func testCompareFalseDoesNotReportRecoveredAndKeepsNewer() async {
        // A move that sees changed bytes (a newer valid commit) must not remove
        // them and must not claim recovery.
        let root = newRoot()
        let namespace = FarmSnapshotFixtures.namespace()
        let io = ControlledFarmSnapshotFileIO()
        let (store, authority) = makeStore(root: root, fileIO: io)
        _ = await activate(store, authority, namespace)

        // Seed corrupt bytes, but overwrite the live file with a VALID newer record
        // during the createDirectory barrier so compare-and-move sees changed bytes.
        let live = liveURL(root: root, namespace)
        try? FileManager.default.createDirectory(at: live.deletingLastPathComponent(), withIntermediateDirectories: true)
        try? Data("{ broken".utf8).write(to: live)

        let newer = FarmSnapshotFixtures.envelope(namespace: namespace, millis: 7000)
        let barrier = AsyncBarrier()
        io.createDirectoryBarrier = barrier

        async let hydration = store.hydrateActive()
        await barrier.waitUntilArrived()
        // Replace the corrupt bytes with a valid newer record before the move.
        try? FarmSnapshotEnvelope.makeEncoder().encode(newer).write(to: live)
        barrier.release()

        let result = await hydration
        XCTAssertNotEqual(result, .recovered, "compare-false must not report recovery")
        // The newer valid file survives on disk.
        let survivor = try? FarmSnapshotEnvelope.makeDecoder().decode(FarmSnapshotEnvelope.self, from: Data(contentsOf: live))
        XCTAssertEqual(survivor, newer)
    }

    // MARK: Purge

    func testPurgeRemovesBaseQuarantineAndTempAndBlocksResurrection() async {
        let root = newRoot()
        let namespace = FarmSnapshotFixtures.namespace()
        let (store, authority) = makeStore(root: root)
        let session = await activate(store, authority, namespace)
        XAssertEqual(await store.commit(FarmSnapshotFixtures.envelope(namespace: namespace, millis: 1000), capturedSession: session), .committed)

        // Plant a stray temp + a quarantine artifact.
        let serverDir = root.appendingPathComponent("servers").appendingPathComponent(namespace.serverID.uuidString)
        try? Data("tmp".utf8).write(to: serverDir.appendingPathComponent(".stray.tmp"))
        let quarantineDir = root.appendingPathComponent("quarantine").appendingPathComponent(namespace.serverID.uuidString)
        try? FileManager.default.createDirectory(at: quarantineDir, withIntermediateDirectories: true)
        try? Data("q".utf8).write(to: quarantineDir.appendingPathComponent("old.json"))

        XAssertEqual(await store.purge(serverID: namespace.serverID), .purged)
        XCTAssertFalse(FileManager.default.fileExists(atPath: serverDir.path))
        XCTAssertFalse(FileManager.default.fileExists(atPath: quarantineDir.path))

        // Tombstoned: activation cannot resurrect the namespace.
        XCTAssertNil(authority.mint(namespace: namespace, generation: 1))
    }

    func testPurgeIsolatesOtherServers() async {
        let root = newRoot()
        let nsA = FarmSnapshotFixtures.namespace()
        let nsB = FarmSnapshotFixtures.namespace()
        let (store, authority) = makeStore(root: root)

        let sessionA = await activate(store, authority, nsA)
        XAssertEqual(await store.commit(FarmSnapshotFixtures.envelope(namespace: nsA, millis: 1), capturedSession: sessionA), .committed)
        let sessionB = await activate(store, authority, nsB)
        XAssertEqual(await store.commit(FarmSnapshotFixtures.envelope(namespace: nsB, millis: 1), capturedSession: sessionB), .committed)

        XAssertEqual(await store.purge(serverID: nsA.serverID), .purged)

        // B is untouched and still hydrates.
        let sessionB2 = await activate(store, authority, nsB)
        if case .snapshot = await store.hydrateActive() {} else { XCTFail("B should survive A purge") }
        _ = sessionB2
    }

    func testPurgeSurfacesRemovalFailure() async {
        let root = newRoot()
        let namespace = FarmSnapshotFixtures.namespace()
        let io = ControlledFarmSnapshotFileIO()
        io.failRemove = true
        let (store, authority) = makeStore(root: root, fileIO: io)
        let session = await activate(store, authority, namespace)
        _ = session

        if case .failed(let count) = await store.purge(serverID: namespace.serverID) {
            XCTAssertGreaterThan(count, 0)
        } else {
            XCTFail("expected purge failure to be surfaced")
        }
        // Still tombstoned so a retry cannot resurrect.
        XCTAssertTrue(authority.isTombstoned(namespace.serverID))
    }

    // MARK: Real FileManager atomicity + exact counts

    func testRealFileManagerReplacementAlwaysYieldsValidJSON() async {
        let root = newRoot()
        let namespace = FarmSnapshotFixtures.namespace()
        let (store, authority) = makeStore(root: root)
        let session = await activate(store, authority, namespace)
        let live = liveURL(root: root, namespace)

        for index in 1...25 {
            let envelope = FarmSnapshotFixtures.envelope(namespace: namespace, millis: Int64(index))
            XAssertEqual(await store.commit(envelope, capturedSession: session), .committed)
            // The live file is always a fully-decodable record — never torn.
            let decoded = try? FarmSnapshotEnvelope.makeDecoder().decode(FarmSnapshotEnvelope.self, from: Data(contentsOf: live))
            XCTAssertEqual(decoded?.lastUpdatedAtMillis, Int64(index))
        }
    }

    func testExactCountsForFreshCommitIntoAbsentNamespace() async {
        let root = newRoot()
        let namespace = FarmSnapshotFixtures.namespace()
        let io = ControlledFarmSnapshotFileIO()
        let (store, authority) = makeStore(root: root, fileIO: io)
        let session = await activate(store, authority, namespace)

        XAssertEqual(await store.commit(FarmSnapshotFixtures.envelope(namespace: namespace, millis: 1), capturedSession: session), .committed)
        // One async existence read, one sync durable read, one write, one promote.
        XCTAssertEqual(io.readCount, 1)
        XCTAssertEqual(io.readSyncCount, 1)
        XCTAssertEqual(io.writeCount, 1)
        XCTAssertEqual(io.promoteCount, 1)
    }
}
