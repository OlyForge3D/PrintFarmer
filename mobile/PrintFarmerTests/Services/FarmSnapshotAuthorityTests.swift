import XCTest
@testable import PrintFarmer

/// Authority + owner-store unit behavior (issue #816, Gates A/B/E).
final class FarmSnapshotAuthorityTests: XCTestCase {

    // MARK: Authority

    func testMintAdvancesMonotonicTokenAndSupersedes() {
        let authority = FarmSnapshotFixtures.makeAuthority(tombstoneDefaults: UserDefaults(suiteName: trackedSuiteName("tomb"))!)
        let namespace = FarmSnapshotFixtures.namespace()

        let first = authority.mint(namespace: namespace, generation: 0)
        let second = authority.mint(namespace: namespace, generation: 0)

        XCTAssertNotNil(first)
        XCTAssertNotNil(second)
        XCTAssertGreaterThan(second!.token, first!.token)
        // The newer session supersedes the older one entirely.
        XCTAssertTrue(authority.isCurrent(second!))
        XCTAssertFalse(authority.isCurrent(first!))
    }

    func testTombstonedServerCannotMint() {
        let authority = FarmSnapshotFixtures.makeAuthority(tombstoneDefaults: UserDefaults(suiteName: trackedSuiteName("tomb"))!)
        let namespace = FarmSnapshotFixtures.namespace()
        authority.tombstone(namespace.serverID)

        XCTAssertNil(authority.mint(namespace: namespace, generation: 0))
        XCTAssertTrue(authority.isTombstoned(namespace.serverID))
    }

    func testTombstoneRevokesMatchingCurrentSession() {
        let authority = FarmSnapshotFixtures.makeAuthority(tombstoneDefaults: UserDefaults(suiteName: trackedSuiteName("tomb"))!)
        let namespace = FarmSnapshotFixtures.namespace()
        let session = authority.mint(namespace: namespace, generation: 0)!

        authority.tombstone(namespace.serverID)
        XCTAssertFalse(authority.isCurrent(session))
        XCTAssertNil(authority.currentSession())
    }

    func testRevokeClearsCurrent() {
        let authority = FarmSnapshotFixtures.makeAuthority(tombstoneDefaults: UserDefaults(suiteName: trackedSuiteName("tomb"))!)
        let session = authority.mint(namespace: FarmSnapshotFixtures.namespace(), generation: 0)!
        authority.revoke()
        XCTAssertNil(authority.currentSession())
        XCTAssertFalse(authority.isCurrent(session))
    }

    func testWithPromotionSkipsWhenNotCurrent() {
        let authority = FarmSnapshotFixtures.makeAuthority(tombstoneDefaults: UserDefaults(suiteName: trackedSuiteName("tomb"))!)
        let session = authority.mint(namespace: FarmSnapshotFixtures.namespace(), generation: 0)!
        authority.revoke()

        var ran = false
        let result: Bool? = authority.withPromotion(session, cancelled: { false }) {
            ran = true
            return true
        }
        XCTAssertNil(result)
        XCTAssertFalse(ran)
    }

    func testWithPromotionSkipsWhenCancelled() {
        let authority = FarmSnapshotFixtures.makeAuthority(tombstoneDefaults: UserDefaults(suiteName: trackedSuiteName("tomb"))!)
        let session = authority.mint(namespace: FarmSnapshotFixtures.namespace(), generation: 0)!
        var ran = false
        let result: Bool? = authority.withPromotion(session, cancelled: { true }) {
            ran = true
            return true
        }
        XCTAssertNil(result)
        XCTAssertFalse(ran)
    }

    func testIsCurrentRequiresExactSession() {
        let authority = FarmSnapshotFixtures.makeAuthority(tombstoneDefaults: UserDefaults(suiteName: trackedSuiteName("tomb"))!)
        let namespace = FarmSnapshotFixtures.namespace()
        let session = authority.mint(namespace: namespace, generation: 0)!
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
