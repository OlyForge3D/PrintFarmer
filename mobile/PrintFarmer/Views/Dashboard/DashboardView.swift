import SwiftUI

struct DashboardView: View {
    @Environment(ServiceContainer.self) private var services
    @Environment(AppRouter.self) private var router
    @Environment(\.horizontalSizeClass) private var sizeClass
    @State private var viewModel = DashboardViewModel()
    @State private var retryTask: Task<Void, Never>?

    var body: some View {
        farmFloorContent
            .task(id: services.activeServerGeneration) {
                viewModel.isViewActive = true
                viewModel.configure(
                    printerService: services.printerService,
                    jobService: services.jobService,
                    statisticsService: services.statisticsService,
                    jobAnalyticsService: services.jobAnalyticsService,
                    farmOnly: true
                )
                viewModel.configureSnapshot(
                    store: services.farmSnapshotStore,
                    autoPrintService: services.autoPrintService
                )
                viewModel.configureSignalR(services.signalRService)
                await viewModel.hydrateFromCache()
                await viewModel.loadDashboard()
            }
            .onReceive(NotificationCenter.default.publisher(for: UIApplication.willEnterForegroundNotification)) { _ in
                retryTask = Task { await viewModel.loadDashboard() }
            }
            .onDisappear {
                viewModel.isViewActive = false
                retryTask?.cancel()
            }
    }

    @ViewBuilder
    private var farmFloorContent: some View {
        @Bindable var router = router
        if viewModel.farmSource == .live {
            PrinterListView(farmViewModel: viewModel)
        } else {
            NavigationStack(path: $router.printersPath) {
                Group {
                    if viewModel.isReadOnly {
                        coldOfflineShell
                    } else if viewModel.isAbsentFleetReportable {
                        absentFleetState
                    } else if let error = viewModel.errorMessage {
                        errorState(error)
                    } else {
                        loadingState
                    }
                }
                .navigationTitle("Farm")
                .rootNavigationChrome(for: .farm)
                .refreshable { await viewModel.loadDashboard() }
                .navigationDestination(for: AppDestination.self) { destination in
                    destinationView(for: destination)
                }
            }
        }
    }

    private var loadingState: some View {
        VStack(spacing: 16) {
            Spacer(minLength: 100)
            ProgressView("Loading farm…")
            Spacer()
        }
        .frame(maxWidth: .infinity)
    }

    private func errorState(_ message: String) -> some View {
        ContentUnavailableView {
            Label("Error", systemImage: "exclamationmark.triangle")
        } description: {
            Text(message)
        } actions: {
            Button("Retry") {
                retryTask = Task { await viewModel.loadDashboard() }
            }
        }
    }

    private var coldOfflineShell: some View {
        VStack(spacing: 0) {
            if viewModel.isStaleBannerReportable {
                ConnectionStatusBar(
                    status: .offline,
                    lastConfirmedAt: viewModel.lastUpdatedAt,
                    hasCache: true
                )
            }
            Group {
                if viewModel.printers.isEmpty {
                    EmptyStateView(
                        icon: "printer",
                        title: "No Printers",
                        message: "The last saved snapshot for this server had no printers."
                    )
                    .accessibilityIdentifier("farm-cached-empty-state")
                } else {
                    farmCardsGrid
                }
            }
            .accessibilityIdentifier("cold-offline-shell")
        }
    }

    private var absentFleetState: some View {
        VStack(spacing: 0) {
            ConnectionStatusBar(status: .offline, lastConfirmedAt: nil, hasCache: false)
            ContentUnavailableView {
                Label("No Cached Fleet", systemImage: "wifi.slash")
            } description: {
                Text("You haven't loaded this server's fleet while online yet. Reconnect to see your printers.")
            } actions: {
                Button("Retry") {
                    retryTask = Task { await viewModel.loadDashboard() }
                }
            }
        }
        .accessibilityIdentifier("farm-absent-state")
    }

    private var farmCardsGrid: some View {
        let columns: [GridItem] = sizeClass == .compact
            ? [GridItem(.flexible())]
            : [GridItem(.adaptive(minimum: 320), spacing: 16)]
        return ScrollView {
            LazyVGrid(columns: columns, spacing: 16) {
                ForEach(viewModel.printers.sorted { sortPriority($0) < sortPriority($1) }) { printer in
                    PrinterCardView(
                        printer: printer,
                        isPendingReady: viewModel.isPendingReady(printer),
                        isReadOnly: true
                    )
                    .accessibilityIdentifier("farm-card-\(printer.id.uuidString)")
                }
            }
            .padding()
        }
    }

    private func sortPriority(_ printer: Printer) -> Int {
        if viewModel.isPendingReady(printer) { return 0 }
        guard printer.isOnline else { return 100 }
        switch printer.state?.lowercased() {
        case "printing": return 1
        case "ready", "idle": return 2
        default: return 3
        }
    }
}
