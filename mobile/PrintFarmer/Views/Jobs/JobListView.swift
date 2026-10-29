import SwiftUI
import OSLog
#if DEBUG
import UIKit
#endif

struct JobListView: View {
    @Environment(AuthViewModel.self) private var authViewModel
    @Environment(AppRouter.self) private var router
    @Environment(ServiceContainer.self) private var services
    @Environment(\.dynamicTypeSize) private var dynamicTypeSize
    private let ownsNavigationStack: Bool
    @State private var viewModel = JobListViewModel()
    @State private var queueEditMode: EditMode = .inactive
    @State private var retryTask: Task<Void, Never>?
    @State private var showsJobHistory = false
    @State private var historyNavigationPath: [AppDestination] = []
    #if DEBUG
    @State private var queueReorderDiagnostics: String?
    #endif
    @Environment(\.scenePhase) private var scenePhase

    private var activePrinterStatusRequestKey: String {
        let ids = viewModel.activePrinterIDs.map(\.uuidString).joined(separator: ",")
        return "\(services.activeServerGeneration):\(viewModel.isViewActive):\(scenePhase == .active):\(ids)"
    }

    init(ownsNavigationStack: Bool = true) {
        self.ownsNavigationStack = ownsNavigationStack
    }

    private func queueCard<Content: View>(
        @ViewBuilder content: () -> Content
    ) -> some View {
        content()
            .padding(10)
            .frame(maxWidth: .infinity, alignment: .leading)
            .background(Color.pfCard, in: RoundedRectangle(cornerRadius: 12))
            .overlay {
                RoundedRectangle(cornerRadius: 12)
                    .strokeBorder(Color.pfBorder, lineWidth: 1)
            }
    }

    var body: some View {
        @Bindable var router = router

        Group {
            if ownsNavigationStack {
                NavigationStack(path: $router.jobsPath) {
                    screenContent
                }
            } else {
                screenContent
            }
        }
        .task {
            #if DEBUG
            if QueueReorderUITestDiagnostics.isActive {
                queueReorderDiagnostics = QueueReorderUITestDiagnostics.shared.summary
            }
            QueueReorderUITestDiagnostics.logIfActive("loadTask start")
            #endif
            viewModel.activate()
            viewModel.configure(jobService: services.jobService)
            viewModel.configure(jobAnalyticsService: services.jobAnalyticsService)
            viewModel.configureSignalR(services.signalRService)
            viewModel.setQueueWriteAuthorization(canWriteQueue)
            viewModel.startObservingNetworkPath()
            #if DEBUG
            QueueReorderUITestObservation.start(viewModel)
            #endif
            await viewModel.loadJobs()
            queueEditMode = viewModel.keepsQueueEditingActive ? .active : .inactive
            #if DEBUG
            QueueReorderUITestDiagnostics.logIfActive(
                "loadTask end cancelled=\(Task.isCancelled) editMode=\(queueEditMode == .active)"
            )
            #endif
        }
        .task(id: activePrinterStatusRequestKey) {
            #if DEBUG
            QueueReorderUITestDiagnostics.logIfActive("statusTask start key=\(activePrinterStatusRequestKey)")
            defer { QueueReorderUITestDiagnostics.logIfActive("statusTask end cancelled=\(Task.isCancelled)") }
            #endif
            await refreshActivePrinterStatuses()
        }
        .onChange(of: canWriteQueue) { _, isAuthorized in
            viewModel.setQueueWriteAuthorization(isAuthorized)
        }
        .onChange(of: viewModel.keepsQueueEditingActive, initial: true) { _, isActive in
            queueEditMode = isActive ? .active : .inactive
            #if DEBUG
            QueueReorderUITestDiagnostics.logIfActive("editMode=\(isActive)")
            #endif
        }
        .onDisappear {
            retryTask?.cancel()
            viewModel.deactivate()
        }
    }

    private var canWriteQueue: Bool {
        guard authViewModel.isAuthenticated,
              !authViewModel.snapshotActivationPending,
              let user = authViewModel.currentUser,
              user.isActive else {
            return false
        }

        return user.permissions.contains("queue:write")
            || user.roles.contains("farm_admin")
    }

    private func refreshActivePrinterStatuses() async {
        let printerIDs = viewModel.activePrinterIDs
        guard scenePhase == .active, !printerIDs.isEmpty else {
            await viewModel.pollActivePrinterStatuses(
                printerIDs: printerIDs, isForeground: scenePhase == .active
            )
            return
        }

        let generation = services.activeServerGeneration
        await services.awaitActiveServerSettled()
        guard !Task.isCancelled, services.isActiveGeneration(generation) else { return }
        viewModel.configure(printerService: services.printerService)
        await viewModel.pollActivePrinterStatuses(printerIDs: printerIDs) {
            services.isActiveGeneration(generation)
        }
    }

    @ViewBuilder
    private var screenContent: some View {
        Group {
            if viewModel.isLoading && viewModel.jobs.isEmpty {
                ProgressView("Loading jobs…")
                    .frame(maxWidth: .infinity, maxHeight: .infinity)
            } else if let error = viewModel.errorMessage, viewModel.jobs.isEmpty {
                ContentUnavailableView {
                    Label("Error", systemImage: "exclamationmark.triangle")
                } description: {
                    Text(error)
                } actions: {
                    Button("Retry") {
                        retryTask?.cancel()
                        retryTask = Task { await viewModel.loadJobs() }
                    }
                }
            } else if !viewModel.hasAnyJobs {
                EmptyStateView(
                    icon: "tray",
                    title: "No Print Jobs",
                    message: "No jobs in the queue. Jobs will appear here when queued."
                )
            } else {
                jobList
            }
        }
        .navigationTitle("Queue")
        #if os(iOS)
        .navigationBarTitleDisplayMode(.inline)
        #endif
        .rootNavigationChrome(for: .queue)
        .refreshable {
            await viewModel.loadJobs()
        }
        .navigationDestination(for: AppDestination.self) { destination in
            destinationView(for: destination)
        }
        .sheet(isPresented: $showsJobHistory) {
            NavigationStack(path: $historyNavigationPath) {
                Group {
                    if viewModel.completedHistoryJobs.isEmpty {
                        ContentUnavailableView(
                            "No Job History",
                            systemImage: "clock.arrow.circlepath",
                            description: Text("Completed and cancelled jobs will appear here.")
                        )
                    } else {
                        List {
                            ForEach(viewModel.completedHistoryJobs) { item in
                                recentJobRow(item)
                            }
                        }
                        .listStyle(.plain)
                    }
                }
                .navigationTitle("Job History")
                .navigationBarTitleDisplayMode(.inline)
                .navigationDestination(for: AppDestination.self) { destination in
                    destinationView(for: destination)
                }
                .toolbar {
                    ToolbarItem(placement: .confirmationAction) {
                        Button("Done") {
                            showsJobHistory = false
                        }
                    }
                }
            }
            .accessibilityIdentifier("jobList.history")
        }
        .accessibilityElement(children: .contain)
        .accessibilityIdentifier("jobList.root")
    }
    
