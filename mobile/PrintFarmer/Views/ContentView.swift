import SwiftUI

struct ContentView: View {
    static let sidebarRowMinimumHeight: CGFloat = 44

    @Environment(AppRouter.self) private var router
    @Environment(ServiceContainer.self) private var services
    @Environment(\.horizontalSizeClass) private var sizeClass
    @Environment(\.dynamicTypeSize) private var dynamicTypeSize
    @State private var showScan = false
    @State private var scanSessionActive = false
    @State private var externalScanRequestID: UUID?

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
                                Label(tab.title, systemImage: tab.systemImage)
                                    .frame(minHeight: Self.sidebarRowMinimumHeight)
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
                } detail: {
                    tabContentView(for: router.selectedTab)
                        .safeAreaInset(edge: .bottom, alignment: .trailing) {
                            if router.isAtRoot(router.selectedTab) { scanButton }
                        }
                }
                .navigationSplitViewStyle(.balanced)
            } else {
                TabView(selection: $router.selectedTab) {
                    ForEach(AppTab.allCases, id: \.self) { tab in
                        tabContentView(for: tab)
                            .safeAreaInset(edge: .bottom, alignment: .trailing) {
                                if router.isAtRoot(tab) { scanButton }
                            }
                            .tabItem {
                                Label(tab.title, systemImage: tab.systemImage)
                                    .accessibilityIdentifier(tab.tabAccessibilityIdentifier)
                            }
                            .tag(tab)
                            .badge(tab == .farm ? router.pendingReadyCount : 0)
                    }
                }
            }
        }
        .task { presentPendingExternalScan() }
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
                .font(.headline)
                .dynamicTypeSize(dynamicTypeSize.isAccessibilitySize ? .xxxLarge : dynamicTypeSize)
                .lineLimit(1)
                .fixedSize(horizontal: true, vertical: false)
                .padding(.horizontal, 18)
                .frame(minHeight: 48)
        }
        .buttonStyle(.plain)
        .foregroundStyle(.white)
        .background(Color.pfAccentBg, in: Capsule())
        .shadow(color: .black.opacity(0.2), radius: 8, y: 4)
        .padding(.trailing, 16)
        .padding(.bottom, 12)
        .accessibilityLabel("Scan")
        .accessibilityHint("Opens barcode and NFC scanning for printers and spools.")
        .accessibilityIdentifier("navigation.scan")
    }

    private func presentPendingExternalScan() {
        guard !scanSessionActive || showScan else { return }
        let requestID = router.pendingExternalScanRequestID
        guard router.consumeExternalScanRequest() else { return }
        externalScanRequestID = requestID
        scanSessionActive = true
        showScan = true
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
