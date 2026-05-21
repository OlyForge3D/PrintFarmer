import SwiftUI

struct JobDetailView: View {
    @Environment(ServiceContainer.self) private var services
    @State private var viewModel: JobDetailViewModel
    @State private var activeTasks: [Task<Void, Never>] = []

    init(jobId: UUID) {
        _viewModel = State(initialValue: JobDetailViewModel(jobId: jobId))
    }

    var body: some View {
        Group {
            if let job = viewModel.job {
                jobContent(job)
            } else if let error = viewModel.errorMessage {
                ContentUnavailableView {
                    Label("Error", systemImage: "exclamationmark.triangle")
                } description: {
                    Text(error)
                } actions: {
                    Button("Retry") {
                        let task = Task { await viewModel.loadJob() }
                        activeTasks.append(task)
                    }
                }
            } else {
                ProgressView("Loading job…")
                    .frame(maxWidth: .infinity, maxHeight: .infinity)
            }
        }
        .navigationTitle(viewModel.job?.name ?? "Job")
        #if os(iOS)
        .navigationBarTitleDisplayMode(.large)
        #endif
        .refreshable {
            await viewModel.loadJob()
        }
        .alert("Cancel Job?", isPresented: $viewModel.showCancelConfirmation) {
            Button("Keep", role: .cancel) {}
            Button("Cancel Job", role: .destructive) {
                UIImpactFeedbackGenerator(style: .heavy).impactOccurred()
                let task = Task { await viewModel.cancelJob() }
                activeTasks.append(task)
            }
        } message: {
            Text("This will cancel the print job. This action cannot be undone.")
        }
        .alert("Action Failed", isPresented: .constant(viewModel.actionError != nil)) {
            Button("OK") { viewModel.actionError = nil }
        } message: {
            if let error = viewModel.actionError {
                Text(error)
            }
        }
        .task {
            viewModel.configure(jobService: services.jobService)
            await viewModel.loadJob()
        }
        .onDisappear {
            activeTasks.forEach { $0.cancel() }
            activeTasks.removeAll()
            viewModel.isViewActive = false
        }
    }

    // MARK: - Content

