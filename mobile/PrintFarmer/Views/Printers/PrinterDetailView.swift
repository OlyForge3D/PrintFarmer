import SwiftUI

struct PrinterDetailView: View {
    @Environment(ServiceContainer.self) private var services
    @Environment(AppRouter.self) private var router
    @Environment(\.horizontalSizeClass) private var sizeClass
    @State private var viewModel: PrinterDetailViewModel
    @State private var activeTasks: [Task<Void, Never>] = []

    init(printerId: UUID) {
        _viewModel = State(initialValue: PrinterDetailViewModel(printerId: printerId))
    }

    var body: some View {
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
        .navigationTitle("Printer")
        #if os(iOS)
        .navigationBarTitleDisplayMode(.inline)
        #endif
        .refreshable {
            await viewModel.loadPrinter()
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
            await viewModel.loadPrinter()

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
        }
        .sheet(isPresented: $viewModel.showSpoolPicker) {
            SpoolPickerView { spool in
                let task = Task { await viewModel.setActiveSpool(spool) }
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
            if sizeClass == .regular {
                iPadPrinterContent(printer)
            } else {
                VStack(alignment: .leading, spacing: 20) {
                    headerSection(printer)
                    temperatureSection(printer)

                    if let jobName = printer.fileName ?? printer.jobName,
                       let state = printer.state?.lowercased(),
                       state == "printing" || state == "paused" {
                        currentJobSection(jobName: jobName, progress: printer.progress)
                    }

                    cameraSection(printer)
                    if printer.obicoEnabled && viewModel.isActivelyPrinting {
                        failureDetectionSummary(printer)
                    }
                    filamentSection(printer)
                    AutoDispatchSection(printerId: printer.id, isPrinting: viewModel.isPrinting || viewModel.isPaused)

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
                        .background(Color.pfCard, in: RoundedRectangle(cornerRadius: 12))
                        .overlay(
                            RoundedRectangle(cornerRadius: 12)
                                .strokeBorder(Color.pfBorder, lineWidth: 1)
                        )
                    }
                    .buttonStyle(.plain)

                    if printer.isOnline {
                        actionSection(printer)
                    }
                }
                .padding()
            }
        }
    }

    private func iPadPrinterContent(_ printer: Printer) -> some View {
        HStack(alignment: .top, spacing: 20) {
            // Left column: header, temps, actions
            VStack(alignment: .leading, spacing: 20) {
                headerSection(printer)
                temperatureSection(printer)
                filamentSection(printer)
                AutoDispatchSection(printerId: printer.id, isPrinting: viewModel.isPrinting || viewModel.isPaused)

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
                    .background(Color.pfCard, in: RoundedRectangle(cornerRadius: 12))
                    .overlay(
                        RoundedRectangle(cornerRadius: 12)
                            .strokeBorder(Color.pfBorder, lineWidth: 1)
                    )
                }
                .buttonStyle(.plain)

                if printer.isOnline {
                    actionSection(printer)
                }
            }
            .frame(maxWidth: .infinity)

            // Right column: camera, current job
            VStack(alignment: .leading, spacing: 20) {
                cameraSection(printer)
                if printer.obicoEnabled && viewModel.isActivelyPrinting {
                    failureDetectionSummary(printer)
                }

                if let jobName = printer.fileName ?? printer.jobName,
                   let state = printer.state?.lowercased(),
                   state == "printing" || state == "paused" {
                    currentJobSection(jobName: jobName, progress: printer.progress)
                }
            }
            .frame(maxWidth: .infinity)
        }
        .padding()
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

    // MARK: - Current Job

    private func currentJobSection(jobName: String, progress: Double?) -> some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("Current Job")
                .font(.headline)

            VStack(alignment: .leading, spacing: 10) {
                Text(jobName)
                    .font(.subheadline.weight(.medium))

                if let progress {
                    VStack(alignment: .leading, spacing: 4) {
                        PrintProgressBar(progress: progress, height: 10)
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

    // MARK: - Camera Snapshot

    private func cameraSection(_ printer: Printer) -> some View {
        VStack(alignment: .leading, spacing: 12) {
            HStack {
                Text("Camera")
                    .font(.headline)

                if viewModel.canShowLivestream {
                    Text(viewModel.showLivestream ? "LIVE" : "SNAPSHOT")
                        .font(.caption2.weight(.bold))
                        .foregroundStyle(viewModel.showLivestream ? .white : Color.pfTextSecondary)
                        .padding(.horizontal, 6)
                        .padding(.vertical, 2)
                        .background(
                            viewModel.showLivestream ? Color.red : Color.pfBorder,
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
                }

                if viewModel.snapshotData != nil || printer.cameraSnapshotUrl != nil {
                    Button {
                        viewModel.rotateCameraView()
                    } label: {
                        Image(systemName: "rotate.right")
                            .font(.subheadline)
                    }
                    .accessibilityLabel("Rotate camera view")
                    
                    if !viewModel.showLivestream {
                        Button {
                            let task = Task { await viewModel.refreshSnapshot() }
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
            }
            .frame(maxWidth: .infinity)
            .background(Color.pfCard, in: RoundedRectangle(cornerRadius: 12))
            .overlay(
                RoundedRectangle(cornerRadius: 12)
                    .strokeBorder(Color.pfBorder, lineWidth: 1)
            )
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

                // Maintenance toggle
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
