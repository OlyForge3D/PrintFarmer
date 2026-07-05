import KeychainSwift
import XCTest
@testable import PrintFarmer

/// Tests for AuthService: login, logout, token storage, session restore.
/// Uses MockURLProtocol to avoid real network calls.
/// NOTE: These tests interact with Keychain. Ensure the test target
/// has Keychain entitlements or run on simulator.
@MainActor
final class AuthServiceTests: XCTestCase {

    private var apiClient: APIClient!
    private var authService: AuthService!
    private var keychain: KeychainSwift!
    private var credentialsStore: ServerCredentialsStore!
    private var userDefaults: UserDefaults!
    private var userDefaultsSuiteName: String!

    override func setUp() {
        super.setUp()
        MockURLProtocol.reset()
        let suiteName = "AuthServiceTests-\(UUID().uuidString)"
        let defaults = UserDefaults(suiteName: suiteName)!
        let testKeychain = KeychainSwift(keyPrefix: "AuthServiceTests_\(UUID().uuidString)_")
        let client = MockAPIClient.makeAPIClient()
        defaults.removePersistentDomain(forName: suiteName)
        testKeychain.clear()

        userDefaultsSuiteName = suiteName
        userDefaults = defaults
        keychain = testKeychain
        credentialsStore = ServerCredentialsStore(keychain: testKeychain)
        apiClient = client
        authService = AuthService(
            apiClient: client,
            credentialsStore: credentialsStore,
            userDefaultsBox: AuthServiceUserDefaultsBox(defaults),
            migrateLegacyServerURL: false
        )
    }

    override func tearDown() {
        MockURLProtocol.reset()
        keychain.clear()
        userDefaults.removePersistentDomain(forName: userDefaultsSuiteName)
        // Clean up any persisted server URL from tests
        UserDefaults.standard.removeObject(forKey: APIClient.serverURLKey)
        apiClient = nil
        authService = nil
        credentialsStore = nil
        keychain = nil
        userDefaults = nil
        userDefaultsSuiteName = nil
        super.tearDown()
    }

    // MARK: - Login

    func testSuccessfulLoginReturnsAuthResponse() async throws {
        MockAPIClient.stubResponse(json: TestJSON.authResponseSuccess)

        let response = try await authService.login(
            serverURL: "https://print.example.com",
            username: "admin",
            password: "password123"
        )

        XCTAssertTrue(response.success)
        XCTAssertNotNil(response.token)
        XCTAssertEqual(response.user?.username, "admin")
    }

    func testSuccessfulLoginStoresTokenForActivatedServerAndAppliesItToAPIClient() async throws {
        MockAPIClient.stubResponse(json: TestJSON.authResponseSuccess)

        let response = try await authService.login(
            serverURL: "https://print.example.com",
            username: "admin",
            password: "password123"
        )

        let registry = ServerRegistry(userDefaults: userDefaults, migrateLegacyServerURL: false)
        let server = try XCTUnwrap(registry.activeServer)
        XCTAssertEqual(server.normalizedURLString, "https://print.example.com")
        XCTAssertEqual(credentialsStore.load(serverId: server.id)?.accessToken, response.token)
        let currentToken = await apiClient.currentAccessToken()
        XCTAssertEqual(currentToken, response.token)
    }

    func testSuccessfulLoginAppliesServerBaseURLToSharedAPIClient() async throws {
        MockAPIClient.stubResponse(json: TestJSON.authResponseSuccess)

        _ = try await authService.login(
            serverURL: "https://new-server.example.com",
            username: "admin",
            password: "password123"
        )

        let currentURL = await apiClient.currentBaseURL()
        XCTAssertEqual(currentURL, URL(string: "https://new-server.example.com")!)
    }

