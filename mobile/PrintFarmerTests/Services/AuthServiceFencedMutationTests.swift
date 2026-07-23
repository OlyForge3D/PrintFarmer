import XCTest
@testable import PrintFarmer

/// Scope J tests (issue #816): atomic fencing of ALL auth side effects on the
/// current auth-operation epoch. Login persistence, credential clears,
/// snapshot-owner writes, failed-login clears, and logout local mutations must
/// each run inside a compare-and-set on `authEpoch.isCurrent(operation)` — never
/// check-then-mutate under an await.
@MainActor
final class AuthServiceFencedMutationTests: XCTestCase {

    nonisolated(unsafe) private var mockAPIClient: MockAPIClient!
    private var apiClient: APIClient!
    private var registry: ServerRegistry!
    private var owners: FarmSnapshotOwnerStore!
    private var credentialsStore: ServerCredentialsStore!
    private var epoch: AuthOperationEpoch!
    private var authService: AuthService!
    private var serverID: UUID!

    override func setUp() {
        super.setUp()
        mockAPIClient = MockAPIClient()
        apiClient = mockAPIClient.apiClient
        registry = ServerRegistry(userDefaults: UserDefaults(suiteName: trackedSuiteName("reg"))!, migrateLegacyServerURL: false)
        let server = try! registry.add(displayName: "A", baseURL: URL(string: "https://a.example.com")!)
        try! registry.setActive(id: server.id)
        serverID = server.id
        owners = FarmSnapshotOwnerStore(userDefaults: UserDefaults(suiteName: trackedSuiteName("own"))!)
        credentialsStore = ServerCredentialsStore(keychain: .init(keyPrefix: "AuthJ_\(UUID().uuidString)_"))
        epoch = AuthOperationEpoch()
        authService = AuthService(
            apiClient: apiClient,
            credentialsStore: credentialsStore,
            userDefaultsBox: AuthServiceUserDefaultsBox(UserDefaults(suiteName: trackedSuiteName("auth"))!),
            migrateLegacyServerURL: false,
            serverRegistry: registry,
            snapshotOwnerStore: owners,
            authEpoch: epoch
        )
    }

    override func tearDown() {
        credentialsStore?.clear(serverId: serverID)
        mockAPIClient = nil; apiClient = nil; registry = nil
        owners = nil; credentialsStore = nil; epoch = nil; authService = nil; serverID = nil
        super.tearDown()
    }

    private func userJSON(id: UUID, username: String = "user") -> String {
        """
        {"id":"\(id.uuidString)","username":"\(username)","email":"u@e.com","firstName":null,
         "lastName":null,"isActive":true,"emailConfirmed":true,"lastLogin":null,
         "createdAt":"2026-01-01T00:00:00Z","roles":["farm_admin"],"permissions":[]}
        """
    }

    /// J: T1 login parks on the network handler → test advances the epoch to T2
    /// before releasing → the login response arrives, but every T1 durable
    /// mutation (credential save + owner write) MUST be a no-op because T1 is no
    /// longer current. Neither credentials nor owner reflect T1.
    func testT1LoginParkedThenEpochAdvancesDoesNotPersistT1State() async throws {
        let t1 = epoch.advance()
        XCTAssertEqual(t1, 1)

        let userT1 = UUID()
        let loginJSON = """
        {"success":true,"token":"bearer-T1","expiresAt":null,\
        "user":\(userJSON(id: userT1))}
        """

        let entry = DispatchSemaphore(value: 0)
        let release = DispatchSemaphore(value: 0)
        mockAPIClient.requestHandler = { req in
            let path = req.url?.path ?? ""
            if path.hasSuffix("/api/auth/login") {
                // Park; test will advance the epoch before releasing.
                entry.signal()
                _ = release.wait(timeout: .now() + .seconds(10))
                return (TestData.httpResponse(url: req.url, statusCode: 200), Data(loginJSON.utf8))
            }
            return (TestData.httpResponse(url: req.url, statusCode: 404), Data("{}".utf8))
        }

        let loginTask = Task { () -> AuthLoginOutcome in
            try await self.authService.login(
                serverURL: "https://a.example.com", username: "u", password: "p",
                operation: AuthOperationToken(value: t1)
            )
        }

        // Wait for the network handler to be entered (the login is now parked).
        await withCheckedContinuation { (cont: CheckedContinuation<Void, Never>) in
            DispatchQueue.global().async {
                _ = entry.wait(timeout: .now() + .seconds(10))
                cont.resume()
            }
        }

        // Advance the epoch → T1 is now STALE. All T1 fenced mutations must no-op.
        let t2 = epoch.advance()
        XCTAssertEqual(t2, 2)

        // Release the network. Login proceeds through fencedMutation(T1) which now no-ops.
        release.signal()
        let outcome = try await loginTask.value
        if case .superseded = outcome {} else {
            XCTFail("expected .superseded, got \(outcome)")
        }

        // No T1 credentials were persisted.
        XCTAssertNil(credentialsStore.load(serverId: serverID),
                     "a superseded login MUST NOT persist its credentials")
        // No T1 owner was persisted.
        XCTAssertNil(owners.ownerUserID(serverID: serverID),
                     "a superseded login MUST NOT persist its owner")
    }

