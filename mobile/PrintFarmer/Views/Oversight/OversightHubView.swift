import SwiftUI

struct OversightHubView: View {
    static let minimumRowHeight: CGFloat = 44

    @Environment(AppRouter.self) private var router
    @Environment(ServiceContainer.self) private var services
    @Environment(ServerRegistry.self) private var serverRegistry
    @State private var showsUpgradeOffer = false
    @State private var offerServerID: UUID?
    @State private var subtitleContext = OversightRowSubtitleContext.unknown

    private var capabilities: ResolvedSystemCapabilities {
        services.capabilitiesService.resolved
    }

    private var sections: [OversightSectionModel] {
        OversightCatalog.simpleSections(for: capabilities)
    }

    /// Combine the fetched attention/maintenance signals with router
    /// state that is already observed by SwiftUI. Reading the router
    /// fields here keeps the navigation-settings subtitle in sync
    /// without a redundant fetch.
    ///
    /// We also capture live inputs to `NavigationShellDerivation.automatic`
    /// (latest farm shape, current shift-plan capability, current admin
    /// flag) into `automaticInputs`. This lets the derivation recompute
    /// a fresh Automatic reading here rather than trust the router's
    /// stored `establishedAutomaticDerivation`, which
    /// `configureAdaptiveShell` only recomputes on server/user context
    /// change — so a mid-session shift-plan toggle or admin flip would
    /// otherwise leave the subtitle stale (#2449 review round 2).
    /// All three sources are `@Observable`, so SwiftUI re-renders this
    /// view whenever they change.
    private var resolvedSubtitleContext: OversightRowSubtitleContext {
        oversightRowSubtitleContext(
            base: subtitleContext,
            router: router,
            capabilities: capabilities,
            farmShape: services.farmShapeService.latestShape
        )
    }

    var body: some View {
        @Bindable var router = router

        List {
            if showsUpgradeOffer {
                Section {
                    OversightUpgradeOfferCard(
                        context: OversightUpgradeOfferContext(
                            farmShape: services.farmShapeService.latestShape,
                            shiftPlanEnabled: capabilities.shiftPlanEnabled
                        ),
                        turnOn: acceptUpgradeOffer,
                        notNow: dismissUpgradeOffer
                    )
                }
            }
            ForEach(sections) { sectionModel in
                Section(sectionModel.section.title) {
                    ForEach(sectionModel.destinations) { destination in
                        OversightNavigationRow(
                            destination: destination,
                            subtitle: OversightRowSubtitles.subtitle(
                                for: destination,
                                in: resolvedSubtitleContext
                            )
                        )
                    }
                }
            }
        }
        .listStyle(.insetGrouped)
        .navigationTitle("Oversight")
        .rootNavigationChrome(for: .oversight)
        .navigationDestination(for: AppDestination.self) { destination in
            destinationView(for: destination)
        }
        .task(id: sections.isEmpty) {
            guard sections.isEmpty else { return }
            router.resetToRoot(tab: .oversight)
            router.selectTab(
                router.fallbackTab(for: capabilities),
                capabilities: capabilities
            )
        }
        .task(id: subtitleRefreshKey) {
            await refreshSubtitleContext()
        }
        .refreshable {
            await refreshSubtitleContext()
        }
        .onAppear(perform: refreshUpgradeOffer)
        .onChange(of: services.farmShapeService.latestShape) {
            refreshUpgradeOffer()
        }
        .onChange(of: capabilities.shiftPlanEnabled) {
            refreshUpgradeOffer()
        }
        .onChange(of: serverRegistry.activeServerID) {
            refreshUpgradeOffer()
        }
        .onChange(of: serverRegistry.navigationLayoutPreference) {
            refreshUpgradeOffer()
        }
        .onChange(of: router.activeShell) {
            refreshUpgradeOffer()
        }
        .onChange(of: router.configuredServerID) {
            refreshUpgradeOffer()
        }
        .onChange(of: router.configuredIsFarmAdmin) {
            refreshUpgradeOffer()
        }
        .onChange(of: router.appliedNavigationPreference) {
            refreshUpgradeOffer()
        }
        .accessibilityIdentifier("oversight.hub")
    }

    private func refreshUpgradeOffer() {
        guard let activeServerID = serverRegistry.activeServerID else {
            showsUpgradeOffer = false
            offerServerID = nil
            return
        }
        if offerServerID != activeServerID {
            showsUpgradeOffer = false
            offerServerID = nil
        }
        guard router.activeShell == .simple,
              router.configuredServerID == activeServerID,
              router.configuredIsFarmAdmin,
              router.oversightAvailability.supportsTwoModes else {
            showsUpgradeOffer = false
            offerServerID = nil
            return
        }
        showsUpgradeOffer = serverRegistry.observeOversightUpgradeOffer(
            farmShape: services.farmShapeService.latestShape,
            shiftPlanEnabled: capabilities.shiftPlanEnabled,
            isFarmAdmin: router.configuredIsFarmAdmin
        )
        offerServerID = showsUpgradeOffer ? activeServerID : nil
    }

