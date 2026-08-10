import Foundation
import Observation

/// App-level authentication state that controls whether the user sees
/// LoginView or the main TabView. Injected via @Environment.
@MainActor @Observable
final class AuthViewModel {
    var isAuthenticated = false
    var currentUser: UserDTO?
    var isLoading = false
    var errorMessage: String?

    /// D: true when authentication succeeded but the farm-snapshot activation could not
    /// complete because startup preparation failed. The session is authenticated, but
    /// the snapshot is NOT yet ready — the app must not treat it as fully ready and can
    /// retry activation (without a new login) via `retrySnapshotActivationIfPending()`.
    private(set) var snapshotActivationPending = false
    /// D: the full activation snapshot to retry against. Stores server/user/generation/
    /// auth-token so the retry cannot bind a different server than the one that failed
    /// and cannot be reused across a server switch or an auth-token change (issue #816
    /// reject D: "invalidate on server/desired-target/auth change").
    struct PendingActivation: Equatable, Sendable {
        let authToken: Int
        let serverID: UUID
        let userID: UUID
        let generation: Int
    }
    @ObservationIgnored private(set) var pendingActivation: PendingActivation?

    /// The current user's primary role for permission gating.
    /// Returns "farm_admin" when the user has that role; otherwise the first role; nil if unauthenticated.
    var currentUserRole: String? {
        guard let roles = currentUser?.roles else { return nil }
        if roles.contains("farm_admin") { return "farm_admin" }
        return roles.first
    }
    /// True once the initial session restore check has completed.
    private(set) var hasCheckedAuth = false

    private let services: ServiceContainer
    @ObservationIgnored private var sessionExpiredObserver: NSObjectProtocol?

    init(services: ServiceContainer) {
        self.services = services
        let invalidateOfflineReplay = services.makeOfflineReplaySessionExpiryInvalidator()
        sessionExpiredObserver = NotificationCenter.default.addObserver(
            forName: .sessionExpired,
            object: nil,
            queue: .main
        ) { [weak self] note in
            guard let self else { return }
            // The event carries the originating auth-session identity
            // {generation, authSessionToken}. Unauthenticated/login clients suppress the
            // event entirely (A), so a missing identity is discarded here too.
            let generation = (note.userInfo?["generation"] as? Int)
            let authSessionToken = (note.userInfo?["authSessionToken"] as? Int)
            guard invalidateOfflineReplay(generation, authSessionToken) else { return }
            Task { @MainActor in
                await self.handleSessionExpiration(generation: generation, authSessionToken: authSessionToken)
            }
        }
    }

    /// React to a server-reported session expiry. This is NOT a user logout: it must
    /// not advance the auth-operation token. It fail-closes unless BOTH the originating
    /// server generation AND the originating auth-session token are still current — so a
    /// stale-server or old-session 401 arriving during a newer same-server login can
    /// never log out the newer session (issue #816 A). The carried token is used
    /// directly; the current token is NEVER borrowed to authorize an old event.
    private func handleSessionExpiration(generation: Int?, authSessionToken: Int?) async {
        guard let generation, let authSessionToken else { return } // suppress identity-less events
        guard services.isActiveGeneration(generation),
              services.authOperationEpoch.isCurrent(authSessionToken) else { return }
        services.invalidateOfflineWriteReplayAuthority()
        await services.unbindOfflineWriteQueue()
        guard services.isActiveGeneration(generation),
              services.authOperationEpoch.isCurrent(authSessionToken) else { return }
        await services.revokeFarmSnapshot()
        await services.authService.logout(operation: AuthOperationToken(value: authSessionToken))
        guard services.authOperationEpoch.isCurrent(authSessionToken) else { return }
        isAuthenticated = false
        currentUser = nil
    }

    /// D: record a snapshot activation outcome. `.preparationFailed` marks a retryable
    /// pending activation with the exact snapshot (server/user/generation/authToken); any
    /// other outcome clears the pending state. The pending record is later invalidated on
    /// any server/desired-target/auth change (checked at retry time in
    /// `retrySnapshotActivationIfPending`).
    private func recordActivationOutcome(_ result: FarmSnapshotActivationResult, authToken: Int) {
        if result == .preparationFailed,
           let serverID = services.currentActiveServerID,
           let userID = currentUser?.id {
            snapshotActivationPending = true
            pendingActivation = PendingActivation(
                authToken: authToken,
                serverID: serverID,
                userID: userID,
                generation: services.activeServerGeneration
            )
        } else {
            snapshotActivationPending = false
            pendingActivation = nil
        }
    }

