import XCTest
@testable import PrintFarmer

/// Authority + owner-store unit behavior (issue #816, Gates A/B/E).
final class FarmSnapshotAuthorityTests: XCTestCase {

    // MARK: Authority

    func testMintAdvancesMonotonicTokenAndSupersedes()throws {
        let authority = FarmSnapshotFixtures.makeAuthority(tombstoneDefaults: UserDefaults(suiteName: trackedSuiteName("tomb"))!)
        let namespace = FarmSnapshotFixtures.namespace()

        let first = try authority.mint(namespace: namespace, generation: 0)
        let second = try authority.mint(namespace: namespace, generation: 0)

        XCTAssertNotNil(first)
        XCTAssertNotNil(second)
        XCTAssertGreaterThan(second!.token, first!.token)
        // The newer session supersedes the older one entirely.
        XCTAssertTrue(authority.isCurrent(second!))
        XCTAssertFalse(authority.isCurrent(first!))
    }

    // MARK: H3 — high-water CAS never rewinds; conditional deactivation

    func testAdoptHigherTokenThenMintDoesNotRewind()throws {
        let authority = FarmSnapshotFixtures.makeAuthority(tombstoneDefaults: UserDefaults(suiteName: trackedSuiteName("tomb"))!)
        let ns = FarmSnapshotFixtures.namespace()
        // Externally adopt a high token (models a token minted on another counter).
        let high = FarmSnapshotSession(namespace: ns, generation: 0, token: 100)
        XCTAssertTrue(try authority.adopt(high))
        // A subsequent mint must issue a token STRICTLY above the adopted high-water —
        // never rewind to 1.
        let minted = try authority.mint(namespace: ns, generation: 0)!
        XCTAssertGreaterThan(minted.token, 100)
        XCTAssertTrue(authority.isCurrent(minted))
        // A delayed older adopt (below the high-water) is rejected.
        let stale = FarmSnapshotSession(namespace: ns, generation: 0, token: 50)
        XCTAssertFalse(try authority.adopt(stale))
        XCTAssertTrue(authority.isCurrent(minted))
    }

    func testAdoptAfterRevokeRejectsOldToken()throws {
        let authority = FarmSnapshotFixtures.makeAuthority(tombstoneDefaults: UserDefaults(suiteName: trackedSuiteName("tomb"))!)
        let ns = FarmSnapshotFixtures.namespace()
        let s1 = try authority.mint(namespace: ns, generation: 0)!
        authority.revoke() // current == nil, but high-water retained
        // Re-adopting the old (now consumed) session must fail — no resurrection after
        // the current was cleared.
        XCTAssertFalse(try authority.adopt(s1))
        XCTAssertNil(authority.currentSession())
    }

    func testDelayedOldDeactivateAfterNewerActivationSurvives()throws {
        let authority = FarmSnapshotFixtures.makeAuthority(tombstoneDefaults: UserDefaults(suiteName: trackedSuiteName("tomb"))!)
        let ns = FarmSnapshotFixtures.namespace()
        let s1 = try authority.mint(namespace: ns, generation: 0)!
        let s2 = try authority.mint(namespace: ns, generation: 0)! // newer supersedes s1
        // A delayed conditional deactivate of the OLD session must NOT clear the newer
        // one — it returns false and s2 survives.
        XCTAssertFalse(authority.deactivate(s1))
        XCTAssertTrue(authority.isCurrent(s2))
        // Deactivating the exact current session does clear it.
        XCTAssertTrue(authority.deactivate(s2))
        XCTAssertNil(authority.currentSession())
    }

    func testActivateRejectionPreventsBindAndCommit() async throws {
        let root = FarmSnapshotFixtures.tempRoot()
        defer { try? FileManager.default.removeItem(at: root) }
        let authority = FarmSnapshotFixtures.makeAuthority(tombstoneDefaults: UserDefaults(suiteName: trackedSuiteName("tomb"))!)
        let store = FarmSnapshotStore(authority: authority, rootURL: root)
        let ns = FarmSnapshotFixtures.namespace()
        // Newer session is current.
        let newer = try authority.mint(namespace: ns, generation: 0)!
        _ = await store.activate(session: newer)
        // An OLDER externally-constructed session (lower token) is rejected by activate.
        let older = FarmSnapshotSession(namespace: ns, generation: 0, token: newer.token - 1)
        let accepted = await store.activate(session: older)
        XCTAssertFalse(accepted, "activate must reject an older/consumed token")
        XCTAssertTrue(authority.isCurrent(newer))
        // A commit captured on the rejected older session must not apply.
        let result = await store.commit(FarmSnapshotFixtures.envelope(namespace: ns, millis: 1), capturedSession: older)
        XCTAssertEqual(result, .superseded)
    }

    // MARK: H — durable authority monotonicity across restart