    // MARK: - Job List

    private var jobList: some View {
        List {
            #if DEBUG
            let _ = QueueReorderUITestDiagnostics.logBody("jobList")
            let _ = QueueReorderUITestEventLogProbe.requestAttach()
            #endif
            if let message = viewModel.errorMessage {
                Section {
                    Label(message, systemImage: "exclamationmark.triangle")
                        .foregroundStyle(Color.pfError)
                }
            }

            if let message = offlineReorderMessage {
                Section {
                    Label(message, systemImage: "wifi.slash")
                        .foregroundStyle(.secondary)
                }
            }

            Section {
                if viewModel.activeJobs.isEmpty {
                    Text("No jobs currently printing.")
                        .foregroundStyle(Color.pfTextSecondary)
                } else {
                    ForEach(viewModel.activeJobs) { item in
                        queueCard { activeJobRow(item) }
                    }
                }
            } header: {
                sectionHeader(
                    "Printing",
                    countText: viewModel.queueSectionCountText(for: viewModel.activeJobs.count),
                    systemImage: "printer.fill"
                )
                    .accessibilityIdentifier("jobList.section.printing")
            }

            Section {
                if viewModel.queuedJobs.isEmpty {
                    Text("No jobs waiting to print.")
                        .foregroundStyle(Color.pfTextSecondary)
                } else {
                    ForEach(viewModel.queuedJobs) { item in
                        let groupID = item.job.jobStatus == .queued
                            ? viewModel.reorderGroupID(for: item)
                            : nil
                        let canMove = groupID.flatMap { groupID in
                            item.job.jobUUID.map { id in
                                viewModel.presentsMoveControl(id: id, inGroup: groupID)
                            }
                        } ?? false
                        queueCard { queuedJobRow(item, groupID: groupID) }
                            .moveDisabled(!canMove)
                    }
                    .onMove { offsets, destination in
                        #if DEBUG
                        if QueueReorderUITestDiagnostics.isActive {
                            QueueReorderUITestDiagnostics.shared.recordOnMove(
                                offsets: offsets,
                                destination: destination
                            )
                            queueReorderDiagnostics = QueueReorderUITestDiagnostics.shared.summary
                        }
                        #endif
                        Task { @MainActor in
                            await viewModel.moveQueuedRows(
                                fromOffsets: offsets,
                                toOffset: destination
                            )
                            #if DEBUG
                            QueueReorderUITestDiagnostics.logIfActive("moveQueuedRows returned")
                            if QueueReorderUITestDiagnostics.isActive {
                                queueReorderDiagnostics = QueueReorderUITestDiagnostics.shared.summary
                            }
                            #endif
                        }
                    }
                }
            } header: {
                Group {
                    if dynamicTypeSize.isAccessibilitySize {
                        VStack(alignment: .leading, spacing: 4) {
                            HStack {
                                Label("Queued", systemImage: "tray.full")
                                    .font(.subheadline.weight(.semibold))
                                Spacer()
                                Text(viewModel.queueSectionCountText(for: viewModel.queuedJobs.count))
                                    .font(.caption.monospacedDigit())
                                    .foregroundStyle(Color.pfTextTertiary)
                            }
                            if viewModel.presentsQueueReorder && !viewModel.reorderableQueuedJobs.isEmpty {
                                Text("Drag to reorder")
                                    .font(.caption)
                                    .foregroundStyle(Color.pfTextSecondary)
                            }
                        }
                    } else {
                        HStack {
                            Label("Queued", systemImage: "tray.full")
                                .font(.subheadline.weight(.semibold))
                            Spacer()
                            if viewModel.presentsQueueReorder && !viewModel.reorderableQueuedJobs.isEmpty {
                                Text("Drag to reorder")
                                    .font(.caption)
                                    .foregroundStyle(Color.pfTextSecondary)
                            }
                            Text(viewModel.queueSectionCountText(for: viewModel.queuedJobs.count))
                                .font(.caption.monospacedDigit())
                                .monospacedDigit()
                                .foregroundStyle(Color.pfTextTertiary)
                        }
                    }
                }
                .dynamicTypeSize(dynamicTypeSize.isAccessibilitySize ? .xxxLarge : dynamicTypeSize)
                .accessibilityElement(children: .combine)
                .accessibilityIdentifier("jobList.section.queued")
            }

            if let error = viewModel.recentFailuresError {
                Section {
                    VStack(alignment: .leading, spacing: 8) {
                        Text("Couldn't load recent failures.")
                            .foregroundStyle(Color.pfError)
                        Text(error)
                            .font(.caption)
                            .foregroundStyle(Color.pfTextSecondary)
                        Button("Retry") {
                            retryTask?.cancel()
                            retryTask = Task { await viewModel.loadJobs() }
                        }
                    }
                    .dynamicTypeSize(dynamicTypeSize.isAccessibilitySize ? .xxxLarge : dynamicTypeSize)
                } header: {
                    recentFailuresHeader(count: 0)
                        .accessibilityIdentifier("jobList.section.recent-failures")
                }
            } else if !viewModel.recentFailures.isEmpty {
                Section {
                    ForEach(viewModel.recentFailures) { item in
                        queueCard { recentFailureRow(item) }
                    }
                } header: {
                    recentFailuresHeader(count: viewModel.recentFailures.count)
                    .accessibilityIdentifier("jobList.section.recent-failures")
                }
            } else {
                Section {
                    Text("No recent failures.")
                        .foregroundStyle(Color.pfTextSecondary)
                } header: {
                    recentFailuresHeader(count: 0)
                        .accessibilityIdentifier("jobList.section.recent-failures")
                }
            }
        }
        .listStyle(.plain)
        .scrollContentBackground(.hidden)
        .listRowBackground(Color.clear)
        .listRowSeparator(.hidden)
        .listRowInsets(EdgeInsets(top: 4, leading: 14, bottom: 4, trailing: 14))
        .contentMargins(.bottom, 112, for: .scrollContent)
        .environment(
            \.editMode,
            $queueEditMode
        )
        .accessibilityIdentifier("jobList.combined.list")
        #if DEBUG
        .overlay(alignment: .topLeading) {
            // #3457 DEBUG test seam: zero-geometry counters label asserted by Harvest XCUI, not a List row.
            if let queueReorderDiagnostics {
                Text(queueReorderDiagnostics)
                    .font(.system(size: 1))
                    .foregroundStyle(.clear)
                    .frame(width: 1, height: 1)
                    .allowsHitTesting(false)
                    .accessibilityLabel(queueReorderDiagnostics)
                    .accessibilityIdentifier("jobList.reorderDiagnostics")
            }
            // R3457-D01 diagnostic only: deferred event trail; drag/drop state read at gesture transitions.
            if QueueReorderUITestDiagnostics.isActive {
                QueueReorderUITestEventLogProbe()
                    .frame(width: 1, height: 1)
                    .allowsHitTesting(false)
            }
        }
        #endif
    }

