import SwiftUI

struct ShiftTasksView: View {
    @Environment(ServiceContainer.self) private var services
    @State private var viewModel = ShiftTasksViewModel()

    var body: some View {
        NavigationStack {
            content
                .navigationTitle("Tasks")
                .toolbar {
                    ToolbarItem(placement: .topBarTrailing) {
                        NavigationLink {
                            JobListView()
                        } label: {
                            Label("Print queue", systemImage: "tray.full")
                        }
                        .accessibilityLabel("Print queue")
                        .accessibilityHint("Opens the existing print job queue.")
                        .accessibilityIdentifier("shiftTasks.printQueue")
                    }
                }
                .refreshable {
                    await reloadCapabilitiesAndTasks()
                }
        }
        .task(id: services.activeServerGeneration) {
            await reloadCapabilitiesAndTasks()
        }
        .onDisappear {
            viewModel.deactivate()
        }
    }

    @ViewBuilder
    private var content: some View {
        if let snapshot = viewModel.snapshot {
            snapshotList(snapshot)
        } else {
            switch viewModel.phase {
            case .idle, .loading:
                ProgressView("Loading shift tasks...")
                    .frame(maxWidth: .infinity, maxHeight: .infinity)
                    .accessibilityIdentifier("shiftTasks.loading")
            case .failed:
                loadError
            case .content, .disabled:
                snapshotList(.flat([], mode: .featureDisabled))
            }
        }
    }

    private func snapshotList(_ snapshot: ShiftTaskSnapshot) -> some View {
        List {
            switch snapshot.mode {
            case .grouped:
                EmptyView()
            case .legacyFallback:
                statusBanner(
                    title: "Legacy task list",
                    message: "This server does not provide a grouped shift plan. Tasks remain in server order.",
                    systemImage: "list.bullet.rectangle"
                )
            case .featureDisabled:
                statusBanner(
                    title: "Shift plan disabled",
                    message: "The grouped shift plan is disabled on this server. Legacy tasks and the print queue remain available.",
                    systemImage: "checklist.unchecked"
                )
            }

            if let failure = viewModel.loadFailure {
                Section {
                    VStack(alignment: .leading, spacing: 10) {
                        Label("Refresh failed", systemImage: "exclamationmark.triangle.fill")
                            .font(.headline)
                            .foregroundStyle(.red)
                        Text(failure.message)
                            .font(.subheadline)
                            .foregroundStyle(.secondary)
                        Button("Retry") {
                            Task {
                                _ = await viewModel.retryLoad(failureID: failure.id)
                            }
                        }
                        .frame(minHeight: 44)
                        .accessibilityLabel("Retry task refresh")
                        .accessibilityHint("Reloads the canonical task list from the current server.")
                        .accessibilityIdentifier("shiftTasks.refresh.retry")
                    }
                    .padding(.vertical, 4)
                    .accessibilityIdentifier("shiftTasks.refresh.error")
                }
            }

            if snapshot.groups.isEmpty
                && snapshot.compatibilityErrorMessage == nil {
                Section {
                    ContentUnavailableView {
                        Label(
                            snapshot.mode == .featureDisabled
                                ? "No legacy tasks"
                                : "No shift tasks",
                            systemImage: "checkmark.circle"
                        )
                    } description: {
                        Text(
                            snapshot.mode == .featureDisabled
                                ? "The grouped plan is disabled. Open Print queue for existing job work."
                                : "There are no pending shift tasks."
                        )
                    }
                    .accessibilityIdentifier("shiftTasks.empty")
                }
            } else {
                ForEach(snapshot.groups) { group in
                    Section {
                        ForEach(group.tasks) { task in
                            ShiftTaskRow(task: task, viewModel: viewModel)
                        }
                    } header: {
                        Text(group.anchorKind.groupTitle)
                            .accessibilityIdentifier(
                                "shiftTasks.group.\(group.anchorKind.wireValue)"
                            )
                    }
                }
            }
        }
        .listStyle(.insetGrouped)
        .accessibilityIdentifier("shiftTasks.list")
    }

