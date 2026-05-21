import SwiftUI

struct DashboardView: View {
    @Environment(AppRouter.self) private var router
    @Environment(ServiceContainer.self) private var services
    @Environment(\.horizontalSizeClass) private var sizeClass
    @State private var viewModel = DashboardViewModel()
    @State private var dispatchViewModel = DispatchViewModel()
    @State private var dispatchRetryTask: Task<Void, Never>?
    @State private var retryTask: Task<Void, Never>?
    @State private var currentPage = 0

    var body: some View {
        @Bindable var router = router

        NavigationStack(path: $router.dashboardPath) {
            Group {
                if viewModel.isLoading && viewModel.printers.isEmpty {
                    loadingState
                } else if let error = viewModel.errorMessage, viewModel.printers.isEmpty {
                    errorState(error)
                } else if viewModel.printers.isEmpty {
                    // Empty fleet state
                    EmptyStateView(
                        icon: "printer",
                        title: "No Printers",
                        message: "No printers are registered. Add printers in PrintFarmer to see your fleet here."
                    )
                } else {
                    // iPhone: swipeable pages
                    if sizeClass == .compact {
                        VStack(spacing: 0) {
                            // Maintenance alerts banner (pinned)
                            if viewModel.hasMaintenanceAlerts {
                                maintenanceAlert
                                    .padding(.horizontal)
                                    .padding(.top, 16)
                            }
                            
                            TabView(selection: $currentPage) {
                                OverviewPage()
                                    .tag(0)
                                ActivePage()
                                    .tag(1)
                                QueuePage()
                                    .tag(2)
                                DispatchPage()
                                    .tag(3)
                            }
                            .tabViewStyle(.page(indexDisplayMode: .never))
                            
                            PageIndicator(currentPage: $currentPage, pageCount: 4, labels: ["Overview", "Active", "Queue", "Dispatch"])
                                .padding(.bottom, 8)
                        }
                    } else {
                        // iPad: keep existing ScrollView layout
                        iPadContent
                    }
                }
            }
            .navigationTitle("Dashboard")
            .refreshable {
                await viewModel.loadDashboard()
            }
            .navigationDestination(for: AppDestination.self) { destination in
                destinationView(for: destination)
            }
        }
        .task {
            viewModel.configure(
                printerService: services.printerService,
                jobService: services.jobService,
                statisticsService: services.statisticsService,
                jobAnalyticsService: services.jobAnalyticsService
            )
            viewModel.configureSignalR(services.signalRService)
            await viewModel.loadDashboard()
        }
        .onDisappear {
            viewModel.isViewActive = false
            dispatchRetryTask?.cancel()
            retryTask?.cancel()
        }
    }

    // MARK: - iPhone Pages
    