    private func recentFailuresHeader(count: Int) -> some View {
        HStack {
            Label("Recent failures", systemImage: "exclamationmark.triangle")
                .font(.subheadline.weight(.semibold))
                .lineLimit(1)
                .minimumScaleFactor(0.85)
            Spacer()
            Menu {
                Button {
                    historyNavigationPath = []
                    showsJobHistory = true
                } label: {
                    Label("Job history", systemImage: "clock.arrow.circlepath")
                }
                .accessibilityIdentifier("jobList.history.select")
            } label: {
                Image(systemName: "ellipsis")
                    .frame(width: 32, height: 32)
                    .contentShape(Rectangle())
            }
            .accessibilityLabel("Job history")
            .accessibilityHint("Opens completed and cancelled jobs, including harvest actions.")
            .accessibilityIdentifier("jobList.history.open")
            Text("\(count)")
                .font(.caption.monospacedDigit())
                .foregroundStyle(Color.pfTextTertiary)
        }
        .dynamicTypeSize(dynamicTypeSize.isAccessibilitySize ? .xxxLarge : dynamicTypeSize)
        .accessibilityElement(children: .contain)
    }

    private func sectionHeader(_ title: String, countText: String, systemImage: String) -> some View {
        HStack {
            Label(title, systemImage: systemImage)
                .font(.subheadline.weight(.semibold))
            Spacer()
            Text(countText)
                .font(.caption.monospacedDigit())
                .monospacedDigit()
                .foregroundStyle(Color.pfTextTertiary)
        }
        .dynamicTypeSize(dynamicTypeSize.isAccessibilitySize ? .xxxLarge : dynamicTypeSize)
        .padding(.vertical, 2)
        .accessibilityElement(children: .combine)
    }

    private var offlineReorderMessage: String? {
        guard viewModel.queueWriteAuthorized,
              !viewModel.isNetworkReachable else {
            return nil
        }
        return "Queue reordering is unavailable while offline."
    }

    // MARK: - Active Job Row

    private func activeJobRow(_ item: QueuedPrintJobResponse) -> some View {
        jobDetailLink(for: item) {
            Group {
                if dynamicTypeSize.isAccessibilitySize {
                    VStack(alignment: .leading, spacing: 8) {
                        Text(item.displayName)
                            .font(.subheadline.weight(.semibold))
                            .fixedSize(horizontal: false, vertical: true)
                        StatusBadge(jobStatus: item.job.jobStatus)
                        HStack(alignment: .top, spacing: 12) {
                            jobThumbnail(for: item, size: 44)
                            activeJobDetails(item)
                        }
                    }
                } else {
                    HStack(spacing: 12) {
                        jobThumbnail(for: item, size: 44)
                        VStack(alignment: .leading, spacing: 4) {
                            HStack {
                                Text(item.displayName)
                                    .font(.subheadline.weight(.semibold))
                                    .lineLimit(1)
                                Spacer()
                                StatusBadge(jobStatus: item.job.jobStatus)
                            }
                            activeJobDetails(item)
                        }
                    }
                }
            }
            .padding(.vertical, 2)
        }
        .buttonStyle(.plain)
        .accessibilityIdentifier("job.row.\(item.job.jobUUID?.uuidString ?? "unknown")")
    }

    @ViewBuilder
    private func activeJobDetails(_ item: QueuedPrintJobResponse) -> some View {
        let printerID = item.job.assignedPrinterId.flatMap(UUID.init(uuidString:))
        let status = printerID.flatMap { viewModel.activePrinterStatuses[$0] }
        VStack(alignment: .leading, spacing: 4) {
            if let printerName = Self.activeJobPrinterName(item) {
                Label(printerName, systemImage: "printer")
                    .font(.caption2)
                    .foregroundStyle(.secondary)
                    .lineLimit(dynamicTypeSize.isAccessibilitySize ? nil : 1)
                    .fixedSize(horizontal: false, vertical: true)
            }

            if let status {
                if let progress = status.progress, progress.isFinite {
                    PrintProgressBar(
                        progress: progress, height: 4,
                        color: progressColor(for: item.job.jobStatus)
                    )
                    .accessibilityIdentifier("job.active.progress.\(item.job.id)")
                } else {
                    Text("Progress unavailable")
                        .font(.caption2)
                        .foregroundStyle(Color.pfTextSecondary)
                }
                if dynamicTypeSize.isAccessibilitySize {
                    VStack(alignment: .leading, spacing: 4) {
                        if item.job.isMultiCopy {
                            Label("\(item.job.completedCopies)/\(item.job.copies)", systemImage: "doc.on.doc")
                                .font(.caption2)
                                .foregroundStyle(.secondary)
                        }
                        activeJobETA(status)
                        .accessibilityIdentifier("job.active.eta.\(item.job.id)")
                    }
                } else {
                    HStack {
                        if item.job.isMultiCopy {
                            Label("\(item.job.completedCopies)/\(item.job.copies)", systemImage: "doc.on.doc")
                                .font(.caption2)
                                .foregroundStyle(.secondary)
                        }
                        Spacer()
                        activeJobETA(status)
                            .accessibilityIdentifier("job.active.eta.\(item.job.id)")
                    }
                }
            } else if printerID == nil || printerID.map(viewModel.unavailableActivePrinterStatuses.contains) == true {
                Text("Live status unavailable")
                    .font(.caption2)
                    .foregroundStyle(Color.pfTextSecondary)
            } else {
                Text("Loading live status…")
                    .font(.caption2)
                    .foregroundStyle(Color.pfTextSecondary)
            }
        }
    }

