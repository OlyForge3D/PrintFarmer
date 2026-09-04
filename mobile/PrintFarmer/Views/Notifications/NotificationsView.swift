import SwiftUI

struct NotificationsView: View {
    @Environment(AppRouter.self) private var router
    @Environment(ServiceContainer.self) private var services
    private let ownsNavigationStack: Bool
    @State private var viewModel = NotificationsViewModel()
    @State private var activeTasks: [Task<Void, Never>] = []

    init(ownsNavigationStack: Bool = true) {
        self.ownsNavigationStack = ownsNavigationStack
    }

    var body: some View {
        @Bindable var router = router

        Group {
            if ownsNavigationStack {
                NavigationStack(path: $router.notificationsSheetPath) {
                    screenContent
                }
            } else {
                screenContent
            }
        }
        .task {
            viewModel.isViewActive = true
            viewModel.configure(notificationService: services.notificationService)
            await viewModel.loadNotifications()
        }
        .onChange(of: viewModel.unreadCount) { _, newValue in
            router.notificationBadgeCount = newValue
        }
        .onDisappear {
            viewModel.isViewActive = false
            activeTasks.forEach { $0.cancel() }
        }
    }

    @ViewBuilder
    private var screenContent: some View {
        Group {
            if viewModel.isLoading && viewModel.notifications.isEmpty {
                ProgressView("Loading notifications…")
                    .frame(maxWidth: .infinity, maxHeight: .infinity)
            } else if let error = viewModel.errorMessage, viewModel.notifications.isEmpty {
                ContentUnavailableView {
                    Label("Error", systemImage: "exclamationmark.triangle")
                } description: {
                    Text(error)
                } actions: {
                    Button("Retry") {
                        let task = Task { await viewModel.loadNotifications() }
                        activeTasks.append(task)
                    }
                }
            } else if viewModel.notifications.isEmpty {
                EmptyStateView(
                    icon: "bell.slash",
                    title: "No Notifications",
                    message: "You're all caught up! Notifications about print completions, failures, and alerts will appear here."
                )
            } else {
                notificationList
            }
        }
        .navigationTitle("Notifications")
        .toolbar {
            ToolbarItem(placement: .automatic) {
                if !viewModel.notifications.isEmpty {
                    Button("Mark All Read") {
                        UIImpactFeedbackGenerator(style: .medium).impactOccurred()
                        let task = Task { await viewModel.markAllRead() }
                        activeTasks.append(task)
                    }
                    .disabled(viewModel.unreadCount == 0)
                }
            }
        }
        .refreshable {
            await viewModel.loadNotifications()
        }
        .navigationDestination(for: AppDestination.self) { destination in
            destinationView(for: destination)
        }
    }

    // MARK: - Notification List

    private var notificationList: some View {
        List {
            ForEach(viewModel.notifications) { notification in
                notificationDestination(notification)
                    .swipeActions(edge: .leading) {
                        if !notification.isRead {
                            Button {
                                UIImpactFeedbackGenerator(style: .light).impactOccurred()
                                let task = Task { await viewModel.markRead(id: notification.id) }
                                activeTasks.append(task)
                            } label: {
                                Label("Read", systemImage: "envelope.open")
                            }
                            .tint(Color.pfHomed)
                        }
                    }
                    .swipeActions(edge: .trailing) {
                        Button(role: .destructive) {
                            UIImpactFeedbackGenerator(style: .medium).impactOccurred()
                            let task = Task { await viewModel.deleteNotification(id: notification.id) }
                            activeTasks.append(task)
                        } label: {
                            Label("Delete", systemImage: "trash")
                        }
                    }
                    .accessibilityIdentifier("notifications.row.\(notification.id)")
            }
        }
        .listStyle(.plain)
    }

    @ViewBuilder
    private func notificationDestination(_ notification: AppNotification) -> some View {
        if let jobId = notification.jobId {
            NavigationLink(value: AppDestination.jobDetail(id: jobId)) {
                NotificationRow(notification: notification)
            }
            .simultaneousGesture(TapGesture().onEnded {
                markReadIfNeeded(notification)
            })
        } else {
            NotificationRow(notification: notification)
                .contentShape(Rectangle())
                .onTapGesture {
                    markReadIfNeeded(notification)
                }
        }
    }

    private func markReadIfNeeded(_ notification: AppNotification) {
        guard !notification.isRead else { return }
        let task = Task {
            await viewModel.markRead(id: notification.id)
        }
        activeTasks.append(task)
    }
}

// MARK: - Notification Row

private struct NotificationRow: View {
    let notification: AppNotification

    var body: some View {
        HStack(spacing: 12) {
            // Unread indicator
            Circle()
                .fill(notification.isRead ? Color.clear : Color.pfAccent)
                .frame(width: 8, height: 8)

            // Icon
            Image(systemName: iconName)
                .font(.title3)
                .foregroundStyle(iconColor)
                .frame(width: 32)

            // Content
            VStack(alignment: .leading, spacing: 4) {
                Text(notification.subject)
                    .font(.subheadline.weight(notification.isRead ? .regular : .semibold))
                    .lineLimit(1)

                Text(notification.body)
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .lineLimit(2)
            }

            Spacer()

            Text(notification.createdAt.relativeFormatted)
                .font(.caption2)
                .foregroundStyle(.tertiary)
        }
        .padding(.vertical, 4)
        .opacity(notification.isRead ? 0.7 : 1.0)
    }

    private var iconName: String {
        switch notification.type {
        case .jobCompleted: "checkmark.circle.fill"
        case .jobFailed: "xmark.octagon.fill"
        case .jobStarted: "play.circle.fill"
        case .jobPaused: "pause.circle.fill"
        case .jobResumed: "play.fill"
        case .queueAlert: "exclamationmark.triangle.fill"
        case .systemAlert: "info.circle.fill"
        case .bedClearRequired: "bed.double.fill"
        }
    }

    private var iconColor: Color {
        switch notification.type {
        case .jobCompleted: .pfSuccess
        case .jobFailed: .pfError
        case .jobStarted, .jobResumed: .pfSecondaryAccent
        case .jobPaused: .pfWarning
        case .queueAlert: .pfWarning
        case .systemAlert: .pfSecondaryAccent
        case .bedClearRequired: .pfWarning
        }
    }
}