    @ViewBuilder
    private func OverviewPage() -> some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 20) {
                summarySection
                
                if let summary = viewModel.summary {
                    utilizationSection(summary: summary)
                }
                
                if let stats = viewModel.queueStats {
                    queueHealthSection(stats: stats)
                }
            }
            .padding()
        }
        .refreshable {
            await viewModel.loadDashboard()
        }
    }
    
    @ViewBuilder
    private func ActivePage() -> some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 20) {
                activeJobsSection
            }
            .padding()
        }
        .refreshable {
            await viewModel.loadDashboard()
        }
    }
    
    @ViewBuilder
    private func QueuePage() -> some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 20) {
                if !viewModel.upcomingJobs.isEmpty {
                    upNextSection
                }
                
                if !viewModel.modelStats.isEmpty {
                    modelBreakdownSection
                }
            }
            .padding()
        }
        .refreshable {
            await viewModel.loadDashboard()
        }
    }
    
    @ViewBuilder
    private func DispatchPage() -> some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 20) {
                if dispatchViewModel.isLoading && dispatchViewModel.queueStatus == nil {
                    ProgressView("Loading dispatch…")
                        .frame(maxWidth: .infinity, maxHeight: .infinity)
                } else if let error = dispatchViewModel.error, dispatchViewModel.queueStatus == nil {
                    ContentUnavailableView {
                        Label("Error", systemImage: "exclamationmark.triangle")
                    } description: {
                        Text(error)
                    } actions: {
                        Button("Retry") {
                            dispatchRetryTask = Task {
                                await dispatchViewModel.loadQueueStatus()
                                await dispatchViewModel.loadHistory()
                            }
                        }
                    }
                } else {
                    dispatchStatusCards
                    dispatchHistorySection
                }
            }
            .padding()
        }
        .refreshable {
            await dispatchViewModel.loadQueueStatus()
            await dispatchViewModel.loadHistory()
        }
        .task {
            dispatchViewModel.configure(dispatchService: services.dispatchService)
            await dispatchViewModel.loadQueueStatus()
            await dispatchViewModel.loadHistory()
        }
        .onDisappear {
            viewModel.isViewActive = false
            dispatchRetryTask?.cancel()
            retryTask?.cancel()
        }
    }
    
    // MARK: - iPad Content
    
    private var iPadContent: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 20) {
                // Maintenance alerts banner
                if viewModel.hasMaintenanceAlerts {
                    maintenanceAlert
                }
                
                // Fleet summary cards
                summarySection
                
                // Utilization
                if let summary = viewModel.summary {
                    utilizationSection(summary: summary)
                }
                
                // iPad: 2-column layout for active jobs + dispatch
                HStack(alignment: .top, spacing: 16) {
                    activeJobsSection
                        .frame(maxWidth: .infinity)
                    VStack(alignment: .leading, spacing: 16) {
                        dispatchStatusCards
                        dispatchHistorySection
                    }
                    .frame(maxWidth: .infinity)
                }
                
                // Farm Status sections
                farmStatusSections
            }
            .padding()
        }
    }

    // MARK: - Summary Cards

    private var summarySection: some View {
        let columnCount = sizeClass == .regular ? 6 : 3
        let columns = Array(repeating: GridItem(.flexible()), count: columnCount)
        let isIPad = sizeClass == .regular

        return VStack(alignment: .leading, spacing: 12) {
            Text("Fleet Overview")
                .font(.title2.bold())

            LazyVGrid(columns: columns, spacing: 12) {
                SummaryCard(title: "Total", count: viewModel.printers.count, icon: "printer", color: .pfTextPrimary, isLarge: isIPad)
                SummaryCard(title: "Online", count: viewModel.onlineCount, icon: "wifi", color: .pfSuccess, isLarge: isIPad)
                SummaryCard(title: "Printing", count: viewModel.printingCount, icon: "printer.fill", color: .pfSecondaryAccent, isLarge: isIPad)
                SummaryCard(title: "Paused", count: viewModel.pausedCount, icon: "pause.circle", color: .pfWarning, isLarge: isIPad)
                SummaryCard(title: "Offline", count: viewModel.offlineCount, icon: "wifi.slash", color: .pfTextTertiary, isLarge: isIPad)
                SummaryCard(title: "Error", count: viewModel.errorCount, icon: "exclamationmark.triangle", color: .pfError, isLarge: isIPad)
            }
        }
    }

    // MARK: - Active Jobs (printers currently printing)

    private var activeJobsSection: some View {
        let activeStates: Set<String> = ["printing", "paused", "pendingready"]
        let printingPrinters = viewModel.printers.filter { printer in
            guard let state = printer.state?.lowercased() else { return false }
            return activeStates.contains(state)
        }
        .sorted { sortPriority($0) < sortPriority($1) }
        let topPrinters = Array(printingPrinters.prefix(5))

        return VStack(alignment: .leading, spacing: 12) {
            HStack {
                Text("Active Jobs")
                    .font(.title2.bold())

                if viewModel.activeJobCount > 0 {
                    Text("\(viewModel.activeJobCount)")
                        .font(.caption.weight(.semibold).monospacedDigit())
                        .padding(.horizontal, 6)
                        .padding(.vertical, 2)
                        .background(Color.pfAccent.opacity(0.15), in: Capsule())
                        .foregroundStyle(Color.pfAccent)
                }

                Spacer()

                if !topPrinters.isEmpty {
                    Button {
                        router.selectedTab = .jobs
                    } label: {
                        Text("See All")
                            .font(.subheadline)
                    }
                }
            }

            if topPrinters.isEmpty {
                HStack {
                    Spacer()
                    VStack(spacing: 8) {
                        Image(systemName: "tray")
                            .font(.largeTitle)
                            .foregroundStyle(.secondary)
                        Text("No active jobs")
                            .foregroundStyle(.secondary)
                    }
                    .padding(.vertical, 24)
                    Spacer()
                }
            } else if sizeClass == .regular {
                LazyVGrid(columns: [GridItem(.flexible()), GridItem(.flexible())], spacing: 12) {
                    ForEach(topPrinters) { printer in
                        NavigationLink(value: AppDestination.printerDetail(id: printer.id)) {
                            ActiveJobRow(
                                printer: printer,
                                activeJob: viewModel.activeJobForPrinter(printer.id)
                            )
                        }
                        .buttonStyle(.plain)
                    }
                }
            } else {
                ForEach(topPrinters) { printer in
                    NavigationLink(value: AppDestination.printerDetail(id: printer.id)) {
                        ActiveJobRow(
                            printer: printer,
                            activeJob: viewModel.activeJobForPrinter(printer.id)
                        )
                    }
                    .buttonStyle(.plain)
                }
            }

            // Queue depth summary
            if viewModel.queuedJobCount > 0 {
                HStack(spacing: 6) {
                    Image(systemName: "clock")
                        .font(.caption)
                    Text("\(viewModel.queuedJobCount) job(s) queued across fleet")
                        .font(.caption)
                }
                .foregroundStyle(.secondary)
            }
        }
    }

    // MARK: - Farm Status Sections

    private var farmStatusSections: some View {
        VStack(alignment: .leading, spacing: 20) {
            // Queue Health
            if let stats = viewModel.queueStats {
                queueHealthSection(stats: stats)
            }

            // Model breakdown
            if !viewModel.modelStats.isEmpty {
                modelBreakdownSection
            }

            // Up Next
            if !viewModel.upcomingJobs.isEmpty {
                upNextSection
            }
        }
    }

    private func queueHealthSection(stats: QueueStats) -> some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("Queue Health")
                .font(.title2.bold())

            let columns = sizeClass == .regular
                ? [GridItem(.flexible()), GridItem(.flexible()), GridItem(.flexible()), GridItem(.flexible())]
                : [GridItem(.flexible()), GridItem(.flexible())]

            LazyVGrid(columns: columns, spacing: 12) {
                queueStatCard(title: "Queued", count: stats.totalQueued, icon: "tray.full", color: .pfSecondaryAccent)
                queueStatCard(title: "Printing", count: stats.totalPrinting, icon: "printer.fill", color: .pfAccent)
                queueStatCard(title: "Paused", count: stats.totalPaused, icon: "pause.circle", color: .pfWarning)
                queueStatCard(title: "Avg Wait", count: stats.averageWaitTimeMinutes, icon: "clock", color: .pfSuccess, suffix: "m")
            }
        }
    }

    private func queueStatCard(title: String, count: Int, icon: String, color: Color, suffix: String = "") -> some View {
        VStack(spacing: sizeClass == .regular ? 8 : 6) {
            Image(systemName: icon)
                .font(sizeClass == .regular ? .title3 : .body)
                .foregroundStyle(color)

            HStack(spacing: 0) {
                Text("\(count)")
                    .font(sizeClass == .regular ? .title2.bold().monospacedDigit() : .title3.bold().monospacedDigit())
                if !suffix.isEmpty {
                    Text(suffix)
                        .font(sizeClass == .regular ? .subheadline : .caption)
                        .foregroundStyle(.secondary)
                }
            }

            Text(title)
                .font(sizeClass == .regular ? .caption : .caption2)
                .foregroundStyle(.secondary)
        }
        .frame(maxWidth: .infinity)
        .padding(.vertical, sizeClass == .regular ? 16 : 12)
        .background(Color.pfCard, in: RoundedRectangle(cornerRadius: 10))
        .overlay(
            RoundedRectangle(cornerRadius: 10)
                .strokeBorder(Color.pfBorder, lineWidth: 1)
        )
    }

    private var modelBreakdownSection: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("By Printer Model")
                .font(.title2.bold())

            VStack(spacing: 8) {
                ForEach(viewModel.modelStats, id: \.modelName) { stat in
                    modelStatRow(stat: stat)
                }
            }
        }
    }

    private func modelStatRow(stat: QueuePrinterModelStats) -> some View {
        HStack(spacing: 12) {
            VStack(alignment: .leading, spacing: 2) {
                Text(stat.modelName)
                    .font(.subheadline.weight(.medium))
                    .lineLimit(1)
                if stat.currentlyPrinting > 0 {
                    Text("\(stat.currentlyPrinting) printing")
                        .font(.caption2)
                        .foregroundStyle(.secondary)
                }
            }

            Spacer()

            VStack(alignment: .trailing, spacing: 2) {
                Text("\(stat.totalQueued)")
                    .font(.title3.bold().monospacedDigit())
                    .foregroundStyle(Color.pfAccent)
                Text("queued")
                    .font(.caption2)
                    .foregroundStyle(.secondary)
            }
        }
        .padding(12)
        .background(Color.pfCard, in: RoundedRectangle(cornerRadius: 8))
        .overlay(
            RoundedRectangle(cornerRadius: 8)
                .strokeBorder(Color.pfBorder, lineWidth: 1)
        )
    }

    private var activePrintETAsSection: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("Active Print ETAs")
                .font(.title2.bold())

            VStack(spacing: 8) {
                ForEach(Array(viewModel.activePrintingPrinters.prefix(5)), id: \.id) { printer in
                    activePrintRow(printer: printer)
                }
            }
        }
    }

    private func activePrintRow(printer: Printer) -> some View {
        HStack(spacing: 12) {
            VStack(alignment: .leading, spacing: 2) {
                Text(printer.fileName ?? printer.jobName ?? "Unknown Job")
                    .font(.subheadline.weight(.medium))
                    .lineLimit(1)
                Label(printer.name, systemImage: "printer")
                    .font(.caption2)
                    .foregroundStyle(.secondary)
                    .lineLimit(1)
            }

            Spacer()

            if let progress = printer.progress, progress > 0 {
                VStack(alignment: .trailing, spacing: 2) {
                    Text("\(Int(progress * 100))%")
                        .font(.subheadline.weight(.medium).monospacedDigit())
                        .foregroundStyle(Color.pfAccent)
                    Text("printing")
                        .font(.caption2)
                        .foregroundStyle(.secondary)
                }
            } else {
                Text("—")
                    .font(.subheadline)
                    .foregroundStyle(.tertiary)
            }
        }
        .padding(12)
        .background(Color.pfCard, in: RoundedRectangle(cornerRadius: 8))
        .overlay(
            RoundedRectangle(cornerRadius: 8)
                .strokeBorder(Color.pfBorder, lineWidth: 1)
        )
    }

    private var upNextSection: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("Up Next")
                .font(.title2.bold())

            VStack(spacing: 8) {
                ForEach(Array(viewModel.upcomingJobs.prefix(5)), id: \.job.id) { jobMeta in
                    upNextRow(jobMeta: jobMeta)
                }
            }
        }
    }

    private func upNextRow(jobMeta: QueuedJobWithMeta) -> some View {
        HStack(spacing: 12) {
            VStack(alignment: .leading, spacing: 2) {
                Text(jobMeta.job.fileName ?? jobMeta.job.name)
                    .font(.subheadline.weight(.medium))
                    .lineLimit(1)
                if let printerName = jobMeta.assignedPrinter?.name {
                    Label(printerName, systemImage: "printer")
                        .font(.caption2)
                        .foregroundStyle(.secondary)
                        .lineLimit(1)
                } else if let model = jobMeta.job.printerModel {
                    Label("Any \(model)", systemImage: "printer")
                        .font(.caption2)
                        .foregroundStyle(.tertiary)
                        .lineLimit(1)
                }
            }

            Spacer()

            VStack(alignment: .trailing, spacing: 2) {
                Text("#\(jobMeta.job.queuePosition)")
                    .font(.subheadline.weight(.semibold).monospacedDigit())
                    .foregroundStyle(Color.pfTextSecondary)
                if let eta = jobMeta.estimatedStartTime {
                    Text(eta.relativeFormatted)
                        .font(.caption2)
                        .foregroundStyle(.secondary)
                }
            }
        }
        .padding(12)
        .background(Color.pfCard, in: RoundedRectangle(cornerRadius: 8))
        .overlay(
            RoundedRectangle(cornerRadius: 8)
                .strokeBorder(Color.pfBorder, lineWidth: 1)
        )
    }

    // MARK: - Dispatch Inline Content

    private var dispatchStatusCards: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("Queue Status")
                .font(.title2.bold())

            let twoColumns = Array(repeating: GridItem(.flexible()), count: 2)
            let threeColumns = Array(repeating: GridItem(.flexible()), count: 3)

            LazyVGrid(columns: twoColumns, spacing: 12) {
                dispatchCard(
                    title: "Queued Jobs",
                    value: "\(dispatchViewModel.totalQueuedJobs)",
                    subtitle: "Unassigned: \(dispatchViewModel.pendingJobCount)",
                    icon: "tray.full",
                    color: .pfSecondaryAccent
                )

                dispatchCard(
                    title: "Printers",
                    value: "\(dispatchViewModel.idlePrinterCount + dispatchViewModel.busyPrinterCount)",
                    subtitle: "\(dispatchViewModel.idlePrinterCount) Idle · \(dispatchViewModel.busyPrinterCount) Busy",
                    icon: "printer",
                    color: .pfAccent
                )
            }

            Text("Last 24 Hours")
                .font(.subheadline.weight(.medium))
                .foregroundStyle(.secondary)
                .padding(.top, 4)

            LazyVGrid(columns: threeColumns, spacing: 12) {
                dispatchCard(
                    title: "Dispatched",
                    value: "\(dispatchViewModel.dispatchedLast24h)",
                    icon: "checkmark.circle",
                    color: .pfSuccess
                )

                dispatchCard(
                    title: "Auto",
                    value: "\(dispatchViewModel.autoDispatchedLast24h)",
                    icon: "bolt.circle",
                    color: .pfSecondaryAccent
                )

                dispatchCard(
                    title: "Failed",
                    value: "\(dispatchViewModel.failedLast24h)",
                    icon: "xmark.circle",
                    color: dispatchViewModel.failedLast24h > 0 ? .pfWarning : .secondary
                )
            }

            if dispatchViewModel.averageScoreLast24h > 0 {
                dispatchAverageScoreCard
            }

            if let depths = dispatchViewModel.queueStatus?.printerQueueDepths, !depths.isEmpty {
                dispatchPrinterQueueDepthsSection(depths)
            }
        }
    }

    private var dispatchAverageScoreCard: some View {
        HStack(spacing: 12) {
            Image(systemName: "gauge.medium")
                .font(.title3)
                .foregroundStyle(Color.pfAccent)

            VStack(alignment: .leading, spacing: 2) {
                Text("Avg Dispatch Score (24h)")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                Text(String(format: "%.1f", dispatchViewModel.averageScoreLast24h))
                    .font(.title3.bold().monospacedDigit())
            }

            Spacer()

            ProgressView(value: min(dispatchViewModel.averageScoreLast24h / 100.0, 1.0))
                .tint(.pfAccent)
                .frame(width: 80)
        }
        .padding(12)
        .background(Color.pfCard, in: RoundedRectangle(cornerRadius: 12))
        .overlay(
            RoundedRectangle(cornerRadius: 12)
                .strokeBorder(Color.pfBorder, lineWidth: 1)
        )
    }

    private func dispatchPrinterQueueDepthsSection(_ depths: [PrinterQueueDepth]) -> some View {
        VStack(alignment: .leading, spacing: 8) {
            Text("Printer Queues")
                .font(.subheadline.weight(.medium))
                .foregroundStyle(.secondary)
                .padding(.top, 4)

            ForEach(depths, id: \.printerId) { printer in
                HStack(spacing: 10) {
                    Image(systemName: printer.isPrinting ? "printer.fill" : "printer")
                        .foregroundStyle(printer.isAvailable ? Color.pfSuccess : .secondary)

                    Text(printer.printerName)
                        .font(.subheadline)
                        .lineLimit(1)

                    Spacer()

                    Text("\(printer.queueDepth) queued")
                        .font(.caption.monospacedDigit())
                        .foregroundStyle(.secondary)

                    Circle()
                        .fill(printer.isPrinting ? Color.pfAccent : (printer.isAvailable ? .pfSuccess : .secondary))
                        .frame(width: 8, height: 8)
                }
                .padding(10)
                .background(Color.pfCard, in: RoundedRectangle(cornerRadius: 8))
                .overlay(
                    RoundedRectangle(cornerRadius: 8)
                        .strokeBorder(Color.pfBorder, lineWidth: 1)
                )
            }
        }
    }

    private func dispatchCard(
        title: String,
        value: String,
        subtitle: String? = nil,
        icon: String,
        color: Color
    ) -> some View {
        VStack(spacing: 6) {
            Image(systemName: icon)
                .font(.title3)
                .foregroundStyle(color)

            Text(value)
                .font(.title2.bold().monospacedDigit())

            Text(title)
                .font(.caption)
                .foregroundStyle(.secondary)
                .multilineTextAlignment(.center)

            if let subtitle {
                Text(subtitle)
                    .font(.caption2)
                    .foregroundStyle(.tertiary)
                    .multilineTextAlignment(.center)
            }
        }
        .frame(maxWidth: .infinity)
        .padding(.vertical, 14)
        .background(Color.pfCard, in: RoundedRectangle(cornerRadius: 12))
        .overlay(
            RoundedRectangle(cornerRadius: 12)
                .strokeBorder(Color.pfBorder, lineWidth: 1)
        )
    }

    private var dispatchHistorySection: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("Recent Dispatches")
                .font(.title2.bold())

            if dispatchViewModel.history.isEmpty {
                Text("No recent dispatch history")
                    .font(.subheadline)
                    .foregroundStyle(.secondary)
                    .frame(maxWidth: .infinity)
                    .padding(.vertical, 20)
            } else {
                ForEach(dispatchViewModel.history.prefix(20), id: \.id) { entry in
                    dispatchHistoryRow(entry)
                }
            }
        }
    }

    private func dispatchHistoryRow(_ entry: DispatchHistoryEntry) -> some View {
        HStack(spacing: 10) {
            Image(systemName: "arrow.right.circle.fill")
                .foregroundStyle(Color.pfAccent)

            VStack(alignment: .leading, spacing: 2) {
                Text(entry.jobName ?? "Job \(entry.printJobId.uuidString.prefix(8))")
                    .font(.subheadline.weight(.medium))
                    .lineLimit(1)

                HStack(spacing: 8) {
                    if let printer = entry.printerName {
                        Label(printer, systemImage: "printer")
                            .font(.caption)
                            .foregroundStyle(.secondary)
                    }
                    Text(entry.action)
                        .font(.caption)
                        .foregroundStyle(.secondary)
                }
            }

            Spacer()

            Text(entry.createdAtUtc, style: .relative)
                .font(.caption2)
                .foregroundStyle(.tertiary)
        }
        .padding(10)
        .background(Color.pfCard, in: RoundedRectangle(cornerRadius: 8))
        .overlay(
            RoundedRectangle(cornerRadius: 8)
                .strokeBorder(Color.pfBorder, lineWidth: 1)
        )
    }

    // MARK: - Utilization Section

    private func utilizationSection(summary: StatisticsSummary) -> some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("Farm Utilization")
                .font(.title2.bold())

            let columns = sizeClass == .regular
                ? [GridItem(.flexible()), GridItem(.flexible()), GridItem(.flexible()), GridItem(.flexible())]
                : [GridItem(.flexible()), GridItem(.flexible())]

            LazyVGrid(columns: columns, spacing: 12) {
                utilizationCard(
                    title: "Success Rate",
                    value: String(format: "%.0f%%", summary.successRate),
                    icon: "checkmark.seal",
                    color: summary.successRate >= 90 ? .pfSuccess : (summary.successRate >= 70 ? .pfWarning : .pfError)
                )
                utilizationCard(
                    title: "Print Hours",
                    value: String(format: "%.0f", summary.totalPrintHours),
                    icon: "clock.fill",
                    color: .pfAccent
                )
                utilizationCard(
                    title: "Filament Used",
                    value: summary.totalFilamentGrams >= 1000
                        ? String(format: "%.1f kg", summary.totalFilamentGrams / 1000.0)
                        : String(format: "%.0f g", summary.totalFilamentGrams),
                    icon: "circle.circle",
                    color: .pfSecondaryAccent
                )
                utilizationCard(
                    title: "Completed",
                    value: "\(summary.completedJobs)/\(summary.totalJobs)",
                    icon: "tray.full.fill",
                    color: .pfTextPrimary
                )
            }
        }
    }

    private func utilizationCard(title: String, value: String, icon: String, color: Color) -> some View {
        VStack(spacing: 6) {
            Image(systemName: icon)
                .font(.title3)
                .foregroundStyle(color)
            Text(value)
                .font(.title3.bold().monospacedDigit())
            Text(title)
                .font(.caption)
                .foregroundStyle(.secondary)
                .multilineTextAlignment(.center)
        }
        .frame(maxWidth: .infinity)
        .padding(.vertical, 14)
        .background(Color.pfCard, in: RoundedRectangle(cornerRadius: 12))
        .overlay(
            RoundedRectangle(cornerRadius: 12)
                .strokeBorder(Color.pfBorder, lineWidth: 1)
        )
    }

    // MARK: - Maintenance Alert

    private var maintenanceAlert: some View {
        HStack(spacing: 10) {
            Image(systemName: "wrench.and.screwdriver.fill")
                .foregroundStyle(Color.pfWarning)

            VStack(alignment: .leading, spacing: 2) {
                Text("\(viewModel.maintenanceCount) printer(s) in maintenance")
                    .font(.subheadline.weight(.medium))
                Text(viewModel.printersInMaintenance.map(\.name).joined(separator: ", "))
                    .font(.caption)
                    .foregroundStyle(Color.pfTextSecondary)
                    .lineLimit(1)
            }

            Spacer()
        }
        .padding(12)
        .background(Color.pfWarning.opacity(0.1), in: RoundedRectangle(cornerRadius: 10))
    }

    // MARK: - States

    private var loadingState: some View {
        VStack(spacing: 16) {
            Spacer(minLength: 100)
            ProgressView("Loading dashboard…")
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
    
    // MARK: - Helpers
    
    private func sortPriority(_ printer: Printer) -> Int {
        // PendingReady always sorts to top regardless of isOnline
        if printer.state?.lowercased() == "pendingready" { return 0 }
        guard printer.isOnline else { return 100 }
        switch printer.state?.lowercased() {
        case "printing": return 1
        case "ready", "idle": return 2
        default: return 3
        }
    }
}

// MARK: - Summary Card

private struct SummaryCard: View {
    let title: String
    let count: Int
    let icon: String
    let color: Color
    var isLarge: Bool = false

    var body: some View {
        VStack(spacing: isLarge ? 10 : 6) {
            Image(systemName: icon)
                .font(isLarge ? .title2 : .title3)
                .foregroundStyle(color)

            Text("\(count)")
                .font(isLarge ? .title.bold().monospacedDigit() : .title2.bold().monospacedDigit())

            Text(title)
                .font(isLarge ? .subheadline : .caption)
                .foregroundStyle(.secondary)
        }
        .frame(maxWidth: .infinity)
        .padding(.vertical, isLarge ? 20 : 14)
        .background(Color.pfCard, in: RoundedRectangle(cornerRadius: 12))
        .overlay(
            RoundedRectangle(cornerRadius: 12)
                .strokeBorder(Color.pfBorder, lineWidth: 1)
        )
    }
}

// MARK: - Navigation Helper

@MainActor @ViewBuilder
func destinationView(for destination: AppDestination) -> some View {
    switch destination {
    case .printerDetail(let id):
        PrinterDetailView(printerId: id)
    case .jobDetail(let id):
        JobDetailView(jobId: id)
    case .locationDetail:
        ContentUnavailableView {
            Label("Coming Soon", systemImage: "map")
        } description: {
            Text("Location details will be available in a future update.")
        }
    case .createJob:
        ContentUnavailableView {
            Label("Coming Soon", systemImage: "plus.circle")
        } description: {
            Text("Job creation will be available in a future update.")
        }
    case .createPrinter:
        ContentUnavailableView {
            Label("Coming Soon", systemImage: "printer.fill.and.paper")
        } description: {
            Text("Printer setup will be available in a future update.")
        }
    case .maintenanceAnalytics:
        MaintenanceAnalyticsView()
    case .uptimeReliability:
        UptimeView()
    case .predictiveInsights(let printerId):
        PredictiveInsightsView(printerId: printerId)
    case .jobHistory:
        JobHistoryView()
    case .jobTimeline:
        JobTimelineView()
    case .dispatchDashboard:
        DispatchDashboardView()
    }
}

// MARK: - Active Job Row (printer with running job)

private struct ActiveJobRow: View {
    let printer: Printer
    var activeJob: QueuedPrintJobResponse?

    private var etaInfo: (remaining: TimeInterval, completion: Date)? {
        guard let startTime = activeJob?.job.actualStartTimeUtc,
              let estSeconds = activeJob?.job.estimatedPrintTimeSeconds, estSeconds > 0 else {
            return nil
        }
        let total = TimeInterval(estSeconds)
        let elapsed = Date.now.timeIntervalSince(startTime)
        let remaining = max(0, total - elapsed)
        let completion = Date.now.addingTimeInterval(remaining)
        return (remaining, completion)
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack {
                VStack(alignment: .leading, spacing: 2) {
                    Text(printer.fileName ?? printer.jobName ?? "Unknown Job")
                        .font(.subheadline.weight(.medium))
                        .lineLimit(1)

                    Label(printer.name, systemImage: "printer")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                        .lineLimit(1)
                }

                Spacer()

                StatusBadge(printerState: printer.state, isOnline: printer.isOnline)
            }

            if let progress = printer.progress {
                HStack(spacing: 8) {
                    PrintProgressBar(progress: progress, height: 6)
                    Text("\(Int(progress * 100))%")
                        .font(.caption.weight(.medium).monospacedDigit())
                        .foregroundStyle(Color.pfAccent)
                        .frame(width: 36, alignment: .trailing)
                }
            }

            if let eta = etaInfo {
                HStack(spacing: 4) {
                    Image(systemName: "clock")
                        .font(.caption2)
                    if eta.remaining > 0 {
                        Text("~\(eta.remaining.durationFormatted) left")
                            .font(.caption)
                        Text("·")
                            .font(.caption)
                            .foregroundStyle(.tertiary)
                        Text("Done \(eta.completion.shortTimeFormatted)")
                            .font(.caption)
                    } else {
                        Text("Completing…")
                            .font(.caption)
                    }
                }
                .foregroundStyle(.secondary)
            }
        }
        .padding(12)
        .background(Color.pfCard, in: RoundedRectangle(cornerRadius: 10))
        .overlay(
            RoundedRectangle(cornerRadius: 10)
                .strokeBorder(Color.pfBorder, lineWidth: 1)
        )
    }
}
