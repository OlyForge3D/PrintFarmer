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

            PrinterListView()
                .tabItem { Label("Farm", systemImage: "printer") }
                .tag(AppTab.farm)
                .badge(router.pendingReadyCount)

            JobListView()
                .tabItem { Label("Tasks", systemImage: "checklist") }
                .tag(AppTab.tasks)

            ScanView()
                .tabItem { Label("Scan", systemImage: "barcode.viewfinder") }
                .tag(AppTab.scan)

            SpoolInventoryView()
                .tabItem { Label("Inventory", systemImage: "cylinder.fill") }
                .tag(AppTab.inventory)
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
                    sidebarButton(tab: .tasks, title: "Tasks", icon: "checklist")
                    sidebarButton(tab: .scan, title: "Scan", icon: "barcode.viewfinder")
                    sidebarButton(tab: .inventory, title: "Inventory", icon: "cylinder.fill")
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

    private func sidebarButton(tab: AppTab, title: String, icon: String) -> some View {
        Button {
            router.selectedTab = tab
        } label: {
            Label(title, systemImage: icon)
        }
        .listRowBackground(router.selectedTab == tab ? Color.accentColor.opacity(0.15) : nil)
        .foregroundStyle(router.selectedTab == tab ? Color.accentColor : .primary)
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
                }
            }
        }
        .listRowBackground(router.selectedTab == .farm ? Color.accentColor.opacity(0.15) : nil)
        .foregroundStyle(router.selectedTab == .farm ? Color.accentColor : .primary)
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
                }
            }
        }
        .listRowBackground(router.selectedTab == .attention ? Color.accentColor.opacity(0.15) : nil)
        .foregroundStyle(router.selectedTab == .attention ? Color.accentColor : .primary)
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
            SpoolInventoryView()
        }
    }
}

