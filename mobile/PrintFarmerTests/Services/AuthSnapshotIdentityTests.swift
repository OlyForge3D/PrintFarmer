import XCTest
@testable import PrintFarmer

/// H2 remediation proofs (issue #816): verified identity + auth-operation epoch.
/// Assertions target the non-secret owner store / view-state — never the Keychain
/// — so they are independent of this host's Keychain environment.
@MainActor
final class AuthSnapshotIdentityTests: XCTestCase {

    nonisolated(unsafe) private var mockAPIClient: MockAPIClient!
    private var apiClient: APIClient!
    private var registry: ServerRegistry!
    private var owners: FarmSnapshotOwnerStore!
    private var epoch: AuthOperationEpoch!
    private var authService: AuthService!
    private var serverID: UUID!

    override func setUp() {
        super.setUp()
        mockAPIClient = MockAPIClient()
        apiClient = mockAPIClient.apiClient
        registry = ServerRegistry(userDefaults: UserDefaults(suiteName: "reg-\(UUID().uuidString)")!, migrateLegacyServerURL: false)
        let server = try! registry.add(displayName: "A", baseURL: URL(string: "https://a.example.com")!)
        try! registry.setActive(id: server.id)
        serverID = server.id
        owners = FarmSnapshotOwnerStore(userDefaults: UserDefaults(suiteName: "own-\(UUID().uuidString)")!)
        epoch = AuthOperationEpoch()
        authService = AuthService(
            apiClient: apiClient,
            credentialsStore: ServerCredentialsStore(keychain: .init(keyPrefix: "AuthId_\(UUID().uuidString)_")),
            userDefaultsBox: AuthServiceUserDefaultsBox(UserDefaults(suiteName: "auth-\(UUID().uuidString)")!),
            migrateLegacyServerURL: false,
            serverRegistry: registry,
            snapshotOwnerStore: owners,
            authEpoch: epoch
        )
    }

    override func tearDown() {
        mockAPIClient = nil; apiClient = nil; registry = nil
        owners = nil; epoch = nil; authService = nil; serverID = nil
        super.tearDown()
    }

    private func userJSON(id: UUID, username: String = "user") -> String {
        """
        {"id":"\(id.uuidString)","username":"\(username)","email":"u@e.com","firstName":null,
         "lastName":null,"isActive":true,"emailConfirmed":true,"lastLogin":null,
         "createdAt":"2026-01-01T00:00:00Z","roles":["farm_admin"],"permissions":[]}
        """
    }

    // MARK: AuthOperationEpoch

    func testAuthOperationEpochIsMonotonic() {
        let e = AuthOperationEpoch()
        let a = e.advance()
        let b = e.advance()
        XCTAssertGreaterThan(b, a)
        XCTAssertTrue(e.isCurrent(b))
        XCTAssertFalse(e.isCurrent(a))
    }

    // MARK: Token-only verified identity

    func testTokenOnlyLoginWithUnverifiableIdentityClearsStalePriorOwner() async throws {
        // A prior owner exists for this server.
        let priorUser = UUID()
        owners.setOwner(userID: priorUser, serverID: serverID)

        // Token-only login response, and /api/auth/me is unauthorized (unverifiable).
        mockAPIClient.stubResponses([
            "/api/auth/login": (200, #"{"success":true,"token":"tok","user":null}"#),
            "/api/auth/me": (401, "{}")
        ])

        _ = try await authService.login(serverURL: "https://a.example.com", username: "u", password: "p")

        // Fail closed: the stale prior owner must NOT survive a token-only login.
        XCTAssertNil(owners.ownerUserID(serverID: serverID))
    }

    func testTokenOnlyLoginVerifiesOwnerViaCurrentUser() async throws {
        owners.setOwner(userID: UUID(), serverID: serverID) // stale prior owner
        let verifiedID = UUID()
        mockAPIClient.stubResponses([
            "/api/auth/login": (200, #"{"success":true,"token":"tok","user":null}"#),
            "/api/auth/me": (200, userJSON(id: verifiedID))
        ])

        _ = try await authService.login(serverURL: "https://a.example.com", username: "u", password: "p")

        // The owner is the freshly-verified identity, not the stale prior owner.
        XCTAssertEqual(owners.ownerUserID(serverID: serverID), verifiedID)
    }

    func testLoginWithUserInResponsePersistsThatOwner() async throws {
        let responseUser = UUID()
        let loginJSON = #"{"success":true,"token":"tok","user":\#(userJSON(id: responseUser))}"#
        mockAPIClient.stubResponses(["/api/auth/login": (200, loginJSON)])

        _ = try await authService.login(serverURL: "https://a.example.com", username: "u", password: "p")

        XCTAssertEqual(owners.ownerUserID(serverID: serverID), responseUser)
    }

    // MARK: Restore-after-logout view-state gating (barrier auth service)

    func testRestoreSupersededByLogoutDoesNotAuthenticate() async {
        let services = ServiceContainer()
        let barrier = AsyncBarrier()
        let user = UserDTO(
            id: UUID(), username: "u", email: "u@e.com", firstName: nil, lastName: nil,
            isActive: true, emailConfirmed: true, lastLogin: nil, createdAt: Date(),
            roles: ["farm_admin"], permissions: []
        )
        let barrierAuth = BarrierAuthService(restoreBarrier: barrier, restoreUser: user)
        services.authService = barrierAuth
        let vm = AuthViewModel(services: services)

        let task = Task { @MainActor in await vm.restoreSession() }
        await barrier.waitUntilArrived()
        // A logout supersedes the in-flight restore.
        await vm.logout()
        barrier.release()
        await task.value

        XCTAssertFalse(vm.isAuthenticated)
        XCTAssertNil(vm.currentUser)
    }
}

/// Minimal auth service whose `restoreSession` suspends on a barrier so tests can
/// interleave a logout deterministically.
private final class BarrierAuthService: AuthServiceProtocol, @unchecked Sendable {
    private let restoreBarrier: AsyncBarrier
    private let restoreUser: UserDTO

    init(restoreBarrier: AsyncBarrier, restoreUser: UserDTO) {
        self.restoreBarrier = restoreBarrier
        self.restoreUser = restoreUser
    }

    func login(serverURL: String, username: String, password: String) async throws -> AuthResponse {
        AuthResponse(success: true, token: "t", expiresAt: nil, user: restoreUser, error: nil)
    }
    func logout() async {}
    func restoreSession() async -> UserDTO? {
        await restoreBarrier.arriveAndWait()
        return restoreUser
    }
    func currentUser() async throws -> UserDTO { restoreUser }
    var isAuthenticated: Bool { get async { true } }
}