    private func acceptUpgradeOffer() {
        guard let offerServerID,
              router.configuredIsFarmAdmin,
              serverRegistry.acceptOversightUpgradeOffer(
                  for: offerServerID,
                  isFarmAdmin: router.configuredIsFarmAdmin
              ) else {
            refreshUpgradeOffer()
            return
        }
        showsUpgradeOffer = false
        self.offerServerID = nil
    }

    private func dismissUpgradeOffer() {
        guard let offerServerID, router.configuredIsFarmAdmin else {
            refreshUpgradeOffer()
            return
        }
        serverRegistry.dismissOversightUpgradeOffer(for: offerServerID)
        showsUpgradeOffer = false
        self.offerServerID = nil
    }

    /// Fetch the honest signals feeding the hub's row subtitles. Both
    /// fetches use `try?` — a failure keeps the previously-loaded field
    /// (or `nil`) in place so the derivation falls back to the static
    /// description rather than surfacing a stale count. The maintenance
    /// endpoint is safe on any server that publishes upcoming tasks; the
    /// attention endpoint is skipped when the capability gate is off.
    ///
    /// This runs inside `.task(id: subtitleRefreshKey)`, which changes on:
    /// * `services.activeServerGeneration` — server / user context change.
    /// * `capabilities.attentionEnabled` — same-server feature toggle.
    ///
    /// The very first thing we do on every run is clear the previous
    /// snapshot back to `.unknown`. That guarantees:
    /// * A server switch never briefly renders the outgoing server's
    ///   attention count next to the incoming server's chrome.
    /// * When a capability transitions off mid-session, the stale
    ///   `attentionItemCount` is immediately cleared to `nil` instead of
    ///   being displayed for a feature that is no longer enabled — the
    ///   derivation falls back to the descriptive subtitle, which is the
    ///   honest unknown reading (issue #2449 review round 4).
    /// * A user-initiated pull-to-refresh (`.refreshable`) or return to
    ///   the hub re-runs the same clear-then-fetch sequence, so
    ///   same-server data mutations refresh the visible subtitles.
    private func refreshSubtitleContext() async {
        subtitleContext = .unknown

        var context = OversightRowSubtitleContext.unknown

        if capabilities.attentionEnabled {
            if let feed = try? await services.attentionService.getFeed(
                cursor: nil,
                limit: nil
            ) {
                context.attentionItemCount = feed.items.count
                context.attentionHasMorePages = (feed.nextCursor != nil)
                context.healthyPrinterCount = feed.healthyPrinterCount
            }
        }

        if let tasks = try? await services.maintenanceService.getUpcoming(
            lookaheadDays: 14,
            includeOverdue: true,
            printerId: nil
        ) {
            context.upcomingMaintenance = tasks
        }

        if Task.isCancelled { return }
        subtitleContext = context
    }

    /// Composite refresh trigger for `.task(id:)`. Any change here
    /// re-runs `refreshSubtitleContext`, which first clears
    /// `subtitleContext` to `.unknown` and then refetches — so the
    /// visible subtitle never lingers on a value that is no longer
    /// authoritative for the current server + capability state.
    private var subtitleRefreshKey: OversightSubtitleRefreshKey {
        OversightSubtitleRefreshKey(
            serverGeneration: services.activeServerGeneration,
            attentionEnabled: capabilities.attentionEnabled
        )
    }
}

/// Composite key that gates `OversightHubView.refreshSubtitleContext`
/// via `.task(id:)`. When any field differs from the previous value,
/// SwiftUI cancels the current fetch and re-runs, which first clears
/// `subtitleContext` back to `.unknown` and then refetches — so the
/// visible subtitle can never linger on a value that is no longer
/// authoritative for the current server + capability state.
///
/// Fields:
/// * `serverGeneration` — bumps on server / user context change,
///   the round-1 gate against cross-server bleed-through.
/// * `attentionEnabled` — flipping this off must clear
///   `attentionItemCount` immediately; the fetch is skipped when
///   the capability is off, so the `.unknown` clear stands and the
///   subtitle falls back to the descriptive catalog copy. Flipping
///   it on re-runs the fetch and repopulates the count.
///
/// Exposed at file scope (not nested private) so the test target can
/// exercise the equatable invariants that drive `.task(id:)`
/// invalidation without needing SwiftUI view-hosting infrastructure
/// (issue #2449 review round 4).
struct OversightSubtitleRefreshKey: Equatable {
    let serverGeneration: Int
    let attentionEnabled: Bool
}

struct OversightTabRootView: View {
    static let supportedTabs: [AppTab] = [
        .oversight,
        .overview,
        .fleet,
        .jobs,
        .upkeep,
        .reports
    ]

    let tab: AppTab

    @Environment(AppRouter.self) private var router
    @Environment(ServiceContainer.self) private var services

