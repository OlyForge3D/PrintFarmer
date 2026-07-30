import SwiftUI
#if canImport(UIKit)
import UIKit
#endif
#if canImport(AppKit)
import AppKit
#endif

struct AttentionPendingAction: Identifiable, Equatable {
    let fingerprint: AttentionOccurrenceFingerprint
    let action: AttentionAction
    let serverGeneration: Int

    var itemID: String { fingerprint.itemID }
    var id: String { "\(itemID):\(action.kind.rawValue)" }
}

struct AttentionNavigationTargets: Equatable {
    let printer: AppDestination
    let job: AppDestination?

    init(item: AttentionItem) {
        printer = .printerDetail(id: item.printerId)
        job = item.jobId.map(AppDestination.jobDetail)
    }
}

struct AttentionAccessibilityDescriptor: Equatable {
    let identifier: String
    let label: String
    let hint: String
}

enum AttentionAccessibility {
    static func card(_ item: AttentionItem) -> AttentionAccessibilityDescriptor {
        AttentionAccessibilityDescriptor(
            identifier: "attention.item.\(item.id)",
            label: "\(severityName(item.severity)) attention item",
            hint: "Contains details, destinations, media, and server-provided actions."
        )
    }

    static func summary(_ item: AttentionItem) -> AttentionAccessibilityDescriptor {
        AttentionAccessibilityDescriptor(
            identifier: "attention.item.\(item.id).summary",
            label: [
                item.title,
                item.detail,
                item.printerName,
                item.occurredAt.relativeFormatted,
            ].joined(separator: ". "),
            hint: "Review the item details before choosing an action."
        )
    }

    static func severity(_ item: AttentionItem) -> AttentionAccessibilityDescriptor {
        AttentionAccessibilityDescriptor(
            identifier: "attention.item.\(item.id).severity",
            label: "Severity, \(severityName(item.severity))",
            hint: "Indicates the urgency of this attention item."
        )
    }

    static func deadline(_ item: AttentionItem, deadline: Date) -> AttentionAccessibilityDescriptor {
        AttentionAccessibilityDescriptor(
            identifier: "attention.item.\(item.id).deadline",
            label: "Deadline, \(deadline.relativeFormatted)",
            hint: "Complete the required work before this deadline."
        )
    }

    static func healthySummary(count: Int, expanded: Bool) -> AttentionAccessibilityDescriptor {
        let printerText = count == 1 ? "printer" : "printers"
        return AttentionAccessibilityDescriptor(
            identifier: "attention.healthy.summary",
            label: "\(count) \(printerText) running normally",
            hint: expanded
                ? "Collapses the healthy printer explanation."
                : "Expands the healthy printer explanation."
        )
    }

    static func media(
        item: AttentionItem,
        state: AttentionMediaState
    ) -> AttentionAccessibilityDescriptor {
        let suffix: String
        let label: String
        let hint: String
        switch state {
        case .idle:
            suffix = "idle"
            label = "Load camera snapshot"
            hint = "Loads the latest available failure snapshot for \(item.printerName)."
        case .loading:
            suffix = "loading"
            label = "Loading failure camera snapshot for \(item.printerName)"
            hint = "Other attention items remain available while this image loads."
        case .available:
            suffix = "image"
            label = "Failure camera snapshot for \(item.printerName)"
            hint = "Use this image to inspect the reported print failure."
        case .unavailable:
            suffix = "unavailable"
            label = "Failure camera snapshot unavailable for \(item.printerName)"
            hint = "The item and its actions remain available. Retry the image manually."
        }
        return AttentionAccessibilityDescriptor(
            identifier: "attention.item.\(item.id).media.\(suffix)",
            label: label,
            hint: hint
        )
    }

    static func navigation(
        item: AttentionItem,
        destination: AppDestination
    ) -> AttentionAccessibilityDescriptor {
        switch destination {
        case .printerDetail:
            return AttentionAccessibilityDescriptor(
                identifier: "attention.item.\(item.id).navigation.printer",
                label: "Open printer, \(item.printerName)",
                hint: "Opens the printer matching this item's stable printer identifier."
            )
        case .jobDetail:
            return AttentionAccessibilityDescriptor(
                identifier: "attention.item.\(item.id).navigation.job",
                label: "Open related job",
                hint: "Opens the job matching this item's stable job identifier."
            )
        default:
            preconditionFailure("Attention cards only navigate to printer or job details")
        }
    }

    static func action(
        item: AttentionItem,
        action: AttentionAction
    ) -> AttentionAccessibilityDescriptor {
        AttentionAccessibilityDescriptor(
            identifier: "attention.item.\(item.id).action.\(action.kind.rawValue)",
            label: action.label,
            hint: actionHint(action.kind)
        )
    }

    static func actionProgress(
        item: AttentionItem,
        actionKind: AttentionActionKind
    ) -> AttentionAccessibilityDescriptor {
        AttentionAccessibilityDescriptor(
            identifier: "attention.item.\(item.id).action.progress",
            label: "\(actionName(actionKind)) in progress",
            hint: "Wait for the server action and canonical feed refresh to finish."
        )
    }

    static func actionError(
        item: AttentionItem,
        failure: AttentionActionFailure
    ) -> AttentionAccessibilityDescriptor {
        AttentionAccessibilityDescriptor(
            identifier: "attention.item.\(item.id).action.error",
            label: "\(failure.action.label) failed. \(failure.message)",
            hint: "Retry this item or choose another available action."
        )
    }

