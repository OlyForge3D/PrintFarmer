import SwiftUI

struct OversightHubView: View {
    static let minimumRowHeight: CGFloat = 44

    @Environment(AppRouter.self) private var router
    @Environment(ServiceContainer.self) private var services

    private var capabilities: ResolvedSystemCapabilities {
        services.capabilitiesService.resolved
    }

    private var sections: [OversightSectionModel] {
        OversightCatalog.simpleSections(for: capabilities)
    }

    var body: some View {
        @Bindable var router = router

        List {
            ForEach(sections) { sectionModel in
                Section(sectionModel.section.title) {
                    ForEach(sectionModel.destinations) { destination in
                        OversightNavigationRow(destination: destination)
                    }
                }
            }
        }
        .listStyle(.insetGrouped)
        .navigationTitle("Oversight")
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
        .accessibilityIdentifier("oversight.hub")
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
        case .attention, .farm, .tasks, .scan, .inventory:
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
                OversightNavigationRow(destination: destination)
            }
            .listStyle(.insetGrouped)
            .navigationTitle(root.title)
            .navigationDestination(for: AppDestination.self) { destination in
                destinationView(for: destination)
            }
            .accessibilityIdentifier(root.accessibilityIdentifier)
        }
    }
}

private struct OversightNavigationRow: View {
    let destination: OversightDestination

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
                    Text(destination.subtitle)
                        .font(.subheadline)
                        .foregroundStyle(.secondary)
                }
                .fixedSize(horizontal: false, vertical: true)
            }
            .padding(.vertical, 4)
            .frame(minHeight: OversightHubView.minimumRowHeight)
        }
        .accessibilityLabel(destination.title)
        .accessibilityValue(destination.subtitle)
        .accessibilityHint(destination.accessibilityHint)
        .accessibilityIdentifier(destination.accessibilityIdentifier)
    }
}
