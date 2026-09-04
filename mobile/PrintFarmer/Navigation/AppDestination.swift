import Foundation

enum AppDestination: Hashable {
    case printerDetail(id: UUID)
    case jobDetail(id: UUID)
    case createJob
    case createPrinter
    case dashboard
    case maintenance
    case notifications
    case settings
    case navigationSettings
    case maintenanceAnalytics
    case uptimeReliability
    case predictiveInsights(printerId: UUID?)
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
/// `current` keeps the shipping five-tab operator UI unchanged while the
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
    case scan
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
            [.attention, .farm, .tasks, .scan, .inventory]
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
        case .farm, .scan, .inventory, .oversight,
             .overview, .fleet, .jobs, .upkeep, .reports:
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
            case .attention, .farm, .tasks, .scan, .inventory:
                return true
            }
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
    /// five-tab operator shell.
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
        case .scan:
            "Scan"
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
        case .scan:
            "barcode.viewfinder"
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
        case .tasks, .scan, .inventory, .oversight,
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
