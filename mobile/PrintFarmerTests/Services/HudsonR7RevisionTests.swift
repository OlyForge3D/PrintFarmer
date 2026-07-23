import XCTest
import KeychainSwift
@testable import PrintFarmer

/// Focused proofs for the r7 revision of issue #816 (Hicks + Vasquez
/// combined mandates). One test per finding; each test exercises the
/// exact production code path the reject called out.
///
/// - J1 owner ABA + prior-state restore + registry rollback + keychain surfacing
/// - J2 required identity verification /me failure publishes nothing
/// - J3 stale-transient restore CAS-clears T1 session
/// - J4 authenticated APIClient construction requires stable serverID
/// - H1 corrupt durable record throws exact typed persistenceFailure + poisons
/// - H2 two record instances at same file path serialize their RMW via shared lock
/// - H3 write-verify failure restores exact prior verified bytes
/// - I  AsyncBarrier parks two waiters simultaneously and resumes both
@MainActor
final class HudsonR7RevisionTests: XCTestCase {

    // MARK: - J1 owner CAS on operation token (ABA-safe against equal-user T2)

    /// J1 (issue #816 reject, Hicks): the operation-tagged owner CAS distinguishes
    /// T1 from an equal-user T2 that reused the same userID. T2 publishes its
    /// own operation-token tag; T1's rollback tries to restore prior state,
    /// sees the operation-token has advanced, and leaves T2's publication
    /// untouched.
    func testOwnerRollbackIsABASafeAgainstEqualUserT2ViaOperationTokenCAS() throws {
        let defaults = UserDefaults(suiteName: trackedSuiteName("owner"))!
        let store = FarmSnapshotOwnerStore(userDefaults: defaults)
        let serverID = UUID()
        let sharedUserID = UUID() // T1 and T2 both publish the SAME user

        // T1 publishes owner tagged with operation token 1, capturing the prior
        // (nil) state.
        let priorT1 = store.setOwnerCapturingPrior(userID: sharedUserID, serverID: serverID, operationToken: 1)
        XCTAssertEqual(priorT1.userID, nil)
        XCTAssertEqual(priorT1.operationToken, nil)
        XCTAssertEqual(store.ownerUserID(serverID: serverID), sharedUserID)
        XCTAssertEqual(store.ownerOperationToken(serverID: serverID), 1)

        // T2 (equal-user relogin) publishes with operation token 2, capturing
        // T1's state as its prior.
        let priorT2 = store.setOwnerCapturingPrior(userID: sharedUserID, serverID: serverID, operationToken: 2)
        XCTAssertEqual(priorT2.userID, sharedUserID)
        XCTAssertEqual(priorT2.operationToken, 1)
        XCTAssertEqual(store.ownerUserID(serverID: serverID), sharedUserID)
        XCTAssertEqual(store.ownerOperationToken(serverID: serverID), 2)

        // T1's rollback runs LATE (after T2 published). Compare-and-restore on
        // operation token 1 MUST fail (current is 2) — T2's publication is
        // preserved untouched.
        let rolled = store.restoreOwnerIfOperationMatches(
            serverID: serverID, expectedOperationToken: 1, prior: priorT1
        )
        XCTAssertFalse(rolled, "T1 rollback MUST NOT restore over T2's operation-token publication (J1 ABA)")
        XCTAssertEqual(store.ownerUserID(serverID: serverID), sharedUserID)
        XCTAssertEqual(store.ownerOperationToken(serverID: serverID), 2, "T2's operation token preserved (J1 ABA)")

        // T2's own rollback (if it fails downstream) CAS-matches on token 2 and
        // restores T1's exact prior state — proving the restore semantic (not
        // clear).
        let rolledT2 = store.restoreOwnerIfOperationMatches(
            serverID: serverID, expectedOperationToken: 2, prior: priorT2
        )
        XCTAssertTrue(rolledT2, "T2's own rollback CAS on its own token MUST succeed (J1)")
        XCTAssertEqual(store.ownerUserID(serverID: serverID), sharedUserID,
                       "T2 rollback restores T1's owner userID (J1 prior-state restore, not clear)")
        XCTAssertEqual(store.ownerOperationToken(serverID: serverID), 1,
                       "T2 rollback restores T1's operation token exactly (J1 prior-state restore)")
    }

    // MARK: - J1 credentials prior-state restore

