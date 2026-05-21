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
        isLoading = true
        if let user = await services.authService.restoreSession() {
            currentUser = user
            isAuthenticated = true
        }
        isLoading = false
        hasCheckedAuth = true
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
        } catch let error as NetworkError {
            errorMessage = friendlyMessage(for: error)
        } catch {
            errorMessage = error.localizedDescription
        }

        isLoading = false
    }

    func logout() async {
        await services.authService.logout()
        isAuthenticated = false
        currentUser = nil
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
