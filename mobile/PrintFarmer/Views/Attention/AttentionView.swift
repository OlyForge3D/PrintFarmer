import SwiftUI
#if canImport(UIKit)
import UIKit
#endif

/// Attention tab — F2-U1 (issue #779) unified feed shell.
///
/// Replaces the F1 placeholder that reused `NotificationsViewModel`
/// while F2 was in flight. The tab now renders the canonical
/// severity-ordered Attention feed backed by `AttentionFeedViewModel`
/// against the shipped `AttentionServiceProtocol` (#744) and the
/// lowercase `attentionchanged` invalidation subscription (#731/#707).
///
/// Explicit boundaries (per #779):
/// * No inline action execution, snooze UI, or media capture — owned
///   by F2-U2 (#780).
/// * No reconnect-gap refresh — owned by F2-R (#781).
/// * Kinds/severities/actions/timestamps come from the server; the
///   shell only groups by canonical severity.
struct AttentionView: View {
    @Environment(AppRouter.self) private var router
    @Environment(ServiceContainer.self) private var services
    @Environment(ServerRegistry.self) private var serverRegistry
    @State private var feedViewModel = AttentionFeedViewModel()
    @State private var showingSettings = false
    @State private var showingDashboard = false
    @State private var showingMaintenance = false
    @State private var showingNotifications = false
    /// Locally observed feature-disabled flag. Populated from the shared
    /// operator feature gate (#725) via `SystemCapabilitiesService` and
    /// also flipped locally when `/api/attention` returns ProblemDetails
    /// with `code == "featureDisabled"` (the view model surfaces this
    /// through `phase == .disabled`).
    @State private var attentionEnabled: Bool = true