    static func activeJobPrinterName(_ item: QueuedPrintJobResponse) -> String? {
        if let name = item.job.printerName,
           !name.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
            return name
        }
        return item.job.assignedPrinterId != nil || item.assignedPrinter != nil
            ? "Assigned to printer" : nil
    }

    private func activeJobETA(_ status: PrinterStatusDetail) -> some View {
        Group {
            if let seconds = status.printTimeLeftSeconds,
               seconds.isFinite, seconds >= 0 {
                Label("\(seconds.durationFormatted) left", systemImage: "clock")
                            .font(.caption2)
                            .foregroundStyle(.secondary)
            } else {
                Label("ETA unavailable", systemImage: "clock")
                    .font(.caption2)
                    .foregroundStyle(Color.pfTextSecondary)
            }
        }
    }

    // MARK: - Queued Job Row

    private func queuedJobRow(
        _ item: QueuedPrintJobResponse,
        groupID: String? = nil
    ) -> some View {
        queueRowAccessibilityActions(item, groupID: groupID) {
            jobDetailLink(for: item) {
                Group {
                    if dynamicTypeSize.isAccessibilitySize {
                        VStack(alignment: .leading, spacing: 8) {
                            Text(item.displayName)
                                .font(.subheadline.weight(.semibold))
                                .fixedSize(horizontal: false, vertical: true)
                            HStack(alignment: .top, spacing: 12) {
                                jobThumbnail(for: item, size: 44)
                                queuedJobDetails(item, stacksMetadata: true)
                            }
                        }
                    } else {
                        HStack(spacing: 12) {
                            jobThumbnail(for: item, size: 44)
                            VStack(alignment: .leading, spacing: 4) {
                                HStack {
                                    Text(item.displayName)
                                        .font(.subheadline.weight(.semibold))
                                        .lineLimit(1)
                                    Spacer()
                                }
                                queuedJobDetails(item, stacksMetadata: false)
                            }
                        }
                    }
                }
                .padding(.vertical, 2)
            }
            .buttonStyle(.plain)
            .accessibilityIdentifier("job.row.\(item.job.jobUUID?.uuidString ?? "unknown")")
        }
        .swipeActions(edge: .trailing) {
            if let uuid = item.job.jobUUID {
                Button {
                    UIImpactFeedbackGenerator(style: .medium).impactOccurred()
                    retryTask?.cancel()
                    retryTask = Task { await viewModel.cancelJob(id: uuid) }
                } label: {
                    Label("Cancel", systemImage: "xmark.circle")
                }
                .tint(Color.pfErrorFill)
            }
        }
        .swipeActions(edge: .leading) {
            if let uuid = item.job.jobUUID {
                Button {
                    UIImpactFeedbackGenerator(style: .medium).impactOccurred()
                    retryTask?.cancel()
                    retryTask = Task { await viewModel.dispatchJob(id: uuid) }
                } label: {
                    Label("Start", systemImage: "play.circle.fill")
                }
                .tint(Color.pfAccent)
            }
        }
    }

    @ViewBuilder
    private func queuedJobDetails(
        _ item: QueuedPrintJobResponse,
        stacksMetadata: Bool
    ) -> some View {
        if stacksMetadata {
            VStack(alignment: .leading, spacing: 4) {
                Text(Self.queuedJobMetadata(item))
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
                if let duration = item.job.estimatedDuration {
                    Label(duration.durationFormatted, systemImage: "clock")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                }
            }
        } else {
            Text(Self.queuedJobMetadata(item))
                .font(.caption)
                .foregroundStyle(.secondary)
                .lineLimit(1)
        }
    }

    static func queuedJobMetadata(_ item: QueuedPrintJobResponse) -> String {
        var parts: [String] = []
        switch item.job.priority {
        case .low:
            parts.append("Low")
        case .normal:
            break
        case .high:
            parts.append("High")
        case .urgent:
            parts.append("Urgent")
        }
        if let material = item.gcodeFile?.materialType ?? item.job.filamentName {
            parts.append(material)
        }
        if let assignedPrinter = item.assignedPrinter,
           !assignedPrinter.name.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
            parts.append("Assigned to \(assignedPrinter.name)")
        } else if item.job.assignedPrinterId != nil || item.assignedPrinter != nil {
            parts.append("Assigned to printer")
        } else {
            parts.append("Unassigned")
        }
        return parts.joined(separator: " · ")
    }

    private func queueRowAccessibilityActions<Content: View>(
        _ item: QueuedPrintJobResponse,
        groupID: String?,
        @ViewBuilder content: () -> Content
    ) -> some View {
        // Action availability changes during a move; the row itself must not
        // switch conditional-content branches under UIKit's interactive drag.
        content()
            .accessibilityActions {
                if let groupID, let id = item.job.jobUUID {
                    if viewModel.canMoveQueuedJob(id: id, direction: .up, inGroup: groupID) {
                        Button("Move up") {
                            Task { @MainActor in
                                await viewModel.moveQueuedJob(id: id, direction: .up, inGroup: groupID)
                            }
                        }
                    }
                    if viewModel.canMoveQueuedJob(id: id, direction: .down, inGroup: groupID) {
                        Button("Move down") {
                            Task { @MainActor in
                                await viewModel.moveQueuedJob(id: id, direction: .down, inGroup: groupID)
                            }
                        }
                    }
                }
            }
    }

    // MARK: - Recent Job Row

    private func recentFailureRow(_ item: QueueHistoryEntry) -> some View {
        let jobID = UUID(uuidString: item.id)
        return Group {
            if dynamicTypeSize.isAccessibilitySize {
                HStack(alignment: .top, spacing: 8) {
                    recentFailureNavigationLink(item, id: jobID)
                        .frame(maxWidth: .infinity, alignment: .leading)
                    if let jobID {
                        rerunFailedJobButton(id: jobID)
                    }
                }
            } else {
                HStack(alignment: .top, spacing: 12) {
                    recentFailureNavigationLink(item, id: jobID)
                        .layoutPriority(1)
                    if let jobID {
                        rerunFailedJobButton(id: jobID)
                    }
                }
            }
        }
    }

    @ViewBuilder
    private func recentFailureNavigationLink(
        _ item: QueueHistoryEntry,
        id: UUID?
    ) -> some View {
        if let id {
            NavigationLink(value: AppDestination.jobDetail(id: id)) {
                recentFailureRowContent(item)
            }
            .buttonStyle(.plain)
            .accessibilityLabel(recentFailureAccessibilityLabel(item))
            .accessibilityIdentifier("job.row.\(item.id)")
        } else {
            recentFailureRowContent(item)
                .accessibilityElement(children: .combine)
                .accessibilityLabel(recentFailureAccessibilityLabel(item))
                .accessibilityIdentifier("job.row.\(item.id)")
        }
    }

    private func rerunFailedJobButton(id: UUID) -> some View {
        Button {
            UIImpactFeedbackGenerator(style: .medium).impactOccurred()
            Task { await viewModel.rerunFailedJob(id: id) }
        } label: {
            Text("Retry")
                .font(.subheadline.weight(.semibold))
                .dynamicTypeSize(dynamicTypeSize.isAccessibilitySize ? .xxxLarge : dynamicTypeSize)
                .foregroundStyle(Color.pfAccent)
                .padding(.horizontal, 8)
                .padding(.vertical, 6)
                .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .fixedSize()
        .disabled(!viewModel.canRerunFailedJobs || viewModel.rerunningFailedJobIDs.contains(id))
        .accessibilityHint("Creates a new queued copy of this failed job.")
        .accessibilityIdentifier("job.retry.\(id.uuidString.lowercased())")
    }

    private func recentFailureRowContent(_ item: QueueHistoryEntry) -> some View {
        Group {
            if dynamicTypeSize.isAccessibilitySize {
                VStack(alignment: .leading, spacing: 8) {
                    HStack(alignment: .top, spacing: 12) {
                        recentFailureIcon
                        VStack(alignment: .leading, spacing: 4) {
                            Text(item.jobName)
                                .font(.subheadline)
                                .dynamicTypeSize(.xxxLarge)
                                .lineLimit(2)
                            StatusBadge(jobStatus: .failed)
                                .dynamicTypeSize(.xxxLarge)
                        }
                    }
                    VStack(alignment: .leading, spacing: 4) {
                        Text(recentFailureSummary(item))
                            .font(.caption)
                            .dynamicTypeSize(.xxxLarge)
                            .foregroundStyle(Color.pfError)
                        if let printerName = item.printerName {
                            Text(printerName)
                                .font(.caption)
                                .dynamicTypeSize(.xxxLarge)
                                .foregroundStyle(Color.pfTextSecondary)
                        }
                        if let completedAt = item.completedAt {
                            Text(completedAt.formatted(date: .omitted, time: .shortened))
                                .font(.caption2)
                                .dynamicTypeSize(.xxxLarge)
                                .foregroundStyle(Color.pfTextTertiary)
                        }
                    }
                }
            } else {
                HStack(spacing: 12) {
                    recentFailureIcon
                    VStack(alignment: .leading, spacing: 4) {
                        HStack {
                            Text(item.jobName)
                                .font(.subheadline)
                                .lineLimit(1)
                            Spacer()
                            StatusBadge(jobStatus: .failed)
                        }
                        HStack {
                            Text(recentFailureSummary(item))
                                .font(.caption)
                                .foregroundStyle(Color.pfError)
                                .lineLimit(1)
                            if let printerName = item.printerName {
                                Text("· \(printerName)")
                                    .font(.caption)
                                    .foregroundStyle(Color.pfTextSecondary)
                                    .lineLimit(1)
                            }
                            Spacer()
                            if let completedAt = item.completedAt {
                                Text(completedAt.formatted(date: .omitted, time: .shortened))
                                    .font(.caption2)
                                    .foregroundStyle(Color.pfTextTertiary)
                                    .lineLimit(1)
                            }
                        }
                    }
                }
            }
        }
        .padding(.vertical, 2)
    }

    private var recentFailureIcon: some View {
        Image(systemName: "exclamationmark.triangle.fill")
            .font(.system(size: 18))
            .foregroundStyle(Color.pfError)
            .frame(width: 36, height: 36)
            .background(
                Color.pfError.opacity(0.12),
                in: RoundedRectangle(cornerRadius: 8)
            )
    }

    private func recentFailureSummary(_ item: QueueHistoryEntry) -> String {
        guard let percentage = item.completionPercentage,
              percentage.isFinite,
              (0...100).contains(percentage) else {
            return "Failed"
        }
        return "Failed at \(Int(percentage.rounded()))%"
    }

    private func recentFailureAccessibilityLabel(_ item: QueueHistoryEntry) -> String {
        var components = [
            item.jobName,
            "Failed status",
            recentFailureSummary(item),
        ]
        if let completedAt = item.completedAt {
            components.append(completedAt.relativeFormatted)
        }
        if let printerName = item.printerName {
            components.append(printerName)
        }
        if let failureReason = item.failureReason {
            components.append(failureReason)
        }
        return components.joined(separator: ", ")
    }

    private func recentJobRow(_ item: QueuedPrintJobResponse) -> some View {
        jobDetailLink(for: item) {
            Group {
                if dynamicTypeSize.isAccessibilitySize {
                    VStack(alignment: .leading, spacing: 8) {
                        HStack(alignment: .top, spacing: 12) {
                            jobThumbnail(for: item, size: 36)
                            VStack(alignment: .leading, spacing: 4) {
                                Text(item.job.name)
                                    .font(.subheadline)
                                    .dynamicTypeSize(dynamicTypeSize.isAccessibilitySize ? .xxxLarge : dynamicTypeSize)
                                    .lineLimit(2)
                                StatusBadge(jobStatus: item.job.jobStatus)
                                    .dynamicTypeSize(dynamicTypeSize.isAccessibilitySize ? .xxxLarge : dynamicTypeSize)
                            }
                        }
                        VStack(alignment: .leading, spacing: 4) {
                            if let printerName = item.job.printerName {
                                Text(printerName)
                                    .font(.caption)
                                    .dynamicTypeSize(dynamicTypeSize.isAccessibilitySize ? .xxxLarge : dynamicTypeSize)
                                    .foregroundStyle(.secondary)
                                    .lineLimit(1)
                            }
                            if let endTime = item.job.actualEndTimeUtc {
                                Text(endTime.relativeFormatted)
                                    .font(.caption2)
                                    .dynamicTypeSize(dynamicTypeSize.isAccessibilitySize ? .xxxLarge : dynamicTypeSize)
                                    .foregroundStyle(.tertiary)
                            }
                        }
                    }
                } else {
                    HStack(spacing: 12) {
                        jobThumbnail(for: item, size: 36)
                        VStack(alignment: .leading, spacing: 4) {
                            HStack {
                                Text(item.job.name)
                                    .font(.subheadline)
                                    .lineLimit(1)
                                Spacer()
                                StatusBadge(jobStatus: item.job.jobStatus)
                            }
                            HStack {
                                if let printerName = item.job.printerName {
                                    Text(printerName)
                                        .font(.caption)
                                        .foregroundStyle(.secondary)
                                }
                                Spacer()
                                if let endTime = item.job.actualEndTimeUtc {
                                    Text(endTime.relativeFormatted)
                                        .font(.caption2)
                                        .foregroundStyle(.tertiary)
                                }
                            }
                            if let reason = item.job.failureReason, item.job.jobStatus == .failed {
                                Text(reason)
                                    .font(.caption)
                                    .foregroundStyle(Color.pfError)
                                    .lineLimit(2)
                            }
                        }
                    }
                }
            }
            .padding(.vertical, 2)
        }
        .buttonStyle(.plain)
        .accessibilityLabel(recentJobAccessibilityLabel(item))
        .accessibilityIdentifier("job.row.\(item.job.jobUUID?.uuidString ?? "unknown")")
    }

    @ViewBuilder
    private func jobDetailLink<Content: View>(
        for item: QueuedPrintJobResponse,
        @ViewBuilder content: () -> Content
    ) -> some View {
        if let uuid = item.job.jobUUID {
            NavigationLink(value: AppDestination.jobDetail(id: uuid)) {
                content()
            }
        } else {
            content()
        }
    }

    private func recentJobAccessibilityLabel(_ item: QueuedPrintJobResponse) -> String {
        var components = [item.job.name, "\(item.job.status) status"]
        if let printerName = item.job.printerName {
            components.append(printerName)
        }
        if let failureReason = item.job.failureReason {
            components.append(failureReason)
        }
        return components.joined(separator: ", ")
    }

    // MARK: - Helpers

    private func progressColor(for status: PrintJobStatus?) -> Color {
        switch status {
        case .printing: .pfAccent
        case .paused: .pfWarning
        default: .pfAccent
        }
    }

    // MARK: - Thumbnails

    @ViewBuilder
    private func jobThumbnail(for item: QueuedPrintJobResponse, size: CGFloat = 44) -> some View {
        let urlString = item.job.thumbnailUrl ?? item.gcodeFile?.thumbnailUrl
        AuthenticatedJobThumbnail(
            path: urlString,
            apiClient: services.apiClient,
            size: size,
            accessibilityLabel: "Thumbnail for \(item.job.name)",
            identifier: "job.thumbnail.\(item.job.id)"
        )
    }

}

