import SwiftUI

struct PrinterDetailView: View {
    @Environment(ServiceContainer.self) private var services
    @Environment(AppRouter.self) private var router
    @Environment(AuthViewModel.self) private var authViewModel
    @Environment(\.horizontalSizeClass) private var sizeClass
    @Environment(\.scenePhase) private var scenePhase
    @State private var viewModel: PrinterDetailViewModel
    @State private var coverageViewModel: PrinterFilamentCoverageViewModel
    @State private var activeTasks: [Task<Void, Never>] = []

    private let printerId: UUID

    init(printerId: UUID) {
        self.printerId = printerId
        _viewModel = State(initialValue: PrinterDetailViewModel(printerId: printerId))
        _coverageViewModel = State(initialValue: PrinterFilamentCoverageViewModel(printerId: printerId))
    }

    var body: some View {
        VStack(spacing: 0) {
            // #789: shared stale banner — honest, read-only cached coverage.
            if coverageViewModel.isShowingStaleCache {
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
        .accessibilityIdentifier("printer.detail.root.\(printerId.uuidString)")
        .navigationTitle("Printer")
        #if os(iOS)
        .navigationBarTitleDisplayMode(.inline)
        #endif
        .refreshable {
            await viewModel.loadPrinter()
            viewModel.setSnapshotPollingAllowed(scenePhase == .active)
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
            viewModel.isViewActive = true
            viewModel.setSnapshotPollingAllowed(scenePhase == .active)
            viewModel.configure(printerService: services.printerService)
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
            coverageViewModel.configure(coverageService: services.filamentCoverageService)
            coverageViewModel.configureSignalR(services.signalRService)
            // #789: wire + hydrate this printer's coverage read-cache BEFORE the
            // canonical load so an offline detail launch shows honest stale data.
            coverageViewModel.configureCache(services.filamentCoverageReadCache)
            await coverageViewModel.hydrateFromCache()
            await viewModel.loadPrinter()
            await coverageViewModel.load()
            viewModel.setSnapshotPollingAllowed(scenePhase == .active)

            // Handle NFC "mark ready" deep link
            if let pendingId = router.pendingNFCReadyPrinterId, pendingId == viewModel.printerId {
                viewModel.showNFCReadyConfirmation = true
                router.pendingNFCReadyPrinterId = nil
            }
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
                    viewModel.setSnapshotPollingAllowed(true)
                }
            case .inactive, .background:
                viewModel.setSnapshotPollingAllowed(false)
            @unknown default:
                viewModel.setSnapshotPollingAllowed(false)
            }
        }
        .sheet(isPresented: $viewModel.showSpoolPicker) {
            SpoolPickerView { spool in
                let task = Task { await viewModel.setActiveSpool(spool) }
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
            Button("Cancel", role: .cancel) {}
            Button("Mark Ready") {
                let task = Task { await viewModel.markPrinterReady() }
                activeTasks.append(task)
            }
        } message: {
            Text("Clear the bed and mark this printer as ready for the next print job?")
        }
    }

    // MARK: - Filament Section

    private func filamentSection(_ printer: Printer) -> some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("Filament")
                .font(.headline)

            VStack(alignment: .leading, spacing: 10) {
                if let spool = viewModel.effectiveSpoolInfo, spool.hasActiveSpool {
                    activeSpoolContent(spool)
                } else {
                    // No filament loaded
                    VStack(spacing: 12) {
                        HStack(spacing: 8) {
                            Image(systemName: "cylinder")
                                .font(.title2)
                                .foregroundStyle(Color.pfTextTertiary)
                            Text("No filament loaded")
                                .font(.subheadline)
                                .foregroundStyle(Color.pfTextSecondary)
                            Spacer()
                        }

                        HStack(spacing: 10) {
                            Button {
                                viewModel.loadFilament()
                            } label: {
                                Label("Set", systemImage: "plus.circle.fill")
                                    .frame(maxWidth: .infinity, minHeight: 44)
                            }
                            .buttonStyle(.borderedProminent)
                            .tint(Color.pfAccent)

                            NFCScanButton(action: {
                                viewModel.handleNFCScanToLoad()
                            })
                        }
                    }
                }
            }
            .padding()
            .background(Color.pfCard, in: RoundedRectangle(cornerRadius: 12))
            .overlay(
                RoundedRectangle(cornerRadius: 12)
                    .strokeBorder(Color.pfBorder, lineWidth: 1)
            )
        }
    }

    @ViewBuilder
    private func activeSpoolContent(_ spool: PrinterSpoolInfo) -> some View {
        HStack(spacing: 12) {
            Circle()
                .fill(Color(hex: spool.colorHex ?? "#808080"))
                .frame(width: 28, height: 28)
                .overlay(
                    Circle()
                        .strokeBorder(Color.pfBorder, lineWidth: 1)
                )

            VStack(alignment: .leading, spacing: 2) {
                Text(spool.filamentName ?? spool.spoolName ?? "Unknown")
                    .font(.subheadline.weight(.medium))

                HStack(spacing: 6) {
                    if let material = spool.material {
                        Text(material)
                            .font(.caption.weight(.medium))
                            .padding(.horizontal, 6)
                            .padding(.vertical, 2)
                            .background(Color.pfBackgroundTertiary, in: Capsule())
                    }
                    if let vendor = spool.vendor {
                        Text(vendor)
                            .font(.caption)
                            .foregroundStyle(Color.pfTextSecondary)
                    }
                }
            }

            Spacer()
        }

        if let remaining = spool.remainingWeightG {
            VStack(alignment: .leading, spacing: 4) {
                HStack {
                    Text("Remaining")
                        .font(.caption)
                        .foregroundStyle(Color.pfTextSecondary)
                    Spacer()
                    Text("\(Int(remaining))g")
                        .font(.caption.weight(.medium))
                }

                GeometryReader { geo in
                    ZStack(alignment: .leading) {
                        RoundedRectangle(cornerRadius: 4)
                            .fill(Color.pfBackgroundTertiary)
                            .frame(height: 8)

                        RoundedRectangle(cornerRadius: 4)
                            .fill(Color.pfAccent)
                            .frame(width: geo.size.width * filamentProgress(remaining: remaining), height: 8)
                    }
                }
                .frame(height: 8)
            }
        }

        Divider()

        HStack(spacing: 12) {
            Button {
                viewModel.loadFilament()
            } label: {
                Label("Change", systemImage: "arrow.triangle.swap")
                    .frame(maxWidth: .infinity)
            }
            .buttonStyle(.bordered)

            Button(role: .destructive) {
                UIImpactFeedbackGenerator(style: .heavy).impactOccurred()
                let task = Task { await viewModel.ejectFilament() }
                activeTasks.append(task)
            } label: {
                Label("Eject", systemImage: "eject.fill")
                    .frame(maxWidth: .infinity)
            }
            .buttonStyle(.bordered)
            .tint(Color.pfError)
        }
        .disabled(viewModel.isPerformingAction)

        NFCScanButton(action: {
            viewModel.handleNFCScanToLoad()
        }, compact: true)
    }

    /// Estimate progress assuming ~1000g full spool when no initial weight data is available
    private func filamentProgress(remaining: Double) -> CGFloat {
        let assumed = 1000.0
        return min(max(CGFloat(remaining / assumed), 0), 1)
    }

    // MARK: - Main Content

    private func printerContent(_ printer: Printer) -> some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 20) {
                operatorSections(printer)
            }
            .frame(maxWidth: sizeClass == .regular ? 760 : .infinity, alignment: .leading)
            .frame(maxWidth: .infinity)
            .padding()
        }
    }

    /// Fixed operator-first section order (issue #712, F7). Identical on iPhone
    /// and iPad (iPad only constrains the content width) so the six operator
    /// questions — what's printing, when it's done, what's loaded, whether it
    /// covers, what's queued, what's due — are answerable within ~two screens.
    /// Temperatures, jog/console, and destructive actions are demoted into the
    /// collapsed Advanced disclosure so no temperature graph appears by default.
    @ViewBuilder
    private func operatorSections(_ printer: Printer) -> some View {
        // 1. Header — printer state + primary Pause/Resume/Cancel controls.
        headerSection(printer)
        primaryControlsRow(printer)
        // 2. Camera — snapshot with tap-to-live (existing MJPEGStreamView).
        cameraSection(printer)
        if printer.obicoEnabled && viewModel.isActivelyPrinting {
            failureDetectionSummary(printer)
        }
        // 3. Current job — progress, ETA, F4 coverage verdict, part + thumbnail.
        currentJobBlock(printer)
        // 4. Filament slots — F6 toolhead roster + per-tool coverage + spool.
        filamentSlotsSection(printer)
        // 5. Queue — next 3 assigned jobs, per-tool match state, dispatch-to.
        queueSection(printer)
        // 6. Maintenance odometer — hours vs threshold, due → log completion.
        maintenanceSection(printer)
        // 7. History tail — last 5 job outcomes.
        historySection(printer)
        // 8. Open in Mainsail — printer backend deep link.
        mainsailLinkSection(printer)
        // 9. Advanced — temps, jog/console, predictive, destructive actions.
        advancedSection(printer)
    }

    // MARK: - Primary Controls (header row)

    @ViewBuilder
    private func primaryControlsRow(_ printer: Printer) -> some View {
        if viewModel.isPrinting || viewModel.isPaused {
            HStack(spacing: 12) {
                if viewModel.isPaused {
                    primaryControlButton(
                        "Resume", icon: "play.fill", tint: Color.pfAccent,
                        identifier: "printer.detail.control.resume"
                    ) {
                        await viewModel.resumePrinter()
                    }
                } else {
                    primaryControlButton(
                        "Pause", icon: "pause.fill", tint: Color.pfAccent,
                        identifier: "printer.detail.control.pause"
                    ) {
                        await viewModel.pausePrinter()
                    }
                }

                Button(role: .destructive) {
                    UIImpactFeedbackGenerator(style: .heavy).impactOccurred()
                    viewModel.requestCancel()
                } label: {
                    Label("Cancel", systemImage: "xmark.circle.fill")
                        .frame(maxWidth: .infinity, minHeight: 44)
                }
                .buttonStyle(.bordered)
                .tint(Color.pfError)
                .disabled(viewModel.isPerformingAction)
                .accessibilityIdentifier("printer.detail.control.cancel")
                .accessibilityLabel("Cancel current print")
            }
            .accessibilityIdentifier("printer.detail.header.controls")
        }
    }

    private func primaryControlButton(
        _ title: String,
        icon: String,
        tint: Color,
        identifier: String,
        action: @escaping () async -> Void
    ) -> some View {
        Button {
            UIImpactFeedbackGenerator(style: .medium).impactOccurred()
            let task = Task { await action() }
            activeTasks.append(task)
        } label: {
            Label(title, systemImage: icon)
                .frame(maxWidth: .infinity, minHeight: 44)
        }
        .buttonStyle(.borderedProminent)
        .tint(tint)
        .disabled(viewModel.isPerformingAction)
        .accessibilityIdentifier(identifier)
        .accessibilityLabel(title)
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
                            if let coverage = coverageViewModel.coverage, coverage.status != .unknown {
                                FilamentCoverageBadge(
                                    status: coverage.status,
                                    earliestPredictedRunoutAt: coverage.earliestPredictedRunoutAt
                                )
                            }
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

    // MARK: - Filament Slots (F6 toolheads + coverage + spool)

    private func filamentSlotsSection(_ printer: Printer) -> some View {
        VStack(alignment: .leading, spacing: 16) {
            if !viewModel.toolheads.isEmpty {
                VStack(alignment: .leading, spacing: 12) {
                    Text("Filament Slots")
                        .font(.headline)
                        .accessibilityIdentifier("printer.detail.slots")
                    VStack(spacing: 8) {
                        ForEach(viewModel.toolheads) { toolhead in
                            toolheadSlotRow(toolhead)
                        }
                    }
                    .padding()
                    .operatorCard()
                }
            }

            if let coverage = coverageViewModel.coverage {
                FilamentCoverageDetailSection(coverage: coverage)
            }

            filamentSection(printer)
        }
    }

    private func toolheadSlotRow(_ toolhead: Toolhead) -> some View {
        let slotName = toolhead.name ?? "Tool \(toolhead.index)"
        let material = toolhead.currentMaterial?.trimmingCharacters(in: .whitespacesAndNewlines)
        let hasMaterial = !(material?.isEmpty ?? true)
        return HStack(spacing: 12) {
            Circle()
                .fill(Color(hex: toolhead.currentFilamentColor ?? "#808080"))
                .frame(width: 22, height: 22)
                .overlay(Circle().strokeBorder(Color.pfBorder, lineWidth: 1))
                .accessibilityHidden(true)

            VStack(alignment: .leading, spacing: 2) {
                Text(slotName)
                    .font(.subheadline.weight(.medium))
                Text(hasMaterial ? material! : "Empty")
                    .font(.caption)
                    .foregroundStyle(hasMaterial ? Color.pfTextSecondary : Color.pfTextTertiary)
            }
            .fixedSize(horizontal: false, vertical: true)

            Spacer(minLength: 8)

            if let nozzle = toolhead.nozzleDiameter {
                Text("\(nozzle, specifier: "%.1f") mm")
                    .font(.caption)
                    .monospacedDigit()
                    .foregroundStyle(Color.pfTextSecondary)
            }
        }
        .frame(minHeight: 44)
        .accessibilityElement(children: .combine)
        .accessibilityLabel("\(slotName), \(hasMaterial ? material! : "empty")")
        .accessibilityIdentifier("printer.detail.slot.\(toolhead.index)")
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

    // MARK: - Advanced (collapsed disclosure)

    private func advancedSection(_ printer: Printer) -> some View {
        DisclosureGroup {
            VStack(alignment: .leading, spacing: 16) {
                temperatureSection(printer)
                advancedControlsLink(for: printer)
                AutoDispatchSection(printerId: printer.id, isPrinting: viewModel.isPrinting || viewModel.isPaused)
                predictiveInsightsLink(printer)
                if printer.isOnline {
                    actionSection(printer)
                }
            }
            .padding(.top, 12)
        } label: {
            Label("Advanced", systemImage: "chevron.down.circle")
                .font(.headline)
        }
        .padding()
        .operatorCard()
        .accessibilityIdentifier("printer.detail.advanced.disclosure")
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

    // MARK: - Header

    private func detailHeaderBaseColor(_ printer: Printer) -> Color {
        if !printer.isOnline { return Color(hex: "#4b5563") }
        switch printer.state?.lowercased() {
        case "printing": return Color(hex: "#059669")
        case "paused": return Color(hex: "#b45309")
        case "error": return Color(hex: "#dc2626")
        default: return Color(hex: "#1d4ed8")
        }
    }

    private func detailStatusLabel(_ printer: Printer) -> String {
        guard printer.isOnline else { return "Offline" }
        guard let state = printer.state else { return "Idle" }
        switch state.lowercased() {
        case "printing": return "Printing"
        case "paused": return "Paused"
        case "error": return "Error"
        case "idle", "ready": return "Ready"
        default: return state.capitalized
        }
    }

    private func headerSection(_ printer: Printer) -> some View {
        let baseColor = detailHeaderBaseColor(printer)

        return VStack(alignment: .leading, spacing: 0) {
            // Gradient header with name, manufacturer, model, state
            HStack(alignment: .top) {
                VStack(alignment: .leading, spacing: 4) {
                    Text(printer.name)
                        .font(.title2.weight(.semibold))
                        .foregroundStyle(.white)
                        .lineLimit(1)
                        .accessibilityLabel("\(printer.name), printer detail")
                        .accessibilityIdentifier(
                            "printer.detail.destination.\(printer.id.uuidString.lowercased())"
                        )

                    if let manufacturer = printer.manufacturerName,
                       let model = printer.modelName {
                        Text("\(manufacturer) · \(model)")
                            .font(.subheadline)
                            .foregroundStyle(.white.opacity(0.8))
                            .lineLimit(1)
                    } else if let manufacturer = printer.manufacturerName {
                        Text(manufacturer)
                            .font(.subheadline)
                            .foregroundStyle(.white.opacity(0.8))
                            .lineLimit(1)
                    } else if let model = printer.modelName {
                        Text(model)
                            .font(.subheadline)
                            .foregroundStyle(.white.opacity(0.8))
                            .lineLimit(1)
                    }
                }

                Spacer()

                VStack(alignment: .trailing, spacing: 6) {
                    HStack(spacing: 6) {
                        if printer.obicoEnabled {
                            Image(systemName: "shield.checkered")
                                .font(.caption)
                                .foregroundStyle(.white.opacity(0.85))
                        }
                        Text(detailStatusLabel(printer))
                            .font(.caption.weight(.semibold))
                            .padding(.horizontal, 8)
                            .padding(.vertical, 3)
                            .background(.black.opacity(0.3), in: Capsule())
                            .foregroundStyle(.white)
                    }

                    if printer.inMaintenance {
                        Text("Maintenance")
                            .font(.caption2.weight(.semibold))
                            .padding(.horizontal, 8)
                            .padding(.vertical, 3)
                            .background(.black.opacity(0.3), in: Capsule())
                            .foregroundStyle(.white)
                    }

                    if let homedAxes = printer.homedAxes ?? viewModel.statusDetail?.homedAxes {
                        homedAxesBadges(homedAxes)
                    }
                }
            }
            .padding(.horizontal, 16)
            .padding(.vertical, 12)
            .background(
                LinearGradient(
                    colors: [baseColor, baseColor.opacity(0.85)],
                    startPoint: .leading,
                    endPoint: .trailing
                )
            )

            // Location row below the gradient
            if let location = printer.location {
                HStack {
                    Label(location.name, systemImage: "building.2")
                        .font(.subheadline)
                        .foregroundStyle(.secondary)
                    Spacer()
                }
                .padding(.horizontal, 16)
                .padding(.vertical, 10)
                .background(Color.pfCard)
            }
        }
        .clipShape(RoundedRectangle(cornerRadius: 12))
        .overlay(
            RoundedRectangle(cornerRadius: 12)
                .strokeBorder(baseColor.opacity(0.3), lineWidth: 1)
        )
    }

    // MARK: - Homed Axes

    /// Compact X/Y/Z badges showing which axes have been homed. Hidden entirely when
    /// the backend doesn't supply the field (older backend or unsupported printer).
    @ViewBuilder
    private func homedAxesBadges(_ homedAxes: String) -> some View {
        let normalized = homedAxes.lowercased()
        HStack(spacing: 4) {
            ForEach(["x", "y", "z"], id: \.self) { axis in
                let isHomed = normalized.contains(axis)
                Text(axis.uppercased())
                    .font(.caption2.weight(.bold))
                    .frame(width: 18, height: 18)
                    .background(
                        isHomed ? Color.green.opacity(0.85) : Color.black.opacity(0.3),
                        in: Capsule()
                    )
                    .foregroundStyle(.white)
                    .accessibilityLabel("\(axis.uppercased()) axis \(isHomed ? "homed" : "not homed")")
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

        return VStack(alignment: .leading, spacing: 12) {
            Text("Temperatures")
                .font(.headline)

            VStack(spacing: 8) {
                TemperatureView(
                    label: "Hotend",
                    current: hotend,
                    target: hotendTgt,
                    icon: .hotend
                )

                Divider()

                TemperatureView(
                    label: "Bed",
                    current: bed,
                    target: bedTgt,
                    icon: .bed
                )
            }
            .padding()
            .background(Color.pfCard, in: RoundedRectangle(cornerRadius: 12))
            .overlay(
                RoundedRectangle(cornerRadius: 12)
                    .strokeBorder(Color.pfBorder, lineWidth: 1)
            )
        }
    }

    // MARK: - Advanced Controls Entry
    //
    // F1 (#706) gates jog/preheat/z-offset/home controls behind this link.
    // The section is hidden when the printer is offline (mirroring the
    // previous inline behavior via `PrinterControlsSection.isHidden`).

    @ViewBuilder
    private func advancedControlsLink(for printer: Printer) -> some View {
        if !PrinterControlsSection.isHidden(for: printer) {
            NavigationLink(value: AppDestination.advancedPrinterControls(printerId: printer.id)) {
                HStack(spacing: 12) {
                    Image(systemName: "slider.horizontal.3")
                        .font(.headline)
                        .foregroundStyle(Color.pfAccent)
                        .frame(width: 32)
                        .accessibilityHidden(true)

                    VStack(alignment: .leading, spacing: 2) {
                        Text("Advanced")
                            .font(.subheadline.weight(.semibold))
                            .foregroundStyle(Color.pfTextPrimary)
                        Text("Jog, preheat, home, z-offset")
                            .font(.caption)
                            .foregroundStyle(.secondary)
                    }
                    .fixedSize(horizontal: false, vertical: true)

                    Spacer(minLength: 8)

                    Image(systemName: "chevron.right")
                        .font(.caption)
                        .foregroundStyle(.tertiary)
                        .accessibilityHidden(true)
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
            .accessibilityLabel("Advanced controls")
            .accessibilityHint("Opens jog, preheat, home, and z-offset controls.")
            .accessibilityAddTraits(.isButton)
            .accessibilityIdentifier("printer.detail.advanced")
        }
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
            if viewModel.showLivestream,
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

    // MARK: - Action Buttons

    private func actionSection(_ printer: Printer) -> some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("Actions")
                .font(.headline)

            VStack(spacing: 10) {
                // Contextual actions based on state
                if viewModel.isPrinting {
                    HStack(spacing: 12) {
                        actionButton("Pause", icon: "pause.fill") {
                            await viewModel.pausePrinter()
                        }
                        .disabled(viewModel.isPerformingAction)

                        actionButton("Cancel", icon: "xmark.circle.fill", role: .destructive) {
                            viewModel.requestCancel()
                        }
                        .disabled(viewModel.isPerformingAction)
                    }
                }

                if viewModel.isPaused {
                    HStack(spacing: 12) {
                        actionButton("Resume", icon: "play.fill") {
                            await viewModel.resumePrinter()
                        }
                        .disabled(viewModel.isPerformingAction)

                        actionButton("Cancel", icon: "xmark.circle.fill", role: .destructive) {
                            viewModel.requestCancel()
                        }
                        .disabled(viewModel.isPerformingAction)
                    }
                }

                if viewModel.isPrinting || viewModel.isPaused {
                    actionButton("Stop", icon: "stop.fill", role: .destructive) {
                        await viewModel.stopPrinter()
                    }
                    .disabled(viewModel.isPerformingAction)
                }

                // Maintenance toggle (admin only)
                if authViewModel.currentUserRole == "farm_admin" {
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
                // Write NFC printer tag
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

                // Emergency Stop — always enabled when online
                Button(role: .destructive) {
                    viewModel.requestEmergencyStop()
                } label: {
                    Label("Emergency Stop", systemImage: "exclamationmark.octagon.fill")
                        .fullWidthActionButton(prominence: .prominent)
                        .fontWeight(.semibold)
                }
                .buttonStyle(.borderedProminent)
                .tint(Color.pfError)
                .accessibilityLabel("Emergency stop printer")
            }
        }
    }

    private func actionButton(
        _ title: String,
        icon: String,
        role: ButtonRole? = nil,
        action: @escaping () async -> Void
    ) -> some View {
        Button(role: role) {
            UIImpactFeedbackGenerator(style: role == .destructive ? .heavy : .medium).impactOccurred()
            let task = Task { await action() }
            activeTasks.append(task)
        } label: {
            Label(title, systemImage: icon)
                .fullWidthActionButton()
        }
        .buttonStyle(.bordered)
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
