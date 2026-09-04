import SwiftUI

enum PrinterListNavigationContext {
    case farm
    case fleet

    var navigationTitle: String {
        switch self {
        case .farm:
            "Farm"
        case .fleet:
            "Fleet"
        }
    }

    var accessibilityIdentifier: String {
        switch self {
        case .farm:
            "farm.root"
        case .fleet:
            "oversight.root.fleet"
        }
    }

    var accessibilityPrefix: String {
        switch self {
        case .farm:
            "farm"
        case .fleet:
            "oversight.fleet"
        }
    }
}

struct PrinterListView: View {
    let navigationContext: PrinterListNavigationContext

    @Environment(AppRouter.self) private var router
    @Environment(ServiceContainer.self) private var services
    @Environment(\.horizontalSizeClass) private var sizeClass
    @State private var viewModel = PrinterListViewModel()
    @State private var coverageViewModel = FarmFilamentCoverageViewModel()
    @State private var retryTask: Task<Void, Never>?

    private var filamentCoverageEnabled: Bool {
        services.capabilitiesService.resolved.filamentCoverageEnabled
    }

    private var iPadColumns: [GridItem] {
        [GridItem(.adaptive(minimum: 340))]
    }

    init(navigationContext: PrinterListNavigationContext = .farm) {
        self.navigationContext = navigationContext
    }

    var body: some View {
        @Bindable var router = router

        Group {
            switch navigationContext {
            case .farm:
                navigationStack(path: $router.printersPath)
            case .fleet:
                navigationStack(path: $router.fleetPath)
            }
        }
        .task {
            PrinterListViewLifecycle.taskActivate(
                viewModel: viewModel,
                printerService: services.printerService,
                autoPrintService: services.autoPrintService,
                signalRService: services.signalRService
            )
            await viewModel.bootstrap(startupPrefetchStore: services.startupPrefetchStore)
        }
        .task(id: filamentCoverageEnabled) {
            guard filamentCoverageEnabled else {
                coverageViewModel.disableForCapabilityGate()
                return
            }
            coverageViewModel.configure(coverageService: services.filamentCoverageService)
            coverageViewModel.configureSignalR(services.signalRService)
            // #789: wire the read-cache before readiness-prefetch consumption or
            // the hydrate-then-load fallback.
            coverageViewModel.configureCache(services.filamentCoverageReadCache)
            await coverageViewModel.bootstrap(startupPrefetchStore: services.startupPrefetchStore)
        }
        .onDisappear {
            PrinterListViewLifecycle.onDisappear(
                viewModel: viewModel,
                retryTask: retryTask
            )
            coverageViewModel.tearDownSignalR()
        }
        .onReceive(NotificationCenter.default.publisher(for: UIApplication.willEnterForegroundNotification)) { _ in
            Task {
                await PrinterListViewLifecycle.willEnterForeground(
                    viewModel: viewModel,
                    coverageViewModel: coverageViewModel,
                    refreshCoverage: filamentCoverageEnabled
                )
            }
        }
        .onChange(of: activeNavigationPathCount) { _, newCount in
            if newCount == 0 {
                Task { await viewModel.loadAutoDispatchStatuses() }
            }
        }
        .accessibilityIdentifier(navigationContext.accessibilityIdentifier)
    }

