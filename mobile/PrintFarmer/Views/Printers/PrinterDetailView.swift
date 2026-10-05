import SwiftUI
import OSLog

@MainActor
struct PrinterDetailSafetyRefresh: View {
    @ObservedObject var owner: PrinterControlsViewModel
    let onRefresh: () -> Void

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            if let error = owner.safetyReadError {
                Text(error)
                    .font(.footnote)
                    .foregroundStyle(Color.pfError)
                    .accessibilityElement(children: .ignore)
                    .accessibilityLabel(error)
                    .accessibilityIdentifier("printer.detail.safety.error")
            }
            ControlActionButton(
                title: "Refresh safety checks",
                identifier: "printer.detail.safety.refresh",
                action: onRefresh
            )
            .disabled(owner.isRefreshingSafety || owner.isLoadingCapabilities)
        }
    }
}

struct PrinterDetailSafetyRefreshCadence {
    static let statusInterval: Duration = .seconds(5)
    static let discoveryInterval: Duration = .seconds(60)

    private var lastDiscoveryAt: ContinuousClock.Instant

    init(now: ContinuousClock.Instant = .now) {
        lastDiscoveryAt = now
    }

    mutating func shouldRefreshDiscovery(at now: ContinuousClock.Instant) -> Bool {
        guard now - lastDiscoveryAt >= Self.discoveryInterval else { return false }
        lastDiscoveryAt = now
        return true
    }
}

struct PrinterDetailView: View {
    private static let logger = Logger(subsystem: "com.printfarmer.ios", category: "PrinterDetail")
    @Environment(ServiceContainer.self) private var services
    @Environment(AppRouter.self) private var router
    @Environment(AuthViewModel.self) private var authViewModel
    @Environment(ServerRegistry.self) private var serverRegistry
    @Environment(\.dynamicTypeSize) private var dynamicTypeSize
    @Environment(\.scenePhase) private var scenePhase
    @State private var viewModel: PrinterDetailViewModel
    @State private var coverageViewModel: PrinterFilamentCoverageViewModel
    @State private var spoolLookup = PrinterDetailSpoolLookup()
    @State private var activeTasks: [Task<Void, Never>] = []
    @State private var guidedSwapTarget: AppRouter.FilamentSwapDeepLink?
    @State private var showsEjectConfirmation = false
    @State private var showsFilamentLoadConfirmation = false
    @State private var showsFilamentUnloadConfirmation = false
    @State private var showsSafetyChecks = false
    @State private var printPreviewImage: UIImage?
    @State private var printPreviewPath: String?
    // Transient UI state only (issue #2522) — never persisted, resets to
    // `.status` whenever this view is (re)constructed for a printer/server,
    // matching `viewModel`/`coverageViewModel`'s own per-identity lifetime.
    @State private var selectedPanel: PrinterDetailPanel = .status
    // The command owner outlives page visibility and access changes.
    @State private var controlsViewModel: PrinterControlsViewModel?
    @State private var controlsComposition: PrinterControlsComposition?

    private let printerId: UUID

    private var filamentCoverageEnabled: Bool {
        services.capabilitiesService.resolved.filamentCoverageEnabled
    }

    private var guidedSwapEnabled: Bool {
        services.capabilitiesService.resolved.guidedSwapEnabled
    }

    private var activeSpoolLookupAuthority: PrinterDetailSpoolLookupAuthority? {
        guard selectedPanel == .filament,
              authViewModel.isAuthenticated,
              let userID = authViewModel.currentUser?.id,
              let printer = viewModel.printer else {
            return nil
        }
        var spoolIDs = Set(viewModel.toolheads.compactMap(\.currentSpoolId))
        if let spool = viewModel.effectiveSpoolInfo,
           spool.hasActiveSpool,
           let spoolID = spool.activeSpoolId {
            spoolIDs.insert(spoolID)
        }
        guard !spoolIDs.isEmpty else { return nil }
        return PrinterDetailSpoolLookupAuthority(
            serverID: serverRegistry.activeServerID,
            userID: userID,
            generation: services.activeServerGeneration,
            printerID: printer.id,
            spoolIDs: spoolIDs.sorted()
        )
    }

    /// Pager neighbors stay mounted; camera work requires visible Status
    /// and an active application scene.
    private var isStatusPageForeground: Bool {
        PrinterDetailCameraLifecycleMapping.isForeground(
            scenePhase: scenePhase,
            selectedPanel: selectedPanel
        )
    }

    private var statusHeroHeight: CGFloat {
        dynamicTypeSize.isAccessibilitySize ? 196 : 168
    }

    init(printerId: UUID) {
        self.init(viewModel: PrinterDetailViewModel(printerId: printerId))
    }

    init(viewModel: PrinterDetailViewModel) {
        self.printerId = viewModel.printerId
        _viewModel = State(initialValue: viewModel)
        _coverageViewModel = State(initialValue: PrinterFilamentCoverageViewModel(printerId: viewModel.printerId))
    }

