import SwiftUI

struct JobListView: View {
    @Environment(AppRouter.self) private var router
    @Environment(ServiceContainer.self) private var services
    @Environment(\.horizontalSizeClass) private var sizeClass
    @State private var viewModel = JobListViewModel()
    @State private var currentPage = 0
    @State private var retryTask: Task<Void, Never>?

    var body: some View {
        @Bindable var router = router

        NavigationStack(path: $router.jobsPath) {
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
                    // iPhone: swipeable pages
                    if sizeClass == .compact {
                        VStack(spacing: 0) {
                            TabView(selection: $currentPage) {
                                QueuePage()
                                    .tag(0)
                                PrintingPage()
                                    .tag(1)
                                RecentPage()
                                    .tag(2)
                            }
                            .tabViewStyle(.page(indexDisplayMode: .never))
                            
                            PageIndicator(currentPage: $currentPage, pageCount: 3, labels: ["Queue", "Printing", "Recent"])
                                .padding(.bottom, 8)
                        }
                    } else {
                        // iPad: keep existing List layout
                        jobList
                    }
                }
            }
            .navigationTitle("Jobs")
            .refreshable {
                await viewModel.loadJobs()
            }
            .navigationDestination(for: AppDestination.self) { destination in
                destinationView(for: destination)
            }
        }
        .task {
            viewModel.configure(jobService: services.jobService)
            await viewModel.loadJobs()
        }
        .onDisappear {
        retryTask?.cancel()
        viewModel.isViewActive = false
    }
    }
    
    // MARK: - iPhone Pages
    
    @ViewBuilder
    private func PrintingPage() -> some View {
        Group {
            if viewModel.activeJobs.isEmpty {
                EmptyStateView(
                    icon: "tray",
                    title: "No Active Jobs",
                    message: "No jobs currently printing."
                )
                .padding()
            } else {
                List {
                    ForEach(viewModel.activeJobs) { item in
                        activeJobRow(item)
                    }
                }
                .listStyle(.plain)
                .refreshable {
                    await viewModel.loadJobs()
                }
            }
        }
    }
    
    @ViewBuilder
    private func QueuePage() -> some View {
        Group {
            if viewModel.queuedJobs.isEmpty {
                EmptyStateView(
                    icon: "tray",
                    title: "Queue Empty",
                    message: "No jobs waiting to print."
                )
                .padding()
            } else {
                List {
                    ForEach(viewModel.queuedJobs) { item in
                        queuedJobRow(item)
                    }
                }
                .listStyle(.plain)
                .refreshable {
                    await viewModel.loadJobs()
                }
            }
        }
    }
    
    @ViewBuilder
    private func RecentPage() -> some View {
        Group {
            if viewModel.recentJobs.isEmpty {
                EmptyStateView(
                    icon: "clock",
                    title: "No Recent Jobs",
                    message: "Completed jobs will appear here."
                )
                .padding()
            } else {
                List {
                    ForEach(viewModel.recentJobs.prefix(10)) { item in
                        recentJobRow(item)
                    }
                }
                .listStyle(.plain)
                .refreshable {
                    await viewModel.loadJobs()
                }
            }
        }
    }

    // MARK: - Job List

    private var jobList: some View {
        List {
            if !viewModel.queuedJobs.isEmpty {
                Section {
                    ForEach(viewModel.queuedJobs) { item in
                        queuedJobRow(item)
                    }
                } header: {
                    Label("In Queue", systemImage: "tray.full")
                }
            }

            if !viewModel.activeJobs.isEmpty {
                Section {
                    ForEach(viewModel.activeJobs) { item in
                        activeJobRow(item)
                    }
                } header: {
                    Label("Printing", systemImage: "printer.fill")
                }
            }

            if !viewModel.recentJobs.isEmpty {
                Section(isExpanded: $viewModel.showRecentJobs) {
                    ForEach(viewModel.recentJobs.prefix(10)) { item in
                        recentJobRow(item)
                    }
                } header: {
                    Label("Recent", systemImage: "clock")
                }
            }
        }
        .listStyle(.plain)
    }

    // MARK: - Active Job Row

    private func activeJobRow(_ item: QueuedPrintJobResponse) -> some View {
        Button {
            if let uuid = item.job.jobUUID {
                router.jobsPath.append(AppDestination.jobDetail(id: uuid))
            }
        } label: {
            HStack(spacing: 12) {
                jobThumbnail(for: item, size: 48)
                VStack(alignment: .leading, spacing: 6) {
                HStack {
                    Text(item.job.name)
                        .font(.headline)
                        .lineLimit(1)
                    Spacer()
                    StatusBadge(jobStatus: item.job.jobStatus)
                }

                if let printerName = item.job.printerName {
                    Label(printerName, systemImage: "printer")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                        .lineLimit(1)
                }

                if let startTime = item.job.actualStartTimeUtc,
                   let estSeconds = item.job.estimatedPrintTimeSeconds, estSeconds > 0 {
                    let elapsed = Date.now.timeIntervalSince(startTime)
                    let total = TimeInterval(estSeconds)
                    let progress = min(1.0, elapsed / total)
                    PrintProgressBar(progress: progress, height: 6, color: progressColor(for: item.job.jobStatus))

                    HStack {
                        if item.job.isMultiCopy {
                            Label("\(item.job.completedCopies)/\(item.job.copies)", systemImage: "doc.on.doc")
                                .font(.caption)
                                .foregroundStyle(.secondary)
                        }
                        Spacer()
                        let remaining = max(0, total - elapsed)
                        Label("~\(remaining.durationFormatted) left", systemImage: "clock")
                            .font(.caption)
                            .foregroundStyle(.secondary)
                    }
                }
            }
            }
            .padding(.vertical, 4)
        }
        .buttonStyle(.plain)
    }

    // MARK: - Queued Job Row

    private func queuedJobRow(_ item: QueuedPrintJobResponse) -> some View {
        Button {
            if let uuid = item.job.jobUUID {
                router.jobsPath.append(AppDestination.jobDetail(id: uuid))
            }
        } label: {
            HStack(spacing: 12) {
                jobThumbnail(for: item, size: 44)
                VStack(alignment: .leading, spacing: 6) {
                HStack {
                    Text(item.job.name)
                        .font(.headline)
                        .lineLimit(1)
                    Spacer()
                    priorityIndicator(item.job.priority)
                }

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

                HStack {
                    Text("#\(item.job.queuePosition) in queue")
                        .font(.caption2)
                        .foregroundStyle(.tertiary)
                    Spacer()
                    Text(item.job.createdAtUtc.relativeFormatted)
                        .font(.caption2)
                        .foregroundStyle(.tertiary)
                }
            }
            }
            .padding(.vertical, 4)
        }
        .buttonStyle(.plain)
        .swipeActions(edge: .trailing) {
            if let uuid = item.job.jobUUID {
                Button {
                    UIImpactFeedbackGenerator(style: .medium).impactOccurred()
                    retryTask = Task { await viewModel.cancelJob(id: uuid) }
                } label: {
                    Label("Cancel", systemImage: "xmark.circle")
                }
                .tint(Color.pfError)
            }
        }
        .swipeActions(edge: .leading) {
            if let uuid = item.job.jobUUID {
                Button {
                    UIImpactFeedbackGenerator(style: .medium).impactOccurred()
                    retryTask = Task { await viewModel.dispatchJob(id: uuid) }
                } label: {
                    Label("Start", systemImage: "play.circle.fill")
                }
                .tint(Color.pfAccent)
            }
        }
    }

    // MARK: - Recent Job Row

    private func recentJobRow(_ item: QueuedPrintJobResponse) -> some View {
        Button {
            if let uuid = item.job.jobUUID {
                router.jobsPath.append(AppDestination.jobDetail(id: uuid))
            }
        } label: {
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
            .padding(.vertical, 2)
        }
        .buttonStyle(.plain)
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
    private func priorityIndicator(_ priority: Int) -> some View {
        if let p = PrintJobPriority.from(intValue: priority), p == .high || p == .urgent {
            HStack(spacing: 2) {
                Image(systemName: p == .urgent ? "exclamationmark.triangle.fill" : "flag.fill")
                    .font(.caption2)
                Text(p == .urgent ? "Urgent" : "High")
                    .font(.caption2.weight(.semibold))
            }
            .foregroundStyle(p == .urgent ? Color.pfError : Color.pfWarning)
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
