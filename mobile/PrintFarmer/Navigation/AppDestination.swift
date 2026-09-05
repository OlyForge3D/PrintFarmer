import Foundation

enum AppDestination: Hashable {
    case printerDetail(id: UUID)
    case jobDetail(id: UUID)
    case createJob
    case createPrinter
    case account
    case dashboard
    case maintenance
    case notifications
    case settings
    case navigationSettings
    case maintenanceAnalytics
    case uptimeReliability
    case filamentCoverage
    case predictiveInsights(printerId: UUID?)
    case jobQueue
    case jobHistory
    case jobTimeline
    case dispatchDashboard
    case locations
    case offlineQueue
    case manageServers
    /// Advanced printer controls (jog, preheat, z-offset, disable motors).
    /// F1 (#706) moves these off the printer detail scroll and gates them
    /// behind an "Advanced" navigation destination inside Printer Detail.
    case advancedPrinterControls(printerId: UUID)
}

/// Navigation shells supported by the tab model.
///
/// `current` keeps the shipping operator UI unchanged while the
/// adaptive shells are introduced incrementally by the IOS navigation epic.
enum NavigationShell: Hashable, CaseIterable, Sendable {
    case current
    case simple
    case twoModes
}

enum OversightMode: Hashable, CaseIterable, Sendable {
    case floor
    case oversight
}

enum SidebarSection: String, CaseIterable, Sendable {
    case floor
    case oversight

    var title: String {
        rawValue.capitalized
    }
}

/// Every tab addressable by the current and adaptive navigation shells.
enum AppTab: String, Hashable, CaseIterable, Sendable {
    enum BadgeKind: Hashable {
        case none
        case notifications
        case pendingReady
    }

    case attention
    case farm
    case tasks
    case inventory
    case oversight
    case overview
    case fleet
    case jobs
    case upkeep
    case reports

    static func tabs(
        for shell: NavigationShell,
        mode: OversightMode
    ) -> [AppTab] {
        switch shell {
        case .current:
            [.attention, .farm, .tasks, .inventory]
        case .simple:
            [.attention, .farm, .inventory, .oversight]
        case .twoModes:
            switch mode {
            case .floor:
                [.attention, .farm, .tasks, .inventory]
            case .oversight:
                [.overview, .fleet, .jobs, .upkeep, .reports]
            }
        }
    }

    func isEnabled(in capabilities: ResolvedSystemCapabilities) -> Bool {
        switch self {
        case .attention:
            capabilities.attentionEnabled
        case .tasks:
            capabilities.shiftPlanEnabled
        case .oversight:
            OversightCatalog.hasVisibleDestinations(capabilities: capabilities)
        case .overview:
            OversightCatalog.isRootVisible(.overview, capabilities: capabilities)
        case .fleet:
            OversightCatalog.isRootVisible(.fleet, capabilities: capabilities)
        case .jobs:
            OversightCatalog.isRootVisible(.jobs, capabilities: capabilities)
        case .upkeep:
            OversightCatalog.isRootVisible(.upkeep, capabilities: capabilities)
        case .reports:
            OversightCatalog.isRootVisible(.reports, capabilities: capabilities)
        case .farm, .inventory:
            true
        }
    }

    static func visibleTabs(
        for shell: NavigationShell,
        mode: OversightMode,
        capabilities: ResolvedSystemCapabilities,
        oversightAvailability: OversightNavigationAvailability = .fullyAvailable
    ) -> [AppTab] {
        tabs(for: shell, mode: mode).filter { tab in
            guard tab.isEnabled(in: capabilities) else { return false }

            switch tab {
            case .oversight:
                return oversightAvailability.hasVisibleHubDestinations
            case .overview, .fleet, .jobs, .upkeep, .reports:
                return oversightAvailability.visibleTabs.contains(tab)
            case .attention, .farm, .tasks, .inventory:
                return true
            }
        }
    }

    static func visibleTabs(
        in section: SidebarSection,
        for shell: NavigationShell,
        capabilities: ResolvedSystemCapabilities,
        oversightAvailability: OversightNavigationAvailability = .fullyAvailable
    ) -> [AppTab] {
        switch section {
        case .floor:
            let floorShell = shell == .simple ? NavigationShell.simple : .twoModes
            return visibleTabs(
                for: floorShell,
                mode: .floor,
                capabilities: capabilities,
                oversightAvailability: oversightAvailability
            ).filter { $0 != .oversight }
        case .oversight:
            return visibleTabs(
                for: .twoModes,
                mode: .oversight,
                capabilities: capabilities,
                oversightAvailability: oversightAvailability
            )
        }
    }

