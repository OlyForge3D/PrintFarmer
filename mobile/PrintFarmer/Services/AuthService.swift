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

    /// J: run a durable destination mutation FENCED on exactly this operation — atomic
    /// under the auth-operation epoch for real operations (no await inside the lock), or
    /// unconditional for legacy `.unspecified`. Returns whether the mutation ran; a
    /// superseded operation performs zero side effects. Because the epoch currency check
    /// and the mutation share one lock domain, the epoch cannot advance between them, so
    /// a parked/late operation can neither persist nor clear a newer session's state.
    @discardableResult
    private func fencedMutation(_ operation: AuthOperationToken, _ body: () -> Void) -> Bool {
        if operation == .unspecified { body(); return true }
        let ran: Void? = authEpoch.withCurrent(operation.value) { body() }
        return ran != nil
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
            // H2/J: a superseded/stale failed login must not clear a newer login's
            // stored credentials — fence the clear atomically on exactly this operation.
            fencedMutation(operation) { credentialsStore.clear(serverId: server.id) }
            throw NetworkError.authFailed(response.error ?? "Login failed")
        }

        // H2/J: fence every durable mutation atomically on exactly this operation at the
        // mutation point. A superseded login performs no durable work. The shared
        // APIClient session is applied via a destination compare-and-set so a login
        // superseded DURING the await cannot clobber a newer session.
        guard fencedMutation(operation, {
            credentialsStore.save(
                ServerCredentials(accessToken: token, expiresAt: response.expiresAt),
                serverId: server.id
            )
        }) else { return .superseded }
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
        // J (issue #816 reject): do NOT ignore the owner-mutation fenced result.
        // If it fails, the operation has been superseded — return .superseded
        // rather than proceeding to activate() and stamping the registry.
        guard fencedMutation(operation, {
            if let user = verifiedUser {
                snapshotOwnerStore.setOwner(userID: user.id, serverID: server.id)
            } else {
                snapshotOwnerStore.clearOwner(serverID: server.id)
            }
        }) else { return .superseded }
        // J (issue #816 reject): active-server registry mutation must also be
        // fenced on this operation and must not silently swallow its outcome.
        // A superseded login MUST NOT stamp the registry active server under a
        // superseded operation's identity.
        let activated = await activate(server, operation: operation)
        guard activated else { return .superseded }
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
        // J (issue #816 reject): capture the session snapshot ATOMICALLY under an
        // operation currency check BEFORE any other await (activeServer() or the
        // /logout network hop). Even a T2 apply that lands between here and the
        // network can never cause /logout to be sent under T2's bearer, because
        // this snapshot returns nil when the epoch has advanced past this
        // operation's token.
        let logoutClient: APIClient?
        if operation == .unspecified {
            logoutClient = await apiClient.sessionSnapshotClient()
        } else {
            logoutClient = await apiClient.sessionSnapshotClientIfCurrent(
                epoch: authEpoch, token: operation.value
            )
        }
        // Only look up the current registered server AFTER the snapshot is fixed.
        // A stale logout that fails the epoch-currency check does not need to
        // resolve the active server — it will neither hit the network nor mutate
        // any local state below (the fencedMutation guard also rejects it).
        let currentServer = await activeServer()
        // Network uses the snapshot's captured bearer/baseURL. A superseded
        // logout has a nil snapshot and skips the network entirely (better than
        // sending /logout under T2's session).
        if let logoutClient {
            try? await logoutClient.postVoid("/api/auth/logout")
        }
        // After the network await, clear local + durable state only if THIS operation is
        // still current, each mutation fenced atomically on the operation.
        fencedMutation(operation) {
            if let server = currentServer {
                credentialsStore.clear(serverId: server.id)
                // Explicit logout clears the persisted owner identity for this server.
                snapshotOwnerStore.clearOwner(serverID: server.id)
            }
        }
        // Clear the shared session only if still current (destination CAS), so a stale
        // logout can never clear a newer session's APIClient bearer.
        if operation == .unspecified {
            await apiClient.setAccessToken(nil)
        } else {
            _ = await apiClient.applySessionIfCurrent(
                baseURL: nil, accessToken: nil, epoch: authEpoch, token: operation.value
            )
        }
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
            guard fencedMutation(operation, {
                snapshotOwnerStore.setOwner(userID: user.id, serverID: server.id)
            }) else { return .superseded }
            return .restored(user)
        } catch {
            // H2/J: a stale restore's late 401 must not clear a newer login's
            // credentials/owner/session — fence the definitive-rejection clears on the
            // operation, and CAS the APIClient clear.
            if isDefinitiveAuthRejection(error) {
                fencedMutation(operation) {
                    credentialsStore.clear(serverId: server.id)
                    snapshotOwnerStore.clearOwner(serverID: server.id)
                }
                if isCurrentOperation(operation) {
                    if operation == .unspecified {
                        await apiClient.setAccessToken(nil)
                    } else {
                        _ = await apiClient.applySessionIfCurrent(
                            baseURL: nil, accessToken: nil, epoch: authEpoch, token: operation.value
                        )
                    }
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

    /// J (issue #816 reject): activate the given server as the registry's active
    /// server, FENCED on this operation and surfacing the result. A superseded
    /// operation makes no registry mutation and returns false. The mutation is
    /// synchronous under a MainActor hop (registry is main-actor-isolated), so
    /// the operation-currency check and the mutation are one atomic unit under
    /// the auth-operation epoch lock — a newer operation cannot advance between
    /// them and see a stale active server.
    @discardableResult
    private func activate(_ server: RegisteredServer, operation: AuthOperationToken = .unspecified) async -> Bool {
        if let serverRegistry {
            return await MainActor.run {
                self.fencedRegistryMutation(operation) {
                    // J: the registry setActive result MUST NOT be silently
                    // swallowed. A duplicate/no-op is treated as success; a hard
                    // failure logs and returns false so the caller can react.
                    do {
                        try serverRegistry.setActive(id: server.id)
                        return true
                    } catch {
                        return false
                    }
                }
            }
        }

        let userDefaultsBox = userDefaultsBox
        let migrateLegacyServerURL = migrateLegacyServerURL
        return await MainActor.run {
            self.fencedRegistryMutation(operation) {
                let registry = ServerRegistry(
                    userDefaults: userDefaultsBox.userDefaults,
                    migrateLegacyServerURL: migrateLegacyServerURL
                )
                do {
                    try registry.setActive(id: server.id)
                    return true
                } catch {
                    return false
                }
            }
        }
    }

    /// J: fenced variant of `fencedMutation` that runs the given body under the
    /// operation-currency lock and returns the body's own success/failure. Used
    /// by `activate()` so a supersession short-circuits (returns false without
    /// running body) and a body-reported failure surfaces to the caller.
    /// nonisolated because it only touches the Sendable `authEpoch` — safe to
    /// invoke from a MainActor closure.
    @discardableResult
    private nonisolated func fencedRegistryMutation(_ operation: AuthOperationToken, _ body: () -> Bool) -> Bool {
        if operation == .unspecified { return body() }
        return authEpoch.withCurrent(operation.value) { body() } ?? false
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
