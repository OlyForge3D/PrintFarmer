import XCTest
@testable import PrintFarmer

/// D (issue #816): startup-preparation failure must propagate as a typed
/// `.preparationFailed` result — never silently "activated" — and a later retry
/// must bind the SAME authenticated server without a new login.
///
/// Semantics documented for #816 D:
/// * `ServiceContainer.activateFarmSnapshotForActiveServer` returns a typed
///   `FarmSnapshotActivationResult`; `.preparationFailed` means startup readiness
///   (residue sweep) failed and no session was published.
/// * The auth flow (via `AuthViewModel.recordActivationOutcome`) then marks
///   `snapshotActivationPending = true` and keeps the auth op token so
///   `retrySnapshotActivationIfPending()` can re-run activation without re-login.
/// * On retry success the pending flag clears and the same authenticated server
///   binds — no new login, no new user credentials.
@MainActor
final class FarmSnapshotStartupFailureRetryTests: XCTestCase {

    /// Fault-injecting `FarmSnapshotStoring` — `prepareStartup()` returns
    /// `!shouldSucceed`. Flip `shouldSucceed = true` when the caller wants the
    /// next (and subsequent) prep calls to succeed. All other operations delegate
    /// to the inner real store so bind/activate semantics stay real.
    ///
    /// NOTE: `ServiceContainer.init` spawns a fire-and-forget `prepareStartup()`
    /// task for durable residue sweep (H4). A "call the first N times fails"
    /// design would race with that background call. Using an explicit `shouldSucceed`
    /// gate makes the test deterministic regardless of when that background call runs.
    private final class FailingPrepareStore: FarmSnapshotStoring, @unchecked Sendable {
        private let inner: FarmSnapshotStore
        private let lock = NSLock()
        private var shouldSucceed = false
        private var callCount = 0
        var prepareCallCount: Int { lock.lock(); defer { lock.unlock() }; return callCount }

        init(inner: FarmSnapshotStore) { self.inner = inner }

        func armSuccess() { lock.lock(); shouldSucceed = true; lock.unlock() }

        func prepareStartup() async -> Bool {
            let succeed: Bool = {
                lock.lock(); defer { lock.unlock() }
                callCount += 1
                return shouldSucceed
            }()
            if !succeed { return false }
            return await inner.prepareStartup()
        }

        func activate(session: FarmSnapshotSession) async -> Bool { await inner.activate(session: session) }
        func deactivate(session: FarmSnapshotSession) async -> Bool { await inner.deactivate(session: session) }
        func currentSession() async -> FarmSnapshotSession? { await inner.currentSession() }
        func hydrateActive() async -> FarmSnapshotHydration { await inner.hydrateActive() }
        func commit(_ envelope: FarmSnapshotEnvelope, capturedSession: FarmSnapshotSession) async -> FarmSnapshotCommitResult {
            await inner.commit(envelope, capturedSession: capturedSession)
        }
        func purge(serverID: UUID) async -> FarmSnapshotPurgeResult { await inner.purge(serverID: serverID) }
    }

    private var roots: [URL] = []

    override func tearDown() async throws {
        roots.forEach { try? FileManager.default.removeItem(at: $0) }
        roots = []
        try await super.tearDown()
    }

    private func newRoot() -> URL {
        let root = FarmSnapshotFixtures.tempRoot()
        roots.append(root)
        return root
    }

    // MARK: - D1: container returns .preparationFailed on failed prep, then binds on retry

