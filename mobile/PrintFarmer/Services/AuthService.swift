import Foundation

// MARK: - Auth Service

final class AuthServiceUserDefaultsBox: @unchecked Sendable {
    let userDefaults: UserDefaults

    init(_ userDefaults: UserDefaults) {
        self.userDefaults = userDefaults
    }
}

/// Monotonic epoch for auth operations (login / restore / logout). Advanced by
/// the orchestrator (`AuthViewModel`) at the start of each operation; consulted
/// by `AuthService` before durable owner/credential mutations and by the view
/// model before view-state/snapshot activation, so a superseded late-completing
/// login/restore cannot resurrect auth/owner/session (issue #816, H2).
final class AuthOperationEpoch: @unchecked Sendable {
    private let lock = NSLock()
    private var value = 0

    @discardableResult
    func advance() -> Int {
        lock.lock()
        defer { lock.unlock() }
        value += 1
        return value
    }

    var current: Int {
        lock.lock()
        defer { lock.unlock() }
        return value
    }

    func isCurrent(_ epoch: Int) -> Bool {
        lock.lock()
        defer { lock.unlock() }
        return value == epoch
    }
}

actor AuthService: AuthServiceProtocol {
    private let apiClient: APIClient
    private let credentialsStore: ServerCredentialsStore
    private let userDefaultsBox: AuthServiceUserDefaultsBox
    private let migrateLegacyServerURL: Bool
    private let serverRegistry: ServerRegistry?
    private let snapshotOwnerStore: FarmSnapshotOwnerStore
    private let authEpoch: AuthOperationEpoch

    init(
        apiClient: APIClient,
        credentialsStore: ServerCredentialsStore = ServerCredentialsStore(),
        userDefaultsBox: AuthServiceUserDefaultsBox = AuthServiceUserDefaultsBox(.standard),
        migrateLegacyServerURL: Bool = true,
        serverRegistry: ServerRegistry? = nil,
        snapshotOwnerStore: FarmSnapshotOwnerStore = FarmSnapshotOwnerStore(),
        authEpoch: AuthOperationEpoch = AuthOperationEpoch()
    ) {
        self.apiClient = apiClient
        self.credentialsStore = credentialsStore
        self.userDefaultsBox = userDefaultsBox
        self.migrateLegacyServerURL = migrateLegacyServerURL
        self.serverRegistry = serverRegistry
        self.snapshotOwnerStore = snapshotOwnerStore
        self.authEpoch = authEpoch
    }

    /// Authenticate against a Printfarmer server.
    /// Stores the JWT and applies it to the shared API client for the active server on success.
    func login(serverURL: String, username: String, password: String) async throws -> AuthResponse {
        // H2: capture the auth-operation epoch at entry. If a logout / newer op
        // supersedes this login while it is in flight, durable owner/credential
        // mutations are skipped so a late login cannot resurrect auth/owner state.
        let epoch = authEpoch.current
        guard let normalizedURL = APIClient.normalizedServerURLString(serverURL),
              let url = URL(string: normalizedURL) else {
            throw NetworkError.invalidURL(serverURL)
        }
        let server = try await resolveActiveServer(for: url, normalizedURLString: normalizedURL)
        let loginClient = await apiClient.unauthenticatedClient(baseURL: url)

        let request = LoginRequest(
            usernameOrEmail: username,
            password: password,
            rememberMe: true
        )
        let response: AuthResponse = try await loginClient.post("/api/auth/login", body: request)

        guard response.success, let token = response.token else {
            credentialsStore.clear(serverId: server.id)
            throw NetworkError.authFailed(response.error ?? "Login failed")
        }

        guard authEpoch.isCurrent(epoch) else { return response }
        credentialsStore.save(
            ServerCredentials(accessToken: token, expiresAt: response.expiresAt),
            serverId: server.id
        )
        await apiClient.updateBaseURL(server.baseURL)
        await apiClient.setAccessToken(token)
        await registerTokenExpiryChecker(for: server)

        // H2: the snapshot owner must be a VERIFIED current identity. A token-only
        // response (`user == nil`) must never reuse a persisted prior owner — verify
        // via an authoritative `currentUser()` fetch, and fail closed (clear any
        // stale owner) if no stable id can be established.
        var verifiedUser = response.user
        if verifiedUser == nil {
            verifiedUser = try? await currentUser()
        }
        guard authEpoch.isCurrent(epoch) else { return response }
        if let user = verifiedUser {
            snapshotOwnerStore.setOwner(userID: user.id, serverID: server.id)
        } else {
            snapshotOwnerStore.clearOwner(serverID: server.id)
        }
        await activate(server)
        return response
    }

    func logout() async {
        // Supersede any in-flight login/restore so its late completion cannot
        // resurrect credentials/owner/session (H2).
        authEpoch.advance()
        let currentServer = await activeServer()
        try? await apiClient.postVoid("/api/auth/logout")
        if let server = currentServer {
            credentialsStore.clear(serverId: server.id)
            // Explicit logout clears the persisted owner identity for this server.
            snapshotOwnerStore.clearOwner(serverID: server.id)
        }
        await apiClient.setAccessToken(nil)
    }

    /// Attempt to restore a previous session from Keychain.
    /// Returns the current user on success, nil if no valid session.
    func restoreSession() async -> UserDTO? {
        let epoch = authEpoch.current
        guard let server = await activeServer() else { return nil }
        migrateLegacyCredentialsIfAllowed(to: server)
        guard let credentials = credentialsStore.load(serverId: server.id) else { return nil }

        await apiClient.updateBaseURL(server.baseURL)
        await apiClient.setAccessToken(credentials.accessToken)
        await registerTokenExpiryChecker(for: server)

        do {
            let user: UserDTO = try await apiClient.get("/api/auth/me")
            // H2: if a logout / newer op superseded this restore mid-flight, do not
            // persist the owner or report success (no resurrection).
            guard authEpoch.isCurrent(epoch) else { return nil }
            // Online-verified restore: persist the owner (transient offline never
            // reaches here, so the last verified owner is preserved).
            snapshotOwnerStore.setOwner(userID: user.id, serverID: server.id)
            return user
        } catch {
            if isDefinitiveAuthRejection(error) {
                credentialsStore.clear(serverId: server.id)
                snapshotOwnerStore.clearOwner(serverID: server.id)
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

    private func activeServer() async -> RegisteredServer? {
        if let serverRegistry {
            return await MainActor.run {
                serverRegistry.activeServer
            }
        }

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
        if let serverRegistry {
            return try await MainActor.run {
                if let matching = serverRegistry.servers.first(where: { $0.normalizedURLString == normalizedURLString }) {
                    return matching
                }

                if let active = serverRegistry.activeServer, active.normalizedURLString == normalizedURLString {
                    return active
                }

                return try serverRegistry.add(displayName: url.host ?? "PrintFarmer", baseURL: url, makeActiveIfNeeded: false)
            }
        }

        let userDefaultsBox = userDefaultsBox
        let migrateLegacyServerURL = migrateLegacyServerURL
        return try await MainActor.run {
            let registry = ServerRegistry(
                userDefaults: userDefaultsBox.userDefaults,
                migrateLegacyServerURL: migrateLegacyServerURL
            )
            if let matching = registry.servers.first(where: { $0.normalizedURLString == normalizedURLString }) {
                return matching
            }

            if let active = registry.activeServer, active.normalizedURLString == normalizedURLString {
                return active
            }

            let server = try registry.add(displayName: url.host ?? "PrintFarmer", baseURL: url, makeActiveIfNeeded: false)
            return server
        }
    }

    private func activate(_ server: RegisteredServer) async {
        if let serverRegistry {
            await MainActor.run {
                try? serverRegistry.setActive(id: server.id)
            }
            return
        }

        let userDefaultsBox = userDefaultsBox
        let migrateLegacyServerURL = migrateLegacyServerURL
        await MainActor.run {
            let registry = ServerRegistry(
                userDefaults: userDefaultsBox.userDefaults,
                migrateLegacyServerURL: migrateLegacyServerURL
            )
            try? registry.setActive(id: server.id)
        }
    }

    private func registerTokenExpiryChecker(for server: RegisteredServer) async {
        let credentialsStore = credentialsStore
        let serverId = server.id
        await apiClient.setTokenExpiryChecker {
            credentialsStore.isExpired(serverId: serverId)
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
