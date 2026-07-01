import Foundation

// MARK: - Auth Service

final class AuthServiceUserDefaultsBox: @unchecked Sendable {
    let userDefaults: UserDefaults

    init(_ userDefaults: UserDefaults) {
        self.userDefaults = userDefaults
    }
}

actor AuthService: AuthServiceProtocol {
    private let apiClient: APIClient
    private let credentialsStore: ServerCredentialsStore
    private let userDefaultsBox: AuthServiceUserDefaultsBox
    private let migrateLegacyServerURL: Bool

    init(
        apiClient: APIClient,
        credentialsStore: ServerCredentialsStore = ServerCredentialsStore(),
        userDefaultsBox: AuthServiceUserDefaultsBox = AuthServiceUserDefaultsBox(.standard),
        migrateLegacyServerURL: Bool = true
    ) {
        self.apiClient = apiClient
        self.credentialsStore = credentialsStore
        self.userDefaultsBox = userDefaultsBox
        self.migrateLegacyServerURL = migrateLegacyServerURL
    }

    /// Authenticate against a Printfarmer server.
    /// Sets the API client's base URL and stores the JWT for the active server on success.
    func login(serverURL: String, username: String, password: String) async throws -> AuthResponse {
        guard let normalizedURL = APIClient.normalizedServerURLString(serverURL),
              let url = URL(string: normalizedURL) else {
            throw NetworkError.invalidURL(serverURL)
        }
        let server = try await resolveActiveServer(for: url, normalizedURLString: normalizedURL)
        await apiClient.updateBaseURL(url)

        let request = LoginRequest(
            usernameOrEmail: username,
            password: password,
            rememberMe: true
        )
        let response: AuthResponse = try await apiClient.post("/api/auth/login", body: request)

        guard response.success, let token = response.token else {
            credentialsStore.clear(serverId: server.id)
            throw NetworkError.authFailed(response.error ?? "Login failed")
        }

        credentialsStore.save(
            ServerCredentials(accessToken: token, expiresAt: response.expiresAt),
            serverId: server.id
        )
        await apiClient.setAccessToken(token)
        await registerTokenExpiryChecker()
        return response
    }

    func logout() async {
        let currentServer = await activeServer()
        try? await apiClient.postVoid("/api/auth/logout")
        if let server = currentServer {
            credentialsStore.clear(serverId: server.id)
        }
        await apiClient.setAccessToken(nil)
    }

    /// Attempt to restore a previous session from Keychain.
    /// Returns the current user on success, nil if no valid session.
    func restoreSession() async -> UserDTO? {
        guard let server = await activeServer() else { return nil }
        migrateLegacyCredentialsIfAllowed(to: server)
        guard let credentials = credentialsStore.load(serverId: server.id) else { return nil }

        await apiClient.updateBaseURL(server.baseURL)
        await apiClient.setAccessToken(credentials.accessToken)
        await registerTokenExpiryChecker()

        do {
            let user: UserDTO = try await apiClient.get("/api/auth/me")
            return user
        } catch {
            if isDefinitiveAuthRejection(error) {
                credentialsStore.clear(serverId: server.id)
                await apiClient.setAccessToken(nil)
            }
            return nil
        }
    }

    func currentUser() async throws -> UserDTO {
        try await apiClient.get("/api/auth/me")
    }

    var isAuthenticated: Bool {
        get async {
            guard let server = await activeServer() else { return false }
            migrateLegacyCredentialsIfAllowed(to: server)
            return credentialsStore.load(serverId: server.id) != nil
        }
    }

    /// Returns `true` when the active server's stored token has expired or will expire within 5 minutes.
    func isTokenExpired() async -> Bool {
        guard let server = await activeServer() else { return true }
        return credentialsStore.isExpired(serverId: server.id)
    }

    private func activeServer() async -> RegisteredServer? {
        let userDefaultsBox = userDefaultsBox
        let migrateLegacyServerURL = migrateLegacyServerURL
        return await MainActor.run {
            ServerRegistry(
                userDefaults: userDefaultsBox.userDefaults,
                migrateLegacyServerURL: migrateLegacyServerURL
            ).activeServer
        }
    }

    private func resolveActiveServer(for url: URL, normalizedURLString: String) async throws -> RegisteredServer {
        let userDefaultsBox = userDefaultsBox
        let migrateLegacyServerURL = migrateLegacyServerURL
        return try await MainActor.run {
            let registry = ServerRegistry(
                userDefaults: userDefaultsBox.userDefaults,
                migrateLegacyServerURL: migrateLegacyServerURL
            )
            if let matching = registry.servers.first(where: { $0.normalizedURLString == normalizedURLString }) {
                try registry.setActive(id: matching.id)
                return matching
            }

            if let active = registry.activeServer, active.normalizedURLString == normalizedURLString {
                return active
            }

            let server = try registry.add(displayName: url.host ?? "PrintFarmer", baseURL: url)
            try registry.setActive(id: server.id)
            return server
        }
    }

    private func registerTokenExpiryChecker() async {
        await apiClient.setTokenExpiryChecker { [weak self] in
            guard let self else { return true }
            return await self.isTokenExpired()
        }
    }

    private func migrateLegacyCredentialsIfAllowed(to server: RegisteredServer) {
        guard legacyServerURLMatches(server) else { return }
        credentialsStore.migrateLegacyCredentialsIfNeeded(to: server.id)
    }

    private func legacyServerURLMatches(_ server: RegisteredServer) -> Bool {
        guard let legacyURLString = APIClient.savedServerURLString(userDefaults: userDefaultsBox.userDefaults) else {
            return false
        }
        return legacyURLString == server.normalizedURLString
    }

    private func isDefinitiveAuthRejection(_ error: Error) -> Bool {
        guard let networkError = error as? NetworkError else { return false }
        switch networkError {
        case .unauthorized, .forbidden:
            return true
        case .clientError(let statusCode, _):
            return statusCode == 401 || statusCode == 403
        case .unexpectedStatus(let statusCode):
            return statusCode == 401 || statusCode == 403
        default:
            return false
        }
    }
}
