import XCTest
import KeychainSwift
@testable import PrintFarmer

/// In-memory KeychainSwift so credentials round-trip in the unit-test host (the real
/// keychain is unavailable there). Lets a container switch resolve a valid access
/// token and actually reach `connect()` for the connect-boundary orphan proof (#816 C).
private final class InMemoryKeychain: KeychainSwift, @unchecked Sendable {
    private let store = NSMutableDictionary()
    private let lock = NSLock()
    @discardableResult
    override func set(_ value: String, forKey key: String, withAccess access: KeychainSwiftAccessOptions? = nil) -> Bool {
        lock.lock(); store[key] = value; lock.unlock(); return true
    }
    override func get(_ key: String) -> String? {
        lock.lock(); defer { lock.unlock() }; return store[key] as? String
    }
    @discardableResult
    override func delete(_ key: String) -> Bool {
        lock.lock(); store.removeObject(forKey: key); lock.unlock(); return true
    }
}

/// Dedicated SignalR probe for the connect-boundary orphan proof (issue #816 C). Its
/// `connect()` signals an entered latch then waits on an idempotent release gate; its
/// `disconnect()` only RECORDS the call (never touches the shared hub / never blocks on
/// connect). All observation methods delegate to a base mock. This isolates the
/// connect/disconnect ordering from the hub coordinator so the test cannot deadlock.
private final class OrphanProbeSignalR: SignalRServiceProtocol, @unchecked Sendable {
    private let base = MockSignalRService()
    let connectEntered = AsyncBarrier()
    let connectGate = AsyncBarrier()
    private let lock = NSLock()
    private var disconnects = 0
    var disconnectCount: Int { withLock { disconnects } }
    private func withLock<T>(_ body: () -> T) -> T { lock.lock(); defer { lock.unlock() }; return body() }

    var connectionState: SignalRConnectionState { base.connectionState }

    func connect() async throws {
        connectEntered.signal()               // causal: entered the connect boundary
        await connectGate.arriveAndWait()      // wait on an idempotent release gate
    }

    func disconnect() async {
        withLock { disconnects += 1 }          // record only, non-blocking
    }

