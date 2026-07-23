import XCTest
import os
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
    ///
    /// I (issue #816): all rendezvous uses AsyncBarrier — no DispatchSemaphore
    /// timeouts. Unconditional close/drain registered BEFORE any throwing op.
    func testT1LoginParkedThenEpochAdvancesDoesNotPersistT1State() async throws {
        let t1 = epoch.advance()
        XCTAssertEqual(t1, 1)

        let userT1 = UUID()
        let loginJSON = """
        {"success":true,"token":"bearer-T1","expiresAt":null,\
        "user":\(userJSON(id: userT1))}
        """

        let networkBarrier = AsyncBarrier()
        addTeardownBlock { networkBarrier.close() }
        mockAPIClient.asyncRequestHandler = { [weak networkBarrier] req in
            let path = req.url?.path ?? ""
            if path.hasSuffix("/api/auth/login") {
                await networkBarrier?.arriveAndWait()
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
        addTeardownBlock { loginTask.cancel(); _ = try? await loginTask.value }
        await networkBarrier.waitUntilArrived()

        let t2 = epoch.advance()
        XCTAssertEqual(t2, 2)
        networkBarrier.release()

        let outcome = try await loginTask.value
        if case .superseded = outcome {} else {
            XCTFail("expected .superseded, got \(outcome)")
        }
        XCTAssertNil(credentialsStore.load(serverId: serverID),
                     "a superseded login MUST NOT persist its credentials")
        XCTAssertNil(owners.ownerUserID(serverID: serverID),
                     "a superseded login MUST NOT persist its owner")
    }

    /// J: A stale logout(operation: T1) spanning a concurrent T2 login MUST use a
    /// bearer-T1 session snapshot for its `/logout` network request AND MUST NOT
    /// clear T2's credentials/owner/API bearer. Proves the sessionSnapshotClient
    /// (immutable) is what carries the network call and fencedMutation blocks the
    /// local mutations.
    ///
    /// I hardening: rendezvous uses AsyncBarrier; unconditional close+drain
    /// registered BEFORE any throwing operation. J-strengthened assertion:
    /// checks actual "bearer-T1" content of the Authorization header rather than
    /// the redacted "******" placeholder that only proves header presence.
    func testStaleLogoutT1SpanningT2LoginPreservesT2State() async throws {
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
        XCTAssertEqual(credentialsStore.load(serverId: serverID)?.accessToken, "bearer-T1")
        XCTAssertEqual(owners.ownerUserID(serverID: serverID), userT1)
        let baselineBearer = await apiClient.currentAccessToken()
        XCTAssertEqual(baselineBearer, "bearer-T1")

        let logoutBarrier = AsyncBarrier()
        addTeardownBlock { logoutBarrier.close() }
        let userT2 = UUID()
        let loginT2JSON = """
        {"success":true,"token":"bearer-T2","expiresAt":null,\
        "user":\(userJSON(id: userT2))}
        """
        mockAPIClient.reset()
        mockAPIClient.asyncRequestHandler = { [weak logoutBarrier] req in
            let path = req.url?.path ?? ""
            if path.hasSuffix("/api/auth/logout") {
                await logoutBarrier?.arriveAndWait()
                return (TestData.httpResponse(url: req.url, statusCode: 200), Data("{}".utf8))
            }
            if path.hasSuffix("/api/auth/login") {
                return (TestData.httpResponse(url: req.url, statusCode: 200), Data(loginT2JSON.utf8))
            }
            return (TestData.httpResponse(url: req.url, statusCode: 404), Data("{}".utf8))
        }

        let logoutTask = Task { await self.authService.logout(operation: AuthOperationToken(value: t1)) }
        addTeardownBlock { logoutTask.cancel(); _ = await logoutTask.value }
        await logoutBarrier.waitUntilArrived()

        let t2 = epoch.advance()
        _ = try await authService.login(
            serverURL: "https://a.example.com", username: "u", password: "p",
            operation: AuthOperationToken(value: t2))
        XCTAssertEqual(credentialsStore.load(serverId: serverID)?.accessToken, "bearer-T2")
        XCTAssertEqual(owners.ownerUserID(serverID: serverID), userT2)
        let midBearer = await apiClient.currentAccessToken()
        XCTAssertEqual(midBearer, "bearer-T2")

        logoutBarrier.release()
        await logoutTask.value

        XCTAssertEqual(credentialsStore.load(serverId: serverID)?.accessToken, "bearer-T2",
                       "stale T1 logout MUST NOT clear T2's credentials")
        XCTAssertEqual(owners.ownerUserID(serverID: serverID), userT2,
                       "stale T1 logout MUST NOT clear T2's owner")
        let finalBearer = await apiClient.currentAccessToken()
        XCTAssertEqual(finalBearer, "bearer-T2",
                       "stale T1 logout MUST NOT clear T2's APIClient bearer")

        let logoutRequest = mockAPIClient.capturedRequests.first { ($0.url?.path ?? "").hasSuffix("/api/auth/logout") }
        XCTAssertNotNil(logoutRequest, "expected the /logout request to be captured")
        let auth = logoutRequest?.value(forHTTPHeaderField: "Authorization") ?? ""
        XCTAssertTrue(auth.contains("bearer-T1"),
                      "stale logout's network MUST carry T1 bearer; header=\(auth)")
        XCTAssertFalse(auth.contains("bearer-T2"),
                       "stale logout's network MUST NOT carry T2 bearer; header=\(auth)")
    }

    // MARK: - J tests (issue #816 reject): T1/T2 interleavings + stale-before-capture

    /// J: T1 login parks in /me identity verification → T2 advances epoch during
    /// the park → T1 resumes and the owner mutation MUST no-op (fencedMutation
    /// returns false), causing login to return .superseded rather than falling
    /// through to activate() and stamping the registry. Proves "Do not ignore
    /// owner mutation result" fix — a superseded login cannot stamp the registry
    /// active server under a superseded operation's identity.
    func testT1LoginParkedBetweenCurrentUserAndOwnerMutationReturnsSuperseded() async throws {
        let t1 = epoch.advance()
        let loginJSON = #"{"success":true,"token":"bearer-T1","user":null}"#

        let meBarrier = AsyncBarrier()
        addTeardownBlock { meBarrier.close() }
        let userT1 = UUID()
        let meJSON = userJSON(id: userT1)

        mockAPIClient.asyncRequestHandler = { [weak meBarrier] req in
            let path = req.url?.path ?? ""
            if path.hasSuffix("/api/auth/login") {
                return (TestData.httpResponse(url: req.url, statusCode: 200), Data(loginJSON.utf8))
            }
            if path.hasSuffix("/api/auth/me") {
                await meBarrier?.arriveAndWait()
                return (TestData.httpResponse(url: req.url, statusCode: 200), Data(meJSON.utf8))
            }
            return (TestData.httpResponse(url: req.url, statusCode: 404), Data("{}".utf8))
        }

        let loginTask = Task { () -> AuthLoginOutcome in
            try await self.authService.login(
                serverURL: "https://a.example.com", username: "u", password: "p",
                operation: AuthOperationToken(value: t1))
        }
        addTeardownBlock { loginTask.cancel(); _ = try? await loginTask.value }

        await meBarrier.waitUntilArrived()
        let t2 = epoch.advance()
        XCTAssertEqual(t2, 2)
        meBarrier.release()

        let outcome = try await loginTask.value
        if case .superseded = outcome {} else {
            XCTFail("expected .superseded when T2 advanced before owner mutation, got \(outcome)")
        }
        XCTAssertNil(owners.ownerUserID(serverID: serverID),
                     "T1 owner MUST NOT be persisted when superseded during /me verification")
    }

    /// J: a stale logout(T1) that runs AFTER the epoch has already advanced to
    /// T2 MUST NOT send /logout at all — sessionSnapshotClientIfCurrent returns
    /// nil under the epoch fence, so no network hop happens. Proves the reject's
    /// "capture exact session snapshot matching operation BEFORE activeServer/
    /// network awaits" + "network uses T1 snapshot client/bearer" clauses.
    func testStaleLogoutBeforeCaptureSkipsNetworkEntirely() async throws {
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

        let t2 = epoch.advance()
        XCTAssertEqual(t2, 2)

        let logoutHits = OSAllocatedUnfairLock(initialState: 0)
        mockAPIClient.reset()
        mockAPIClient.requestHandler = { req in
            let path = req.url?.path ?? ""
            if path.hasSuffix("/api/auth/logout") {
                logoutHits.withLock { $0 += 1 }
            }
            return (TestData.httpResponse(url: req.url, statusCode: 200), Data("{}".utf8))
        }

        await authService.logout(operation: AuthOperationToken(value: t1))

        let hits = logoutHits.withLock { $0 }
        XCTAssertEqual(hits, 0,
                       "a stale logout whose epoch has advanced past its token MUST NOT send /logout")
        XCTAssertEqual(credentialsStore.load(serverId: serverID)?.accessToken, "bearer-T1",
                       "stale logout MUST NOT clear credentials it isn't fenced to")
    }
}