    /// J: A stale logout(operation: T1) spanning a concurrent T2 login MUST use a
    /// bearer-T1 session snapshot for its `/logout` network request AND MUST NOT
    /// clear T2's credentials/owner/API bearer. Proves the sessionSnapshotClient
    /// (immutable) is what carries the network call and fencedMutation blocks the
    /// local mutations.
    func testStaleLogoutT1SpanningT2LoginPreservesT2State() async throws {
        // 1) Establish T1 authenticated state.
        let t1 = epoch.advance()
        let userT1 = UUID()
        let loginT1JSON = """
        {"success":true,"token":"bearer-T1","expiresAt":null,\
        "user":\(userJSON(id: userT1))}
        """
        mockAPIClient.stubResponses(["/api/auth/login": (200, loginT1JSON)])
        _ = try await authService.login(
            serverURL: "https://a.example.com", username: "u", password: "p",
            operation: AuthOperationToken(value: t1))
        // Baseline: T1 credentials/owner/APIClient bearer are set.
        XCTAssertEqual(credentialsStore.load(serverId: serverID)?.accessToken, "bearer-T1")
        XCTAssertEqual(owners.ownerUserID(serverID: serverID), userT1)
        let baselineBearer = await apiClient.currentAccessToken()
        XCTAssertEqual(baselineBearer, "bearer-T1")

        // 2) Reconfigure the router: /logout parks; /login for T2 returns T2 state.
        let logoutEntry = DispatchSemaphore(value: 0)
        let logoutRelease = DispatchSemaphore(value: 0)
        let userT2 = UUID()
        let loginT2JSON = """
        {"success":true,"token":"bearer-T2","expiresAt":null,\
        "user":\(userJSON(id: userT2))}
        """
        mockAPIClient.reset() // clear captured baseline
        mockAPIClient.requestHandler = { req in
            let path = req.url?.path ?? ""
            if path.hasSuffix("/api/auth/logout") {
                logoutEntry.signal()
                _ = logoutRelease.wait(timeout: .now() + .seconds(10))
                return (TestData.httpResponse(url: req.url, statusCode: 200), Data("{}".utf8))
            }
            if path.hasSuffix("/api/auth/login") {
                return (TestData.httpResponse(url: req.url, statusCode: 200), Data(loginT2JSON.utf8))
            }
            return (TestData.httpResponse(url: req.url, statusCode: 404), Data("{}".utf8))
        }

        // 3) Start the STALE T1 logout — it parks on /logout.
        let logoutTask = Task { await self.authService.logout(operation: AuthOperationToken(value: t1)) }
        await withCheckedContinuation { (cont: CheckedContinuation<Void, Never>) in
            DispatchQueue.global().async {
                _ = logoutEntry.wait(timeout: .now() + .seconds(10))
                cont.resume()
            }
        }

        // 4) During the park: a T2 login runs and completes (actor re-entrancy).
        let t2 = epoch.advance()
        _ = try await authService.login(
            serverURL: "https://a.example.com", username: "u", password: "p",
            operation: AuthOperationToken(value: t2))
        // T2 state is durably established.
        XCTAssertEqual(credentialsStore.load(serverId: serverID)?.accessToken, "bearer-T2")
        XCTAssertEqual(owners.ownerUserID(serverID: serverID), userT2)
        let midBearer = await apiClient.currentAccessToken()
        XCTAssertEqual(midBearer, "bearer-T2")

        // 5) Release the T1 logout. Its fencedMutation(T1) must no-op AND its API
        //    clear (CAS on T1) must not clobber T2's bearer.
        logoutRelease.signal()
        await logoutTask.value

        // 6) T2 state MUST survive the stale logout.
        XCTAssertEqual(credentialsStore.load(serverId: serverID)?.accessToken, "bearer-T2",
                       "stale T1 logout MUST NOT clear T2's credentials")
        XCTAssertEqual(owners.ownerUserID(serverID: serverID), userT2,
                       "stale T1 logout MUST NOT clear T2's owner")
        let finalBearer = await apiClient.currentAccessToken()
        XCTAssertEqual(finalBearer, "bearer-T2",
                       "stale T1 logout MUST NOT clear T2's APIClient bearer")

        // 7) The captured `/logout` request MUST carry T1's bearer, not T2's —
        //    proving the network call went through the immutable session snapshot
        //    captured BEFORE the T2 login mutated the shared APIClient.
        let logoutRequest = mockAPIClient.capturedRequests.first { ($0.url?.path ?? "").hasSuffix("/api/auth/logout") }
        XCTAssertNotNil(logoutRequest, "expected the /logout request to be captured")
        let auth = logoutRequest?.value(forHTTPHeaderField: "Authorization") ?? ""
        XCTAssertEqual(auth, "Bearer bearer-T1",
                       "the stale logout's network call MUST carry T1's original bearer, not T2's")
    }
}