    @discardableResult
    func onConnectionStateChanged(_ handler: @escaping @Sendable (SignalRConnectionState) -> Void) -> (initial: SignalRConnectionState, subscription: SignalRSubscription) {
        base.onConnectionStateChanged(handler)
    }
    @discardableResult func onPrinterUpdated(_ handler: @escaping @Sendable (PrinterStatusUpdate) -> Void) -> SignalRSubscription { base.onPrinterUpdated(handler) }
    @discardableResult func onJobQueueUpdated(_ handler: @escaping @Sendable (JobQueueUpdate) -> Void) -> SignalRSubscription { base.onJobQueueUpdated(handler) }
    @discardableResult func onAttentionChanged(_ handler: @escaping @Sendable (AttentionChangedEvent) -> Void) -> SignalRSubscription { base.onAttentionChanged(handler) }
    @discardableResult func onFilamentCoverageChanged(_ handler: @escaping @Sendable (FilamentCoverageChangedEvent) -> Void) -> SignalRSubscription { base.onFilamentCoverageChanged(handler) }
    @discardableResult func onTaskInvalidated(_ handler: @escaping @Sendable (ShiftTaskInvalidation) -> Void) -> SignalRSubscription { base.onTaskInvalidated(handler) }
    func onFallbackGroupsUpdated(_ handler: @escaping @Sendable (FallbackGroupsUpdatedEvent) -> Void) { base.onFallbackGroupsUpdated(handler) }
}

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
        AuthServiceUserDefaultsBox(UserDefaults(suiteName: trackedSuiteName("container"))!)
    }

    private func ownerStore() -> FarmSnapshotOwnerStore {
        FarmSnapshotOwnerStore(userDefaults: UserDefaults(suiteName: trackedSuiteName("owner"))!)
    }

    private func registry() -> ServerRegistry {
        ServerRegistry(userDefaults: UserDefaults(suiteName: trackedSuiteName("reg"))!, migrateLegacyServerURL: false)
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

        let authority = FarmSnapshotFixtures.makeAuthority(tombstoneDefaults: UserDefaults(suiteName: trackedSuiteName("tomb"))!)
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

        let authority = FarmSnapshotFixtures.makeAuthority(tombstoneDefaults: UserDefaults(suiteName: trackedSuiteName("tomb"))!)
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

        let authority = FarmSnapshotFixtures.makeAuthority(tombstoneDefaults: UserDefaults(suiteName: trackedSuiteName("tomb"))!)
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
        let seedAuthority = FarmSnapshotFixtures.makeAuthority(tombstoneDefaults: UserDefaults(suiteName: trackedSuiteName("tomb"))!)
        let seedStore = FarmSnapshotStore(authority: seedAuthority, rootURL: root)
        let seedSession = try seedAuthority.mint(namespace: namespace, generation: 0)!
        await seedStore.activate(session: seedSession)
        let env = FarmSnapshotFixtures.envelope(
            namespace: namespace, millis: 5000,
            printers: [FarmSnapshotPrinter(FarmSnapshotFixtures.printerWithSecrets(), isPendingReady: false)]
        )
        XAssertEqual(await seedStore.commit(env, capturedSession: seedSession), .committed)

        // Phase 2 (offline relaunch): fresh authority + store on the same disk,
        // same persisted owner, NO network.
        let authority = FarmSnapshotFixtures.makeAuthority(tombstoneDefaults: UserDefaults(suiteName: trackedSuiteName("tomb"))!)
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
        let seedAuthority = FarmSnapshotFixtures.makeAuthority(tombstoneDefaults: UserDefaults(suiteName: trackedSuiteName("tomb"))!)
        let seedStore = FarmSnapshotStore(authority: seedAuthority, rootURL: root)
        let envA = FarmSnapshotFixtures.envelope(namespace: nsA, millis: 1000)
        let envB = FarmSnapshotFixtures.envelope(namespace: nsB, millis: 2000)
        let sA = try seedAuthority.mint(namespace: nsA, generation: 0)!
        await seedStore.activate(session: sA)
        XAssertEqual(await seedStore.commit(envA, capturedSession: sA), .committed)
        let sB = try seedAuthority.mint(namespace: nsB, generation: 0)!
        await seedStore.activate(session: sB)
        XAssertEqual(await seedStore.commit(envB, capturedSession: sB), .committed)

        let authority = FarmSnapshotFixtures.makeAuthority(tombstoneDefaults: UserDefaults(suiteName: trackedSuiteName("tomb"))!)
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

        let authority = FarmSnapshotFixtures.makeAuthority(tombstoneDefaults: UserDefaults(suiteName: trackedSuiteName("tomb"))!)
        let store = FarmSnapshotStore(authority: authority, rootURL: root)
        // Seed a cached snapshot before demo exit.
        let env = FarmSnapshotFixtures.envelope(namespace: namespace, millis: 4200)
        let seed = try authority.mint(namespace: namespace, generation: 0)!
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

    // MARK: H1 — switch epoch: services and snapshot bind the SAME captured server

    func testSwitchBindsSnapshotToSettledServerConsistentWithServices() async throws {
        let reg = registry()
        let a = try reg.add(displayName: "A", baseURL: URL(string: "https://a.example.com")!)
        let b = try reg.add(displayName: "B", baseURL: URL(string: "https://b.example.com")!)
        let userA = UUID(), userB = UUID()
        let owners = ownerStore()
        owners.setOwner(userID: userA, serverID: a.id)
        owners.setOwner(userID: userB, serverID: b.id)
        try reg.setActive(id: a.id)

        let authority = FarmSnapshotFixtures.makeAuthority(tombstoneDefaults: UserDefaults(suiteName: trackedSuiteName("tomb"))!)
        let root = newRoot()
        let store = FarmSnapshotStore(authority: authority, rootURL: root)
        let container = ServiceContainer(
            serverRegistry: reg, userDefaultsBox: box(), observeRegistry: true,
            farmSnapshotAuthority: authority, farmSnapshotStore: store, farmSnapshotOwnerStore: owners
        )
        await container.activateFarmSnapshotForActiveServer()

        // Switch to B via the real registry-observation path, then settle.
        try reg.setActive(id: b.id)
        await container.activateFarmSnapshotForActiveServer()

        // The snapshot session binds B's own owner — the same server the services
        // were rebuilt for. Never a mixed (services=A, snapshot=B) binding.
        let session = await store.currentSession()
        XCTAssertEqual(session?.serverID, b.id)
        XCTAssertEqual(session?.userID, userB)
    }

    func testRapidABASwitchSettlesToFinalServerOwner() async throws {
        // Deterministic A→B→A: a barrier parks the B-switch at its outgoing-service
        // teardown; the newer A intent advances the transition epoch WHILE B is
        // suspended, so the resumed B pass is invalidated before it can build or
        // bind B services. Proven without sleeps/polling (H1).
        let reg = registry()
        let a = try reg.add(displayName: "A", baseURL: URL(string: "https://a.example.com")!)
        let b = try reg.add(displayName: "B", baseURL: URL(string: "https://b.example.com")!)
        let userA = UUID(), userB = UUID()
        let owners = ownerStore()
        owners.setOwner(userID: userA, serverID: a.id)
        owners.setOwner(userID: userB, serverID: b.id)
        try reg.setActive(id: a.id)

        let authority = FarmSnapshotFixtures.makeAuthority(tombstoneDefaults: UserDefaults(suiteName: trackedSuiteName("tomb"))!)
        let root = newRoot()
        let store = FarmSnapshotStore(authority: authority, rootURL: root)
        let recorder = SignalRFactoryRecorder(barrierOnFirst: true)
        let container = ServiceContainer(
            serverRegistry: reg, userDefaultsBox: box(), observeRegistry: true,
            farmSnapshotAuthority: authority, farmSnapshotStore: store, farmSnapshotOwnerStore: owners,
            signalRServiceFactory: recorder.factory
        )

        // Request B: the reconciliation loop parks at the initial (A) service's
        // disconnect barrier before it can capture/build B.
        try reg.setActive(id: b.id)
        await recorder.firstDisconnectBarrier.waitUntilArrived()

        // Newer intent (A) supersedes the suspended B switch. Release B: it must be
        // epoch-invalidated and never build or bind B services.
        try reg.setActive(id: a.id)
        recorder.firstDisconnectBarrier.release()

        await container.activateFarmSnapshotForActiveServer()

        let session = await store.currentSession()
        XCTAssertEqual(session?.serverID, a.id)
        XCTAssertEqual(session?.userID, userA)
        XCTAssertEqual(reg.activeServerID, a.id)
        XCTAssertFalse(recorder.createdBaseURLs.contains { $0.host == "b.example.com" },
                       "superseded B switch must never build B services")
    }

    // MARK: H1 — a suspended switch is invalidated by a newer intent before publish

    func testSuspendedSwitchSupersededByDemoNeverBindsRealServer() async throws {
        let reg = registry()
        let a = try reg.add(displayName: "A", baseURL: URL(string: "https://a.example.com")!)
        let b = try reg.add(displayName: "B", baseURL: URL(string: "https://b.example.com")!)
        let userA = UUID(), userB = UUID()
        let owners = ownerStore()
        owners.setOwner(userID: userA, serverID: a.id)
        owners.setOwner(userID: userB, serverID: b.id)
        try reg.setActive(id: a.id)

        let authority = FarmSnapshotFixtures.makeAuthority(tombstoneDefaults: UserDefaults(suiteName: trackedSuiteName("tomb"))!)
        let root = newRoot()
        let store = FarmSnapshotStore(authority: authority, rootURL: root)
        let recorder = SignalRFactoryRecorder(barrierOnFirst: true)
        // observeRegistry:false isolates the epoch mechanism from registry re-drive:
        // the suspended switch is driven manually via switchToServer.
        let container = ServiceContainer(
            serverRegistry: reg, userDefaultsBox: box(), observeRegistry: false,
            farmSnapshotAuthority: authority, farmSnapshotStore: store, farmSnapshotOwnerStore: owners,
            signalRServiceFactory: recorder.factory
        )

        // Begin a manual switch to B; it parks at the outgoing (A) service teardown.
        let switchTask = Task { await container.switchToServer(b) }
        await recorder.firstDisconnectBarrier.waitUntilArrived()

        // Demo supersedes the suspended real switch by advancing the shared
        // transition epoch synchronously. The resumed B switch is invalidated and
        // must neither build nor bind B services.
        container.switchToDemo()
        recorder.firstDisconnectBarrier.release()
        await switchTask.value

        let session = await store.currentSession()
        XCTAssertNil(session, "demo revoke + supersession leaves no live real session")
        XCTAssertFalse(recorder.createdBaseURLs.contains { $0.host == "b.example.com" },
                       "superseded B switch must never build B services")
    }

    // MARK: H1 — production observer enabled: suspended real switch superseded

    func testProductionObserverSuspendedRealSupersededByDemoNeverRebuildsReal() async throws {
        let reg = registry()
        let a = try reg.add(displayName: "A", baseURL: URL(string: "https://a.example.com")!)
        let b = try reg.add(displayName: "B", baseURL: URL(string: "https://b.example.com")!)
        let userA = UUID(), userB = UUID()
        let owners = ownerStore()
        owners.setOwner(userID: userA, serverID: a.id)
        owners.setOwner(userID: userB, serverID: b.id)
        try reg.setActive(id: a.id)

        let authority = FarmSnapshotFixtures.makeAuthority(tombstoneDefaults: UserDefaults(suiteName: trackedSuiteName("tomb"))!)
        let root = newRoot()
        let store = FarmSnapshotStore(authority: authority, rootURL: root)
        let recorder = SignalRFactoryRecorder(barrierOnFirst: true)
        // PRODUCTION observer enabled — the switch is driven by the real registry
        // observation, not a test-only direct call.
        let container = ServiceContainer(
            serverRegistry: reg, userDefaultsBox: box(), observeRegistry: true,
            farmSnapshotAuthority: authority, farmSnapshotStore: store, farmSnapshotOwnerStore: owners,
            signalRServiceFactory: recorder.factory
        )

        // Registry-observed switch to B parks at the outgoing (A) service teardown.
        try reg.setActive(id: b.id)
        await recorder.firstDisconnectBarrier.waitUntilArrived()

        // Demo supersedes the suspended real switch (records .demo + advances epoch).
        container.switchToDemo()
        recorder.firstDisconnectBarrier.release()
        await container.awaitActiveServerSettled()

        // The resumed B switch must neither build nor bind B, and must not undo demo.
        let demoSession = await store.currentSession()
        XCTAssertNil(demoSession, "demo revoke leaves no live real session")
        XCTAssertFalse(recorder.createdBaseURLs.contains { $0.host == "b.example.com" },
                       "superseded B switch must never build B services")
        XCTAssertNil(container.apiClient, "demo composition (nil apiClient) is preserved, not undone")
    }

    func testProductionObserverSuspendedSwitchSupersededByNoActiveServer() async throws {
        let reg = registry()
        let a = try reg.add(displayName: "A", baseURL: URL(string: "https://a.example.com")!)
        let b = try reg.add(displayName: "B", baseURL: URL(string: "https://b.example.com")!)
        let userA = UUID(), userB = UUID()
        let owners = ownerStore()
        owners.setOwner(userID: userA, serverID: a.id)
        owners.setOwner(userID: userB, serverID: b.id)
        try reg.setActive(id: a.id)

        let authority = FarmSnapshotFixtures.makeAuthority(tombstoneDefaults: UserDefaults(suiteName: trackedSuiteName("tomb"))!)
        let root = newRoot()
        let store = FarmSnapshotStore(authority: authority, rootURL: root)
        let recorder = SignalRFactoryRecorder(barrierOnFirst: true)
        let container = ServiceContainer(
            serverRegistry: reg, userDefaultsBox: box(), observeRegistry: true,
            farmSnapshotAuthority: authority, farmSnapshotStore: store, farmSnapshotOwnerStore: owners,
            signalRServiceFactory: recorder.factory
        )

        // Observed switch to B parks at the A-service teardown.
        try reg.setActive(id: b.id)
        await recorder.firstDisconnectBarrier.waitUntilArrived()

        // Registry transitions to NO active server (logout / no-active equivalent). The
        // suspended B switch is superseded; B must never build or bind.
        try reg.setActive(id: nil)
        recorder.firstDisconnectBarrier.release()
        await container.awaitActiveServerSettled()

        let session = await store.currentSession()
        XCTAssertNil(session, "no-active-server target leaves no live session")
        XCTAssertFalse(recorder.createdBaseURLs.contains { $0.host == "b.example.com" },
                       "superseded B switch must never build B services")
    }



    func testActivationBindsWhenAuthTokenStaysCurrent() async throws {
        let (container, store, a, userA) = try makeSingleServerContainer(tombstonedDummy: false)
        let token = container.authOperationEpoch.advance()
        await container.activateFarmSnapshotForActiveServer(authToken: token)
        let session = await store.currentSession()
        XCTAssertEqual(session?.serverID, a)
        XCTAssertEqual(session?.userID, userA)
    }

    func testActivationWithStaleAuthTokenDoesNotBind() async throws {
        let (container, store, _, _) = try makeSingleServerContainer(tombstonedDummy: false)
        let stale = container.authOperationEpoch.advance()
        _ = container.authOperationEpoch.advance() // a newer auth op supersedes `stale`
        await container.activateFarmSnapshotForActiveServer(authToken: stale)
        let session = await store.currentSession()
        XCTAssertNil(session, "a superseded login must not publish a snapshot binding")
    }

    func testAuthEpochAdvanceDuringActivationFailsFinalPublicationCAS() async throws {
        // Park the activation inside `store.activate`'s startup tombstone sweep, then
        // advance the auth epoch (models a logout/newer login landing DURING the
        // activation await). The final exact-token CAS at publication must fail — no
        // binding is published for the superseded operation (issue #816 H2, Bishop).
        let io = ControlledFarmSnapshotFileIO()
        let (container, store, _, _) = try makeSingleServerContainer(tombstonedDummy: true, io: io)
        let token = container.authOperationEpoch.advance()

        let barrier = AsyncBarrier()
        io.removeItemBarrier = barrier
        let task = Task { await container.activateFarmSnapshotForActiveServer(authToken: token) }
        await barrier.waitUntilArrived()
        _ = container.authOperationEpoch.advance() // newer auth op lands during activation
        barrier.release()
        await task.value

        let session = await store.currentSession()
        XCTAssertNil(session, "auth-token CAS must block publication when the epoch advanced during activation")
    }

    func testLoginActivationInFlightThenDemoNeverBindsRealSnapshot() async throws {
        // Bishop: a real login's snapshot activation is in flight (parked in readiness);
        // the user enters demo through the production path. Entering demo advances the
        // auth epoch AND records the `.demo` desired target, so the resumed activation
        // publishes NO real snapshot session and demo (nil apiClient) is preserved.
        let io = ControlledFarmSnapshotFileIO()
        let (container, store, _, _) = try makeSingleServerContainer(tombstonedDummy: true, io: io)
        let token = container.authOperationEpoch.advance()

        let barrier = AsyncBarrier()
        io.removeItemBarrier = barrier
        let task = Task { await container.activateFarmSnapshotForActiveServer(authToken: token) }
        await barrier.waitUntilArrived() // login's snapshot activation parked in readiness
        container.switchToDemo()          // enter demo through the production path
        barrier.release()
        await task.value

        let demoSession = await store.currentSession()
        XCTAssertNil(demoSession, "no real snapshot session binds while demo is active")
        XCTAssertNil(container.apiClient, "demo composition (nil apiClient) is preserved")
    }

    /// Builds an `observeRegistry:false` container with one active server (A) whose
    /// owner is persisted. When `tombstonedDummy` is set, a dummy tombstone forces the
    /// startup sweep inside `store.activate` to call `removeItem` (a gate-able await).
    private func makeSingleServerContainer(
        tombstonedDummy: Bool,
        io: ControlledFarmSnapshotFileIO? = nil
    ) throws -> (ServiceContainer, FarmSnapshotStore, UUID, UUID) {
        let reg = registry()
        let a = try reg.add(displayName: "A", baseURL: URL(string: "https://a.example.com")!)
        let userA = UUID()
        let owners = ownerStore()
        owners.setOwner(userID: userA, serverID: a.id)
        try reg.setActive(id: a.id)

        let authority = FarmSnapshotFixtures.makeAuthority(tombstoneDefaults: UserDefaults(suiteName: trackedSuiteName("tomb"))!)
        if tombstonedDummy { authority.tombstone(UUID()) }
        let root = newRoot()
        let store: FarmSnapshotStore = io.map {
            FarmSnapshotStore(authority: authority, fileIO: $0, rootURL: root, ownerStore: owners)
        } ?? FarmSnapshotStore(authority: authority, rootURL: root, ownerStore: owners)
        let container = ServiceContainer(
            serverRegistry: reg, userDefaultsBox: box(), observeRegistry: false,
            farmSnapshotAuthority: authority, farmSnapshotStore: store, farmSnapshotOwnerStore: owners
        )
        return (container, store, a.id, userA)
    }

    // MARK: C — connect-boundary: demo/no-active supersession disconnects the exact incoming service

    func testIncomingConnectSupersededByDemoDisconnectsExactService() async throws {
        try await runIncomingConnectSupersededProof(enterDemo: true)
    }

    func testIncomingConnectSupersededByNoActiveDisconnectsExactService() async throws {
        try await runIncomingConnectSupersededProof(enterDemo: false)
    }

    private func runIncomingConnectSupersededProof(enterDemo: Bool) async throws {
        let reg = registry()
        let a = try reg.add(displayName: "A", baseURL: URL(string: "https://a.example.com")!)
        let b = try reg.add(displayName: "B", baseURL: URL(string: "https://b.example.com")!)
        let userA = UUID(), userB = UUID()
        let owners = ownerStore()
        owners.setOwner(userID: userA, serverID: a.id)
        owners.setOwner(userID: userB, serverID: b.id)
        try reg.setActive(id: a.id)
        let creds = ServerCredentialsStore(keychain: InMemoryKeychain())
        creds.save(ServerCredentials(accessToken: "tok-b", expiresAt: nil), serverId: b.id)

        let probe = OrphanProbeSignalR()
        let container = ServiceContainer(
            serverRegistry: reg, credentialsStore: creds, userDefaultsBox: box(), observeRegistry: false,
            farmSnapshotAuthority: FarmSnapshotFixtures.makeAuthority(tombstoneDefaults: UserDefaults(suiteName: trackedSuiteName("tomb"))!),
            farmSnapshotStore: FarmSnapshotStore(authority: FarmSnapshotAuthority(tombstoneStore: FarmSnapshotFixtures.makeTombstoneStore(UserDefaults(suiteName: trackedSuiteName("t2"))!)), rootURL: newRoot()),
            farmSnapshotOwnerStore: owners,
            signalRServiceFactory: { baseURL, _ in baseURL.host == "b.example.com" ? probe : MockSignalRService() as SignalRServiceProtocol }
        )

        // Start the real switch to B; its signalR reaches connect() and parks there.
        let switchTask = Task { await container.switchToServer(b) }

        // Item 9: install ACTUAL idempotent release + task cancel/drain teardown BEFORE
        // any throwing statement or assertion. `defer` unblocks the parked connect
        // immediately (idempotent release); the do/catch drains + cancels the switch
        // task on any thrown error so a suspended task can never leak. `deinit` rescue
        // on the barrier is only a secondary safety net, never the primary path.
        defer { probe.connectGate.release() }
        do {
            await probe.connectEntered.waitUntilArrived()

            // Supersede on the MainActor. switchToDemo/switchToReal advance the transition
            // epoch + record the new desired target SYNCHRONOUSLY, so the supersession has
            // executed BEFORE we release connect (MainActor serialization — no poll/sleep).
            if enterDemo {
                container.switchToDemo()
            } else {
                try reg.setActive(id: nil)   // no-active target
                container.switchToReal()     // apply the no-active/real target synchronously
            }
            probe.connectGate.release()
            _ = await switchTask.value

            // The exact displaced incoming B service was disconnected (no orphan receive
            // loop survives), and it is no longer the container's current service.
            let disconnects = probe.disconnectCount
            XCTAssertGreaterThanOrEqual(disconnects, 1, "the superseded incoming B signalR must be disconnected")
            XCTAssertFalse(container.signalRService === probe, "the orphaned service is not the current one")
            if enterDemo {
                XCTAssertNil(container.apiClient, "demo composition preserved")
            }
        } catch {
            probe.connectGate.release()   // idempotent: unblock the parked connect
            switchTask.cancel()
            _ = await switchTask.value    // drain the suspended switch task before failing
            throw error
        }
    }

    // MARK: C — real signalR is disconnected (not orphaned) when demo supersedes

    func testSwitchToDemoDisconnectsDisplacedRealSignalR() async throws {
        let reg = registry()
        _ = try reg.add(displayName: "A", baseURL: URL(string: "https://a.example.com")!)
        try reg.setActive(id: reg.servers[0].id)

        let recorder = SignalRFactoryRecorder(barrierOnFirst: false)
        let container = ServiceContainer(
            serverRegistry: reg, userDefaultsBox: box(), observeRegistry: false,
            farmSnapshotAuthority: FarmSnapshotFixtures.makeAuthority(tombstoneDefaults: UserDefaults(suiteName: trackedSuiteName("tomb"))!),
            farmSnapshotStore: FarmSnapshotStore(authority: FarmSnapshotAuthority(tombstoneStore: FarmSnapshotFixtures.makeTombstoneStore(UserDefaults(suiteName: trackedSuiteName("t2"))!)), rootURL: newRoot()),
            farmSnapshotOwnerStore: ownerStore(),
            signalRServiceFactory: recorder.factory
        )
        // The initial real signalR (created for the active server at init).
        let realService = recorder.service(forHost: "a.example.com")
        XCTAssertNotNil(realService)
        let disconnected = AsyncBarrier()
        realService?.disconnectHook = { disconnected.signal() }

        // Entering demo must disconnect that EXACT displaced real instance, not orphan it.
        container.switchToDemo()
        await disconnected.waitUntilArrived()

        XCTAssertTrue(realService?.disconnectCalled ?? false, "displaced real signalR must be disconnected on demo")
        XCTAssertNil(container.apiClient, "demo composition preserved")
    }

    // MARK: Structural helper — records factory-created services + a first-disconnect barrier

    final class SignalRFactoryRecorder: @unchecked Sendable {
        let firstDisconnectBarrier = AsyncBarrier()
        /// Barrier awaited inside `connect()` for services created for `connectBarrierHost`.
        let connectBarrier = AsyncBarrier()
        var connectBarrierHost: String?
        private let lock = NSLock()
        private(set) var createdBaseURLs: [URL] = []
        private(set) var services: [String: MockSignalRService] = [:] // host -> service
        private var callCount = 0
        private let barrierOnFirst: Bool

        init(barrierOnFirst: Bool) { self.barrierOnFirst = barrierOnFirst }

        func service(forHost host: String) -> MockSignalRService? {
            lock.lock(); defer { lock.unlock() }
            return services[host]
        }

        var factory: ServiceContainer.SignalRServiceFactory {
            { [weak self] baseURL, _ in
                guard let self else { return MockSignalRService() }
                let service = MockSignalRService()
                let isFirst: Bool = {
                    self.lock.lock(); defer { self.lock.unlock() }
                    self.createdBaseURLs.append(baseURL)
                    if let host = baseURL.host { self.services[host] = service }
                    defer { self.callCount += 1 }
                    return self.callCount == 0
                }()
                if isFirst && self.barrierOnFirst {
                    let barrier = self.firstDisconnectBarrier
                    service.disconnectHook = { await barrier.arriveAndWait() }
                }
                if let host = baseURL.host, host == self.connectBarrierHost {
                    let barrier = self.connectBarrier
                    service.connectHook = { await barrier.arriveAndWait() }
                }
                return service
            }
        }
    }
}
