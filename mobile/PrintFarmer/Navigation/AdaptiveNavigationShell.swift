import Foundation

enum NavigationLayoutPreference: String, CaseIterable, Codable, Hashable, Identifiable, Sendable {
    case automatic
    case simple
    case twoModes

    var id: Self { self }

    var title: String {
        switch self {
        case .automatic:
            "Automatic"
        case .simple:
            "Simple"
        case .twoModes:
            "Two modes"
        }
    }

    var subtitle: String {
        switch self {
        case .automatic:
            "Match the layout to this server"
        case .simple:
            "One shell, with Oversight as a tab"
        case .twoModes:
            "Separate Floor and Oversight modes"
        }
    }
}

struct NavigationShellDerivation: Equatable, Sendable {
    let shell: NavigationShell
    let explanation: String

    static func automatic(
        farmShape: FarmShape?,
        shiftPlanEnabled: Bool,
        isFarmAdmin: Bool
    ) -> NavigationShellDerivation {
        guard let farmShape else {
            return NavigationShellDerivation(
                shell: .simple,
                explanation: "This server doesn't report its size. Using the simple layout."
            )
        }

        let observedShape = [
            countDescription(farmShape.accountCount, singular: "account"),
            countDescription(farmShape.locationCount, singular: "bay"),
            countDescription(farmShape.printerCount, singular: "printer"),
            "shift planning \(shiftPlanEnabled ? "on" : "off")"
        ].joined(separator: ", ")

        if !shiftPlanEnabled {
            return NavigationShellDerivation(
                shell: .simple,
                explanation: "\(observedShape). That is the simple layout because this server says it does not run shifts. Printer count does not change the layout."
            )
        }

        if !isFarmAdmin {
            return NavigationShellDerivation(
                shell: .simple,
                explanation: "\(observedShape). That is the simple layout for this account because it is not a farm administrator. Printer count does not change the layout."
            )
        }

        if farmShape.accountCount >= 2 {
            return NavigationShellDerivation(
                shell: .twoModes,
                explanation: "\(observedShape). That is the two modes layout because this server has multiple accounts. Printer count does not change the layout."
            )
        }

        if farmShape.locationCount >= 2 {
            return NavigationShellDerivation(
                shell: .twoModes,
                explanation: "\(observedShape). That is the two modes layout because this server has multiple bays. Printer count does not change the layout."
            )
        }

        return NavigationShellDerivation(
            shell: .simple,
            explanation: "\(observedShape). That is the simple layout. Shift planning being on by itself does not indicate a staffed farm, and printer count does not change the layout."
        )
    }

    private static func countDescription(_ count: Int, singular: String) -> String {
        "\(count) \(singular)\(count == 1 ? "" : "s")"
    }
}

struct OversightNavigationAvailability: Equatable, Sendable {
    static let fullyAvailable = OversightNavigationAvailability(
        hasVisibleHubDestinations: true,
        visibleTabs: [.overview, .fleet, .jobs, .upkeep, .reports]
    )

    let hasVisibleHubDestinations: Bool
    let visibleTabs: [AppTab]

    init(hasVisibleHubDestinations: Bool, visibleTabs: [AppTab]) {
        let visibleTabSet = Set(visibleTabs)
        self.hasVisibleHubDestinations = hasVisibleHubDestinations
        self.visibleTabs = AppTab.tabs(
            for: .twoModes,
            mode: .oversight
        ).filter(visibleTabSet.contains)
    }

    var supportsTwoModes: Bool {
        visibleTabs.count >= 2
    }
}

enum AdaptiveNavigationShell {
    static func requestedShell(
        preference: NavigationLayoutPreference,
        automaticDerivation: NavigationShellDerivation
    ) -> NavigationShell {
        switch preference {
        case .automatic:
            automaticDerivation.shell
        case .simple:
            .simple
        case .twoModes:
            .twoModes
        }
    }

    static func effectiveShell(
        requestedShell: NavigationShell,
        oversightAvailability: OversightNavigationAvailability
    ) -> NavigationShell {
        requestedShell == .twoModes && !oversightAvailability.supportsTwoModes
            ? .simple
            : requestedShell
    }
}
