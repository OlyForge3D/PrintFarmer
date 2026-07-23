import Foundation

// MARK: - Auth Service Protocol

/// Opaque per-operation token minted once at the start of a login/restore/logout
/// and threaded end-to-end so every durable side effect can be gated on exactly
/// that operation at its mutation point (issue #816, H2).
struct AuthOperationToken: Sendable, Equatable {
    let value: Int
    /// Backward-compatible sentinel for callers that do not participate in the
    /// operation-token protocol (treated as always-current).
    static let unspecified = AuthOperationToken(value: Int.min)
}

/// Explicit result of a login operation. There is no "successful" shape for
/// superseded work — a login overtaken by a newer op/logout returns `.superseded`
/// and performs no durable mutation.
enum AuthLoginOutcome: Sendable {
    /// The login applied. `response.user` carries the VERIFIED current identity
    /// (from the response or an authoritative `currentUser()` fetch).
    case applied(AuthResponse)
    case superseded
}

/// Explicit result of a session-restore operation.
enum AuthRestoreOutcome: Sendable {
    case restored(UserDTO)
    case superseded
    case noSession
}

/// Contract for authentication operations.
protocol AuthServiceProtocol: Sendable {
    func login(serverURL: String, username: String, password: String, operation: AuthOperationToken) async throws -> AuthLoginOutcome
    func logout(operation: AuthOperationToken) async
    func restoreSession(operation: AuthOperationToken) async -> AuthRestoreOutcome
    func currentUser() async throws -> UserDTO
    var isAuthenticated: Bool { get async }
}

// MARK: - Backward-compatible convenience

extension AuthServiceProtocol {
    /// Legacy convenience: unspecified-token login unwrapping the applied response.
    func login(serverURL: String, username: String, password: String) async throws -> AuthResponse {
        switch try await login(serverURL: serverURL, username: username, password: password, operation: .unspecified) {
        case .applied(let response):
            return response
        case .superseded:
            throw NetworkError.authFailed("Login superseded")
        }
    }

    func logout() async {
        await logout(operation: .unspecified)
    }

    func restoreSession() async -> UserDTO? {
        switch await restoreSession(operation: .unspecified) {
        case .restored(let user):
            return user
        case .superseded, .noSession:
            return nil
        }
    }
}