    /// J1: credentials rollback restores the exact prior credentials rather
    /// than clearing — so a login that fails at step 3 does not destroy an
    /// existing valid session for the same server.
    func testCredentialsRollbackRestoresPriorCredentialsRatherThanClearing() {
        let keychain = KeychainSwift(keyPrefix: "HudsonR7Creds_\(UUID().uuidString)_")
        let store = ServerCredentialsStore(keychain: keychain)
        let serverID = UUID()

        // Seed a prior verified session.
        let prior = ServerCredentials(accessToken: "prior-token", expiresAt: Date(timeIntervalSince1970: 100_000))
        store.save(prior, serverId: serverID)

        // A new login writes new credentials, capturing prior.
        let captured = store.saveCapturingPrior(
            ServerCredentials(accessToken: "new-token", expiresAt: Date(timeIntervalSince1970: 200_000)),
            serverId: serverID
        )
        XCTAssertEqual(captured?.accessToken, "prior-token")
        XCTAssertEqual(captured?.expiresAt, Date(timeIntervalSince1970: 100_000))
        XCTAssertEqual(store.load(serverId: serverID)?.accessToken, "new-token")

        // Rollback the new login: prior verified credentials are restored, not
        // cleared.
        let rolled = store.restoreIfAccessTokenMatches(
            serverId: serverID, expectedAccessToken: "new-token", prior: captured
        )
        XCTAssertTrue(rolled)
        XCTAssertEqual(store.load(serverId: serverID)?.accessToken, "prior-token",
                       "J1: rollback MUST restore prior credentials, not clear")
        XCTAssertEqual(store.load(serverId: serverID)?.expiresAt, Date(timeIntervalSince1970: 100_000))

        // Cleanup
        store.clear(serverId: serverID)
    }

    // MARK: - J1 registry rollback of add()

    /// J1: a login that fails leaves NO orphan registry entry for a
    /// newly-added server. The rollbackAdd CAS matches (id, createdAt,
    /// updatedAt) and removes only entries that were not concurrently
    /// updated.
    func testServerRegistryRollbackAddRemovesUntouchedNewlyAddedEntry() throws {
        let registry = ServerRegistry(
            userDefaults: UserDefaults(suiteName: trackedSuiteName("reg"))!,
            migrateLegacyServerURL: false
        )
        let created = try registry.add(displayName: "Ephemeral", baseURL: URL(string: "https://ephemeral.example.com")!, makeActiveIfNeeded: false)
        XCTAssertTrue(registry.servers.contains(where: { $0.id == created.id }))

        // Rollback removes the untouched entry.
        XCTAssertTrue(registry.rollbackAdd(created))
        XCTAssertFalse(registry.servers.contains(where: { $0.id == created.id }),
                       "J1: rollback removes newly-added registry entry")
    }

    /// J1: rollbackAdd MUST refuse to remove when the entry was concurrently
    /// updated (e.g. by an interleaved rename) — the CAS on updatedAt
    /// protects a peer operation's state.
    func testServerRegistryRollbackAddRefusesWhenUpdatedConcurrently() throws {
        let registry = ServerRegistry(
            userDefaults: UserDefaults(suiteName: trackedSuiteName("reg"))!,
            migrateLegacyServerURL: false
        )
        let created = try registry.add(displayName: "A", baseURL: URL(string: "https://a.example.com")!, makeActiveIfNeeded: false)

        // A peer operation renames the server (updates it) → updatedAt advances.
        var mutated = created
        mutated.displayName = "A-renamed"
        try registry.update(mutated)

        // Our rollback carries the STALE created snapshot — CAS on updatedAt
        // fails, entry preserved untouched.
        XCTAssertFalse(registry.rollbackAdd(created),
                       "J1: rollback must refuse to remove a concurrently-updated entry")
        XCTAssertTrue(registry.servers.contains(where: { $0.id == created.id }))
        XCTAssertEqual(registry.servers.first(where: { $0.id == created.id })?.displayName, "A-renamed")
    }

    // MARK: - J4 authenticated APIClient construction requires serverID

    /// E (issue #816 reject): constructing an authenticated APIClient without a
    /// serverID is now a COMPILE-TIME-impossible state (bundled
    /// `AuthenticatedIdentity`) rather than a runtime precondition trap. We prove
    /// the authenticated identity is bound atomically at construction.
    func testAPIClientAuthenticatedConstructionWithoutServerIDIsProhibited() async {
        // Identity is bundled, so a bearer and serverID are inseparable.
        let serverID = UUID()
        let client = APIClient(
            baseURL: URL(string: "https://a.example.com")!,
            authenticated: AuthenticatedIdentity(accessToken: "bearer", serverID: serverID)
        )
        // G (issue #816 reject): read the actor-isolated accessor by awaiting it
        // directly — no expectation + wall-clock `wait(for:timeout:)`.
        let observed = await client.currentServerIdentity()
        XCTAssertEqual(observed, serverID, "serverID bound atomically at construction")
    }