    var body: some View {
        @Bindable var router = router
        let capabilities = services.capabilitiesService.resolved

        switch tab {
        case .oversight:
            NavigationStack(path: $router.oversightPath) {
                OversightHubView()
            }
        case .overview:
            OversightDestinationListRoot(
                root: .overview,
                destinations: OversightCatalog.destinations(
                    for: .overview,
                    capabilities: capabilities
                ),
                path: $router.overviewPath
            )
        case .fleet:
            PrinterListView(navigationContext: .fleet)
        case .jobs:
            OversightDestinationListRoot(
                root: .jobs,
                destinations: OversightCatalog.destinations(
                    for: .jobs,
                    capabilities: capabilities
                ),
                path: $router.oversightJobsPath
            )
        case .upkeep:
            OversightDestinationListRoot(
                root: .upkeep,
                destinations: OversightCatalog.destinations(
                    for: .upkeep,
                    capabilities: capabilities
                ),
                path: $router.upkeepPath
            )
        case .reports:
            OversightDestinationListRoot(
                root: .reports,
                destinations: OversightCatalog.destinations(
                    for: .reports,
                    capabilities: capabilities
                ),
                path: $router.reportsPath
            )
        case .attention, .farm, .tasks, .inventory:
            Color.clear
                .accessibilityHidden(true)
        }
    }
}

@MainActor @ViewBuilder
func oversightTabContentView(for tab: AppTab) -> some View {
    OversightTabRootView(tab: tab)
}

private struct OversightDestinationListRoot: View {
    let root: OversightRoot
    let destinations: [OversightDestination]
    @Binding var path: NavigationPath

    @Environment(AppRouter.self) private var router
    @Environment(ServiceContainer.self) private var services

    private var capabilities: ResolvedSystemCapabilities {
        services.capabilitiesService.resolved
    }

    /// Two-modes tab roots do not run the attention/maintenance
    /// fetches the simple hub does, so `upcomingMaintenance` and
    /// `attentionItemCount` stay `nil` here — the derivation
    /// module's honest-unknown fallback returns
    /// `destination.subtitle` for those rows, preserving today's
    /// visible behavior on Overview/Jobs/Upkeep. Router-derived
    /// state (navigation preference + live automatic inputs) is
    /// populated so the Reports > Navigation settings row shows
    /// the same live Automatic reading it does on the simple hub.
    /// This routes every row through
    /// `OversightRowSubtitles.subtitle(...)`, giving one authoritative
    /// derivation for the whole Oversight surface (#2449).
    private var subtitleContext: OversightRowSubtitleContext {
        oversightRowSubtitleContext(
            base: .unknown,
            router: router,
            capabilities: capabilities,
            farmShape: services.farmShapeService.latestShape
        )
    }

    var body: some View {
        NavigationStack(path: $path) {
            List(destinations) { destination in
                OversightNavigationRow(
                    destination: destination,
                    subtitle: OversightRowSubtitles.subtitle(
                        for: destination,
                        in: subtitleContext
                    )
                )
            }
            .listStyle(.insetGrouped)
            .navigationTitle(root.title)
            .rootNavigationChrome(for: root.appTab)
            .navigationDestination(for: AppDestination.self) { destination in
                destinationView(for: destination)
            }
            .accessibilityIdentifier(root.accessibilityIdentifier)
        }
    }
}

/// Shared subtitle-context builder used by both the simple hub and the
/// two-modes tab roots. Layers router-derived state onto whatever base
/// context the caller has already assembled (typically the simple hub's
/// fetched attention/maintenance snapshot, or `.unknown` for the
/// fetch-less two-modes tab roots).
///
/// `AppRouter`, `ResolvedSystemCapabilities`, and `FarmShapeService` are
/// all `@Observable`, so any view calling this rebuilds automatically
/// when the underlying router or capability state changes.
@MainActor
private func oversightRowSubtitleContext(
    base: OversightRowSubtitleContext,
    router: AppRouter,
    capabilities: ResolvedSystemCapabilities,
    farmShape: FarmShape?
) -> OversightRowSubtitleContext {
    var context = base
    context.navigationPreference = router.appliedNavigationPreference
    context.automaticDerivation = router.establishedAutomaticDerivation
    context.automaticInputs = AutomaticInputsSnapshot(
        farmShape: farmShape,
        shiftPlanEnabled: capabilities.shiftPlanEnabled,
        isFarmAdmin: router.configuredIsFarmAdmin
    )
    return context
}

private struct OversightNavigationRow: View {
    let destination: OversightDestination
    let subtitle: String

    var body: some View {
        NavigationLink(value: destination.appDestination) {
            HStack(alignment: .center, spacing: 12) {
                Image(systemName: destination.systemImage)
                    .font(.title3)
                    .foregroundStyle(Color.accentColor)
                    .frame(width: 28)
                    .accessibilityHidden(true)

                VStack(alignment: .leading, spacing: 3) {
                    Text(destination.title)
                        .font(.body.weight(.semibold))
                    Text(subtitle)
                        .font(.subheadline)
                        .foregroundStyle(.secondary)
                }
                .fixedSize(horizontal: false, vertical: true)
            }
            .padding(.vertical, 4)
            .frame(minHeight: OversightHubView.minimumRowHeight)
        }
        .accessibilityLabel(destination.title)
        .accessibilityValue(subtitle)
        .accessibilityHint(destination.accessibilityHint)
        .accessibilityIdentifier(destination.accessibilityIdentifier)
    }
}