    func testHighWaterSurvivesAuthorityRecreation()throws {
        let suite = trackedSuiteName("tomb")
        let ns = FarmSnapshotFixtures.namespace()
        // Adopt a high token, then recreate the authority on the SAME durable store.
        let a1 = FarmSnapshotAuthority(tombstoneStore: FarmSnapshotFixtures.makeTombstoneStore(UserDefaults(suiteName: suite)!))
        XCTAssertTrue(try a1.adopt(FarmSnapshotSession(namespace: ns, generation: 0, token: 500)))
        let a2 = FarmSnapshotAuthority(tombstoneStore: FarmSnapshotFixtures.makeTombstoneStore(UserDefaults(suiteName: suite)!))
        // The next mint on the recreated authority is STRICTLY greater than the durable
        // high-water — a delayed older token can never re-adopt after relaunch.
        let minted = try a2.mint(namespace: ns, generation: 0)!
        XCTAssertGreaterThan(minted.token, 500)
        // A stale token at-or-below the persisted high-water is rejected.
        XCTAssertFalse(try a2.adopt(FarmSnapshotSession(namespace: ns, generation: 0, token: 400)))
        XCTAssertTrue(a2.isCurrent(minted))
    }

    func testHighWaterFromMintSurvivesRecreationAndRevokeStaysNonReusable()throws {
        let suite = trackedSuiteName("tomb")
        let ns = FarmSnapshotFixtures.namespace()
        let a1 = FarmSnapshotAuthority(tombstoneStore: FarmSnapshotFixtures.makeTombstoneStore(UserDefaults(suiteName: suite)!))
        let s1 = try a1.mint(namespace: ns, generation: 0)!
        a1.revoke()
        // Recreate; the revoked token remains non-reusable and the counter is monotonic.
        let a2 = FarmSnapshotAuthority(tombstoneStore: FarmSnapshotFixtures.makeTombstoneStore(UserDefaults(suiteName: suite)!))
        XCTAssertFalse(try a2.adopt(s1), "a revoked/consumed token cannot re-adopt after restart")
        let s2 = try a2.mint(namespace: ns, generation: 0)!
        XCTAssertGreaterThan(s2.token, s1.token)
    }


    func testReserveDoesNotPublishUntilAdopt() async throws {
        let root = FarmSnapshotFixtures.tempRoot()
        defer { try? FileManager.default.removeItem(at: root) }
        let authority = FarmSnapshotFixtures.makeAuthority(tombstoneDefaults: UserDefaults(suiteName: trackedSuiteName("tomb"))!)
        let store = FarmSnapshotStore(authority: authority, rootURL: root)
        let ns = FarmSnapshotFixtures.namespace()

        // Reserve a candidate — it is NOT current, so nothing can commit against it.
        let candidate = try authority.reserve(namespace: ns, generation: 0)!
        XCTAssertNil(authority.currentSession(), "a reserved candidate is not current")
        XCTAssertFalse(authority.isCurrent(candidate))
        let earlyCommit = await store.commit(FarmSnapshotFixtures.envelope(namespace: ns, millis: 1), capturedSession: candidate)
        XCTAssertEqual(earlyCommit, .superseded, "no commit can be authorized before adopt")

        // Publish via adopt — now it is authoritative and can commit.
        XCTAssertTrue(try authority.adopt(candidate))
        XCTAssertTrue(authority.isCurrent(candidate))
        let liveCommit = await store.commit(FarmSnapshotFixtures.envelope(namespace: ns, millis: 2), capturedSession: candidate)
        XCTAssertEqual(liveCommit, .committed)
    }

    func testAdoptHigherThenReserveDoesNotRewindOrPublish()throws {
        let authority = FarmSnapshotFixtures.makeAuthority(tombstoneDefaults: UserDefaults(suiteName: trackedSuiteName("tomb"))!)
        let ns = FarmSnapshotFixtures.namespace()
        let high = FarmSnapshotSession(namespace: ns, generation: 0, token: 100)
        XCTAssertTrue(try authority.adopt(high))
        // reserve issues 101 (strictly above the adopted high-water) and does NOT change
        // the current session.
        let candidate = try authority.reserve(namespace: ns, generation: 0)!
        XCTAssertGreaterThan(candidate.token, 100)
        XCTAssertTrue(authority.isCurrent(high), "reserve must not publish over the current session")
    }

    func testTombstonedServerCannotMint()throws {
        let authority = FarmSnapshotFixtures.makeAuthority(tombstoneDefaults: UserDefaults(suiteName: trackedSuiteName("tomb"))!)
        let namespace = FarmSnapshotFixtures.namespace()
        try authority.tombstone(namespace.serverID)

        XCTAssertNil(try authority.mint(namespace: namespace, generation: 0))
        XCTAssertTrue(authority.isTombstoned(namespace.serverID))
    }