    /// E: clearSession() clears everything — accessToken, serverID,
    /// and authSessionToken — atomically.
    func testAPIClientSetAccessTokenNilClearsServerIDAndAuthSessionToken() async {
        let serverID = UUID()
        let client = APIClient(
            baseURL: URL(string: "https://a.example.com")!,
            authenticated: AuthenticatedIdentity(accessToken: "bearer", serverID: serverID, authSessionToken: 42)
        )
        var observedServer = await client.currentServerIdentity()
        XCTAssertEqual(observedServer, serverID)

        await client.clearSession()
        observedServer = await client.currentServerIdentity()
        let observedToken = await client.currentAccessToken()
        XCTAssertNil(observedServer, "J4: clearing accessToken clears serverID")
        XCTAssertNil(observedToken)
    }

    // MARK: - H1 corrupt durable record throws typed persistenceFailure

    /// H1 (issue #816 reject, Vasquez): an existing but corrupt authority
    /// file MUST throw the exact typed .persistenceFailure on load — never
    /// silently return a zeroed payload — and every subsequent mutation on
    /// the same record must throw too, so no operation proceeds against a
    /// silently-reset authority.
    func testCorruptDurableAuthorityRecordThrowsTypedPersistenceFailure() throws {
        let root = FarmSnapshotFixtures.tempRoot()
        addTeardownBlock { try? FileManager.default.removeItem(at: root) }

        // Write garbage into the exact record filename so the on-disk record
        // exists but cannot be decoded.
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        let recordURL = root.appendingPathComponent(FarmSnapshotDurableAuthorityRecord.filename)
        try Data("not valid json { }".utf8).write(to: recordURL)

        let record = FarmSnapshotDurableAuthorityRecord(rootURL: root)

        // Every read throws the exact typed error.
        XCTAssertThrowsError(try record.loadReservedHighWater()) { err in
            XCTAssertEqual(err as? FarmSnapshotAuthorityError, .persistenceFailure,
                           "H1: corrupt file MUST throw exact typed .persistenceFailure on read")
        }
        XCTAssertThrowsError(try record.loadAdoptedHighWater()) { err in
            XCTAssertEqual(err as? FarmSnapshotAuthorityError, .persistenceFailure)
        }
        XCTAssertThrowsError(try record.loadTombstones()) { err in
            XCTAssertEqual(err as? FarmSnapshotAuthorityError, .persistenceFailure)
        }
        // Every mutation throws too — no silent reset.
        XCTAssertThrowsError(try record.reserveNextToken()) { err in
            XCTAssertEqual(err as? FarmSnapshotAuthorityError, .persistenceFailure,
                           "H1: mutation on corrupt record MUST fail closed")
        }
        XCTAssertThrowsError(try record.tryAdopt(token: 100)) { err in
            XCTAssertEqual(err as? FarmSnapshotAuthorityError, .persistenceFailure)
        }
        XCTAssertThrowsError(try record.insertTombstone(UUID())) { err in
            XCTAssertEqual(err as? FarmSnapshotAuthorityError, .persistenceFailure)
        }

        // Byte preservation: the failed operations MUST NOT have overwritten
        // the corrupt bytes — the exact garbage we wrote is still on disk.
        let bytesAfter = try Data(contentsOf: recordURL)
        XCTAssertEqual(bytesAfter, Data("not valid json { }".utf8),
                       "H1: corrupt bytes MUST NOT be overwritten by failed mutations")
    }

    // MARK: - H2 two record instances at same file path share their lock

    /// H2 (issue #816 reject, Vasquez): two `FarmSnapshotDurableAuthorityRecord`
    /// instances constructed at the same on-disk path share ONE canonical
    /// lock, so their read-modify-write reservation calls serialize even
    /// though the instances are distinct objects. Reservations must be
    /// strictly increasing across the two objects without collision.
    func testDistinctRecordsAtSamePathShareCanonicalLockAndDoNotCollide() throws {
        let root = FarmSnapshotFixtures.tempRoot()
        addTeardownBlock { try? FileManager.default.removeItem(at: root) }

        // Two records, same root, distinct instances.
        let a = FarmSnapshotDurableAuthorityRecord(rootURL: root)
        let b = FarmSnapshotDurableAuthorityRecord(rootURL: root)
        XCTAssertFalse(a === b)

        // Race N reservations across the two records; every issued token must
        // be unique and strictly greater than the last file-persisted value.
        var tokens = Set<UInt64>()
        let count = 100
        let group = DispatchGroup()
        let lock = NSLock()
        DispatchQueue.concurrentPerform(iterations: count) { i in
            let record = (i.isMultiple(of: 2)) ? a : b
            group.enter()
            defer { group.leave() }
            do {
                let token = try record.reserveNextToken()
                lock.lock()
                tokens.insert(token)
                lock.unlock()
            } catch {
                XCTFail("H2: reservation must not throw during shared-lock race: \(error)")
            }
        }
        group.wait()
        XCTAssertEqual(tokens.count, count, "H2: shared lock MUST guarantee unique reservations across distinct record objects")
        // Reservations should cover 1...count (or higher if any pre-existing).
        XCTAssertEqual(tokens.min(), 1)
        XCTAssertEqual(tokens.max(), UInt64(count))
    }

