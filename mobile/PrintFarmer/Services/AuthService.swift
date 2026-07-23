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

    /// Runs `body` under the epoch lock IFF `epoch` is still current, so the epoch
    /// cannot advance between the currency check and the body's mutation — one atomic
    /// authority operation (issue #816 B). Returns `body`'s result, or `nil` when the
    /// operation was already superseded (body not run).
    func withCurrent<T>(_ epoch: Int, _ body: () -> T) -> T? {
        lock.lock()
        defer { lock.unlock() }
        guard value == epoch else { return nil }
        return body()
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

    /// Whether `operation` is still the current auth operation (or unspecified).
    private func isCurrentOperation(_ operation: AuthOperationToken) -> Bool {
        operation == .unspecified || authEpoch.isCurrent(operation.value)
    }

    /// Authenticate against a Printfarmer server.
    /// Stores the JWT and applies it to the shared API client for the active server on success.
    func login(serverURL: String, username: String, password: String, operation: AuthOperationToken) async throws -> AuthLoginOutcome {
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
            // H2: a superseded/stale failed login must not clear a newer login's stored
            // credentials — gate the clear on exactly this operation.
            if isCurrentOperation(operation) {
                credentialsStore.clear(serverId: server.id)
            }
            throw NetworkError.authFailed(response.error ?? "Login failed")
        }

        // H2: gate every durable mutation on exactly this operation token at the
        // mutation point. A superseded login performs no durable work. The credential
        // write (synchronous) is atomic with the check on this actor; the shared
        // APIClient session is applied via a destination compare-and-set so a login
        // superseded DURING the await cannot clobber a newer session.
        guard isCurrentOperation(operation) else { return .superseded }
        credentialsStore.save(
            ServerCredentials(accessToken: token, expiresAt: response.expiresAt),
            serverId: server.id
        )
        if operation == .unspecified {
            await apiClient.updateBaseURL(server.baseURL)
            await apiClient.setAccessToken(token)
        } else {
            let applied = await apiClient.applySessionIfCurrent(
                baseURL: server.baseURL, accessToken: token, epoch: authEpoch, token: operation.value
            )
            guard applied else { return .superseded }
        }
        await registerTokenExpiryChecker(for: server)

        // The snapshot owner must be a VERIFIED current identity. A token-only
        // response (`user == nil`) must never reuse a persisted prior owner — verify
        // via an authoritative `currentUser()` fetch, and fail closed (clear any
        // stale owner) if no stable id can be established.
        var verifiedUser = response.user
        if verifiedUser == nil {
            verifiedUser = try? await currentUser()
        }
        guard isCurrentOperation(operation) else { return .superseded }
        if let user = verifiedUser {
            snapshotOwnerStore.setOwner(userID: user.id, serverID: server.id)
        } else {
            snapshotOwnerStore.clearOwner(serverID: server.id)
        }
        await activate(server)
        // Carry the VERIFIED user back to the caller (not the original nil).
        let verifiedResponse = AuthResponse(
            success: response.success,
            token: response.token,
            expiresAt: response.expiresAt,
            user: verifiedUser,
            error: response.error
        )
        return .applied(verifiedResponse)
    }

    func logout(operation: AuthOperationToken) async {
        let currentServer = await activeServer()
        try? await apiClient.postVoid("/api/auth/logout")
        // H2: a late logout whose operation was superseded by a newer login must
        // not erase the newer login's credentials/owner/session.
        guard isCurrentOperation(operation) else { return }
        if let server = currentServer {
            credentialsStore.clear(serverId: server.id)
            // Explicit logout clears the persisted owner identity for this server.
            snapshotOwnerStore.clearOwner(serverID: server.id)
        }
        await apiClient.setAccessToken(nil)
    }

    /// Attempt to restore a previous session from Keychain.
    func restoreSession(operation: AuthOperationToken) async -> AuthRestoreOutcome {
        guard let server = await activeServer() else { return .noSession }
        migrateLegacyCredentialsIfAllowed(to: server)
        guard let credentials = credentialsStore.load(serverId: server.id) else { return .noSession }

        // H2: apply the shared session via a destination CAS so a restore superseded by
        // a newer login/logout cannot clobber the newer session. Legacy unspecified
        // callers keep the unconditional behavior.
        if operation == .unspecified {
            await apiClient.updateBaseURL(server.baseURL)
            await apiClient.setAccessToken(credentials.accessToken)
        } else {
            let applied = await apiClient.applySessionIfCurrent(
                baseURL: server.baseURL, accessToken: credentials.accessToken, epoch: authEpoch, token: operation.value
            )
            guard applied else { return .superseded }
        }
        await registerTokenExpiryChecker(for: server)

        do {
            let user: UserDTO = try await apiClient.get("/api/auth/me")
            // H2: if a logout / newer op superseded this restore mid-flight, do not
            // persist the owner or report success (no resurrection).
            guard isCurrentOperation(operation) else { return .superseded }
            // Online-verified restore: persist the owner (transient offline never
            // reaches here, so the last verified owner is preserved).
            snapshotOwnerStore.setOwner(userID: user.id, serverID: server.id)
            return .restored(user)
        } catch {
            // H2: a stale restore's late 401 must not clear a newer login's
            // credentials/owner/session — gate the definitive-rejection clears on the
            // token, and CAS the APIClient clear.
            if isDefinitiveAuthRejection(error), isCurrentOperation(operation) {
                credentialsStore.clear(serverId: server.id)
                snapshotOwnerStore.clearOwner(serverID: server.id)
                if operation == .unspecified {
                    await apiClient.setAccessToken(nil)
                } else {
                    _ = await apiClient.applySessionIfCurrent(
                        baseURL: nil, accessToken: nil, epoch: authEpoch, token: operation.value
                    )
                }
            }
            return .noSession
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