    private func jobContent(_ job: PrintJob) -> some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 20) {
                // Thumbnail
                thumbnailSection(job)

                // Status + Progress
                statusSection(job)

                // Info
                infoSection(job)

                // Timestamps
                timestampsSection(job)

                // Actions
                actionSection(job)
            }
            .padding()
        }
    }

    // MARK: - Thumbnail

    private func thumbnailSection(_ job: PrintJob) -> some View {
        Group {
            if let gcodeFileId = job.gcodeFileId,
               let baseURL = APIClient.savedBaseURL(),
               let url = URL(string: "/api/gcode-files/thumbnail/\(gcodeFileId)", relativeTo: baseURL) {
                AsyncImage(url: url) { phase in
                    switch phase {
                    case .success(let image):
                        image
                            .resizable()
                            .aspectRatio(contentMode: .fit)
                            .frame(maxHeight: 200)
                            .clipShape(RoundedRectangle(cornerRadius: 12))
                            .frame(maxWidth: .infinity)
                    case .failure:
                        EmptyView()
                    case .empty:
                        ProgressView()
                            .frame(height: 120)
                            .frame(maxWidth: .infinity)
                    @unknown default:
                        EmptyView()
                    }
                }
            }
        }
    }

    // MARK: - Status

    private func statusSection(_ job: PrintJob) -> some View {
        VStack(alignment: .leading, spacing: 12) {
            HStack {
                StatusBadge(jobStatus: job.status)
                Spacer()
                if job.isMultiCopy {
                    Label("\(job.completedCopies) / \(job.copies) copies", systemImage: "doc.on.doc")
                        .font(.subheadline)
                        .foregroundStyle(.secondary)
                }
            }

            if viewModel.isActive {
                VStack(alignment: .leading, spacing: 8) {
                    Text("Progress")
                        .font(.headline)

                    if let eta = job.estimatedPrintTime?.timeSpanSeconds, let started = job.actualStartTime {
                        let elapsed = Date.now.timeIntervalSince(started)
                        let remaining = max(0, eta - elapsed)
                        let progress = eta > 0 ? min(1.0, elapsed / eta) : 0
                        PrintProgressBar(progress: progress, height: 12, color: progressColor(for: job))

                        if remaining > 0 {
                            HStack {
                                Spacer()
                                Label("~\(remaining.durationFormatted) remaining", systemImage: "clock")
                                    .font(.caption)
                                    .foregroundStyle(.secondary)
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
    }

    // MARK: - Info

    private func infoSection(_ job: PrintJob) -> some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("Details")
                .font(.headline)

            VStack(spacing: 0) {
                infoRow(label: "File", value: job.gcodeFileName, icon: "doc")

                if let printerName = job.assignedPrinterName {
                    Divider()
                    if let printerId = job.assignedPrinterId {
                        NavigationLink(value: AppDestination.printerDetail(id: printerId)) {
                            infoRow(label: "Printer", value: printerName, icon: "printer")
                                .foregroundStyle(.primary)
                        }
                    } else {
                        infoRow(label: "Printer", value: printerName, icon: "printer")
                    }
                }

                if let priority = PrintJobPriority.from(intValue: job.priority) {
                    Divider()
                    infoRow(label: "Priority", value: priorityLabel(priority), icon: "flag")
                }

                if let filament = job.filamentName {
                    Divider()
                    infoRow(label: "Filament", value: filament, icon: "circle.fill")
                }

                if let eta = job.estimatedPrintTime {
                    Divider()
                    infoRow(label: "Est. Time", value: eta.timeSpanFormatted, icon: "clock")
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

    // MARK: - Timestamps

    private func timestampsSection(_ job: PrintJob) -> some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("Timeline")
                .font(.headline)

            VStack(spacing: 0) {
                infoRow(label: "Created", value: (job.createdAt ?? Date()).formatted(date: .abbreviated, time: .shortened), icon: "calendar")

                if let started = job.actualStartTime {
                    Divider()
                    infoRow(label: "Started", value: started.formatted(date: .abbreviated, time: .shortened), icon: "play.circle")
                }

                if let completed = job.actualEndTime {
                    Divider()
                    infoRow(label: "Completed", value: completed.formatted(date: .abbreviated, time: .shortened), icon: "checkmark.circle")
                }

                if let actual = job.actualPrintTime {
                    Divider()
                    infoRow(label: "Print Time", value: actual.timeSpanFormatted, icon: "timer")
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

    // MARK: - Actions

    private func actionSection(_ job: PrintJob) -> some View {
        VStack(spacing: 10) {
            if viewModel.canDispatch {
                Button {
                    let task = Task { await viewModel.dispatchJob() }
                    activeTasks.append(task)
                } label: {
                    Label("Start Print", systemImage: "play.circle.fill")
                        .fullWidthActionButton(prominence: .prominent)
                        .fontWeight(.semibold)
                }
                .buttonStyle(.borderedProminent)
            }

            // Printing: Pause + Abort side-by-side
            if viewModel.canPause && viewModel.canAbort {
                HStack(spacing: 10) {
                    Button {
                        let task = Task { await viewModel.pauseJob() }
                        activeTasks.append(task)
                    } label: {
                        Label("Pause", systemImage: "pause.circle.fill")
                            .frame(maxWidth: .infinity, minHeight: 44)
                            .fontWeight(.semibold)
                    }
                    .buttonStyle(.borderedProminent)
                    .tint(Color.pfWarning)

                    Button(role: .destructive) {
                        let task = Task { await viewModel.abortJob() }
                        activeTasks.append(task)
                    } label: {
                        Label("Abort", systemImage: "stop.circle.fill")
                            .frame(maxWidth: .infinity, minHeight: 44)
                    }
                    .buttonStyle(.borderedProminent)
                    .tint(Color.pfError)
                }
            } else {
                if viewModel.canPause {
                    Button {
                        let task = Task { await viewModel.pauseJob() }
                        activeTasks.append(task)
                    } label: {
                        Label("Pause", systemImage: "pause.circle.fill")
                            .fullWidthActionButton()
                            .fontWeight(.semibold)
                    }
                    .buttonStyle(.borderedProminent)
                    .tint(Color.pfWarning)
                }
            }

            // Paused: Resume + Abort side-by-side
            if viewModel.canResume && viewModel.canAbort {
                HStack(spacing: 10) {
                    Button {
                        let task = Task { await viewModel.resumeJob() }
                        activeTasks.append(task)
                    } label: {
                        Label("Resume", systemImage: "play.circle.fill")
                            .frame(maxWidth: .infinity, minHeight: 44)
                            .fontWeight(.semibold)
                    }
                    .buttonStyle(.borderedProminent)

                    Button(role: .destructive) {
                        let task = Task { await viewModel.abortJob() }
                        activeTasks.append(task)
                    } label: {
                        Label("Abort", systemImage: "stop.circle.fill")
                            .frame(maxWidth: .infinity, minHeight: 44)
                    }
                    .buttonStyle(.borderedProminent)
                    .tint(Color.pfError)
                }
            } else {
                if viewModel.canResume {
                    Button {
                        let task = Task { await viewModel.resumeJob() }
                        activeTasks.append(task)
                    } label: {
                        Label("Resume", systemImage: "play.circle.fill")
                            .fullWidthActionButton(prominence: .prominent)
                            .fontWeight(.semibold)
                    }
                    .buttonStyle(.borderedProminent)
                }

                if viewModel.canAbort {
                    Button(role: .destructive) {
                        let task = Task { await viewModel.abortJob() }
                        activeTasks.append(task)
                    } label: {
                        Label("Abort", systemImage: "stop.circle.fill")
                            .fullWidthActionButton()
                    }
                    .buttonStyle(.borderedProminent)
                    .tint(Color.pfError)
                }
            }

            if viewModel.canCancel {
                Button(role: .destructive) {
                    viewModel.showCancelConfirmation = true
                } label: {
                    Label("Cancel", systemImage: "xmark.circle")
                        .fullWidthActionButton()
                }
                .buttonStyle(.bordered)
            }
        }
        .disabled(viewModel.isPerformingAction)
    }

    // MARK: - Helpers

    private func infoRow(label: String, value: String, icon: String) -> some View {
        HStack {
            Label(label, systemImage: icon)
                .font(.subheadline)
                .foregroundStyle(.secondary)
                .frame(width: 120, alignment: .leading)

            Spacer()

            Text(value)
                .font(.subheadline)
                .lineLimit(1)
        }
        .padding(.vertical, 6)
    }

    private func progressColor(for job: PrintJob) -> Color {
        switch job.status {
        case .printing: .pfAccent
        case .paused: .pfWarning
        case .failed: .pfError
        default: .pfAccent
        }
    }

    private func priorityLabel(_ priority: PrintJobPriority) -> String {
        switch priority {
        case .low: "Low"
        case .normal: "Normal"
        case .high: "High"
        case .urgent: "Urgent"
        }
    }
}
