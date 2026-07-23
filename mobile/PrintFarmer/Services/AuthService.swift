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
    ///
    /// J (issue #816 reject, Hicks): shared destinations (credentials, apiClient
    /// session, snapshot owner, registry active-server) are published ONLY
    /// AFTER identity verification succeeds, and each publication step is fenced
    /// on the operation epoch with an operation-owned rollback so a supersession
    /// at ANY await boundary leaves credentials + apiClient bearer/session +
    /// owner + registry active-server UNTOUCHED. The rollback uses
    /// compare-and-clear helpers so a newer T2 login that has already
    /// re-published its own values is never clobbered.
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

        // J (issue #816 reject, Hicks): verify identity BEFORE publishing any
        // shared destination. The verification runs on an EPHEMERAL, bearer-loaded
        // client — the shared apiClient is not repointed here, so a supersession
        // during /me does not leave any shared destination holding this operation's
        // state.
        var verifiedUser = response.user
        if verifiedUser == nil {
            let verifyClient = await apiClient.unauthenticatedClient(baseURL: server.baseURL)
            await verifyClient.setAccessToken(token)
            verifiedUser = try? await verifyClient.get("/api/auth/me")
        }
        // After the verification await, if we've been superseded, publish nothing.
        guard isCurrentOperation(operation) else { return .superseded }

        // Publication phase — every step is fenced or CAS, and rolls back prior
        // published steps in reverse order on failure so no destination is left
        // holding this superseded operation's state at any await boundary.

        // Step 1: credentials + owner (single synchronous fenced block).
        let published = fencedMutation(operation) {
            credentialsStore.save(
                ServerCredentials(accessToken: token, expiresAt: response.expiresAt),
                serverId: server.id
            )
            if let user = verifiedUser {
                snapshotOwnerStore.setOwner(userID: user.id, serverID: server.id)
            } else {
                snapshotOwnerStore.clearOwner(serverID: server.id)
            }
        }
        guard published else { return .superseded }

        // Step 2: apiClient shared session (CAS on the epoch — carries stable
        // serverID so a later logout snapshot cannot separate baseURL from
        // server identity).
        if operation == .unspecified {
            await apiClient.updateBaseURL(server.baseURL)
            await apiClient.setAccessToken(token)
        } else {
            let applied = await apiClient.applySessionIfCurrent(
                baseURL: server.baseURL, accessToken: token, serverID: server.id,
                epoch: authEpoch, token: operation.value
            )
            guard applied else {
                // Rollback step 1: our exact credentials + owner.
                credentialsStore.clearIfAccessTokenMatches(serverId: server.id, expectedAccessToken: token)
                snapshotOwnerStore.clearOwnerIfMatches(serverID: server.id, expectedUserID: verifiedUser?.id)
                return .superseded
            }
        }
        await registerTokenExpiryChecker(for: server)

        // Step 3: registry activate (fenced on operation).
        let activated = await activate(server, operation: operation)
        guard activated else {
            // Rollback steps 1-2: our exact credentials + owner + apiClient session.
            credentialsStore.clearIfAccessTokenMatches(serverId: server.id, expectedAccessToken: token)
            snapshotOwnerStore.clearOwnerIfMatches(serverID: server.id, expectedUserID: verifiedUser?.id)
            if operation != .unspecified {
                await apiClient.clearSessionIfMatches(
                    expectedAccessToken: token,
                    expectedAuthSessionToken: operation.value
                )
            }
            return .superseded
        }
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
        // J (issue #816 reject, Hicks): capture the FULL logout snapshot
        // (client + baseURL + accessToken + stable serverID) in ONE APIClient
        // actor hop, fenced on the operation. Local cleanup uses the SNAPSHOT's
        // serverID, NEVER the mutable registry's activeServer — so a
        // registry-driven server switch landing between here and the /logout
        // network await (or between the network hop and local cleanup)
        // cannot cause /logout to hit server A while cleanup wipes server B.
        // A superseded logout returns a nil snapshot and does zero work.
        let snapshot: APIClient.LogoutSnapshot?
        if operation == .unspecified {
            snapshot = await apiClient.logoutOperationSnapshot()
        } else {
            snapshot = await apiClient.logoutOperationSnapshotIfCurrent(
                epoch: authEpoch, token: operation.value
            )
        }
        // J: when the snapshot's serverID is unset (legacy `.unspecified`
        // callers or tests that bypass login and never call
        // applySessionIfCurrent), fall back to the registry's currently-active
        // server for local cleanup. Fenced callers always populate the
        // snapshot's serverID atomically via applySessionIfCurrent, so this
        // fallback is only taken by legacy paths that were never subject to
        // the epoch-fenced atomicity guarantee.
        let cleanupServerID: UUID?
        if let snapshotServerID = snapshot?.serverID {
            cleanupServerID = snapshotServerID
        } else if operation == .unspecified, snapshot != nil {
            cleanupServerID = await activeServer()?.id
        } else {
            cleanupServerID = nil
        }
        // Network uses the snapshot's captured bearer/baseURL. A superseded
        // logout has a nil snapshot and skips the network entirely (better
        // than sending /logout under T2's session).
        if let snapshot {
            try? await snapshot.client.postVoid("/api/auth/logout")
        }
        // After the network await, clear local + durable state only if THIS
        // operation is still current, each mutation fenced atomically on the
        // operation. Local cleanup targets the SNAPSHOT'S serverID — a
        // registry switch cannot redirect it.
        fencedMutation(operation) {
            if let serverID = cleanupServerID {
                credentialsStore.clear(serverId: serverID)
                // Explicit logout clears the persisted owner identity for this server.
                snapshotOwnerStore.clearOwner(serverID: serverID)
            }
        }
        // Clear the shared session only if still current (destination CAS), so a stale
        // logout can never clear a newer session's APIClient bearer.
        if operation == .unspecified {
            await apiClient.setAccessToken(nil)
        } else {
            _ = await apiClient.applySessionIfCurrent(
                baseURL: nil, accessToken: nil, serverID: nil,
                epoch: authEpoch, token: operation.value
            )
        }
    }

    /// Attempt to restore a previous session from Keychain.
    ///
    /// J (issue #816 reject, Hicks): apiClient session is applied via CAS FIRST
    /// (matching legacy transient-offline continuity: a transient /me failure
    /// preserves creds AND leaves the apiClient bearer applied for the next
    /// online retry), but every operation-fenced destination that follows uses
    /// compare-and-clear rollback so a supersession detected after publication
    /// leaves credentials/apiClient/owner UNCHANGED. Only a definitive auth
    /// rejection clears creds/owner/apiClient.
    func restoreSession(operation: AuthOperationToken) async -> AuthRestoreOutcome {
        guard let server = await activeServer() else { return .noSession }
        migrateLegacyCredentialsIfAllowed(to: server)
        guard let credentials = credentialsStore.load(serverId: server.id) else { return .noSession }

        // H2/J: apply the shared session via a destination CAS with atomic serverID
        // so a restore superseded by a newer login/logout cannot clobber the newer
        // session. Legacy unspecified callers keep the unconditional behavior.
        if operation == .unspecified {
            await apiClient.updateBaseURL(server.baseURL)
            await apiClient.setAccessToken(credentials.accessToken)
        } else {
            let applied = await apiClient.applySessionIfCurrent(
                baseURL: server.baseURL, accessToken: credentials.accessToken, serverID: server.id,
                epoch: authEpoch, token: operation.value
            )
            guard applied else { return .superseded }
        }
        await registerTokenExpiryChecker(for: server)

        do {
            let user: UserDTO = try await apiClient.get("/api/auth/me")
            // J (issue #816 reject, Hicks): if a logout / newer op superseded this
            // restore mid-flight (isCurrentOperation false OR the fenced owner write
            // fails), roll back the apiClient session we published — compare-and-clear
            // so a newer T2 login's session is preserved (different bearer/token).
            guard isCurrentOperation(operation) else {
                if operation != .unspecified {
                    await apiClient.clearSessionIfMatches(
                        expectedAccessToken: credentials.accessToken,
                        expectedAuthSessionToken: operation.value
                    )
                }
                return .superseded
            }
            let published = fencedMutation(operation) {
                snapshotOwnerStore.setOwner(userID: user.id, serverID: server.id)
            }
            guard published else {
                if operation != .unspecified {
                    await apiClient.clearSessionIfMatches(
                        expectedAccessToken: credentials.accessToken,
                        expectedAuthSessionToken: operation.value
                    )
                }
                return .superseded
            }
            return .restored(user)
        } catch {
            // H2/J: a stale restore's late 401 must not clear a newer login's
            // credentials/owner/session — fence the definitive-rejection clears on the
            // operation, and CAS the APIClient clear. Transient errors do NOT clear
            // (offline continuity).
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
                            baseURL: nil, accessToken: nil, serverID: nil,
                            epoch: authEpoch, token: operation.value
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
