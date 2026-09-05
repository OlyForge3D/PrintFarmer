import SwiftUI
#if canImport(UIKit)
import UIKit
#endif

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
/// * **Simple** — four tabs: Attention · Farm · Inventory · Oversight (hub).
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
    @State private var navigationIdentityError: String?
    @State private var navigationIdentityRetryRevision = 0

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

        return VStack(spacing: 0) {
            if router.shouldShowModeControl(for: router.resolvedTab(for: capabilities)) {
                modeControl(capabilities: capabilities)
            }

            compactTabView(selection: selection, tabs: tabs)
        }
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
                        }
                        .badge(badgeCount(for: tab))
                        .accessibilityIdentifier(tab.tabAccessibilityIdentifier)
                    }
                }
            } else {
                TabView(selection: selection) {
                    ForEach(tabs, id: \.self) { tab in
                        tabContentView(for: tab)
                            .tabItem {
                                Label(tab.title, systemImage: tab.systemImage)
                            }
                            .tag(tab)
                            .badge(badgeCount(for: tab))
                    }
                }
            }
        }
        #if canImport(UIKit)
        .background {
            TabBarAccessibilityIdentifierBridge(
                identifiers: tabs.map(\.tabAccessibilityIdentifier)
            )
            .frame(width: 0, height: 0)
        }
        #endif
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
        let tabs = router.visibleTabs(for: capabilities)
        let resolvedTab = resolvedShippingTab(for: capabilities)

        return NavigationSplitView(columnVisibility: $router.sidebarVisibility) {
            VStack(spacing: 0) {
                if router.shouldShowModeControl(for: resolvedTab) {
                    modeControl(capabilities: capabilities)
                }
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
            }
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

        _ = serverRegistry.observeOversightUpgradeOffer(
            farmShape: services.farmShapeService.latestShape,
            shiftPlanEnabled: capabilities.shiftPlanEnabled,
            isFarmAdmin: navigationIdentity.isFarmAdmin
        )

        if sizeClass == .regular {
            router.presentShippingShell(capabilities: capabilities)
            return
        }

        router.configureAdaptiveShell(
            serverID: activeServer.id,
            userID: navigationIdentity.userID,
            preference: navigationIdentity.isProvisional
                ? .simple
                : serverRegistry.navigationLayoutPreference,
            farmShape: services.farmShapeService.sessionShape,
            isFarmAdmin: navigationIdentity.isFarmAdmin,
            capabilities: capabilities,
            oversightAvailability: oversightAvailability,
            preserveNavigationOnContextChange: preserveNavigationOnIdentityUpgrade
        )
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

    private var isCompactShellReady: Bool {
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

#if canImport(UIKit)
/// SwiftUI does not consistently propagate a tab label's identifier to the
/// underlying `UITabBarItem`, so enforce the published UI-test contract there.
private struct TabBarAccessibilityIdentifierBridge: UIViewControllerRepresentable {
    let identifiers: [String]

    func makeUIViewController(context: Context) -> BridgeViewController {
        BridgeViewController(identifiers: identifiers)
    }

    func updateUIViewController(
        _ controller: BridgeViewController,
        context: Context
    ) {
        controller.updateIdentifiers(identifiers)
    }

    final class BridgeViewController: UIViewController {
        var identifiers: [String]

        init(identifiers: [String]) {
            self.identifiers = identifiers
            super.init(nibName: nil, bundle: nil)
        }

        @available(*, unavailable)
        required init?(coder: NSCoder) {
            fatalError("init(coder:) has not been implemented")
        }

        override func viewDidAppear(_ animated: Bool) {
            super.viewDidAppear(animated)
            scheduleIdentifierUpdate()
        }

        override func viewDidLayoutSubviews() {
            super.viewDidLayoutSubviews()
            applyIdentifiers()
        }

        func updateIdentifiers(_ identifiers: [String]) {
            self.identifiers = identifiers
            scheduleIdentifierUpdate()
        }

        private func scheduleIdentifierUpdate() {
            applyIdentifiers()
            DispatchQueue.main.async { [weak self] in
                self?.applyIdentifiers()
                DispatchQueue.main.async { [weak self] in
                    self?.applyIdentifiers()
                }
            }
        }

        func applyIdentifiers() {
            let controller = tabBarController
                ?? findTabBarController(from: view.window?.rootViewController)
            guard let items = controller?.tabBar.items else { return }
            for (item, identifier) in zip(items, identifiers) {
                item.accessibilityIdentifier = identifier
            }
        }

        private func findTabBarController(
            from controller: UIViewController?
        ) -> UITabBarController? {
            guard let controller else { return nil }
            if let tabBarController = controller as? UITabBarController {
                return tabBarController
            }
            for child in controller.children {
                if let match = findTabBarController(from: child) {
                    return match
                }
            }
            return findTabBarController(from: controller.presentedViewController)
        }
    }
}
#endif