    /// Fail-prep → typed `.preparationFailed` with NO published session → retry
    /// succeeds → binds the same authenticated server (no new login).
    func testPreparationFailureReturnsTypedResultAndRetrySucceeds() async throws {
        let reg = ServerRegistry(
            userDefaults: UserDefaults(suiteName: trackedSuiteName("reg"))!,
            migrateLegacyServerURL: false)
        let a = try reg.add(displayName: "A", baseURL: URL(string: "https://a.example.com")!)
        try reg.setActive(id: a.id)

        let userA = UUID()
        let owners = FarmSnapshotOwnerStore(userDefaults: UserDefaults(suiteName: trackedSuiteName("own"))!)
        owners.setOwner(userID: userA, serverID: a.id)

        let authority = FarmSnapshotFixtures.makeAuthority(
            tombstoneDefaults: UserDefaults(suiteName: trackedSuiteName("tomb"))!)
        let root = newRoot()
        let realStore = FarmSnapshotStore(authority: authority, rootURL: root)
        let failing = FailingPrepareStore(inner: realStore)

        let container = ServiceContainer(
            serverRegistry: reg,
            userDefaultsBox: AuthServiceUserDefaultsBox(UserDefaults(suiteName: trackedSuiteName("auth"))!),
            observeRegistry: false,
            farmSnapshotAuthority: authority,
            farmSnapshotStore: failing,
            farmSnapshotOwnerStore: owners)

        // Mint a valid auth token from the epoch (0 is not "current" — never issued).
        let token = container.authOperationEpoch.advance()

        // 1) First attempt: startup preparation fails → typed .preparationFailed.
        let first = await container.activateFarmSnapshotForActiveServer(authToken: token)
        XCTAssertEqual(first, .preparationFailed,
                       "must surface a typed .preparationFailed, not silently 'activated'")
        XCTAssertNil(authority.currentSession(),
                     "no session may be published when startup preparation failed")

        // 2) Retry (same auth token — no new login): prep now succeeds, bind lands.
        failing.armSuccess()
        let retry = await container.retryFarmSnapshotActivation(authToken: token)
        XCTAssertEqual(retry, .activated,
                       "retry must bind the same authenticated server without a new login")
        let session = authority.currentSession()
        XCTAssertNotNil(session, "authority must hold a bound session after retry")
        XCTAssertEqual(session?.serverID, a.id)
        XCTAssertEqual(session?.userID, userA)
        XCTAssertGreaterThanOrEqual(failing.prepareCallCount, 2,
                                    "retry must re-invoke prepareStartup at least once more")
    }

    // MARK: - D2: end-to-end via AuthViewModel — pending on fail, cleared on retry