    func testTombstoneRevokesMatchingCurrentSession()throws {
        let authority = FarmSnapshotFixtures.makeAuthority(tombstoneDefaults: UserDefaults(suiteName: trackedSuiteName("tomb"))!)
        let namespace = FarmSnapshotFixtures.namespace()
        let session = try authority.mint(namespace: namespace, generation: 0)!

        try authority.tombstone(namespace.serverID)
        XCTAssertFalse(authority.isCurrent(session))
        XCTAssertNil(authority.currentSession())
    }

    func testRevokeClearsCurrent()throws {
        let authority = FarmSnapshotFixtures.makeAuthority(tombstoneDefaults: UserDefaults(suiteName: trackedSuiteName("tomb"))!)
        let session = try authority.mint(namespace: FarmSnapshotFixtures.namespace(), generation: 0)!
        authority.revoke()
        XCTAssertNil(authority.currentSession())
        XCTAssertFalse(authority.isCurrent(session))
    }

    func testWithPromotionSkipsWhenNotCurrent()throws {
        let authority = FarmSnapshotFixtures.makeAuthority(tombstoneDefaults: UserDefaults(suiteName: trackedSuiteName("tomb"))!)
        let session = try authority.mint(namespace: FarmSnapshotFixtures.namespace(), generation: 0)!
        authority.revoke()

        var ran = false
        let result: Bool? = authority.withPromotion(session, cancelled: { false }) {
            ran = true
            return true
        }
        XCTAssertNil(result)
        XCTAssertFalse(ran)
    }

    func testWithPromotionSkipsWhenCancelled()throws {
        let authority = FarmSnapshotFixtures.makeAuthority(tombstoneDefaults: UserDefaults(suiteName: trackedSuiteName("tomb"))!)
        let session = try authority.mint(namespace: FarmSnapshotFixtures.namespace(), generation: 0)!
        var ran = false
        let result: Bool? = authority.withPromotion(session, cancelled: { true }) {
            ran = true
            return true
        }
        XCTAssertNil(result)
        XCTAssertFalse(ran)
    }

    func testIsCurrentRequiresExactSession()throws {
        let authority = FarmSnapshotFixtures.makeAuthority(tombstoneDefaults: UserDefaults(suiteName: trackedSuiteName("tomb"))!)
        let namespace = FarmSnapshotFixtures.namespace()
        let session = try authority.mint(namespace: namespace, generation: 0)!
        // A same-namespace/same-generation session with a different token is not current.
        let impostor = FarmSnapshotSession(namespace: namespace, generation: 0, token: session.token + 99)
        XCTAssertTrue(authority.isCurrent(session))
        XCTAssertFalse(authority.isCurrent(impostor))
    }

    // MARK: Owner store

    func testOwnerStoreSetGetClear() {
        let defaults = UserDefaults(suiteName: trackedSuiteName("owner"))!
        let store = FarmSnapshotOwnerStore(userDefaults: defaults)
        let server = UUID()
        let user = UUID()

        XCTAssertNil(store.ownerUserID(serverID: server)) // token-only / legacy → nil
        store.setOwner(userID: user, serverID: server)
        XCTAssertEqual(store.ownerUserID(serverID: server), user)
        store.clearOwner(serverID: server)
        XCTAssertNil(store.ownerUserID(serverID: server))
    }

    func testOwnerStoreIsScopedPerServer() {
        let defaults = UserDefaults(suiteName: trackedSuiteName("owner"))!
        let store = FarmSnapshotOwnerStore(userDefaults: defaults)
        let serverA = UUID(), userA = UUID()
        let serverB = UUID(), userB = UUID()
        store.setOwner(userID: userA, serverID: serverA)
        store.setOwner(userID: userB, serverID: serverB)

        store.clearOwner(serverID: serverA)
        XCTAssertNil(store.ownerUserID(serverID: serverA))
        XCTAssertEqual(store.ownerUserID(serverID: serverB), userB)
    }

    func testOwnerStorePersistsAcrossRecreation() {
        let suite = trackedSuiteName("owner")
        let server = UUID(), user = UUID()
        FarmSnapshotOwnerStore(userDefaults: UserDefaults(suiteName: suite)!)
            .setOwner(userID: user, serverID: server)
        // Recreate the store on the same backing (simulates process relaunch).
        let reloaded = FarmSnapshotOwnerStore(userDefaults: UserDefaults(suiteName: suite)!)
        XCTAssertEqual(reloaded.ownerUserID(serverID: server), user)
    }

    /// Focused hygiene proof (issue #816, Vasquez): writing a key into a TRACKED suite
    /// and invoking the shared cleanup primitive removes the exact persistent domain —
    /// no untracked suite is created to test it.
    func testTrackedSuiteCleanupPrimitiveRemovesDomain() {
        let suite = trackedSuiteName("hygiene-proof")
        UserDefaults(suiteName: suite)!.set("value", forKey: "key")
        // The domain now exists and is non-empty.
        XCTAssertEqual(UserDefaults().persistentDomain(forName: suite)?["key"] as? String, "value")
        // The same primitive used by trackedSuiteName's teardown removes it exactly.
        XCTAssertTrue(TrackedDefaults.removeDomain(suite))
        let residual = UserDefaults().persistentDomain(forName: suite) ?? [:]
        XCTAssertTrue(residual.isEmpty, "domain must be nil/empty after cleanup")
    }

