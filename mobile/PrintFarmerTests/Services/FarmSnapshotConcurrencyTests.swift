import XCTest
@testable import PrintFarmer

/// Race + fault ordering proofs (issue #816, Gate D/F). All ordering is
/// mutation-bound via `AsyncBarrier` and real `Task`s — no sleeps/polling.
final class FarmSnapshotConcurrencyTests: XCTestCase {

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

    private func liveURL(root: URL, _ namespace: FarmSnapshotNamespace) -> URL {
        root.appendingPathComponent("servers", isDirectory: true)
            .appendingPathComponent(namespace.serverID.uuidString, isDirectory: true)
            .appendingPathComponent("\(namespace.userID.uuidString).json")
    }

    private func makeStore(root: URL, io: FarmSnapshotFileIO) -> (FarmSnapshotStore, FarmSnapshotAuthority) {
        let authority = FarmSnapshotFixtures.makeAuthority(tombstoneDefaults: UserDefaults(suiteName: trackedSuiteName("tomb"))!)
        return (FarmSnapshotStore(authority: authority, fileIO: io, rootURL: root), authority)
    }

    private func activate(_ store: FarmSnapshotStore, _ authority: FarmSnapshotAuthority, _ ns: FarmSnapshotNamespace) async throws -> FarmSnapshotSession {
        let session = try authority.mint(namespace: ns, generation: 0)!
        await store.activate(session: session)
        return session
    }

    // MARK: Revoke / tombstone / cancel during a suspended candidate write

    func testRevokeDuringSuspendedWritePreservesPriorBytesAndDoesNotCommit() async throws {
        let root = newRoot()
        let ns = FarmSnapshotFixtures.namespace()
        let io = ControlledFarmSnapshotFileIO()
        let (store, authority) = makeStore(root: root, io: io)
        let session = try await activate(store, authority, ns)

        let prior = FarmSnapshotFixtures.envelope(namespace: ns, millis: 1000)
        XAssertEqual(await store.commit(prior, capturedSession: session), .committed)

        let barrier = AsyncBarrier()
        defer { barrier.close() } // I: unstrand any parked continuation on failure
        io.writeCandidateBarrier = barrier
        let attempt = FarmSnapshotFixtures.envelope(namespace: ns, millis: 2000)
        let task = Task { await store.commit(attempt, capturedSession: session) }

        await barrier.waitUntilArrived()
        authority.revoke() // lands while the candidate write is suspended
        barrier.release()

        XAssertEqual(await task.value, .superseded)
        // Prior bytes remain the exact live record; the candidate never promoted.
        let live = liveURL(root: root, ns)
        let onDisk = try? FarmSnapshotEnvelope.makeDecoder().decode(FarmSnapshotEnvelope.self, from: Data(contentsOf: live))
        XCTAssertEqual(onDisk, prior)
    }

    func testTombstoneDuringSuspendedWriteSuperseded() async throws {
        let root = newRoot()
        let ns = FarmSnapshotFixtures.namespace()
        let io = ControlledFarmSnapshotFileIO()
        let (store, authority) = makeStore(root: root, io: io)
        let session = try await activate(store, authority, ns)

        let barrier = AsyncBarrier()
        defer { barrier.close() } // I: unstrand any parked continuation on failure
        io.writeCandidateBarrier = barrier
        let attempt = FarmSnapshotFixtures.envelope(namespace: ns, millis: 2000)
        let task = Task { await store.commit(attempt, capturedSession: session) }

        await barrier.waitUntilArrived()
        try authority.tombstone(ns.serverID)
        barrier.release()

        XAssertEqual(await task.value, .superseded)
        XAssertEqual(await store.hydrateActive(), .inactive)
    }

    func testCancellationDuringSuspendedWriteDoesNotCommit() async throws {
        let root = newRoot()
        let ns = FarmSnapshotFixtures.namespace()
        let io = ControlledFarmSnapshotFileIO()
        let (store, authority) = makeStore(root: root, io: io)
        let session = try await activate(store, authority, ns)

        let prior = FarmSnapshotFixtures.envelope(namespace: ns, millis: 1000)
        XAssertEqual(await store.commit(prior, capturedSession: session), .committed)

        let barrier = AsyncBarrier()
        defer { barrier.close() } // I: unstrand any parked continuation on failure
        io.writeCandidateBarrier = barrier
        let attempt = FarmSnapshotFixtures.envelope(namespace: ns, millis: 2000)
        let task = Task { await store.commit(attempt, capturedSession: session) }

        await barrier.waitUntilArrived()
        task.cancel()
        barrier.release()

        XAssertEqual(await task.value, .superseded)
        XAssertEqual(await store.hydrateActive(), .snapshot(prior))
    }