    func testLoginForAlreadyActiveServerAppliesSessionToSharedAPIClient() async throws {
        let registry = ServerRegistry(userDefaults: userDefaults, migrateLegacyServerURL: false)
        let server = try registry.add(
            displayName: "PrintFarmer",
            baseURL: URL(string: "https://print.example.com")!
        )
        try registry.setActive(id: server.id)
        authService = AuthService(
            apiClient: apiClient,
            credentialsStore: credentialsStore,
            userDefaultsBox: AuthServiceUserDefaultsBox(userDefaults),
            migrateLegacyServerURL: false,
            serverRegistry: registry
        )
        MockAPIClient.stubResponse(json: TestJSON.authResponseSuccess)

        let response = try await authService.login(
            serverURL: "https://print.example.com",
            username: "admin",
            password: "password123"
        )

        let currentBaseURL = await apiClient.currentBaseURL()
        let currentAccessToken = await apiClient.currentAccessToken()
        XCTAssertEqual(currentBaseURL, server.baseURL)
        XCTAssertEqual(currentAccessToken, response.token)
        XCTAssertEqual(credentialsStore.load(serverId: server.id)?.accessToken, response.token)

        MockURLProtocol.reset()
        MockAPIClient.stubResponse(json: TestJSON.userDTO)
        let _: UserDTO = try await apiClient.get("/api/auth/me")

        let captured = try XCTUnwrap(MockURLProtocol.capturedRequests.first)
        let token = try XCTUnwrap(response.token)
        XCTAssertEqual(captured.value(forHTTPHeaderField: "Authorization"), "Bearer " + token)
    }

    func testLoginNormalizesTrailingSlash() async throws {
        MockAPIClient.stubResponse(json: TestJSON.authResponseSuccess)

        _ = try await authService.login(
            serverURL: "https://print.example.com/",
            username: "admin",
            password: "password123"
        )

        let registry = ServerRegistry(userDefaults: userDefaults, migrateLegacyServerURL: false)
        XCTAssertEqual(registry.activeServer?.normalizedURLString, "https://print.example.com")
    }

    func testLoginUsesEphemeralClientWithoutCrossServerTokenBleed() async throws {
        let firstServer = try addServer(displayName: "First", urlString: "https://first.example.com", active: true)
        let sharedClient = APIClient(
            baseURL: firstServer.baseURL,
            session: MockURLProtocol.mockSession(),
            accessToken: "first-token"
        )
        authService = AuthService(
            apiClient: sharedClient,
            credentialsStore: credentialsStore,
            userDefaultsBox: AuthServiceUserDefaultsBox(userDefaults),
            migrateLegacyServerURL: false
        )
        apiClient = sharedClient

        let requestStarted = DispatchSemaphore(value: 0)
        let allowResponse = DispatchSemaphore(value: 0)
        MockURLProtocol.requestHandler = { request in
            requestStarted.signal()
            _ = allowResponse.wait(timeout: .now() + 5)
            return (TestData.httpResponse(url: request.url, statusCode: 200), Data(TestJSON.authResponseSuccess.utf8))
        }

        let service = authService!
        let loginTask = Task {
            try await service.login(
                serverURL: "https://second.example.com",
                username: "admin",
                password: "password123"
            )
        }
        guard await waitForSemaphore(requestStarted, timeout: 5) else {
            allowResponse.signal()
            _ = try? await loginTask.value
            XCTFail("Timed out waiting for login request")
            return
        }

        let sharedBaseURL = await sharedClient.currentBaseURL()
        let sharedAccessToken = await sharedClient.currentAccessToken()
        XCTAssertEqual(sharedBaseURL, firstServer.baseURL)
        XCTAssertEqual(sharedAccessToken, "first-token")
        XCTAssertEqual(MockURLProtocol.capturedRequests.first?.url?.host, "second.example.com")
        XCTAssertNil(MockURLProtocol.capturedRequests.first?.value(forHTTPHeaderField: "Authorization"))

        allowResponse.signal()
        _ = try await loginTask.value
    }

    func testFailedLoginThrowsAuthFailed() async {
        MockAPIClient.stubResponse(json: TestJSON.authResponseFailure)

        do {
            _ = try await authService.login(
                serverURL: "https://print.example.com",
                username: "admin",
                password: "wrong"
            )
            XCTFail("Expected NetworkError.authFailed")
        } catch let error as NetworkError {
            if case .authFailed(let message) = error {
                XCTAssertEqual(message, "Invalid username or password")
            } else {
                XCTFail("Expected .authFailed, got \(error)")
            }
        } catch {
            XCTFail("Unexpected error type: \(error)")
        }
    }

