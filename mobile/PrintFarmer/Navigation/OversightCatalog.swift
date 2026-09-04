import Foundation

enum OversightDestination: String, CaseIterable, Hashable, Identifiable {
    case dashboard
    case dispatch
    case filamentCoverage
    case maintenance
    case maintenanceAnalytics
    case predictiveInsights
    case jobHistory
    case jobTimeline
    case locations
    case uptimeReliability
    case navigationSettings

    var id: String { rawValue }

    var title: String {
        switch self {
        case .dashboard:
            "Dashboard"
        case .dispatch:
            "Dispatch"
        case .filamentCoverage:
            "Filament Coverage"
        case .maintenance:
            "Maintenance"
        case .maintenanceAnalytics:
            "Maintenance Analytics"
        case .predictiveInsights:
            "Predictive Insights"
        case .jobHistory:
            "Job History"
        case .jobTimeline:
            "Job Timeline"
        case .locations:
            "Locations"
        case .uptimeReliability:
            "Uptime & Reliability"
        case .navigationSettings:
            "Settings → Navigation"
        }
    }

    var subtitle: String {
        switch self {
        case .dashboard:
            "Fleet health at a glance"
        case .dispatch:
            "Queued work and anything blocking it"
        case .filamentCoverage:
            "Fleet-wide material coverage and runout risk"
        case .maintenance:
            "Alerts and scheduled upkeep"
        case .maintenanceAnalytics:
            "Intervals, overdue trends, and maintenance history"
        case .predictiveInsights:
            "Printers trending toward a failure"
        case .jobHistory:
            "Completed, failed, and cancelled jobs"
        case .jobTimeline:
            "What ran when, printer by printer"
        case .locations:
            "Farm locations, bays, and printer placement"
        case .uptimeReliability:
            "Availability and failure rates"
        case .navigationSettings:
            "Choose Automatic, Simple, or Two modes"
        }
    }

    var systemImage: String {
        switch self {
        case .dashboard:
            "rectangle.grid.2x2"
        case .dispatch:
            "arrow.triangle.branch"
        case .filamentCoverage:
            "gauge.with.dots.needle.50percent"
        case .maintenance:
            "wrench.and.screwdriver"
        case .maintenanceAnalytics:
            "chart.xyaxis.line"
        case .predictiveInsights:
            "sparkles"
        case .jobHistory:
            "clock.arrow.circlepath"
        case .jobTimeline:
            "calendar"
        case .locations:
            "mappin.and.ellipse"
        case .uptimeReliability:
            "gauge.open.with.lines.needle.67percent.and.arrowtriangle"
        case .navigationSettings:
            "rectangle.3.group"
        }
    }

    var accessibilityIdentifier: String {
        "oversight.destination.\(rawValue)"
    }

    var accessibilityHint: String {
        "Navigates to the \(title) screen."
    }

    func isAvailable(in capabilities: ResolvedSystemCapabilities) -> Bool {
        switch self {
        case .filamentCoverage:
            capabilities.filamentCoverageEnabled
        case .dashboard, .dispatch, .maintenance, .maintenanceAnalytics,
             .predictiveInsights, .jobHistory, .jobTimeline, .locations,
             .uptimeReliability, .navigationSettings:
            true
        }
    }
}

enum OversightSection: String, CaseIterable, Hashable, Identifiable {
    case rightNow
    case upkeep
    case records
    case reports

    var id: String { rawValue }

    var title: String {
        switch self {
        case .rightNow:
            "Right now"
        case .upkeep:
            "Upkeep"
        case .records:
            "Records"
        case .reports:
            "Reports"
        }
    }

    fileprivate var destinations: [OversightDestination] {
        switch self {
        case .rightNow:
            [.dashboard, .dispatch, .filamentCoverage]
        case .upkeep:
            [.maintenance, .maintenanceAnalytics, .predictiveInsights]
        case .records:
            [.jobHistory, .jobTimeline, .locations]
        case .reports:
            [.uptimeReliability, .navigationSettings]
        }
    }
}

struct OversightSectionModel: Identifiable, Hashable {
    let section: OversightSection
    let destinations: [OversightDestination]

    var id: OversightSection { section }
}