    var body: some View {
        VStack(spacing: 0) {
            // #789: shared stale banner — honest, read-only cached coverage.
            // Gated on `isStaleCacheReportable`, not `isShowingStaleCache`: the
            // latter is true from the instant the cache hydrates, which flashed
            // an "offline" banner on every healthy open of this screen.
            if filamentCoverageEnabled && coverageViewModel.isStaleCacheReportable {
                ConnectionStatusBar(
                    status: .offline,
                    lastConfirmedAt: coverageViewModel.cacheLastUpdatedAt,
                    hasCache: true
                )
            }
            Group {
                if let printer = viewModel.printer {
                    printerContent(printer)
                } else if let error = viewModel.errorMessage {
                    ContentUnavailableView {
                        Label("Error", systemImage: "exclamationmark.triangle")
                    } description: {
                        Text(error)
                    } actions: {
                        Button("Retry") {
                            let task = Task { await viewModel.loadPrinter() }
                            activeTasks.append(task)
                        }
                    }
                } else {
                    ProgressView("Loading printer…")
                        .frame(maxWidth: .infinity, maxHeight: .infinity)
                }
            }
        }
        // Stable, printer-scoped destination identifier so task-action routing
        // (#788) can assert it reached the exact printer and place a11y focus
        // there. Additive only — no behavior change.
        //
        // `.accessibilityElement(children: .contain)` (issue #2522): without
        // it, this identifier — set on a plain, non-rendering `VStack` —
        // bubbles down and OVERRIDES the explicit identifiers of multiple
        // distinct descendant elements (observed: the panel selector's own
        // `SegmentedControl` and the paging `TabView`'s internal
        // `CollectionView` both silently lost their own identifiers and
        // reported "printer.detail.root.<uuid>" instead once the
        // paged detail replaced the single old `ScrollView`).
        // `.contain` makes this VStack a genuine, opaque accessibility node
        // in its own right so its identifier stops leaking onto children
        // that already declare their own.
        .accessibilityElement(children: .contain)
        .accessibilityIdentifier("printer.detail.root.\(printerId.uuidString)")
        .navigationTitle("")
        .navigationBarBackButtonHidden()
        .toolbar(.hidden, for: .tabBar)
        .toolbar {
            ToolbarItem(placement: .topBarLeading) {
                Button {
                    Self.returnToFarm(router: router)
                } label: { Label("Farm", systemImage: "chevron.left") }
                    .accessibilityIdentifier("printer.detail.farm")
            }
            ToolbarItem(placement: .topBarTrailing) {
                if let baseURL = serverRegistry.activeServer?.baseURL {
                    Link(destination: baseURL.appendingPathComponent("printers")
                        .appendingPathComponent(printerId.uuidString.lowercased())) {
                        Label("Open in web", systemImage: "arrow.up.right")
                    }
                    .accessibilityIdentifier("printer.detail.web")
                }
            }
        }
        #if os(iOS)
        .navigationBarTitleDisplayMode(.inline)
        #endif
        .refreshable {
            await PrinterDetailViewLifecycle.refresh(
                viewModel: viewModel,
                coverageViewModel: coverageViewModel,
                refreshCoverage: filamentCoverageEnabled,
                snapshotPollingAllowed: { isStatusPageForeground }
            )
            await viewModel.loadFilamentCommandCapabilities()
        }
        .alert(
            viewModel.pendingAction?.title ?? "Confirm",
            isPresented: $viewModel.showConfirmation,
            presenting: viewModel.pendingAction
        ) { _ in
            Button("Cancel", role: .cancel) {}
            Button("Confirm", role: .destructive) {
                UIImpactFeedbackGenerator(style: .heavy).impactOccurred()
                let task = Task { await viewModel.confirmAction() }
                activeTasks.append(task)
            }
        } message: { action in
            Text(action.message)
        }
        .alert("Action Failed", isPresented: .constant(viewModel.actionError != nil)) {
            Button("OK") { viewModel.actionError = nil }
        } message: {
            if let error = viewModel.actionError {
                Text(error)
            }
        }
        .confirmationDialog(
            "Load filament?",
            isPresented: $showsFilamentLoadConfirmation,
            titleVisibility: .visible
        ) {
            Button("Load") { dispatchPhysicalFilamentCommand(load: true) }
            Button("Cancel", role: .cancel) {}
        } message: {
            Text("Requests a physical filament load. Confirm the assigned spool and printer before continuing.")
        }
        .confirmationDialog(
            "Unload filament?",
            isPresented: $showsFilamentUnloadConfirmation,
            titleVisibility: .visible
        ) {
            Button("Unload", role: .destructive) { dispatchPhysicalFilamentCommand(load: false) }
            Button("Cancel", role: .cancel) {}
        } message: {
            Text("Requests a physical filament unload. This does not clear the spool assignment.")
        }
        .alert("Start Failed", isPresented: .constant(viewModel.dispatchError != nil)) {
            Button("OK") { viewModel.dispatchError = nil }
        } message: {
            Text(viewModel.dispatchError ?? "")
        }
        .task {
            let generation = services.activeServerGeneration
            await services.awaitActiveServerSettled()
            guard !Task.isCancelled, services.activeServerGeneration == generation else { return }
            // Pin setup controls to the same composition used for detail data,
            // before the first await that loads that data.
            let composition = services.printerControlsComposition
            controlsComposition = composition
            viewModel.isViewActive = true
            viewModel.setSnapshotPollingAllowed(isStatusPageForeground)
            viewModel.configure(printerService: composition?.printerService ?? services.printerService)
            #if canImport(UIKit)
            if let nfc = services.nfcService {
                viewModel.configureNFCScanner(nfc)
            }
            #endif
            viewModel.configureSignalR(services.signalRService)
            viewModel.configurePredictive(services.predictiveService)
            viewModel.configureFailureDetection(services.failureDetectionService)
            viewModel.configureOperatorServices(
                jobService: services.jobService,
                maintenanceService: services.maintenanceService
            )
            await viewModel.loadPrinter()
            if let printer = viewModel.printer {
                await ensureControlsOwnerIfAvailable(for: printer)
            }
            await viewModel.loadOperatorSections()
            if let controlsViewModel {
                viewModel.adoptFilamentCommandCapabilities(
                    controlsViewModel.capabilities,
                    error: controlsViewModel.capabilityLoadError
                )
            } else {
                await viewModel.loadFilamentCommandCapabilities()
            }
            viewModel.setSnapshotPollingAllowed(isStatusPageForeground)

            // Handle NFC "mark ready" deep link
            if let pendingId = router.pendingNFCReadyPrinterId, pendingId == viewModel.printerId {
                await viewModel.prepareReadyConfirmation()
                router.pendingNFCReadyPrinterId = nil
            }
            if let target = router.pendingFilamentSwap, target.printerId == viewModel.printerId {
                router.pendingFilamentSwap = nil
                if guidedSwapEnabled {
                    guidedSwapTarget = target
                    viewModel.showSpoolPicker = true
                }
            }
        }
        .task(id: filamentCoverageEnabled) {
            guard filamentCoverageEnabled else {
                coverageViewModel.disableForCapabilityGate()
                return
            }
            coverageViewModel.configure(coverageService: services.filamentCoverageService)
            coverageViewModel.configureSignalR(services.signalRService)
            // #789: wire + hydrate this printer's coverage read-cache BEFORE the
            // canonical load so an offline detail launch shows honest stale data.
            coverageViewModel.configureCache(services.filamentCoverageReadCache)
            await coverageViewModel.hydrateFromCache()
            await coverageViewModel.load()
        }
        .task(id: activeSpoolLookupAuthority) {
            guard let authority = activeSpoolLookupAuthority else {
                spoolLookup.invalidate()
                return
            }
            await services.awaitActiveServerSettled()
            guard !Task.isCancelled,
                  services.isActiveGeneration(authority.generation),
                  activeSpoolLookupAuthority == authority else {
                return
            }
            let spoolService: any SpoolServiceProtocol = services.spoolService
            let authorityGeneration = authority.generation
            let isAuthorityCurrent: @MainActor () -> Bool = {
                activeSpoolLookupAuthority == authority
                    && services.isActiveGeneration(authorityGeneration)
            }
            await spoolLookup.load(
                service: spoolService,
                authority: authority,
                isCurrent: isAuthorityCurrent
            )
        }
        .onDisappear {
            spoolLookup.invalidate()
            activeTasks.forEach { $0.cancel() }
            activeTasks.removeAll()
            viewModel.isViewActive = false
            viewModel.stopSnapshotPolling()
            coverageViewModel.tearDownSignalR()
        }
        .onChange(of: scenePhase) { _, newPhase in
            switch newPhase {
            case .active:
                if viewModel.isViewActive {
                    let task = Task {
                        await PrinterDetailViewLifecycle.willEnterForeground(
                            viewModel: viewModel,
                            coverageViewModel: coverageViewModel,
                            refreshCoverage: filamentCoverageEnabled,
                            snapshotPollingAllowed: selectedPanel == .status
                        )
                    }
                    activeTasks.append(task)
                }
            case .inactive, .background:
                viewModel.setSnapshotPollingAllowed(false)
            @unknown default:
                viewModel.setSnapshotPollingAllowed(false)
            }
        }
        // Reacts to a page switch alone, independent of `scenePhase` (issue
        // #2522, Hicks review finding 19): leaving Status for another page
        // must stop camera polling immediately, not just the next
        // time the app backgrounds/foregrounds.
        .onChange(of: selectedPanel) { _, _ in
            viewModel.setSnapshotPollingAllowed(isStatusPageForeground)
        }
        .onChange(of: guidedSwapEnabled) { _, isEnabled in
            guard !isEnabled, guidedSwapTarget != nil else { return }
            guidedSwapTarget = nil
            viewModel.showSpoolPicker = false
        }
        .sheet(isPresented: $viewModel.showSpoolPicker) {
            SpoolPickerView { spool in
                let task = Task {
                    if let target = guidedSwapTarget {
                        await viewModel.bindToolheadSpool(spool, at: target.toolheadIndex)
                        guidedSwapTarget = nil
                    } else {
                        await viewModel.setActiveSpool(spool)
                    }
                }
                activeTasks.append(task)
            }
        }
        .sheet(isPresented: $viewModel.showScannedDataSheet) {
            if let data = viewModel.nfcScannedData {
                AddSpoolView(scannedData: data)
                    .onDisappear {
                        let task = Task { await viewModel.loadPrinter() }
                        activeTasks.append(task)
                    }
            }
        }
        .alert("Scan Error", isPresented: .constant(viewModel.nfcScanError != nil)) {
            Button("OK") { viewModel.nfcScanError = nil }
        } message: {
            if let error = viewModel.nfcScanError {
                Text(error)
            }
        }
        .alert("Mark Printer Ready?", isPresented: $viewModel.showNFCReadyConfirmation) {
            Button("Cancel", role: .cancel) {
                viewModel.reviewedReadyStatus = nil
            }
            Button("Mark Ready") {
                let task = Task { await viewModel.markPrinterReady() }
                activeTasks.append(task)
            }
        } message: {
            Text(
                "Clear the bed and confirm \(viewModel.reviewedReadyStatus?.nextJobName ?? "the reviewed next job")?"
            )
        }
    }