    static func actionRefresh(
        item: AttentionItem,
        pending: AttentionActionRefreshPending
    ) -> AttentionAccessibilityDescriptor {
        if let message = pending.message {
            return AttentionAccessibilityDescriptor(
                identifier: "attention.item.\(item.id).action.refreshError",
                label: "\(pending.action.label) completed. Attention refresh failed. \(message)",
                hint: "Retry the canonical feed refresh without repeating the completed action."
            )
        }
        return AttentionAccessibilityDescriptor(
            identifier: "attention.item.\(item.id).action.refreshPending",
            label: "\(pending.action.label) completed. Refreshing attention",
            hint: "The completed action stays unavailable until canonical server truth is applied."
        )
    }

    static func orderedIdentifiers(
        item: AttentionItem,
        mediaState: AttentionMediaState?,
        actions: [AttentionAction],
        actionState: AttentionItemActionState
    ) -> [String] {
        var identifiers = [
            severity(item).identifier,
            summary(item).identifier,
        ]
        if let deadline = item.deadlineAt {
            identifiers.append(Self.deadline(item, deadline: deadline).identifier)
        }
        if let mediaState {
            identifiers.append(media(item: item, state: mediaState).identifier)
        }
        identifiers.append(navigation(item: item, destination: .printerDetail(id: item.printerId)).identifier)
        if let jobID = item.jobId {
            identifiers.append(navigation(item: item, destination: .jobDetail(id: jobID)).identifier)
        }
        identifiers.append(contentsOf: actions.map { action(item: item, action: $0).identifier })
        switch actionState {
        case .idle:
            break
        case .inProgress(let kind):
            identifiers.append(actionProgress(item: item, actionKind: kind).identifier)
        case .failed(let failure):
            identifiers.append(actionError(item: item, failure: failure).identifier)
            identifiers.append("attention.item.\(item.id).action.retry")
        case .refreshPending(let pending):
            identifiers.append(actionRefresh(item: item, pending: pending).identifier)
            if pending.message != nil {
                identifiers.append("attention.item.\(item.id).action.refreshRetry")
            }
        }
        return identifiers
    }

    private static func severityName(_ severity: AttentionSeverity) -> String {
        switch severity {
        case .critical: "Critical"
        case .warning: "Warning"
        case .info: "Informational"
        case .unknown: "Other"
        }
    }

    private static func actionName(_ kind: AttentionActionKind) -> String {
        switch kind {
        case .pause: "Pause"
        case .resume: "Resume"
        case .cancel: "Cancel"
        case .acknowledge: "Acknowledge"
        case .resolve: "Resolve"
        case .dismiss: "Dismiss"
        case .snooze: "Snooze"
        case .harvest: "Harvest"
        case .unknown: "Action"
        }
    }

    private static func actionHint(_ kind: AttentionActionKind) -> String {
        switch kind {
        case .pause: "Asks the server to pause the current print."
        case .resume: "Asks the server to resume the current print."
        case .cancel: "Asks the server to cancel the current print."
        case .acknowledge: "Acknowledges this maintenance alert on the server."
        case .resolve: "Resolves this maintenance alert on the server."
        case .dismiss: "Dismisses this maintenance alert on the server."
        case .snooze: "Snoozes this item for one hour using the server snooze route."
        case .harvest: "Records the completed plate harvest through the server action route."
        case .unknown: "This action is not supported by this app version."
        }
    }
}

/// Attention tab — canonical shell plus F2-U2 item interaction.
///
/// Kinds, severities, actions, timestamps, and action routes remain
/// server-owned. Reconnect-gap refresh remains owned by F2-R (#781).
struct AttentionView: View {
    @Environment(AppRouter.self) private var router
    @Environment(ServiceContainer.self) private var services
    @Environment(ServerRegistry.self) private var serverRegistry
    @State private var feedViewModel = AttentionFeedViewModel()
    @State private var showingSettings = false
    @State private var showingDashboard = false
    @State private var showingMaintenance = false
    @State private var showingNotifications = false
    @State private var pendingAction: AttentionPendingAction?
    /// Locally observed feature-disabled flag. Populated from the shared
    /// operator feature gate (#725) via `SystemCapabilitiesService` and
    /// also flipped locally when `/api/attention` returns ProblemDetails
    /// with `code == "featureDisabled"` (the view model surfaces this
    /// through `phase == .disabled`).
    @State private var attentionEnabled: Bool = true

