import XCTest
@testable import PrintFarmer

/// Container-level authority proofs (issue #816, Gates A/B, blocker). Uses the
/// real `ServiceContainer` + `ServerRegistry` with an injected snapshot trio.
@MainActor
final class FarmSnapshotContainerAuthorityTests: XCTestCase {

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

    private func box() -> AuthServiceUserDefaultsBox {
        AuthServiceUserDefaultsBox(UserDefaults(suiteName: "container-\(UUID().uuidString)")!)
    }

    private func ownerStore() -> FarmSnapshotOwnerStore {
        FarmSnapshotOwnerStore(userDefaults: UserDefaults(suiteName: "owner-\(UUID().uuidString)")!)
    }

    private func registry() -> ServerRegistry {
        ServerRegistry(userDefaults: UserDefaults(suiteName: "reg-\(UUID().uuidString)")!, migrateLegacyServerURL: false)
    }

    // MARK: Structural: activation resolves the settled server's OWN owner

    func testActivateResolvesActiveServersOwnOwner() async throws {
        let reg = registry()
        let a = try reg.add(displayName: "A", baseURL: URL(string: "https://a.example.com")!)
        let b = try reg.add(displayName: "B", baseURL: URL(string: "https://b.example.com")!)
        let userA = UUID(), userB = UUID()
        let owners = ownerStore()
        owners.setOwner(userID: userA, serverID: a.id)
        owners.setOwner(userID: userB, serverID: b.id)
        try reg.setActive(id: b.id)

        let authority = FarmSnapshotAuthority()
        let root = newRoot()
        let store = FarmSnapshotStore(authority: authority, rootURL: root)
        let container = ServiceContainer(
            serverRegistry: reg, userDefaultsBox: box(), observeRegistry: false,
            farmSnapshotAuthority: authority, farmSnapshotStore: store, farmSnapshotOwnerStore: owners
        )

        await container.activateFarmSnapshotForActiveServer()
        let session = await store.currentSession()
        // Active server is B → bound owner must be B's own owner, never A's.
        XCTAssertEqual(session?.serverID, b.id)
        XCTAssertEqual(session?.userID, userB)
        XCTAssertNotEqual(session?.userID, userA)
    }

    // MARK: Blocker — no cross-bind across a settled registry switch (observeRegistry:true)

    func testActivationNeverCrossBindsAcrossSettledSwitch() async throws {
        let reg = registry()
        let a = try reg.add(displayName: "A", baseURL: URL(string: "https://a.example.com")!)
        let b = try reg.add(displayName: "B", baseURL: URL(string: "https://b.example.com")!)
        let userA = UUID(), userB = UUID()
        let owners = ownerStore()
        owners.setOwner(userID: userA, serverID: a.id)
        owners.setOwner(userID: userB, serverID: b.id)
        try reg.setActive(id: a.id)

        let authority = FarmSnapshotAuthority()
        let root = newRoot()
        let store = FarmSnapshotStore(authority: authority, rootURL: root)
        let container = ServiceContainer(
            serverRegistry: reg, userDefaultsBox: box(), observeRegistry: true,
            farmSnapshotAuthority: authority, farmSnapshotStore: store, farmSnapshotOwnerStore: owners
        )

        // Settle the initial A binding.
        await container.activateFarmSnapshotForActiveServer()
        let initial = await store.currentSession()
        XCTAssertEqual(initial?.serverID, a.id)
        XCTAssertEqual(initial?.userID, userA)

        // The registry switches to B (observed, real ordering). Activation initiated
        // in A's context must settle to B's OWN owner — never (B, userA).
        try reg.setActive(id: b.id)
        await container.activateFarmSnapshotForActiveServer()

        let settled = await store.currentSession()
        XCTAssertEqual(settled?.serverID, b.id)
        XCTAssertEqual(settled?.userID, userB)
        XCTAssertNotEqual(settled?.userID, userA, "must never cross-bind (B, userA)")
    }

    // MARK: Token-only fail-closed