    private func navigationStack(
        path: Binding<NavigationPath>
    ) -> some View {
        NavigationStack(path: path) {
            VStack(spacing: 0) {
                // #789: shared stale banner — honest, read-only cached coverage.
                if filamentCoverageEnabled && coverageViewModel.isStaleCacheReportable {
                    ConnectionStatusBar(
                        status: .offline,
                        lastConfirmedAt: coverageViewModel.cacheLastUpdatedAt,
                        hasCache: true
                    )
                }
                Group {
                    if viewModel.isLoading && viewModel.printers.isEmpty {
                        ProgressView("Loading printers…")
                            .frame(maxWidth: .infinity, maxHeight: .infinity)
                    } else if let error = viewModel.errorMessage, viewModel.printers.isEmpty {
                        ContentUnavailableView {
                            Label("Error", systemImage: "exclamationmark.triangle")
                        } description: {
                            Text(error)
                        } actions: {
                            Button("Retry") {
                                retryTask = Task { await viewModel.loadPrinters() }
                            }
                        }
                    } else if viewModel.printers.isEmpty {
                        EmptyStateView(
                            icon: "printer",
                            title: "No Printers",
                            message: "No printers are registered yet."
                        )
                    } else {
                        printerList
                    }
                }
            }
            .navigationTitle(navigationContext.navigationTitle)
            .searchable(text: $viewModel.searchText, prompt: "Search printers")
            .refreshable {
                await PrinterListViewLifecycle.refresh(
                    viewModel: viewModel,
                    coverageViewModel: coverageViewModel,
                    refreshCoverage: filamentCoverageEnabled
                )
            }
            .toolbar {
                if sizeClass == .compact {
                    ToolbarItem(placement: .topBarTrailing) {
                        ServerSwitcherMenu(style: .toolbar)
                    }
                }

                ToolbarItem(placement: .automatic) {
                    statusFilterMenu
                }
            }
            .navigationDestination(for: AppDestination.self) { destination in
                destinationView(for: destination)
            }
        }
    }

    private var activeNavigationPathCount: Int {
        switch navigationContext {
        case .farm:
            router.printersPath.count
        case .fleet:
            router.fleetPath.count
        }
    }

    // MARK: - Printer List

    private var printerList: some View {
        ScrollView {
            LazyVStack(spacing: 12) {
                // Location filter pills
                if viewModel.availableLocations.count > 1 {
                    locationFilterBar
                }

                if viewModel.filteredPrinters.isEmpty {
                    ContentUnavailableView.search(text: viewModel.searchText)
                        .padding(.top, 40)
                } else if sizeClass == .regular {
                    // iPad: adaptive grid of cards
                    LazyVGrid(columns: iPadColumns, spacing: 12) {
                        ForEach(viewModel.filteredPrinters) { printer in
                            NavigationLink(value: AppDestination.printerDetail(id: printer.id)) {
                                iPadPrinterCardView(
                                    printer: printer,
                                    isPendingReady: viewModel.isPendingReady(printer),
                                    coverage: filamentCoverageEnabled
                                        ? coverageViewModel.coverage(for: printer.id)
                                        : nil
                                )
                            }
                            .buttonStyle(.plain)
                            .accessibilityLabel(
                                "\(printer.name), \(printer.state ?? "unknown") status"
                                + "\(printer.isOnline ? ", online" : ", offline")"
                            )
                            .accessibilityHint("Opens \(printer.name) printer details.")
                            // Stable-id scoping (F4-M #778 cycle-3 review
                            // blocker D): XCUI tests scope badge / absence
                            // assertions beneath this identifier so a
                            // sibling printer's badge cannot satisfy a
                            // per-card assertion. The id is the backend
                            // printer UUID, never the display name.
                            .accessibilityIdentifier(
                                printerAccessibilityIdentifier(for: printer)
                            )
                        }
                    }
                } else {
                    ForEach(viewModel.filteredPrinters) { printer in
                        NavigationLink(value: AppDestination.printerDetail(id: printer.id)) {
                            PrinterCardView(
                                printer: printer,
                                isPendingReady: viewModel.isPendingReady(printer),
                                coverage: filamentCoverageEnabled
                                    ? coverageViewModel.coverage(for: printer.id)
                                    : nil
                            )
                        }
                        .buttonStyle(.plain)
                        .accessibilityLabel("\(printer.name), \(printer.state ?? "unknown") status\(printer.isOnline ? ", online" : ", offline")")
                        .accessibilityHint("Opens \(printer.name) printer details.")
                        // Stable-id scoping (F4-M #778 cycle-3): same as
                        // the iPad path above.
                        .accessibilityIdentifier(
                            printerAccessibilityIdentifier(for: printer)
                        )
                    }
                }
            }
            .padding(.horizontal)
            .padding(.vertical, 8)
        }
    }

    // MARK: - Filters