    private func statusBanner(
        title: String,
        message: String,
        systemImage: String
    ) -> some View {
        Section {
            Label {
                VStack(alignment: .leading, spacing: 4) {
                    Text(title)
                        .font(.headline)
                    Text(message)
                        .font(.subheadline)
                        .foregroundStyle(.secondary)
                }
            } icon: {
                Image(systemName: systemImage)
                    .foregroundStyle(.secondary)
            }
            .accessibilityElement(children: .combine)
            .accessibilityIdentifier("shiftTasks.mode.banner")
        }
    }

    private var loadError: some View {
        // The initial-load `.failed` terminal state must offer functional
        // pull-to-refresh. `.refreshable` only drives a genuine scroll
        // container, so the error content is hosted in a `ScrollView` whose
        // content fills the viewport and always bounces; a bare
        // `ContentUnavailableView` leaves pull-to-refresh inert. Only the
        // error is rendered here (snapshot stays nil until the canonical
        // reload resolves), so recovery cannot flash a false all-clear. The
        // explicit Retry control and accessibility semantics are preserved.
        ScrollView(.vertical) {
            ContentUnavailableView {
                Label("Unable to load tasks", systemImage: "exclamationmark.triangle")
            } description: {
                Text(viewModel.loadFailure?.message ?? "The current server did not return a task list.")
            } actions: {
                if let failure = viewModel.loadFailure {
                    Button("Retry") {
                        Task {
                            _ = await viewModel.retryLoad(failureID: failure.id)
                        }
                    }
                    .frame(minHeight: 44)
                    .accessibilityLabel("Retry task refresh")
                    .accessibilityHint("Reloads the canonical task list from the current server.")
                    .accessibilityIdentifier("shiftTasks.load.retry")
                }
            }
            .frame(maxWidth: .infinity)
            .containerRelativeFrame(.vertical)
            .accessibilityIdentifier("shiftTasks.load.error")
        }
        .scrollBounceBehavior(.always)
        .refreshable {
            // The canonical reload is attached directly to this scroll
            // container so pull-to-refresh is functional from the `.failed`
            // terminal state; `.always` bounce keeps the single error view
            // pullable so the gesture can engage the refresh control.
            await reloadCapabilitiesAndTasks()
        }
        .accessibilityIdentifier("shiftTasks.load.errorList")
    }

    private func reloadCapabilitiesAndTasks() async {
        await services.capabilitiesService.refresh()
        guard !Task.isCancelled else { return }

        viewModel.configure(
            taskService: services.shiftTaskService,
            signalRService: services.signalRService,
            shiftPlanEnabled: services.capabilitiesService.resolved.shiftPlanEnabled,
            offlineQueue: services.offlineWriteQueue
        )
        _ = await viewModel.refresh()
    }
}

private struct ShiftTaskRow: View {
    let task: ShiftTask
    let viewModel: ShiftTasksViewModel

    private var presentation: ShiftTaskRowPresentation {
        ShiftTaskRowPresentation(task: task)
    }

    private var activity: ShiftTaskMutationActivity? {
        viewModel.mutationActivity(for: task.id)
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            taskSummary

            if let failure = activity?.failure {
                mutationError(failure, isRetrying: activity?.isInFlight == true)
            } else if let activity, activity.isInFlight {
                HStack(spacing: 10) {
                    ProgressView()
                    Text("\(activity.intent.operation.displayName) in progress")
                        .font(.subheadline)
                }
                .frame(minHeight: 44)
                .accessibilityIdentifier("shiftTasks.mutation.progress.\(task.id)")
            } else if task.taskType.supportsKnownLifecycleActions {
                lifecycleActions
            } else {
                Text("No actions are available for this task type.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .accessibilityIdentifier("shiftTasks.unknown.\(task.id)")
            }
        }
        .padding(.vertical, 6)
    }