    // MARK: - Filament Section (issue #2522 — single #2519 PrinterFilamentSection,
    // replacing the old duplicate Filament Slots / Coverage / printer-spool blocks)

    private func filamentPresentation(_ printer: Printer) -> PrinterFilamentPresentation {
        let coverageState = PrinterDetailFilamentCoverageStateMapping.coverageState(
            featureEnabled: filamentCoverageEnabled,
            isFeatureDisabled: coverageViewModel.isFeatureDisabled,
            isPrinterNotFound: coverageViewModel.isPrinterNotFound,
            hasCoverage: coverageViewModel.coverage != nil,
            lastLoadError: coverageViewModel.lastLoadError
        )
        return PrinterFilamentPresentation(
            printer: printer,
            toolheads: viewModel.toolheads,
            spool: viewModel.effectiveSpoolInfo,
            coverage: coverageViewModel.coverage,
            coverageState: coverageState,
            // `PrinterDetailFilamentStaleMapping.isStale` — the RAW
            // `isShowingStaleCache` flag (Hicks review finding 16). While a
            // canonical refresh is still in flight the on-screen coverage is
            // UNCONFIRMED cached data; it must present as last-confirmed
            // with mutation actions disabled the whole time that flag is
            // set, not only once the refresh concludes. `isStaleCacheReportable`
            // (which additionally requires `hasConcludedCanonicalLoad`) stays
            // reserved for the connection-status banner above (line ~41),
            // which suppresses a premature "offline" flash — a different,
            // cosmetic concern from mutation-safety gating here.
            isStale: PrinterDetailFilamentStaleMapping.isStale(
                isShowingStaleCache: coverageViewModel.isShowingStaleCache
            ),
            supportedActions: PrinterDetailFilamentActionMapping.supportedActions(
                hasActiveSpool: viewModel.effectiveSpoolInfo?.hasActiveSpool ?? false
            )
        )
    }

    private func filamentActions(_ printer: Printer) -> [PrinterFilamentAction] {
        PrinterDetailFilamentActionMapping.actions(
            printerID: printer.id,
            hasActiveSpool: viewModel.effectiveSpoolInfo?.hasActiveSpool ?? false,
            isPerformingAction: viewModel.isPerformingAction,
            nfcAvailable: services.nfcService?.isAvailable ?? false,
            isOnline: printer.isOnline,
            printerState: printer.state
        )
    }

    @MainActor
    private func handleFilamentAction(_ action: PrinterFilamentAction) {
        switch action.kind {
        case .set, .change:
            viewModel.loadFilament()
        case .clearAssignment:
            // Assignment-only (issue #2522 / #2519 integration contract):
            // NEVER alias to `ejectFilament()`, which also dispatches a
            // physical `unloadFilament()` POST. That combined operation
            // remains a distinct view-model operation.
            UIImpactFeedbackGenerator(style: .heavy).impactOccurred()
            let task = Task { await viewModel.clearActiveSpoolAssignment() }
            activeTasks.append(task)
        case .scanNFC:
            viewModel.handleNFCScanToLoad()
        case .guidedSwap:
            // Not offered from this surface (see PrinterDetailFilamentActionMapping);
            // only reachable via the existing NFC deep link.
            break
        }
    }

    private var canStartPrinterCommand: Bool {
        guard authViewModel.isAuthenticated,
              !authViewModel.snapshotActivationPending,
              let user = authViewModel.currentUser,
              user.isActive else {
            return false
        }
        return user.permissions.contains("queue:start") || user.roles.contains("farm_admin")
    }

    private func physicalFilamentCommandBlockedReason(load: Bool) -> String? {
        guard canStartPrinterCommand else {
            return "Queue.Start permission is required to use physical filament controls."
        }
        return viewModel.physicalFilamentCommandBlockedReason(
            supports: load ? \.supportsFilamentLoad : \.supportsFilamentUnload
        )
    }

    private func physicalFilamentControls() -> some View {
        let loadReason = physicalFilamentCommandBlockedReason(load: true)
        let unloadReason = physicalFilamentCommandBlockedReason(load: false)
        return VStack(alignment: .leading, spacing: 8) {
            HStack(spacing: 12) {
                Button {
                    showsFilamentLoadConfirmation = true
                } label: {
                    Label("Load", systemImage: "arrow.down.to.line.compact")
                        .frame(maxWidth: .infinity, minHeight: 44)
                }
                .buttonStyle(.borderedProminent)
                .disabled(loadReason != nil)
                .accessibilityLabel("Load filament")
                .accessibilityHint(loadReason ?? "Requests a physical filament load.")
                .accessibilityIdentifier("printer.detail.filament.load")

                Button {
                    showsFilamentUnloadConfirmation = true
                } label: {
                    Label("Unload", systemImage: "arrow.up.to.line.compact")
                        .frame(maxWidth: .infinity, minHeight: 44)
                }
                .buttonStyle(.bordered)
                .disabled(unloadReason != nil)
                .accessibilityLabel("Unload filament")
                .accessibilityHint(unloadReason ?? "Requests a physical filament unload without changing its assignment.")
                .accessibilityIdentifier("printer.detail.filament.unload")
            }

            if viewModel.isActivelyPrinting {
                Text("Load and Unload are disabled while a print is active.")
                    .font(.footnote)
                    .foregroundStyle(Color.pfTextSecondary)
                    .accessibilityIdentifier("printer.detail.filament.printingLockout")
            } else if !canStartPrinterCommand {
                Text("Queue.Start permission is required to use physical filament controls.")
                    .font(.footnote)
                    .foregroundStyle(Color.pfTextSecondary)
            } else if let error = viewModel.filamentCommandCapabilitiesError {
                Text("Filament command support could not be confirmed: \(error)")
                    .font(.footnote)
                    .foregroundStyle(Color.pfTextSecondary)
            } else if viewModel.filamentCommandCapabilities == nil {
                Text("Checking backend filament-command support.")
                    .font(.footnote)
                    .foregroundStyle(Color.pfTextSecondary)
            } else if loadReason != nil || unloadReason != nil {
                Text(loadReason ?? unloadReason ?? "")
                    .font(.footnote)
                    .foregroundStyle(Color.pfTextSecondary)
            }
        }
        .accessibilityIdentifier("printer.detail.filament.physicalControls")
    }

    @MainActor
    private func dispatchPhysicalFilamentCommand(load: Bool) {
        if let reason = physicalFilamentCommandBlockedReason(load: load) {
            viewModel.actionError = reason
            return
        }
        UIImpactFeedbackGenerator(style: load ? .medium : .heavy).impactOccurred()
        let task = Task {
            if load {
                await viewModel.loadPhysicalFilament()
            } else {
                await viewModel.unloadPhysicalFilament()
            }
        }
        activeTasks.append(task)
    }

    // MARK: - Main Content

    private func controlsAvailable(for printer: Printer) -> Bool {
        AdvancedPrinterControlsAccess.isEntryVisible(
            isEnabled: serverRegistry.advancedPrinterControlsEnabled,
            for: printer
        )
    }