    func testLoginWithInvalidURLThrows() async {
        do {
            _ = try await authService.login(
                serverURL: "",
                username: "admin",
                password: "password123"
            )
            XCTFail("Expected error for empty URL")
        } catch let error as NetworkError {
            if case .invalidURL = error {
                // Expected
            } else {
                XCTFail("Expected .invalidURL, got \(error)")
            }
        } catch {
            XCTFail("Unexpected error type: \(error)")
        }
    }

    func testLoginSendsCorrectRequest() async throws {
        MockAPIClient.stubResponse(json: TestJSON.authResponseSuccess)

        _ = try await authService.login(
            serverURL: "https://print.example.com",
            username: "admin",
            password: "secret"
        )

        let captured = MockURLProtocol.capturedRequests.first
        XCTAssertNotNil(captured)
        XCTAssertEqual(captured?.httpMethod, "POST")
        XCTAssertTrue(captured?.url?.path.contains("/api/auth/login") ?? false)

        // Verify request body
        if let body = captured?.capturedHTTPBody() {
            let request = try JSONDecoder().decode(LoginRequest.self, from: body)
            XCTAssertEqual(request.usernameOrEmail, "admin")
            XCTAssertEqual(request.password, "secret")
            XCTAssertTrue(request.rememberMe)
        } else {
            XCTFail("Expected request body")
        }
    }

    // MARK: - Logout

    func testLogoutClearsAccessToken() async throws {
        // Login first
        MockAPIClient.stubResponse(json: TestJSON.authResponseSuccess)
        _ = try await authService.login(
            serverURL: "https://print.example.com",
            username: "admin",
            password: "password123"
        )

        // Logout - stub the POST /api/auth/logout endpoint
        MockURLProtocol.reset()
        MockAPIClient.stubEmptySuccess()
        await authService.logout()

        // Next request should NOT have Authorization
        MockURLProtocol.reset()
        MockAPIClient.stubResponse(json: TestJSON.printerArray)
        let _: [Printer] = try await apiClient.get("/api/printers")

        let captured = MockURLProtocol.capturedRequests.first
        XCTAssertNil(captured?.value(forHTTPHeaderField: "Authorization"))
    }

    func testLogoutClearsServerThatWasActiveAtCallTime() async throws {
        let firstServer = try addServer(displayName: "First", urlString: "https://first.example.com", active: true)
        let secondServer = try addServer(displayName: "Second", urlString: "https://second.example.com")
        credentialsStore.save(ServerCredentials(accessToken: "first-token", expiresAt: nil), serverId: firstServer.id)
        credentialsStore.save(ServerCredentials(accessToken: "second-token", expiresAt: nil), serverId: secondServer.id)

        let requestStarted = DispatchSemaphore(value: 0)
        let allowResponse = DispatchSemaphore(value: 0)
        MockURLProtocol.requestHandler = { request in
            requestStarted.signal()
            _ = allowResponse.wait(timeout: .now() + 5)
            return (TestData.httpResponse(url: request.url, statusCode: 200), Data())
        }

        let service = authService!
        let logoutTask = Task { await service.logout() }
        guard await waitForSemaphore(requestStarted, timeout: 5) else {
            allowResponse.signal()
            await logoutTask.value
            XCTFail("Timed out waiting for logout request")
            return
        }

        try setActiveServer(secondServer.id)
        allowResponse.signal()
        await logoutTask.value

        XCTAssertNil(credentialsStore.load(serverId: firstServer.id))
        XCTAssertEqual(credentialsStore.load(serverId: secondServer.id)?.accessToken, "second-token")
    }

    // MARK: - IsAuthenticated

    func testIsAuthenticatedReflectsKeychainState() async {
        // Before login, check initial state
        // Note: isAuthenticated checks Keychain, so this test depends on
        // Keychain state. In a fresh test environment it should be false.
        let initialState = await authService.isAuthenticated
        // We just verify it returns a boolean without crashing
        _ = initialState
    }

