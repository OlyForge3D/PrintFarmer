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
        userDefaults: UserDefaults = .standard,
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
        userDefaults: UserDefaults = .standard,
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
        userDefaults: UserDefaults = .standard
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
