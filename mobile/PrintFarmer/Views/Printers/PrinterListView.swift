import SwiftUI

enum PrinterListNavigationContext: Equatable {
    case farm

    var navigationTitle: String {
        switch self {
        case .farm:
            "Farm"
        }
    }

    var accessibilityIdentifier: String {
        switch self {
        case .farm:
            "farm.root"
        }
    }

    var accessibilityPrefix: String {
        switch self {
        case .farm:
            "farm"
        }
    }

    func printerCardIdentifier(for printerID: UUID) -> String {
        "\(accessibilityPrefix)-card-\(printerID.uuidString)"
    }

    var appTab: AppTab {
        switch self {
        case .farm:
            .farm
        }
    }
}

struct PrinterListView: View {
    let navigationContext: PrinterListNavigationContext
    let farmViewModel: DashboardViewModel

    @Environment(AppRouter.self) private var router
    @Environment(ServiceContainer.self) private var services
    @Environment(\.horizontalSizeClass) private var sizeClass
    @Environment(\.dynamicTypeSize) private var dynamicTypeSize
    @State private var viewModel = PrinterListViewModel()
    @State private var attentionViewModel = AttentionFeedViewModel()
    @State private var retryTask: Task<Void, Never>?
    @State private var isSearchPresented = false

    private var iPadColumns: [GridItem] {
        sizeClass == .regular && !dynamicTypeSize.isAccessibilitySize
            ? [GridItem(.adaptive(minimum: 300))]
            : [GridItem(.flexible())]
    }

    init(
        navigationContext: PrinterListNavigationContext = .farm,
        farmViewModel: DashboardViewModel
    ) {
        self.navigationContext = navigationContext
        self.farmViewModel = farmViewModel
        _viewModel = State(
            initialValue: PrinterListViewModel(
                initialPrinters: farmViewModel.printers,
                pendingReadyPrinterIDs: farmViewModel.pendingReadyPrinterIDs
            )
        )
    }

    var body: some View {
        @Bindable var router = router

        Group {
            switch navigationContext {
            case .farm:
                navigationStack(path: $router.printersPath)
            }
        }
        .task(id: services.activeServerGeneration) {
            synchronizeFarmData()
        }
        .onChange(of: farmViewModel.farmDataRevision) { _, _ in
            synchronizeFarmData()
        }
        .task(id: attentionAuthority) {
            await attentionViewModel.bootstrap(
                attentionService: services.attentionService,
                signalRService: services.signalRService,
                attentionEnabled: services.capabilitiesService.resolved.attentionEnabled,
                startupPrefetchStore: services.startupPrefetchStore
            )
        }
        .task(id: attentionViewModel.snapshot) {
            if attentionViewModel.snapshot?.nextCursor != nil {
                _ = await attentionViewModel.loadMore()
            }
            viewModel.attentionPrinterIDs = Set(attentionViewModel.snapshot?.items.map(\.printerId) ?? [])
        }
        .onDisappear {
            retryTask?.cancel()
            attentionViewModel.deactivate()
        }
        .onReceive(NotificationCenter.default.publisher(for: UIApplication.willEnterForegroundNotification)) { _ in
            Task {
                _ = await attentionViewModel.refresh()
            }
        }
        .onChange(of: activeNavigationPathCount) { _, newCount in
            if newCount == 0 {
                Task { await farmViewModel.refreshPendingReadyStatus() }
            }
        }
        .onChange(of: router.pendingNeedsAttentionFilter, initial: true) { _, needsAttention in
            guard needsAttention else { return }
            viewModel.selectedStatus = .needsAttention
            viewModel.searchText = ""
            router.pendingNeedsAttentionFilter = false
        }
        .accessibilityIdentifier(navigationContext.accessibilityIdentifier)
    }