    /// D: retry a pending snapshot activation WITHOUT a new login. Reuses the original
    /// auth-operation token, server id, user id, and generation so identity is preserved
    /// and cannot bind a different server. On success (or any non-retryable outcome) the
    /// pending flag clears; if preparation still fails it stays pending for a future
    /// retry. No-op when nothing is pending, the token was superseded, or the pinned
    /// server / user / generation no longer matches the current app state.
    @discardableResult
    func retrySnapshotActivationIfPending() async -> FarmSnapshotActivationResult? {
        guard snapshotActivationPending, let pending = pendingActivation else { return nil }
        // Auth-token invalidation: a newer login/logout advanced the epoch.
        guard services.authOperationEpoch.isCurrent(pending.authToken) else {
            snapshotActivationPending = false
            pendingActivation = nil
            return nil
        }
        // Server-switch invalidation: user changed the active server since the failure.
        // The retry is pinned to the failed server and must NOT bind the current server.
        guard let currentServerID = services.currentActiveServerID,
              currentServerID == pending.serverID else {
            snapshotActivationPending = false
            pendingActivation = nil
            return nil
        }
        // Generation-switch invalidation: the active-server generation advanced.
        guard services.isActiveGeneration(pending.generation) else {
            snapshotActivationPending = false
            pendingActivation = nil
            return nil
        }
        // User-change invalidation: authenticated user is no longer the pending user.
        guard currentUser?.id == pending.userID else {
            snapshotActivationPending = false
            pendingActivation = nil
            return nil
        }
        let result = await services.retryFarmSnapshotActivation(
            authToken: pending.authToken,
            expectedServerID: pending.serverID,
            expectedGeneration: pending.generation
        )
        guard services.authOperationEpoch.isCurrent(pending.authToken) else { return nil }
        recordActivationOutcome(result, authToken: pending.authToken)
        if result == .activated {
            services.authorizeOfflineWriteReplayBinding()
            await services.syncOfflineWriteQueue()
            guard services.authOperationEpoch.isCurrent(pending.authToken) else { return nil }
        }
        return result
    }

    // MARK: - Session Restoration

    func restoreSession() async {
        // Idempotence guard: if this view model was pre-authenticated by
        // `UITestBootstrap` (via `markAuthenticatedForUITesting(user:)`)
        // there is no session to restore. Re-invoking the demo auth
        // service here would send an unnecessary round-trip and — in
        // future auth-service implementations — could reset session
        // state we already established. Short-circuiting keeps this
        // method safe to call multiple times.
        if hasCheckedAuth && isAuthenticated {
            return
        }

        // H2 (Bishop): mint one operation token and revalidate before EVERY VM
        // mutation (loading/user/auth). A superseded restore does nothing — it never
        // clears loading (the newer op owns it) and never resurrects auth after a
        // logout landed. The token is threaded into activation so a logout landing
        // during the activation await cannot rebind.
        let token = AuthOperationToken(value: services.authOperationEpoch.advance())
        services.invalidateOfflineWriteReplayAuthority()
        isLoading = true
        if case .restored(let user) = await services.authService.restoreSession(operation: token) {
            guard services.authOperationEpoch.isCurrent(token.value) else {
                // V5 (issue #816 reject, Vasquez): a SUPERSEDED restore must make
                // ZERO view-state changes — `hasCheckedAuth` (like loading/user/
                // auth) belongs to the newer operation that superseded us. Setting
                // it here let a stale restore flip the flag out from under a newer
                // login/logout. Return without touching any view state.
                return
            }
            currentUser = user
            isAuthenticated = true
            let activation = await services.activateFarmSnapshotForActiveServer(authToken: token.value)
            guard services.authOperationEpoch.isCurrent(token.value) else {
                return
            }
            recordActivationOutcome(activation, authToken: token.value)
            if activation == .activated {
                services.authorizeOfflineWriteReplayBinding()
                await services.syncOfflineWriteQueue()
                guard services.authOperationEpoch.isCurrent(token.value) else { return }
            }
        } else {
            guard services.authOperationEpoch.isCurrent(token.value) else {
                return
            }
        }
        isLoading = false
        hasCheckedAuth = true
    }

    // MARK: - UI Test Bootstrap

    /// Marks the view model as authenticated without hitting the auth
    /// service. Used exclusively by `UITestBootstrap` (guarded by the
    /// `--uitesting` launch argument) to render `ContentView` on a fresh
    /// simulator. Does not modify `DemoMode.shared`, so production auth
    /// remains untouched on normal launches.
    func markAuthenticatedForUITesting(user: UserDTO) {
        currentUser = user
        isAuthenticated = true
        hasCheckedAuth = true
        isLoading = false
        errorMessage = nil
    }

    // MARK: - Login / Logout