    func testTokenOnlyServerFailsClosedNoHydrate() async throws {
        let reg = registry()
        let a = try reg.add(displayName: "A", baseURL: URL(string: "https://a.example.com")!)
        try reg.setActive(id: a.id)
        let owners = ownerStore() // no owner persisted → token-only/legacy

        let authority = FarmSnapshotAuthority()
        let root = newRoot()
        let store = FarmSnapshotStore(authority: authority, rootURL: root)
        let container = ServiceContainer(
            serverRegistry: reg, userDefaultsBox: box(), observeRegistry: false,
            farmSnapshotAuthority: authority, farmSnapshotStore: store, farmSnapshotOwnerStore: owners
        )

        await container.activateFarmSnapshotForActiveServer()
        let session = await store.currentSession()
        XCTAssertNil(session)
        XAssertEqual(await store.hydrateActive(), .inactive)
    }

    // MARK: Cold-offline relaunch activates + hydrates the exact prior owner

    func testColdOfflineRelaunchActivatesAndHydratesExactOwner() async throws {
        let reg = registry()
        let a = try reg.add(displayName: "A", baseURL: URL(string: "https://a.example.com")!)
        try reg.setActive(id: a.id)
        let userA = UUID()
        let owners = ownerStore()
        owners.setOwner(userID: userA, serverID: a.id)
        let namespace = FarmSnapshotNamespace(serverID: a.id, userID: userA)
        let root = newRoot()

        // Phase 1 (online): seed a cached snapshot for (A, userA).
        let seedAuthority = FarmSnapshotAuthority()
        let seedStore = FarmSnapshotStore(authority: seedAuthority, rootURL: root)
        let seedSession = seedAuthority.mint(namespace: namespace, generation: 0)!
        await seedStore.activate(session: seedSession)
        let env = FarmSnapshotFixtures.envelope(
            namespace: namespace, millis: 5000,
            printers: [FarmSnapshotPrinter(FarmSnapshotFixtures.printerWithSecrets())]
        )
        XAssertEqual(await seedStore.commit(env, capturedSession: seedSession), .committed)

        // Phase 2 (offline relaunch): fresh authority + store on the same disk,
        // same persisted owner, NO network.
        let authority = FarmSnapshotAuthority()
        let store = FarmSnapshotStore(authority: authority, rootURL: root)
        let container = ServiceContainer(
            serverRegistry: reg, userDefaultsBox: box(), observeRegistry: false,
            farmSnapshotAuthority: authority, farmSnapshotStore: store, farmSnapshotOwnerStore: owners
        )

        await container.activateFarmSnapshotForActiveServer()
        let session = await store.currentSession()
        XCTAssertEqual(session?.userID, userA)
        XAssertEqual(await store.hydrateActive(), .snapshot(env))
    }

    // MARK: Offline A→B→A selects each persisted owner

    func testOfflineABASelectsEachPersistedOwner() async throws {
        let reg = registry()
        let a = try reg.add(displayName: "A", baseURL: URL(string: "https://a.example.com")!)
        let b = try reg.add(displayName: "B", baseURL: URL(string: "https://b.example.com")!)
        let userA = UUID(), userB = UUID()
        let owners = ownerStore()
        owners.setOwner(userID: userA, serverID: a.id)
        owners.setOwner(userID: userB, serverID: b.id)
        let nsA = FarmSnapshotNamespace(serverID: a.id, userID: userA)
        let nsB = FarmSnapshotNamespace(serverID: b.id, userID: userB)
        let root = newRoot()

        // Seed both namespaces.
        let seedAuthority = FarmSnapshotAuthority()
        let seedStore = FarmSnapshotStore(authority: seedAuthority, rootURL: root)
        let envA = FarmSnapshotFixtures.envelope(namespace: nsA, millis: 1000)
        let envB = FarmSnapshotFixtures.envelope(namespace: nsB, millis: 2000)
        let sA = seedAuthority.mint(namespace: nsA, generation: 0)!
        await seedStore.activate(session: sA)
        XAssertEqual(await seedStore.commit(envA, capturedSession: sA), .committed)
        let sB = seedAuthority.mint(namespace: nsB, generation: 0)!
        await seedStore.activate(session: sB)
        XAssertEqual(await seedStore.commit(envB, capturedSession: sB), .committed)

        let authority = FarmSnapshotAuthority()
        let store = FarmSnapshotStore(authority: authority, rootURL: root)
        let container = ServiceContainer(
            serverRegistry: reg, userDefaultsBox: box(), observeRegistry: false,
            farmSnapshotAuthority: authority, farmSnapshotStore: store, farmSnapshotOwnerStore: owners
        )

        try reg.setActive(id: a.id)
        await container.activateFarmSnapshotForActiveServer()
        XAssertEqual(await store.currentSession()?.userID, userA)
        XAssertEqual(await store.hydrateActive(), .snapshot(envA))

        try reg.setActive(id: b.id)
        await container.activateFarmSnapshotForActiveServer()
        XAssertEqual(await store.currentSession()?.userID, userB)
        XAssertEqual(await store.hydrateActive(), .snapshot(envB))

        try reg.setActive(id: a.id)
        await container.activateFarmSnapshotForActiveServer()
        XAssertEqual(await store.currentSession()?.userID, userA)
        XAssertEqual(await store.hydrateActive(), .snapshot(envA))
    }

