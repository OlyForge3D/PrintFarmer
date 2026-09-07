import AppIntents
import Foundation

/// Pure decision table for an external scan request, so the lifecycle is
/// testable without standing up the SwiftUI shell (#2480).
enum ExternalScanRouting {
    enum Decision: Equatable {
        /// Nothing pending.
        case idle
        /// Keep waiting for a coherent authenticated flow, binding the request to
        /// `scopeTo` if it is not bound yet.
        case retain(scopeTo: UUID?)
        /// The request can no longer be honoured coherently — drop it.
        case cancel
        /// Open the scanner now.
        case deliver
    }

    /// - Parameters:
    ///   - pending: the persisted request, if any.
    ///   - isShowingMainContent: whether the authenticated shell is on screen.
    ///   - activeServerID: the currently selected server.
    ///   - now: evaluation time, for expiry.
    static func decide(
        pending: PendingExternalScanRequest?,
        isShowingMainContent: Bool,
        activeServerID: UUID?,
        now: Date = Date()
    ) -> Decision {
        guard let pending else { return .idle }
        guard now.timeIntervalSince(pending.requestedAt) <= ExternalScanRequestStore.expiry else {
            return .cancel
        }
        guard isShowingMainContent else {
            return .retain(scopeTo: pending.scopedServerID ?? activeServerID)
        }
        if let scopedServerID = pending.scopedServerID, scopedServerID != activeServerID {
            return .cancel
        }
        return .deliver
    }

    /// The lifecycle events `RootView` reacts to, and what each one means for a
    /// pending external scan request.
    ///
    /// This lives here rather than inline in `RootView` so the wiring itself is
    /// testable: a test can drive the exact code the view runs, instead of
    /// asserting against `decide` or the store in isolation and passing even if
    /// the view stopped calling them (#2480).
    enum LifecycleEvent: Equatable {
        /// The scene moved to a new phase.
        case scenePhaseChanged(isBackground: Bool, isActive: Bool, isShowingMainContent: Bool)
        /// `authViewModel.isAuthenticated` changed.
        case authenticationChanged(isAuthenticated: Bool)
        /// The active server generation advanced.
        ///
        /// `previousServerID` is nil the first time a server is registered.
        case activeServerChanged(previousServerID: UUID?, newServerID: UUID?)
    }

    /// What `RootView` must do in response to a lifecycle event.
    enum LifecycleAction: Equatable {
        /// Leave the pending request alone.
        case none
        /// Abandon the pending request.
        case cancel
        /// Re-evaluate routing now — the request may have become deliverable.
        case route
    }

    /// - Returns: the action `RootView` performs for `event`.
    static func lifecycleAction(for event: LifecycleEvent) -> LifecycleAction {
        switch event {
        case .scenePhaseChanged(let isBackground, let isActive, _):
            // Backgrounding is NOT abandonment. Reaching for a password manager
            // during 2FA backgrounds the app mid-login, and cancelling there
            // destroys a legitimate in-flight request. The 10-minute expiry in
            // `decide` already covers genuine abandonment (#2480).
            if isBackground { return .none }
            return isActive ? .route : .none
        case .authenticationChanged(let isAuthenticated):
            // Signing out ends the session the request was scoped to.
            return isAuthenticated ? .route : .cancel
        case .activeServerChanged(let previousServerID, let newServerID):
            // Registering the very first server is not a switch. A request
            // retained on AddFirstServerView is exactly the flow this feature
            // exists to keep alive, so only a move away from an already-known
            // server invalidates it (#2480).
            return previousServerID != nil && previousServerID != newServerID
                ? .cancel
                : .none
        }
    }

    /// Moves a pending request created by builds that used the app's standard
    /// defaults into the App Group before app-side routing reads shared state.
    ///
    /// This type is app-target-only, so the widget extension cannot migrate its
    /// own process-local standard domain.
    static func migrateLegacyPendingRequest(
        from legacyUserDefaults: UserDefaults = .standard,
        to sharedUserDefaults: UserDefaults = ExternalScanRequestStore.sharedUserDefaults,
        now: Date = Date()
    ) {
        let legacyHasPersistedTimestamp =
            legacyUserDefaults.data(forKey: ExternalScanRequestStore.pendingKey) != nil
        guard legacyUserDefaults !== sharedUserDefaults,
              let legacyRequest = ExternalScanRequestStore.pending(
                  userDefaults: legacyUserDefaults,
                  now: now
              ) else { return }

        if let sharedRequest = ExternalScanRequestStore.pending(
            userDefaults: sharedUserDefaults,
            now: now
        ), !legacyHasPersistedTimestamp
            || sharedRequest.requestedAt >= legacyRequest.requestedAt {
            // The shared request already supersedes the legacy one. Verify it
            // remains intact before removing the stale source, otherwise it
            // could be replayed after the shared request is consumed.
            guard ExternalScanRequestStore.pending(
                userDefaults: sharedUserDefaults,
                now: now
            ) == sharedRequest else { return }
            legacyUserDefaults.removeObject(forKey: ExternalScanRequestStore.pendingKey)
            return
        }

        guard ExternalScanRequestStore.store(
            legacyRequest,
            userDefaults: sharedUserDefaults
        ), ExternalScanRequestStore.pending(
            userDefaults: sharedUserDefaults,
            now: now
        ) == legacyRequest else { return }

        legacyUserDefaults.removeObject(forKey: ExternalScanRequestStore.pendingKey)
    }

