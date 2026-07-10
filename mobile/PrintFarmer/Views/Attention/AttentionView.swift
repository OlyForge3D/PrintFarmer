import SwiftUI
#if canImport(UIKit)
import UIKit
#endif

/// Attention tab entry — the operator's default landing surface.
///
/// F1 (#706) folds the retired standalone Notifications tab into this
/// destination and adds the overflow menu that exposes Settings, the
/// server switcher, and access to legacy Dashboard / Maintenance surfaces
/// while F2 builds out the unified attention feed. `NotificationsViewModel`
/// remains the underlying data source; F2 will replace this with the
/// attention API.
struct AttentionView: View {
    @Environment(AppRouter.self) private var router
    @Environment(ServiceContainer.self) private var services
    @Environment(ServerRegistry.self) private var serverRegistry
    @State private var viewModel = NotificationsViewModel()
    @State private var activeTasks: [Task<Void, Never>] = []
    @State private var showingSettings = false
    @State private var showingDashboard = false
    @State private var showingMaintenance = false

    var body: some View {
        @Bindable var router = router

        NavigationStack(path: $router.notificationsPath) {
            Group {
                if viewModel.isLoading && viewModel.notifications.isEmpty {
                    ProgressView("Loading attention…")
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
                        title: "Nothing needs attention",
                        message: "You're all caught up. Alerts, completions, and issues will surface here."
                    )
                } else {
                    attentionList
                }
            }
            .navigationTitle("Attention")
            .toolbar { toolbarContent }
            .refreshable {
                await viewModel.loadNotifications()
            }
            .navigationDestination(for: AppDestination.self) { destination in
                destinationView(for: destination)
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
        .sheet(isPresented: $showingSettings) {
            SettingsView()
        }
        .sheet(isPresented: $showingDashboard) {
            DashboardView()
        }
        .sheet(isPresented: $showingMaintenance) {
            MaintenanceView()
        }
    }

    // MARK: - Toolbar

    @ToolbarContentBuilder
    private var toolbarContent: some ToolbarContent {
        ToolbarItem(placement: .topBarLeading) {
            if ServerSwitcherViewModel(
                servers: serverRegistry.servers,
                activeServerID: serverRegistry.activeServerID
            ).isVisible {
                ServerSwitcherMenu(style: .toolbar)
            }
        }

        ToolbarItem(placement: .topBarTrailing) {
            Menu {
                if !viewModel.notifications.isEmpty {
                    Button {
                        #if canImport(UIKit)
                        UIImpactFeedbackGenerator(style: .medium).impactOccurred()
                        #endif
                        let task = Task { await viewModel.markAllRead() }
                        activeTasks.append(task)
                    } label: {
                        Label("Mark all read", systemImage: "envelope.open")
                    }
                    .disabled(viewModel.unreadCount == 0)
                    Divider()
                }

                Button {
                    showingDashboard = true
                } label: {
                    Label("Dashboard", systemImage: "square.grid.2x2")
                }

                Button {
                    showingMaintenance = true
                } label: {
                    Label("Maintenance", systemImage: "wrench.adjustable")
                }

                Divider()

                Button {
                    showingSettings = true
                } label: {
                    Label("Settings", systemImage: "gear")
                }
            } label: {
                Image(systemName: "ellipsis.circle")
                    .accessibilityLabel("More")
            }
            .accessibilityIdentifier("attention.overflow")
        }
    }

    // MARK: - List

    private var attentionList: some View {
        List {
            ForEach(viewModel.notifications) { notification in
                AttentionRow(notification: notification)
                    .swipeActions(edge: .leading) {
                        if !notification.isRead {
                            Button {
                                #if canImport(UIKit)
                                UIImpactFeedbackGenerator(style: .light).impactOccurred()
                                #endif
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
                            #if canImport(UIKit)
                            UIImpactFeedbackGenerator(style: .medium).impactOccurred()
                            #endif
                            let task = Task { await viewModel.deleteNotification(id: notification.id) }
                            activeTasks.append(task)
                        } label: {
                            Label("Delete", systemImage: "trash")
                        }
                    }
                    .onTapGesture {
                        handleTap(notification)
                    }
            }
        }
        .listStyle(.plain)
    }

    private func handleTap(_ notification: AppNotification) {
        if !notification.isRead {
            let task = Task { await viewModel.markRead(id: notification.id) }
            activeTasks.append(task)
        }
        if let jobId = notification.jobId {
            router.notificationsPath.append(AppDestination.jobDetail(id: jobId))
        }
    }
}

// MARK: - Row

private struct AttentionRow: View {
    let notification: AppNotification

    var body: some View {
        HStack(spacing: 12) {
            Circle()
                .fill(notification.isRead ? Color.clear : Color.pfAccent)
                .frame(width: 8, height: 8)

            Image(systemName: iconName)
                .font(.title3)
                .foregroundStyle(iconColor)
                .frame(width: 32)

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
