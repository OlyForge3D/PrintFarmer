import XCTest
@testable import PrintFarmer

@MainActor
final class OversightCatalogTests: XCTestCase {
    private let capabilities = ResolvedSystemCapabilities.defaults

    func testSimpleHubUsesRequiredSectionAndDestinationOrder() {
        let sections = OversightCatalog.simpleSections(for: capabilities)

        XCTAssertEqual(
            sections.map(\.section),
            [.rightNow, .upkeep, .records, .reports]
        )
        XCTAssertEqual(
            sections.map(\.destinations),
            [
                [.dashboard, .dispatch, .filamentCoverage],
                [.maintenance, .maintenanceAnalytics, .predictiveInsights],
                [.jobHistory, .jobTimeline, .locations],
                [.uptimeReliability, .navigationSettings]
            ]
        )
    }

    func testTwoModesExposeTheSameDestinationSetAsSimpleHub() {
        let simpleDestinations = Set(
            OversightCatalog.simpleSections(for: capabilities)
                .flatMap(\.destinations)
        )
        let twoModeDestinations = Set(
            OversightRoot.allCases.flatMap {
                OversightCatalog.destinations(
                    for: $0,
                    capabilities: capabilities
                )
            }
        )

        XCTAssertEqual(twoModeDestinations, simpleDestinations)
        XCTAssertEqual(
            OversightCatalog.destinations(
                for: .overview,
                capabilities: capabilities
            ),
            [.dashboard, .dispatch]
        )
        XCTAssertEqual(
            OversightCatalog.destinations(
                for: .jobs,
                capabilities: capabilities
            ),
            [.jobHistory, .jobTimeline]
        )
        XCTAssertEqual(
            OversightCatalog.destinations(
                for: .upkeep,
                capabilities: capabilities
            ),
            [.maintenance, .maintenanceAnalytics, .predictiveInsights]
        )
        XCTAssertEqual(
            OversightCatalog.destinations(
                for: .reports,
                capabilities: capabilities
            ),
            [.uptimeReliability, .filamentCoverage, .locations, .navigationSettings]
        )
    }

    func testDisabledFilamentCoverageIsOmittedEverywhere() {
        var disabled = capabilities
        disabled.filamentCoverageEnabled = false

        let simpleSections = OversightCatalog.simpleSections(for: disabled)
        let simpleDestinations = simpleSections.flatMap(\.destinations)
        let twoModeDestinations = OversightRoot.allCases.flatMap {
            OversightCatalog.destinations(for: $0, capabilities: disabled)
        }

        XCTAssertFalse(simpleDestinations.contains(.filamentCoverage))
        XCTAssertFalse(twoModeDestinations.contains(.filamentCoverage))
        XCTAssertTrue(simpleSections.allSatisfy { !$0.destinations.isEmpty })
    }

    func testAllRowsUnavailableOmitsHubAndLeavesOnlyFleetRoot() {
        XCTAssertFalse(
            OversightCatalog.hasVisibleDestinations { _ in false }
        )
        XCTAssertTrue(
            OversightCatalog.simpleSections { _ in false }.isEmpty
        )
        XCTAssertEqual(
            OversightCatalog.visibleTabs { _ in false },
            [.fleet]
        )
    }

    func testVisibleOversightTabsUseStableOrder() {
        XCTAssertEqual(
            OversightCatalog.visibleTabs(capabilities: capabilities),
            [.overview, .fleet, .jobs, .upkeep, .reports]
        )
    }

    func testAccessibilityMetadataAndTargetsAreStable() {
        let destinations = OversightDestination.allCases

        XCTAssertEqual(
            Set(destinations.map(\.accessibilityIdentifier)).count,
            destinations.count
        )
        XCTAssertTrue(
            destinations.allSatisfy {
                !$0.title.isEmpty
                    && !$0.accessibilityHint.isEmpty
                    && $0.accessibilityIdentifier.hasPrefix("oversight.destination.")
            }
        )
        XCTAssertEqual(OversightHubView.minimumRowHeight, 44)
        XCTAssertEqual(
            OversightTabRootView.supportedTabs,
            [.oversight, .overview, .fleet, .jobs, .upkeep, .reports]
        )
    }

    func testCatalogMapsEveryRowToAConcreteAppDestination() {
        XCTAssertEqual(OversightDestination.dashboard.appDestination, .dashboard)
        XCTAssertEqual(OversightDestination.dispatch.appDestination, .dispatchDashboard)
        XCTAssertEqual(OversightDestination.filamentCoverage.appDestination, .filamentCoverage)
        XCTAssertEqual(OversightDestination.maintenance.appDestination, .maintenance)
        XCTAssertEqual(
            OversightDestination.maintenanceAnalytics.appDestination,
            .maintenanceAnalytics
        )
        XCTAssertEqual(
            OversightDestination.predictiveInsights.appDestination,
            .predictiveInsights(printerId: nil)
        )
        XCTAssertEqual(OversightDestination.jobHistory.appDestination, .jobHistory)
        XCTAssertEqual(OversightDestination.jobTimeline.appDestination, .jobTimeline)
        XCTAssertEqual(OversightDestination.locations.appDestination, .locations)
        XCTAssertEqual(
            OversightDestination.uptimeReliability.appDestination,
            .uptimeReliability
        )
        XCTAssertEqual(
            OversightDestination.navigationSettings.appDestination,
            .navigationSettings
        )
    }
}