    /// A read-only/offline visit never fetches setup capabilities. Once built,
    /// retain the owner through tab, connectivity and preference transitions.
    @MainActor
    private func ensureControlsOwnerIfAvailable(for printer: Printer) async {
        guard !Task.isCancelled,
              let composition = controlsComposition,
              composition.identity == services.printerControlsComposition?.identity,
              PrinterDetailControlsOwnerMapping.shouldBuildOwner(
            existingOwnerPrinterID: controlsViewModel?.printer.id,
            printerID: printer.id,
            controlsAvailable: controlsAvailable(for: printer)
                || (printer.backend == .moonraker && serverRegistry.advancedPrinterControlsEnabled)
        ) else { return }
        let vm = PrinterControlsViewModel(composition: composition, printer: printer)
        controlsViewModel = vm
        await vm.loadCapabilities()
    }

    private func printerContent(_ printer: Printer) -> some View {
        PrinterDetailPanelsHost(
            selection: $selectedPanel,
            controlsAvailable: controlsAvailable(for: printer),
            printer: printer,
            status: { statusPage(printer) },
            control: { controlsPage(printer) },
            filament: { filamentPage(printer) },
            queue: {
                ScrollView { queueSection(printer).padding().padding(.bottom, 32) }
            }
        )
        // Construction remains authorization-gated even though both pages exist.
        .task(id: printer.id) {
            await ensureControlsOwnerIfAvailable(for: printer)
        }
        .onChange(of: controlsComposition?.identity) { _, _ in
            let task = Task { await ensureControlsOwnerIfAvailable(for: printer) }
            activeTasks.append(task)
        }
        .onChange(of: controlsAvailable(for: printer)) { _, isAvailable in
            guard isAvailable else { return }
            let task = Task { await ensureControlsOwnerIfAvailable(for: printer) }
            activeTasks.append(task)
        }
        // Forwards every meaningful live snapshot to the owner regardless of
        // whether the Controls page is currently mounted, so pending
        // jog/preheat/home commands still resolve — and offline updates
        // still land — while Controls is offscreen.
        .onChange(of: PrinterControlsUpdateSignal(printer: printer)) { _, _ in
            controlsViewModel?.handlePrinterUpdate(printer)
        }
        .modifier(PrinterControlsAccessLifecycle(viewModel: controlsViewModel))
        .modifier(PrinterDetailSafetyLifecycle(
            viewModel: controlsViewModel,
            observes: scenePhase == .active && (selectedPanel == .control || selectedPanel == .filament)
        ))
        .safeAreaInset(edge: .top, spacing: 0) {
            if selectedPanel == .control {
            let presentation = runActionPresentation(for: printer)
            VStack(alignment: .trailing, spacing: 4) {
                PrinterRunActionBar(
                    presentation: presentation.emergencyAction,
                    onSelect: { kind in handleRunAction(kind) }
                )
                if !printer.isOnline {
                    Text("Printer offline. Use the physical safety switch if needed.")
                        .font(.caption)
                        .foregroundStyle(Color.pfTextSecondary)
                        .fixedSize(horizontal: false, vertical: true)
                }
            }
            .frame(maxWidth: .infinity, alignment: .trailing)
            .padding(.horizontal)
            .padding(.vertical, 4)
            .background(.bar)
            }
        }
    }

    /// AnyLayout preserves child identity (especially the camera) on reflow.
    private func statusPage(_ printer: Printer) -> some View {
        GeometryReader { geometry in
            let columns = PrinterDetailLayout.usesColumns(
                width: geometry.size.width, dynamicTypeSize: dynamicTypeSize
            )
            ScrollView {
                VStack(alignment: .leading, spacing: 8) {
                    cameraSection(printer)
                        .frame(maxWidth: .infinity, alignment: .leading)
                    let layout = columns
                        ? AnyLayout(HStackLayout(alignment: .top, spacing: 24))
                        : AnyLayout(VStackLayout(alignment: .leading, spacing: 16))
                    layout {
                        currentJobBlock(printer)
                        temperatureSection(printer)
                    }
                    .accessibilityElement(children: .contain)
                    .accessibilityIdentifier(
                        columns ? "printer.detail.columns" : "printer.detail.readingColumn"
                    )
                    .frame(maxWidth: .infinity, alignment: .leading)
                }
                .padding()
                .accessibilityElement(children: .contain)
                .accessibilityIdentifier("printer.detail.status.content")
            }
            .accessibilityIdentifier("printer.detail.status.scroll")
        }
    }