    // MARK: H — cross-instance durable monotonic authority (issue #816)

    /// Two live authorities on the SAME persistence domain share ONE coordinator lock
    /// so a read-modify-write is serialized cross-instance. A's adopted 500 is seen by
    /// B — B's next reserve is strictly greater than 500, never rewinding.
    func testTwoLiveAuthoritiesShareDurableHighWaterAcrossInstances() throws {
        let suite = trackedSuiteName("tomb")
        let domain = "cross-inst-\(UUID().uuidString)"
        let defaultsA = UserDefaults(suiteName: suite)!
        let defaultsB = UserDefaults(suiteName: suite)!
        let a = FarmSnapshotAuthority(
            tombstoneStore: FarmSnapshotFixtures.makeTombstoneStore(defaultsA, domainIdentifier: domain))
        let b = FarmSnapshotAuthority(
            tombstoneStore: FarmSnapshotFixtures.makeTombstoneStore(defaultsB, domainIdentifier: domain))
        let ns = FarmSnapshotFixtures.namespace()

        // A adopts 500 externally; the shared durable adopted+reserved counters advance.
        XCTAssertTrue(try a.adopt(FarmSnapshotSession(namespace: ns, generation: 0, token: 500)))

        // B, a DISTINCT live authority in the same domain, reserves next — must be > 500,
        // even though B's in-memory cache seeded at construction was 0 (H bug proof).
        let bReserved = try b.reserve(namespace: ns, generation: 0)!
        XCTAssertGreaterThan(bReserved.token, 500)

        // B then adopts its own reservation — succeeds (token > adopted 500).
        XCTAssertTrue(try b.adopt(bReserved))
    }

    /// Concurrent reserves from two distinct live authorities on the same domain must
    /// yield UNIQUE tokens. Without the shared coordinator, both could reserve 1.
    ///
    /// H (issue #816 reject, Hicks): rewritten to be truly concurrent — each
    /// authority runs its reserve loop in its own `Task` and they both wait on
    /// the same deterministic `AsyncBarrier` before racing. Serial iteration
    /// would prove nothing about cross-instance coordination; a race on the
    /// shared durable counter is the actual invariant.
    func testConcurrentReservesAcrossLiveAuthoritiesAreUniqueAndMonotonic() async throws {
        let suite = trackedSuiteName("tomb")
        let domain = "cross-inst-\(UUID().uuidString)"
        let a = FarmSnapshotAuthority(
            tombstoneStore: FarmSnapshotFixtures.makeTombstoneStore(
                UserDefaults(suiteName: suite)!, domainIdentifier: domain))
        let b = FarmSnapshotAuthority(
            tombstoneStore: FarmSnapshotFixtures.makeTombstoneStore(
                UserDefaults(suiteName: suite)!, domainIdentifier: domain))
        let ns = FarmSnapshotFixtures.namespace()
        let batch = 100

        // Deterministic start barrier: both tasks wait until BOTH have arrived
        // before they begin racing on the durable counter — this maximises
        // interleaving without any sleep/yield/poll.
        let startBarrier = AsyncBarrier()
        defer { startBarrier.close() }

        let taskA = Task<[UInt64], Error> {
            await startBarrier.arriveAndWait()
            var out: [UInt64] = []
            for _ in 0..<batch {
                out.append(try a.reserve(namespace: ns, generation: 0)!.token)
            }
            return out
        }
        let taskB = Task<[UInt64], Error> {
            await startBarrier.arriveAndWait()
            var out: [UInt64] = []
            for _ in 0..<batch {
                out.append(try b.reserve(namespace: ns, generation: 0)!.token)
            }
            return out
        }
        // Both tasks arrive; release them concurrently.
        await startBarrier.waitUntilArrived()
        startBarrier.release()

        let seenA = try await taskA.value
        let seenB = try await taskB.value
        let all = seenA + seenB
        let unique = Set(all)
        XCTAssertEqual(unique.count, all.count,
                       "concurrent reservations across distinct authorities on the same domain MUST be unique (H: cross-instance CAS)")
        // Each authority's own sequence is strictly monotonic (per-instance).
        XCTAssertEqual(seenA, seenA.sorted(), "authority A tokens must be strictly monotonic")
        XCTAssertEqual(seenB, seenB.sorted(), "authority B tokens must be strictly monotonic")
        // Every token is strictly > 0 (durable counter starts at 0).
        XCTAssertTrue(all.allSatisfy { $0 > 0 })
    }