struct AuthenticatedJobThumbnail: View {
    private static let maxImageBytes = 10 * 1024 * 1024
    private static let logger = Logger(subsystem: "com.printfarmer.ios", category: "JobThumbnail")

    let path: String?
    let apiClient: APIClient?
    let size: CGFloat
    let accessibilityLabel: String
    let identifier: String
    @State private var image: UIImage?

    var body: some View {
        Group {
            if let image {
                Image(uiImage: image)
                    .resizable()
                    .scaledToFill()
            } else {
                RoundedRectangle(cornerRadius: 8)
                    .fill(Color.pfCard)
                    .overlay {
                        Image(systemName: "cube")
                            .font(.system(size: size * 0.4))
                            .foregroundStyle(.tertiary)
                    }
            }
        }
        .frame(width: size, height: size)
        .clipShape(RoundedRectangle(cornerRadius: 8))
        .accessibilityElement(children: .ignore)
        .accessibilityLabel(accessibilityLabel)
        .accessibilityValue(image == nil ? "Thumbnail unavailable" : "Thumbnail loaded")
        .accessibilityIdentifier(identifier)
        .task(id: path) {
            await loadThumbnail()
        }
    }

    @MainActor
    private func loadThumbnail() async {
        image = nil
        guard let path, let apiClient, isGcodeThumbnailPath(path) else { return }
        do {
            let data = try await apiClient.getData(path)
            guard !Task.isCancelled, data.count <= Self.maxImageBytes,
                  let loadedImage = UIImage(data: data) else {
                guard !Task.isCancelled else { return }
                Self.logger.notice("Queued job thumbnail was not a decodable image")
                return
            }
            image = loadedImage
        } catch {
            guard !Task.isCancelled else { return }
            Self.logger.notice("Queued job thumbnail unavailable")
        }
    }

