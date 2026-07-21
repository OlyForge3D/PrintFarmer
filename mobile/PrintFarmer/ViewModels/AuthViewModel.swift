import Foundation
import Observation

/// App-level authentication state that controls whether the user sees
/// LoginView or the main TabView. Injected via @Environment.
@MainActor @Observable
final class AuthViewModel {
    var isAuthenticated = false
    var currentUser: UserDTO?
    var isLoading = false
    var errorMessage: String?

    /// The current user's primary role for permission gating.
    /// Returns "farm_admin" when the user has that role; otherwise the first role; nil if unauthenticated.
    var currentUserRole: String? {
        guard let roles = currentUser?.roles else { return nil }
        if roles.contains("farm_admin") { return "farm_admin" }
        return roles.first
    }
    /// True once the initial session restore check has completed.
    private(set) var hasCheckedAuth = false

    private let services: ServiceContainer
    @ObservationIgnored private var sessionExpiredObserver: NSObjectProtocol?

    init(services: ServiceContainer) {
        self.services = services
        sessionExpiredObserver = NotificationCenter.default.addObserver(
            forName: .sessionExpired,
            object: nil,
            queue: .main
        ) { [weak self] _ in
            guard let self else { return }
            Task { @MainActor in
                await self.logout()
            }
        }
    }

    // MARK: - Session Restoration

    func restoreSession() async {
        // Idempotence guard: if this view model was pre-authenticated by
        // `UITestBootstrap` (via `markAuthenticatedForUITesting(user:)`)
        // there is no session to restore. Re-invoking the demo auth
        // service here would send an unnecessary round-trip and — in
        // future auth-service implementations — could reset session
        // state we already established. Short-circuiting keeps this
        // method safe to call multiple times.
        if hasCheckedAuth && isAuthenticated {
            return
        }

        isLoading = true
        if let user = await services.authService.restoreSession() {
            currentUser = user
            isAuthenticated = true
            await services.activateFarmSnapshotForActiveServer()
        }
        isLoading = false
        hasCheckedAuth = true
    }

    // MARK: - UI Test Bootstrap

    /// Marks the view model as authenticated without hitting the auth
    /// service. Used exclusively by `UITestBootstrap` (guarded by the
    /// `--uitesting` launch argument) to render `ContentView` on a fresh
    /// simulator. Does not modify `DemoMode.shared`, so production auth
    /// remains untouched on normal launches.
    func markAuthenticatedForUITesting(user: UserDTO) {
        currentUser = user
        isAuthenticated = true
        hasCheckedAuth = true
        isLoading = false
        errorMessage = nil
    }

    // MARK: - Login / Logout

    func login(serverURL: String, username: String, password: String) async {
        isLoading = true
        errorMessage = nil

        do {
            let response = try await services.authService.login(
                serverURL: serverURL,
                username: username,
                password: password
            )
            currentUser = response.user
            isAuthenticated = true
            await services.activateFarmSnapshotForActiveServer()
        } catch let error as NetworkError {
            errorMessage = friendlyMessage(for: error)
        } catch {
            errorMessage = error.localizedDescription
        }

        isLoading = false
    }

    func logout() async {
        // Revoke snapshot authority (synchronous) and await store deactivation
        // before the auth service tears down the session.
        await services.revokeFarmSnapshot()
        await services.authService.logout()
        isAuthenticated = false
        currentUser = nil
    }

    func logoutIfServerRegistryUnavailable(_ registry: ServerRegistry) async {
        guard isAuthenticated,
              registry.servers.isEmpty || registry.activeServerID == nil else {
            return
        }

        await logout()
    }

    // MARK: - Demo Mode

    func loginAsDemo() {
        DemoMode.shared.activate()
        services.switchToDemo()
        currentUser = DemoData.demoUser
        isAuthenticated = true
    }

    func exitDemoMode() async {
        DemoMode.shared.deactivate()
        services.switchToReal()
        await logout()
    }

    // MARK: - Helpers

    private func friendlyMessage(for error: NetworkError) -> String {
        switch error {
        case .unauthorized:
            "Invalid username or password."
        case .forbidden:
            "Your account does not have access."
        case .serverError:
            "The server encountered an error. Please try again."
        case .invalidURL:
            "Could not reach the server. Check the URL."
        case .noConnection:
            "No internet connection. Check your network."
        case .serverUnreachable:
            "Could not reach the server. Check the URL and try again."
        case .authFailed(let message):
            message
        default:
            error.errorDescription ?? "An unexpected error occurred."
        }
    }
}