    private func filamentPage(_ printer: Printer) -> some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 16) {
                PrinterFilamentSection(
                    presentation: filamentPresentation(printer),
                    actions: filamentActions(printer),
                    onAction: { handleFilamentAction($0) },
                    showsAllActions: true,
                    spoolDetailsByID: spoolLookup.spoolsByID,
                    spoolLookupMessage: spoolLookup.statusMessage
                )
                if let controlsViewModel, controlsAvailable(for: printer) {
                    safetyRefresh(controlsViewModel)
                    PrinterMaterialControls(viewModel: controlsViewModel)
                        .padding()
                        .operatorCard()
                } else {
                    controlsUnavailable(printer)
                }
                physicalFilamentControls()
            }
            .padding()
            .padding(.bottom, 32)
        }
    }

    /// Observe the persistent owner, never construct one inside a pager child.
    @ViewBuilder
    private func controlsPage(_ printer: Printer) -> some View {
        GeometryReader { geometry in
            ScrollView {
                VStack(alignment: .leading, spacing: 20) {
                    if !controlsAvailable(for: printer) {
                        controlsUnavailable(printer)
                    } else if let controlsViewModel {
                        PrinterSetupControlsContent(
                            printer: printer,
                            viewModel: controlsViewModel,
                            usesColumns: PrinterDetailLayout.usesColumns(
                                width: geometry.size.width, dynamicTypeSize: dynamicTypeSize
                            ),
                            showsMaterial: false,
                            showsRuntimeAdjustments: true,
                            usesHeaterSteppers: true,
                            isPrinterDetailControl: true
                        )
                        DisclosureGroup(isExpanded: safetyChecksExpanded) {
                            safetyRefresh(controlsViewModel)
                        } label: {
                            Label("Safety checks", systemImage: "checkmark.shield")
                                .font(.subheadline.weight(.semibold))
                                .foregroundStyle(Color.pfTextPrimary)
                                .frame(minHeight: 44, alignment: .leading)
                        }
                        .padding(.horizontal, 12)
                        .background(Color.pfBackground, in: RoundedRectangle(cornerRadius: 16))
                        .accessibilityElement(children: .contain)
                        .accessibilityIdentifier("printer.detail.safety.disclosure")
                    } else if controlsComposition == nil
                        || controlsComposition?.identity != services.printerControlsComposition?.identity {
                        Text("Controls require a settled registered server connection. Reopen this printer after reconnecting.")
                            .font(.footnote)
                    } else {
                        ProgressView("Loading controls...")
                    }
                }
                .frame(maxWidth: .infinity, alignment: .leading)
                .padding(.horizontal, PrinterDetailLayout.usesColumns(
                    width: geometry.size.width, dynamicTypeSize: dynamicTypeSize
                ) ? 24 : 16)
                .padding(.top, 16)
                .padding(.bottom, 22)
            }
            .background(Color.pfBackgroundTertiary)
        }
    }

    private func controlsUnavailable(_ printer: Printer) -> some View {
        VStack(alignment: .leading, spacing: 8) {
            Label("Controls unavailable", systemImage: "lock.fill")
                .font(.headline)
            if !printer.isOnline {
                Text("This printer is offline. Reconnect before using setup controls.")
            }
            if !serverRegistry.advancedPrinterControlsEnabled {
                Text("Advanced Printer Controls are off for this server. Review Printer Safety in Settings to enable them.")
                NavigationLink(value: AppDestination.settings) {
                    Label("Printer Safety Settings", systemImage: "gearshape")
                        .frame(minHeight: 44)
                }
                .accessibilityIdentifier("printer.detail.control.settings")
            }
        }
        .fixedSize(horizontal: false, vertical: true)
        .padding()
        .operatorCard()
        .accessibilityElement(children: .contain)
        .accessibilityIdentifier("printer.detail.control.unavailable")
    }

    private func safetyRefresh(_ owner: PrinterControlsViewModel) -> some View {
        PrinterDetailSafetyRefresh(owner: owner) {
            let task = Task { await owner.refreshSafetyEvidence() }
            activeTasks.append(task)
        }
    }

    private var safetyChecksExpanded: Binding<Bool> {
        Binding(
            get: { showsSafetyChecks || controlsViewModel?.safetyReadError != nil },
            set: { if controlsViewModel?.safetyReadError == nil { showsSafetyChecks = $0 } }
        )
    }

    private struct PrinterDetailSafetyDemand: Equatable {
        let observes: Bool
        let owner: ObjectIdentifier?
        let loadingCapabilities: Bool
    }

    private struct PrinterDetailSafetyLifecycle: ViewModifier {
        let viewModel: PrinterControlsViewModel?
        let observes: Bool

        func body(content: Content) -> some View {
            content.background {
                if let viewModel {
                    Color.clear
                        .modifier(ObservedPrinterDetailSafetyLifecycle(viewModel: viewModel, observes: observes))
                        .allowsHitTesting(false)
                        .accessibilityHidden(true)
                }
            }
        }
    }

    // This legacy ObservableObject must be observed at the safety task's host,
    // not merely by pager children, so capability completion restarts the task.
    private struct ObservedPrinterDetailSafetyLifecycle: ViewModifier {
        @ObservedObject var viewModel: PrinterControlsViewModel
        let observes: Bool

        func body(content: Content) -> some View {
            content
                .task(id: PrinterDetailSafetyDemand(
                    observes: observes,
                    owner: ObjectIdentifier(viewModel),
                    loadingCapabilities: viewModel.isLoadingCapabilities
                )) {
                    guard observes else {
                        viewModel.suspendSafetyObservation()
                        return
                    }
                    guard !viewModel.isLoadingCapabilities else { return }
                    await viewModel.refreshSafetyEvidence()
                    var cadence = PrinterDetailSafetyRefreshCadence()
                    while !Task.isCancelled && viewModel.isActive {
                        do { try await Task.sleep(for: PrinterDetailSafetyRefreshCadence.statusInterval) } catch { return }
                        let refreshDiscovery = cadence.shouldRefreshDiscovery(at: .now)
                        await viewModel.refreshSafetyEvidence(refreshDiscovery: refreshDiscovery)
                    }
                }
                .onDisappear { viewModel.suspendSafetyObservation() }
        }
    }

    /// Eject clears assignment and requests physical unload; Unassign only
    /// clears inventory assignment.
    @ViewBuilder
    private func ejectFilamentUtility(_ printer: Printer) -> some View {
        if viewModel.effectiveSpoolInfo?.hasActiveSpool ?? false {
            PrinterDetailBorderedDestructiveButton(kind: .eject) {
                showsEjectConfirmation = true
            }
            .disabled(viewModel.isPerformingAction)
            .accessibilityLabel("Eject filament: clears the spool assignment and physically unloads")
            .accessibilityIdentifier("printer.detail.filament.ejectFilament")
            .confirmationDialog("Eject filament?", isPresented: $showsEjectConfirmation, titleVisibility: .visible) {
                Button("Eject", role: .destructive) {
                    guard viewModel.printer?.isOnline == true, !viewModel.isActivelyPrinting else {
                        viewModel.actionError = "The printer must be online and idle to eject filament."
                        return
                    }
                    UIImpactFeedbackGenerator(style: .heavy).impactOccurred()
                    let task = Task { await viewModel.ejectFilament() }
                    activeTasks.append(task)
                }
                Button("Cancel", role: .cancel) {}
            } message: {
                Text("Clears the spool assignment and requests physical unload. Check the printer before confirming.")
            }
        }
    }

    // MARK: - Shared Run-Action Bar (issue #2522 / #2520)

    private func runActionPresentation(for printer: Printer) -> PrinterRunActionPresentation {
        PrinterDetailRunActionMapping.presentation(
            isOnline: printer.isOnline,
            isPrinting: viewModel.isPrinting,
            isPaused: viewModel.isPaused,
            isPerformingAction: viewModel.isPerformingAction,
            pendingKinds: viewModel.pendingRunActionKinds
        )
    }

    @MainActor
    private func handleRunAction(_ kind: PrinterRunActionKind) {
        switch kind {
        case .pause:
            UIImpactFeedbackGenerator(style: .medium).impactOccurred()
            let task = Task { await viewModel.pausePrinter() }
            activeTasks.append(task)
        case .resume:
            UIImpactFeedbackGenerator(style: .medium).impactOccurred()
            let task = Task { await viewModel.resumePrinter() }
            activeTasks.append(task)
        case .cancel:
            UIImpactFeedbackGenerator(style: .heavy).impactOccurred()
            viewModel.requestCancel()
        case .stop:
            UIImpactFeedbackGenerator(style: .heavy).impactOccurred()
            let task = Task { await viewModel.stopPrinter() }
            activeTasks.append(task)
        case .emergencyStop:
            viewModel.requestEmergencyStop()
        }
    }

    // MARK: - Current Job Block (progress · ETA · coverage · part)

    @ViewBuilder
    private func currentJobBlock(_ printer: Printer) -> some View {
        let jobName = printer.fileName ?? printer.jobName ?? viewModel.currentJob?.jobName
        VStack(alignment: .leading, spacing: 8) {
            if viewModel.isActivelyPrinting || jobName != nil {
                HStack(alignment: .firstTextBaseline, spacing: 12) {
                    if let progress = printer.progress, progress.isFinite {
                        Text("\(Int((min(max(progress, 0), 1) * 100).rounded()))%")
                            .font(.system(size: 34, weight: .bold, design: .rounded))
                            .monospacedDigit()
                            .accessibilityLabel("Print progress \(Int((min(max(progress, 0), 1) * 100).rounded())) percent")
                    }
                    Text(jobName ?? "Printing")
                        .font(.headline)
                        .lineLimit(2)
                        .fixedSize(horizontal: false, vertical: true)
                    Spacer(minLength: 0)
                }

                if let progress = printer.progress, progress.isFinite {
                    PrintProgressBar(progress: min(max(progress, 0), 1), height: 6)
                        .accessibilityIdentifier("printer.detail.job.progress")
                }

                HStack(alignment: .top, spacing: 8) {
                    statusFact("Left", value: viewModel.formattedTimeRemaining ?? "Unknown")
                    statusFact("Done at", value: viewModel.formattedEtaClock ?? "Unknown")
                    statusFact("Layer", value: layerLabel(printer))
                }
                .padding(.vertical, 2)
            } else {
                operatorEmptyState(icon: "pause.circle", message: "No active print")
            }
            PrinterRunActionBar(
                presentation: statusActions(for: printer),
                onSelect: { kind in handleRunAction(kind) }
            )
        }
        .padding(.horizontal, 2)
        .accessibilityIdentifier("printer.detail.job")
    }

    private func layerLabel(_ printer: Printer) -> String {
        let current = printer.currentLayer ?? viewModel.statusDetail?.currentLayer
        let total = printer.totalLayers ?? viewModel.statusDetail?.totalLayers
        guard let current, let total,
              current >= 0, total > 0, current <= total else {
            return "Unavailable"
        }
        return "\(current)/\(total)"
    }

    private func statusActions(for printer: Printer) -> PrinterRunActionPresentation {
        let allowed: Set<PrinterRunActionKind> = [.pause, .resume, .cancel]
        return PrinterRunActionPresentation(
            descriptors: runActionPresentation(for: printer).visibleDescriptors.filter {
                allowed.contains($0.kind)
            }
        )
    }

    private func statusFact(_ title: String, value: String) -> some View {
        VStack(alignment: .leading, spacing: 4) {
            Text(title).font(.caption).foregroundStyle(Color.pfTextSecondary)
            Text(value).font(.subheadline.weight(.semibold)).monospacedDigit()
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .accessibilityElement(children: .combine)
    }

    // MARK: - Queue (server-ordered assigned jobs + reviewed-head start)

    private func queueSection(_ printer: Printer) -> some View {
        VStack(alignment: .leading, spacing: 12) {
            let active = viewModel.displayedQueueJobs.filter {
                ["starting", "printing", "paused"].contains($0.job.status.lowercased())
            }
            let assigned = viewModel.displayedQueueJobs.filter {
                $0.job.status.lowercased() == "assigned"
            }
            let queued = viewModel.nextQueuedJobs

            if active.isEmpty && assigned.isEmpty && queued.isEmpty {
                operatorEmptyState(icon: "tray", message: "No active or queued jobs for this printer")
            } else {
                if !active.isEmpty {
                    queueSectionHeader("Printing", count: active.count)
                    ForEach(active) { job in compactQueueRow(job) }
                }
                if !assigned.isEmpty {
                    queueSectionHeader("Assigned", count: assigned.count)
                    ForEach(assigned) { job in compactQueueRow(job) }
                }
                if let next = queued.first {
                    VStack(alignment: .leading, spacing: 10) {
                        Text("Up next")
                            .font(.headline)
                        queueRow(next, printer: printer)
                    }
                    .padding(14)
                    .background(Color.pfSuccess.opacity(0.1), in: RoundedRectangle(cornerRadius: 14))
                    .overlay(
                        RoundedRectangle(cornerRadius: 14)
                            .strokeBorder(Color.pfSuccess.opacity(0.3), lineWidth: 1)
                    )
                }
                let later = Array(queued.dropFirst())
                if !later.isEmpty {
                    queueSectionHeader("Then", count: later.count)
                    ForEach(later) { job in compactQueueRow(job) }
                }
            }
        }
        .accessibilityElement(children: .contain)
        .accessibilityIdentifier("printer.detail.queue")
    }

    private func queueSectionHeader(_ title: String, count: Int) -> some View {
        HStack {
            Text(title)
                .font(.subheadline.weight(.semibold))
                .foregroundStyle(Color.pfTextSecondary)
            Spacer()
            Text("\(count)")
                .font(.caption.monospacedDigit())
                .foregroundStyle(Color.pfTextTertiary)
        }
        .padding(.top, 4)
        .accessibilityElement(children: .combine)
    }

    private func compactQueueRow(_ job: QueuedPrintJobResponse) -> some View {
        let title = job.gcodeFile?.name ?? job.job.name
        return HStack(spacing: 10) {
            remoteThumbnail(job.gcodeFile?.thumbnailUrl ?? job.job.thumbnailUrl, size: 40)
            VStack(alignment: .leading, spacing: 3) {
                Text(title)
                    .font(.subheadline.weight(.medium))
                    .lineLimit(1)
                Text(job.job.printerName ?? job.job.status)
                    .font(.caption)
                    .foregroundStyle(Color.pfTextSecondary)
                    .lineLimit(1)
            }
            Spacer()
            if job.job.priority == .high || job.job.priority == .urgent {
                Text(job.job.priority == .urgent ? "Urgent" : "High")
                    .font(.caption2.weight(.semibold))
                    .foregroundStyle(job.job.priority == .urgent ? Color.pfError : Color.pfWarning)
            }
            if let duration = job.job.estimatedDuration {
                Text(duration.durationFormatted)
                    .font(.caption.monospacedDigit())
                    .foregroundStyle(Color.pfTextSecondary)
            }
        }
        .padding(.vertical, 6)
        .accessibilityElement(children: .combine)
        .accessibilityIdentifier("printer.detail.queue.row.\(job.id)")
    }

    private func queueRow(_ job: QueuedPrintJobResponse, printer: Printer) -> some View {
        let match = viewModel.matchState(for: job)
        let title = job.gcodeFile?.name ?? job.job.name
        return VStack(alignment: .leading, spacing: 10) {
            HStack(alignment: .top, spacing: 12) {
                remoteThumbnail(job.gcodeFile?.thumbnailUrl ?? job.job.thumbnailUrl, size: 48)
                VStack(alignment: .leading, spacing: 5) {
                    Text(title)
                        .font(.headline)
                        .fixedSize(horizontal: false, vertical: true)
                    HStack(spacing: 6) {
                        Text(job.gcodeFile?.materialType ?? job.job.filamentName ?? "Material unavailable")
                            .font(.caption)
                            .foregroundStyle(Color.pfTextSecondary)
                        if let duration = job.job.estimatedDuration {
                            Text("· \(duration.durationFormatted)")
                                .font(.caption.monospacedDigit())
                                .foregroundStyle(Color.pfTextSecondary)
                        }
                    }
                }
                Spacer(minLength: 4)
                if job.job.priority == .high || job.job.priority == .urgent {
                    Text(job.job.priority == .urgent ? "Urgent" : "High")
                        .font(.caption2.weight(.semibold))
                        .foregroundStyle(job.job.priority == .urgent ? Color.pfError : Color.pfWarning)
                }
            }

            HStack(spacing: 6) {
                Image(systemName: match.systemImage)
                    .font(.caption2)
                    .accessibilityHidden(true)
                Text(match.label)
                    .font(.caption)
            }
            .foregroundStyle(matchTint(match))
            .accessibilityElement(children: .combine)
            .accessibilityLabel(match.label)

            Button {
                let task = Task { await viewModel.startNextJob(job) }
                activeTasks.append(task)
            } label: {
                Label("Start next job", systemImage: "play.fill")
                    .font(.subheadline)
                    .frame(maxWidth: .infinity, minHeight: 44)
            }
            .buttonStyle(.borderedProminent)
            .tint(Color.pfSuccess)
            .disabled(!printer.isOnline || !viewModel.isIdle
                      || viewModel.isDispatching || viewModel.isPerformingAction
                      || job.job.rowVersion?.isEmpty != false)
            .disabled(!(authViewModel.currentUser?.permissions.contains("queue:start") == true
                        || authViewModel.currentUser?.roles.contains("farm_admin") == true))
            .accessibilityIdentifier("printer.detail.queue.dispatch.\(job.id)")
            .accessibilityLabel("Start \(title) on this printer")
        }
        .accessibilityElement(children: .contain)
        .accessibilityIdentifier("printer.detail.queue.row.\(job.id)")
    }

    static func returnToFarm(router: AppRouter) {
        router.invalidatePendingNavigation()
        router.selectedTab = .farm
    }

    private func matchTint(_ state: PrinterDetailViewModel.QueueMatchState) -> Color {
        switch state {
        case .match: Color.green
        case .mismatch: Color.pfError
        case .unknown: Color.pfTextSecondary
        }
    }

    // MARK: - Shared operator helpers

    private func operatorEmptyState(icon: String, message: String) -> some View {
        HStack(spacing: 8) {
            Image(systemName: icon)
                .font(.subheadline)
                .foregroundStyle(Color.pfTextTertiary)
                .accessibilityHidden(true)
            Text(message)
                .font(.subheadline)
                .foregroundStyle(Color.pfTextSecondary)
                .fixedSize(horizontal: false, vertical: true)
            Spacer(minLength: 0)
        }
        .padding()
        .operatorCard()
    }

    @ViewBuilder
    private func remoteThumbnail(_ urlString: String?, size: CGFloat) -> some View {
        if let urlString,
           let baseURL = APIClient.savedBaseURL(),
           let url = URL(string: urlString, relativeTo: baseURL) {
            AsyncImage(url: url) { phase in
                switch phase {
                case .success(let image):
                    image
                        .resizable()
                        .aspectRatio(contentMode: .fill)
                default:
                    Color.pfBackgroundTertiary
                }
            }
            .frame(width: size, height: size)
            .clipShape(RoundedRectangle(cornerRadius: 8))
            .accessibilityHidden(true)
        }
    }

    // MARK: - Temperatures

    private func temperatureSection(_ printer: Printer) -> some View {
        // Prefer printer temps (from CompletePrinterDto), fall back to statusDetail
        // (from /status endpoint). PrusaLink's PrinterDto omits temps but /status has them.
        let hotend = printer.hotendTemp ?? viewModel.statusDetail?.hotendTemp
        let hotendTgt = printer.hotendTarget ?? viewModel.statusDetail?.hotendTarget
        let bed = printer.bedTemp ?? viewModel.statusDetail?.bedTemp
        let bedTgt = printer.bedTarget ?? viewModel.statusDetail?.bedTarget

        return PrinterDetailTemperatureStrip(
            hotend: .init(measured: hotend, target: hotendTgt, isOnline: printer.isOnline),
            bed: .init(measured: bed, target: bedTgt, isOnline: printer.isOnline)
        )
    }

    // MARK: - Camera Snapshot

    private func cameraSection(_ printer: Printer) -> some View {
        let hasCamera = hasUsableCamera(for: printer)
        let previewPath = viewModel.currentJobThumbnailUrl
        let previewRequest = "\(printer.id.uuidString)|\(printer.state ?? "")|\(previewPath ?? "")|\(hasCamera)"
        return ZStack(alignment: .top) {
            Group {
                if !hasCamera {
                    if let printPreviewImage, printPreviewPath == previewPath {
                        Image(uiImage: printPreviewImage)
                            .resizable()
                            .scaledToFit()
                            .frame(maxWidth: .infinity, maxHeight: .infinity)
                            .accessibilityLabel("Current print preview")
                    } else {
                        noCameraPlaceholder()
                    }
                } else {
                    cameraPreview(printer)
                }
            }
            .frame(maxWidth: .infinity, maxHeight: .infinity)

            HStack(spacing: 8) {
                Text(hasCamera ? "Camera" : previewPath != nil ? "Print image" : "Camera")
                    .font(.caption.weight(.semibold))
                    .padding(.horizontal, 8)
                    .padding(.vertical, 5)
                    .background(.ultraThinMaterial, in: Capsule())
                if viewModel.canShowLivestream || viewModel.cameraPreviewMode == .snapshotPolling {
                    Text(viewModel.showLivestream && viewModel.canShowLivestream ? "LIVE" : "SNAPSHOT")
                        .font(.caption2.weight(.bold))
                        .foregroundStyle(viewModel.showLivestream && viewModel.canShowLivestream ? .white : Color.pfTextSecondary)
                        .padding(.horizontal, 6)
                        .padding(.vertical, 2)
                        .background(
                            viewModel.showLivestream && viewModel.canShowLivestream ? Color.red : Color.pfBorder,
                            in: Capsule()
                        )
                }

                Spacer()

                if viewModel.canShowLivestream {
                    Button {
                        withAnimation { viewModel.showLivestream.toggle() }
                    } label: {
                        Image(systemName: viewModel.showLivestream ? "photo" : "video.fill")
                            .font(.subheadline.weight(.semibold))
                            .frame(width: 44, height: 44)
                            .background(.ultraThinMaterial, in: Circle())
                    }
                    .accessibilityLabel(viewModel.showLivestream ? "Switch to snapshot" : "Switch to livestream")
                    .accessibilityIdentifier("printer.detail.camera.livetoggle")
                }

                if viewModel.cameraPreviewMode != .unsupported,
                   viewModel.snapshotData != nil || printer.cameraSnapshotUrl != nil || viewModel.cameraPreviewMode == .snapshotPolling {
                    Button {
                        viewModel.rotateCameraView()
                    } label: {
                        Image(systemName: "rotate.right")
                            .font(.subheadline.weight(.semibold))
                            .frame(width: 44, height: 44)
                            .background(.ultraThinMaterial, in: Circle())
                    }
                    .accessibilityLabel("Rotate camera view")
                    
                    if !viewModel.showLivestream || viewModel.cameraPreviewMode == .snapshotPolling {
                        Button {
                            let task = Task { _ = await viewModel.refreshSnapshot() }
                            activeTasks.append(task)
                        } label: {
                            Image(systemName: "arrow.clockwise")
                                .font(.subheadline.weight(.semibold))
                                .frame(width: 44, height: 44)
                                .background(.ultraThinMaterial, in: Circle())
                        }
                        .disabled(viewModel.isLoadingSnapshot)
                        .accessibilityLabel("Refresh camera snapshot")
                    }
                }
            }
            .padding(8)
        }
        .frame(height: statusHeroHeight)
        .clipped()
        .background(Color.pfCard, in: RoundedRectangle(cornerRadius: 12))
        .overlay(
            RoundedRectangle(cornerRadius: 12)
                .strokeBorder(Color.pfBorder, lineWidth: 1)
        )
        .accessibilityIdentifier("printer.detail.hero")
        .task(id: previewRequest) {
            await loadPrintPreview(printer: printer, path: previewPath, hasCamera: hasCamera)
        }
        .onDisappear {
            printPreviewImage = nil
            printPreviewPath = nil
        }
    }

    private func hasUsableCamera(for printer: Printer) -> Bool {
        if viewModel.snapshotData != nil { return true }
        switch viewModel.cameraPreviewMode {
        case .mjpegStream:
            return viewModel.showLivestream
                ? validURL(printer.cameraStreamUrl) || validURL(printer.cameraSnapshotUrl)
                : validURL(printer.cameraSnapshotUrl)
        case .snapshotPolling, .directSnapshot:
            return viewModel.cameraPreviewMode == .snapshotPolling
                ? printer.isOnline
                : validURL(printer.cameraSnapshotUrl)
        case .unsupported, .none:
            return false
        }
    }

    private func validURL(_ value: String?) -> Bool {
        guard let value, let url = URL(string: value) else { return false }
        return url.scheme == "http" || url.scheme == "https"
    }

    @MainActor
    private func loadPrintPreview(printer: Printer, path: String?, hasCamera: Bool) async {
        printPreviewImage = nil
        printPreviewPath = nil
        guard !hasCamera,
              ["printing", "paused"].contains(printer.state?.lowercased() ?? ""),
              let path else { return }
        do {
            let data = try await services.printerService.getCurrentJobThumbnail(id: printer.id, path: path)
            guard !Task.isCancelled,
                  viewModel.printer?.id == printer.id,
                  viewModel.currentJobThumbnailUrl == path,
                  let image = UIImage(data: data) else { return }
            printPreviewImage = image
            printPreviewPath = path
        } catch {
            guard !Task.isCancelled else { return }
            printPreviewImage = nil
            printPreviewPath = nil
            Self.logger.notice("Current-job preview unavailable")
        }
    }

    @ViewBuilder
    private func cameraPreview(_ printer: Printer) -> some View {
        switch viewModel.cameraPreviewMode {
        case .mjpegStream:
            #if canImport(UIKit)
            // `isStatusPageForeground` (issue #2522, Hicks review finding
            // 19): native `TabView` paging keeps this page mounted
            // alongside Controls for swipe animation, so without this gate
            // the live `MJPEGStreamContainer` (a `UIViewRepresentable`
            // backed by a persistent WKWebView connection) would keep
            // streaming off-screen. Falling through to the snapshot/
            // placeholder branches when not foreground tears the stream
            // down without touching `viewModel.showLivestream` or
            // `cameraRotation`, so returning to Status resumes the SAME
            // camera state the user left, not a reset one.
            if viewModel.showLivestream,
               isStatusPageForeground,
               let streamUrlString = printer.cameraStreamUrl,
               let streamUrl = URL(string: streamUrlString) {
                MJPEGStreamContainer(url: streamUrl, rotation: viewModel.cameraRotation)
            } else if let data = viewModel.snapshotData {
                snapshotImage(from: data)
            } else if let urlString = printer.cameraSnapshotUrl,
                      let url = URL(string: urlString) {
                asyncSnapshotImage(url: url)
            } else {
                noCameraPlaceholder()
            }
            #else
            if let data = viewModel.snapshotData {
                snapshotImage(from: data)
            } else if let urlString = printer.cameraSnapshotUrl,
                      let url = URL(string: urlString) {
                asyncSnapshotImage(url: url)
            } else {
                noCameraPlaceholder()
            }
            #endif
        case .snapshotPolling:
            if let data = viewModel.snapshotData {
                snapshotImage(from: data)
            } else if viewModel.isLoadingSnapshot {
                loadingSnapshotPlaceholder()
            } else {
                snapshotUnavailable()
            }
        case .directSnapshot:
            if let data = viewModel.snapshotData {
                snapshotImage(from: data)
            } else if let urlString = printer.cameraSnapshotUrl,
                      let url = URL(string: urlString) {
                asyncSnapshotImage(url: url)
            } else {
                snapshotUnavailable()
            }
        case .unsupported:
            unsupportedCameraPlaceholder()
        case .none:
            noCameraPlaceholder()
        }
    }

    #if canImport(UIKit)
    private func snapshotImage(from data: Data) -> some View {
        Group {
            if let uiImage = UIImage(data: data) {
                Image(uiImage: uiImage)
                    .resizable()
                    .aspectRatio(contentMode: .fit)
                    .rotationEffect(.degrees(Double(viewModel.cameraRotation)))
                    .clipShape(RoundedRectangle(cornerRadius: 12))
            } else {
                snapshotUnavailable()
            }
        }
    }
    #else
    private func snapshotImage(from data: Data) -> some View {
        snapshotUnavailable()
    }
    #endif

    private func asyncSnapshotImage(url: URL) -> some View {
        AsyncImage(url: url) { phase in
            switch phase {
            case .success(let image):
                image
                    .resizable()
                    .aspectRatio(contentMode: .fit)
                    .rotationEffect(.degrees(Double(viewModel.cameraRotation)))
                    .clipShape(RoundedRectangle(cornerRadius: 12))
            case .failure:
                snapshotUnavailable()
            case .empty:
                ProgressView()
                    .frame(height: 200)
                    .frame(maxWidth: .infinity)
            @unknown default:
                EmptyView()
            }
        }
    }

    private func noCameraPlaceholder() -> some View {
        VStack(spacing: 8) {
            Image(systemName: "camera.fill")
                .font(.title)
                .foregroundStyle(Color.pfTextTertiary)
            Text("No camera available")
                .font(.subheadline)
                .foregroundStyle(Color.pfTextSecondary)
        }
        .frame(height: 120)
        .frame(maxWidth: .infinity)
    }

    private func unsupportedCameraPlaceholder() -> some View {
        VStack(spacing: 8) {
            Image(systemName: "video.slash.fill")
                .font(.title)
                .foregroundStyle(Color.pfTextTertiary)
            Text("No live preview available")
                .font(.subheadline)
                .foregroundStyle(Color.pfTextSecondary)
        }
        .frame(height: 120)
        .frame(maxWidth: .infinity)
    }

    private func loadingSnapshotPlaceholder() -> some View {
        VStack(spacing: 8) {
            ProgressView()
            Text("Loading snapshot…")
                .font(.caption)
                .foregroundStyle(Color.pfTextSecondary)
        }
        .frame(height: 200)
        .frame(maxWidth: .infinity)
    }

    private func snapshotUnavailable() -> some View {
        VStack(spacing: 8) {
            Image(systemName: "photo.badge.exclamationmark")
                .font(.title)
                .foregroundStyle(Color.pfTextTertiary)
            Text("Snapshot unavailable")
                .font(.subheadline)
                .foregroundStyle(Color.pfTextSecondary)
        }
        .frame(height: 200)
        .frame(maxWidth: .infinity)
    }
}