    private func isGcodeThumbnailPath(_ path: String) -> Bool {
        guard let components = URLComponents(string: path),
              components.scheme == nil,
              components.host == nil,
              components.queryItems?.isEmpty != false,
              components.fragment == nil,
              components.path.hasPrefix("/api/gcode-files/thumbnail/"),
              let fileID = components.path.split(separator: "/").last else {
            return false
        }
        return UUID(uuidString: String(fileID)) != nil
    }
}

#if DEBUG
/// R3457-D01 diagnostic only (one revertible commit; active only in the queue-reorder
/// UI-test fixture). Logs every view-model property invalidation through Observation
/// without touching SwiftUI state or adding view dependencies.
@MainActor
private enum QueueReorderUITestObservation {
    private static var observed: Set<ObjectIdentifier> = []

    static func start(_ viewModel: JobListViewModel) {
        guard QueueReorderUITestDiagnostics.isActive,
              observed.insert(ObjectIdentifier(viewModel)).inserted else { return }
        let probes: [(String, @MainActor @Sendable (JobListViewModel) -> String)] = [
            ("queuedOrder", { model in
                model.queuedJobs.map { String(($0.job.jobUUID?.uuidString ?? "?").suffix(2)) }
                    .joined(separator: ",")
            }),
            ("jobs", { "\($0.jobs.count)" }),
            ("isLoading", { "\($0.isLoading)" }),
            ("errorMessage", { $0.errorMessage == nil ? "nil" : "set" }),
            ("isReorderingQueue", { "\($0.isReorderingQueue)" }),
            ("isRevalidatingQueue", { "\($0.isRevalidatingQueue)" }),
            ("hasFreshQueueSnapshot", { "\($0.hasFreshQueueSnapshot)" }),
            ("queueWriteAuthorized", { "\($0.queueWriteAuthorized)" }),
            ("isNetworkReachable", { "\($0.isNetworkReachable)" }),
            ("isViewActive", { "\($0.isViewActive)" }),
            ("keepsQueueEditingActive", { "\($0.keepsQueueEditingActive)" }),
            ("presentsQueueReorder", { "\($0.presentsQueueReorder)" }),
            ("activePrinterStatuses", { "\($0.activePrinterStatuses.count)" }),
            ("unavailableActivePrinterStatuses", { "\($0.unavailableActivePrinterStatuses.count)" }),
            ("recentFailures", { "\($0.recentFailures.count)" }),
            ("jobListMayHaveMore", { "\($0.jobListMayHaveMore)" }),
        ]
        for (name, read) in probes {
            track(name, viewModel, read)
        }
    }

    private static func track(
        _ name: String,
        _ viewModel: JobListViewModel,
        _ read: @escaping @MainActor @Sendable (JobListViewModel) -> String
    ) {
        let value = withObservationTracking {
            read(viewModel)
        } onChange: { [weak viewModel] in
            QueueReorderUITestDiagnostics.shared.log("vm.\(name) willSet")
            Task { @MainActor in
                guard let viewModel else { return }
                track(name, viewModel, read)
            }
        }
        QueueReorderUITestDiagnostics.shared.log("vm.\(name)=\(value)")
    }
}

/// R3457-D01 diagnostic only: event-driven, read-only view of the List's backing collection
/// view. Logging targets are added to its existing gesture recognizers (adding a target never
/// changes recognition), including recognizers in visible cells (the reorder accessory). Drag/drop
/// state is sampled, deduplicated, at recognizer transitions/changes and contentOffset/contentSize
/// KVO, and the List's existing drag/drop delegates are wrapped by a forwarding logging proxy;
/// there is no timer. The accessibility label returns the trail published by
/// `QueueReorderUITestDiagnostics` once a gesture resolves, never a live in-gesture copy.
private struct QueueReorderUITestEventLogProbe: UIViewRepresentable {
    func makeUIView(context: Context) -> ProbeView { ProbeView() }
    func updateUIView(_ uiView: ProbeView, context: Context) {}

    /// Called from List body evaluation; defers recognizer discovery to the next main-queue turn.
    @MainActor static func requestAttach() -> Bool {
        guard QueueReorderUITestDiagnostics.isActive, let probe = ProbeView.current else { return true }
        DispatchQueue.main.async { probe.attachRecognizers() }
        return true
    }