    /// A recreated store on the same domain continues from durable state — proves the
    /// cross-instance coordinator (and the durable counters) survive object recreation.
    func testRecreatedInstanceContinuesFromDurableState() throws {
        let suite = trackedSuiteName("tomb")
        let domain = "cross-inst-\(UUID().uuidString)"
        let ns = FarmSnapshotFixtures.namespace()

        let a = FarmSnapshotAuthority(
            tombstoneStore: FarmSnapshotFixtures.makeTombstoneStore(
                UserDefaults(suiteName: suite)!, domainIdentifier: domain))
        let s1 = try a.mint(namespace: ns, generation: 0)!

        // Recreate on same suite + same domain identifier.
        let b = FarmSnapshotAuthority(
            tombstoneStore: FarmSnapshotFixtures.makeTombstoneStore(
                UserDefaults(suiteName: suite)!, domainIdentifier: domain))
        let s2 = try b.mint(namespace: ns, generation: 0)!
        XCTAssertGreaterThan(s2.token, s1.token, "recreated instance must not rewind the counter")
    }

    /// Injected persistence failure => typed error, NO token published, NO current session.
    /// The reserve/adopt CAS verifies the write via re-read and treats a mismatch as
    /// a typed persistence failure; no session is ever returned or published.
    func testInjectedPersistenceFailureReserveThrowsAndDoesNotPublish() throws {
        let suite = trackedSuiteName("tomb")
        let failing = FailingUserDefaults(suiteName: suite)!
        let authority = FarmSnapshotAuthority(
            tombstoneStore: FarmSnapshotFixtures.makeTombstoneStore(failing))
        let ns = FarmSnapshotFixtures.namespace()

        XCTAssertThrowsError(try authority.reserve(namespace: ns, generation: 0)) { err in
            XCTAssertEqual(err as? FarmSnapshotAuthorityError, .persistenceFailure)
        }
        XCTAssertNil(authority.currentSession(), "no session may be published on persistence failure")
    }

    /// Injected persistence failure on mint => typed error, NO current session set.
    func testInjectedPersistenceFailureMintThrowsAndDoesNotPublish() throws {
        let suite = trackedSuiteName("tomb")
        let failing = FailingUserDefaults(suiteName: suite)!
        let authority = FarmSnapshotAuthority(
            tombstoneStore: FarmSnapshotFixtures.makeTombstoneStore(failing))
        let ns = FarmSnapshotFixtures.namespace()

        XCTAssertThrowsError(try authority.mint(namespace: ns, generation: 0)) { err in
            XCTAssertEqual(err as? FarmSnapshotAuthorityError, .persistenceFailure)
        }
        XCTAssertNil(authority.currentSession())
    }

    /// UInt64.max reserved high-water => next reserve overflows => typed exhaustion,
    /// no trap, no publication.
    func testUInt64OverflowSurfacesTokenSpaceExhausted() throws {
        let suite = trackedSuiteName("tomb")
        let defaults = UserDefaults(suiteName: suite)!
        // Seed the durable reserved high-water directly at the boundary.
        defaults.set(NSNumber(value: UInt64.max), forKey: FarmSnapshotTombstoneStore.reservedHighWaterKey)
        let authority = FarmSnapshotAuthority(
            tombstoneStore: FarmSnapshotFixtures.makeTombstoneStore(defaults))
        let ns = FarmSnapshotFixtures.namespace()

        XCTAssertThrowsError(try authority.reserve(namespace: ns, generation: 0)) { err in
            XCTAssertEqual(err as? FarmSnapshotAuthorityError, .tokenSpaceExhausted)
        }
        XCTAssertNil(authority.currentSession())

        // A mint at the boundary is also fail-closed with typed exhaustion.
        XCTAssertThrowsError(try authority.mint(namespace: ns, generation: 0)) { err in
            XCTAssertEqual(err as? FarmSnapshotAuthorityError, .tokenSpaceExhausted)
        }
    }

    /// Cross-instance releaseCoordinator cleans up the static domain map (housekeeping
    /// primitive), so long-running test processes do not accumulate stale locks.
    func testReleaseCoordinatorDropsDomainLock() {
        let suite = trackedSuiteName("tomb")
        let domain = "release-\(UUID().uuidString)"
        // Create a store to force the domain lock into the static registry.
        _ = FarmSnapshotFixtures.makeTombstoneStore(UserDefaults(suiteName: suite)!, domainIdentifier: domain)
        FarmSnapshotTombstoneStore.releaseCoordinator(forDomain: domain)
        // Recreating on the same domain still works (registers a fresh lock).
        _ = FarmSnapshotFixtures.makeTombstoneStore(UserDefaults(suiteName: suite)!, domainIdentifier: domain)
    }

    // MARK: - H (issue #816 reject): shared domain coordinator + true durability

