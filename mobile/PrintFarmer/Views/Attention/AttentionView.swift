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
    @State private var showingNotifications = false
    /// Locally observed feature-disabled flag. Populated from the shared
    /// operator feature gate (#725) via `SystemCapabilitiesService` and
    /// also flipped locally when a downstream request returns
    /// ProblemDetails with `code == "featureDisabled"` so the fallback
    /// stays sticky for the session.
    @State private var attentionEnabled: Bool = true

    var body: some View {
        @Bindable var router = router

        NavigationStack(path: $router.notificationsPath) {
            Group {
                if !attentionEnabled {
                    disabledFallback
                } else if viewModel.isLoading && viewModel.notifications.isEmpty {
                    ProgressView("Loading attention…")
                        .frame(maxWidth: .infinity, maxHeight: .infinity)
                        .accessibilityIdentifier("attention.loading")
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
                        .accessibilityIdentifier("attention.retry")
                    }
                    .accessibilityIdentifier("attention.error")
                } else if viewModel.notifications.isEmpty {
                    EmptyStateView(
                        icon: "bell.slash",
                        title: "Nothing needs attention",
                        message: "You're all caught up. Alerts, completions, and issues will surface here."
                    )
                    .accessibilityIdentifier("attention.empty")
                } else {
                    attentionList
                }
            }
            .navigationTitle("Attention")
            .toolbar { toolbarContent }
            .refreshable {
                if attentionEnabled {
                    await viewModel.loadNotifications()
                }
                await services.capabilitiesService.refresh()
                attentionEnabled = services.capabilitiesService.resolved.attentionEnabled
            }
            .navigationDestination(for: AppDestination.self) { destination in
                destinationView(for: destination)
            }
        }
        .task {
            // #725: fetch shared operator feature gates before loading any
            // downstream feature-owned endpoints so we can render the safe
            // fallback without a flash of the enabled UI.
            await services.capabilitiesService.refresh()
            attentionEnabled = services.capabilitiesService.resolved.attentionEnabled

            viewModel.isViewActive = true
            viewModel.configure(notificationService: services.notificationService)
            if attentionEnabled {
                await viewModel.loadNotifications()
            }
        }
        .onChange(of: viewModel.unreadCount) { _, newValue in
            router.notificationBadgeCount = attentionEnabled ? newValue : 0
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
        .sheet(isPresented: $showingNotifications) {
            NotificationsView()
        }
        // #727: Reset each legacy fallback sheet's owned NavigationPath
        // when the sheet is dismissed so reopening starts at its documented
        // root. `resetLegacySheet(_:)` never touches the active tab's
        // stack, so dismissing a sheet leaves the Attention tab's own
        // navigation intact.
        .onChange(of: showingDashboard) { _, isPresented in
            if !isPresented { router.resetLegacySheet(.dashboard) }
        }
        .onChange(of: showingMaintenance) { _, isPresented in
            if !isPresented { router.resetLegacySheet(.maintenance) }
        }
        .onChange(of: showingNotifications) { _, isPresented in
            if !isPresented { router.resetLegacySheet(.notifications) }
        }
        .onChange(of: showingSettings) { _, isPresented in
            if !isPresented { router.resetLegacySheet(.settings) }
        }
    }

    // MARK: - Feature-disabled fallback (#725)

    /// Safe fallback shown when `attentionEnabled` resolves to `false`
    /// or when `/api/attention` returns ProblemDetails with
    /// `code == "featureDisabled"`. The three legacy source screens are
    /// preserved as non-tab destinations so operators can still reach
    /// notifications, maintenance, and the dashboard through the beta.
    private var disabledFallback: some View {
        VStack(spacing: 24) {
            VStack(spacing: 12) {
                Image(systemName: "bell.slash.circle")
                    .font(.system(size: 44, weight: .regular))
                    .foregroundStyle(.secondary)
                    .accessibilityHidden(true)

                Text("Operator feed disabled")
                    .font(.title3.weight(.semibold))
                    .multilineTextAlignment(.center)

                Text("The operator attention feed is turned off on this server. Use the legacy screens below while it's disabled.")
                    .font(.subheadline)
                    .foregroundStyle(.secondary)
                    .multilineTextAlignment(.center)
                    .fixedSize(horizontal: false, vertical: true)
            }
            .padding(.horizontal, 24)
            .accessibilityElement(children: .combine)

            VStack(spacing: 12) {
                fallbackButton(
                    title: "Notifications",
                    systemImage: "bell",
                    hint: "Opens the classic notifications list.",
                    identifier: "attention.fallback.notifications"
                ) {
                    showingNotifications = true
                }
                fallbackButton(
                    title: "Dashboard",
                    systemImage: "square.grid.2x2",
                    hint: "Opens the printer dashboard summary.",
                    identifier: "attention.fallback.dashboard"
                ) {
                    showingDashboard = true
                }
                fallbackButton(
                    title: "Maintenance",
                    systemImage: "wrench.adjustable",
                    hint: "Opens the maintenance tasks screen.",
                    identifier: "attention.fallback.maintenance"
                ) {
                    showingMaintenance = true
                }
            }
            .padding(.horizontal, 24)
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .accessibilityIdentifier("attention.disabled.fallback")
    }

    @ViewBuilder
    private func fallbackButton(
        title: String,
        systemImage: String,
        hint: String,
        identifier: String,
        action: @escaping () -> Void
    ) -> some View {
        Button(action: action) {
            HStack(spacing: 12) {
                Image(systemName: systemImage)
                    .font(.headline)
                    .frame(width: 28)
                    .accessibilityHidden(true)
                Text(title)
                    .font(.body.weight(.semibold))
                Spacer(minLength: 8)
                Image(systemName: "chevron.right")
                    .font(.caption)
                    .foregroundStyle(.tertiary)
                    .accessibilityHidden(true)
            }
            .padding(.horizontal, 16)
            .padding(.vertical, 14)
            .frame(minHeight: 44)
            .frame(maxWidth: .infinity)
            .background(Color.pfCard, in: RoundedRectangle(cornerRadius: 12))
            .overlay(
                RoundedRectangle(cornerRadius: 12)
                    .strokeBorder(Color.pfBorder, lineWidth: 1)
            )
        }
        .buttonStyle(.plain)
        .accessibilityLabel(title)
        .accessibilityHint(hint)
        .accessibilityAddTraits(.isButton)
        .accessibilityIdentifier(identifier)
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
                    .accessibilityIdentifier("attention.overflow.markAllRead")
                    Divider()
                }

                Button {
                    showingDashboard = true
                } label: {
                    Label("Dashboard", systemImage: "square.grid.2x2")
                }
                .accessibilityIdentifier("attention.overflow.dashboard")

                Button {
                    showingMaintenance = true
                } label: {
                    Label("Maintenance", systemImage: "wrench.adjustable")
                }
                .accessibilityIdentifier("attention.overflow.maintenance")

                Divider()

                Button {
                    showingSettings = true
                } label: {
                    Label("Settings", systemImage: "gear")
                }
                .accessibilityIdentifier("attention.overflow.settings")
            } label: {
                Image(systemName: "ellipsis.circle")
                    .imageScale(.large)
                    .frame(minWidth: 44, minHeight: 44)
            }
            .accessibilityLabel("More")
            .accessibilityHint("Opens dashboard, maintenance, and settings.")
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
            // Unread indicator combines a shape AND a text label. Color
            // alone is never used to convey unread state (WCAG 1.4.1).
            Circle()
                .fill(notification.isRead ? Color.clear : Color.pfAccent)
                .frame(width: 8, height: 8)
                .accessibilityHidden(true)

            Image(systemName: iconName)
                .font(.title3)
                .foregroundStyle(iconColor)
                .frame(width: 32)
                .accessibilityHidden(true)

            VStack(alignment: .leading, spacing: 4) {
                Text(notification.subject)
                    .font(.subheadline.weight(notification.isRead ? .regular : .semibold))
                    .lineLimit(2)

                Text(notification.body)
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .lineLimit(3)
            }
            .fixedSize(horizontal: false, vertical: true)

            Spacer(minLength: 8)

            Text(notification.createdAt.relativeFormatted)
                .font(.caption2)
                .foregroundStyle(.tertiary)
                .accessibilityHidden(true)
        }
        .padding(.vertical, 6)
        .frame(minHeight: 44)
        .opacity(notification.isRead ? 0.7 : 1.0)
        .accessibilityElement(children: .combine)
        .accessibilityLabel(accessibilityLabel)
        .accessibilityAddTraits(.isButton)
    }

    private var accessibilityLabel: String {
        let status = notification.isRead ? "Read" : "Unread"
        return "\(status). \(kindLabel). \(notification.subject). \(notification.body). \(notification.createdAt.relativeFormatted)."
    }

    private var kindLabel: String {
        switch notification.type {
        case .jobCompleted: "Job completed"
        case .jobFailed: "Job failed"
        case .jobStarted: "Job started"
        case .jobPaused: "Job paused"
        case .jobResumed: "Job resumed"
        case .queueAlert: "Queue alert"
        case .systemAlert: "System alert"
        case .bedClearRequired: "Bed clear required"
        }
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