    /// App-side lifecycle entry point. Migration must happen here rather than
    /// in the shared intent source, which also compiles into the widget target.
    @MainActor
    static func applyFromApp(
        _ event: LifecycleEvent,
        router: AppRouter,
        activeServerID: UUID?,
        isShowingMainContent: Bool,
        capabilities: ResolvedSystemCapabilities,
        legacyUserDefaults: UserDefaults = .standard,
        sharedUserDefaults: UserDefaults = ExternalScanRequestStore.sharedUserDefaults,
        now: Date = Date()
    ) {
        migrateLegacyPendingRequest(
            from: legacyUserDefaults,
            to: sharedUserDefaults,
            now: now
        )
        apply(
            event,
            router: router,
            activeServerID: activeServerID,
            isShowingMainContent: isShowingMainContent,
            capabilities: capabilities,
            userDefaults: sharedUserDefaults,
            now: now
        )
    }

    /// App-side routing entry point that migrates legacy state before the first
    /// shared-store read.
    @MainActor
    static func routeFromApp(
        router: AppRouter,
        activeServerID: UUID?,
        isShowingMainContent: Bool,
        capabilities: ResolvedSystemCapabilities,
        legacyUserDefaults: UserDefaults = .standard,
        sharedUserDefaults: UserDefaults = ExternalScanRequestStore.sharedUserDefaults,
        now: Date = Date()
    ) {
        migrateLegacyPendingRequest(
            from: legacyUserDefaults,
            to: sharedUserDefaults,
            now: now
        )
        route(
            router: router,
            activeServerID: activeServerID,
            isShowingMainContent: isShowingMainContent,
            capabilities: capabilities,
            userDefaults: sharedUserDefaults,
            now: now
        )
    }

    /// Applies a lifecycle event to the persisted request and the router.
    ///
    /// `RootView` delegates every scan-related lifecycle hook here, so a test
    /// that drives this function exercises the same production code the view
    /// runs rather than a re-implementation of it (#2480).
    @MainActor
    static func apply(
        _ event: LifecycleEvent,
        router: AppRouter,
        activeServerID: UUID?,
        isShowingMainContent: Bool,
        capabilities: ResolvedSystemCapabilities,
        userDefaults: UserDefaults = ExternalScanRequestStore.sharedUserDefaults,
        now: Date = Date()
    ) {
        switch lifecycleAction(for: event) {
        case .none:
            return
        case .cancel:
            cancelPending(router: router, userDefaults: userDefaults)
        case .route:
            route(
                router: router,
                activeServerID: activeServerID,
                isShowingMainContent: isShowingMainContent,
                capabilities: capabilities,
                userDefaults: userDefaults,
                now: now
            )
        }
    }

    /// Evaluates the pending request against the current shell state and acts on
    /// the result.
    @MainActor
    static func route(
        router: AppRouter,
        activeServerID: UUID?,
        isShowingMainContent: Bool,
        capabilities: ResolvedSystemCapabilities,
        userDefaults: UserDefaults = ExternalScanRequestStore.sharedUserDefaults,
        now: Date = Date()
    ) {
        let decision = decide(
            pending: ExternalScanRequestStore.pending(userDefaults: userDefaults, now: now),
            isShowingMainContent: isShowingMainContent,
            activeServerID: activeServerID,
            now: now
        )
        switch decision {
        case .idle:
            return
        case .retain(let scopeTo):
            // Held for the login the user is in the middle of — see #2480.
            ExternalScanRequestStore.scope(to: scopeTo, userDefaults: userDefaults)
        case .cancel:
            cancelPending(router: router, userDefaults: userDefaults)
        case .deliver:
            _ = ExternalScanRequestStore.consume(userDefaults: userDefaults)
            router.navigate(to: .scan, capabilities: capabilities)
        }
    }

    /// Abandons a request that can no longer be honoured coherently: logout, a
    /// server/account switch, or expiry.
    @MainActor
    static func cancelPending(
        router: AppRouter,
        userDefaults: UserDefaults = ExternalScanRequestStore.sharedUserDefaults
    ) {
        ExternalScanRequestStore.cancel(userDefaults: userDefaults)
        router.cancelPendingExternalScan()
    }
}

struct PrintFarmerAppShortcuts: AppShortcutsProvider {
    static var appShortcuts: [AppShortcut] {
        AppShortcut(
            intent: OpenScannerIntent(),
            phrases: [
                "Scan with \(.applicationName)",
                "Open the scanner in \(.applicationName)"
            ],
            shortTitle: "Scan",
            systemImageName: "barcode.viewfinder"
        )
    }

    static let shortcutTileColor: ShortcutTileColor = .orange
}