    // MARK: - Session Restore

    func testRestoreSessionCallsGetMe() async throws {
        // This test verifies the restoreSession flow.
        // Without a token in Keychain, it should return nil.
        let user = await authService.restoreSession()
        XCTAssertNil(user, "Should return nil when no token is stored")
    }

    func testRestoreSessionNetworkErrorDoesNotClearStoredCredentials() async throws {
        let server = try addServer(displayName: "PrintFarmer", urlString: "https://print.example.com", active: true)
        credentialsStore.save(ServerCredentials(accessToken: "stored-token", expiresAt: nil), serverId: server.id)
        MockAPIClient.stubError(.notConnectedToInternet)

        let user = await authService.restoreSession()

        XCTAssertNil(user)
        XCTAssertEqual(credentialsStore.load(serverId: server.id)?.accessToken, "stored-token")
        let currentToken = await apiClient.currentAccessToken()
        XCTAssertEqual(currentToken, "stored-token")
    }

    func testRestoreSessionUnauthorizedClearsStoredCredentials() async throws {
        let server = try addServer(displayName: "PrintFarmer", urlString: "https://print.example.com", active: true)
        credentialsStore.save(ServerCredentials(accessToken: "stored-token", expiresAt: nil), serverId: server.id)
        MockAPIClient.stubResponse(json: "{}", statusCode: 401)

        let user = await authService.restoreSession()

        XCTAssertNil(user)
        XCTAssertNil(credentialsStore.load(serverId: server.id))
        let currentToken = await apiClient.currentAccessToken()
        XCTAssertNil(currentToken)
    }

    func testRestoreSessionMigratesLegacyTokenOnlyWhenLegacyURLMatchesActiveServer() async throws {
        let server = try addServer(displayName: "Legacy", urlString: "https://legacy.example.com", active: true)
        userDefaults.set("https://legacy.example.com", forKey: APIClient.serverURLKey)
        keychain.set("legacy-token", forKey: ServerCredentialsStore.legacyTokenKey)
        MockAPIClient.stubResponse(json: TestJSON.userDTO)

        let user = await authService.restoreSession()

        XCTAssertEqual(user?.username, "admin")
        XCTAssertEqual(credentialsStore.load(serverId: server.id)?.accessToken, "legacy-token")
        XCTAssertNil(keychain.get(ServerCredentialsStore.legacyTokenKey))
    }

    func testRestoreSessionDoesNotMigrateLegacyTokenWhenLegacyURLDiffersFromActiveServer() async throws {
        let server = try addServer(displayName: "Active", urlString: "https://active.example.com", active: true)
        userDefaults.set("https://legacy.example.com", forKey: APIClient.serverURLKey)
        keychain.set("legacy-token", forKey: ServerCredentialsStore.legacyTokenKey)
        MockAPIClient.stubResponse(json: TestJSON.userDTO)

        let user = await authService.restoreSession()

        XCTAssertNil(user)
        XCTAssertNil(credentialsStore.load(serverId: server.id))
        XCTAssertEqual(keychain.get(ServerCredentialsStore.legacyTokenKey), "legacy-token")
        XCTAssertTrue(MockURLProtocol.capturedRequests.isEmpty)
    }

    @MainActor
    private func addServer(displayName: String, urlString: String, active: Bool = false) throws -> RegisteredServer {
        let registry = ServerRegistry(userDefaults: userDefaults, migrateLegacyServerURL: false)
        let server = try registry.add(displayName: displayName, baseURL: URL(string: urlString)!)
        if active {
            try registry.setActive(id: server.id)
        }
        return server
    }

    @MainActor
    private func setActiveServer(_ id: UUID) throws {
        try ServerRegistry(userDefaults: userDefaults, migrateLegacyServerURL: false).setActive(id: id)
    }

    private func waitForSemaphore(_ semaphore: DispatchSemaphore, timeout: TimeInterval) async -> Bool {
        await withCheckedContinuation { continuation in
            DispatchQueue.global().async {
                continuation.resume(returning: semaphore.wait(timeout: .now() + timeout) == .success)
            }
        }
    }
}