    var body: some View {
        @Bindable var router = router

        NavigationStack(path: $router.notificationsPath) {
            VStack(spacing: 0) {
                // #789: shared stale/degraded banner — text + accessibility carry
                // the honestly-stale state (never color alone), identical on
                // iPhone and iPad. Only shown while offline cached data is on
                // screen (cleared the moment a canonical refresh confirms live).
                if feedViewModel.isShowingStaleCache {
                    ConnectionStatusBar(
                        status: .offline,
                        lastConfirmedAt: feedViewModel.cacheLastUpdatedAt,
                        hasCache: true
                    )
                }
                Group {
                    if !attentionEnabled || feedViewModel.phase == .disabled {
                        disabledFallback
                    } else {
                        feedContent
                    }
                }
            }
            .onChange(of: feedViewModel.mediaGeneration) { _, _ in
                reconcilePendingAction()
            }
            .navigationTitle("Attention")
            .toolbar { toolbarContent }
            .refreshable {
                // Cycle-8 blocker B fix: capture owner provenance
                // synchronously at .refreshable closure entry (before
                // any await). Passed as parameters into
                // performRecoveryRefresh so a mid-await service swap
                // can be detected.
                let capturedGeneration = services.activeServerGeneration
                let capturedToken = feedViewModel.currentLifecycleToken()
                await performRecoveryRefresh(
                    capturedServerGeneration: capturedGeneration,
                    capturedLifecycleToken: capturedToken
                )
            }
            .navigationDestination(for: AppDestination.self) { destination in
                destinationView(for: destination)
            }
        }
        .task(id: services.activeServerGeneration) {
            // #779 blocker 1: capture the VM's lifecycle token BEFORE
            // any pre-bootstrap `await`. If the view disappears
            // between capture and bootstrap, `deactivate` will have
            // bumped the token — bootstrap becomes a no-op and cannot
            // reactivate an off-screen view even when the capability
            // refresh swallowed its cancellation.
            let token = feedViewModel.currentLifecycleToken()

            // #725: fetch shared operator feature gates before loading
            // any downstream feature-owned endpoints so we render the
            // safe fallback without a flash of the enabled UI.
            await services.capabilitiesService.refresh()

            // SwiftUI cancels `.task` on disappear. Even if the
            // capability service swallowed cancellation, honour it
            // here and bail without touching the VM.
            if Task.isCancelled { return }

            attentionEnabled = services.capabilitiesService.resolved.attentionEnabled

            // #789: wire the read-cache before bootstrap so hydrate can surface
            // honestly-stale Attention on a cold/offline launch. Idempotent.
            feedViewModel.configureCache(services.attentionReadCache)

            // Single canonical GET per re-entry: bootstrap consumes any
            // queued drain (from a signalR event while off-screen) and
            // owns the refresh below — no duplicate dispatch. The
            // lifecycle token check inside bootstrap makes this a
            // no-op when the view is no longer present.
            await feedViewModel.bootstrap(
                attentionService: services.attentionService,
                signalRService: services.signalRService,
                attentionEnabled: attentionEnabled,
                lifecycleToken: token,
                printerService: services.printerService
            )
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
        .confirmationDialog(
            pendingAction.map { "Confirm \($0.action.label)" } ?? "Confirm action",
            isPresented: Binding(
                get: { pendingAction != nil },
                set: { if !$0 { pendingAction = nil } }
            ),
            titleVisibility: .visible,
            presenting: pendingAction
        ) { pending in
            Button(
                "Confirm \(pending.action.label)",
                role: confirmationRole(for: pending.action.kind)
            ) {
                pendingAction = nil
                triggerAction(pending)
            }
            Button("Cancel", role: .cancel) {
                pendingAction = nil
            }
        } message: { pending in
            Text("This sends \(pending.action.label) for this item to the PrintFarmer server.")
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
        .onChange(of: router.sheetDismissalNonce) { _, _ in
            // #726 sheet-safe routing: a task-action handoff requested that any
            // active operator/legacy sheet be dismissed before the destination
            // is applied. Close every legacy sheet; the per-sheet `.onChange`
            // handlers above reset their stacks as they transition to false.
            showingSettings = false
            showingDashboard = false
            showingMaintenance = false
            showingNotifications = false
        }
    }

    private var feedItemCount: Int {
        feedViewModel.snapshot?.items.count ?? 0
    }

    private func reconcilePendingAction() {
        if let pendingAction,
           !feedViewModel.shouldPreserveActionAuthority(
               for: pendingAction.fingerprint
           ) {
            self.pendingAction = nil
        }
    }

    /// Recovery orchestration that runs with EXPLICIT owner
    /// provenance captured synchronously at the UI trigger boundary.
    ///
    /// Cycle-8 blocker B fix: the previous implementation captured
    /// the lifecycle token inside its own body (`let token = ...`
    /// after `Task { }` had started). That created a "late-adoption"
    /// race where the Task body — spawned by a button under
    /// authority A — could begin running AFTER B had bootstrapped,
    /// and would then capture B's identity and duplicate its GET.
    ///
    /// Cycle-8 delta fix: RESTORE the post-capability-await lifecycle
    /// token guard. Cycle 8 dropped it (with the false reasoning
    /// that our own configure(newGate) would bump the token). But
    /// that only matters AFTER we configure — a deactivate that
    /// bumps the token BEFORE we configure must still cause the
    /// recovery to abort with zero VM mutation. See
    /// `AttentionRecoveryOrchestrator.run` for the full guard
    /// sequence.
    private func performRecoveryRefresh(
        capturedServerGeneration: Int,
        capturedLifecycleToken: AttentionLifecycleToken
    ) async {
        await AttentionRecoveryOrchestrator.run(
            capturedServerGeneration: capturedServerGeneration,
            capturedLifecycleToken: capturedLifecycleToken,
            currentServerGeneration: { services.activeServerGeneration },
            currentLifecycleToken: { feedViewModel.currentLifecycleToken() },
            capabilityRefresh: { await services.capabilitiesService.refresh() },
            resolvedAttentionEnabled: { services.capabilitiesService.resolved.attentionEnabled },
            getAttentionEnabled: { attentionEnabled },
            setAttentionEnabled: { attentionEnabled = $0 },
            configureVMWithNewGate: { newEnabled in
                feedViewModel.configure(
                    attentionService: services.attentionService,
                    signalRService: services.signalRService,
                    attentionEnabled: newEnabled
                )
            },
            resetDisabledLatch: { newEnabled in
                feedViewModel.retryDisabledRecovery(attentionEnabled: newEnabled)
            },
            refresh: { await feedViewModel.refresh() }
        )
    }

    /// Synchronous UI trigger for a recovery refresh. Captures the
    /// current owner provenance BEFORE spawning the async Task, then
    /// hands off to `performRecoveryRefresh`. Used by Button actions
    /// where the Task body would otherwise start under a potentially-
    /// different authority.
    private func triggerRecoveryRefresh() {
        let capturedGeneration = services.activeServerGeneration
        let capturedToken = feedViewModel.currentLifecycleToken()
        Task {
            await performRecoveryRefresh(
                capturedServerGeneration: capturedGeneration,
                capturedLifecycleToken: capturedToken
            )
        }
    }

    private func handleActionTap(item: AttentionItem, action: AttentionAction) {
        let pending = AttentionPendingAction(
            fingerprint: AttentionOccurrenceFingerprint(item: item),
            action: action,
            serverGeneration: services.activeServerGeneration
        )
        if action.requiresConfirmation {
            pendingAction = pending
        } else {
            triggerAction(pending)
        }
    }

    private func triggerAction(_ pending: AttentionPendingAction) {
        Task {
            guard pending.serverGeneration == services.activeServerGeneration else {
                return
            }
            await feedViewModel.performAction(
                pending.action,
                for: pending.fingerprint
            )
        }
    }

    private func retryAction(_ failure: AttentionActionFailure) {
        let capturedGeneration = services.activeServerGeneration
        Task {
            guard capturedGeneration == services.activeServerGeneration else {
                return
            }
            await feedViewModel.retryAction(failureID: failure.id)
        }
    }

    private func retryActionRefresh(_ pending: AttentionActionRefreshPending) {
        let capturedGeneration = services.activeServerGeneration
        Task {
            guard capturedGeneration == services.activeServerGeneration else {
                return
            }
            await feedViewModel.retryActionRefresh(pendingID: pending.id)
        }
    }

    private func retryMedia(itemID: String) {
        let capturedGeneration = services.activeServerGeneration
        Task {
            guard capturedGeneration == services.activeServerGeneration else {
                return
            }
            await feedViewModel.retrySnapshot(for: itemID)
        }
    }

    private func confirmationRole(for kind: AttentionActionKind) -> ButtonRole? {
        switch kind {
        case .cancel, .dismiss:
            .destructive
        default:
            nil
        }
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
            emptyStateSurface
        case .loaded, .error:
            attentionList
        case .disabled:
            // Guarded above; fall through as a defensive no-op.
            EmptyView()
        }
    }

    /// Empty-state surface wrapped in a scroll container so pull-to-
    /// refresh reliably fires even when there is no list content. The
    /// explicit Refresh button gives operators a discoverable recovery
    /// affordance that does not depend on a gesture — matches the
    /// `ShiftTasksView` precedent for empty/failed shells.
    private var emptyStateSurface: some View {
        ScrollView {
            VStack(spacing: 20) {
                Spacer(minLength: 60)
                EmptyStateView(
                    icon: "checkmark.seal",
                    title: "Nothing needs attention",
                    message: "All monitored printers are running normally."
                )
                .accessibilityIdentifier("attention.empty")

                Button {
                    // Cycle-8 blocker B fix: synchronous UI-trigger
                    // capture (owner provenance snapshotted here,
                    // before the Task body starts, so a mid-queue
                    // service swap cannot smuggle B identity into the
                    // A-triggered recovery).
                    triggerRecoveryRefresh()
                } label: {
                    Label("Refresh", systemImage: "arrow.clockwise")
                }
                .buttonStyle(.borderedProminent)
                .controlSize(.large)
                .accessibilityIdentifier("attention.empty.refresh")

                Spacer(minLength: 40)
            }
            .frame(maxWidth: .infinity)
            .padding(.horizontal, 24)
        }
        .scrollBounceBehavior(.always)
        .accessibilityIdentifier("attention.empty.scroll")
    }

    private var errorPlaceholder: some View {
        ContentUnavailableView {
            Label("Couldn't load attention", systemImage: "exclamationmark.triangle")
        } description: {
            Text(feedViewModel.loadFailure?.message
                ?? "The attention feed could not be loaded.")
        } actions: {
            Button("Retry") {
                // Cycle-8 audit: capture owner provenance
                // synchronously at the button tap so an A-triggered
                // retry cannot fire refresh()/retryLoad() against B
                // after service replacement.
                let capturedGeneration = services.activeServerGeneration
                Task {
                    guard capturedGeneration == services.activeServerGeneration else { return }
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
                        AttentionItemRow(
                            item: item,
                            actionState: feedViewModel.actionState(for: item.id),
                            mediaState: item.kind == .failure
                                ? feedViewModel.mediaState(for: item.id)
                                : nil,
                            mediaRequestID: feedViewModel.mediaRequestID(for: item.id),
                            onAction: { action in
                                handleActionTap(item: item, action: action)
                            },
                            onRetryAction: retryAction,
                            onRetryActionRefresh: retryActionRefresh,
                            onLoadMedia: {
                                await feedViewModel.loadSnapshot(for: item.id)
                            },
                            onRetryMedia: {
                                retryMedia(itemID: item.id)
                            }
                        )
                        .listRowInsets(
                            EdgeInsets(top: 8, leading: 16, bottom: 8, trailing: 16)
                        )
                        .listRowBackground(Color.clear)
                    }
                } header: {
                    Text(group.severity.accessibilityLabel)
                        .font(.footnote.weight(.semibold))
                        .accessibilityAddTraits(.isHeader)
                        .accessibilityLabel(
                            "\(group.severity.accessibilityLabel) attention items"
                        )
                        .accessibilityHint(
                            "Items in this section share the same severity."
                        )
                        .accessibilityIdentifier("attention.severity.\(group.severity.rawValue)")
                }
            }

            if feedViewModel.canLoadMore {
                paginationSentinel
            } else if let paginationFailure = feedViewModel.paginationFailure,
                      paginationFailure.cursor == feedViewModel.snapshot?.nextCursor {
                paginationRetrySurface(paginationFailure)
            }
        }
        .listStyle(.insetGrouped)
        .accessibilityIdentifier("attention.list")
    }

