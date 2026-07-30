import Foundation
@testable import PrintFarmer

final class MockAuthService: AuthServiceProtocol, @unchecked Sendable {
    var authResponseToReturn: AuthResponse?
    var userToReturn: UserDTO?
    var errorToThrow: Error?
    var authenticated = false

    // Call tracking
    var loginCalledWithServerURL: String?
    var loginCalledWithUsername: String?
    var loginCalledWithPassword: String?
    var logoutCalled = false
    var restoreSessionCalled = false
    var currentUserCalled = false

    func login(serverURL: String, username: String, password: String, operation: AuthOperationToken) async throws -> AuthLoginOutcome {
        loginCalledWithServerURL = serverURL
        loginCalledWithUsername = username
        loginCalledWithPassword = password
        if let error = errorToThrow { throw error }
        guard let response = authResponseToReturn else {
            throw NetworkError.authFailed("No response configured")
        }
        if response.success { authenticated = true }
        return .applied(response)
    }

    func logout(operation: AuthOperationToken) async {
        logoutCalled = true
        authenticated = false
    }

    func restoreSession(operation: AuthOperationToken) async -> AuthRestoreOutcome {
        restoreSessionCalled = true
        if let user = userToReturn {
            return .restored(user)
        }
        return .noSession
    }

    func currentUser() async throws -> UserDTO {
        currentUserCalled = true
        if let error = errorToThrow { throw error }
        guard let user = userToReturn else {
            throw NetworkError.unauthorized
        }
        return user
    }

    var isAuthenticated: Bool { authenticated }

    func reset() {
        authResponseToReturn = nil
        userToReturn = nil
        errorToThrow = nil
        authenticated = false
        loginCalledWithServerURL = nil
        loginCalledWithUsername = nil
        loginCalledWithPassword = nil
        logoutCalled = false
        restoreSessionCalled = false
        currentUserCalled = false
    }
}
