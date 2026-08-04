import SwiftUI

/// Root view that gates between loading, login, and main content.
///
/// Extracted from `PFarmApp` so that `@Observable` property tracking
/// on `AuthViewModel` runs inside a real `View` body — this ensures
/// SwiftUI reliably re-renders when `isAuthenticated` changes.
struct RootView: View {
    @Environment(AuthViewModel.self) private var authViewModel
    @Environment(AppRouter.self) private var router
    @Environment(ServerRegistry.self) private var serverRegistry
    @Environment(ServiceContainer.self) private var services
    @Environment(\.scenePhase) private var scenePhase
    @State private var pendingReadyMonitor = PendingReadyMonitor()
    @State private var connectionMonitor = ConnectionMonitor()
    @AppStorage("hasSeenOnboarding") private var hasSeenOnboarding = false
    @AppStorage("hasCompletedNetworkPermission") private var hasCompletedNetworkPermission = false
    @State private var minimumSplashElapsed = false
    @State private var disconnectTask: Task<Void, Never>?
    @State private var staleRegistrySignOutTask: Task<Void, Never>?

    var body: some View {
        VStack(spacing: 0) {
            if DemoMode.shared.isActive && authViewModel.isAuthenticated {
                DemoModeBanner()
            }

            if authViewModel.isAuthenticated && !DemoMode.shared.isActive && isShowingMainContent {
                ConnectionStatusBar(monitor: connectionMonitor)
            }

            Group {
                if !authViewModel.hasCheckedAuth || !minimumSplashElapsed {
                    launchScreen
                        .task {
                            try? await Task.sleep(for: .seconds(1.5))
                            minimumSplashElapsed = true
                        }
                } else if serverRegistry.servers.isEmpty || serverRegistry.activeServerID == nil {
                    AddFirstServerView()
                        .task {
                            await authViewModel.logoutIfServerRegistryUnavailable(serverRegistry)
                        }
                } else if authViewModel.isAuthenticated {
                    // D (issue #816 reject): the root MUST consume `snapshotActivationPending`
                    // and visibly gate/offer retry rather than silently entering
                    // ContentView on `isAuthenticated` alone. If the snapshot did not
                    // activate (e.g. startup preparation failed), show an accessible
                    // retry-or-sign-out surface instead of the main app shell.
                    if authViewModel.snapshotActivationPending {
                        SnapshotActivationPendingView()
                    } else {
                        ContentView()
                            .id(services.activeServerGeneration)
                            .task(id: services.activeServerGeneration) {
                                if !UITestBootstrap.isEnabled {
                                    pendingReadyMonitor.configure(
                                        autoPrintService: services.autoPrintService,
                                        printerService: services.printerService
                                    )
                                    await pendingReadyMonitor.requestNotificationPermission()
                                    pendingReadyMonitor.startMonitoring()
                                }
                                do {
                                    try await services.signalRService.connect()
                                } catch {
                                    // SignalR will auto-reconnect; log silently
                                }
                                connectionMonitor.configure(
                                    apiClient: services.apiClient,
                                    signalRService: services.signalRService
                                )
                                connectionMonitor.start()
                            }
                            .onChange(of: pendingReadyMonitor.pendingReadyCount) { _, newValue in
                                router.pendingReadyCount = newValue
                            }
                    }
                } else if !hasSeenOnboarding {
                    OnboardingView(hasSeenOnboarding: $hasSeenOnboarding)
                } else if !hasCompletedNetworkPermission {
                    LocalNetworkPermissionView(hasCompletedNetworkPermission: $hasCompletedNetworkPermission)
                } else {
                    LoginView()
                }
            }
        }
        .onChange(of: scenePhase) { _, newPhase in
            guard newPhase == .active else { return }
            resumeConnectivityAfterForeground()
        }
        .onChange(of: authViewModel.isAuthenticated) { _, isAuthenticated in
            if !isAuthenticated {
                pendingReadyMonitor.stopMonitoring()
                connectionMonitor.stop()
                router.pendingReadyCount = 0
                disconnectTask = Task { await services.signalRService.disconnect() }
            }
        }
        .onChange(of: services.activeServerGeneration) {
            pendingReadyMonitor.stopMonitoring()
            connectionMonitor.stop()
            router.pendingReadyCount = 0
        }
        .onChange(of: serverRegistry.servers.isEmpty) { _, isEmpty in
            guard isEmpty else { return }
            signOutIfServerRegistryUnavailable()
        }
        .onChange(of: serverRegistry.activeServerID) { _, activeServerID in
            guard activeServerID == nil else { return }
            signOutIfServerRegistryUnavailable()
        }
        .onDisappear {
            connectionMonitor.stop()
            disconnectTask?.cancel()
            staleRegistrySignOutTask?.cancel()
        }
    }

    /// Re-arms live connectivity when the app returns to the foreground.
    ///
    /// iOS suspends the app in the background: the SignalR WebSocket is torn
    /// down by the system and the connectivity poll loop stops advancing. With
    /// nothing observing `scenePhase`, the first thing the user saw on re-open
    /// was whatever stale sample the monitor last took — typically a failed one,
    /// hence the red offline banner that only a manual pull-to-refresh cleared.
    ///
    /// The actual recovery sequence lives on ``ConnectionMonitor`` because the
    /// network-path observer triggers the identical sequence; this hook only
    /// decides *whether* the app is in a state where resuming makes sense.
    @MainActor
    private func resumeConnectivityAfterForeground() {
        guard isShowingMainContent, !DemoMode.shared.isActive else { return }
        connectionMonitor.requestResume()
    }

    /// True only when the authenticated `ContentView` shell is actually on
    /// screen — i.e. auth checked, splash elapsed, a server is selected, AND
    /// the farm-snapshot activation completed (D: the pending-retry screen must
    /// NOT show the connection bar because ContentView isn't mounted).
    private var isShowingMainContent: Bool {
        authViewModel.hasCheckedAuth
            && minimumSplashElapsed
            && !serverRegistry.servers.isEmpty
            && serverRegistry.activeServerID != nil
            && authViewModel.isAuthenticated
            && !authViewModel.snapshotActivationPending
    }

    /// Shown briefly while `restoreSession()` checks for a saved token.
    private var launchScreen: some View {
        VStack(spacing: 16) {
            Image("AppLogo")
                .resizable()
                .scaledToFit()
                .frame(width: 56, height: 56)
                .clipShape(RoundedRectangle(cornerRadius: 12))

            Text("PrintFarmer")
                .font(.largeTitle.bold())
                .foregroundStyle(Color("LaunchText"))

            ProgressView()
                .padding(.top, 8)
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .background(Color("LaunchBackground"))
    }

    private func signOutIfServerRegistryUnavailable() {
        staleRegistrySignOutTask?.cancel()
        staleRegistrySignOutTask = Task {
            await authViewModel.logoutIfServerRegistryUnavailable(serverRegistry)
        }
    }
}
