import Foundation

// MARK: - Auth Service Protocol

/// Contract for authentication operations.
protocol AuthServiceProtocol: Sendable {
    func login(serverURL: String, username: String, password: String) async throws -> AuthResponse
    func logout() async
    func restoreSession() async -> UserDTO?
    func currentUser() async throws -> UserDTO
    var isAuthenticated: Bool { get async }
}