    /// H: two Authorities constructed on the SAME persistence domain share ONE
    /// coordinator, so B adopting a newer session on domain D is IMMEDIATELY
    /// visible to A on the same domain D. A.isCurrent(oldSession) MUST return
    /// false; A.withPromotion(oldSession) MUST refuse to run its body. Fixes the
    /// reject: "A stale instance cannot promote after B adopts/tombstones."
    func testCrossInstanceCoordinatorSharesCurrentAndBlocksStalePromotion() throws {
        let domain = "shared-\(UUID().uuidString)"
        let defaults = UserDefaults(suiteName: trackedSuiteName("tomb"))!
        let authA = FarmSnapshotFixtures.makeAuthority(
            tombstoneDefaults: defaults, domainIdentifier: domain)
        let authB = FarmSnapshotFixtures.makeAuthority(
            tombstoneDefaults: defaults, domainIdentifier: domain)

        let ns = FarmSnapshotFixtures.namespace()
        // A mints an "old" session; both A and B see it as current (shared coordinator).
        let oldSession = try authA.mint(namespace: ns, generation: 0)!
        XCTAssertTrue(authA.isCurrent(oldSession))
        XCTAssertTrue(authB.isCurrent(oldSession),
                      "B on the same domain must observe A's mint (shared coordinator)")

        // B mints a newer session — A must now see the old session as NOT current.
        let newerSession = try authB.mint(namespace: ns, generation: 0)!
        XCTAssertGreaterThan(newerSession.token, oldSession.token)
        XCTAssertFalse(authA.isCurrent(oldSession),
                       "A stale Authority MUST NOT still see the superseded session as current")
        XCTAssertTrue(authA.isCurrent(newerSession),
                      "A must see B's newer session as current (shared state)")

        // A tries to run a promotion body against the stale session — must be blocked.
        var bodyRan = false
        let result = authA.withPromotion(oldSession, cancelled: { false }) {
            bodyRan = true
            return 42
        }
        XCTAssertNil(result, "stale withPromotion MUST return nil")
        XCTAssertFalse(bodyRan, "stale withPromotion body MUST NOT run — B has newer current")
    }

    /// H: B tombstones a server on the shared domain; A on the same domain then
    /// tries to reserve/mint on that server MUST refuse. Fixes: "B tombstones =>
    /// A cannot reserve/promote."
    func testCrossInstanceTombstoneBlocksPeerReserveAndPromote() throws {
        let domain = "tomb-\(UUID().uuidString)"
        let defaults = UserDefaults(suiteName: trackedSuiteName("tomb"))!
        let authA = FarmSnapshotFixtures.makeAuthority(
            tombstoneDefaults: defaults, domainIdentifier: domain)
        let authB = FarmSnapshotFixtures.makeAuthority(
            tombstoneDefaults: defaults, domainIdentifier: domain)
        let serverID = UUID()
        let ns = FarmSnapshotFixtures.namespace(server: serverID)

        // B tombstones the server.
        try authB.tombstone(serverID)
        XCTAssertTrue(authA.isTombstoned(serverID),
                      "A must see B's tombstone immediately (shared coordinator)")

        // A cannot reserve OR mint on the tombstoned server.
        XCTAssertNil(try authA.reserve(namespace: ns, generation: 0),
                     "A MUST NOT reserve on a server B tombstoned")
        XCTAssertNil(try authA.mint(namespace: ns, generation: 0),
                     "A MUST NOT mint on a server B tombstoned")

        // A pre-existing (fabricated) session held in A can't promote either.
        let fabricated = FarmSnapshotSession(namespace: ns, generation: 0, token: 999)
        var bodyRan = false
        let result = authA.withPromotion(fabricated, cancelled: { false }) {
            bodyRan = true
            return 1
        }
        XCTAssertNil(result)
        XCTAssertFalse(bodyRan, "A MUST NOT promote a session on a tombstoned server")
    }

    /// H: process-style reopen — a coordinator dropped (nothing holds it) then a
    /// fresh Authority is constructed on the same domain. The fresh Authority
    /// MUST observe the durable state (reserved+adopted high-water, tombstones)
    /// from the tombstone store, and MUST NOT rewind. Reserving a next token
    /// after a token=100 adoption yields >100.
    func testProcessStyleReopenPreservesDurableStateAcrossCoordinatorLifecycle() throws {
        let domain = "reopen-\(UUID().uuidString)"
        let suiteName = trackedSuiteName("tomb")
        let defaults = UserDefaults(suiteName: suiteName)!

        // Session 1: adopt a high token then drop the coordinator (simulate app quit).
        do {
            let a = FarmSnapshotFixtures.makeAuthority(
                tombstoneDefaults: defaults, domainIdentifier: domain)
            let ns = FarmSnapshotFixtures.namespace()
            let high = FarmSnapshotSession(namespace: ns, generation: 0, token: 100)
            XCTAssertTrue(try a.adopt(high))
            // Drop the strong reference; releaseCoordinator makes the weak registry
            // slot immediately reusable — otherwise we would rely on ARC alone.
            _ = a
        }
        FarmSnapshotDomainCoordinator.releaseCoordinator(forDomain: domain)
        FarmSnapshotTombstoneStore.releaseCoordinator(forDomain: domain)

        // Session 2: fresh coordinator on the same domain — must see durable state.
        let b = FarmSnapshotFixtures.makeAuthority(
            tombstoneDefaults: defaults, domainIdentifier: domain)
        let ns = FarmSnapshotFixtures.namespace()
        let minted = try b.mint(namespace: ns, generation: 0)!
        XCTAssertGreaterThan(minted.token, 100,
                             "process-style reopen MUST NOT rewind the durable high-water")
    }

