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
    @State private var connectionGate = BackendConnectionGate()
    @AppStorage("hasSeenOnboarding") private var hasSeenOnboarding = false
    @AppStorage("hasCompletedNetworkPermission") private var hasCompletedNetworkPermission = false
    @State private var minimumSplashElapsed = false
    @State private var disconnectTask: Task<Void, Never>?
    @State private var staleRegistrySignOutTask: Task<Void, Never>?
    @State private var certificateTrustPresentation = CertificateTrustPresentation.shared

    var body: some View {
        VStack(spacing: 0) {
            if DemoMode.shared.isActive && authViewModel.isAuthenticated {
                DemoModeBanner()
            }

            if authViewModel.isAuthenticated && !DemoMode.shared.isActive && isShowingMainContent
                && connectionMonitor.isReportable {
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
                    if authViewModel.isLoading {
                        BackendConnectionCheckView(
                            isChecking: true,
                            statusText: "Connecting to services..."
                        )
                    } else if authViewModel.snapshotActivationPending {
                        SnapshotActivationPendingView()
                    } else if shouldBypassConnectionGate || connectionGate.allowsMainContent {
                        mainContent
                    } else {
                        connectionCheck
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
            routePendingExternalScan()
            resumeConnectivityAfterForeground()
        }
        .onReceive(NotificationCenter.default.publisher(for: .externalScanRequested)) { _ in
            routePendingExternalScan()
        }
        .task {
            routePendingExternalScan()
        }
        .onChange(of: authViewModel.isAuthenticated) { _, isAuthenticated in
            if !isAuthenticated {
                pendingReadyMonitor.stopMonitoring()
                connectionMonitor.stop()
                connectionGate.reset()
                router.pendingReadyCount = 0
                router.resetAdaptiveShellSession()
                disconnectTask = Task { await services.signalRService.disconnect() }
            }
        }
        .onChange(of: services.activeServerGeneration) {
            certificateTrustPresentation.respond(accepted: false)
            Task { await CertificateTrustCoordinator.shared.cancelPendingConfirmations() }
            pendingReadyMonitor.stopMonitoring()
            connectionMonitor.stop()
            connectionGate.reset()
            router.pendingReadyCount = 0
            router.invalidatePendingNavigation()
        }
        .onChange(of: DemoMode.shared.isActive) {
            connectionGate.reset()
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
            connectionGate.reset()
            disconnectTask?.cancel()
            staleRegistrySignOutTask?.cancel()
        }
        .sheet(
            item: Binding(
                get: { certificateTrustPresentation.request },
                set: { request in
                    if request == nil {
                        certificateTrustPresentation.respond(accepted: false)
                    }
                }
            )
        ) { request in
            CertificateTrustView(request: request) { accepted in
                certificateTrustPresentation.respond(accepted: accepted)
            }
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
            && !authViewModel.isLoading
            && !authViewModel.snapshotActivationPending
            && (shouldBypassConnectionGate || connectionGate.allowsMainContent)
    }

    private var shouldBypassConnectionGate: Bool {
        DemoMode.shared.isActive || UITestBootstrap.isEnabled
    }

    private var mainContent: some View {
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
                connectionMonitor.configure(
                    apiClient: services.apiClient,
                    signalRService: services.signalRService
                )
                connectionMonitor.start()
                await services.signalRService.ensureConnected()
            }
            .onChange(of: pendingReadyMonitor.pendingReadyCount) { _, newValue in
                router.pendingReadyCount = newValue
            }
    }

    private var connectionCheck: some View {
        BackendConnectionCheckView(
            isChecking: connectionGate.isChecking,
            statusText: connectionGate.failures == nil
                ? "Connecting to services..."
                : connectionGate.failureTitle
        )
            .task(id: "\(services.activeServerGeneration):\(connectionGate.retryRevision)") {
                let generation = services.activeServerGeneration
                let plan = BackendReadinessPlan(services: services)
                await connectionGate.check(
                    plan: plan,
                    generation: generation
                ) {
                    authViewModel.isAuthenticated
                        && !authViewModel.isLoading
                        && !authViewModel.snapshotActivationPending
                        && services.activeServerGeneration == generation
                }
            }
            .alert(
                connectionGate.failureTitle,
                isPresented: Binding(
                    get: { connectionGate.failures != nil },
                    set: { isPresented in
                        if !isPresented {
                            connectionGate.continueOffline()
                        }
                    }
                )
            ) {
                Button("Try Again") {
                    connectionGate.retry()
                }
                Button("Continue Offline") {
                    connectionGate.continueOffline()
                }
            } message: {
                Text(
                    connectionGate.failureMessage
                        ?? "You can continue with cached data and available services."
                )
            }
    }

    /// Shown while `restoreSession()` checks for a saved token. Same component
    /// as every later startup phase so the launch reads as one screen.
    private var launchScreen: some View {
        LaunchSplashView(
            statusText: nil,
            detailText: nil,
            busyAccessibilityLabel: "Starting PrintFarmer"
        )
    }

    private func signOutIfServerRegistryUnavailable() {
        staleRegistrySignOutTask?.cancel()
        staleRegistrySignOutTask = Task {
            await authViewModel.logoutIfServerRegistryUnavailable(serverRegistry)
        }
    }

    private func routePendingExternalScan() {
        guard ExternalScanRequestStore.consume() else { return }
        router.navigate(
            to: .scan,
            capabilities: services.capabilitiesService.resolved
        )
    }

    private struct BackendConnectionCheckView: View {
        let isChecking: Bool
        let statusText: String

        var body: some View {
            LaunchSplashView(
                statusText: statusText,
                detailText: isChecking
                    ? "Preparing your farm and checking each enabled mobile feature."
                    : "Try again, or continue with cached data and available services.",
                isBusy: isChecking,
                busyAccessibilityLabel: "Connecting to backend services"
            )
            .accessibilityIdentifier("backendConnectionGate")
        }
    }
}