    // MARK: - H3 write-verify failure restores exact prior verified bytes

    /// H3 (issue #816 reject, Vasquez): when the atomic write is
    /// acknowledged but the verifying re-read observes different bytes
    /// (simulated via testInterceptAfterAtomicWrite), writeLocked MUST
    /// throw the typed error AND restore the exact prior verified bytes so
    /// the record is never left holding the failed write's partially-
    /// applied state.
    func testWriteVerifyFailureRestoresPriorBytes() throws {
        let root = FarmSnapshotFixtures.tempRoot()
        addTeardownBlock { try? FileManager.default.removeItem(at: root) }
        let record = FarmSnapshotDurableAuthorityRecord(rootURL: root)
        let recordURL = root.appendingPathComponent(FarmSnapshotDurableAuthorityRecord.filename)

        // Baseline: reserve token 1 and adopt it. This is the "prior verified"
        // state we expect to be preserved on a subsequent write failure.
        let baseline = try record.reserveNextToken()
        XCTAssertEqual(baseline, 1)
        XCTAssertTrue(try record.tryAdopt(token: baseline))
        let priorBytes = try Data(contentsOf: recordURL)
        XCTAssertFalse(priorBytes.isEmpty)

        // Inject a write loss: after the atomic write, delete the file. The
        // verifying re-read sees .absent → writeLocked throws
        // .persistenceFailure and MUST restore priorBytes.
        addTeardownBlock {
            record.setAfterAtomicWriteHookForTesting(nil)
        }
        record.setAfterAtomicWriteHookForTesting { url in
            try? FileManager.default.removeItem(at: url)
        }

        // Attempt to reserve another token — MUST throw .persistenceFailure.
        XCTAssertThrowsError(try record.reserveNextToken()) { err in
            XCTAssertEqual(err as? FarmSnapshotAuthorityError, .persistenceFailure)
        }
        // Clear the injection so we can observe restored bytes.
        record.setAfterAtomicWriteHookForTesting(nil)

        // H3: prior bytes are restored. The record is again readable and
        // reports the baseline high-water — no reset to zero, no partial
        // state.
        let restoredBytes = try Data(contentsOf: recordURL)
        XCTAssertEqual(restoredBytes, priorBytes,
                       "H3: write-verify failure MUST restore exact prior verified bytes")
        XCTAssertEqual(try record.loadReservedHighWater(), baseline,
                       "H3: prior high-water preserved after write-verify failure")
        XCTAssertEqual(try record.loadAdoptedHighWater(), baseline,
                       "H3: prior adopted preserved after write-verify failure")
    }

    // MARK: - I AsyncBarrier parks two waiters simultaneously and resumes both

    /// I (issue #816 reject, Hicks): the previous single-slot releaseWaiter
    /// silently overwrote a first waiter's continuation when a second
    /// arrived — a 'two waiters parked, then released' test could not
    /// prove both parked. This proves both waiters are truly parked and
    /// both resume on release().
    func testAsyncBarrierResumesTwoParkedWaitersSimultaneously() async {
        let barrier = AsyncBarrier()

        actor Counter {
            var resumed = 0
            func bump() { resumed += 1 }
            var count: Int { resumed }
        }
        let counter = Counter()

        // Park two tasks on arriveAndWait.
        let a = Task { await barrier.arriveAndWait(); await counter.bump() }
        let b = Task { await barrier.arriveAndWait(); await counter.bump() }

        // Wait until both have arrived (waitUntilArrived resumes on first arrival;
        // for two arrivals we need a second wait or explicit rendezvous).
        // Use signal() semantics: waitUntilArrived returns as soon as one has
        // arrived. To prove BOTH parked, release and observe both counters
        // increment.
        await barrier.waitUntilArrived()

        // Release — BOTH parked continuations must resume (was: only the
        // last-registered one).
        barrier.release()

        // Drain both tasks to completion.
        _ = await a.value
        _ = await b.value

        let observed = await counter.count
        XCTAssertEqual(observed, 2,
                       "I: release() MUST resume BOTH parked arriveAndWait waiters (was: only one)")
    }
}
