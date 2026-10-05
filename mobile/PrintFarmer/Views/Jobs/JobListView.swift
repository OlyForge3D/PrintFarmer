import SwiftUI

struct JobListView: View {
    @Environment(AuthViewModel.self) private var authViewModel
    @Environment(AppRouter.self) private var router
    @Environment(ServiceContainer.self) private var services
    @Environment(\.dynamicTypeSize) private var dynamicTypeSize
    private let ownsNavigationStack: Bool
    @State private var viewModel = JobListViewModel()
    @State private var retryTask: Task<Void, Never>?
    @State private var showsJobHistory = false
    @State private var historyNavigationPath: [AppDestination] = []

    init(ownsNavigationStack: Bool = true) {
        self.ownsNavigationStack = ownsNavigationStack
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
        }
        .onChange(of: canWriteQueue) { _, isAuthorized in
            viewModel.setQueueWriteAuthorization(isAuthorized)
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
        .rootNavigationChrome(for: .queue) {
            Button {
                historyNavigationPath = []
                showsJobHistory = true
            } label: {
                Image(systemName: "clock.arrow.circlepath")
                    .frame(
                        minWidth: RootNavigationChrome.minimumTouchTarget,
                        minHeight: RootNavigationChrome.minimumTouchTarget
                    )
            }
            .accessibilityLabel("Job history")
            .accessibilityHint("Opens completed and cancelled jobs, including harvest actions.")
            .accessibilityIdentifier("jobList.history.open")
        }
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
                        activeJobRow(item)
                    }
                }
            } header: {
                sectionHeader("Printing", count: viewModel.activeJobs.count, systemImage: "printer.fill")
                    .accessibilityIdentifier("jobList.section.printing")
            }

            Section {
                if !viewModel.assignedJobs.isEmpty {
                    HStack(spacing: 6) {
                        Image(systemName: "checkmark.circle")
                            .accessibilityHidden(true)
                        Text("Assigned")
                        Spacer()
                        Text("\(viewModel.assignedJobs.count)")
                            .monospacedDigit()
                    }
                    .font(.caption.weight(.semibold))
                    .foregroundStyle(Color.pfTextSecondary)
                    .accessibilityElement(children: .combine)
                    .accessibilityIdentifier("jobList.assigned.subheading")

                    ForEach(viewModel.assignedJobs) { item in
                        queuedJobRow(item)
                    }
                }

                if viewModel.reorderableQueuedJobs.isEmpty {
                    if viewModel.assignedJobs.isEmpty {
                        Text("No jobs waiting to print.")
                            .foregroundStyle(Color.pfTextSecondary)
                    }
                } else {
                    ForEach(viewModel.reorderableQueuedJobs) { item in
                        let groupID = viewModel.reorderGroupID(for: item)
                        let canMove = item.job.jobUUID.map { id in
                            viewModel.canMoveQueuedJob(id: id, direction: .up, inGroup: groupID)
                                || viewModel.canMoveQueuedJob(id: id, direction: .down, inGroup: groupID)
                        } ?? false
                        queuedJobRow(item, groupID: groupID)
                            .moveDisabled(!canMove)
                    }
                    .onMove { offsets, destination in
                        Task { @MainActor in
                            await viewModel.moveQueuedJobs(
                                fromOffsets: offsets,
                                toOffset: destination
                            )
                        }
                    }
                }
            } header: {
                HStack {
                    Label("Queued", systemImage: "tray.full")
                        .font(.subheadline.weight(.semibold))
                    Spacer()
                    if viewModel.canReorderQueue && !viewModel.reorderableQueuedJobs.isEmpty {
                        Text("Drag to reorder")
                            .font(.caption)
                            .foregroundStyle(Color.pfTextSecondary)
                    }
                    Text("\(viewModel.queuedJobs.count)")
                        .font(.caption.monospacedDigit())
                        .monospacedDigit()
                        .foregroundStyle(Color.pfTextTertiary)
                }
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
                    sectionHeader("Recent failures", count: 0, systemImage: "exclamationmark.triangle")
                        .accessibilityIdentifier("jobList.section.recent-failures")
                }
            } else if !viewModel.recentFailures.isEmpty {
                Section {
                    ForEach(viewModel.recentFailures) { item in
                        recentFailureRow(item)
                    }
                } header: {
                    sectionHeader(
                        "Recent failures",
                        count: viewModel.recentFailures.count,
                        systemImage: "exclamationmark.triangle"
                    )
                    .accessibilityIdentifier("jobList.section.recent-failures")
                }
            } else {
                Section {
                    Text("No recent failures.")
                        .foregroundStyle(Color.pfTextSecondary)
                } header: {
                    sectionHeader("Recent failures", count: 0, systemImage: "exclamationmark.triangle")
                        .accessibilityIdentifier("jobList.section.recent-failures")
                }
            }
        }
        .listStyle(.plain)
        .contentMargins(.bottom, 200, for: .scrollContent)
        .environment(\.editMode, .constant(viewModel.keepsQueueEditingActive ? .active : .inactive))
        .accessibilityIdentifier("jobList.combined.list")
    }

    private func sectionHeader(_ title: String, count: Int, systemImage: String) -> some View {
        HStack {
            Label(title, systemImage: systemImage)
                .font(.subheadline.weight(.semibold))
            Spacer()
            Text("\(count)")
                .font(.caption.monospacedDigit())
                .monospacedDigit()
                .foregroundStyle(Color.pfTextTertiary)
        }
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
                        Text(item.job.name)
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
                                Text(item.job.name)
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
        VStack(alignment: .leading, spacing: 4) {
            if let printerName = item.job.printerName {
                Label(printerName, systemImage: "printer")
                    .font(.caption2)
                    .foregroundStyle(.secondary)
                    .lineLimit(dynamicTypeSize.isAccessibilitySize ? nil : 1)
                    .fixedSize(horizontal: false, vertical: true)
            }

            if let startTime = item.job.actualStartTimeUtc,
               let estSeconds = item.job.estimatedPrintTimeSeconds, estSeconds > 0 {
                let elapsed = Date.now.timeIntervalSince(startTime)
                let total = TimeInterval(estSeconds)
                let progress = min(1.0, elapsed / total)
                PrintProgressBar(progress: progress, height: 4, color: progressColor(for: item.job.jobStatus))

                if dynamicTypeSize.isAccessibilitySize {
                    VStack(alignment: .leading, spacing: 4) {
                        if item.job.isMultiCopy {
                            Label("\(item.job.completedCopies)/\(item.job.copies)", systemImage: "doc.on.doc")
                                .font(.caption2)
                                .foregroundStyle(.secondary)
                        }
                        let remaining = max(0, total - elapsed)
                        Label("~\(remaining.durationFormatted) left", systemImage: "clock")
                            .font(.caption2)
                            .foregroundStyle(.secondary)
                    }
                } else {
                    HStack {
                        if item.job.isMultiCopy {
                            Label("\(item.job.completedCopies)/\(item.job.copies)", systemImage: "doc.on.doc")
                                .font(.caption2)
                                .foregroundStyle(.secondary)
                        }
                        Spacer()
                        let remaining = max(0, total - elapsed)
                        Label("~\(remaining.durationFormatted) left", systemImage: "clock")
                            .font(.caption2)
                            .foregroundStyle(.secondary)
                    }
                }
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
                            Text(item.job.name)
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
                                    Text(item.job.name)
                                        .font(.subheadline.weight(.semibold))
                                        .lineLimit(1)
                                    Spacer()
                                    priorityIndicator(item.job.priority)
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
                priorityIndicator(item.job.priority)
                if let printerName = item.job.printerName {
                    Label(printerName, systemImage: "printer")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                        .fixedSize(horizontal: false, vertical: true)
                }
                if item.job.isMultiCopy {
                    Label("\(item.job.copies) copies", systemImage: "doc.on.doc")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                }
                if let duration = item.job.estimatedDuration {
                    Label(duration.durationFormatted, systemImage: "clock")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                }
            }
        } else {
            HStack(spacing: 12) {
                if let printerName = item.job.printerName {
                    Label(printerName, systemImage: "printer")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                        .lineLimit(1)
                }
                Spacer()
                if item.job.isMultiCopy {
                    Label("\(item.job.copies) copies", systemImage: "doc.on.doc")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                }
                if let duration = item.job.estimatedDuration {
                    Label(duration.durationFormatted, systemImage: "clock")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                }
            }
        }
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
        recentFailureNavigationLink(item, id: UUID(uuidString: item.id))
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
                            Text(completedAt.relativeFormatted)
                                .font(.caption2)
                                .dynamicTypeSize(.xxxLarge)
                                .foregroundStyle(Color.pfTextTertiary)
                        }
                        if let failureReason = item.failureReason {
                            Text(failureReason)
                                .font(.caption)
                                .dynamicTypeSize(.xxxLarge)
                                .foregroundStyle(Color.pfError)
                                .lineLimit(2)
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
                                Text(completedAt.relativeFormatted)
                                    .font(.caption2)
                                    .foregroundStyle(Color.pfTextTertiary)
                                    .lineLimit(1)
                            }
                        }
                        if let failureReason = item.failureReason {
                            Text(failureReason)
                                .font(.caption)
                                .foregroundStyle(Color.pfError)
                                .lineLimit(1)
                        }
                    }
                }
            }
        }
        .padding(.vertical, 2)
    }

    private var recentFailureIcon: some View {
        Image(systemName: "exclamationmark.triangle.fill")
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

    @ViewBuilder
    private func priorityIndicator(_ priority: PrintJobPriority) -> some View {
        if priority == .high || priority == .urgent {
            HStack(spacing: 2) {
                Image(systemName: priority == .urgent ? "exclamationmark.triangle.fill" : "flag.fill")
                    .font(.caption2)
                Text(priority == .urgent ? "Urgent" : "High")
                    .font(.caption2.weight(.semibold))
            }
            .fixedSize(horizontal: true, vertical: false)
            .foregroundStyle(priority == .urgent ? Color.pfError : Color.pfWarning)
        }
    }

    // MARK: - Thumbnails

    @ViewBuilder
    private func jobThumbnail(for item: QueuedPrintJobResponse, size: CGFloat = 44) -> some View {
        let urlString = item.job.thumbnailUrl ?? item.gcodeFile?.thumbnailUrl
        if let urlString,
           let baseURL = APIClient.savedBaseURL(),
           let url = URL(string: urlString, relativeTo: baseURL) {
            AsyncImage(url: url) { phase in
                switch phase {
                case .success(let image):
                    image
                        .resizable()
                        .aspectRatio(contentMode: .fill)
                        .frame(width: size, height: size)
                        .clipShape(RoundedRectangle(cornerRadius: 8))
                default:
                    placeholderThumbnail(size: size)
                }
            }
        } else {
            placeholderThumbnail(size: size)
        }
    }

    private func placeholderThumbnail(size: CGFloat = 44) -> some View {
        RoundedRectangle(cornerRadius: 8)
            .fill(Color.pfCard)
            .frame(width: size, height: size)
            .overlay(
                Image(systemName: "cube")
                    .font(.system(size: size * 0.4))
                    .foregroundStyle(.tertiary)
            )
    }
}
