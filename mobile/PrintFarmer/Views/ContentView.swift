import SwiftUI

/// Operator-shell content view. F1 (#706) replaces the seven-tab layout
/// (dashboard, printers, jobs, notifications, inventory, maintenance,
/// settings) with the five operator-first destinations: Attention, Farm,
/// Tasks, Scan, Inventory. Settings and server switching move to the
/// Attention overflow menu; jog/preheat/z-offset controls live behind
/// Printer Detail → Advanced.
struct ContentView: View {
    @Environment(AppRouter.self) private var router
    @Environment(ServerRegistry.self) private var serverRegistry
    @Environment(\.horizontalSizeClass) private var sizeClass

    var body: some View {
        if sizeClass == .regular {
            iPadLayout
        } else {
            compactLayout
        }
    }

    // MARK: - Compact (iPhone)

    private var compactLayout: some View {
        @Bindable var router = router

        return TabView(selection: $router.selectedTab) {
            AttentionView()
                .tabItem { Label("Attention", systemImage: "bell.badge") }
                .tag(AppTab.attention)
                .badge(router.notificationBadgeCount)
                .accessibilityIdentifier("tab.attention")

            PrinterListView()
                .tabItem { Label("Farm", systemImage: "printer") }
                .tag(AppTab.farm)
                .badge(router.pendingReadyCount)
                .accessibilityIdentifier("tab.farm")

            JobListView()
                .tabItem { Label("Tasks", systemImage: "checklist") }
                .tag(AppTab.tasks)
                .accessibilityIdentifier("tab.tasks")

            ScanView()
                .tabItem { Label("Scan", systemImage: "barcode.viewfinder") }
                .tag(AppTab.scan)
                .accessibilityIdentifier("tab.scan")

            InventoryView()
                .tabItem { Label("Inventory", systemImage: "cylinder.fill") }
                .tag(AppTab.inventory)
                .accessibilityIdentifier("tab.inventory")
        }
    }

    // MARK: - Regular (iPad)

    private var iPadLayout: some View {
        @Bindable var router = router

        return NavigationSplitView(columnVisibility: $router.sidebarVisibility) {
            List {
                Section {
                    sidebarAttentionButton
                    sidebarFarmButton
                    sidebarButton(tab: .tasks, title: "Tasks", icon: "checklist", identifier: "sidebar.tasks")
                    sidebarButton(tab: .scan, title: "Scan", icon: "barcode.viewfinder", identifier: "sidebar.scan")
                    sidebarButton(tab: .inventory, title: "Inventory", icon: "cylinder.fill", identifier: "sidebar.inventory")
                } header: {
                    Text("Operator")
                }
            }
            .listStyle(.sidebar)
            .navigationTitle("PrintFarmer")
        } detail: {
            tabContentView(for: router.selectedTab)
        }
        .navigationSplitViewStyle(.balanced)
    }

    private func sidebarButton(tab: AppTab, title: String, icon: String, identifier: String) -> some View {
        Button {
            router.selectedTab = tab
        } label: {
            Label(title, systemImage: icon)
                .frame(minHeight: 44)
        }
        .listRowBackground(router.selectedTab == tab ? Color.accentColor.opacity(0.15) : nil)
        .foregroundStyle(router.selectedTab == tab ? Color.accentColor : .primary)
        .accessibilityLabel(title)
        .accessibilityHint("Opens the \(title) destination.")
        .accessibilityAddTraits(router.selectedTab == tab ? [.isButton, .isSelected] : .isButton)
        .accessibilityIdentifier(identifier)
    }

    private var sidebarFarmButton: some View {
        Button {
            router.selectedTab = .farm
        } label: {
            HStack {
                Label("Farm", systemImage: "printer")
                Spacer()
                if router.pendingReadyCount > 0 {
                    Text("\(router.pendingReadyCount)")
                        .font(.caption2.weight(.bold))
                        .padding(.horizontal, 6)
                        .padding(.vertical, 2)
                        .background(Color.pfWarning, in: Capsule())
                        .foregroundStyle(.white)
                        .accessibilityHidden(true)
                }
            }
            .frame(minHeight: 44)
        }
        .listRowBackground(router.selectedTab == .farm ? Color.accentColor.opacity(0.15) : nil)
        .foregroundStyle(router.selectedTab == .farm ? Color.accentColor : .primary)
        .accessibilityLabel(router.pendingReadyCount > 0 ? "Farm, \(router.pendingReadyCount) ready" : "Farm")
        .accessibilityHint("Opens the Farm destination.")
        .accessibilityAddTraits(router.selectedTab == .farm ? [.isButton, .isSelected] : .isButton)
        .accessibilityIdentifier("sidebar.farm")
    }

    private var sidebarAttentionButton: some View {
        Button {
            router.selectedTab = .attention
        } label: {
            HStack {
                Label("Attention", systemImage: "bell.badge")
                Spacer()
                if router.notificationBadgeCount > 0 {
                    Text("\(router.notificationBadgeCount)")
                        .font(.caption2.weight(.bold))
                        .padding(.horizontal, 6)
                        .padding(.vertical, 2)
                        .background(Color.red, in: Capsule())
                        .foregroundStyle(.white)
                        .accessibilityHidden(true)
                }
            }
            .frame(minHeight: 44)
        }
        .listRowBackground(router.selectedTab == .attention ? Color.accentColor.opacity(0.15) : nil)
        .foregroundStyle(router.selectedTab == .attention ? Color.accentColor : .primary)
        .accessibilityLabel(router.notificationBadgeCount > 0 ? "Attention, \(router.notificationBadgeCount) unread" : "Attention")
        .accessibilityHint("Opens the Attention destination.")
        .accessibilityAddTraits(router.selectedTab == .attention ? [.isButton, .isSelected] : .isButton)
        .accessibilityIdentifier("sidebar.attention")
    }

    @ViewBuilder
    private func tabContentView(for tab: AppTab) -> some View {
        switch tab {
        case .attention:
            AttentionView()
        case .farm:
            PrinterListView()
        case .tasks:
            JobListView()
        case .scan:
            ScanView()
        case .inventory:
            InventoryView()
        }
    }
}