    /// Rendered in place of the auto-loading sentinel when `loadMore`
    /// has failed for the currently-visible cursor. Shows the explicit
    /// error and requires an operator tap to retry — the sentinel's
    /// `.onAppear` is deliberately gone from this surface so a failed
    /// page cannot re-trigger every time SwiftUI recycles the row.
    /// See #779 blocker 3.
    private func paginationRetrySurface(_ failure: AttentionPaginationFailure) -> some View {
        VStack(alignment: .leading, spacing: 8) {
            Label("Couldn't load more", systemImage: "exclamationmark.triangle.fill")
                .font(.subheadline.weight(.semibold))
                .foregroundStyle(.orange)
            Text(failure.message)
                .font(.footnote)
                .foregroundStyle(.secondary)
            Button {
                // Cycle-8 audit: capture owner provenance
                // synchronously. `retryLoadMore(failureID:)` is
                // structurally safe against A→B replacement because
                // `paginationFailure` is reset in `invalidateAuthority`
                // — a stale A failureID finds no matching failure
                // under B and returns false. The extra fence is
                // defense-in-depth so the intent is documented.
                let capturedGeneration = services.activeServerGeneration
                Task {
                    guard capturedGeneration == services.activeServerGeneration else { return }
                    await feedViewModel.retryLoadMore(failureID: failure.id)
                }
            } label: {
                Label("Retry loading more", systemImage: "arrow.clockwise")
            }
            .buttonStyle(.borderedProminent)
            .controlSize(.small)
            .accessibilityIdentifier("attention.loadMoreRetry")
        }
        .accessibilityIdentifier("attention.loadMoreFailure")
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
                // Cycle-8 audit: same defense-in-depth as
                // retryLoadMore above. `retryLoad(failureID:)` is
                // structurally safe because `loadFailure` is reset
                // in `invalidateAuthority`.
                let capturedGeneration = services.activeServerGeneration
                Task {
                    guard capturedGeneration == services.activeServerGeneration else { return }
                    await feedViewModel.retryLoad(failureID: failure.id)
                }
            }
            .buttonStyle(.borderedProminent)
            .controlSize(.small)
            .accessibilityIdentifier("attention.refreshError.retry")
        }
    }

    private func healthySummarySection(healthyCount: Int) -> some View {
        let accessibility = AttentionAccessibility.healthySummary(
            count: healthyCount,
            expanded: feedViewModel.isHealthySummaryExpanded
        )
        return Section {
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
            .accessibilityLabel(accessibility.label)
            .accessibilityValue(
                feedViewModel.isHealthySummaryExpanded ? "Expanded" : "Collapsed"
            )
            .accessibilityHint(accessibility.hint)
            .accessibilityAddTraits(.isButton)
            .accessibilityIdentifier(accessibility.identifier)
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
            //
            // Cycle-8 audit: capture owner provenance synchronously.
            // `loadMore()` is structurally safe against A→B replacement
            // (activeRequestTokens are authority-scoped; snapshot's
            // cursor is per-authority) but the extra fence documents
            // the intent and prevents a stray A-onAppear queued Task
            // from firing a paginated load against B.
            let capturedGeneration = services.activeServerGeneration
            Task {
                guard capturedGeneration == services.activeServerGeneration else { return }
                await feedViewModel.loadMore()
            }
        }
    }

    // MARK: - Feature-disabled fallback (#725)
    //
    // Wrapped in a ScrollView with `.scrollBounceBehavior(.always)` so
    // pull-to-refresh fires reliably even though the fallback has no
    // list content. The explicit "Try again" button re-runs capability
    // resolution AND clears the view model's internal disabled latch —
    // without that latch clear, a `featureDisabled` server response
    // would have wedged the surface until the operator navigated away
    // and back.

    private var disabledFallback: some View {
        ScrollView {
            VStack(spacing: 24) {
                Spacer(minLength: 40)
                VStack(spacing: 12) {
                    Image(systemName: "bell.slash.circle")
                        .font(.system(size: 44, weight: .regular))
                        .foregroundStyle(.secondary)
                        .accessibilityHidden(true)

                    Text("Operator feed disabled")
                        .font(.title3.weight(.semibold))
                        .multilineTextAlignment(.center)

                    Text("The operator attention feed is turned off on this server. Use the legacy screens below while it's disabled, or check for a re-enable with Try again.")
                        .font(.subheadline)
                        .foregroundStyle(.secondary)
                        .multilineTextAlignment(.center)
                        .fixedSize(horizontal: false, vertical: true)
                }
                .padding(.horizontal, 24)
                .accessibilityElement(children: .combine)

                Button {
                    // Cycle-8 blocker B fix: synchronous UI-trigger
                    // capture — owner provenance snapshotted before
                    // Task body starts.
                    triggerRecoveryRefresh()
                } label: {
                    Label("Try again", systemImage: "arrow.clockwise")
                }
                .buttonStyle(.borderedProminent)
                .controlSize(.large)
                .accessibilityIdentifier("attention.disabled.retry")

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

                Spacer(minLength: 40)
            }
            .frame(maxWidth: .infinity)
        }
        .scrollBounceBehavior(.always)
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

// MARK: - Item card

struct AttentionItemRow: View {
    @Environment(\.dynamicTypeSize) private var dynamicTypeSize
    @Environment(AppRouter.self) private var router

    let item: AttentionItem
    let actionState: AttentionItemActionState
    let mediaState: AttentionMediaState?
    let mediaRequestID: AttentionMediaRequestID
    let onAction: (AttentionAction) -> Void
    let onRetryAction: (AttentionActionFailure) -> Void
    let onRetryActionRefresh: (AttentionActionRefreshPending) -> Void
    let onLoadMedia: () async -> Bool
    let onRetryMedia: () -> Void

    private var supportedActions: [AttentionAction] {
        AttentionFeedViewModel.supportedActions(in: item)
    }

    private var navigationTargets: AttentionNavigationTargets {
        AttentionNavigationTargets(item: item)
    }

    var body: some View {
        let cardAccessibility = AttentionAccessibility.card(item)

        VStack(alignment: .leading, spacing: 14) {
            severityHeader
            summary
            if let deadline = item.deadlineAt {
                deadlineView(deadline)
            }
            if let mediaState {
                mediaView(state: mediaState)
            }
            navigationLinks
            if !supportedActions.isEmpty || actionState != .idle {
                Divider()
                actionControls
            }
        }
        .padding(16)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(Color.pfCard, in: RoundedRectangle(cornerRadius: 14))
        .overlay(
            RoundedRectangle(cornerRadius: 14)
                .strokeBorder(Color.pfBorder, lineWidth: 1)
        )
        .accessibilityElement(children: .contain)
        .accessibilityLabel(cardAccessibility.label)
        .accessibilityHint(cardAccessibility.hint)
        .accessibilityIdentifier(cardAccessibility.identifier)
    }

    private var severityHeader: some View {
        let accessibility = AttentionAccessibility.severity(item)

        return HStack(alignment: .firstTextBaseline, spacing: 8) {
            Label(severityText, systemImage: iconName)
                .font(.caption.weight(.bold))
                .foregroundStyle(iconColor)
                .padding(.horizontal, 10)
                .padding(.vertical, 5)
                .background(iconColor.opacity(0.12), in: Capsule())
                .accessibilityLabel(accessibility.label)
                .accessibilityHint(accessibility.hint)
                .accessibilityIdentifier(accessibility.identifier)

            Spacer(minLength: 8)

            Text(item.occurredAt.relativeFormatted)
                .font(.caption)
                .foregroundStyle(.secondary)
                .accessibilityHidden(true)
        }
    }

    private var summary: some View {
        let accessibility = AttentionAccessibility.summary(item)

        return VStack(alignment: .leading, spacing: 6) {
            Text(item.title)
                .font(.headline)
                .fixedSize(horizontal: false, vertical: true)

            Text(item.detail)
                .font(.subheadline)
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)

            Text(item.printerName)
                .font(.footnote.weight(.semibold))
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)
        }
        .accessibilityElement(children: .ignore)
        .accessibilityLabel(accessibility.label)
        .accessibilityHint(accessibility.hint)
        .accessibilityIdentifier(accessibility.identifier)
    }

    private func deadlineView(_ deadline: Date) -> some View {
        let accessibility = AttentionAccessibility.deadline(item, deadline: deadline)

        return Label("Due \(deadline.relativeFormatted)", systemImage: "clock.badge.exclamationmark")
            .font(.footnote.weight(.semibold))
            .foregroundStyle(item.severity == .critical ? Color.pfError : Color.pfWarning)
            .fixedSize(horizontal: false, vertical: true)
            .accessibilityLabel(accessibility.label)
            .accessibilityHint(accessibility.hint)
            .accessibilityIdentifier(accessibility.identifier)
    }

    @ViewBuilder
    private func mediaView(state: AttentionMediaState) -> some View {
        Group {
            switch state {
            case .idle:
                mediaLoadButton(state: state)
            case .loading:
                mediaLoading(state: state)
            case .available(let data):
                mediaImage(data: data)
            case .unavailable(let message):
                mediaUnavailable(message: message)
            }
        }
        .task(id: mediaRequestID) {
            guard state == .idle else { return }
            _ = await onLoadMedia()
        }
    }

    private func mediaLoadButton(state: AttentionMediaState) -> some View {
        let accessibility = AttentionAccessibility.media(item: item, state: state)

        return Button(action: onRetryMedia) {
            Label("Load camera snapshot", systemImage: "camera")
                .frame(maxWidth: .infinity, alignment: .leading)
        }
        .buttonStyle(.bordered)
        .accessibilityLabel(accessibility.label)
        .accessibilityHint(accessibility.hint)
        .accessibilityIdentifier(accessibility.identifier)
    }

    private func mediaLoading(state: AttentionMediaState) -> some View {
        let accessibility = AttentionAccessibility.media(item: item, state: state)

        return HStack(spacing: 10) {
            ProgressView()
            Text("Loading camera snapshot…")
                .font(.footnote)
                .fixedSize(horizontal: false, vertical: true)
        }
        .frame(maxWidth: .infinity, minHeight: 80, alignment: .center)
        .accessibilityElement(children: .ignore)
        .accessibilityLabel(accessibility.label)
        .accessibilityHint(accessibility.hint)
        .accessibilityIdentifier(accessibility.identifier)
    }

    @ViewBuilder
    private func mediaImage(data: Data) -> some View {
        let accessibility = AttentionAccessibility.media(
            item: item,
            state: .available(data)
        )

        #if canImport(UIKit)
        if let image = UIImage(data: data) {
            Image(uiImage: image)
                .resizable()
                .scaledToFit()
                .clipShape(RoundedRectangle(cornerRadius: 10))
                .accessibilityLabel(accessibility.label)
                .accessibilityHint(accessibility.hint)
                .accessibilityIdentifier(accessibility.identifier)
        } else {
            mediaUnavailable(message: "The camera returned data that is not a displayable image.")
        }
        #elseif canImport(AppKit)
        if let image = NSImage(data: data) {
            Image(nsImage: image)
                .resizable()
                .scaledToFit()
                .clipShape(RoundedRectangle(cornerRadius: 10))
                .accessibilityLabel(accessibility.label)
                .accessibilityHint(accessibility.hint)
                .accessibilityIdentifier(accessibility.identifier)
        } else {
            mediaUnavailable(message: "The camera returned data that is not a displayable image.")
        }
        #else
        mediaUnavailable(message: "Camera images are unavailable on this platform.")
        #endif
    }

    private func mediaUnavailable(message: String) -> some View {
        let accessibility = AttentionAccessibility.media(
            item: item,
            state: .unavailable(message)
        )

        return VStack(alignment: .leading, spacing: 8) {
            Label("Camera snapshot unavailable", systemImage: "photo.badge.exclamationmark")
                .font(.footnote.weight(.semibold))
            Text(message)
                .font(.caption)
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)
            Button("Retry camera snapshot", action: onRetryMedia)
                .buttonStyle(.bordered)
                .accessibilityLabel("Retry camera snapshot")
                .accessibilityHint(
                    "Makes one new failure snapshot request for \(item.printerName)."
                )
                .accessibilityIdentifier(
                    "attention.item.\(item.id).media.retry"
                )
        }
        .frame(maxWidth: .infinity, minHeight: 100, alignment: .leading)
        .padding(12)
        .background(Color.secondary.opacity(0.08), in: RoundedRectangle(cornerRadius: 10))
        .accessibilityElement(children: .contain)
        .accessibilityLabel(accessibility.label)
        .accessibilityHint(accessibility.hint)
        .accessibilityIdentifier(accessibility.identifier)
    }

    private var navigationLinks: some View {
        let layout = dynamicTypeSize.isAccessibilitySize
            ? AnyLayout(VStackLayout(alignment: .leading, spacing: 8))
            : AnyLayout(HStackLayout(alignment: .center, spacing: 8))

        return layout {
            navigationButton(
                title: "Printer",
                systemImage: "printer",
                destination: navigationTargets.printer
            )
            if let job = navigationTargets.job {
                navigationButton(
                    title: "Job",
                    systemImage: "doc.text",
                    destination: job
                )
            }
        }
    }

    private func navigationButton(
        title: String,
        systemImage: String,
        destination: AppDestination
    ) -> some View {
        let accessibility = AttentionAccessibility.navigation(
            item: item,
            destination: destination
        )

        return Button {
            router.notificationsPath.append(destination)
        } label: {
            Label(title, systemImage: systemImage)
                .frame(maxWidth: .infinity)
        }
        .buttonStyle(.bordered)
        .accessibilityLabel(accessibility.label)
        .accessibilityHint(accessibility.hint)
        .accessibilityAddTraits(.isButton)
        .accessibilityIdentifier(accessibility.identifier)
    }

    @ViewBuilder
    private var actionControls: some View {
        VStack(alignment: .leading, spacing: 10) {
            ForEach(Array(supportedActions.enumerated()), id: \.offset) { _, action in
                actionButton(action)
            }

            switch actionState {
            case .idle:
                EmptyView()
            case .inProgress(let kind):
                actionProgress(kind)
            case .failed(let failure):
                actionFailure(failure)
            case .refreshPending(let pending):
                actionRefresh(pending)
            }
        }
    }

    private func actionButton(_ action: AttentionAction) -> some View {
        let accessibility = AttentionAccessibility.action(item: item, action: action)

        return Button(role: buttonRole(for: action.kind)) {
            onAction(action)
        } label: {
            HStack {
                Label(action.label, systemImage: actionIcon(action.kind))
                Spacer(minLength: 8)
                if action.requiresConfirmation {
                    Image(systemName: "checkmark.shield")
                        .accessibilityHidden(true)
                }
            }
            .frame(maxWidth: .infinity, minHeight: 44, alignment: .leading)
        }
        .buttonStyle(.borderedProminent)
        .tint(actionTint(action.kind))
        .disabled(isActionBlocked(action.kind))
        .accessibilityLabel(accessibility.label)
        .accessibilityHint(accessibility.hint)
        .accessibilityIdentifier(accessibility.identifier)
    }

    private func actionProgress(_ kind: AttentionActionKind) -> some View {
        let accessibility = AttentionAccessibility.actionProgress(
            item: item,
            actionKind: kind
        )

        return HStack(spacing: 10) {
            ProgressView()
            Text(accessibility.label)
                .font(.footnote.weight(.semibold))
                .fixedSize(horizontal: false, vertical: true)
        }
        .accessibilityElement(children: .ignore)
        .accessibilityLabel(accessibility.label)
        .accessibilityHint(accessibility.hint)
        .accessibilityIdentifier(accessibility.identifier)
    }

    private func actionFailure(_ failure: AttentionActionFailure) -> some View {
        let accessibility = AttentionAccessibility.actionError(
            item: item,
            failure: failure
        )

        return VStack(alignment: .leading, spacing: 8) {
            Label("Action failed", systemImage: "exclamationmark.triangle.fill")
                .font(.footnote.weight(.semibold))
                .foregroundStyle(Color.pfError)
            Text(failure.message)
                .font(.caption)
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)
            Button("Retry \(failure.action.label)") {
                onRetryAction(failure)
            }
            .buttonStyle(.bordered)
            .accessibilityLabel("Retry \(failure.action.label)")
            .accessibilityHint("Repeats the failed server request once.")
            .accessibilityIdentifier("attention.item.\(item.id).action.retry")
        }
        .padding(12)
        .background(Color.pfError.opacity(0.08), in: RoundedRectangle(cornerRadius: 10))
        .accessibilityElement(children: .contain)
        .accessibilityLabel(accessibility.label)
        .accessibilityHint(accessibility.hint)
        .accessibilityIdentifier(accessibility.identifier)
    }

    private func actionRefresh(
        _ pending: AttentionActionRefreshPending
    ) -> some View {
        let accessibility = AttentionAccessibility.actionRefresh(
            item: item,
            pending: pending
        )

        return VStack(alignment: .leading, spacing: 8) {
            if let message = pending.message {
                Label("Action completed, refresh failed", systemImage: "arrow.clockwise.circle")
                    .font(.footnote.weight(.semibold))
                    .foregroundStyle(Color.pfWarning)
                Text(message)
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
                Button("Retry refresh") {
                    onRetryActionRefresh(pending)
                }
                .buttonStyle(.bordered)
                .accessibilityLabel("Retry attention refresh")
                .accessibilityHint(
                    "Refreshes canonical server truth without repeating \(pending.action.label)."
                )
                .accessibilityIdentifier(
                    "attention.item.\(item.id).action.refreshRetry"
                )
            } else {
                HStack(spacing: 10) {
                    ProgressView()
                    Text("Action completed. Refreshing attention…")
                        .font(.footnote.weight(.semibold))
                        .fixedSize(horizontal: false, vertical: true)
                }
            }
        }
        .padding(12)
        .background(Color.pfWarning.opacity(0.08), in: RoundedRectangle(cornerRadius: 10))
        .accessibilityElement(children: .contain)
        .accessibilityLabel(accessibility.label)
        .accessibilityHint(accessibility.hint)
        .accessibilityIdentifier(accessibility.identifier)
    }

    private func isActionBlocked(_ kind: AttentionActionKind) -> Bool {
        switch actionState {
        case .inProgress, .refreshPending:
            return true
        case .failed(let failure):
            return failure.action.kind == kind
        case .idle:
            return false
        }
    }

    private var severityText: String {
        switch item.severity {
        case .critical: "Critical"
        case .warning: "Warning"
        case .info: "Info"
        case .unknown: "Other"
        }
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
        case .info, .unknown: .pfSecondaryAccent
        }
    }

    private func actionIcon(_ kind: AttentionActionKind) -> String {
        switch kind {
        case .pause: "pause.fill"
        case .resume: "play.fill"
        case .cancel: "xmark.octagon"
        case .acknowledge: "checkmark"
        case .resolve: "checkmark.circle"
        case .dismiss: "eye.slash"
        case .snooze: "clock"
        case .harvest: "tray.and.arrow.up"
        case .unknown: "questionmark"
        }
    }

    private func actionTint(_ kind: AttentionActionKind) -> Color {
        switch kind {
        case .cancel, .dismiss: .pfError
        default: .accentColor
        }
    }

    private func buttonRole(for kind: AttentionActionKind) -> ButtonRole? {
        switch kind {
        case .cancel, .dismiss: .destructive
        default: nil
        }
    }
}
