import SwiftUI

private struct NavigationIdentityRequest: Hashable {
    let serverID: UUID
    let generation: Int
    let endpoint: String
    let observedUserID: UUID?
    let observedRoles: [String]
    let retryRevision: Int
}

private struct ResolvedNavigationIdentity: Equatable {
    let serverID: UUID
    let endpoint: String
    let userID: UUID
    let isFarmAdmin: Bool
    let isProvisional: Bool
}

/// Adaptive operator shell for compact devices.
///
/// The compact layout renders one of two shells, chosen from farm shape and
/// operator role (see ``AdaptiveNavigationShell`` and the Navigation Shell
/// section of `mobile/README.md`):
///
/// * **Simple** — Attention · Farm · Tasks · Inventory · Oversight (hub).
///   Tasks is capability-gated on `shiftPlanEnabled`, so a server with shift
///   planning off renders four tabs (#2479).
/// * **Two modes** — a pinned Floor | Oversight control with four Floor tabs
///   (Attention · Farm · Tasks · Inventory) and five Oversight tabs
///   (Overview · Fleet · Jobs · Upkeep · Reports).
///
/// Scan is not a tab. Bin scans live inside the harvest task (Attention),
/// spool scans live on the Inventory nav bar, and the Offline Queue is
/// reachable from the account/You area.
///
/// The regular-width iPad layout uses `NavigationSplitView` for the sidebar
/// instead of a tab bar.
///
/// Adaptive-shell configuration is gated behind endpoint-fenced navigation
/// identity resolution so that a server switch or a role change never
/// races an in-flight shell derivation.
struct ContentView: View {
    static let sidebarRowMinimumHeight: CGFloat = 44
    static let modeControlMinimumHeight = RootNavigationChrome.minimumTouchTarget
    /// Minimum spacing between foreground farm-shape refreshes (#2478).
    static let farmShapeRefreshInterval: TimeInterval = 300

    static func shippingTabs(
        for capabilities: ResolvedSystemCapabilities
    ) -> [AppTab] {
        AppTab.visibleTabs(
            for: .current,
            mode: .floor,
            capabilities: capabilities
        )
    }

    @Environment(AuthViewModel.self) private var authViewModel
    @Environment(AppRouter.self) private var router
    @Environment(ServiceContainer.self) private var services
    @Environment(ServerRegistry.self) private var serverRegistry
    @Environment(\.horizontalSizeClass) private var sizeClass
    @Environment(\.scenePhase) private var scenePhase
    @State private var navigationIdentity: ResolvedNavigationIdentity?
    @State private var navigationIdentityError: String?
    @State private var navigationIdentityRetryRevision = 0
    @State private var lastFarmShapeRefresh: Date?

    var body: some View {
        let capabilities = services.capabilitiesService.resolved
        let oversightAvailability = currentOversightAvailability(
            capabilities: capabilities
        )

        Group {
            if isAdaptiveShellReady {
                if sizeClass == .regular {
                    iPadLayout(capabilities: capabilities)
                } else {
                    compactLayout(capabilities: capabilities)
                }
            } else if let navigationIdentityError {
                navigationIdentityFailure(navigationIdentityError)
            } else {
                ProgressView()
                    .accessibilityLabel("Preparing navigation")
                    .accessibilityIdentifier("navigation.shellLoading")
                    .accessibilityAddTraits(.updatesFrequently)
            }
        }
        .onAppear {
            synchronizeAdaptiveShell(
                capabilities: capabilities,
                oversightAvailability: oversightAvailability
            )
        }
        .onChange(of: capabilities) { _, newCapabilities in
            synchronizeAdaptiveShell(
                capabilities: newCapabilities,
                oversightAvailability: currentOversightAvailability(
                    capabilities: newCapabilities
                )
            )
        }
        .onChange(of: services.farmShapeService.sessionShape) {
            synchronizeAdaptiveShell(
                capabilities: capabilities,
                oversightAvailability: oversightAvailability
            )
        }
        .onChange(of: services.farmShapeService.latestShape) {
            synchronizeAdaptiveShell(
                capabilities: capabilities,
                oversightAvailability: oversightAvailability
            )
        }
        .onChange(of: serverRegistry.navigationLayoutPreference) {
            synchronizeAdaptiveShell(
                capabilities: capabilities,
                oversightAvailability: oversightAvailability
            )
        }
        .task(id: navigationIdentityRequest) {
            guard let request = navigationIdentityRequest else {
                navigationIdentity = nil
                return
            }
            await resolveNavigationIdentity(request)
        }
        .onChange(of: sizeClass) {
            synchronizeAdaptiveShell(
                capabilities: capabilities,
                oversightAvailability: oversightAvailability
            )
        }
        .onChange(of: scenePhase) { _, newPhase in
            guard newPhase == .active else { return }
            refreshLatestFarmShapeIfDue()
        }
    }