    // MARK: Persisted-demo deletion routes through purge / fails closed

    func testDemoCompositionWiresPurgeHandlerAndRemovesServer() async throws {
        let reg = registry()
        let a = try reg.add(displayName: "A", baseURL: URL(string: "https://a.example.com")!)
        _ = ServiceContainer.demo(serverRegistry: reg)
        XCTAssertNotNil(reg.snapshotPurgeHandler)

        try await reg.purgeAndRemove(id: a.id)
        XCTAssertTrue(reg.servers.isEmpty)
    }

    func testPurgeAndRemoveFailsClosedWithoutHandler() async throws {
        let reg = registry()
        let a = try reg.add(displayName: "A", baseURL: URL(string: "https://a.example.com")!)

        do {
            try await reg.purgeAndRemove(id: a.id)
            XCTFail("expected fail-closed without a purge handler")
        } catch let error as ServerRegistryError {
            XCTAssertEqual(error, .purgeUnavailable(a.id))
        }
        XCTAssertEqual(reg.servers.count, 1, "server must be retained when purge is unavailable")
    }

    func testPurgeAndRemoveFailsClosedWhenPurgeFails() async throws {
        let reg = registry()
        let a = try reg.add(displayName: "A", baseURL: URL(string: "https://a.example.com")!)
        reg.snapshotPurgeHandler = { _ in .failed(failureCount: 1) }

        do {
            try await reg.purgeAndRemove(id: a.id)
            XCTFail("expected fail-closed on purge failure")
        } catch let error as ServerRegistryError {
            XCTAssertEqual(error, .purgeFailed(a.id))
        }
        XCTAssertEqual(reg.servers.count, 1)
    }

    // MARK: Persisted-demo exit reactivates real snapshot in the same process

    func testPersistedDemoExitReactivatesRealSnapshot() async throws {
        let reg = registry()
        let a = try reg.add(displayName: "A", baseURL: URL(string: "https://a.example.com")!)
        try reg.setActive(id: a.id)
        let userA = UUID()
        let owners = ownerStore()
        owners.setOwner(userID: userA, serverID: a.id)
        let namespace = FarmSnapshotNamespace(serverID: a.id, userID: userA)
        let root = newRoot()

        let authority = FarmSnapshotAuthority()
        let store = FarmSnapshotStore(authority: authority, rootURL: root)
        // Seed a cached snapshot before demo exit.
        let env = FarmSnapshotFixtures.envelope(namespace: namespace, millis: 4200)
        let seed = authority.mint(namespace: namespace, generation: 0)!
        await store.activate(session: seed)
        XAssertEqual(await store.commit(env, capturedSession: seed), .committed)
        authority.revoke()

        let container = ServiceContainer.demo(
            serverRegistry: reg,
            farmSnapshotAuthority: authority,
            farmSnapshotStore: store,
            farmSnapshotOwnerStore: owners
        )

        // Exit demo → real composition, then a real activation binds + hydrates.
        container.switchToReal()
        await container.activateFarmSnapshotForActiveServer()
        XAssertEqual(await store.currentSession()?.userID, userA)
        XAssertEqual(await store.hydrateActive(), .snapshot(env))
    }
}
