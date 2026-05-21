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

    func login(serverURL: String, username: String, password: String) async throws -> AuthResponse {
        loginCalledWithServerURL = serverURL
        loginCalledWithUsername = username
        loginCalledWithPassword = password
        if let error = errorToThrow { throw error }
        guard let response = authResponseToReturn else {
            throw NetworkError.authFailed("No response configured")
        }
        if response.success { authenticated = true }
        return response
    }

    func logout() async {
        logoutCalled = true
        authenticated = false
    }

    func restoreSession() async -> UserDTO? {
        restoreSessionCalled = true
        return userToReturn
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