    /// AuthViewModel.login → activation fails → `snapshotActivationPending == true`
    /// AND `isAuthenticated == true` (session is authenticated but snapshot NOT
    /// ready — never declared fully ready). Then
    /// `retrySnapshotActivationIfPending()` → prep succeeds → `.activated` and
    /// `snapshotActivationPending == false`.
    func testAuthViewModelMarksPendingOnFailThenClearsOnRetry() async throws {
        // Full network stack for AuthService via MockURLProtocol.
        let mockAPIClient = MockAPIClient()

        // Registry + owner store: shared between AuthService and ServiceContainer so
        // the login can register the server and the container can see it as active.
        let reg = ServerRegistry(
            userDefaults: UserDefaults(suiteName: trackedSuiteName("reg"))!,
            migrateLegacyServerURL: false)
        let serverA = try reg.add(
            displayName: "A", baseURL: URL(string: "https://print.example.com")!,
            makeActiveIfNeeded: true)
        try reg.setActive(id: serverA.id)

        let owners = FarmSnapshotOwnerStore(userDefaults: UserDefaults(suiteName: trackedSuiteName("own"))!)

        // Injected fault store: first prepareStartup() fails; second succeeds.
        let authority = FarmSnapshotFixtures.makeAuthority(
            tombstoneDefaults: UserDefaults(suiteName: trackedSuiteName("tomb"))!)
        let root = newRoot()
        let realStore = FarmSnapshotStore(authority: authority, rootURL: root)
        let failing = FailingPrepareStore(inner: realStore)

        let services = ServiceContainer(
            serverRegistry: reg,
            userDefaultsBox: AuthServiceUserDefaultsBox(UserDefaults(suiteName: trackedSuiteName("auth"))!),
            observeRegistry: false,
            farmSnapshotAuthority: authority,
            farmSnapshotStore: failing,
            farmSnapshotOwnerStore: owners,
            synchronizeOfflineQueueOnStartup: false,
            apiClientFactory: { baseURL, generation, accessToken, authSessionToken, serverID in
                let identity = accessToken.flatMap { token in
                    serverID.map {
                        AuthenticatedIdentity(
                            accessToken: token,
                            serverID: $0,
                            authSessionToken: authSessionToken
                        )
                    }
                }
                return APIClient(
                    baseURL: baseURL,
                    session: mockAPIClient.urlSession,
                    serverGeneration: generation,
                    authenticated: identity
                )
            })
        // Replace the container's default AuthService with one bound to the mock.
        services.authService = AuthService(
            apiClient: mockAPIClient.apiClient,
            userDefaultsBox: AuthServiceUserDefaultsBox(UserDefaults(suiteName: trackedSuiteName("authsvc"))!),
            migrateLegacyServerURL: false,
            serverRegistry: reg,
            snapshotOwnerStore: owners,
            authEpoch: services.authOperationEpoch)
        let vm = AuthViewModel(services: services)

        // Login response uses the fixture format the AuthResponse decoder expects.
        let loginJSON = TestJSON.authResponseSuccess
        mockAPIClient.stubResponses(["/api/auth/login": (200, loginJSON)])

        // 1) Login: succeeds → activation fails (prep fault) → VM marks pending.
        await vm.login(serverURL: "https://print.example.com", username: "admin", password: "pw")
        XCTAssertTrue(vm.isAuthenticated,
                      "auth succeeded — session is authenticated even though snapshot is not ready")
        XCTAssertTrue(vm.snapshotActivationPending,
                      "activation failed → VM must record a retryable pending activation, not silently ready")
        XCTAssertNil(authority.currentSession(),
                     "no session published because startup preparation failed")
        let capabilityRequestsBeforeRetry = mockAPIClient.capturedRequests.filter {
            $0.url?.path == "/api/system/capabilities"
        }.count
        XCTAssertEqual(capabilityRequestsBeforeRetry, 1)

        // 2) Retry (no new login, no new credentials): prep succeeds → bind lands.
        failing.armSuccess()
        let retry = await vm.retrySnapshotActivationIfPending()
        XCTAssertEqual(retry, .activated, "retry must bind without a new login")
        XCTAssertFalse(vm.snapshotActivationPending, "pending flag must clear on successful retry")
        let session = authority.currentSession()
        XCTAssertEqual(session?.serverID, serverA.id, "must bind the same authenticated server")
        XCTAssertGreaterThanOrEqual(failing.prepareCallCount, 2,
                                    "retry must re-invoke prepareStartup at least once more")
        XCTAssertGreaterThan(
            mockAPIClient.capturedRequests.filter {
                $0.url?.path == "/api/system/capabilities"
            }.count,
            capabilityRequestsBeforeRetry,
            "a delayed retry must not reuse the capability result prepared before activation failed"
        )
    }

    // MARK: - D3: switch-before-retry — retry pinned to failed server (ServiceContainer)