    var body: some View {
        @Bindable var router = router

        NavigationStack(path: $router.notificationsPath) {
            Group {
                if !attentionEnabled || feedViewModel.phase == .disabled {
                    disabledFallback
                } else {
                    feedContent
                }
            }
            .navigationTitle("Attention")
            .toolbar { toolbarContent }
            .refreshable {
                await services.capabilitiesService.refresh()
                let previouslyEnabled = attentionEnabled
                attentionEnabled = services.capabilitiesService.resolved.attentionEnabled
                // Rebind the view model if the gate flipped so the
                // disabled fallback is authoritative for the session.
                if previouslyEnabled != attentionEnabled {
                    feedViewModel.configure(
                        attentionService: services.attentionService,
                        signalRService: services.signalRService,
                        attentionEnabled: attentionEnabled
                    )
                }
                if attentionEnabled {
                    await feedViewModel.refresh()
                }
            }
            .navigationDestination(for: AppDestination.self) { destination in
                destinationView(for: destination)
            }
        }
        .task(id: services.activeServerGeneration) {
            // #725: fetch shared operator feature gates before loading
            // any downstream feature-owned endpoints so we render the
            // safe fallback without a flash of the enabled UI.
            await services.capabilitiesService.refresh()
            attentionEnabled = services.capabilitiesService.resolved.attentionEnabled

            feedViewModel.configure(
                attentionService: services.attentionService,
                signalRService: services.signalRService,
                attentionEnabled: attentionEnabled
            )

            if attentionEnabled {
                await feedViewModel.refresh()
            }
        }
        .onChange(of: feedItemCount) { _, newValue in
            // #779 replaces the notifications-count-driven badge with a
            // count derived from the canonical attention feed. Badge
            // reflects the total number of items needing attention.
            router.notificationBadgeCount = attentionEnabled ? newValue : 0
        }
        .onDisappear {
            feedViewModel.deactivate()
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

    private var feedItemCount: Int {
        feedViewModel.snapshot?.items.count ?? 0
    }

    // MARK: - Feed content

    @ViewBuilder
    private var feedContent: some View {
        switch feedViewModel.phase {
        case .idle, .loading:
            ProgressView("Loading attention…")
                .frame(maxWidth: .infinity, maxHeight: .infinity)
                .accessibilityIdentifier("attention.loading")
        case .error where feedViewModel.snapshot == nil:
            errorPlaceholder
        case .loaded where feedViewModel.shouldShowEmpty:
            EmptyStateView(
                icon: "checkmark.seal",
                title: "Nothing needs attention",
                message: "All monitored printers are running normally."
            )
            .accessibilityIdentifier("attention.empty")
        case .loaded, .error:
            attentionList
        case .disabled:
            // Guarded above; fall through as a defensive no-op.
            EmptyView()
        }
    }

    private var errorPlaceholder: some View {
        ContentUnavailableView {
            Label("Couldn't load attention", systemImage: "exclamationmark.triangle")
        } description: {
            Text(feedViewModel.loadFailure?.message
                ?? "The attention feed could not be loaded.")
        } actions: {
            Button("Retry") {
                Task {
                    guard let failure = feedViewModel.loadFailure else {
                        await feedViewModel.refresh()
                        return
                    }
                    await feedViewModel.retryLoad(failureID: failure.id)
                }
            }
            .accessibilityIdentifier("attention.retry")
        }
        .accessibilityIdentifier("attention.error")
    }

    // MARK: - List

    @ViewBuilder
    private var attentionList: some View {
        List {
            if let failure = feedViewModel.loadFailure, feedViewModel.snapshot != nil {
                // Inline refresh-error banner shown when a snapshot is
                // already visible. The list stays pull-to-refresh
                // accessible so recovery does not require an empty
                // list (#779).
                Section {
                    inlineRefreshError(failure)
                }
                .accessibilityIdentifier("attention.refreshError")
            }

            if feedViewModel.shouldShowHealthySummary,
               let healthyCount = feedViewModel.snapshot?.healthyPrinterCount {
                healthySummarySection(healthyCount: healthyCount)
            }

            ForEach(feedViewModel.groups) { group in
                Section {
                    ForEach(group.items) { item in
                        AttentionItemRow(item: item)
                            .accessibilityIdentifier("attention.item.\(item.id)")
                    }
                } header: {
                    Text(group.severity.accessibilityLabel)
                        .font(.footnote.weight(.semibold))
                        .accessibilityIdentifier("attention.severity.\(group.severity.rawValue)")
                }
            }

            if feedViewModel.canLoadMore {
                paginationSentinel
            }
        }
        .listStyle(.insetGrouped)
        .accessibilityIdentifier("attention.list")
    }

    private func inlineRefreshError(_ failure: AttentionLoadFailure) -> some View {
        VStack(alignment: .leading, spacing: 8) {
            Label("Refresh failed", systemImage: "exclamationmark.triangle.fill")
                .font(.subheadline.weight(.semibold))
                .foregroundStyle(.red)
            Text(failure.message)
                .font(.footnote)
                .foregroundStyle(.secondary)
            Button("Try again") {
                Task { await feedViewModel.retryLoad(failureID: failure.id) }
            }
            .buttonStyle(.borderedProminent)
            .controlSize(.small)
            .accessibilityIdentifier("attention.refreshError.retry")
        }
    }

    private func healthySummarySection(healthyCount: Int) -> some View {
        Section {
            DisclosureGroup(isExpanded: Binding(
                get: { feedViewModel.isHealthySummaryExpanded },
                set: { feedViewModel.isHealthySummaryExpanded = $0 }
            )) {
                Text("Nothing to do for the healthy printers. They will appear here if a job stalls, a filament runs out, or a build plate needs to be cleared.")
                    .font(.footnote)
                    .foregroundStyle(.secondary)
                    .accessibilityIdentifier("attention.healthy.detail")
            } label: {
                Label {
                    Text("\(healthyCount) \(healthyCount == 1 ? "printer" : "printers") running normally")
                        .font(.subheadline.weight(.medium))
                } icon: {
                    Image(systemName: "checkmark.circle.fill")
                        .foregroundStyle(.green)
                }
            }
            .accessibilityIdentifier("attention.healthy.summary")
        }
    }

    private var paginationSentinel: some View {
        HStack {
            Spacer()
            if feedViewModel.isLoadingMore {
                ProgressView()
                    .accessibilityIdentifier("attention.loadingMore")
            } else {
                Text("Load more")
                    .font(.footnote)
                    .foregroundStyle(.secondary)
                    .accessibilityIdentifier("attention.loadMoreSentinel")
            }
            Spacer()
        }
        .frame(minHeight: 32)
        .onAppear {
            // Bounded trigger: `loadMore` guards against dispatching a
            // duplicate request while one is already in flight or while
            // a canonical refresh is running.
            Task { await feedViewModel.loadMore() }
        }
    }

    // MARK: - Feature-disabled fallback (#725)

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
                Button {
                    showingNotifications = true
                } label: {
                    Label("Notifications", systemImage: "bell")
                }
                .accessibilityIdentifier("attention.overflow.notifications")

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
}

// MARK: - Row

/// Read-only attention item card. #779 explicitly excludes inline action
/// execution, snooze UI, camera snapshots/media, and printer/job
/// destination navigation. The row therefore renders server-supplied
/// title/detail/timestamp/deadline only. F2-U2 (#780) will augment this
/// with actions and destinations.
private struct AttentionItemRow: View {
    let item: AttentionItem

    var body: some View {
        HStack(spacing: 12) {
            Image(systemName: iconName)
                .font(.title3)
                .foregroundStyle(iconColor)
                .frame(width: 32)
                .accessibilityHidden(true)

            VStack(alignment: .leading, spacing: 4) {
                Text(item.title)
                    .font(.subheadline.weight(.semibold))
                    .lineLimit(2)

                Text(item.detail)
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .lineLimit(3)

                HStack(spacing: 8) {
                    Text(item.printerName)
                        .font(.caption2.weight(.medium))
                        .foregroundStyle(.tertiary)
                    if let deadline = item.deadlineAt {
                        Text("• Due \(deadline.relativeFormatted)")
                            .font(.caption2)
                            .foregroundStyle(.tertiary)
                    }
                }
            }
            .fixedSize(horizontal: false, vertical: true)

            Spacer(minLength: 8)

            Text(item.occurredAt.relativeFormatted)
                .font(.caption2)
                .foregroundStyle(.tertiary)
                .accessibilityHidden(true)
        }
        .padding(.vertical, 6)
        .frame(minHeight: 44)
        .accessibilityElement(children: .combine)
        .accessibilityLabel(accessibilityLabel)
    }

    private var accessibilityLabel: String {
        let severity: String
        switch item.severity {
        case .critical: severity = "Critical"
        case .warning: severity = "Warning"
        case .info: severity = "Informational"
        case .unknown: severity = "Attention"
        }
        var parts = [severity, item.title, item.detail, item.printerName]
        parts.append(item.occurredAt.relativeFormatted)
        if let deadline = item.deadlineAt {
            parts.append("Due \(deadline.relativeFormatted)")
        }
        return parts.joined(separator: ". ")
    }

    private var iconName: String {
        switch item.kind {
        case .failure: "xmark.octagon.fill"
        case .runout: "drop.triangle.fill"
        case .harvest: "tray.and.arrow.up"
        case .maintenance: "wrench.adjustable.fill"
        case .offline: "wifi.exclamationmark"
        case .unknown: "exclamationmark.circle.fill"
        }
    }

    private var iconColor: Color {
        switch item.severity {
        case .critical: .pfError
        case .warning: .pfWarning
        case .info: .pfSecondaryAccent
        case .unknown: .pfSecondaryAccent
        }
    }
}