    /// Foreground refresh of the *latest* observed farm shape (#2478).
    ///
    /// This never touches the session shape, so it cannot move the live shell:
    /// its only job is to let a farm that grew while the app was backgrounded
    /// reach the in-context upgrade offer instead of waiting for the next
    /// startup fetch to lose a deadline race. It is throttled to at most one
    /// request per ``farmShapeRefreshInterval`` per foregrounding, so it never
    /// becomes a poll.
    private func refreshLatestFarmShapeIfDue() {
        guard let activeServer = serverRegistry.activeServer,
              let navigationIdentity,
              navigationIdentity.serverID == activeServer.id,
              navigationIdentity.endpoint == activeServer.normalizedURLString,
              !navigationIdentity.isProvisional else {
            return
        }

        let now = Date()
        if let lastFarmShapeRefresh,
           now.timeIntervalSince(lastFarmShapeRefresh) < Self.farmShapeRefreshInterval {
            return
        }
        lastFarmShapeRefresh = now

        let serverID = activeServer.id
        Task {
            await services.farmShapeService.refreshLatest(serverID: serverID)
        }
    }

    // MARK: - Compact (iPhone)

    /// The compact shell owns tab selection only. The Floor/Oversight control
    /// is rendered exactly once per root screen by `RootNavigationChrome`
    /// inside that root's own `NavigationStack` (#2481), so the shell must not
    /// render a second copy above the tab view.
    private func compactLayout(
        capabilities: ResolvedSystemCapabilities
    ) -> some View {
        let selection = Binding(
            get: { router.resolvedTab(for: capabilities) },
            set: { router.selectTab($0, capabilities: capabilities) }
        )
        let tabs = router.visibleTabs(for: capabilities)

        return compactTabView(selection: selection, tabs: tabs)
    }

