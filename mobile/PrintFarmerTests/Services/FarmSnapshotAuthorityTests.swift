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
        authority.tombstone(namespace.serverID)

        XCTAssertNil(try authority.mint(namespace: namespace, generation: 0))
        XCTAssertTrue(authority.isTombstoned(namespace.serverID))
    }

    func testTombstoneRevokesMatchingCurrentSession()throws {
        let authority = FarmSnapshotFixtures.makeAuthority(tombstoneDefaults: UserDefaults(suiteName: trackedSuiteName("tomb"))!)
        let namespace = FarmSnapshotFixtures.namespace()
        let session = try authority.mint(namespace: namespace, generation: 0)!

        authority.tombstone(namespace.serverID)
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
}
