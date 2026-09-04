import SwiftUI

private struct NavigationIdentityRequest: Hashable {
    let serverID: UUID
    let generation: Int
    let fallbackUserID: UUID?
    let fallbackRoles: [String]
}

private struct ResolvedNavigationIdentity: Equatable {
    let serverID: UUID
    let userID: UUID
    let isFarmAdmin: Bool
}

/// Adaptive operator shell for compact devices, with the existing split view
/// retained until the dedicated iPad navigation work lands.
struct ContentView: View {
    static let sidebarRowMinimumHeight: CGFloat = 44
    static let modeControlMinimumHeight: CGFloat = 44

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
    @State private var navigationIdentity: ResolvedNavigationIdentity?

    var body: some View {
        let capabilities = services.capabilitiesService.resolved
        let oversightAvailability = currentOversightAvailability(
            capabilities: capabilities
        )

        Group {
            if sizeClass == .regular {
                iPadLayout(capabilities: capabilities)
            } else if isCompactShellReady {
                compactLayout(capabilities: capabilities)
            } else {
                ProgressView()
                    .accessibilityLabel("Preparing navigation")
                    .accessibilityIdentifier("navigation.shellLoading")
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
    }

    // MARK: - Compact (iPhone)

    private func compactLayout(
        capabilities: ResolvedSystemCapabilities
    ) -> some View {
        let selection = Binding(
            get: { router.resolvedTab(for: capabilities) },
            set: { router.selectTab($0, capabilities: capabilities) }
        )
        let tabs = router.visibleTabs(for: capabilities)

        return TabView(selection: selection) {
            ForEach(tabs, id: \.self) { tab in
                tabContentView(for: tab)
                    .safeAreaInset(edge: .top, spacing: 0) {
                        if router.shouldShowModeControl(for: tab) {
                            modeControl(capabilities: capabilities)
                        }
                    }
                    .tabItem {
                        Label(tab.title, systemImage: tab.systemImage)
                    }
                    .tag(tab)
                    .badge(badgeCount(for: tab))
                    .accessibilityIdentifier(tab.tabAccessibilityIdentifier)
            }
        }
    }

    private func modeControl(
        capabilities: ResolvedSystemCapabilities
    ) -> some View {
        Picker(
            "Navigation mode",
            selection: Binding(
                get: { router.activeMode },
                set: { router.setNavigationMode($0, capabilities: capabilities) }
            )
        ) {
            Text("Floor").tag(OversightMode.floor)
            Text("Oversight").tag(OversightMode.oversight)
        }
        .pickerStyle(.segmented)
        .frame(minHeight: Self.modeControlMinimumHeight)
        .padding(.horizontal)
        .background(.bar)
        .overlay(alignment: .bottom) {
            Divider()
        }
        .accessibilityLabel("Navigation mode")
        .accessibilityHint("Switches between Floor work and Oversight.")
        .accessibilityIdentifier("navigation.modeControl")
    }

    // MARK: - Regular (iPad)

    private func iPadLayout(capabilities: ResolvedSystemCapabilities) -> some View {
        @Bindable var router = router
        let tabs = Self.shippingTabs(for: capabilities)

        return NavigationSplitView(columnVisibility: $router.sidebarVisibility) {
            List {
                Section {
                    ForEach(tabs, id: \.self) { tab in
                        sidebarButton(
                            tab: tab,
                            capabilities: capabilities
                        )
                    }
                } header: {
                    Text("Operator")
                }
            }
            .listStyle(.sidebar)
            .navigationTitle("PrintFarmer")
        } detail: {
            tabContentView(for: resolvedShippingTab(for: capabilities))
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
        let tabs = Self.shippingTabs(for: capabilities)
        return tabs.contains(router.selectedTab)
            ? router.selectedTab
            : AppTab.fallbackTab(
                for: .current,
                mode: .floor,
                capabilities: capabilities
            )
    }

    private func selectShippingTab(
        _ tab: AppTab,
        capabilities: ResolvedSystemCapabilities
    ) {
        let tabs = Self.shippingTabs(for: capabilities)
        router.selectedTab = tabs.contains(tab)
            ? tab
            : AppTab.fallbackTab(
                for: .current,
                mode: .floor,
                capabilities: capabilities
            )
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
        tab.badgeKind == .pendingReady ? Color.pfWarning : Color.red
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
        oversightAvailability: OversightNavigationAvailability
    ) {
        if sizeClass == .regular {
            router.presentShippingShell(capabilities: capabilities)
            return
        }

        guard let serverID = serverRegistry.activeServerID,
              let navigationIdentity,
              navigationIdentity.serverID == serverID else {
            router.reconcileCapabilities(
                capabilities,
                oversightAvailability: oversightAvailability
            )
            return
        }

        router.configureAdaptiveShell(
            serverID: serverID,
            userID: navigationIdentity.userID,
            preference: serverRegistry.navigationLayoutPreference,
            farmShape: services.farmShapeService.sessionShape,
            isFarmAdmin: navigationIdentity.isFarmAdmin,
            capabilities: capabilities,
            oversightAvailability: oversightAvailability
        )
    }

    private var navigationIdentityRequest: NavigationIdentityRequest? {
        guard let serverID = serverRegistry.activeServerID else { return nil }
        return NavigationIdentityRequest(
            serverID: serverID,
            generation: services.activeServerGeneration,
            fallbackUserID: authViewModel.currentUser?.id,
            fallbackRoles: authViewModel.currentUser?.roles ?? []
        )
    }

    private func resolveNavigationIdentity(_ request: NavigationIdentityRequest) async {
        navigationIdentity = nil
        let verifiedUser = await services.currentUserForNavigation(
            serverID: request.serverID,
            generation: request.generation
        )
        guard !Task.isCancelled,
              navigationIdentityRequest == request else {
            return
        }

        navigationIdentity = ResolvedNavigationIdentity(
            serverID: request.serverID,
            userID: verifiedUser?.id ?? request.fallbackUserID ?? request.serverID,
            isFarmAdmin: verifiedUser?.roles.contains("farm_admin") == true
        )
        synchronizeAdaptiveShell(
            capabilities: services.capabilitiesService.resolved,
            oversightAvailability: currentOversightAvailability(
                capabilities: services.capabilitiesService.resolved
            )
        )
    }

    private var isCompactShellReady: Bool {
        guard let serverID = serverRegistry.activeServerID,
              let navigationIdentity,
              navigationIdentity.serverID == serverID else {
            return false
        }
        return router.hasAdaptiveShellConfiguration(
            serverID: serverID,
            userID: navigationIdentity.userID
        )
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
        case .scan:
            ScanView()
        case .inventory:
            InventoryView()
        case .oversight, .overview, .fleet, .jobs, .upkeep, .reports:
            oversightTabContentView(for: tab)
        }
    }
}