    /// D (issue #816 reject): after a preparationFailed, if the user switches the
    /// active server BEFORE retry, the retry MUST NOT bind the current different
    /// server. Retry is pinned to the failed server/generation and returns
    /// `.notApplicable` when either mismatches.
    func testRetryPinnedToFailedServerRefusesToBindCurrentDifferentServer() async throws {
        let reg = ServerRegistry(
            userDefaults: UserDefaults(suiteName: trackedSuiteName("reg"))!,
            migrateLegacyServerURL: false)
        let a = try reg.add(displayName: "A", baseURL: URL(string: "https://a.example.com")!)
        let b = try reg.add(displayName: "B", baseURL: URL(string: "https://b.example.com")!)
        try reg.setActive(id: a.id)

        let userA = UUID()
        let userB = UUID()
        let owners = FarmSnapshotOwnerStore(userDefaults: UserDefaults(suiteName: trackedSuiteName("own"))!)
        owners.setOwner(userID: userA, serverID: a.id)
        owners.setOwner(userID: userB, serverID: b.id)

        let authority = FarmSnapshotFixtures.makeAuthority(
            tombstoneDefaults: UserDefaults(suiteName: trackedSuiteName("tomb"))!)
        let root = newRoot()
        let realStore = FarmSnapshotStore(authority: authority, rootURL: root)
        let failing = FailingPrepareStore(inner: realStore)

        let container = ServiceContainer(
            serverRegistry: reg,
            userDefaultsBox: AuthServiceUserDefaultsBox(UserDefaults(suiteName: trackedSuiteName("auth"))!),
            observeRegistry: false,
            farmSnapshotAuthority: authority,
            farmSnapshotStore: failing,
            farmSnapshotOwnerStore: owners)

        let token = container.authOperationEpoch.advance()
        let genAtFail = container.activeServerGeneration
        let pinnedServerID = a.id

        // 1) First attempt on A fails.
        let first = await container.activateFarmSnapshotForActiveServer(authToken: token)
        XCTAssertEqual(first, .preparationFailed)
        XCTAssertNil(authority.currentSession())

        // 2) User switches to B (arm success too so we can prove the block is
        //    the pin, not the fault).
        try reg.setActive(id: b.id)
        await container.switchToServer(b)
        failing.armSuccess()

        // 3) Retry PINNED to A: must NOT bind B, even though B has an owner and
        //    prep would now succeed.
        let retry = await container.retryFarmSnapshotActivation(
            authToken: token,
            expectedServerID: pinnedServerID,
            expectedGeneration: genAtFail)
        XCTAssertEqual(retry, .notApplicable,
                       "retry pinned to A MUST NOT bind current different server B")
        let session = authority.currentSession()
        XCTAssertNotEqual(session?.serverID, b.id,
                          "pinned retry must never bind the switched-to server B")
    }

    // MARK: - D4: switch-before-retry — VM invalidates pending on server change

    /// D (VM side): after a preparationFailed, if the user switches the active
    /// server BEFORE retry, `retrySnapshotActivationIfPending()` MUST detect the
    /// mismatch, drop the stale pending record, and NOT re-invoke activation. The
    /// pending flag clears and the return is nil.
    func testViewModelDropsStalePendingWhenServerSwitchesBeforeRetry() async throws {
        let mockAPIClient = MockAPIClient()

        let reg = ServerRegistry(
            userDefaults: UserDefaults(suiteName: trackedSuiteName("reg"))!,
            migrateLegacyServerURL: false)
        let a = try reg.add(
            displayName: "A", baseURL: URL(string: "https://print.example.com")!,
            makeActiveIfNeeded: true)
        let b = try reg.add(
            displayName: "B", baseURL: URL(string: "https://b.example.com")!)
        try reg.setActive(id: a.id)

        let owners = FarmSnapshotOwnerStore(userDefaults: UserDefaults(suiteName: trackedSuiteName("own"))!)

        let authority = FarmSnapshotFixtures.makeAuthority(
            tombstoneDefaults: UserDefaults(suiteName: trackedSuiteName("tomb"))!)
        let root = newRoot()
        let realStore = FarmSnapshotStore(authority: authority, rootURL: root)
        let failing = FailingPrepareStore(inner: realStore)

        let services = ServiceContainer(
            serverRegistry: reg,
            userDefaultsBox: AuthServiceUserDefaultsBox(UserDefaults(suiteName: trackedSuiteName("auth"))!),
            observeRegistry: false,
            farmSnapshotAuthority: authority,
            farmSnapshotStore: failing,
            farmSnapshotOwnerStore: owners)
        services.authService = AuthService(
            apiClient: mockAPIClient.apiClient,
            userDefaultsBox: AuthServiceUserDefaultsBox(UserDefaults(suiteName: trackedSuiteName("authsvc"))!),
            migrateLegacyServerURL: false,
            serverRegistry: reg,
            snapshotOwnerStore: owners,
            authEpoch: services.authOperationEpoch)
        let vm = AuthViewModel(services: services)

        mockAPIClient.stubResponses(["/api/auth/login": (200, TestJSON.authResponseSuccess)])
        await vm.login(serverURL: "https://print.example.com", username: "admin", password: "pw")
        XCTAssertTrue(vm.isAuthenticated)
        XCTAssertTrue(vm.snapshotActivationPending, "activation failed → pending")

        // Switch to B, arm success (so the pin is the block, not the fault).
        try reg.setActive(id: b.id)
        await services.switchToServer(b)
        failing.armSuccess()

        let outcome = await vm.retrySnapshotActivationIfPending()
        XCTAssertNil(outcome,
                     "server changed since fail → retry MUST return nil, not bind different server")
        XCTAssertFalse(vm.snapshotActivationPending,
                       "stale pending record MUST be dropped on server change")
        let session = authority.currentSession()
        XCTAssertNotEqual(session?.serverID, b.id,
                          "stale-retry MUST NOT publish a session for the switched-to server B")
    }

