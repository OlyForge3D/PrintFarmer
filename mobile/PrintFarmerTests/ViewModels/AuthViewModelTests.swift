import XCTest
import Observation
@testable import PrintFarmer

/// AuthViewModel tests. AuthViewModel depends on the concrete AuthService actor,
/// so we use MockURLProtocol to intercept network calls at the URLSession layer.
/// This validates the full AuthViewModel → AuthService → APIClient path.
@MainActor
final class AuthViewModelTests: XCTestCase {

    nonisolated(unsafe) private var mockAPIClient: MockAPIClient!
    private var apiClient: APIClient!
    private var authService: AuthService!
    private var services: ServiceContainer!
    private var viewModel: AuthViewModel!

    override func setUp() {
        super.setUp()
        mockAPIClient = MockAPIClient()
        apiClient = mockAPIClient.apiClient
        authService = AuthService(apiClient: apiClient)
        services = ServiceContainer()
        services.authService = authService
        viewModel = AuthViewModel(services: services)
    }

    override func tearDown() {
        UserDefaults.standard.removeObject(forKey: APIClient.serverURLKey)
        viewModel = nil
        services = nil
        authService = nil
        apiClient = nil
        mockAPIClient = nil
        super.tearDown()
    }

    // MARK: - Initial State

    func testInitialState() {
        XCTAssertFalse(viewModel.isAuthenticated)
        XCTAssertNil(viewModel.currentUser)
        XCTAssertFalse(viewModel.isLoading)
        XCTAssertNil(viewModel.errorMessage)
    }

    // MARK: - Login Success

    func testLoginSuccessSetsAuthenticated() async {
        mockAPIClient.stubResponse(json: TestJSON.authResponseSuccess)

        await viewModel.login(serverURL: "https://print.example.com", username: "admin", password: "password")

        XCTAssertTrue(viewModel.isAuthenticated)
        XCTAssertNotNil(viewModel.currentUser)
        XCTAssertEqual(viewModel.currentUser?.username, "admin")
        XCTAssertNil(viewModel.errorMessage)
        XCTAssertFalse(viewModel.isLoading)
    }

    // MARK: - Login Errors

    func testLoginUnauthorizedSetsMessage() async {
        mockAPIClient.stubResponse(json: "{}", statusCode: 401)

        await viewModel.login(serverURL: "https://print.example.com", username: "admin", password: "wrong")

        XCTAssertFalse(viewModel.isAuthenticated)
        XCTAssertNotNil(viewModel.errorMessage)
        XCTAssertFalse(viewModel.isLoading)
    }

    func testLoginForbiddenSetsMessage() async {
        mockAPIClient.stubResponse(json: "{}", statusCode: 403)

        await viewModel.login(serverURL: "https://print.example.com", username: "admin", password: "password")

        XCTAssertFalse(viewModel.isAuthenticated)
        XCTAssertNotNil(viewModel.errorMessage)
        XCTAssertTrue(viewModel.errorMessage?.contains("access") ?? false)
    }

    func testLoginServerErrorSetsMessage() async {
        mockAPIClient.stubResponse(json: "{}", statusCode: 500)

        await viewModel.login(serverURL: "https://print.example.com", username: "admin", password: "password")

        XCTAssertFalse(viewModel.isAuthenticated)
        XCTAssertNotNil(viewModel.errorMessage)
        XCTAssertFalse(viewModel.isLoading)
    }

    func testLoginNetworkErrorSetsMessage() async {
        mockAPIClient.stubError(.notConnectedToInternet)

        await viewModel.login(serverURL: "https://print.example.com", username: "admin", password: "password")

        XCTAssertFalse(viewModel.isAuthenticated)
        XCTAssertNotNil(viewModel.errorMessage)
    }

    func testLoginPrivateHTTPSConnectFailureShowsTransportErrorDetails() async {
        mockAPIClient = MockAPIClient(baseURL: URL(string: "https://10.0.0.20")!)
        apiClient = mockAPIClient.apiClient
        authService = AuthService(apiClient: apiClient)
        services.authService = authService
        viewModel = AuthViewModel(services: services)
        mockAPIClient.stubError(.cannotConnectToHost)

        await viewModel.login(serverURL: "https://10.0.0.20", username: "admin", password: "password")

        XCTAssertFalse(viewModel.isAuthenticated)
        XCTAssertNotNil(viewModel.errorMessage)
        XCTAssertTrue(viewModel.errorMessage?.contains("Network error") == true)
        XCTAssertFalse(viewModel.errorMessage?.contains("Check the URL and try again.") == true)
    }