    private var taskSummary: some View {
        VStack(alignment: .leading, spacing: 7) {
            HStack(alignment: .firstTextBaseline, spacing: 8) {
                Text(task.title)
                    .font(.headline)
                    .foregroundStyle(task.priority == .high ? .red : .primary)
                Spacer(minLength: 8)
                Text(presentation.priorityText)
                    .font(.caption.weight(.semibold))
                    .foregroundStyle(task.priority == .high ? .red : .secondary)
            }

            if let description = task.description, !description.isEmpty {
                Text(description)
                    .font(.subheadline)
                    .foregroundStyle(.secondary)
            }

            Label(presentation.timeText, systemImage: "clock")
                .font(.caption)
                .foregroundStyle(.secondary)

            Text("\(presentation.typeText) - \(presentation.sourceText)")
                .font(.caption)
                .foregroundStyle(.secondary)
        }
        .accessibilityElement(children: .ignore)
        .accessibilityLabel(presentation.accessibilityLabel)
        .accessibilityIdentifier("shiftTasks.row.info.\(task.id)")
    }

    private var lifecycleActions: some View {
        ViewThatFits(in: .horizontal) {
            HStack(spacing: 8) {
                operationButton(.complete)
                operationButton(.skip)
                operationButton(.dismiss)
            }
            VStack(spacing: 8) {
                operationButton(.complete)
                operationButton(.skip)
                operationButton(.dismiss)
            }
        }
    }

    private func operationButton(_ operation: ShiftTaskMutationOperation) -> some View {
        Button {
            Task {
                await viewModel.perform(operation, taskID: task.id)
            }
        } label: {
            Text(operation.displayName)
                .frame(maxWidth: .infinity, minHeight: 44)
        }
        .buttonStyle(.bordered)
        .frame(maxWidth: .infinity)
        .accessibilityLabel(operation.displayName)
        .accessibilityHint("\(operation.displayName) for \(task.title).")
        .accessibilityIdentifier("shiftTasks.action.\(operation.rawValue).\(task.id)")
    }

    private func mutationError(
        _ failure: ShiftTaskMutationFailure,
        isRetrying: Bool
    ) -> some View {
        let accessibility = ShiftTaskMutationErrorAccessibility(
            task: task,
            operation: failure.intent.operation
        )
        return VStack(alignment: .leading, spacing: 10) {
            Label {
                Text("\(failure.intent.operation.displayName) failed")
                    .font(.subheadline.weight(.semibold))
            } icon: {
                Image(systemName: "exclamationmark.circle.fill")
            }
            .foregroundStyle(.red)
            .accessibilityIdentifier("shiftTasks.mutation.error.\(task.id)")

            Text(failure.message)
                .font(.caption)
                .foregroundStyle(.red)

            HStack(spacing: 8) {
                Button {
                    Task {
                        _ = await viewModel.retryMutation(failureID: failure.id)
                    }
                } label: {
                    Text("Retry")
                        .frame(maxWidth: .infinity, minHeight: 44)
                }
                .buttonStyle(.borderedProminent)
                .tint(.red)
                .frame(maxWidth: .infinity)
                .disabled(isRetrying)
                .accessibilityLabel(accessibility.retryLabel)
                .accessibilityHint(accessibility.retryHint)
                .accessibilityIdentifier("shiftTasks.mutation.retry.\(task.id)")

                Button {
                    _ = viewModel.dismissMutationError(failureID: failure.id)
                } label: {
                    Text("Dismiss")
                        .frame(maxWidth: .infinity, minHeight: 44)
                }
                .buttonStyle(.bordered)
                .frame(maxWidth: .infinity)
                .disabled(isRetrying)
                .accessibilityLabel(accessibility.dismissLabel)
                .accessibilityHint(accessibility.dismissHint)
                .accessibilityIdentifier("shiftTasks.mutation.dismiss.\(task.id)")
            }
        }
        .padding(12)
        .background(Color.red.opacity(0.12), in: RoundedRectangle(cornerRadius: 10))
        .overlay(
            RoundedRectangle(cornerRadius: 10)
                .strokeBorder(Color.red.opacity(0.45), lineWidth: 1)
        )
    }
}
