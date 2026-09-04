import Foundation

// MARK: - Demo Auth Service

final class DemoAuthService: AuthServiceProtocol, @unchecked Sendable {
    private var _isAuthenticated = false
    private let user: UserDTO

    init(user: UserDTO = DemoData.demoUser) {
        self.user = user
    }

    func login(serverURL: String, username: String, password: String, operation: AuthOperationToken) async throws -> AuthLoginOutcome {
        _isAuthenticated = true
        return .applied(
            AuthResponse(
                success: DemoData.demoAuthResponse.success,
                token: DemoData.demoAuthResponse.token,
                expiresAt: DemoData.demoAuthResponse.expiresAt,
                user: user,
                error: DemoData.demoAuthResponse.error
            )
        )
    }

    func logout(operation: AuthOperationToken) async {
        _isAuthenticated = false
    }

    func restoreSession(operation: AuthOperationToken) async -> AuthRestoreOutcome {
        let isActive = await MainActor.run { DemoMode.shared.isActive }
        if isActive {
            _isAuthenticated = true
            return .restored(user)
        }
        return .noSession
    }

    func currentUser() async throws -> UserDTO {
        user
    }

    var isAuthenticated: Bool { _isAuthenticated }
}
