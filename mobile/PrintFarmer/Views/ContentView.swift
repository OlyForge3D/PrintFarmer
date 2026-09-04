import SwiftUI

/// Operator-shell content view. F1 (#706) replaces the seven-tab layout
/// (dashboard, printers, jobs, notifications, inventory, maintenance,
/// settings) with the five operator-first destinations: Attention, Farm,
/// Tasks, Scan, Inventory. Settings and server switching move to the
/// Attention overflow menu; jog/preheat/z-offset controls live behind
/// Printer Detail → Advanced.
struct ContentView: View {
    static let sidebarRowMinimumHeight: CGFloat = 44
    static func shippingTabs(
        for capabilities: ResolvedSystemCapabilities
    ) -> [AppTab] {
        AppTab.visibleTabs(
            for: .current,
            mode: .floor,
            capabilities: capabilities
        )
    }

    @Environment(AppRouter.self) private var router
    @Environment(ServiceContainer.self) private var services
    @Environment(ServerRegistry.self) private var serverRegistry
    @Environment(\.horizontalSizeClass) private var sizeClass

    var body: some View {
        let capabilities = services.capabilitiesService.resolved

        Group {
            if sizeClass == .regular {
                iPadLayout(capabilities: capabilities)
            } else {
                compactLayout(capabilities: capabilities)
            }
        }
        .onAppear {
            router.reconcileCapabilities(capabilities)
        }
        .onChange(of: capabilities) { _, newCapabilities in
            router.reconcileCapabilities(newCapabilities)
        }
    }

    // MARK: - Compact (iPhone)

    private func compactLayout(capabilities: ResolvedSystemCapabilities) -> some View {
        let selection = Binding(
            get: { resolvedShippingTab(for: capabilities) },
            set: { selectShippingTab($0, capabilities: capabilities) }
        )
        let tabs = Self.shippingTabs(for: capabilities)

        return TabView(selection: selection) {
            ForEach(tabs, id: \.self) { tab in
                tabContentView(for: tab)
                    .tabItem {
                        Label(tab.title, systemImage: tab.systemImage)
                    }
                    .tag(tab)
                    .badge(badgeCount(for: tab))
                    .accessibilityIdentifier(tab.tabAccessibilityIdentifier)
            }
        }
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
            EmptyView()
        }
    }
}
