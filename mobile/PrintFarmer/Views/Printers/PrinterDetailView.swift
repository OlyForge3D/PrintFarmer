import SwiftUI

struct PrinterDetailView: View {
    @Environment(ServiceContainer.self) private var services
    @Environment(AppRouter.self) private var router
    @Environment(AuthViewModel.self) private var authViewModel
    @Environment(ServerRegistry.self) private var serverRegistry
    @Environment(\.dynamicTypeSize) private var dynamicTypeSize
    @Environment(\.scenePhase) private var scenePhase
    @State private var viewModel: PrinterDetailViewModel
    @State private var coverageViewModel: PrinterFilamentCoverageViewModel
    @State private var activeTasks: [Task<Void, Never>] = []
    @State private var guidedSwapTarget: AppRouter.FilamentSwapDeepLink?
    // Transient UI state only (issue #2522) — never persisted, resets to
    // `.overview` whenever this view is (re)constructed for a printer/server,
    // matching `viewModel`/`coverageViewModel`'s own per-identity lifetime.
    @State private var selectedPanel: PrinterDetailPanel = .overview
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

    /// Gates camera snapshot polling and MJPEG stream mounting (issue #2522,
    /// Hicks review finding 19). Native `TabView` paging keeps the adjacent
    /// page mounted for swipe animation, so `overviewPage`'s `cameraSection`
    /// stays alive — and, without this gate, kept polling/streaming — even
    /// while the Controls page is the one on screen. Combines BOTH
    /// conditions the pre-#2522 single-page screen never had to distinguish:
    /// the existing `scenePhase == .active` foreground gate, and now also
    /// `selectedPanel == .overview`, so leaving the Overview page (Controls
    /// selected) stops the camera exactly the same way backgrounding the
    /// app already did.
    private var isOverviewPageForeground: Bool {
        PrinterDetailCameraLifecycleMapping.isForeground(
            scenePhase: scenePhase,
            selectedPanel: selectedPanel
        )
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
        // Overview/Controls pager replaced the single old `ScrollView`).
        // `.contain` makes this VStack a genuine, opaque accessibility node
        // in its own right so its identifier stops leaking onto children
        // that already declare their own.
        .accessibilityElement(children: .contain)
        .accessibilityIdentifier("printer.detail.root.\(printerId.uuidString)")
        .navigationTitle("Printer")
        #if os(iOS)
        .navigationBarTitleDisplayMode(.inline)
        #endif
        .refreshable {
            await PrinterDetailViewLifecycle.refresh(
                viewModel: viewModel,
                coverageViewModel: coverageViewModel,
                refreshCoverage: filamentCoverageEnabled,
                snapshotPollingAllowed: { isOverviewPageForeground }
            )
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
        .task {
            let generation = services.activeServerGeneration
            await services.awaitActiveServerSettled()
            guard !Task.isCancelled, services.activeServerGeneration == generation else { return }
            // Pin setup controls to the same composition used for detail data,
            // before the first await that loads that data.
            let composition = services.printerControlsComposition
            controlsComposition = composition
            viewModel.isViewActive = true
            viewModel.setSnapshotPollingAllowed(isOverviewPageForeground)
            viewModel.configure(printerService: composition?.printerService ?? services.printerService)
            #if canImport(UIKit)
            if let nfc = services.nfcService {
                viewModel.configureNFCScanner(nfc)
            }
            #endif
            viewModel.configureAutoDispatch(services.autoPrintService)
            viewModel.configureSignalR(services.signalRService)
            viewModel.configurePredictive(services.predictiveService)
            viewModel.configureFailureDetection(services.failureDetectionService)
            viewModel.configureOperatorServices(
                jobService: services.jobService,
                maintenanceService: services.maintenanceService
            )
            await viewModel.loadPrinter()
            viewModel.setSnapshotPollingAllowed(isOverviewPageForeground)

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
        .onDisappear {
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
                            snapshotPollingAllowed: selectedPanel == .overview
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
        // #2522, Hicks review finding 19): leaving the Overview page for
        // Controls must stop camera polling immediately, not just the next
        // time the app backgrounds/foregrounds.
        .onChange(of: selectedPanel) { _, _ in
            viewModel.setSnapshotPollingAllowed(isOverviewPageForeground)
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
        .sheet(item: $viewModel.dispatchTargetJob) { job in
            dispatchSheet(job)
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
            nfcAvailable: services.nfcService?.isAvailable ?? false
        )
    }

    private func filamentSectionView(_ printer: Printer) -> some View {
        PrinterFilamentSection(
            presentation: filamentPresentation(printer),
            actions: filamentActions(printer),
            onAction: { action in handleFilamentAction(action) }
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
            // stays reachable separately, accurately labeled "Eject
            // Filament", in `setupActionsSection`.
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

    // MARK: - Main Content (issue #2522 — Overview/Controls paging)

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
            overview: { overviewPage(printer) },
            controls: { controlsPage(printer) }
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
        .safeAreaInset(edge: .top, spacing: 0) {
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

    /// AnyLayout preserves child identity (especially the camera) on reflow.
    private func overviewPage(_ printer: Printer) -> some View {
        GeometryReader { geometry in
            let columns = PrinterDetailLayout.usesColumns(
                width: geometry.size.width, dynamicTypeSize: dynamicTypeSize
            )
            ScrollView {
                let layout = columns
                    ? AnyLayout(HStackLayout(alignment: .top, spacing: 24))
                    : AnyLayout(VStackLayout(alignment: .leading, spacing: 20))
                layout {
                    overviewPrimary(printer)
                        .frame(maxWidth: .infinity, alignment: .leading)
                    overviewSupporting(printer)
                        .frame(maxWidth: .infinity, alignment: .leading)
                }
                .padding()
                .accessibilityElement(children: .contain)
                .accessibilityIdentifier(columns ? "printer.detail.columns" : "printer.detail.readingColumn")
            }
        }
    }

    private func overviewPrimary(_ printer: Printer) -> some View {
        VStack(alignment: .leading, spacing: 20) {
            temperatureSection(printer)
            filamentSectionView(printer)
            currentJobBlock(printer)
            if let homedAxes = resolvedHomedAxes(printer) {
                homedAxesBadges(homedAxes)
            }
            ejectFilamentUtility(printer)
        }
    }

    private func overviewSupporting(_ printer: Printer) -> some View {
        VStack(alignment: .leading, spacing: 20) {
            cameraSection(printer)
            if printer.obicoEnabled && viewModel.isActivelyPrinting {
                failureDetectionSummary(printer)
            }
            queueSection(printer)
            maintenanceSection(printer)
            historySection(printer)
            mainsailLinkSection(printer)
            AutoDispatchSection(printerId: printer.id, isPrinting: viewModel.isPrinting || viewModel.isPaused)
            if printer.isOnline {
                setupActionsSection(printer)
            }
            predictiveInsightsLink(printer)
        }
    }

    /// Observe the persistent owner, never construct one inside a pager child.
    /// Maintenance and NFC remain on Overview under their separate gates.
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
                            materialPresentation: filamentPresentation(printer),
                            observesSafety: true,
                            materialActions: filamentActions(printer),
                            onMaterialAction: { handleFilamentAction($0) }
                        )
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
        VStack(alignment: .leading, spacing: 12) {
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
                .accessibilityIdentifier("printer.detail.controls.settings")
            }
        }
        .fixedSize(horizontal: false, vertical: true)
        .padding()
        .operatorCard()
        .accessibilityElement(children: .contain)
        .accessibilityIdentifier("printer.detail.controls.unavailable")
    }

    /// Retained physical-unload utility (issue #2522 preserve-before-cleanup
    /// checklist, Hicks review finding 17). At the pre-#2522 baseline this
    /// lived in the removed `activeSpoolContent` block with NO
    /// `printer.isOnline` gate — only an active-spool visibility gate and a
    /// pending-action disable gate — so it is rendered directly in
    /// `overviewPage`, never folded into the online-gated
    /// `setupActionsSection` below. Dispatches the ORIGINAL combined
    /// operation (`ejectFilament()`: clears the assignment AND physically
    /// unloads via `unloadFilament()`), preserved with truthful, distinct
    /// wording — never to be confused with #2519's "Clear spool assignment"
    /// action in `PrinterFilamentSection`, which is assignment-only
    /// (`clearActiveSpoolAssignment()`, no physical unload).
    @ViewBuilder
    private func ejectFilamentUtility(_ printer: Printer) -> some View {
        if viewModel.effectiveSpoolInfo?.hasActiveSpool ?? false {
            PrinterDetailBorderedDestructiveButton(kind: .eject) {
                UIImpactFeedbackGenerator(style: .heavy).impactOccurred()
                let task = Task { await viewModel.ejectFilament() }
                activeTasks.append(task)
            }
            .disabled(viewModel.isPerformingAction)
            .accessibilityLabel("Eject filament: clears the spool assignment and physically unloads")
            .accessibilityIdentifier("printer.detail.overview.ejectFilament")
        }
    }

    /// Retained-location setup actions (issue #2522 preserve-before-cleanup
    /// checklist): the admin maintenance toggle and NFC printer-tag write,
    /// both previously nested inside the old Advanced disclosure's Actions
    /// block, which rendered `if printer.isOnline` regardless of the
    /// Advanced Printer Controls safety preference. The caller
    /// (`overviewPage`) reproduces that exact `printer.isOnline` gate; this
    /// function itself only decides whether it has anything to show at all.
    @ViewBuilder
    private func setupActionsSection(_ printer: Printer) -> some View {
        let showsMaintenanceToggle = authViewModel.currentUserRole == "farm_admin"
        #if canImport(UIKit)
        let showsWriteTag = true
        #else
        let showsWriteTag = false
        #endif
        if showsMaintenanceToggle || showsWriteTag {
            VStack(alignment: .leading, spacing: 12) {
                Text("Setup Actions")
                    .font(.headline)

                VStack(spacing: 10) {
                    if showsMaintenanceToggle {
                        Button {
                            UIImpactFeedbackGenerator(style: .medium).impactOccurred()
                            let task = Task { await viewModel.toggleMaintenance() }
                            activeTasks.append(task)
                        } label: {
                            Label(
                                printer.inMaintenance ? "Exit Maintenance" : "Enter Maintenance",
                                systemImage: "wrench.and.screwdriver"
                            )
                            .fullWidthActionButton()
                        }
                        .buttonStyle(.bordered)
                        .disabled(viewModel.isPerformingAction || viewModel.isPrinting || viewModel.isPaused)
                        .accessibilityLabel(printer.inMaintenance ? "Exit maintenance mode" : "Enter maintenance mode")
                    }

                    #if canImport(UIKit)
                    Button {
                        viewModel.writeNFCPrinterTag()
                    } label: {
                        Label("Write Tag", systemImage: "wave.3.right")
                            .fullWidthActionButton()
                    }
                    .buttonStyle(.bordered)
                    .disabled(viewModel.isPerformingAction)
                    .accessibilityLabel("Write NFC printer identification tag")
                    #endif
                }
            }
            .accessibilityIdentifier("printer.detail.overview.setupActions")
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
        VStack(alignment: .leading, spacing: 12) {
            Text("Current Job")
                .font(.headline)

            if viewModel.isActivelyPrinting || jobName != nil {
                VStack(alignment: .leading, spacing: 12) {
                    HStack(alignment: .top, spacing: 12) {
                        remoteThumbnail(viewModel.currentJobThumbnailUrl, size: 56)
                        VStack(alignment: .leading, spacing: 6) {
                            Text(jobName ?? "Printing")
                                .font(.subheadline.weight(.semibold))
                                .fixedSize(horizontal: false, vertical: true)
                        }
                        Spacer(minLength: 0)
                    }

                    if let progress = printer.progress {
                        PrintProgressBar(progress: progress, height: 10)
                            .accessibilityLabel("Print progress \(Int((progress * 100).rounded())) percent")
                    }

                    currentJobEtaRow()
                }
                .padding()
                .operatorCard()
            } else {
                operatorEmptyState(icon: "pause.circle", message: "No active print")
            }
            PrinterRunActionBar(
                presentation: runActionPresentation(for: printer).routineActions,
                onSelect: { kind in handleRunAction(kind) }
            )
        }
        .accessibilityIdentifier("printer.detail.job")
    }

    @ViewBuilder
    private func currentJobEtaRow() -> some View {
        if let remaining = viewModel.formattedTimeRemaining {
            let clock = viewModel.formattedEtaClock
            HStack(spacing: 6) {
                Image(systemName: "clock")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .accessibilityHidden(true)
                Text(clock.map { "Done in \(remaining) · \($0)" } ?? "Done in \(remaining)")
                    .font(.subheadline)
                    .monospacedDigit()
                    .fixedSize(horizontal: false, vertical: true)
                Spacer(minLength: 0)
            }
            .accessibilityElement(children: .combine)
            .accessibilityLabel(
                clock.map { "Estimated completion in \(remaining), at \($0)" }
                    ?? "Estimated time remaining \(remaining)"
            )
            .accessibilityIdentifier("printer.detail.job.eta")
        }
    }

    // MARK: - Queue (next 3 assigned jobs + match state + dispatch-to)

    private func queueSection(_ printer: Printer) -> some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("Queue")
                .font(.headline)

            if viewModel.nextQueuedJobs.isEmpty {
                operatorEmptyState(icon: "tray", message: "No jobs queued for this printer")
            } else {
                VStack(spacing: 10) {
                    ForEach(viewModel.nextQueuedJobs) { job in
                        queueRow(job)
                    }
                }
            }
        }
        .accessibilityIdentifier("printer.detail.queue")
    }

    private func queueRow(_ job: QueuedPrintJobResponse) -> some View {
        let match = viewModel.matchState(for: job)
        let title = job.gcodeFile?.name ?? job.job.name
        return VStack(alignment: .leading, spacing: 8) {
            HStack(alignment: .top, spacing: 12) {
                remoteThumbnail(job.gcodeFile?.thumbnailUrl ?? job.job.thumbnailUrl, size: 44)
                VStack(alignment: .leading, spacing: 4) {
                    Text(title)
                        .font(.subheadline.weight(.medium))
                        .fixedSize(horizontal: false, vertical: true)
                    HStack(spacing: 4) {
                        Image(systemName: match.systemImage)
                            .font(.caption2)
                            .accessibilityHidden(true)
                        Text(match.label)
                            .font(.caption)
                    }
                    .foregroundStyle(matchTint(match))
                    .accessibilityElement(children: .combine)
                    .accessibilityLabel(match.label)
                }
                Spacer(minLength: 0)
            }

            Button {
                let task = Task { await viewModel.beginDispatch(for: job) }
                activeTasks.append(task)
            } label: {
                Label("Dispatch to…", systemImage: "paperplane")
                    .font(.subheadline)
                    .frame(maxWidth: .infinity, minHeight: 44)
            }
            .buttonStyle(.bordered)
            .accessibilityIdentifier("printer.detail.queue.dispatch.\(job.id)")
            .accessibilityLabel("Dispatch \(title) to another printer")
        }
        .padding()
        .operatorCard()
        .accessibilityIdentifier("printer.detail.queue.row.\(job.id)")
    }

    private func matchTint(_ state: PrinterDetailViewModel.QueueMatchState) -> Color {
        switch state {
        case .match: Color.green
        case .mismatch: Color.pfError
        case .unknown: Color.pfTextSecondary
        }
    }

    // MARK: - Maintenance Odometer

    private func maintenanceSection(_ printer: Printer) -> some View {
        VStack(alignment: .leading, spacing: 12) {
            HStack {
                Text("Maintenance")
                    .font(.headline)
                Spacer()
                if let hours = viewModel.printerStatistics?.totalPrintHours {
                    Text("\(Int(hours.rounded())) h total")
                        .font(.caption)
                        .monospacedDigit()
                        .foregroundStyle(Color.pfTextSecondary)
                        .accessibilityLabel("\(Int(hours.rounded())) total print hours")
                }
            }

            if viewModel.odometerRows.isEmpty {
                operatorEmptyState(icon: "wrench.and.screwdriver", message: "No scheduled maintenance")
            } else {
                VStack(spacing: 8) {
                    ForEach(viewModel.odometerRows) { row in
                        odometerRow(row)
                    }
                }
            }
        }
        .accessibilityIdentifier("printer.detail.maintenance")
    }

    private func odometerRow(_ row: PrinterDetailViewModel.OdometerRow) -> some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack(alignment: .firstTextBaseline) {
                VStack(alignment: .leading, spacing: 2) {
                    Text(row.title)
                        .font(.subheadline.weight(.medium))
                        .fixedSize(horizontal: false, vertical: true)
                    if let component = row.component, !component.isEmpty {
                        Text(component)
                            .font(.caption)
                            .foregroundStyle(Color.pfTextSecondary)
                    }
                }
                Spacer(minLength: 8)
                Text(row.stateLabel)
                    .font(.caption.weight(.semibold))
                    .padding(.horizontal, 8)
                    .padding(.vertical, 3)
                    .background(
                        row.isDue ? Color.pfError.opacity(0.15) : Color.pfBackgroundTertiary,
                        in: Capsule()
                    )
                    .foregroundStyle(row.isDue ? Color.pfError : Color.pfTextSecondary)
            }

            if let threshold = row.thresholdHours {
                Text("\(Int(row.currentHours.rounded())) / \(Int(threshold.rounded())) h")
                    .font(.caption)
                    .monospacedDigit()
                    .foregroundStyle(Color.pfTextSecondary)
            }

            if row.isDue {
                Button {
                    let performedBy = authViewModel.currentUser?.username ?? "operator"
                    let task = Task { await viewModel.logMaintenanceCompletion(row, performedBy: performedBy) }
                    activeTasks.append(task)
                } label: {
                    Label("Log Completed", systemImage: "checkmark.circle")
                        .font(.subheadline)
                        .frame(maxWidth: .infinity, minHeight: 44)
                }
                .buttonStyle(.bordered)
                .disabled(viewModel.isPerformingAction)
                .accessibilityIdentifier("printer.detail.maintenance.log.\(row.id)")
                .accessibilityLabel("Log \(row.title) as completed")
            }
        }
        .padding()
        .operatorCard()
        .accessibilityIdentifier("printer.detail.maintenance.row.\(row.id)")
    }

    // MARK: - History Tail

    private func historySection(_ printer: Printer) -> some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("History")
                .font(.headline)

            if viewModel.historyTail.isEmpty {
                operatorEmptyState(icon: "clock.arrow.circlepath", message: "No recent jobs")
            } else {
                VStack(spacing: 8) {
                    ForEach(viewModel.historyTail) { job in
                        historyRow(job)
                    }
                }
                .padding()
                .operatorCard()
            }
        }
        .accessibilityIdentifier("printer.detail.history")
    }

    private func historyRow(_ job: PrinterHistoryJob) -> some View {
        let outcome = job.outcome
        let name = job.filename.isEmpty ? "Job" : job.filename
        return HStack(spacing: 12) {
            Image(systemName: historyIcon(outcome))
                .font(.subheadline)
                .foregroundStyle(historyTint(outcome))
                .frame(width: 24)
                .accessibilityHidden(true)

            VStack(alignment: .leading, spacing: 2) {
                Text(name)
                    .font(.subheadline)
                    .lineLimit(1)
                    .truncationMode(.middle)
                Text(outcome.label)
                    .font(.caption)
                    .foregroundStyle(Color.pfTextSecondary)
            }

            Spacer(minLength: 8)

            if let end = job.endDate {
                Text(end, format: .relative(presentation: .named))
                    .font(.caption)
                    .foregroundStyle(Color.pfTextTertiary)
            }
        }
        .frame(minHeight: 44)
        .accessibilityElement(children: .combine)
        .accessibilityLabel("\(name), \(outcome.label)")
        .accessibilityIdentifier("printer.detail.history.row.\(job.id)")
    }

    private func historyIcon(_ outcome: PrinterHistoryJob.Outcome) -> String {
        switch outcome {
        case .completed: "checkmark.circle.fill"
        case .cancelled: "xmark.circle.fill"
        case .failed: "exclamationmark.triangle.fill"
        case .inProgress: "arrow.triangle.2.circlepath"
        case .unknown: "questionmark.circle"
        }
    }

    private func historyTint(_ outcome: PrinterHistoryJob.Outcome) -> Color {
        switch outcome {
        case .completed: Color.green
        case .cancelled: Color.pfTextSecondary
        case .failed: Color.pfError
        case .inProgress: Color.pfAccent
        case .unknown: Color.pfTextTertiary
        }
    }

    // MARK: - Open in Mainsail

    @ViewBuilder
    private func mainsailLinkSection(_ printer: Printer) -> some View {
        if let url = viewModel.mainsailUrl {
            Link(destination: url) {
                HStack(spacing: 12) {
                    Image(systemName: "safari")
                        .font(.headline)
                        .foregroundStyle(Color.pfAccent)
                        .frame(width: 32)
                        .accessibilityHidden(true)
                    Text("Open in Mainsail")
                        .font(.subheadline.weight(.semibold))
                        .foregroundStyle(Color.pfTextPrimary)
                    Spacer(minLength: 8)
                    Image(systemName: "arrow.up.right.square")
                        .font(.caption)
                        .foregroundStyle(.tertiary)
                        .accessibilityHidden(true)
                }
                .padding()
                .frame(minHeight: 44)
                .operatorCard()
            }
            .accessibilityIdentifier("printer.detail.mainsail")
            .accessibilityLabel("Open in Mainsail")
            .accessibilityHint("Opens the printer's web interface in your browser.")
            .accessibilityAddTraits(.isButton)
        }
    }

    private func predictiveInsightsLink(_ printer: Printer) -> some View {
        NavigationLink(value: AppDestination.predictiveInsights(printerId: printer.id)) {
            HStack {
                Label("Predictive Insights", systemImage: "gauge.with.dots.needle.33percent")
                    .font(.subheadline.weight(.medium))
                Spacer()
                Image(systemName: "chevron.right")
                    .font(.caption)
                    .foregroundStyle(.tertiary)
            }
            .padding()
            .frame(minHeight: 44)
            .background(Color.pfCard, in: RoundedRectangle(cornerRadius: 12))
            .overlay(
                RoundedRectangle(cornerRadius: 12)
                    .strokeBorder(Color.pfBorder, lineWidth: 1)
            )
        }
        .buttonStyle(.plain)
        .accessibilityIdentifier("printer.detail.predictive")
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

    // MARK: - Dispatch-to sheet

    private func dispatchSheet(_ job: QueuedPrintJobResponse) -> some View {
        NavigationStack {
            Group {
                if viewModel.isLoadingCandidates {
                    ProgressView("Finding printers…")
                        .frame(maxWidth: .infinity, maxHeight: .infinity)
                } else if let error = viewModel.dispatchError {
                    ContentUnavailableView(
                        "Couldn’t load candidates",
                        systemImage: "exclamationmark.triangle",
                        description: Text(error)
                    )
                } else if viewModel.dispatchCandidates.isEmpty {
                    ContentUnavailableView(
                        "No eligible printers",
                        systemImage: "printer.dotmatrix",
                        description: Text("No other printer can take this job right now.")
                    )
                } else {
                    List(viewModel.dispatchCandidates) { candidate in
                        Button {
                            let task = Task { await viewModel.dispatch(job, to: candidate.printerId) }
                            activeTasks.append(task)
                        } label: {
                            dispatchCandidateRow(candidate)
                        }
                        .disabled(candidate.eliminated || viewModel.isDispatching)
                        .accessibilityIdentifier(
                            "printer.detail.dispatch.candidate.\(candidate.printerId.uuidString.lowercased())"
                        )
                    }
                }
            }
            .navigationTitle("Dispatch Job")
            #if os(iOS)
            .navigationBarTitleDisplayMode(.inline)
            #endif
            .toolbar {
                ToolbarItem(placement: .cancellationAction) {
                    Button("Cancel") { viewModel.cancelDispatch() }
                        .accessibilityIdentifier("printer.detail.dispatch.cancel")
                }
            }
        }
        .presentationDetents([.medium, .large])
        .accessibilityIdentifier("printer.detail.dispatch.sheet")
    }

    private func dispatchCandidateRow(_ candidate: DispatchCandidate) -> some View {
        let name = candidate.printerName.isEmpty ? "Printer" : candidate.printerName
        return HStack {
            VStack(alignment: .leading, spacing: 2) {
                Text(name)
                    .font(.subheadline.weight(.medium))
                    .foregroundStyle(candidate.eliminated ? Color.pfTextTertiary : Color.pfTextPrimary)
                if candidate.eliminated, let reason = candidate.eliminationReasons.first {
                    Text(reason)
                        .font(.caption)
                        .foregroundStyle(Color.pfError)
                } else {
                    Text("Score \(Int(candidate.score.rounded()))")
                        .font(.caption)
                        .monospacedDigit()
                        .foregroundStyle(Color.pfTextSecondary)
                }
            }
            .fixedSize(horizontal: false, vertical: true)
            Spacer(minLength: 8)
            Image(systemName: candidate.eliminated ? "slash.circle" : "paperplane.fill")
                .foregroundStyle(candidate.eliminated ? Color.pfError : Color.pfAccent)
                .accessibilityHidden(true)
        }
        .frame(minHeight: 44)
        .accessibilityElement(children: .combine)
        .accessibilityLabel(
            candidate.eliminated
                ? "\(name), not eligible. \(candidate.eliminationReasons.first ?? "")"
                : "\(name), eligible, score \(Int(candidate.score.rounded()))"
        )
    }

    // MARK: - Homed Axes

    /// The list DTO can carry an empty homing string while `/status` carries the real
    /// one, and `??` only falls through on `nil` — so a stale empty value used to win
    /// and badge a homed printer as unhomed. Prefer the first value that names an axis,
    /// and fall back to an empty-but-present value so "nothing is homed" still renders.
    static func resolveHomedAxes(_ candidates: [String?]) -> String? {
        let present = candidates.compactMap { $0 }
        return present.first { !$0.isEmpty } ?? present.first
    }

    private func resolvedHomedAxes(_ printer: Printer) -> String? {
        Self.resolveHomedAxes([printer.homedAxes, viewModel.statusDetail?.homedAxes])
    }

    /// Compact X/Y/Z badges showing which axes have been homed. Hidden entirely when
    /// the backend doesn't supply the field (older backend or unsupported printer).
    @ViewBuilder
    private func homedAxesBadges(_ homedAxes: String) -> some View {
        let normalized = homedAxes.lowercased()
        HStack(spacing: 8) {
            Text("Homed axes")
                .font(.caption)
                .foregroundStyle(Color.pfTextSecondary)
            HStack(spacing: 4) {
                ForEach(["x", "y", "z"], id: \.self) { axis in
                    let isHomed = normalized.contains(axis)
                    Text(axis.uppercased())
                        .font(.caption2.weight(.bold))
                        .padding(.horizontal, 6)
                        .padding(.vertical, 3)
                        .background(
                            isHomed ? Color.pfSuccess.opacity(0.12) : Color.pfBackgroundTertiary,
                            in: Capsule()
                        )
                        .foregroundStyle(isHomed ? Color.pfSuccess : Color.pfTextSecondary)
                        .accessibilityLabel("\(axis.uppercased()) axis \(isHomed ? "homed" : "not homed")")
                }
            }
        }
        .accessibilityElement(children: .combine)
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
        VStack(alignment: .leading, spacing: 12) {
            HStack {
                Text("Camera")
                    .font(.headline)

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
                            .font(.subheadline)
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
                            .font(.subheadline)
                    }
                    .accessibilityLabel("Rotate camera view")
                    
                    if !viewModel.showLivestream || viewModel.cameraPreviewMode == .snapshotPolling {
                        Button {
                            let task = Task { _ = await viewModel.refreshSnapshot() }
                            activeTasks.append(task)
                        } label: {
                            Image(systemName: "arrow.clockwise")
                                .font(.subheadline)
                        }
                        .disabled(viewModel.isLoadingSnapshot)
                        .accessibilityLabel("Refresh camera snapshot")
                    }
                }
            }

            Group {
                cameraPreview(printer)
            }
            .frame(maxWidth: .infinity)
            .background(Color.pfCard, in: RoundedRectangle(cornerRadius: 12))
            .overlay(
                RoundedRectangle(cornerRadius: 12)
                    .strokeBorder(Color.pfBorder, lineWidth: 1)
            )
        }
    }

    @ViewBuilder
    private func cameraPreview(_ printer: Printer) -> some View {
        switch viewModel.cameraPreviewMode {
        case .mjpegStream:
            #if canImport(UIKit)
            // `isOverviewPageForeground` (issue #2522, Hicks review finding
            // 19): native `TabView` paging keeps this page mounted
            // alongside Controls for swipe animation, so without this gate
            // the live `MJPEGStreamContainer` (a `UIViewRepresentable`
            // backed by a persistent WKWebView connection) would keep
            // streaming off-screen. Falling through to the snapshot/
            // placeholder branches when not foreground tears the stream
            // down without touching `viewModel.showLivestream` or
            // `cameraRotation`, so returning to Overview resumes the SAME
            // camera state the user left, not a reset one.
            if viewModel.showLivestream,
               isOverviewPageForeground,
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

    // MARK: - Failure Detection Summary

    private func failureDetectionSummary(_ printer: Printer) -> some View {
        let status = viewModel.failureDetectionStatus
        let displayState = status?.state ?? "checking"
        let stateColor: Color = {
            switch displayState {
            case "monitoring": return .pfSuccess
            case "error": return .pfError
            case "misconfigured": return .pfWarning
            default: return .pfTextSecondary
            }
        }()
        let stateLabel: String = {
            switch displayState {
            case "monitoring": return "Guarding"
            case "idle": return "Ready"
            case "misconfigured": return "Needs Setup"
            case "error": return "Error"
            case "disabled": return printer.obicoEnabled ? "Standby" : "Off"
            default: return printer.obicoEnabled ? "Checking" : "Off"
            }
        }()

        return VStack(alignment: .leading, spacing: 8) {
            HStack(spacing: 6) {
                Image(systemName: "shield.checkered")
                    .font(.subheadline)
                    .foregroundStyle(stateColor)
                Text("Failure Detection")
                    .font(.subheadline.weight(.medium))
                Spacer()
                Text(stateLabel)
                    .font(.caption2.weight(.semibold))
                    .textCase(.uppercase)
                    .padding(.horizontal, 8)
                    .padding(.vertical, 3)
                    .background(stateColor.opacity(0.15), in: Capsule())
                    .foregroundStyle(stateColor)
            }

            if let status {
                switch status.lastOutcome {
                case "failure":
                    HStack(spacing: 6) {
                        Image(systemName: "exclamationmark.triangle.fill")
                            .font(.caption2)
                            .foregroundStyle(Color.pfError)
                        if let confidence = status.lastConfidence {
                            Text("Failure detected • \(Int(confidence * 100))% confidence")
                                .font(.caption)
                                .foregroundStyle(.secondary)
                        } else {
                            Text("Failure detected")
                                .font(.caption)
                                .foregroundStyle(.secondary)
                        }
                        if status.lastAutoPaused == true {
                            Text("• auto-paused")
                                .font(.caption)
                                .foregroundStyle(Color.pfError)
                        }
                    }
                case "healthy":
                    HStack(spacing: 6) {
                        Image(systemName: "checkmark.circle.fill")
                            .font(.caption2)
                            .foregroundStyle(Color.pfSuccess)
                        Text("No failure detected")
                            .font(.caption)
                            .foregroundStyle(.secondary)
                    }
                default:
                    if displayState == "monitoring" {
                        HStack(spacing: 6) {
                            Image(systemName: "eye.fill")
                                .font(.caption2)
                                .foregroundStyle(Color.pfSuccess)
                            Text("Actively watching this print")
                                .font(.caption)
                                .foregroundStyle(.secondary)
                        }
                    }
                }
            }
        }
        .padding(12)
        .background(Color.pfCard, in: RoundedRectangle(cornerRadius: 12))
        .overlay(
            RoundedRectangle(cornerRadius: 12)
                .strokeBorder(stateColor.opacity(0.3), lineWidth: 1)
        )
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
    ///   switches pages mid-refresh (Overview → Controls or back), that stale
    ///   snapshot would restart polling on a now-hidden Controls page, or
    ///   stop it on a now-visible Overview page — the opposite of current
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