    func testLoginPrivateHTTPSConnectionRefusedShowsPreTLSHint() async {
        mockAPIClient = MockAPIClient(baseURL: URL(string: "https://10.0.0.20")!)
        apiClient = mockAPIClient.apiClient
        authService = AuthService(apiClient: apiClient)
        services.authService = authService
        viewModel = AuthViewModel(services: services)
        mockAPIClient.requestHandler = { _ in
            throw URLError(
                .cannotConnectToHost,
                userInfo: ["_kCFStreamErrorCodeKey": 61]
            )
        }

        await viewModel.login(serverURL: "https://10.0.0.20", username: "admin", password: "password")

        XCTAssertFalse(viewModel.isAuthenticated)
        XCTAssertNotNil(viewModel.errorMessage)
        XCTAssertTrue(viewModel.errorMessage?.contains("Connection refused") == true)
        XCTAssertTrue(viewModel.errorMessage?.contains("before the TLS handshake started") == true)
        XCTAssertTrue(viewModel.errorMessage?.contains("no trust challenge observed for 10.0.0.20") == true)
    }

    func testLoginPrivateHTTPSCertificateUsageErrorShowsTrustHint() async {
        mockAPIClient = MockAPIClient(baseURL: URL(string: "https://10.0.0.20")!)
        apiClient = mockAPIClient.apiClient
        authService = AuthService(apiClient: apiClient)
        services.authService = authService
        viewModel = AuthViewModel(services: services)
        mockAPIClient.requestHandler = { _ in
            TLSDiagnostics.recordChallenge(
                host: "10.0.0.20",
                authenticationMethod: NSURLAuthenticationMethodServerTrust,
                disposition: "cancelAuthenticationChallenge",
                trustError: "\"PrintFarmer\" certificate is not permitted for this usage",
                certificateWarning: "leaf cert has CA:TRUE; leaf cert missing serverAuth EKU"
            )
            throw URLError(.cancelled)
        }

        await viewModel.login(serverURL: "https://10.0.0.20", username: "admin", password: "password")

        XCTAssertFalse(viewModel.isAuthenticated)
        XCTAssertNotNil(viewModel.errorMessage)
        XCTAssertTrue(viewModel.errorMessage?.contains("CA certificate") == true)
        XCTAssertTrue(viewModel.errorMessage?.contains("serverAuth") == true)
        XCTAssertTrue(viewModel.errorMessage?.contains("certificate is not permitted for this usage") == true)
    }

    func testLoginPrivateHTTPSMissingIntermediateShowsTrustHint() async {
        mockAPIClient = MockAPIClient(baseURL: URL(string: "https://10.0.0.20")!)
        apiClient = mockAPIClient.apiClient
        authService = AuthService(apiClient: apiClient)
        services.authService = authService
        viewModel = AuthViewModel(services: services)
        mockAPIClient.requestHandler = { _ in
            TLSDiagnostics.recordChallenge(
                host: "10.0.0.20",
                authenticationMethod: NSURLAuthenticationMethodServerTrust,
                disposition: "cancelAuthenticationChallenge",
                trustError: "Trust evaluate failure: [leaf ExtendedKeyUsage MissingIntermediate]",
                certificateWarning: "leaf cert missing serverAuth EKU"
            )
            throw URLError(.cancelled)
        }

        await viewModel.login(serverURL: "https://10.0.0.20", username: "admin", password: "password")

        XCTAssertFalse(viewModel.isAuthenticated)
        XCTAssertNotNil(viewModel.errorMessage)
        XCTAssertTrue(viewModel.errorMessage?.contains("serverAuth") == true)
        XCTAssertTrue(viewModel.errorMessage?.contains("intermediate certificate") == true)
        XCTAssertTrue(viewModel.errorMessage?.contains("ExtendedKeyUsage MissingIntermediate") == true)
    }

    func testLoginClearsErrorOnSuccess() async {
        mockAPIClient.stubResponse(json: "{}", statusCode: 401)
        await viewModel.login(serverURL: "https://print.example.com", username: "admin", password: "wrong")
        XCTAssertNotNil(viewModel.errorMessage)

        mockAPIClient.stubResponse(json: TestJSON.authResponseSuccess)
        await viewModel.login(serverURL: "https://print.example.com", username: "admin", password: "password")

        XCTAssertNil(viewModel.errorMessage)
        XCTAssertTrue(viewModel.isAuthenticated)
    }

    // MARK: - Logout

    func testLogoutClearsState() async {
        mockAPIClient.stubResponse(json: TestJSON.authResponseSuccess)
        await viewModel.login(serverURL: "https://print.example.com", username: "admin", password: "password")
        XCTAssertTrue(viewModel.isAuthenticated)

        await viewModel.logout()

        XCTAssertFalse(viewModel.isAuthenticated)
        XCTAssertNil(viewModel.currentUser)
    }

    // MARK: - Session Restore

    func testRestoreSessionWithNoToken() async {
        await viewModel.restoreSession()

        XCTAssertFalse(viewModel.isAuthenticated)
        XCTAssertNil(viewModel.currentUser)
        XCTAssertFalse(viewModel.isLoading)
    }

    func testRestoreSessionCompletesLoadingCycle() async {
        mockAPIClient.stubResponse(json: TestJSON.userDTO)
        await viewModel.restoreSession()
        XCTAssertFalse(viewModel.isLoading)
    }

    // MARK: - Session Expired Notification