    @ViewBuilder
    private func compactTabView(
        selection: Binding<AppTab>,
        tabs: [AppTab]
    ) -> some View {
        Group {
            if #available(iOS 18.0, *) {
                TabView(selection: selection) {
                    ForEach(tabs, id: \.self) { tab in
                        Tab(value: tab) {
                            tabContentView(for: tab)
                        } label: {
                            Label(tab.title, systemImage: tab.systemImage)
                                .accessibilityIdentifier(tab.tabAccessibilityIdentifier)
                        }
                        .badge(badgeCount(for: tab))
                    }
                }
            } else {
                TabView(selection: selection) {
                    ForEach(tabs, id: \.self) { tab in
                        tabContentView(for: tab)
                            .tabItem {
                                Label(tab.title, systemImage: tab.systemImage)
                                    .accessibilityIdentifier(tab.tabAccessibilityIdentifier)
                            }
                            .tag(tab)
                            .badge(badgeCount(for: tab))
                    }
                }
            }
        }
    }

    // MARK: - Regular (iPad)

    private func iPadLayout(capabilities: ResolvedSystemCapabilities) -> some View {
        @Bindable var router = router
        let resolvedTab = resolvedShippingTab(for: capabilities)

        return NavigationSplitView(columnVisibility: $router.sidebarVisibility) {
            List {
                ForEach(SidebarSection.allCases, id: \.self) { section in
                    let tabs = router.visibleTabs(
                        in: section,
                        for: capabilities
                    )
                    if !tabs.isEmpty {
                        Section {
                            ForEach(tabs, id: \.self) { tab in
                                sidebarButton(
                                    tab: tab,
                                    capabilities: capabilities
                                )
                            }
                        } header: {
                            Text(section.title)
                                .accessibilityIdentifier(
                                    "sidebar.section.\(section.rawValue)"
                                )
                        }
                    }
                }
            }
            .listStyle(.sidebar)
            .navigationTitle("PrintFarmer")
        } detail: {
            tabContentView(for: resolvedTab)
        }
        .navigationSplitViewStyle(.balanced)
    }

    private func sidebarButton(
        tab: AppTab,
        capabilities: ResolvedSystemCapabilities
    ) -> some View {
        let isSelected = resolvedShippingTab(for: capabilities) == tab
        let badgeCount = badgeCount(for: tab)

        return Button {
            selectShippingTab(tab, capabilities: capabilities)
        } label: {
            HStack {
                Label(tab.title, systemImage: tab.systemImage)
                Spacer()
                if badgeCount > 0 {
                    Text("\(badgeCount)")
                        .font(.caption2.weight(.bold))
                        .padding(.horizontal, 6)
                        .padding(.vertical, 2)
                        .background(sidebarBadgeColor(for: tab), in: Capsule())
                        .foregroundStyle(.white)
                        .accessibilityHidden(true)
                }
            }
            .frame(minHeight: Self.sidebarRowMinimumHeight)
        }
        .listRowBackground(isSelected ? Color.accentColor.opacity(0.15) : nil)
        .foregroundStyle(isSelected ? Color.accentColor : .primary)
        .accessibilityLabel(sidebarAccessibilityLabel(for: tab))
        .accessibilityHint("Opens the \(tab.title) destination.")
        .accessibilityAddTraits(isSelected ? [.isButton, .isSelected] : .isButton)
        .accessibilityIdentifier(tab.sidebarAccessibilityIdentifier)
    }

    private func resolvedShippingTab(
        for capabilities: ResolvedSystemCapabilities
    ) -> AppTab {
        router.resolvedTab(for: capabilities)
    }

    private func selectShippingTab(
        _ tab: AppTab,
        capabilities: ResolvedSystemCapabilities
    ) {
        router.selectTab(tab, capabilities: capabilities)
    }

    private func badgeCount(for tab: AppTab) -> Int {
        switch tab.badgeKind {
        case .notifications:
            router.notificationBadgeCount
        case .pendingReady:
            router.pendingReadyCount
        case .none:
            0
        }
    }

    private func sidebarBadgeColor(for tab: AppTab) -> Color {
        tab.badgeKind == .pendingReady ? Color.pfWarningFill : Color.pfErrorFill
    }

    private func sidebarAccessibilityLabel(for tab: AppTab) -> String {
        switch tab.badgeKind {
        case .notifications where router.notificationBadgeCount > 0:
            "Attention, \(router.notificationBadgeCount) unread"
        case .pendingReady where router.pendingReadyCount > 0:
            "Farm, \(router.pendingReadyCount) ready"
        case .none, .notifications, .pendingReady:
            tab.title
        }
    }

    private func synchronizeAdaptiveShell(
        capabilities: ResolvedSystemCapabilities,
        oversightAvailability: OversightNavigationAvailability,
        preserveNavigationOnIdentityUpgrade: Bool = false
    ) {
        guard let activeServer = serverRegistry.activeServer,
              let navigationIdentity,
              navigationIdentity.serverID == activeServer.id,
              navigationIdentity.endpoint == activeServer.normalizedURLString else {
            router.reconcileCapabilities(
                capabilities,
                oversightAvailability: oversightAvailability
            )
            return
        }

        router.setExpandedSidebarPresentation(
            sizeClass == .regular,
            capabilities: capabilities,
            oversightAvailability: oversightAvailability
        )

        // Read the established latch BEFORE observing the offer: observing
        // records the freshly grown shape as the new baseline, and the latch's
        // pre-latch fallback derives from that same baseline. Reading after
        // would derive `.twoModes` from the very growth the offer is about to
        // ask the user about (#2478).
        let persistedAutomaticShell = serverRegistry.establishedAutomaticShell(
            for: activeServer.id,
            isFarmAdmin: navigationIdentity.isFarmAdmin
        )

        _ = serverRegistry.observeOversightUpgradeOffer(
            farmShape: services.farmShapeService.latestShape,
            shiftPlanEnabled: capabilities.shiftPlanEnabled,
            isFarmAdmin: navigationIdentity.isFarmAdmin
        )

        let preference: NavigationLayoutPreference = navigationIdentity.isProvisional
            ? .simple
            : serverRegistry.navigationLayoutPreference

        router.configureAdaptiveShell(
            serverID: activeServer.id,
            userID: navigationIdentity.userID,
            preference: preference,
            farmShape: services.farmShapeService.sessionShape,
            isFarmAdmin: navigationIdentity.isFarmAdmin,
            capabilities: capabilities,
            oversightAvailability: oversightAvailability,
            persistedAutomaticShell: persistedAutomaticShell,
            preserveNavigationOnContextChange: preserveNavigationOnIdentityUpgrade
        )

        // Latch the Automatic layout this session settled on so later farm
        // growth can only offer Two modes, never impose it (#2478).
        if preference == .automatic {
            if let establishedShell = router.establishedAutomaticShell {
                serverRegistry.recordEstablishedAutomaticShell(
                    establishedShell,
                    for: activeServer.id
                )
            } else if router.establishedAutomaticDerivation?.cause.contradictsLatch == true {
                // A role or capability reading has contradicted whatever was
                // stored, so drop it instead of letting a stale Simple outrank
                // the Two modes this account earns back on promotion (#2478).
                serverRegistry.clearEstablishedAutomaticShell(for: activeServer.id)
            }
        }
        if UITestBootstrap.isEnabled,
           UITestBootstrap.startsInOversightMode {
            router.setNavigationMode(
                .oversight,
                capabilities: capabilities
            )
        }
    }

    private var navigationIdentityRequest: NavigationIdentityRequest? {
        guard let activeServer = serverRegistry.activeServer else { return nil }
        return NavigationIdentityRequest(
            serverID: activeServer.id,
            generation: services.activeServerGeneration,
            endpoint: activeServer.normalizedURLString,
            observedUserID: authViewModel.currentUser?.id,
            observedRoles: authViewModel.currentUser?.roles ?? [],
            retryRevision: navigationIdentityRetryRevision
        )
    }

    private func resolveNavigationIdentity(_ request: NavigationIdentityRequest) async {
        navigationIdentity = nil
        navigationIdentityError = nil
        let provisionalUserID = UUID()
        var retryDelayMilliseconds = 250
        var unsettledMilliseconds = 0

        while !Task.isCancelled, navigationIdentityRequest == request {
            let resolution = await services.currentUserForNavigation(
                serverID: request.serverID,
                generation: request.generation,
                expectedEndpoint: request.endpoint
            )
            guard !Task.isCancelled,
                  navigationIdentityRequest == request else {
                return
            }

            switch resolution {
            case .verified(let identity):
                let preservesProvisionalNavigation =
                    navigationIdentity?.serverID == request.serverID
                    && navigationIdentity?.endpoint == request.endpoint
                    && navigationIdentity?.isProvisional == true
                navigationIdentityError = nil
                navigationIdentity = ResolvedNavigationIdentity(
                    serverID: request.serverID,
                    endpoint: request.endpoint,
                    userID: identity.userID,
                    isFarmAdmin: identity.roles.contains("farm_admin"),
                    isProvisional: false
                )
                synchronizeAdaptiveShell(
                    capabilities: services.capabilitiesService.resolved,
                    oversightAvailability: currentOversightAvailability(
                        capabilities: services.capabilitiesService.resolved
                    ),
                    preserveNavigationOnIdentityUpgrade: preservesProvisionalNavigation
                )
                return
            case .offline:
                unsettledMilliseconds = 0
                navigationIdentityError = nil
                if navigationIdentity == nil {
                    navigationIdentity = ResolvedNavigationIdentity(
                        serverID: request.serverID,
                        endpoint: request.endpoint,
                        userID: provisionalUserID,
                        isFarmAdmin: false,
                        isProvisional: true
                    )
                    synchronizeAdaptiveShell(
                        capabilities: services.capabilitiesService.resolved,
                        oversightAvailability: currentOversightAvailability(
                            capabilities: services.capabilitiesService.resolved
                        )
                    )
                }
            case .notSettled:
                navigationIdentity = nil
                unsettledMilliseconds += retryDelayMilliseconds
                if unsettledMilliseconds >= 8_000 {
                    navigationIdentityError = "The selected server is still switching. Try again or sign out."
                }
            case .failed(let message):
                navigationIdentity = nil
                navigationIdentityError = message
            }

            do {
                try await Task.sleep(for: .milliseconds(retryDelayMilliseconds))
            } catch {
                return
            }
            retryDelayMilliseconds = min(retryDelayMilliseconds * 2, 8_000)
        }
    }

    private var isAdaptiveShellReady: Bool {
        guard let activeServer = serverRegistry.activeServer,
              let navigationIdentity,
              navigationIdentity.serverID == activeServer.id,
              navigationIdentity.endpoint == activeServer.normalizedURLString else {
            return false
        }
        return router.hasAdaptiveShellConfiguration(
            serverID: activeServer.id,
            userID: navigationIdentity.userID
        )
    }

    private func navigationIdentityFailure(_ message: String) -> some View {
        ContentUnavailableView {
            Label("Navigation Unavailable", systemImage: "person.crop.circle.badge.exclamationmark")
        } description: {
            Text(message)
        } actions: {
            Button("Try Again") {
                navigationIdentityRetryRevision &+= 1
            }
            .frame(minHeight: Self.modeControlMinimumHeight)
            .accessibilityIdentifier("navigation.identityRetry")

            Button("Sign Out", role: .destructive) {
                Task {
                    await authViewModel.logout()
                }
            }
            .frame(minHeight: Self.modeControlMinimumHeight)
            .accessibilityIdentifier("navigation.identitySignOut")
        }
        .accessibilityElement(children: .contain)
        .accessibilityAddTraits(.updatesFrequently)
        .accessibilityIdentifier("navigation.identityUnavailable")
    }

    private func currentOversightAvailability(
        capabilities: ResolvedSystemCapabilities
    ) -> OversightNavigationAvailability {
        OversightNavigationAvailability(
            hasVisibleHubDestinations: OversightCatalog.hasVisibleDestinations(
                capabilities: capabilities
            ),
            visibleTabs: OversightCatalog.visibleTabs(
                capabilities: capabilities
            )
        )
    }

    @ViewBuilder
    private func tabContentView(for tab: AppTab) -> some View {
        switch tab {
        case .attention:
            AttentionView()
        case .farm:
            PrinterListView()
        case .tasks:
            ShiftTasksView()
        case .inventory:
            InventoryView()
        case .oversight, .overview, .fleet, .jobs, .upkeep, .reports:
            oversightTabContentView(for: tab)
        }
    }
}