    // MARK: - D5: root gate — VM state drives the visible-gate branch

    /// D (UI-gate): the state transitions the RootView switches on are:
    /// `isAuthenticated && snapshotActivationPending == true` → pending gate;
    /// `isAuthenticated && !snapshotActivationPending` → ContentView. This test
    /// asserts those transitions deterministically at the VM layer so the pure
    /// if/else in RootView cannot silently drop back to a `isAuthenticated`-only
    /// gate. When the retry succeeds the pending flag clears — the UI will
    /// re-render into the ContentView branch.
    func testViewModelPendingFlagGovernsRootGateTransitions() async throws {
        let mockAPIClient = MockAPIClient()

        let reg = ServerRegistry(
            userDefaults: UserDefaults(suiteName: trackedSuiteName("reg"))!,
            migrateLegacyServerURL: false)
        let a = try reg.add(
            displayName: "A", baseURL: URL(string: "https://print.example.com")!,
            makeActiveIfNeeded: true)
        try reg.setActive(id: a.id)

        let owners = FarmSnapshotOwnerStore(userDefaults: UserDefaults(suiteName: trackedSuiteName("own"))!)
        let authority = FarmSnapshotFixtures.makeAuthority(
            tombstoneDefaults: UserDefaults(suiteName: trackedSuiteName("tomb"))!)
        let root = newRoot()
        let realStore = FarmSnapshotStore(authority: authority, rootURL: root)
        let failing = FailingPrepareStore(inner: realStore)

        let services = ServiceContainer(
            serverRegistry: reg,
            userDefaultsBox: AuthServiceUserDefaultsBox(UserDefaults(suiteName: trackedSuiteName("auth"))!),
            observeRegistry: false,
            farmSnapshotAuthority: authority,
            farmSnapshotStore: failing,
            farmSnapshotOwnerStore: owners)
        services.authService = AuthService(
            apiClient: mockAPIClient.apiClient,
            userDefaultsBox: AuthServiceUserDefaultsBox(UserDefaults(suiteName: trackedSuiteName("authsvc"))!),
            migrateLegacyServerURL: false,
            serverRegistry: reg,
            snapshotOwnerStore: owners,
            authEpoch: services.authOperationEpoch)
        let vm = AuthViewModel(services: services)

        // Baseline: unauthenticated → neither branch would render the pending gate.
        XCTAssertFalse(vm.isAuthenticated)
        XCTAssertFalse(vm.snapshotActivationPending)

        mockAPIClient.stubResponses(["/api/auth/login": (200, TestJSON.authResponseSuccess)])
        await vm.login(serverURL: "https://print.example.com", username: "admin", password: "pw")

        // Authenticated + pending → the visible retry gate branch fires.
        XCTAssertTrue(vm.isAuthenticated)
        XCTAssertTrue(vm.snapshotActivationPending,
                      "root gate MUST render the pending-retry view (not ContentView) when true")

        // Retry succeeds → pending clears → root gate falls through to ContentView.
        failing.armSuccess()
        let outcome = await vm.retrySnapshotActivationIfPending()
        XCTAssertEqual(outcome, .activated)
        XCTAssertFalse(vm.snapshotActivationPending,
                       "root gate MUST return to ContentView once pending clears")
    }
}