    func testSessionExpiredNotificationTriggersLogout() async {
        mockAPIClient.stubResponse(json: TestJSON.authResponseSuccess)
        await viewModel.login(serverURL: "https://print.example.com", username: "admin", password: "password")
        XCTAssertTrue(viewModel.isAuthenticated)

        // Deterministically observe the real AuthViewModel logout transition
        // instead of sleeping. Posting `.sessionExpired` drives the production
        // observer's async logout chain, which ultimately clears
        // `isAuthenticated` on the main actor. `withObservationTracking` fires
        // `onChange` causally on that exact @Observable mutation; the
        // expectation is fulfilled only once the state has actually become
        // false — i.e. downstream of, and caused by, `logout()` completing.
        // The wait timeout is a failure ceiling, never a success condition: if
        // the logout chain never clears state the expectation is never
        // fulfilled and the test fails. This mirrors the production observation
        // idiom in `ServiceContainer.observeActiveServer()` and contains no
        // `Task.sleep`, `Task.yield`, polling loop, retry, or elapsed-time
        // success condition.
        let loggedOut = XCTestExpectation(description: "session expiry drives AuthViewModel logout to completion")
        let observedViewModel = viewModel!
        withObservationTracking {
            _ = observedViewModel.isAuthenticated
        } onChange: {
            // `onChange` fires on the will-change edge, before the new value is
            // observable. Hop to the next main-actor step so the mutation is
            // fully applied, then latch success only on the real cleared state.
            Task { @MainActor in
                if observedViewModel.isAuthenticated == false {
                    loggedOut.fulfill()
                }
            }
        }

        NotificationCenter.default.post(name: .sessionExpired, object: nil)

        let result = await XCTWaiter.fulfillment(of: [loggedOut], timeout: 5)
        XCTAssertEqual(result, .completed, "session-expiry logout did not drive isAuthenticated to false")
        XCTAssertFalse(viewModel.isAuthenticated)
        XCTAssertNil(viewModel.currentUser)
    }
}

// MARK: - currentUserRole

extension AuthViewModelTests {

    private func makeUser(roles: [String]) -> UserDTO {
        UserDTO(
            id: UUID(),
            username: "test",
            email: "test@example.com",
            firstName: nil,
            lastName: nil,
            isActive: true,
            emailConfirmed: true,
            lastLogin: nil,
            createdAt: Date(),
            roles: roles,
            permissions: []
        )
    }

    func testCurrentUserRoleNilWhenNoUser() {
        XCTAssertNil(viewModel.currentUserRole)
    }

    func testCurrentUserRoleReturnsFarmAdminWhenPresent() {
        viewModel.currentUser = makeUser(roles: ["operator", "farm_admin"])
        XCTAssertEqual(viewModel.currentUserRole, "farm_admin")
    }

    func testCurrentUserRoleReturnsFirstRoleWhenNotAdmin() {
        viewModel.currentUser = makeUser(roles: ["operator"])
        XCTAssertEqual(viewModel.currentUserRole, "operator")
    }

    func testCurrentUserRoleNilForEmptyRoles() {
        viewModel.currentUser = makeUser(roles: [])
        XCTAssertNil(viewModel.currentUserRole)
    }

    // MARK: - Maintenance Toggle Gating (#274)

    /// Admin: currentUserRole == "farm_admin" -> the maintenance toggle is shown.
    func testMaintenanceToggleVisibleForAdmin() {
        viewModel.currentUser = makeUser(roles: ["farm_admin"])
        XCTAssertEqual(viewModel.currentUserRole, "farm_admin",
            "Admin must have currentUserRole == farm_admin so the maintenance toggle is shown")
    }

    /// Multiple roles including farm_admin: should still gate as admin.
    func testMaintenanceToggleVisibleForMultiRoleWithAdmin() {
        viewModel.currentUser = makeUser(roles: ["operator", "farm_admin", "viewer"])
        XCTAssertEqual(viewModel.currentUserRole, "farm_admin",
            "User with multiple roles including farm_admin must see maintenance toggle")
    }

    /// Non-admin: currentUserRole != "farm_admin" -> the maintenance toggle is hidden.
    func testMaintenanceToggleHiddenForNonAdmin() {
        viewModel.currentUser = makeUser(roles: ["operator"])
        XCTAssertNotEqual(viewModel.currentUserRole, "farm_admin",
            "Non-admin must not have currentUserRole == farm_admin so the maintenance toggle is hidden")
    }

    /// Case sensitivity: backend always sends lowercase "farm_admin".
    func testMaintenanceToggleCaseSensitive() {
        viewModel.currentUser = makeUser(roles: ["Farm_Admin"])
        XCTAssertNotEqual(viewModel.currentUserRole, "farm_admin",
            "Role matching is case-sensitive — backend normalizes to lowercase")
    }

    /// Unauthenticated (nil user, not just empty roles): maintenance toggle hidden.
    func testMaintenanceToggleHiddenWhenUnauthenticated() {
        viewModel.currentUser = nil
        XCTAssertNil(viewModel.currentUserRole,
            "Unauthenticated user must have nil currentUserRole so the maintenance toggle is hidden")
    }

}
