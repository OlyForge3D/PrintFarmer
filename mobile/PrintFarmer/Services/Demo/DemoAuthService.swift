import Foundation

// MARK: - Demo Auth Service

final class DemoAuthService: AuthServiceProtocol, @unchecked Sendable {
    private var _isAuthenticated = false

    func login(serverURL: String, username: String, password: String, operation: AuthOperationToken) async throws -> AuthLoginOutcome {
        _isAuthenticated = true
        return .applied(DemoData.demoAuthResponse)
    }

    func logout(operation: AuthOperationToken) async {
        _isAuthenticated = false
    }

    func restoreSession(operation: AuthOperationToken) async -> AuthRestoreOutcome {
        let isActive = await MainActor.run { DemoMode.shared.isActive }
        if isActive {
            _isAuthenticated = true
            return .restored(DemoData.demoUser)
        }
        return .noSession
    }

    func currentUser() async throws -> UserDTO {
        DemoData.demoUser
    }

    var isAuthenticated: Bool { _isAuthenticated }
}