    final class ProbeView: UIView {
        @MainActor fileprivate static weak var current: ProbeView?
        private weak var collectionView: UICollectionView?
        private var hookedRecognizers: Set<ObjectIdentifier> = []
        private var inFlightRecognizers: Set<ObjectIdentifier> = []

        override init(frame: CGRect) {
            super.init(frame: frame)
            isUserInteractionEnabled = false
            isAccessibilityElement = true
            accessibilityIdentifier = "jobList.reorderEventLog"
        }

        required init?(coder: NSCoder) { nil }

        override var accessibilityLabel: String? {
            get { QueueReorderUITestDiagnostics.shared.eventLog }
            set {}
        }

        override func didMoveToWindow() {
            super.didMoveToWindow()
            guard window != nil else {
                if Self.current === self { Self.current = nil }
                QueueReorderUITestDiagnostics.shared.log("probe detached")
                return
            }
            Self.current = self
            QueueReorderUITestDiagnostics.shared.log("probe attached")
            attachRecognizers()
        }

        fileprivate func attachRecognizers() {
            if collectionView == nil, let found = locateCollectionView() {
                collectionView = found
                QueueReorderUITestDiagnostics.shared.log(
                    "probe cv frame=\(found.frame) id=\(found.accessibilityIdentifier ?? "nil")"
                )
            }
            guard let view = collectionView else { return }
            for recognizer in view.gestureRecognizers ?? []
            where hookedRecognizers.insert(ObjectIdentifier(recognizer)).inserted {
                recognizer.addTarget(self, action: #selector(recognizerChanged(_:)))
                QueueReorderUITestDiagnostics.shared.log("probe hooked \(type(of: recognizer))")
            }
            attachSessionObservers(to: view)
            hookCellRecognizers(in: view)
        }

        // Session-callback layer (R3457-D01, log-only). UIDrag/UIDropInteraction delegates are
        // read-only, so the observable session callbacks are the collection view's drag/drop
        // delegates. A proxy is installed only over a delegate SwiftUI already set; it forwards
        // every message unchanged and mirrors `responds(to:)`, adding only void notifications.
        private var dragProxy: QueueReorderSessionLogProxy?
        private var dropProxy: QueueReorderSessionLogProxy?
        private var scrollObservations: [NSKeyValueObservation] = []
        private var lastSample = ""
        private var loggedAbsentDelegates: Set<String> = []

        private func attachSessionObservers(to view: UICollectionView) {
            if scrollObservations.isEmpty {
                QueueReorderUITestDiagnostics.shared.log(
                    "probe cv dragEnabled=\(view.dragInteractionEnabled)"
                        + " reorderCadence=\(view.reorderingCadence.rawValue)"
                        + " interactions=\(view.interactions.map { "\(type(of: $0))" })"
                )
                scrollObservations = [
                    view.observe(\.contentOffset) { [weak self] _, _ in
                        MainActor.assumeIsolated { self?.sample("scroll") }
                    },
                    view.observe(\.contentSize) { [weak self] _, _ in
                        MainActor.assumeIsolated {
                            guard let self, let cv = self.collectionView else { return }
                            self.hookCellRecognizers(in: cv)
                            self.sample("layout")
                        }
                    },
                ]
            }
            if let current = view.dragDelegate, !(current is QueueReorderSessionLogProxy) {
                let proxy = QueueReorderSessionLogProxy(role: .drag, original: current) { [weak self] in
                    self?.sessionState() ?? "probe=nil"
                }
                dragProxy = proxy
                view.dragDelegate = proxy
                QueueReorderUITestDiagnostics.shared.log("probe wrapped dragDelegate \(type(of: current))")
            } else if view.dragDelegate == nil, !loggedAbsentDelegates.contains("drag") {
                loggedAbsentDelegates.insert("drag")
                QueueReorderUITestDiagnostics.shared.log("probe dragDelegate=nil (not wrapped)")
            }
            if let current = view.dropDelegate, !(current is QueueReorderSessionLogProxy) {
                let proxy = QueueReorderSessionLogProxy(role: .drop, original: current) { [weak self] in
                    self?.sessionState() ?? "probe=nil"
                }
                dropProxy = proxy
                view.dropDelegate = proxy
                QueueReorderUITestDiagnostics.shared.log("probe wrapped dropDelegate \(type(of: current))")
            } else if view.dropDelegate == nil, !loggedAbsentDelegates.contains("drop") {
                loggedAbsentDelegates.insert("drop")
                QueueReorderUITestDiagnostics.shared.log("probe dropDelegate=nil (not wrapped)")
            }
        }

        /// Hooks every recognizer in visible cells' subtrees, including the reorder accessory's.
        private func hookCellRecognizers(in view: UICollectionView) {
            for cell in view.visibleCells {
                var pending: [UIView] = [cell]
                while !pending.isEmpty {
                    let candidate = pending.removeFirst()
                    for recognizer in candidate.gestureRecognizers ?? []
                    where hookedRecognizers.insert(ObjectIdentifier(recognizer)).inserted {
                        recognizer.addTarget(self, action: #selector(recognizerChanged(_:)))
                        let row = view.indexPath(for: cell).map { "\($0.section).\($0.item)" } ?? "?"
                        QueueReorderUITestDiagnostics.shared.log(
                            "probe hooked cell[\(row)] \(type(of: candidate)) \(type(of: recognizer))"
                        )
                    }
                    pending.append(contentsOf: candidate.subviews)
                }
            }
        }

        /// Deduplicated state sample: logs only when drag/drop/scroll/visible-row layout changed.
        private func sample(_ source: String) {
            let state = sessionState() + " rows=" + visibleRowLayout()
            guard state != lastSample else { return }
            lastSample = state
            QueueReorderUITestDiagnostics.shared.log("sample \(source) \(state)")
        }

        private func visibleRowLayout() -> String {
            guard let view = collectionView else { return "nil" }
            return view.visibleCells
                .map { cell -> (CGFloat, String) in
                    let row = view.indexPath(for: cell).map { "\($0.section).\($0.item)" } ?? "?"
                    return (cell.frame.minY, "\(row)@\(Int(cell.frame.minY.rounded()))")
                }
                .sorted { $0.0 < $1.0 }
                .map(\.1)
                .joined(separator: ",")
        }

        @objc private func recognizerChanged(_ recognizer: UIGestureRecognizer) {
            let id = ObjectIdentifier(recognizer)
            let phase: String
            switch recognizer.state {
            case .began:
                phase = "began"
                inFlightRecognizers.insert(id)
                QueueReorderUITestDiagnostics.shared.suspendPublication()
            case .ended:
                phase = "ended"
            case .cancelled:
                phase = "cancelled"
            case .failed:
                phase = "failed"
            case .changed:
                sample("gr-changed \(type(of: recognizer))")
                return
            default:
                return
            }
            QueueReorderUITestDiagnostics.shared.log("gr \(type(of: recognizer)) \(phase) \(sessionState())")
            sample("gr-\(phase)")
            guard phase != "began" else { return }
            inFlightRecognizers.remove(id)
            guard inFlightRecognizers.isEmpty else { return }
            DispatchQueue.main.async { [weak self] in
                guard let self else { return }
                QueueReorderUITestDiagnostics.shared.log("gr resolved+1 \(self.sessionState())")
                if self.inFlightRecognizers.isEmpty {
                    QueueReorderUITestDiagnostics.shared.resumePublication()
                }
            }
        }

        private func sessionState() -> String {
            guard let view = collectionView else { return "cv=nil" }
            return "drag=\(view.hasActiveDrag) drop=\(view.hasActiveDrop)"
                + " uncommitted=\(view.hasUncommittedUpdates) editing=\(view.isEditing)"
                + " tracking=\(view.isTracking) dragging=\(view.isDragging)"
                + " offsetY=\(Int(view.contentOffset.y.rounded())) cells=\(view.visibleCells.count)"
        }

        /// The nearest collection view sharing an ancestor with this overlay is the List's.
        private func locateCollectionView() -> UICollectionView? {
            var ancestor = superview
            while let container = ancestor {
                var pending = container.subviews
                while !pending.isEmpty {
                    let view = pending.removeFirst()
                    if let collectionView = view as? UICollectionView { return collectionView }
                    pending.append(contentsOf: view.subviews)
                }
                ancestor = container.superview
            }
            return nil
        }
    }
}

/// R3457-D01 diagnostic only: transparent logging proxy over the List collection view's existing
/// drag or drop delegate. Every selector the original implements is forwarded unchanged
/// (`responds(to:)` mirrors the original); the only additions are void session notifications,
/// which log and then forward when the original also implements them.
private final class QueueReorderSessionLogProxy: NSObject, UICollectionViewDragDelegate,
    UICollectionViewDropDelegate {
    enum Role { case drag, drop }

    private nonisolated static let dragNotifications: Set<Selector> = [
        #selector(UICollectionViewDragDelegate.collectionView(_:dragSessionWillBegin:)),
        #selector(UICollectionViewDragDelegate.collectionView(_:dragSessionDidEnd:)),
    ]
    private nonisolated static let dropNotifications: Set<Selector> = [
        #selector(UICollectionViewDropDelegate.collectionView(_:dropSessionDidEnter:)),
        #selector(UICollectionViewDropDelegate.collectionView(_:dropSessionDidExit:)),
        #selector(UICollectionViewDropDelegate.collectionView(_:dropSessionDidEnd:)),
    ]

    private nonisolated let role: Role
    // Read by Objective-C runtime forwarding hooks; only ever set in init.
    private nonisolated(unsafe) weak var original: NSObjectProtocol?
    private let state: () -> String
    private var lastUpdate = ""

    init(role: Role, original: NSObjectProtocol, state: @escaping () -> String) {
        self.role = role
        self.original = original
        self.state = state
    }

    private nonisolated var addedNotifications: Set<Selector> {
        role == .drag ? Self.dragNotifications : Self.dropNotifications
    }

    override func responds(to selector: Selector!) -> Bool {
        if addedNotifications.contains(selector) { return true }
        return original?.responds(to: selector) ?? false
    }

    override func forwardingTarget(for selector: Selector!) -> Any? {
        original
    }

    private var drag: UICollectionViewDragDelegate? { original as? UICollectionViewDragDelegate }
    private var drop: UICollectionViewDropDelegate? { original as? UICollectionViewDropDelegate }

    private func log(_ event: String) {
        QueueReorderUITestDiagnostics.shared.log("session \(event) \(state())")
    }

    // MARK: Drag (required + intercepted)

    func collectionView(
        _ collectionView: UICollectionView,
        itemsForBeginning session: UIDragSession,
        at indexPath: IndexPath
    ) -> [UIDragItem] {
        let items = drag?.collectionView(collectionView, itemsForBeginning: session, at: indexPath) ?? []
        log("drag itemsForBeginning \(indexPath.section).\(indexPath.item) count=\(items.count)")
        return items
    }

    func collectionView(_ collectionView: UICollectionView, dragSessionWillBegin session: UIDragSession) {
        log("drag willBegin items=\(session.items.count)")
        drag?.collectionView?(collectionView, dragSessionWillBegin: session)
    }

    func collectionView(_ collectionView: UICollectionView, dragSessionDidEnd session: UIDragSession) {
        log("drag didEnd")
        drag?.collectionView?(collectionView, dragSessionDidEnd: session)
    }

    // MARK: Drop (required + intercepted)

    func collectionView(
        _ collectionView: UICollectionView,
        performDropWith coordinator: UICollectionViewDropCoordinator
    ) {
        let destination = coordinator.destinationIndexPath.map { "\($0.section).\($0.item)" } ?? "nil"
        log("drop perform op=\(coordinator.proposal.operation.rawValue)"
            + " intent=\(coordinator.proposal.intent.rawValue) dest=\(destination)")
        drop?.collectionView(collectionView, performDropWith: coordinator)
    }

    /// Reached only when the original implements it (`responds(to:)` mirrors the original).
    func collectionView(
        _ collectionView: UICollectionView,
        dropSessionDidUpdate session: UIDropSession,
        withDestinationIndexPath destinationIndexPath: IndexPath?
    ) -> UICollectionViewDropProposal {
        let proposal = drop?.collectionView?(
            collectionView,
            dropSessionDidUpdate: session,
            withDestinationIndexPath: destinationIndexPath
        ) ?? UICollectionViewDropProposal(operation: .forbidden)
        let destination = destinationIndexPath.map { "\($0.section).\($0.item)" } ?? "nil"
        let update = "op=\(proposal.operation.rawValue) intent=\(proposal.intent.rawValue) dest=\(destination)"
        if update != lastUpdate {
            lastUpdate = update
            log("drop didUpdate \(update)")
        }
        return proposal
    }

    func collectionView(_ collectionView: UICollectionView, dropSessionDidEnter session: UIDropSession) {
        log("drop didEnter")
        drop?.collectionView?(collectionView, dropSessionDidEnter: session)
    }

    func collectionView(_ collectionView: UICollectionView, dropSessionDidExit session: UIDropSession) {
        log("drop didExit")
        drop?.collectionView?(collectionView, dropSessionDidExit: session)
    }

    func collectionView(_ collectionView: UICollectionView, dropSessionDidEnd session: UIDropSession) {
        lastUpdate = ""
        log("drop didEnd")
        drop?.collectionView?(collectionView, dropSessionDidEnd: session)
    }
}
#endif