    func login(serverURL: String, username: String, password: String) async {
        // H2 (Bishop): mint a unique operation token and thread it end-to-end.
        // Revalidate before EVERY VM mutation (loading/error/user/auth). A superseded
        // login returns and does nothing — never success-shaped, never clears a newer
        // operation's loading flag, never resurrects auth after a logout. The token is
        // also passed into activation so a logout landing DURING the activation await
        // fails the final snapshot-publication CAS.
        let token = AuthOperationToken(value: services.authOperationEpoch.advance())
        services.invalidateOfflineWriteReplayAuthority()
        isLoading = true
        errorMessage = nil

        // Registering a real backend always wins over demo mode. If demo
        // services are still wired up, `DemoAuthService` would accept any URL
        // and any credentials, return the mock user, and never contact
        // `serverURL` — silently stranding the user in demo data. Leaving demo
        // mode here guarantees this attempt hits the real server.
        if DemoMode.shared.isActive {
            DemoMode.shared.deactivate()
            services.switchToReal()
        }

        do {
            let outcome = try await services.authService.login(
                serverURL: serverURL,
                username: username,
                password: password,
                operation: token
            )
            // Superseded during the login await: the newer operation owns all VM state.
            guard services.authOperationEpoch.isCurrent(token.value) else { return }
            switch outcome {
            case .applied(let response):
                currentUser = response.user // VERIFIED user
                isAuthenticated = true
                let activation = await services.activateFarmSnapshotForActiveServer(authToken: token.value)
                // Logout / newer login may have landed during the activation awaits.
                guard services.authOperationEpoch.isCurrent(token.value) else { return }
                // D: if startup preparation failed, the session is authenticated but the
                // snapshot is NOT ready — record a retryable pending activation instead of
                // silently declaring fully ready.
                recordActivationOutcome(activation, authToken: token.value)
                if activation == .activated {
                    services.authorizeOfflineWriteReplayBinding()
                    await services.syncOfflineWriteQueue()
                    guard services.authOperationEpoch.isCurrent(token.value) else { return }
                }
                isLoading = false
            case .superseded:
                return // no view-state change, no loading clobber, for superseded work
            }
        } catch let error as NetworkError {
            // Stale failure after a newer operation must not clobber its error/loading.
            guard services.authOperationEpoch.isCurrent(token.value) else { return }
            errorMessage = friendlyMessage(for: error)
            isLoading = false
        } catch {
            guard services.authOperationEpoch.isCurrent(token.value) else { return }
            errorMessage = error.localizedDescription
            isLoading = false
        }
    }

    func logout() async {
        // Supersede any in-flight login/restore (H2), then revoke snapshot
        // authority (synchronous) and await store deactivation before the auth
        // service tears down the session. The VM clears are operation-owned: a newer
        // login starting during logout's awaits supersedes this logout, so its clears
        // (including the loading flag) are skipped and never clobber the newer op.
        let token = AuthOperationToken(value: services.authOperationEpoch.advance())
        services.invalidateOfflineWriteReplayAuthority()
        await services.unbindOfflineWriteQueue()
        guard services.authOperationEpoch.isCurrent(token.value) else { return }
        await services.revokeFarmSnapshot()
        guard services.authOperationEpoch.isCurrent(token.value) else { return }
        _ = await PushNotificationManager.shared.unregisterFromServer()
        await services.authService.logout(operation: token)
        guard services.authOperationEpoch.isCurrent(token.value) else { return }
        isAuthenticated = false
        currentUser = nil
        isLoading = false
        snapshotActivationPending = false
        pendingActivation = nil
    }

    func logoutIfServerRegistryUnavailable(_ registry: ServerRegistry) async {
        guard isAuthenticated,
              registry.servers.isEmpty || registry.activeServerID == nil else {
            return
        }

        await logout()
    }

    // MARK: - Demo Mode

    func loginAsDemo() async {
        services.invalidateOfflineWriteReplayAuthority()
        await services.switchToDemo()
        DemoMode.shared.activate()
        currentUser = DemoData.demoUser
        isAuthenticated = true
    }

    func exitDemoMode() async {
        DemoMode.shared.deactivate()
        services.switchToReal()
        await logout()
    }

    // MARK: - Helpers

    private func friendlyMessage(for error: NetworkError) -> String {
        switch error {
        case .unauthorized:
            "Invalid username or password."
        case .forbidden:
            "Your account does not have access."
        case .serverError:
            "The server encountered an error. Please try again."
        case .invalidURL:
            "Could not reach the server. Check the URL."
        case .noConnection:
            "No internet connection. Check your network."
        case .serverUnreachable:
            "Could not reach the server. Check the URL and try again."
        case .authFailed(let message):
            message
        default:
            error.errorDescription ?? "An unexpected error occurred."
        }
    }
}
