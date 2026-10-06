import SwiftUI
import OSLog

struct ContentView: View {
    private static let logger = Logger(subsystem: "com.printfarmer.ios", category: "SidebarCounts")
    static let sidebarRowMinimumHeight: CGFloat = 44

    @Environment(AppRouter.self) private var router
    @Environment(ServiceContainer.self) private var services
    @Environment(ServerRegistry.self) private var serverRegistry
    @Environment(\.horizontalSizeClass) private var sizeClass
    @Environment(\.dynamicTypeSize) private var dynamicTypeSize
    @State private var showScan = false
    @State private var scanSessionActive = false
    @State private var externalScanRequestID: UUID?
    @State private var sidebarCounts: [AppTab: Int] = [:]

    var body: some View {
        @Bindable var router = router

        Group {
            if sizeClass == .regular {
                NavigationSplitView(columnVisibility: $router.sidebarVisibility) {
                    List {
                        ForEach(AppTab.allCases, id: \.self) { tab in
                            Button {
                                router.selectedTab = tab
                            } label: {
                                HStack(spacing: 10) {
                                    Image(systemName: tab.systemImage)
                                        .font(.system(size: 18, weight: .semibold))
                                        .frame(width: 24)
                                    Text(tab.title)
                                        .font(.subheadline)
                                        .lineLimit(1)
                                        .minimumScaleFactor(0.65)
                                        .frame(minHeight: Self.sidebarRowMinimumHeight, alignment: .leading)
                                    Spacer(minLength: 8)
                                    sidebarCountBadge(for: tab)
                                }
                            }
                            .listRowBackground(router.selectedTab == tab ? Color.pfAccent.opacity(0.15) : nil)
                            .accessibilityLabel(tab.title)
                            .accessibilityHint("Opens \(tab.title).")
                            .accessibilityAddTraits(router.selectedTab == tab ? [.isSelected] : [])
                            .accessibilityIdentifier(tab.sidebarAccessibilityIdentifier)
                        }
                    }
                    .listStyle(.sidebar)
                    .navigationTitle("PrintFarmer")
                    .dynamicTypeSize(dynamicTypeSize.isAccessibilitySize ? .xxxLarge : dynamicTypeSize)
                    .safeAreaInset(edge: .bottom, spacing: 0) {
                        Button {
                            switch router.selectedTab {
                            case .farm: router.printersPath.append(AppDestination.account)
                            case .queue: router.jobsPath.append(AppDestination.account)
                            case .filament: router.inventoryPath.append(AppDestination.account)
                            }
                        } label: {
                            HStack(spacing: 10) {
                                Image(systemName: "person.crop.circle.fill")
                                    .font(.system(size: dynamicTypeSize.isAccessibilitySize ? 18 : 20))
                                    .foregroundStyle(Color.pfTextSecondary)
                                Text("Settings")
                                    .font(.subheadline)
                                    .lineLimit(1)
                                    .minimumScaleFactor(0.75)
                                    .foregroundStyle(Color.pfTextPrimary)
                                if !dynamicTypeSize.isAccessibilitySize {
                                    Text("& servers")
                                        .font(.caption)
                                        .foregroundStyle(Color.pfTextSecondary)
                                }
                                Spacer()
                            }
                            .frame(minHeight: Self.sidebarRowMinimumHeight)
                            .padding(.horizontal, 16)
                            .contentShape(Rectangle())
                        }
                        .buttonStyle(.plain)
                        .accessibilityIdentifier("sidebar.settings")
                        .background(Color.pfBackground)
                        .dynamicTypeSize(dynamicTypeSize.isAccessibilitySize ? .xxxLarge : dynamicTypeSize)
                    }
                } detail: {
                    tabContentView(for: router.selectedTab)
                        .safeAreaInset(edge: .bottom, alignment: .trailing) {
                            if router.isAtRoot(router.selectedTab) { scanFooter }
                        }
                        .toolbarBackground(Color.pfBackground, for: .tabBar)
                        .toolbarBackground(.visible, for: .tabBar)
                }
                .navigationSplitViewStyle(.balanced)
            } else {
                TabView(selection: $router.selectedTab) {
                    ForEach(AppTab.allCases, id: \.self) { tab in
                        tabContentView(for: tab)
                            .tabItem {
                                Label(tab.title, systemImage: tab.systemImage)
                            }
                            .tag(tab)
                            .badge(tab == .farm ? router.pendingReadyCount : 0)
                    }
                }
                .tabViewStyle(.page(indexDisplayMode: .never))
                .toolbar(.hidden, for: .tabBar)
                .safeAreaInset(edge: .bottom, spacing: 0) {
                    if router.isAtRoot(router.selectedTab) {
                        compactShellDock
                    }
                }
            }
        }
        .task { presentPendingExternalScan() }
        .task(id: sidebarCountTaskKey) {
            guard sizeClass == .regular else { return }
            await loadSidebarCounts()
        }
        .onChange(of: router.pendingExternalScanRequestID) { presentPendingExternalScan() }
        .onChange(of: router.isScanFlowDismissing) { presentPendingExternalScan() }
        .onChange(of: services.capabilitiesService.resolved) { _, capabilities in
            router.reconcileCapabilities(capabilities)
        }
        .sheet(isPresented: $showScan, onDismiss: {
            scanSessionActive = false
            externalScanRequestID = nil
            router.completeScanFlowDismissal(capabilities: services.capabilitiesService.resolved)
            presentPendingExternalScan()
        }) {
            ScanFlowView(externalScanRequestID: externalScanRequestID)
        }
    }

