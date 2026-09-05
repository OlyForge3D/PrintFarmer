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
        var context = subtitleContext
        context.navigationPreference = router.appliedNavigationPreference
        context.automaticDerivation = router.establishedAutomaticDerivation
        context.automaticInputs = AutomaticInputsSnapshot(
            farmShape: services.farmShapeService.latestShape,
            shiftPlanEnabled: capabilities.shiftPlanEnabled,
            isFarmAdmin: router.configuredIsFarmAdmin
        )
        return context
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
        .task(id: services.activeServerGeneration) {
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
    /// This runs inside `.task(id: services.activeServerGeneration)`, so
    /// the very first thing we do is clear the previous server's context
    /// back to `.unknown`. That guarantees a server switch never
    /// briefly renders the outgoing server's attention count next to the
    /// incoming server's chrome; during the fetch the rows fall back to
    /// their descriptive catalog subtitles, which is honest.
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

    var body: some View {
        NavigationStack(path: $path) {
            List(destinations) { destination in
                // Two-modes tabs keep the static descriptive subtitle.
                // The data-driven derivation is intentionally scoped to
                // the simple hub (issue #2449); adding it here would
                // require the same per-appearance fetch on every tab
                // root, which is out of scope for this pass.
                OversightNavigationRow(
                    destination: destination,
                    subtitle: destination.subtitle
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