    private var statusFilterMenu: some View {
        Menu {
            ForEach(PrinterListViewModel.StatusFilter.allCases) { filter in
                Button {
                    viewModel.selectedStatus = filter
                } label: {
                    if viewModel.selectedStatus == filter {
                        Label(filter.rawValue, systemImage: "checkmark")
                    } else {
                        Text(filter.rawValue)
                    }
                }
            }
        } label: {
            Image(systemName: "line.3.horizontal.decrease.circle")
                .symbolVariant(viewModel.selectedStatus != .all ? .fill : .none)
        }
        .frame(minWidth: 44, minHeight: 44)
        .accessibilityLabel("Filter printers by status")
        .accessibilityHint("Chooses which printer statuses are shown.")
        .accessibilityIdentifier(
            "\(navigationContext.accessibilityPrefix).statusFilter"
        )
    }

    private var locationFilterBar: some View {
        ScrollView(.horizontal, showsIndicators: false) {
            HStack(spacing: 8) {
                FilterChip(
                    title: "All Locations",
                    identifier: "\(navigationContext.accessibilityPrefix).locationFilter.all",
                    isSelected: viewModel.selectedLocationId == nil
                ) {
                    viewModel.selectedLocationId = nil
                }

                ForEach(viewModel.availableLocations, id: \.id) { location in
                    FilterChip(
                        title: location.name,
                        identifier:
                            "\(navigationContext.accessibilityPrefix).locationFilter."
                                + location.id.uuidString,
                        isSelected: viewModel.selectedLocationId == location.id
                    ) {
                        viewModel.selectedLocationId = location.id
                    }
                }
            }
        }
    }

    private func printerAccessibilityIdentifier(for printer: Printer) -> String {
        switch navigationContext {
        case .farm:
            "farm-card-\(printer.id.uuidString)"
        case .fleet:
            "oversight.fleet.printer.\(printer.id.uuidString)"
        }
    }
}

@MainActor
enum PrinterListViewLifecycle {
    static func taskActivate(
        viewModel: PrinterListViewModel,
        printerService: any PrinterServiceProtocol,
        autoPrintService: any AutoDispatchServiceProtocol,
        signalRService: any SignalRServiceProtocol
    ) {
        viewModel.activate()
        viewModel.configure(
            printerService: printerService,
            autoPrintService: autoPrintService
        )
        viewModel.configureSignalR(signalRService)
    }

    static func onDisappear(
        viewModel: PrinterListViewModel,
        retryTask: Task<Void, Never>?
    ) {
        retryTask?.cancel()
        viewModel.deactivate()
    }

    static func willEnterForeground(
        viewModel: PrinterListViewModel,
        coverageViewModel: FarmFilamentCoverageViewModel? = nil,
        refreshCoverage: Bool = false
    ) async {
        await viewModel.loadAutoDispatchStatuses()
        if refreshCoverage {
            await coverageViewModel?.load()
        }
    }

    static func refresh(
        viewModel: PrinterListViewModel,
        coverageViewModel: FarmFilamentCoverageViewModel,
        refreshCoverage: Bool
    ) async {
        await viewModel.loadPrinters()
        if refreshCoverage {
            await coverageViewModel.load()
        }
    }
}

// MARK: - Filter Chip

private struct FilterChip: View {
    let title: String
    let identifier: String
    let isSelected: Bool
    let action: () -> Void

    var body: some View {
        Button(action: action) {
            HStack(spacing: 6) {
                if isSelected {
                    Image(systemName: "checkmark")
                        .accessibilityHidden(true)
                }
                Text(title)
                    .font(.subheadline.weight(.medium))
                    .fixedSize(horizontal: false, vertical: true)
            }
                .padding(.horizontal, 12)
                .frame(minHeight: 44)
                .foregroundStyle(Color.pfTextPrimary)
                .background(
                    isSelected ? Color.pfAccent.opacity(0.16) : Color.pfBorder.opacity(0.5),
                    in: Capsule()
                )
                .overlay {
                    Capsule()
                        .strokeBorder(
                            isSelected ? Color.pfAccentHover : Color.pfBorder,
                            lineWidth: 1
                        )
                }
        }
        .buttonStyle(.plain)
        .accessibilityLabel(title)
        .accessibilityValue(isSelected ? "Selected" : "Not selected")
        .accessibilityHint("Filters the printer list to \(title.lowercased()).")
        .accessibilityAddTraits(isSelected ? .isSelected : [])
        .accessibilityIdentifier(identifier)
    }
}
