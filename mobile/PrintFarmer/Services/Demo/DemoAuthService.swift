import Foundation

// MARK: - Demo Auth Service

final class DemoAuthService: AuthServiceProtocol, @unchecked Sendable {
    private var _isAuthenticated = false

    func login(serverURL: String, username: String, password: String) async throws -> AuthResponse {
        _isAuthenticated = true
        return DemoData.demoAuthResponse
    }

    func logout() async {
        _isAuthenticated = false
    }

    func restoreSession() async -> UserDTO? {
        let isActive = await MainActor.run { DemoMode.shared.isActive }
        if isActive {
            _isAuthenticated = true
            return DemoData.demoUser
        }
        return nil
    }

    func currentUser() async throws -> UserDTO {
        DemoData.demoUser
    }

    var isAuthenticated: Bool { _isAuthenticated }
}
