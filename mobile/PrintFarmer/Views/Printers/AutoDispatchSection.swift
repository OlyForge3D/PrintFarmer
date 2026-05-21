import SwiftUI

struct AutoDispatchSection: View {
    let printerId: UUID
    let isPrinting: Bool
    @Environment(ServiceContainer.self) private var services
    @State private var viewModel = AutoDispatchViewModel()
    @State private var activeTasks: [Task<Void, Never>] = []

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("Auto-Dispatch")
                .font(.headline)

            VStack(alignment: .leading, spacing: 10) {
                if viewModel.isLoading && viewModel.status == nil {
                    HStack {
                        Spacer()
                        ProgressView()
                        Spacer()
                    }
                    .padding(.vertical, 8)
                } else if let error = viewModel.error, viewModel.status == nil {
                    HStack(spacing: 8) {
                        Image(systemName: "exclamationmark.triangle")
                            .foregroundStyle(Color.pfWarning)
                        Text(error)
                            .font(.caption)
                            .foregroundStyle(.secondary)
                    }
                } else {
                    // Enable/disable toggle
                    Toggle(isOn: Binding(
                        get: { viewModel.isEnabled ?? false },
                        set: { _ in
                            let task = Task { await viewModel.toggleEnabled(printerId: printerId) }
                            activeTasks.append(task)
                        }
                    )) {
                        Label("Auto-Dispatch Enabled", systemImage: "arrow.triangle.2.circlepath")
                            .font(.subheadline)
                    }

                    if viewModel.isEnabled == true {
                        // State-specific UI
                        if viewModel.parsedState == .pendingReady {
                            pendingReadyView
                            actionButtons
                        } else if viewModel.parsedState == .ready {
                            readyView
                        } else {
                            idleView
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
        .task {
            viewModel.configure(autoDispatchService: services.autoPrintService)
            await viewModel.loadStatus(printerId: printerId)
        }
        .onDisappear {
            activeTasks.forEach { $0.cancel() }
            activeTasks.removeAll()
            viewModel.isViewActive = false
        }
    }

    // MARK: - State Views

    private var pendingReadyView: some View {
        VStack(alignment: .leading, spacing: 12) {
            // Banner
            HStack(spacing: 10) {
                Image(systemName: "exclamationmark.triangle.fill")
                    .font(.title2)
                    .foregroundStyle(Color.pfWarning)

                VStack(alignment: .leading, spacing: 2) {
                    Text("🔔 Bed Clear Required")
                        .font(.subheadline.weight(.semibold))
                    Text("Remove the completed print and confirm the bed is clear")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                }
            }
            .padding(12)
            .background(Color.pfWarning.opacity(0.1), in: RoundedRectangle(cornerRadius: 10))

            // Queue info
            if let queuedCount = viewModel.status?.queueDepth, queuedCount > 0 {
                HStack(spacing: 6) {
                    Image(systemName: "tray.fill")
                        .foregroundStyle(.secondary)
                    Text("\(queuedCount) job\(queuedCount == 1 ? "" : "s") queued")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                }
            }
        }
    }

    private var readyView: some View {
        VStack(alignment: .leading, spacing: 10) {
            HStack(spacing: 8) {
                Image(systemName: "checkmark.circle.fill")
                    .foregroundStyle(Color.pfSuccess)
                Text("✅ Bed cleared — dispatching next job...")
                    .font(.subheadline)
                    .foregroundStyle(.secondary)
            }

            // Show filament check if available
            if let result = viewModel.readyResult {
                filamentCheckResult(result)

                // Show next job info
                if let nextJob = result.nextJob {
                    HStack(spacing: 6) {
                        Image(systemName: "doc.fill")
                            .foregroundStyle(.secondary)
                        Text("Next: \(nextJob.name)")
                            .font(.caption)
                            .foregroundStyle(.secondary)
                    }
                }
            }
        }
    }

    private var idleView: some View {
        HStack(spacing: 8) {
            Image(systemName: stateIcon)
                .foregroundStyle(stateColor)
            Text(viewModel.currentState ?? "Unknown")
                .font(.subheadline)
                .foregroundStyle(.secondary)
        }
    }

    private var isActionInProgress: Bool {
        viewModel.isMarkingReady || viewModel.isSkipping || isPrinting
    }

    private var hasQueuedJobs: Bool {
        guard let count = viewModel.status?.queueDepth else { return false }
        return count > 0
    }

    private var actionButtons: some View {
        HStack(spacing: 8) {
            Button {
                UIImpactFeedbackGenerator(style: .medium).impactOccurred()
                viewModel.isMarkingReady = true
                let task = Task { await viewModel.markReady(printerId: printerId) }
                activeTasks.append(task)
            } label: {
                HStack(spacing: 6) {
                    if viewModel.isMarkingReady {
                        ProgressView()
                            .controlSize(.small)
                            .tint(.white)
                    } else {
                        Image(systemName: "play.fill")
                    }
                    Text(viewModel.parsedState == .pendingReady ? "Confirm Bed Clear" : "Next Job")
                }
                .font(.subheadline.weight(.medium))
                .fullWidthActionButton()
            }
            .buttonStyle(.borderedProminent)
            .tint(viewModel.parsedState == .pendingReady ? Color.pfWarning : Color.pfAccent)
            .disabled(isActionInProgress || viewModel.isMarkingReady || (!hasQueuedJobs && viewModel.parsedState != .pendingReady))
            .opacity((isActionInProgress || viewModel.isMarkingReady || (!hasQueuedJobs && viewModel.parsedState != .pendingReady)) ? 0.4 : 1.0)

            Button {
                viewModel.isSkipping = true
                let task = Task { await viewModel.skip(printerId: printerId) }
                activeTasks.append(task)
            } label: {
                HStack(spacing: 6) {
                    if viewModel.isSkipping {
                        ProgressView()
                            .controlSize(.small)
                    } else {
                        Image(systemName: "forward.fill")
                    }
                    Text("Skip")
                }
                .font(.subheadline.weight(.medium))
                .fullWidthActionButton()
            }
            .buttonStyle(.bordered)
            .disabled(isActionInProgress || viewModel.isSkipping || !hasQueuedJobs)
            .opacity((isActionInProgress || viewModel.isSkipping || !hasQueuedJobs) ? 0.4 : 1.0)
        }
    }

    // MARK: - Filament Check

    private func filamentCheckResult(_ result: AutoDispatchReadyResult) -> some View {
        HStack(spacing: 8) {
            let filamentOk = result.filamentCheck?.sufficient ?? false

            Image(systemName: filamentOk ? "checkmark.circle.fill" : "exclamationmark.circle.fill")
                .foregroundStyle(filamentOk ? Color.pfSuccess : Color.pfWarning)

            VStack(alignment: .leading, spacing: 1) {
                Text(filamentOk ? "Filament check passed" : "Filament check failed")
                    .font(.caption.weight(.medium))

                if let message = result.filamentCheck?.message {
                    Text(message)
                        .font(.caption2)
                        .foregroundStyle(.secondary)
                }
            }
        }
        .padding(8)
        .background(
            ((result.filamentCheck?.sufficient ?? false) ? Color.pfSuccess : Color.pfWarning).opacity(0.1),
            in: RoundedRectangle(cornerRadius: 8)
        )
    }

    // MARK: - Helpers

    private var stateIcon: String {
        guard let state = viewModel.parsedState else { return "questionmark.circle" }
        switch state {
        case .none: return "moon"
        case .pendingReady: return "exclamationmark.triangle.fill"
        case .ready: return "checkmark.circle.fill"
        case .dismissed: return "xmark.circle"
        }
    }

    private var stateColor: Color {
        guard let state = viewModel.parsedState else { return .pfTextSecondary }
        switch state {
        case .none: return .pfTextTertiary
        case .pendingReady: return .pfWarning
        case .ready: return .pfSuccess
        case .dismissed: return .pfTextSecondary
        }
    }
}