    private var scanButton: some View {
        Button {
            scanSessionActive = true
            showScan = true
        } label: {
            Label("Scan", systemImage: "barcode.viewfinder")
                .font(.subheadline.weight(.semibold))
                .lineLimit(1)
                .fixedSize(horizontal: true, vertical: false)
                .padding(.horizontal, 14)
                .frame(minHeight: 44)
        }
        .buttonStyle(.plain)
        .foregroundStyle(.white)
        .background(Color.pfAccentBg, in: Capsule())
        .shadow(color: .black.opacity(0.2), radius: 8, y: 4)
        .accessibilityLabel("Scan")
        .accessibilityHint("Opens barcode and NFC scanning for printers and spools.")
        .accessibilityIdentifier("navigation.scan")
        .dynamicTypeSize(dynamicTypeSize.isAccessibilitySize ? .xxxLarge : dynamicTypeSize)
    }

    private var scanFooter: some View {
        HStack {
            Spacer()
            scanButton
        }
        .padding(.horizontal, 16)
        .padding(.vertical, 8)
        .background(Color.pfBackground)
        .dynamicTypeSize(dynamicTypeSize.isAccessibilitySize ? .xxxLarge : dynamicTypeSize)
        .overlay(alignment: .top) {
            Rectangle()
                .fill(Color.pfBorder)
                .frame(height: 1)
        }
    }

    private var compactShellDock: some View {
        VStack(spacing: 0) {
            scanFooter

            HStack(spacing: 0) {
                ForEach(AppTab.allCases, id: \.self) { tab in
                    Button {
                        router.selectedTab = tab
                    } label: {
                        VStack(spacing: 3) {
                            Image(systemName: tab.systemImage)
                                .font(.system(size: 18, weight: .semibold))
                                .frame(height: 21)
                            Text(tab.title)
                                .font(.caption.weight(router.selectedTab == tab ? .semibold : .regular))
                                .lineLimit(1)
                                .minimumScaleFactor(0.8)
                        }
                        .foregroundStyle(router.selectedTab == tab ? Color.pfAccent : Color.pfTextSecondary)
                        .frame(maxWidth: .infinity, minHeight: 50)
                        .contentShape(Rectangle())
                    }
                    .buttonStyle(.plain)
                    .accessibilityLabel(tab.title)
                    .accessibilityAddTraits(router.selectedTab == tab ? .isSelected : [])
                    .accessibilityIdentifier(tab.tabAccessibilityIdentifier)
                }
            }
            .padding(.horizontal, 8)
            .padding(.top, 4)
            .padding(.bottom, 2)
            .background(Color.pfBackground)
            .overlay(alignment: .top) {
                Rectangle()
                    .fill(Color.pfBorder)
                    .frame(height: 1)
            }
            .accessibilityElement(children: .contain)
            .accessibilityLabel("Tab bar")
            .accessibilityAddTraits(.isTabBar)
            .accessibilityIdentifier("navigation.tabBar")
        }
        .background(Color.pfBackground)
        .dynamicTypeSize(dynamicTypeSize.isAccessibilitySize ? .xxxLarge : dynamicTypeSize)
    }

    private func presentPendingExternalScan() {
        guard !scanSessionActive || showScan else { return }
        let requestID = router.pendingExternalScanRequestID
        guard router.consumeExternalScanRequest() else { return }
        externalScanRequestID = requestID
        scanSessionActive = true
        showScan = true
    }

    private var sidebarCountTaskKey: String {
        let layout = sizeClass == .regular ? "regular" : "compact"
        return "\(layout):\(services.activeServerGeneration)"
    }

    @ViewBuilder
    private func sidebarCountBadge(for tab: AppTab) -> some View {
        if let count = sidebarCounts[tab] {
            Text("\(count)")
                .font(.caption.monospacedDigit())
                .foregroundStyle(Color.pfTextSecondary)
                .padding(.horizontal, 8)
                .padding(.vertical, 3)
                .background(Color.pfBackgroundTertiary, in: Capsule())
                .accessibilityLabel("\(tab.title) count \(count)")
                .accessibilityIdentifier("sidebar.count.\(tab.rawValue)")
        }
    }

    private func loadSidebarCounts() async {
        let generation = services.activeServerGeneration
        await services.awaitActiveServerSettled()
        guard !Task.isCancelled, services.isActiveGeneration(generation) else { return }
        sidebarCounts = [:]

        if let serverID = serverRegistry.activeServerID {
            await services.farmShapeService.refreshLatest(serverID: serverID)
            guard !Task.isCancelled, services.isActiveGeneration(generation) else { return }
            if let count = services.farmShapeService.latestShape?.printerCount {
                sidebarCounts[.farm] = count
            }
        }

        do {
            let stats = try await services.jobAnalyticsService.getStats()
            guard !Task.isCancelled, services.isActiveGeneration(generation) else { return }
            sidebarCounts[.queue] = stats.totalQueued + stats.totalPrinting + stats.totalPaused
        } catch {
            Self.logger.warning("Queue sidebar count is unavailable.")
        }

        do {
            let spoolCount = try await services.spoolService.listSpools(limit: 1).totalCount
            guard !Task.isCancelled, services.isActiveGeneration(generation) else { return }
            sidebarCounts[.filament] = spoolCount
        } catch {
            Self.logger.warning("Filament sidebar count is unavailable.")
        }
    }

    @ViewBuilder
    private func tabContentView(for tab: AppTab) -> some View {
        switch tab {
        case .farm: DashboardView()
        case .queue: JobListView()
        case .filament: SpoolInventoryView()
        }
    }
}
