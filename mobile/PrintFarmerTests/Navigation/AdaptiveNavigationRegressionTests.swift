import XCTest
@testable import PrintFarmer

@MainActor
final class AdaptiveNavigationRegressionTests: XCTestCase {
    private let capabilities = ResolvedSystemCapabilities.defaults
    private let printerID = UUID(uuidString: "00000000-0000-0000-0000-000000000001")!

    func testNonAdminDerivesSimpleWithoutLosingOversight() {
        let derivation = NavigationShellDerivation.automatic(
            farmShape: FarmShape(accountCount: 4, locationCount: 3, printerCount: 40),
            shiftPlanEnabled: true,
            isFarmAdmin: false
        )

        XCTAssertEqual(derivation.shell, .simple)
        XCTAssertTrue(
            AppTab.visibleTabs(
                for: derivation.shell,
                mode: .floor,
                capabilities: capabilities
            ).contains(.oversight)
        )
    }

    func testExplicitPreferencesBeatAutomaticDerivationInBothDirections() {
        let simpleDerivation = NavigationShellDerivation.automatic(
            farmShape: FarmShape(accountCount: 1, locationCount: 1, printerCount: 40),
            shiftPlanEnabled: true,
            isFarmAdmin: true
        )
        let twoModesDerivation = NavigationShellDerivation.automatic(
            farmShape: FarmShape(accountCount: 2, locationCount: 1, printerCount: 1),
            shiftPlanEnabled: true,
            isFarmAdmin: true
        )

        XCTAssertEqual(
            AdaptiveNavigationShell.requestedShell(
                preference: .twoModes,
                automaticDerivation: simpleDerivation
            ),
            .twoModes
        )
        XCTAssertEqual(
            AdaptiveNavigationShell.requestedShell(
                preference: .simple,
                automaticDerivation: twoModesDerivation
            ),
            .simple
        )
    }

    func testSwitchingServerContextChangesResolvedShell() {
        let router = AppRouter()

        router.configureAdaptiveShell(
            serverID: UUID(),
            userID: UUID(),
            preference: .simple,
            farmShape: FarmShape(accountCount: 3, locationCount: 2, printerCount: 40),
            isFarmAdmin: true,
            capabilities: capabilities,
            oversightAvailability: .fullyAvailable
        )
        XCTAssertEqual(router.activeShell, .simple)

        router.configureAdaptiveShell(
            serverID: UUID(),
            userID: UUID(),
            preference: .twoModes,
            farmShape: FarmShape(accountCount: 1, locationCount: 1, printerCount: 1),
            isFarmAdmin: true,
            capabilities: capabilities,
            oversightAvailability: .fullyAvailable
        )
        XCTAssertEqual(router.activeShell, .twoModes)
    }

    func testChromeRootAndPushContractEnumeratesTabsFromShellDefinition() {
        let router = AppRouter()
        let shellModes: [(NavigationShell, OversightMode)] = [
            (.simple, .floor),
            (.twoModes, .floor),
            (.twoModes, .oversight)
        ]

        for (shell, mode) in shellModes {
            router.setNavigationShell(
                shell,
                mode: mode,
                capabilities: capabilities
            )

            for tab in AppTab.tabs(for: shell, mode: mode) {
                router.resetToRoot(tab: tab)

                XCTAssertTrue(router.isAtRoot(tab), "\(tab) must expose root chrome")
                XCTAssertEqual(
                    router.shouldShowModeControl(for: tab),
                    shell == .twoModes,
                    "\(tab) mode-control visibility must match its shell"
                )

                appendPushedDestination(for: tab, to: router)

                XCTAssertFalse(router.isAtRoot(tab), "\(tab) pushed screens must hide root chrome")
                XCTAssertFalse(
                    router.shouldShowModeControl(for: tab),
                    "\(tab) pushed screens must hide the mode control"
                )
            }
        }
    }

    func testVisibleTabsAndModeControlNeverExposeADeadOrEmptyShell() {
        let router = AppRouter()
        let availabilityCases = [
            OversightNavigationAvailability(
                hasVisibleHubDestinations: false,
                visibleTabs: []
            ),
            OversightNavigationAvailability(
                hasVisibleHubDestinations: true,
                visibleTabs: [.fleet]
            ),
            OversightNavigationAvailability(
                hasVisibleHubDestinations: true,
                visibleTabs: [.fleet, .reports]
            )
        ]

        for attentionEnabled in [false, true] {
            for shiftPlanEnabled in [false, true] {
                var gatedCapabilities = capabilities
                gatedCapabilities.attentionEnabled = attentionEnabled
                gatedCapabilities.shiftPlanEnabled = shiftPlanEnabled

                for availability in availabilityCases {
                    router.setNavigationShell(
                        .twoModes,
                        mode: .oversight,
                        capabilities: gatedCapabilities,
                        oversightAvailability: availability
                    )

                    XCTAssertFalse(router.visibleTabs(for: gatedCapabilities).isEmpty)
                    XCTAssertEqual(
                        router.activeShell == .twoModes,
                        availability.supportsTwoModes
                    )
                    XCTAssertEqual(
                        router.shouldShowModeControl(
                            for: router.resolvedTab(for: gatedCapabilities)
                        ),
                        availability.supportsTwoModes
                    )
                }
            }
        }
    }