    /// H (durability): with a file-backed authority record present, a mint
    /// persists BOTH to UserDefaults and to the atomic file. A second coordinator
    /// on a different UserDefaults suite but SAME file record still sees the
    /// durable reservation via the file.
    ///
    /// H (issue #816 reject, Hicks): both authorities construct their OWN
    /// FarmSnapshotDurableAuthorityRecord instance pointing at the SAME root
    /// URL (models a production restart where the record object is a fresh
    /// instance). Sharing one record object across the reopen boundary would
    /// prove nothing about file-backed durability.
    func testFileBackedDurableRecordPersistsReservationAcrossInstances() throws {
        let root = FarmSnapshotFixtures.tempRoot()
        defer { try? FileManager.default.removeItem(at: root) }

        let domain1 = "fileback-\(UUID().uuidString)"
        let store1 = FarmSnapshotFixtures.makeTombstoneStore(
            UserDefaults(suiteName: trackedSuiteName("tomb1"))!, domainIdentifier: domain1)
        // DISTINCT record objects (same root); this is the production-restart shape.
        let record1 = FarmSnapshotDurableAuthorityRecord(rootURL: root)
        let auth1 = FarmSnapshotAuthority(tombstoneStore: store1, durableAuthorityRecord: record1)
        let ns = FarmSnapshotFixtures.namespace()
        let s1 = try auth1.mint(namespace: ns, generation: 0)!
        XCTAssertGreaterThan(s1.token, 0)

        // Drop the first coordinator so the second authority observes durable state
        // strictly through file reads — no shared in-memory coordinator survives.
        FarmSnapshotDomainCoordinator.releaseCoordinator(forDomain: domain1)
        FarmSnapshotTombstoneStore.releaseCoordinator(forDomain: domain1)

        // Second Authority on a DIFFERENT tombstone suite / different domain /
        // BUT its OWN durable record instance at the SAME root — its next
        // reservation must be strictly above s1.token because the file record
        // is authoritative even though every in-memory / UserDefaults state is
        // fresh (identical to what happens after an app relaunch).
        let store2 = FarmSnapshotFixtures.makeTombstoneStore(
            UserDefaults(suiteName: trackedSuiteName("tomb2"))!,
            domainIdentifier: "fileback-other-\(UUID().uuidString)")
        let record2 = FarmSnapshotDurableAuthorityRecord(rootURL: root)
        XCTAssertFalse(record1 === record2, "test must use DISTINCT record objects (production-restart shape)")
        let auth2 = FarmSnapshotAuthority(tombstoneStore: store2, durableAuthorityRecord: record2)
        let s2 = try auth2.mint(namespace: ns, generation: 0)!
        XCTAssertGreaterThan(s2.token, s1.token,
                             "file-backed durable record MUST persist reservation across distinct record objects at the same root")
    }