    static func fallbackTab(
        for shell: NavigationShell,
        mode: OversightMode,
        capabilities: ResolvedSystemCapabilities,
        oversightAvailability: OversightNavigationAvailability = .fullyAvailable
    ) -> AppTab {
        visibleTabs(
            for: shell,
            mode: mode,
            capabilities: capabilities,
            oversightAvailability: oversightAvailability
        ).first ?? tabs(for: shell, mode: mode).first ?? .farm
    }

    /// Compatibility helpers for callers that intentionally target today's
    /// operator shell.
    static func visibleTabs(
        for capabilities: ResolvedSystemCapabilities
    ) -> [AppTab] {
        visibleTabs(
            for: .current,
            mode: .floor,
            capabilities: capabilities
        )
    }

    static func fallbackTab(
        for capabilities: ResolvedSystemCapabilities
    ) -> AppTab {
        fallbackTab(
            for: .current,
            mode: .floor,
            capabilities: capabilities
        )
    }

    var title: String {
        switch self {
        case .attention:
            "Attention"
        case .farm:
            "Farm"
        case .tasks:
            "Tasks"
        case .inventory:
            "Inventory"
        case .oversight:
            "Oversight"
        case .overview:
            "Overview"
        case .fleet:
            "Fleet"
        case .jobs:
            "Jobs"
        case .upkeep:
            "Upkeep"
        case .reports:
            "Reports"
        }
    }

    var systemImage: String {
        switch self {
        case .attention:
            "bell.badge"
        case .farm:
            "printer"
        case .tasks:
            "checklist"
        case .inventory:
            "cylinder.fill"
        case .oversight:
            "chart.bar.xaxis"
        case .overview:
            "rectangle.grid.2x2"
        case .fleet:
            "printer"
        case .jobs:
            "list.bullet.rectangle"
        case .upkeep:
            "wrench.and.screwdriver"
        case .reports:
            "chart.bar.doc.horizontal"
        }
    }

    var badgeKind: BadgeKind {
        switch self {
        case .attention:
            .notifications
        case .farm:
            .pendingReady
        case .tasks, .inventory, .oversight,
             .overview, .fleet, .jobs, .upkeep, .reports:
            .none
        }
    }

    var tabAccessibilityIdentifier: String {
        "tab.\(rawValue)"
    }

    var sidebarAccessibilityIdentifier: String {
        "sidebar.\(rawValue)"
    }
}

extension AppRouter {
    var printerDestinationTab: AppTab {
        if presentsExpandedSidebar {
            switch selectedTab {
            case .overview, .fleet, .jobs, .upkeep, .reports:
                return .fleet
            case .attention, .farm, .tasks, .inventory:
                return .farm
            case .oversight:
                break
            }
        }

        return activeShell == .twoModes && activeMode == .oversight
            ? .fleet
            : .farm
    }

    func visibleTabs(
        in sidebarSection: SidebarSection,
        for capabilities: ResolvedSystemCapabilities
    ) -> [AppTab] {
        AppTab.visibleTabs(
            in: sidebarSection,
            for: activeShell,
            capabilities: capabilities,
            oversightAvailability: oversightAvailability
        )
    }

    func shouldShowModeControl(for tab: AppTab) -> Bool {
        activeShell == .twoModes
            && !presentsExpandedSidebar
            && oversightAvailability.supportsTwoModes
            && isAtRoot(tab)
    }

    func setExpandedSidebarPresentation(
        _ isExpanded: Bool,
        capabilities: ResolvedSystemCapabilities,
        oversightAvailability: OversightNavigationAvailability
    ) {
        guard presentsExpandedSidebar != isExpanded else { return }

        if !isExpanded, activeShell == .twoModes {
            let oversightTabs = AppTab.visibleTabs(
                in: .oversight,
                for: activeShell,
                capabilities: capabilities,
                oversightAvailability: oversightAvailability
            )
            let compactMode: OversightMode = oversightTabs.contains(selectedTab)
                ? .oversight
                : .floor
            setNavigationMode(compactMode, capabilities: capabilities)
        }

        presentsExpandedSidebar = isExpanded
        reconcileCapabilities(
            capabilities,
            oversightAvailability: oversightAvailability
        )
    }

    func routeToJobQueue(capabilities: ResolvedSystemCapabilities) {
        let tabs = visibleTabs(for: capabilities)

        if tabs.contains(.jobs) {
            selectedTab = .jobs
            resetToRoot(tab: .jobs)
            oversightJobsPath.append(AppDestination.jobQueue)
        } else if tabs.contains(.oversight) {
            selectedTab = .oversight
            resetToRoot(tab: .oversight)
            oversightPath.append(AppDestination.jobQueue)
        } else if tabs.contains(.tasks) {
            selectedTab = .tasks
            resetToRoot(tab: .tasks)
            tasksPath.append(AppDestination.jobQueue)
        } else {
            selectedTab = fallbackTab(for: capabilities)
        }
    }
}