    private func navigationStack(
        path: Binding<NavigationPath>
    ) -> some View {
        NavigationStack(path: path) {
            VStack(spacing: 0) {
                if let failure = attentionViewModel.loadFailure {
                    Text("Attention unavailable: \(failure.message)")
                        .font(.caption)
                        .foregroundStyle(Color.pfWarning)
                        .padding(.horizontal)
                }
                if let failure = attentionViewModel.paginationFailure {
                    Button("Attention count unavailable. Retry") {
                        Task { _ = await attentionViewModel.retryLoadMore(failureID: failure.id) }
                    }
                    .font(.caption)
                    .foregroundStyle(Color.pfWarning)
                }
                Group {
                    // This list is mounted inside the Farm navigation stack and
                    // reads loading/error state from its canonical Dashboard owner.
                    if farmViewModel.isLoading && farmViewModel.printers.isEmpty {
                        ProgressView("Loading printers…")
                            .frame(maxWidth: .infinity, maxHeight: .infinity)
                    } else if let error = farmViewModel.errorMessage, farmViewModel.printers.isEmpty {
                        ContentUnavailableView {
                            Label("Error", systemImage: "exclamationmark.triangle")
                        } description: {
                            Text(error)
                        } actions: {
                            Button("Retry") {
                                retryTask = Task { await farmViewModel.loadDashboard() }
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
            #if os(iOS)
            .navigationBarTitleDisplayMode(.inline)
            #endif
            .refreshable {
                await farmViewModel.loadDashboard()
                _ = await attentionViewModel.refresh()
            }
            .modifier(
                PresentedFarmSearch(
                    text: $viewModel.searchText,
                    isPresented: $isSearchPresented
                )
            )
            .rootNavigationChrome(for: navigationContext.appTab) {
                Button {
                    isSearchPresented = true
                } label: {
                    Image(systemName: "magnifyingglass")
                        .foregroundStyle(Color.pfTextSecondary)
                        .frame(
                            width: RootNavigationChrome.minimumTouchTarget,
                            height: RootNavigationChrome.minimumTouchTarget
                        )
                        .contentShape(Rectangle())
                }
                .buttonStyle(.plain)
                .accessibilityLabel("Search printers")
                .accessibilityIdentifier("farm.search")
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
        }
    }

    private func synchronizeFarmData() {
        farmViewModel.synchronizeFarmData(to: viewModel)
    }

    // MARK: - Printer List

    private var printerList: some View {
        VStack(spacing: 0) {
            statusFilterBar
                .padding(.horizontal)
                .padding(.top, 8)

            ScrollView {
                LazyVStack(spacing: 12) {
                    Group {
                        if viewModel.filteredPrinters.isEmpty {
                            ContentUnavailableView {
                                Label("No Printers in This Filter", systemImage: "line.3.horizontal.decrease.circle")
                            } description: {
                                Text("Choose another Farm filter to see printers.")
                            }
                            .padding(.top, 40)
                        } else {
                            LazyVGrid(columns: iPadColumns, spacing: 12) {
                                ForEach(viewModel.filteredPrinters) { printer in
                                    NavigationLink(value: AppDestination.printerDetail(id: printer.id)) {
                                        PrinterCardView(
                                            printer: printer,
                                            isPendingReady: viewModel.isPendingReady(printer),
                                            attentionCount: attentionCount(for: printer.id),
                                            failureReason: failureReason(for: printer.id),
                                            printerService: services.printerService
                                        )
                                    }
                                    .buttonStyle(.plain)
                                    .accessibilityElement(children: .combine)
                                    .accessibilityHint("Opens \(printer.name) printer details.")
                                    .accessibilityIdentifier(
                                        printerAccessibilityIdentifier(for: printer)
                                    )
                                }
                            }
                        }
                    }
                    .padding(.horizontal)
                    .padding(.vertical, 8)
                    .padding(.bottom, 112)
                }
            }
            .accessibilityIdentifier("farm.printerList")
        }
    }

    // MARK: - Filters

    private var attentionAuthority: String {
        "\(services.activeServerGeneration)-\(services.capabilitiesService.resolved.attentionEnabled)"
    }

    private func attentionCount(for printerID: UUID) -> Int? {
        guard attentionViewModel.phase == .loaded,
              attentionViewModel.snapshot?.nextCursor == nil,
              attentionViewModel.loadFailure == nil,
              attentionViewModel.paginationFailure == nil else { return nil }
        return attentionViewModel.snapshot?.items.filter { $0.printerId == printerID }.count
    }

    private func failureReason(for printerID: UUID) -> String? {
        attentionViewModel.snapshot?.items.first {
            $0.printerId == printerID && $0.kind == .failure
        }?.detail
    }

    private var statusFilterBar: some View {
        VStack(alignment: .leading, spacing: 0) {
            if dynamicTypeSize.isAccessibilitySize {
                VStack(alignment: .leading, spacing: 8) {
                    statusFilterChips(allowsWrapping: true)
                }
            } else {
                HStack(spacing: 8) {
                    statusFilterChips(allowsWrapping: false)
                }
                .fixedSize(horizontal: true, vertical: false)
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .dynamicTypeSize(dynamicTypeSize.isAccessibilitySize ? .xxxLarge : dynamicTypeSize)
        .accessibilityElement(children: .contain)
        .accessibilityIdentifier("farm.filters")
    }

    private func statusFilterChips(allowsWrapping: Bool) -> some View {
        ForEach(PrinterListViewModel.StatusFilter.allCases) { filter in
            let count = viewModel.count(for: filter)
            FilterChip(
                title: filterCountTitle(filter, count: count),
                identifier: "farm.filter.\(filter.id)",
                isSelected: viewModel.selectedStatus == filter,
                allowsWrapping: allowsWrapping
            ) {
                viewModel.selectedStatus = filter
            }
        }
    }

    private func filterCountTitle(
        _ filter: PrinterListViewModel.StatusFilter,
        count: Int
    ) -> String {
        guard filter == .needsAttention,
              services.capabilitiesService.resolved.attentionEnabled,
              !(attentionViewModel.phase == .loaded
                && attentionViewModel.snapshot?.nextCursor == nil
                && attentionViewModel.loadFailure == nil
                && attentionViewModel.paginationFailure == nil) else {
            return "\(filter.rawValue) \(count)"
        }
        return "\(filter.rawValue) —"
    }

    private func printerAccessibilityIdentifier(for printer: Printer) -> String {
        navigationContext.printerCardIdentifier(for: printer.id)
    }
}

private struct PresentedFarmSearch: ViewModifier {
    @Binding var text: String
    @Binding var isPresented: Bool

    func body(content: Content) -> some View {
        Group {
            if isPresented {
                content.searchable(
                    text: $text,
                    isPresented: $isPresented,
                    prompt: "Search printers"
                )
            } else {
                content
            }
        }
    }
}

// MARK: - Filter Chip

private struct FilterChip: View {
    let title: String
    let identifier: String
    let isSelected: Bool
    let allowsWrapping: Bool
    let action: () -> Void

    var body: some View {
        Button(action: action) {
            HStack(spacing: 6) {
                if isSelected {
                    Image(systemName: "checkmark")
                        .accessibilityHidden(true)
                }
                Text(title)
                    .font(.caption.weight(.medium))
                    .fixedSize(horizontal: false, vertical: true)
            }
            .padding(.horizontal, 8)
            .frame(minHeight: 44)
            .foregroundStyle(Color.pfTextPrimary)
            .background(
                isSelected ? Color.pfAccent.opacity(0.18) : Color.pfCard,
                in: Capsule()
            )
            .overlay {
                Capsule()
                    .strokeBorder(
                        isSelected ? Color.pfAccentHover : Color.pfBorder,
                        lineWidth: 1
                    )
            }
            .frame(maxWidth: allowsWrapping ? .infinity : nil, alignment: .leading)
        }
        .buttonStyle(.plain)
        .fixedSize(horizontal: !allowsWrapping, vertical: false)
        .accessibilityLabel(title)
        .accessibilityValue(isSelected ? "Selected" : "Not selected")
        .accessibilityHint("Filters the printer list to \(title.lowercased()).")
        .accessibilityAddTraits(isSelected ? .isSelected : [])
        .accessibilityIdentifier(identifier)
    }
}
