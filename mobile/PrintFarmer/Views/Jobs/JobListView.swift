import SwiftUI
import OSLog

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
    @State private var activePrinterStatuses: [UUID: PrinterStatusDetail] = [:]
    @State private var unavailableActivePrinterStatuses: Set<UUID> = []

    private var activePrinterIDs: [UUID] {
        Array(Set(viewModel.activeJobs.compactMap { item in
            item.job.assignedPrinterId.flatMap(UUID.init(uuidString:))
        })).sorted { $0.uuidString < $1.uuidString }
    }

    private var activePrinterStatusRequestKey: String {
        "\(services.activeServerGeneration):\(activePrinterIDs.map(\.uuidString).joined(separator: ","))"
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
            viewModel.activate()
            viewModel.configure(jobService: services.jobService)
            viewModel.configure(jobAnalyticsService: services.jobAnalyticsService)
            viewModel.configureSignalR(services.signalRService)
            viewModel.setQueueWriteAuthorization(canWriteQueue)
            viewModel.startObservingNetworkPath()
            await viewModel.loadJobs()
            queueEditMode = viewModel.keepsQueueEditingActive ? .active : .inactive
        }
        .task(id: activePrinterStatusRequestKey) {
            await refreshActivePrinterStatuses()
        }
        .onChange(of: canWriteQueue) { _, isAuthorized in
            viewModel.setQueueWriteAuthorization(isAuthorized)
        }
        .onChange(of: viewModel.keepsQueueEditingActive, initial: true) { _, isActive in
            queueEditMode = isActive ? .active : .inactive
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
        let printerIDs = activePrinterIDs
        guard !printerIDs.isEmpty else {
            activePrinterStatuses = [:]
            unavailableActivePrinterStatuses = []
            return
        }

        let generation = services.activeServerGeneration
        await services.awaitActiveServerSettled()
        guard !Task.isCancelled, services.isActiveGeneration(generation) else { return }

        activePrinterStatuses = [:]
        unavailableActivePrinterStatuses = []
        while !Task.isCancelled {
            for printerID in printerIDs {
                do {
                    let status = try await services.printerService.getStatus(id: printerID)
                    guard !Task.isCancelled, services.isActiveGeneration(generation) else { return }
                    activePrinterStatuses[printerID] = status
                    unavailableActivePrinterStatuses.remove(printerID)
                } catch {
                    guard !Task.isCancelled, services.isActiveGeneration(generation) else { return }
                    activePrinterStatuses.removeValue(forKey: printerID)
                    unavailableActivePrinterStatuses.insert(printerID)
                }
            }
            do {
                try await Task.sleep(for: .seconds(15))
            } catch {
                return
            }
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
                                viewModel.canMoveQueuedJob(id: id, direction: .up, inGroup: groupID)
                                    || viewModel.canMoveQueuedJob(id: id, direction: .down, inGroup: groupID)
                            }
                        } ?? false
                        queueCard { queuedJobRow(item, groupID: groupID) }
                            .moveDisabled(!canMove)
                    }
                    .onMove { offsets, destination in
                        Task { @MainActor in
                            await viewModel.moveQueuedRows(
                                fromOffsets: offsets,
                                toOffset: destination
                            )
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
                            if viewModel.canReorderQueue && !viewModel.reorderableQueuedJobs.isEmpty {
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
                            if viewModel.canReorderQueue && !viewModel.reorderableQueuedJobs.isEmpty {
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
    }

    @ViewBuilder
    private func activeJobDetails(_ item: QueuedPrintJobResponse) -> some View {
        let printerID = item.job.assignedPrinterId.flatMap(UUID.init(uuidString:))
        let status = printerID.flatMap { activePrinterStatuses[$0] }
        VStack(alignment: .leading, spacing: 4) {
            if let printerName = item.job.printerName {
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
            } else if printerID == nil || printerID.map(unavailableActivePrinterStatuses.contains) == true {
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
                Text(queuedJobMetadata(item))
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
            Text(queuedJobMetadata(item))
                .font(.caption)
                .foregroundStyle(.secondary)
                .lineLimit(1)
        }
    }

    private func queuedJobMetadata(_ item: QueuedPrintJobResponse) -> String {
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