enum OversightRoot: String, CaseIterable, Hashable, Identifiable {
    case overview
    case fleet
    case jobs
    case upkeep
    case reports

    var id: String { rawValue }

    var appTab: AppTab {
        switch self {
        case .overview:
            .overview
        case .fleet:
            .fleet
        case .jobs:
            .jobs
        case .upkeep:
            .upkeep
        case .reports:
            .reports
        }
    }

    var title: String {
        switch self {
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

    var accessibilityIdentifier: String {
        "oversight.root.\(rawValue)"
    }

    fileprivate var destinations: [OversightDestination] {
        switch self {
        case .overview:
            [.dashboard, .dispatch]
        case .fleet:
            []
        case .jobs:
            [.jobHistory, .jobTimeline]
        case .upkeep:
            [.maintenance, .maintenanceAnalytics, .predictiveInsights]
        case .reports:
            [.uptimeReliability, .filamentCoverage, .locations, .navigationSettings]
        }
    }
}

enum OversightCatalog {
    static func simpleSections(
        for capabilities: ResolvedSystemCapabilities
    ) -> [OversightSectionModel] {
        simpleSections { $0.isAvailable(in: capabilities) }
    }

    static func simpleSections(
        where isAvailable: (OversightDestination) -> Bool
    ) -> [OversightSectionModel] {
        OversightSection.allCases.compactMap { section in
            let destinations = section.destinations.filter(isAvailable)
            return destinations.isEmpty
                ? nil
                : OversightSectionModel(section: section, destinations: destinations)
        }
    }

    static func destinations(
        for root: OversightRoot,
        capabilities: ResolvedSystemCapabilities
    ) -> [OversightDestination] {
        destinations(for: root) { $0.isAvailable(in: capabilities) }
    }

    static func destinations(
        for root: OversightRoot,
        where isAvailable: (OversightDestination) -> Bool
    ) -> [OversightDestination] {
        root.destinations.filter(isAvailable)
    }

    static func hasVisibleDestinations(
        capabilities: ResolvedSystemCapabilities
    ) -> Bool {
        !simpleSections(for: capabilities).isEmpty
    }

    static func hasVisibleDestinations(
        where isAvailable: (OversightDestination) -> Bool
    ) -> Bool {
        !simpleSections(where: isAvailable).isEmpty
    }

    static func isRootVisible(
        _ root: OversightRoot,
        capabilities: ResolvedSystemCapabilities
    ) -> Bool {
        isRootVisible(root) { $0.isAvailable(in: capabilities) }
    }

    static func isRootVisible(
        _ root: OversightRoot,
        where isAvailable: (OversightDestination) -> Bool
    ) -> Bool {
        root == .fleet || !destinations(for: root, where: isAvailable).isEmpty
    }

    static func visibleTabs(
        capabilities: ResolvedSystemCapabilities
    ) -> [AppTab] {
        visibleTabs { $0.isAvailable(in: capabilities) }
    }

    static func visibleTabs(
        where isAvailable: (OversightDestination) -> Bool
    ) -> [AppTab] {
        let roots: [(AppTab, OversightRoot)] = [
            (.overview, OversightRoot.overview),
            (.fleet, OversightRoot.fleet),
            (.jobs, OversightRoot.jobs),
            (.upkeep, OversightRoot.upkeep),
            (.reports, OversightRoot.reports)
        ]
        return roots.compactMap { tab, root in
            isRootVisible(root, where: isAvailable) ? tab : nil
        }
    }
}

extension OversightDestination {
    var appDestination: AppDestination {
        switch self {
        case .dashboard:
            .dashboard
        case .dispatch:
            .dispatchDashboard
        case .filamentCoverage:
            .filamentCoverage
        case .maintenance:
            .maintenance
        case .maintenanceAnalytics:
            .maintenanceAnalytics
        case .predictiveInsights:
            .predictiveInsights(printerId: nil)
        case .jobHistory:
            .jobHistory
        case .jobTimeline:
            .jobTimeline
        case .locations:
            .locations
        case .uptimeReliability:
            .uptimeReliability
        case .navigationSettings:
            .navigationSettings
        }
    }
}