    func testRevokeDuringSuspendedWriteWithCleanupFailureIsSurfaced() async throws {
        let root = newRoot()
        let ns = FarmSnapshotFixtures.namespace()
        let io = ControlledFarmSnapshotFileIO()
        let (store, authority) = makeStore(root: root, io: io)
        let session = try await activate(store, authority, ns)
        let prior = FarmSnapshotFixtures.envelope(namespace: ns, millis: 1000)
        XAssertEqual(await store.commit(prior, capturedSession: session), .committed)

        let barrier = AsyncBarrier()
        defer { barrier.close() } // I: unstrand any parked continuation on failure
        io.writeCandidateBarrier = barrier
        let attempt = FarmSnapshotFixtures.envelope(namespace: ns, millis: 2000)
        let task = Task { await store.commit(attempt, capturedSession: session) }

        await barrier.waitUntilArrived()
        authority.revoke()
        io.failRemove = true // temp cleanup will fail
        barrier.release()

        XAssertEqual(await task.value, .persistenceFailure(cleanupFailed: true))
        // Even with a cleanup double-fault, the prior accepted bytes are intact.
        XAssertEqual(await store.hydrateActive(), .inactive)
        let live = liveURL(root: root, ns)
        let onDisk = try? FarmSnapshotEnvelope.makeDecoder().decode(FarmSnapshotEnvelope.self, from: Data(contentsOf: live))
        XCTAssertEqual(onDisk, prior)
    }

    // MARK: Reverse-order commits — newest survives, exact counts

    func testConcurrentReverseOrderCommitsNewestSurvivesWithExactCounts() async throws {
        let root = newRoot()
        let ns = FarmSnapshotFixtures.namespace()
        let io = ControlledFarmSnapshotFileIO()
        let (store, authority) = makeStore(root: root, io: io)
        let session = try await activate(store, authority, ns)

        // Hold the OLDER commit at its candidate write.
        let barrier = AsyncBarrier()
        defer { barrier.close() } // I: unstrand any parked continuation on failure
        io.writeCandidateBarrier = barrier
        let older = FarmSnapshotFixtures.envelope(namespace: ns, millis: 1000)
        let olderTask = Task { await store.commit(older, capturedSession: session) }
        await barrier.waitUntilArrived()

        // The NEWER commit completes first and promotes 2000.
        let newer = FarmSnapshotFixtures.envelope(namespace: ns, millis: 2000)
        XAssertEqual(await store.commit(newer, capturedSession: session), .committed)

        // Release the older commit — it completes last and must lose.
        barrier.release()
        XAssertEqual(await olderTask.value, .notNewer(cleanupFailed: false))

        // Exact counts (asserted before any hydrate read): 2 async reads, 2 candidate
        // writes, 2 durable re-reads, 1 promote.
        XCTAssertEqual(io.readCount, 2)
        XCTAssertEqual(io.writeCount, 2)
        XCTAssertEqual(io.readSyncCount, 2)
        XCTAssertEqual(io.promoteCount, 1)

        // The newest record survives on disk.
        let onDisk = try? FarmSnapshotEnvelope.makeDecoder().decode(FarmSnapshotEnvelope.self, from: Data(contentsOf: liveURL(root: root, ns)))
        XCTAssertEqual(onDisk, newer)
    }

    // MARK: Hydrate / quarantine suspended, revoked

    func testHydrateRevokedDuringReadReturnsInactive() async throws {
        let root = newRoot()
        let ns = FarmSnapshotFixtures.namespace()
        let io = ControlledFarmSnapshotFileIO()
        let (store, authority) = makeStore(root: root, io: io)
        let session = try await activate(store, authority, ns)
        XAssertEqual(await store.commit(FarmSnapshotFixtures.envelope(namespace: ns, millis: 1000), capturedSession: session), .committed)

        let barrier = AsyncBarrier()
        defer { barrier.close() } // I: unstrand any parked continuation on failure
        io.readDataBarrier = barrier
        let task = Task { await store.hydrateActive() }
        await barrier.waitUntilArrived()
        authority.revoke()
        barrier.release()

        XAssertEqual(await task.value, .inactive)
    }

    func testQuarantineSuspendedThenRevokeReturnsInactiveAndKeepsCorruptFile() async throws {
        let root = newRoot()
        let ns = FarmSnapshotFixtures.namespace()
        let io = ControlledFarmSnapshotFileIO()
        let (store, authority) = makeStore(root: root, io: io)
        let session = try await activate(store, authority, ns)

        // Seed corrupt live bytes so hydrate enters recovery.
        let live = liveURL(root: root, ns)
        try? FileManager.default.createDirectory(at: live.deletingLastPathComponent(), withIntermediateDirectories: true)
        let corrupt = Data("{ broken".utf8)
        try? corrupt.write(to: live)

        let barrier = AsyncBarrier()
        defer { barrier.close() } // I: unstrand any parked continuation on failure
        io.createDirectoryBarrier = barrier // fires inside recover(), before the move
        let task = Task { await store.hydrateActive() }
        await barrier.waitUntilArrived()
        authority.revoke() // authority lost before the destructive move
        barrier.release()

        XAssertEqual(await task.value, .inactive)
        // The corrupt file was NOT moved because authority was revoked pre-move.
        XCTAssertEqual(try? Data(contentsOf: live), corrupt)
        _ = session
    }
}
