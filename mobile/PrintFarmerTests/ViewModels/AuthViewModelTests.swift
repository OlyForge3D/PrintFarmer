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

    private var priorDemoModeState: Bool = false

    override func setUp() async throws {
        try await super.setUp()
        // `DemoMode` is a process-global singleton backed by UserDefaults, and
        // `login()` branches on it. Without an explicit reset a leaked demo flag
        // makes these tests non-deterministic: `switchToReal()` would rebuild the
        // container and discard the injected `authService` below, silently
        // exercising real services instead of the mock.
        priorDemoModeState = DemoMode.shared.isActive
        DemoMode.shared.deactivate()
        mockAPIClient = MockAPIClient()
        apiClient = mockAPIClient.apiClient
        // These tests exercise the AuthViewModel → AuthService → APIClient auth path;
        // they do not test server switching, so the container does not observe the
        // registry (avoids cross-test churn from the shared registry).
        services = ServiceContainer(observeRegistry: false)
        authService = AuthService(apiClient: apiClient, authEpoch: services.authOperationEpoch)
        services.authService = authService
        viewModel = AuthViewModel(services: services)
    }

    override func tearDown() async throws {
        UserDefaults.standard.removeObject(forKey: APIClient.serverURLKey)
        DemoMode.shared.isActive = priorDemoModeState
        viewModel = nil
        services = nil
        authService = nil
        apiClient = nil
        mockAPIClient = nil
        try await super.tearDown()
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
            // Reproduce the CI interleaving: an unrelated container request starts
            // after this private-host request and must not replace its TLS diagnostics.
            TLSDiagnostics.beginRequest(host: "print.example.com")
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

        // Post a session-expiry event carrying the CURRENT auth-session identity so the
        // identity-scoped handler (issue #816 A) acts on it. Generation 0 is the fresh
        // container's active generation; the token is the current auth-operation epoch.
        NotificationCenter.default.post(
            name: .sessionExpired,
            object: nil,
            userInfo: ["generation": 0, "authSessionToken": services.authOperationEpoch.current]
        )

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

    // MARK: - H2 (Bishop): operation-owned VM state under interleaving

    /// Programmable auth service that captures its outcome BEFORE an optional barrier
    /// so a test can park a login mid-flight, run an interleaving operation, then
    /// release — deterministically (no sleeps/polling).
    private final class ProgrammableAuthService: AuthServiceProtocol, @unchecked Sendable {
        var loginBarrier: AsyncBarrier?
        var restoreBarrier: AsyncBarrier?
        var loginOutcome: () -> Result<AuthLoginOutcome, Error>
        let user: UserDTO
        private(set) var logoutCount = 0

        init(user: UserDTO) {
            self.user = user
            self.loginOutcome = {
                .success(.applied(AuthResponse(success: true, token: "t", expiresAt: nil, user: user, error: nil)))
            }
        }

        func login(serverURL: String, username: String, password: String, operation: AuthOperationToken) async throws -> AuthLoginOutcome {
            let captured = loginOutcome() // capture before suspending so a later reassignment cannot change this call
            if let loginBarrier { await loginBarrier.arriveAndWait() }
            switch captured {
            case .success(let outcome): return outcome
            case .failure(let error): throw error
            }
        }
        func logout(operation: AuthOperationToken) async { logoutCount += 1 }
        func restoreSession(operation: AuthOperationToken) async -> AuthRestoreOutcome {
            if let restoreBarrier { await restoreBarrier.arriveAndWait() }
            return .restored(user)
        }
        func currentUser() async throws -> UserDTO { user }
        var isAuthenticated: Bool { get async { true } }
    }

    func testRestoreSupersededByNewerLoginDoesNotClobber() async {
        let prog = ProgrammableAuthService(user: makeUser(roles: ["operator"]))
        services.authService = prog
        let restoreBarrier = AsyncBarrier()
        prog.restoreBarrier = restoreBarrier

        // A restore is in flight, parked at the network barrier.
        let restoreTask = Task { await self.viewModel.restoreSession() }
        await restoreBarrier.waitUntilArrived()

        // A newer login completes while the restore is parked.
        await viewModel.login(serverURL: "https://a.example.com", username: "u", password: "p")
        XCTAssertTrue(viewModel.isAuthenticated)
        let loginUser = viewModel.currentUser

        // Release the superseded restore: it must not clobber the newer login's state
        // or its loading flag.
        restoreBarrier.release()
        await restoreTask.value
        XCTAssertTrue(viewModel.isAuthenticated, "newer login survives a superseded restore")
        XCTAssertEqual(viewModel.currentUser?.id, loginUser?.id)
        XCTAssertFalse(viewModel.isLoading)
    }
    func testLoginSupersededByLogoutDoesNotClobberVMState() async {
        let prog = ProgrammableAuthService(user: makeUser(roles: ["operator"]))
        services.authService = prog
        let loginBarrier = AsyncBarrier()
        prog.loginBarrier = loginBarrier

        let loginTask = Task { await self.viewModel.login(serverURL: "https://a.example.com", username: "u", password: "p") }
        await loginBarrier.waitUntilArrived()
        XCTAssertTrue(viewModel.isLoading, "login is in flight")

        // Logout lands and supersedes the in-flight login.
        await viewModel.logout()
        // Release the superseded login: it must do nothing (never success-shaped).
        loginBarrier.release()
        await loginTask.value

        XCTAssertFalse(viewModel.isAuthenticated, "superseded login must not authenticate")
        XCTAssertNil(viewModel.currentUser, "superseded login must not set a user")
        XCTAssertNil(viewModel.errorMessage)
        XCTAssertFalse(viewModel.isLoading, "logout (the newer op) owns and clears the loading flag")
    }

    /// V5 (issue #816 reject, Vasquez): a SUPERSEDED restore must make ZERO view
    /// state changes — in particular it must NOT set `hasCheckedAuth`. Here the
    /// restore is superseded by a bare epoch advance (no other operation touches
    /// `hasCheckedAuth`), so the flag isolates the frozen bug: the frozen code set
    /// `hasCheckedAuth = true` in the superseded branch.
    func testSupersededRestoreDoesNotSetHasCheckedAuth() async {
        let prog = ProgrammableAuthService(user: makeUser(roles: ["operator"]))
        services.authService = prog
        let restoreBarrier = AsyncBarrier()
        prog.restoreBarrier = restoreBarrier

        XCTAssertFalse(viewModel.hasCheckedAuth, "precondition: not yet checked")

        // Restore in flight, parked at the network barrier (it advanced the epoch
        // to its own token when it started).
        let restoreTask = Task { await self.viewModel.restoreSession() }
        await restoreBarrier.waitUntilArrived()

        // Supersede the restore by advancing the epoch WITHOUT any other operation
        // that would legitimately set hasCheckedAuth.
        _ = services.authOperationEpoch.advance()

        restoreBarrier.release()
        await restoreTask.value

        XCTAssertFalse(viewModel.hasCheckedAuth,
                       "a superseded restore must NOT set hasCheckedAuth (V5)")
        XCTAssertFalse(viewModel.isAuthenticated, "superseded restore must not authenticate")
    }

    func testOperationOwnedLoadingFlagAcrossOverlappingLogins() async {
        let prog = ProgrammableAuthService(user: makeUser(roles: ["operator"]))
        services.authService = prog
        let b1 = AsyncBarrier(), b2 = AsyncBarrier()

        prog.loginBarrier = b1
        let login1 = Task { await self.viewModel.login(serverURL: "https://a.example.com", username: "u", password: "p") }
        await b1.waitUntilArrived()
        prog.loginBarrier = b2
        let login2 = Task { await self.viewModel.login(serverURL: "https://a.example.com", username: "u", password: "p") }
        await b2.waitUntilArrived()

        // Release the older login first: superseded, it must NOT clear the loading flag
        // that the newer login owns.
        b1.release()
        await login1.value
        XCTAssertTrue(viewModel.isLoading, "superseded login1 must not clear login2's loading flag")

        // Release the current login: it completes and clears its own loading flag.
        b2.release()
        await login2.value
        XCTAssertFalse(viewModel.isLoading)
        XCTAssertTrue(viewModel.isAuthenticated)
    }

    func testStaleLoginFailureAfterNewerSuccessDoesNotClobber() async {
        let prog = ProgrammableAuthService(user: makeUser(roles: ["operator"]))
        services.authService = prog
        let b1 = AsyncBarrier()
        prog.loginBarrier = b1
        prog.loginOutcome = { .failure(NetworkError.authFailed("stale boom")) }

        let login1 = Task { await self.viewModel.login(serverURL: "https://a.example.com", username: "u", password: "p") }
        await b1.waitUntilArrived()

        // A newer login starts and succeeds while login1 is parked.
        prog.loginBarrier = nil
        prog.loginOutcome = {
            .success(.applied(AuthResponse(success: true, token: "t", expiresAt: nil, user: prog.user, error: nil)))
        }
        await viewModel.login(serverURL: "https://a.example.com", username: "u", password: "p")
        XCTAssertTrue(viewModel.isAuthenticated)

        // Release login1's failure: being stale, it must NOT set errorMessage or clear
        // loading — the newer success stands.
        b1.release()
        await login1.value
        XCTAssertNil(viewModel.errorMessage, "stale failure must not clobber the newer success")
        XCTAssertTrue(viewModel.isAuthenticated, "newer success stands")
        XCTAssertFalse(viewModel.isLoading)
    }


    // MARK: - Demo Mode Teardown on Login (regression: PR #1065)

    /// Registering a real backend must leave demo mode, otherwise `DemoAuthService`
    /// accepts any credentials and strands the user in mock data.
    func testLoginExitsDemoModeWhenDemoIsActive() async {
        DemoMode.shared.activate()
        XCTAssertTrue(DemoMode.shared.isActive)

        await viewModel.login(serverURL: Self.unreachableURL, username: "u", password: "p")

        XCTAssertFalse(DemoMode.shared.isActive, "a real login attempt must exit demo mode")
    }

    /// The teardown happens BEFORE authentication, so it must also hold when the
    /// login fails - the user must land on the real (failed) path, not silently
    /// back in demo data.
    func testFailedLoginStillExitsDemoModeAndSurfacesError() async {
        DemoMode.shared.activate()

        await viewModel.login(serverURL: Self.unreachableURL, username: "u", password: "p")

        XCTAssertFalse(DemoMode.shared.isActive, "failed login must not leave the user in demo mode")
        XCTAssertFalse(viewModel.isAuthenticated, "a failed login must not authenticate")
        XCTAssertNotNil(viewModel.errorMessage, "the user must be told why login failed")
        XCTAssertFalse(viewModel.isLoading, "loading must clear so the user can retry")
    }

    /// Guards the inverse: the demo branch must not fire (and must not rebuild the
    /// container, discarding the injected mock authService) on a normal login.
    func testLoginLeavesDemoModeInactiveWhenAlreadyInactive() async {
        XCTAssertFalse(DemoMode.shared.isActive)
        mockAPIClient.stubResponse(json: TestJSON.authResponseSuccess)

        await viewModel.login(serverURL: "http://localhost:5245", username: "u", password: "p")

        XCTAssertFalse(DemoMode.shared.isActive)
    }

    /// Loopback port 1 refuses instantly. These two tests deliberately bypass
    /// `MockURLProtocol`: `switchToReal()` rebuilds the container with a real
    /// `URLSession`, so the injected mock is gone by the time the request is made.
    /// A refused loopback connection keeps that path fast and hermetic.
    private static let unreachableURL = "http://127.0.0.1:1"
}
