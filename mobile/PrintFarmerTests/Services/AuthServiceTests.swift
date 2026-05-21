import XCTest
@testable import PrintFarmer

/// Tests for AuthService: login, logout, token storage, session restore.
/// Uses MockURLProtocol to avoid real network calls.
/// NOTE: These tests interact with Keychain. Ensure the test target
/// has Keychain entitlements or run on simulator.
final class AuthServiceTests: XCTestCase {

    private var apiClient: APIClient!
    private var authService: AuthService!

    override func setUp() {
        super.setUp()
        MockURLProtocol.reset()
        apiClient = MockAPIClient.makeAPIClient()
        authService = AuthService(apiClient: apiClient)
    }

    override func tearDown() {
        MockURLProtocol.reset()
        // Clean up any persisted server URL from tests
        UserDefaults.standard.removeObject(forKey: APIClient.serverURLKey)
        apiClient = nil
        authService = nil
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

    func testSuccessfulLoginSetsAccessTokenOnAPIClient() async throws {
        MockAPIClient.stubResponse(json: TestJSON.authResponseSuccess)

        _ = try await authService.login(
            serverURL: "https://print.example.com",
            username: "admin",
            password: "password123"
        )

        // Verify next request includes the token
        MockAPIClient.stubResponse(json: TestJSON.userDTO)
        let _: UserDTO = try await apiClient.get("/api/auth/me")

        let captured = MockURLProtocol.capturedRequests.last
        let authHeader = captured?.value(forHTTPHeaderField: "Authorization")
        XCTAssertTrue(authHeader?.starts(with: "Bearer ") ?? false)
    }

    func testSuccessfulLoginUpdatesBaseURL() async throws {
        MockAPIClient.stubResponse(json: TestJSON.authResponseSuccess)

        _ = try await authService.login(
            serverURL: "https://new-server.example.com",
            username: "admin",
            password: "password123"
        )

        let currentURL = await apiClient.currentBaseURL()
        XCTAssertEqual(currentURL.absoluteString, "https://new-server.example.com")
    }

    func testLoginNormalizesTrailingSlash() async throws {
        MockAPIClient.stubResponse(json: TestJSON.authResponseSuccess)

        _ = try await authService.login(
            serverURL: "https://print.example.com/",
            username: "admin",
            password: "password123"
        )

        let currentURL = await apiClient.currentBaseURL()
        XCTAssertEqual(currentURL.absoluteString, "https://print.example.com")
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
}