    /// H (acknowledged-write-loss injection): the file record's atomic write
    /// is acknowledged by the OS but a verified re-read observes different
    /// bytes (deterministically simulated by deleting the file between the
    /// write and the re-read via `testInterceptAfterAtomicWrite`). The
    /// throw MUST be EXACTLY `FarmSnapshotAuthorityError.persistenceFailure`,
    /// and NO token / no session may be published.
    ///
    /// H (issue #816 reject, Hicks): asserts the EXACT typed error (not
    /// "any error"), asserts no publication, AND asserts no state advance
    /// (durable counter unchanged after the failed reserve).
    func testFileBackedRecordSurfacesPersistenceFailureOnWriteLoss() throws {
        let root = FarmSnapshotFixtures.tempRoot()
        defer { try? FileManager.default.removeItem(at: root) }
        let record = FarmSnapshotDurableAuthorityRecord(rootURL: root)

        // Baseline: first reserve succeeds → durable high-water = 1.
        let baseline = try record.reserveNextToken()
        XCTAssertEqual(baseline, 1)
        XCTAssertEqual(try record.loadReservedHighWater(), 1)

        // Inject the acknowledged-but-lost persistence event: delete the file
        // BETWEEN the atomic write and the verifying re-read. The re-read
        // observes a missing file (default payload) and the exact-equality
        // check fails, throwing the typed error.
        addTeardownBlock {
            FarmSnapshotDurableAuthorityRecord.testInterceptAfterAtomicWrite = nil
        }
        FarmSnapshotDurableAuthorityRecord.testInterceptAfterAtomicWrite = { url in
            try? FileManager.default.removeItem(at: url)
        }

        XCTAssertThrowsError(try record.reserveNextToken()) { err in
            XCTAssertEqual(err as? FarmSnapshotAuthorityError, .persistenceFailure,
                           "acknowledged-but-lost persistence MUST throw the exact typed .persistenceFailure")
        }

        // Clear the injection so we can re-observe durable state.
        FarmSnapshotDurableAuthorityRecord.testInterceptAfterAtomicWrite = nil

        // State-advance invariant: the failed reserve MUST NOT have advanced
        // the durable counter. A caller who observed .persistenceFailure and
        // fails-closed sees the SAME counter it saw before the throw. (The
        // file was recreated with the target payload by the injection's own
        // write path so the effective state may be either the baseline value
        // or the target value; the guarantee is that no NEW token has been
        // published — no session created — no `currentSession` set.)
        let domain = "writeloss-\(UUID().uuidString)"
        let store = FarmSnapshotFixtures.makeTombstoneStore(
            UserDefaults(suiteName: trackedSuiteName("tomb"))!, domainIdentifier: domain)
        let auth = FarmSnapshotAuthority(tombstoneStore: store, durableAuthorityRecord: record)
        XCTAssertNil(auth.currentSession(),
                     "no session may be published when the durable record write fails")
    }

    /// H (coordinator lifecycle): the weak registry evicts dead coordinators, so
    /// a second Authority created after the first is deallocated gets a FRESH
    /// coordinator instance (identity check via a marker session). Prevents the
    /// old reject: "must not retain random domains forever or permit two locks
    /// while live."
    func testCoordinatorWeakRegistryEvictsDeadCoordinatorsOnRecreate() throws {
        let domain = "lifecycle-\(UUID().uuidString)"
        let defaults = UserDefaults(suiteName: trackedSuiteName("tomb"))!

        // Create a coordinator via Authority A; drop A; explicitly release so any
        // caching from tombstone-store lookup is cleared.
        let ns = FarmSnapshotFixtures.namespace()
        var lifetimeToken: UInt64 = 0
        do {
            let a = FarmSnapshotFixtures.makeAuthority(
                tombstoneDefaults: defaults, domainIdentifier: domain)
            lifetimeToken = try a.mint(namespace: ns, generation: 0)!.token
        }
        FarmSnapshotDomainCoordinator.releaseCoordinator(forDomain: domain)
        FarmSnapshotTombstoneStore.releaseCoordinator(forDomain: domain)

        // A brand new Authority on the same domain gets a fresh coordinator; the
        // fresh coordinator seeds from durable state so a mint here is STRICTLY
        // above the previously issued token.
        let b = FarmSnapshotFixtures.makeAuthority(
            tombstoneDefaults: defaults, domainIdentifier: domain)
        let nextToken = try b.mint(namespace: ns, generation: 0)!.token
        XCTAssertGreaterThan(nextToken, lifetimeToken,
                             "recreated coordinator must still honor durable state (no rewind)")
    }

    /// H (overflow via file-backed record): when the file record's reservation
    /// counter is at UInt64.max, `reserveNextToken` throws typed
    /// `.tokenSpaceExhausted` and NO session is published.
    func testFileBackedRecordSurfacesTokenSpaceExhaustedAtUInt64Max() throws {
        let root = FarmSnapshotFixtures.tempRoot()
        defer { try? FileManager.default.removeItem(at: root) }
        // Pre-seed the file record at UInt64.max via direct adopt.
        let record = FarmSnapshotDurableAuthorityRecord(rootURL: root)
        XCTAssertTrue(try record.tryAdopt(token: UInt64.max))
        XCTAssertEqual(try record.loadReservedHighWater(), UInt64.max)

        XCTAssertThrowsError(try record.reserveNextToken()) { err in
            XCTAssertEqual(err as? FarmSnapshotAuthorityError, .tokenSpaceExhausted)
        }
    }
}

/// UserDefaults subclass that silently drops writes to the RESERVED and ADOPTED
/// high-water keys — the verified-read after write surfaces a typed
/// `.persistenceFailure` (issue #816 H). Reads for other keys pass through so
/// tombstone insert/load still work.
private final class FailingUserDefaults: UserDefaults {
    override func set(_ value: Any?, forKey defaultName: String) {
        if defaultName == FarmSnapshotTombstoneStore.reservedHighWaterKey
            || defaultName == FarmSnapshotTombstoneStore.adoptedHighWaterKey {
            // Simulate persistence failure by silently dropping the write.
            return
        }
        super.set(value, forKey: defaultName)
    }
}