struct PrinterDetailBorderedDestructiveButton: View {
    enum Kind {
        case eject
        case cancel

        var title: String {
            switch self {
            case .eject: "Eject"
            case .cancel: "Cancel"
            }
        }

        var systemImage: String {
            switch self {
            case .eject: "eject.fill"
            case .cancel: "xmark.circle.fill"
            }
        }

        var minimumHeight: CGFloat? {
            switch self {
            case .eject: nil
            case .cancel: 44
            }
        }
    }

    let kind: Kind
    let action: () -> Void

    var body: some View {
        Button(role: .destructive, action: action) {
            Label(kind.title, systemImage: kind.systemImage)
                .frame(maxWidth: .infinity, minHeight: kind.minimumHeight)
        }
        .buttonStyle(.bordered)
        .tint(Color.pfError)
    }
}

@MainActor
enum PrinterDetailViewLifecycle {
    /// - Parameter snapshotPollingAllowed: a closure, not a precomputed
    ///   `Bool` (issue #2522, Hicks review finding 23). This is applied
    ///   AFTER `loadPrinter()`/`coverageViewModel.load()`'s awaits, which a
    ///   pull-to-refresh can leave in flight for a while; evaluating the
    ///   gate eagerly at the call site — as a plain `Bool` argument would —
    ///   captures whatever page/scene state was current when refresh
    ///   STARTED, not when it actually applies the result. If the operator
    ///   switches pages mid-refresh (Status → Control or back), that stale
    ///   snapshot would restart polling on a now-hidden Controls page, or
    ///   stop it on a now-visible Status page — the opposite of current
    ///   reality. A closure re-reads the caller's live state at the exact
    ///   moment it is invoked, below.
    static func refresh(
        viewModel: PrinterDetailViewModel,
        coverageViewModel: PrinterFilamentCoverageViewModel,
        refreshCoverage: Bool,
        snapshotPollingAllowed: @escaping () -> Bool
    ) async {
        await viewModel.loadPrinter()
        if refreshCoverage {
            await coverageViewModel.load()
        }
        viewModel.setSnapshotPollingAllowed(snapshotPollingAllowed())
    }

    static func willEnterForeground(
        viewModel: PrinterDetailViewModel,
        coverageViewModel: PrinterFilamentCoverageViewModel,
        refreshCoverage: Bool,
        snapshotPollingAllowed: Bool
    ) async {
        guard viewModel.isViewActive else { return }
        viewModel.setSnapshotPollingAllowed(snapshotPollingAllowed)
        if refreshCoverage {
            await coverageViewModel.load()
        }
    }
}

// MARK: - Operator card styling (issue #712)

private extension View {
    /// Shared card chrome for the operator-first sections so every block reads
    /// as a consistent, tappable surface.
    func operatorCard() -> some View {
        background(Color.pfCard, in: RoundedRectangle(cornerRadius: 12))
            .overlay(
                RoundedRectangle(cornerRadius: 12)
                    .strokeBorder(Color.pfBorder, lineWidth: 1)
            )
    }
}
