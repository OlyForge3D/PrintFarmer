import SwiftUI

struct ContentView: View {
    @Environment(AppRouter.self) private var router
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
            DashboardView()
                .tabItem { Label("Dashboard", systemImage: "square.grid.2x2") }
                .tag(AppTab.dashboard)

            PrinterListView()
                .tabItem { Label("Printers", systemImage: "printer") }
                .tag(AppTab.printers)
                .badge(router.pendingReadyCount)

            JobListView()
                .tabItem { Label("Jobs", systemImage: "list.bullet.rectangle") }
                .tag(AppTab.jobs)

            SpoolInventoryView()
                .tabItem { Label("Inventory", systemImage: "cylinder.fill") }
                .tag(AppTab.inventory)

            NotificationsView()
                .tabItem { Label("Alerts", systemImage: "bell") }
                .tag(AppTab.notifications)
                .badge(router.notificationBadgeCount)

            MaintenanceView()
                .tabItem { Label("Maintenance", systemImage: "wrench.adjustable") }
                .tag(AppTab.maintenance)

            SettingsView()
                .tabItem { Label("Settings", systemImage: "gear") }
                .tag(AppTab.settings)
        }
    }

    // MARK: - Regular (iPad)

    private var iPadLayout: some View {
        @Bindable var router = router

        return NavigationSplitView(columnVisibility: $router.sidebarVisibility) {
            List {
                // Operations
                Section {
                    sidebarButton(tab: .dashboard, title: "Dashboard", icon: "house")
                    sidebarPrintersButton
                    sidebarButton(tab: .jobs, title: "Print Queue", icon: "tray.full")
                } header: {
                    Text("Operations")
                }

                // Hardware
                Section {
                    sidebarButton(tab: .inventory, title: "Filament Inventory", icon: "cylinder.fill")
                } header: {
                    Text("Hardware")
                }

                // Management
                Section {
                    sidebarButton(tab: .maintenance, title: "Maintenance", icon: "wrench.and.screwdriver")
                    sidebarAlertButton
                } header: {
                    Text("Management")
                }

                // Settings
                Section {
                    sidebarButton(tab: .settings, title: "Settings", icon: "gear")
                } header: {
                    Text("Settings")
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

    private var sidebarPrintersButton: some View {
        Button {
            router.selectedTab = .printers
        } label: {
            HStack {
                Label("Printers", systemImage: "printer")
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
        .listRowBackground(router.selectedTab == .printers ? Color.accentColor.opacity(0.15) : nil)
        .foregroundStyle(router.selectedTab == .printers ? Color.accentColor : .primary)
    }
    
    private var sidebarAlertButton: some View {
        Button {
            router.selectedTab = .notifications
        } label: {
            HStack {
                Label("Alerts", systemImage: "bell")
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
        .listRowBackground(router.selectedTab == .notifications ? Color.accentColor.opacity(0.15) : nil)
        .foregroundStyle(router.selectedTab == .notifications ? Color.accentColor : .primary)
    }

    @ViewBuilder
    private func tabContentView(for tab: AppTab) -> some View {
        switch tab {
        case .dashboard:
            DashboardView()
        case .printers:
            PrinterListView()
        case .jobs:
            JobListView()
        case .inventory:
            SpoolInventoryView()
        case .notifications:
            NotificationsView()
        case .maintenance:
            MaintenanceView()
        case .settings:
            SettingsView()
        }
    }
}