    func testDisabledOperatorFeatureDestinationsAreOmitted() {
        var disabled = capabilities
        disabled.attentionEnabled = false
        disabled.shiftPlanEnabled = false
        disabled.filamentCoverageEnabled = false

        let simpleTabs = AppTab.visibleTabs(
            for: .simple,
            mode: .floor,
            capabilities: disabled
        )
        let floorTabs = AppTab.visibleTabs(
            for: .twoModes,
            mode: .floor,
            capabilities: disabled
        )
        let oversightDestinations = OversightRoot.allCases.flatMap {
            OversightCatalog.destinations(for: $0, capabilities: disabled)
        }

        XCTAssertFalse(simpleTabs.contains(.attention))
        XCTAssertFalse(floorTabs.contains(.attention))
        XCTAssertFalse(floorTabs.contains(.tasks))
        XCTAssertFalse(oversightDestinations.contains(.filamentCoverage))
    }

    func testPersistedStaleSelectionReconcilesToVisibleTab() throws {
        let suiteName = "AdaptiveNavigationRegressionTests.\(UUID().uuidString)"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suiteName))
        defer { defaults.removePersistentDomain(forName: suiteName) }
        defaults.set(AppTab.reports.rawValue, forKey: AppRouter.selectedTabDefaultsKey)
        let router = AppRouter(userDefaults: defaults)
        let availability = OversightNavigationAvailability(
            hasVisibleHubDestinations: false,
            visibleTabs: []
        )

        router.setNavigationShell(
            .simple,
            capabilities: capabilities,
            oversightAvailability: availability
        )

        XCTAssertEqual(router.resolvedTab(for: capabilities), .attention)
        router.reconcileCapabilities(
            capabilities,
            oversightAvailability: availability
        )
        XCTAssertEqual(router.selectedTab, .attention)
        XCTAssertTrue(router.visibleTabs(for: capabilities).contains(router.selectedTab))
    }

    func testUnknownShapeSuppressesOfferAndObservationNeverChangesShell() throws {
        let suiteName = "AdaptiveNavigationRegressionTests.offer.\(UUID().uuidString)"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suiteName))
        defer { defaults.removePersistentDomain(forName: suiteName) }
        let registry = ServerRegistry(userDefaults: defaults, migrateLegacyServerURL: false)
        _ = try registry.add(
            displayName: "Farm",
            baseURL: URL(string: "https://farm.example.com")!
        )

        XCTAssertFalse(
            registry.observeOversightUpgradeOffer(
                farmShape: nil,
                shiftPlanEnabled: true,
                isFarmAdmin: true
            )
        )
        _ = registry.observeOversightUpgradeOffer(
            farmShape: FarmShape(accountCount: 1, locationCount: 1, printerCount: 1),
            shiftPlanEnabled: false,
            isFarmAdmin: true
        )
        XCTAssertTrue(
            registry.observeOversightUpgradeOffer(
                farmShape: FarmShape(accountCount: 2, locationCount: 1, printerCount: 1),
                shiftPlanEnabled: true,
                isFarmAdmin: true
            )
        )
        XCTAssertEqual(
            registry.navigationLayoutPreference,
            .automatic,
            "Observing an offer must never change the shell without an explicit user action"
        )
    }

    private func appendPushedDestination(
        for tab: AppTab,
        to router: AppRouter
    ) {
        switch tab {
        case .attention:
            router.notificationsPath.append(AppDestination.notifications)
        case .farm:
            router.printersPath.append(AppDestination.printerDetail(id: printerID))
        case .tasks:
            router.tasksPath.append(AppDestination.jobHistory)
        case .inventory:
            router.inventoryPath.append(AppDestination.settings)
        case .oversight:
            router.oversightPath.append(AppDestination.dashboard)
        case .overview:
            router.overviewPath.append(AppDestination.dashboard)
        case .fleet:
            router.fleetPath.append(AppDestination.printerDetail(id: printerID))
        case .jobs:
            router.oversightJobsPath.append(AppDestination.jobHistory)
        case .upkeep:
            router.upkeepPath.append(AppDestination.maintenance)
        case .reports:
            router.reportsPath.append(AppDestination.uptimeReliability)
        }
    }
}
