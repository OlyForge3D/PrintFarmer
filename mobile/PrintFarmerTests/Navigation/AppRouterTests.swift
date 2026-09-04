import XCTest
@testable import PrintFarmer

/// Tests for shell-aware tab selection, deep-link mapping, and path isolation.
@MainActor
final class AppRouterTests: XCTestCase {

    private let printerId = UUID(uuidString: "00000000-0000-0000-0000-000000000001")!
    private let spoolId = 42
    private let originServerId = UUID(uuidString: "00000000-0000-0000-0000-000000000010")!
    private let capabilities = ResolvedSystemCapabilities.defaults

    // MARK: - Defaults

    func testDefaultSelectedTabIsAttention() {
        let router = AppRouter()
        XCTAssertEqual(router.selectedTab, .attention)
        XCTAssertEqual(router.activeShell, .current)
        XCTAssertEqual(router.activeMode, .floor)
    }

    func testTabModelEnumeratesCurrentAndAdaptiveSets() {
        XCTAssertEqual(
            AppTab.tabs(for: .current, mode: .floor),
            [.attention, .farm, .tasks, .scan, .inventory]
        )
        XCTAssertEqual(
            AppTab.tabs(for: .simple, mode: .floor),
            [.attention, .farm, .inventory, .oversight]
        )
        XCTAssertEqual(
            AppTab.tabs(for: .twoModes, mode: .floor),
            [.attention, .farm, .tasks, .inventory]
        )
        XCTAssertEqual(
            AppTab.tabs(for: .twoModes, mode: .oversight),
            [.overview, .fleet, .jobs, .upkeep, .reports]
        )
    }

    func testCurrentTabPresentationMatchesShippingUI() {
        let tabs = ContentView.shippingTabs(for: capabilities)

        XCTAssertEqual(tabs, [.attention, .farm, .tasks, .scan, .inventory])
        XCTAssertEqual(tabs.map(\.title), ["Attention", "Farm", "Tasks", "Scan", "Inventory"])
        XCTAssertEqual(
            tabs.map(\.systemImage),
            ["bell.badge", "printer", "checklist", "barcode.viewfinder", "cylinder.fill"]
        )
        XCTAssertEqual(
            tabs.map(\.badgeKind),
            [.notifications, .pendingReady, .none, .none, .none]
        )
        XCTAssertEqual(
            tabs.map(\.tabAccessibilityIdentifier),
            ["tab.attention", "tab.farm", "tab.tasks", "tab.scan", "tab.inventory"]
        )
        XCTAssertEqual(
            tabs.map(\.sidebarAccessibilityIdentifier),
            [
                "sidebar.attention",
                "sidebar.farm",
                "sidebar.tasks",
                "sidebar.scan",
                "sidebar.inventory"
            ]
        )
        XCTAssertEqual(ContentView.sidebarRowMinimumHeight, 44)
    }

    func testVisibleTabsRemoveDisabledAttentionAndTasks() {
        var disabled = capabilities
        disabled.attentionEnabled = false
        disabled.shiftPlanEnabled = false

        XCTAssertEqual(AppTab.visibleTabs(for: disabled), [.farm, .scan, .inventory])
        XCTAssertEqual(AppTab.fallbackTab(for: disabled), .farm)
    }

    func testCapabilityGatingIsAppliedWithinEachAdaptiveSet() {
        var disabled = capabilities
        disabled.attentionEnabled = false
        disabled.shiftPlanEnabled = false

        XCTAssertEqual(
            AppTab.visibleTabs(
                for: .simple,
                mode: .floor,
                capabilities: disabled
            ),
            [.farm, .inventory, .oversight]
        )
        XCTAssertEqual(
            AppTab.visibleTabs(
                for: .twoModes,
                mode: .floor,
                capabilities: disabled
            ),
            [.farm, .inventory]
        )
        XCTAssertEqual(
            AppTab.visibleTabs(
                for: .twoModes,
                mode: .oversight,
                capabilities: disabled
            ),
            [.overview, .fleet, .jobs, .upkeep, .reports]
        )
    }

    func testAttentionAndTasksGatesApplyIndependently() {
        var attentionDisabled = capabilities
        attentionDisabled.attentionEnabled = false
        XCTAssertEqual(
            AppTab.visibleTabs(
                for: .twoModes,
                mode: .floor,
                capabilities: attentionDisabled
            ),
            [.farm, .tasks, .inventory]
        )

        var tasksDisabled = capabilities
        tasksDisabled.shiftPlanEnabled = false
        XCTAssertEqual(
            AppTab.visibleTabs(
                for: .twoModes,
                mode: .floor,
                capabilities: tasksDisabled
            ),
            [.attention, .farm, .inventory]
        )
    }

    func testChangingShellFallsBackWithinTheNewActiveSet() {
        let router = AppRouter()
        router.selectedTab = .farm

        router.setNavigationShell(
            .twoModes,
            mode: .oversight,
            capabilities: capabilities
        )

        XCTAssertEqual(router.activeShell, .twoModes)
        XCTAssertEqual(router.activeMode, .oversight)
        XCTAssertEqual(router.selectedTab, .overview)
    }

    func testShellTransitionsPreserveValidSelectionAndFallbackBidirectionally() {
        let router = AppRouter()
        router.selectedTab = .inventory

        router.setNavigationShell(
            .simple,
            capabilities: capabilities
        )
        XCTAssertEqual(router.selectedTab, .inventory)

        router.selectTab(.oversight, capabilities: capabilities)
        router.setNavigationShell(
            .twoModes,
            mode: .floor,
            capabilities: capabilities
        )
        XCTAssertEqual(router.selectedTab, .attention)

        router.setNavigationShell(
            .twoModes,
            mode: .oversight,
            capabilities: capabilities
        )
        XCTAssertEqual(router.selectedTab, .overview)

        router.selectTab(.reports, capabilities: capabilities)
        router.setNavigationShell(
            .twoModes,
            mode: .floor,
            capabilities: capabilities
        )
        XCTAssertEqual(router.selectedTab, .attention)
    }

    func testInvalidSelectionFallsBackWithinActiveShell() {
        let router = AppRouter()
        router.setNavigationShell(
            .twoModes,
            mode: .oversight,
            capabilities: capabilities
        )

        router.selectTab(.tasks, capabilities: capabilities)

        XCTAssertEqual(router.selectedTab, .overview)
        XCTAssertEqual(router.resolvedTab(for: capabilities), .overview)
        XCTAssertEqual(router.fallbackTab(for: capabilities), .overview)
    }

    func testShellTransitionCancelsPendingDelayedNavigation() async {
        let router = AppRouter()
        router.navigate(
            to: .printerReady(id: printerId),
            capabilities: capabilities
        )
        router.pendingSpoolHighlightId = spoolId
        router.pendingAttentionItemId = "attention-1"
        router.pendingFilamentSwap = .init(
            printerId: printerId,
            toolheadIndex: 1,
            jobId: nil
        )

        router.setNavigationShell(
            .twoModes,
            mode: .oversight,
            capabilities: capabilities
        )
        try? await Task.sleep(for: .milliseconds(120))

        XCTAssertTrue(router.printersPath.isEmpty)
        XCTAssertTrue(router.fleetPath.isEmpty)
        XCTAssertNil(router.pendingNFCReadyPrinterId)
        XCTAssertNil(router.pendingSpoolHighlightId)
        XCTAssertNil(router.pendingAttentionItemId)
        XCTAssertNil(router.pendingFilamentSwap)
        XCTAssertEqual(router.selectedTab, .overview)
    }

    func testReconcileMovesDisabledSelectionToDeterministicFallback() {
        var disabled = capabilities
        disabled.attentionEnabled = false
        disabled.shiftPlanEnabled = false
        let router = AppRouter()
        router.selectedTab = .attention
        router.notificationBadgeCount = 3
        router.pendingAttentionItemId = "attention-1"
        router.notificationsPath.append(AppDestination.jobDetail(id: printerId))

        router.reconcileCapabilities(disabled)

        XCTAssertEqual(router.selectedTab, .farm)
        XCTAssertEqual(router.notificationBadgeCount, 0)
        XCTAssertNil(router.pendingAttentionItemId)
        XCTAssertTrue(router.notificationsPath.isEmpty)
    }

    func testReconcileMovesDisabledTasksSelectionToAttentionWhenAvailable() {
        var disabled = capabilities
        disabled.shiftPlanEnabled = false
        let router = AppRouter()
        router.selectedTab = .tasks
        router.jobsPath.append(AppDestination.jobDetail(id: printerId))

        router.reconcileCapabilities(disabled)

        XCTAssertEqual(router.selectedTab, .attention)
        XCTAssertTrue(router.jobsPath.isEmpty)
    }

    // MARK: - Deep link routing

    func testPrinterDetailDeepLinkSelectsFarmTab() async {
        let router = AppRouter()
        router.selectedTab = .attention

        router.navigate(to: .printerDetail(id: printerId), capabilities: capabilities)
        XCTAssertEqual(router.selectedTab, .farm)

        // navigate() schedules a delayed append; wait past the 50 ms delay.
        try? await Task.sleep(for: .milliseconds(120))
        XCTAssertFalse(router.printersPath.isEmpty)
    }

    func testPrinterReadyDeepLinkSelectsFarmTabAndSetsPending() async {
        let router = AppRouter()
        router.selectedTab = .attention

        router.navigate(to: .printerReady(id: printerId), capabilities: capabilities)

        XCTAssertEqual(router.selectedTab, .farm)
        XCTAssertEqual(router.pendingNFCReadyPrinterId, printerId)

        try? await Task.sleep(for: .milliseconds(120))
        XCTAssertFalse(router.printersPath.isEmpty)
    }

    func testSpoolDetailDeepLinkSelectsInventoryTab() {
        let router = AppRouter()
        router.selectedTab = .attention

        router.navigate(to: .spoolDetail(id: spoolId), capabilities: capabilities)

        XCTAssertEqual(router.selectedTab, .inventory)
        XCTAssertEqual(router.pendingSpoolHighlightId, spoolId)
    }

    func testAttentionDeepLinkSelectsAttentionAndPreservesItem() {
        let router = AppRouter()

        router.navigate(to: .attentionItem(id: "failure-123"), capabilities: capabilities)

        XCTAssertEqual(router.selectedTab, .attention)
        XCTAssertEqual(router.pendingAttentionItemId, "failure-123")
    }

    func testFilamentSwapDeepLinkSelectsFarmAndPreservesDestination() async {
        let jobId = UUID(uuidString: "00000000-0000-0000-0000-000000000002")!
        let router = AppRouter()

        router.navigate(
            to: .filamentSwap(printerId: printerId, toolheadIndex: 2, jobId: jobId),
            capabilities: capabilities
        )

        XCTAssertEqual(router.selectedTab, .farm)
        XCTAssertEqual(
            router.pendingFilamentSwap,
            .init(printerId: printerId, toolheadIndex: 2, jobId: jobId)
        )

        try? await Task.sleep(for: .milliseconds(120))
        XCTAssertFalse(router.printersPath.isEmpty)
    }

    func testPrinterDeepLinkUsesDistinctFleetPathInOversightMode() async {
        let router = AppRouter()
        router.setNavigationShell(
            .twoModes,
            mode: .oversight,
            capabilities: capabilities
        )

        router.navigate(
            to: .printerDetail(id: printerId),
            capabilities: capabilities
        )

        XCTAssertEqual(router.selectedTab, .fleet)
        try? await Task.sleep(for: .milliseconds(120))
        XCTAssertTrue(router.printersPath.isEmpty)
        XCTAssertEqual(router.fleetPath.count, 1)
    }

    func testAttentionDeepLinkFallsBackWhenActiveSetOmitsAttention() {
        let router = AppRouter()
        router.setNavigationShell(
            .twoModes,
            mode: .oversight,
            capabilities: capabilities
        )

        router.navigate(
            to: .attentionItem(id: "failure-123"),
            capabilities: capabilities
        )

        XCTAssertEqual(router.selectedTab, .overview)
        XCTAssertNil(router.pendingAttentionItemId)
        XCTAssertTrue(router.notificationsPath.isEmpty)
    }

    func testSpoolDeepLinkFallsBackWhenActiveSetOmitsInventory() {
        let router = AppRouter()
        router.setNavigationShell(
            .twoModes,
            mode: .oversight,
            capabilities: capabilities
        )

        router.navigate(
            to: .spoolDetail(id: spoolId),
            capabilities: capabilities
        )

        XCTAssertEqual(router.selectedTab, .overview)
        XCTAssertNil(router.pendingSpoolHighlightId)
        XCTAssertTrue(router.inventoryPath.isEmpty)
    }

    func testFilamentSwapUsesDistinctFleetPathInOversightMode() async {
        let router = AppRouter()
        router.setNavigationShell(
            .twoModes,
            mode: .oversight,
            capabilities: capabilities
        )

        router.navigate(
            to: .filamentSwap(
                printerId: printerId,
                toolheadIndex: 2,
                jobId: nil
            ),
            capabilities: capabilities
        )

        XCTAssertEqual(router.selectedTab, .fleet)
        XCTAssertNotNil(router.pendingFilamentSwap)
        try? await Task.sleep(for: .milliseconds(120))
        XCTAssertTrue(router.printersPath.isEmpty)
        XCTAssertEqual(router.fleetPath.count, 1)
    }

    func testDisabledAttentionDeepLinkRoutesToFarmWithoutPendingItem() {
        var disabled = capabilities
        disabled.attentionEnabled = false
        let router = AppRouter()
        router.selectedTab = .inventory

        router.navigate(
            to: .attentionItem(id: "failure-123"),
            capabilities: disabled
        )

        XCTAssertEqual(router.selectedTab, .farm)
        XCTAssertNil(router.pendingAttentionItemId)
        XCTAssertTrue(router.notificationsPath.isEmpty)
    }

    func testDisabledGuidedSwapDeepLinkOpensPrinterWithoutOpeningSwap() async {
        var disabled = capabilities
        disabled.guidedSwapEnabled = false
        let router = AppRouter()

        router.navigate(
            to: .filamentSwap(printerId: printerId, toolheadIndex: 2, jobId: nil),
            capabilities: disabled
        )

        XCTAssertEqual(router.selectedTab, .farm)
        XCTAssertNil(router.pendingFilamentSwap)
        try? await Task.sleep(for: .milliseconds(120))
        XCTAssertFalse(router.printersPath.isEmpty)
    }

    func testCapabilityReconciliationClearsPendingGuidedSwap() {
        let router = AppRouter()
        router.pendingFilamentSwap = .init(
            printerId: printerId,
            toolheadIndex: 2,
            jobId: nil
        )
        var disabled = capabilities
        disabled.guidedSwapEnabled = false

        router.reconcileCapabilities(disabled)

        XCTAssertNil(router.pendingFilamentSwap)
    }

    func testDisablingAdvancedControlsClearsEveryDestinationStack() {
        let router = AppRouter()
        router.printersPath.append(AppDestination.advancedPrinterControls(printerId: printerId))
        router.tasksPath.append(AppDestination.advancedPrinterControls(printerId: printerId))
        router.jobsPath.append(AppDestination.advancedPrinterControls(printerId: printerId))
        router.notificationsPath.append(AppDestination.advancedPrinterControls(printerId: printerId))
        router.inventoryPath.append(AppDestination.advancedPrinterControls(printerId: printerId))
        router.scanPath.append(AppDestination.advancedPrinterControls(printerId: printerId))
        router.oversightPath.append(AppDestination.advancedPrinterControls(printerId: printerId))
        router.overviewPath.append(AppDestination.advancedPrinterControls(printerId: printerId))
        router.fleetPath.append(AppDestination.advancedPrinterControls(printerId: printerId))
        router.oversightJobsPath.append(
            AppDestination.advancedPrinterControls(printerId: printerId)
        )
        router.upkeepPath.append(AppDestination.advancedPrinterControls(printerId: printerId))
        router.reportsPath.append(AppDestination.advancedPrinterControls(printerId: printerId))
        router.dashboardSheetPath.append(AppDestination.advancedPrinterControls(printerId: printerId))
        router.maintenanceSheetPath.append(AppDestination.advancedPrinterControls(printerId: printerId))
        router.notificationsSheetPath.append(AppDestination.advancedPrinterControls(printerId: printerId))

        router.revokeAdvancedPrinterControlsAccess()

        XCTAssertTrue(router.printersPath.isEmpty)
        XCTAssertTrue(router.tasksPath.isEmpty)
        XCTAssertTrue(router.jobsPath.isEmpty)
        XCTAssertTrue(router.notificationsPath.isEmpty)
        XCTAssertTrue(router.inventoryPath.isEmpty)
        XCTAssertTrue(router.scanPath.isEmpty)
        XCTAssertTrue(router.oversightPath.isEmpty)
        XCTAssertTrue(router.overviewPath.isEmpty)
        XCTAssertTrue(router.fleetPath.isEmpty)
        XCTAssertTrue(router.oversightJobsPath.isEmpty)
        XCTAssertTrue(router.upkeepPath.isEmpty)
        XCTAssertTrue(router.reportsPath.isEmpty)
        XCTAssertTrue(router.dashboardSheetPath.isEmpty)
        XCTAssertTrue(router.maintenanceSheetPath.isEmpty)
        XCTAssertTrue(router.notificationsSheetPath.isEmpty)
    }

    func testAdvancedControlsAccessRequiresOptInAndOnlinePrinter() throws {
        var printer = try TestData.decodePrinter()

        XCTAssertFalse(
            AdvancedPrinterControlsAccess.isEntryVisible(isEnabled: false, for: printer)
        )
        XCTAssertTrue(
            AdvancedPrinterControlsAccess.isEntryVisible(isEnabled: true, for: printer)
        )

        printer.isOnline = false
        XCTAssertFalse(
            AdvancedPrinterControlsAccess.isEntryVisible(isEnabled: true, for: printer)
        )
    }

    func testPendingPrinterNavigationIsInvalidatedWhenServerChanges() async {
        let router = AppRouter()

        router.dashboardSheetPath.append(AppDestination.printerDetail(id: printerId))
        router.maintenanceSheetPath.append(AppDestination.maintenanceAnalytics)
        router.notificationsSheetPath.append(AppDestination.jobDetail(id: printerId))
        router.navigate(to: .printerDetail(id: printerId), capabilities: capabilities)
        router.navigate(to: .attentionItem(id: "attention-1"), capabilities: capabilities)
        router.navigate(to: .spoolDetail(id: 42), capabilities: capabilities)
        router.jobsPath.append(AppDestination.jobDetail(id: printerId))
        router.scanPath.append(AppDestination.jobDetail(id: printerId))
        router.oversightPath.append(AppDestination.jobHistory)
        router.overviewPath.append(AppDestination.dispatchDashboard)
        router.fleetPath.append(AppDestination.printerDetail(id: printerId))
        router.oversightJobsPath.append(AppDestination.jobTimeline)
        router.upkeepPath.append(AppDestination.maintenanceAnalytics)
        router.reportsPath.append(AppDestination.uptimeReliability)
        router.navigate(
            to: .filamentSwap(printerId: printerId, toolheadIndex: 1, jobId: nil),
            capabilities: capabilities
        )
        router.invalidatePendingNavigation()
        try? await Task.sleep(for: .milliseconds(120))

        XCTAssertTrue(router.printersPath.isEmpty)
        XCTAssertTrue(router.jobsPath.isEmpty)
        XCTAssertTrue(router.notificationsPath.isEmpty)
        XCTAssertTrue(router.inventoryPath.isEmpty)
        XCTAssertTrue(router.scanPath.isEmpty)
        XCTAssertTrue(router.oversightPath.isEmpty)
        XCTAssertTrue(router.overviewPath.isEmpty)
        XCTAssertTrue(router.fleetPath.isEmpty)
        XCTAssertTrue(router.oversightJobsPath.isEmpty)
        XCTAssertTrue(router.upkeepPath.isEmpty)
        XCTAssertTrue(router.reportsPath.isEmpty)
        XCTAssertTrue(router.dashboardSheetPath.isEmpty)
        XCTAssertTrue(router.maintenanceSheetPath.isEmpty)
        XCTAssertTrue(router.notificationsSheetPath.isEmpty)
        XCTAssertNil(router.pendingAttentionItemId)
        XCTAssertNil(router.pendingSpoolHighlightId)
        XCTAssertNil(router.pendingFilamentSwap)
    }

    func testNotificationRoutingSurfacesInvalidDestination() {
        let router = AppRouter()

        router.routeNotification(
            userInfo: ["link": "printfarmer://attention"],
            capabilities: capabilities
        )

        XCTAssertEqual(
            router.notificationRoutingError,
            "This notification's destination is invalid for the selected server."
        )
        XCTAssertEqual(router.selectedTab, .attention)
    }

    func testNotificationRoutingSurfacesWrongServerOrigin() {
        let router = AppRouter()

        router.routeNotification(
            userInfo: [
                "originServerId": "00000000-0000-0000-0000-000000000011",
                "deepLink": "printfarmer://printer/\(printerId.uuidString)"
            ],
            activeOriginServerId: originServerId,
            capabilities: capabilities
        )

        XCTAssertEqual(router.notificationRoutingError, "This notification belongs to a different server.")
        XCTAssertEqual(router.selectedTab, .attention)
    }

    // MARK: - Reset to root

    func testResetToRootClearsAttentionPath() {
        let router = AppRouter()
        router.notificationsPath.append(AppDestination.jobDetail(id: printerId))
        XCTAssertFalse(router.notificationsPath.isEmpty)

        router.resetToRoot(tab: .attention)
        XCTAssertTrue(router.notificationsPath.isEmpty)
    }

    func testResetToRootClearsFarmPath() {
        let router = AppRouter()
        router.printersPath.append(AppDestination.printerDetail(id: printerId))
        XCTAssertFalse(router.printersPath.isEmpty)

        router.resetToRoot(tab: .farm)
        XCTAssertTrue(router.printersPath.isEmpty)
    }

    func testResetToRootClearsTasksPath() {
        let router = AppRouter()
        router.tasksPath.append("printQueue")
        router.jobsPath.append(AppDestination.jobDetail(id: printerId))
        XCTAssertFalse(router.tasksPath.isEmpty)
        XCTAssertFalse(router.jobsPath.isEmpty)

        router.resetToRoot(tab: .tasks)
        XCTAssertTrue(router.tasksPath.isEmpty)
        XCTAssertTrue(router.jobsPath.isEmpty)
    }

    func testResetToRootClearsScanPath() {
        let router = AppRouter()
        router.scanPath.append(AppDestination.jobDetail(id: printerId))
        XCTAssertFalse(router.scanPath.isEmpty)

        router.resetToRoot(tab: .scan)
        XCTAssertTrue(router.scanPath.isEmpty)
    }

    func testResetToRootClearsInventoryPath() {
        let router = AppRouter()
        router.inventoryPath.append(AppDestination.jobDetail(id: printerId))
        XCTAssertFalse(router.inventoryPath.isEmpty)

        router.resetToRoot(tab: .inventory)
        XCTAssertTrue(router.inventoryPath.isEmpty)
    }

    func testEveryAdaptiveTabResetClearsOnlyItsOwnPath() {
        let tabs: [AppTab] = [
            .oversight,
            .overview,
            .fleet,
            .jobs,
            .upkeep,
            .reports
        ]

        for tab in tabs {
            let router = AppRouter()
            seedAdaptivePaths(router)

            router.resetToRoot(tab: tab)

            for candidate in tabs {
                XCTAssertEqual(
                    adaptivePathCount(for: candidate, router: router),
                    candidate == tab ? 0 : 1,
                    "Resetting \(tab) must not alter \(candidate)"
                )
            }
        }
    }

    // MARK: - AppDestination migrations

    func testAdvancedPrinterControlsDestinationEncodesPrinterId() {
        // F1 (#706): jog/preheat/z-offset controls are only reachable via
        // this destination, which must round-trip the printer id.
        let destination = AppDestination.advancedPrinterControls(printerId: printerId)
        if case .advancedPrinterControls(let id) = destination {
            XCTAssertEqual(id, printerId)
        } else {
            XCTFail("Expected advancedPrinterControls case")
        }
    }

    func testRehomedDestinationsHaveDistinctCanonicalCases() {
        let destinations: [AppDestination] = [
            .dashboard,
            .maintenance,
            .notifications,
            .settings,
            .dispatchDashboard,
            .jobHistory,
            .jobTimeline,
            .uptimeReliability,
            .maintenanceAnalytics,
            .locations,
            .predictiveInsights(printerId: nil),
            .advancedPrinterControls(printerId: printerId),
            .offlineQueue,
            .manageServers
        ]

        XCTAssertEqual(destinations.count, 14)
        XCTAssertEqual(Set(destinations).count, destinations.count)
        XCTAssertNotEqual(AppDestination.settings, .navigationSettings)
    }

    func testPredictiveInsightsDestinationPreservesFarmAndPrinterScopes() {
        let farmWide = AppDestination.predictiveInsights(printerId: nil)
        let printer = AppDestination.predictiveInsights(printerId: printerId)

        if case .predictiveInsights(let id) = farmWide {
            XCTAssertNil(id)
        } else {
            XCTFail("Expected farm-wide predictiveInsights case")
        }

        if case .predictiveInsights(let id) = printer {
            XCTAssertEqual(id, printerId)
        } else {
            XCTFail("Expected printer-scoped predictiveInsights case")
        }
    }

    // MARK: - Legacy sheet reset (#727)

    func testResetLegacySheetDashboardClearsDashboardSheetPath() {
        let router = AppRouter()
        router.dashboardSheetPath.append(AppDestination.printerDetail(id: printerId))
        XCTAssertFalse(router.dashboardSheetPath.isEmpty)

        router.resetLegacySheet(.dashboard)
        XCTAssertTrue(router.dashboardSheetPath.isEmpty,
                      "Dismissing the Dashboard sheet must clear its owned path")
    }

    func testResetLegacySheetMaintenanceClearsMaintenanceSheetPath() {
        let router = AppRouter()
        router.maintenanceSheetPath.append(AppDestination.maintenanceAnalytics)
        XCTAssertFalse(router.maintenanceSheetPath.isEmpty)

        router.resetLegacySheet(.maintenance)
        XCTAssertTrue(router.maintenanceSheetPath.isEmpty,
                      "Dismissing the Maintenance sheet must clear its owned path")
    }

    func testResetLegacySheetNotificationsClearsNotificationsSheetPath() {
        let router = AppRouter()
        router.notificationsSheetPath.append(AppDestination.jobDetail(id: printerId))
        XCTAssertFalse(router.notificationsSheetPath.isEmpty)

        router.resetLegacySheet(.notifications)
        XCTAssertTrue(router.notificationsSheetPath.isEmpty,
                      "Dismissing the legacy Notifications sheet must clear its owned path")
    }

    func testResetLegacySheetSettingsIsNoOp() {
        // SettingsView owns a local NavigationStack, so the router has no
        // path to reset. The case must exist so callers can wire every
        // legacy sheet through the same entry point, but the router state
        // must be untouched.
        let router = AppRouter()
        router.notificationsPath.append(AppDestination.jobDetail(id: printerId))
        router.dashboardSheetPath.append(AppDestination.printerDetail(id: printerId))
        router.maintenanceSheetPath.append(AppDestination.maintenanceAnalytics)
        router.notificationsSheetPath.append(AppDestination.jobDetail(id: printerId))

        router.resetLegacySheet(.settings)

        XCTAssertFalse(router.notificationsPath.isEmpty)
        XCTAssertFalse(router.dashboardSheetPath.isEmpty)
        XCTAssertFalse(router.maintenanceSheetPath.isEmpty)
        XCTAssertFalse(router.notificationsSheetPath.isEmpty)
    }

    func testResetLegacySheetLeavesAttentionTabStackIntact() {
        // The Attention tab's `notificationsPath` MUST NOT be touched when
        // a legacy sheet is dismissed. This is the whole point of keeping
        // sheet-owned paths separate from tab-owned paths (#727).
        let router = AppRouter()
        router.notificationsPath.append(AppDestination.jobDetail(id: printerId))
        let attentionDepthBefore = router.notificationsPath.count

        for sheet in LegacySheet.allCases {
            router.resetLegacySheet(sheet)
            XCTAssertEqual(router.notificationsPath.count, attentionDepthBefore,
                           "Dismissing \(sheet) must not touch the Attention tab stack")
        }
    }

    func testResetLegacySheetLeavesOtherTabStacksIntact() {
        // Dismissing a legacy sheet must never disturb any other tab's
        // stack — Farm, Tasks, Scan, or Inventory.
        let router = AppRouter()
        router.printersPath.append(AppDestination.printerDetail(id: printerId))
        router.jobsPath.append(AppDestination.jobDetail(id: printerId))
        router.scanPath.append(AppDestination.jobDetail(id: printerId))
        router.inventoryPath.append(AppDestination.jobDetail(id: printerId))

        for sheet in LegacySheet.allCases {
            router.resetLegacySheet(sheet)
        }

        XCTAssertEqual(router.printersPath.count, 1, "Farm tab stack must be intact")
        XCTAssertEqual(router.jobsPath.count, 1, "Tasks tab stack must be intact")
        XCTAssertEqual(router.scanPath.count, 1, "Scan tab stack must be intact")
        XCTAssertEqual(router.inventoryPath.count, 1, "Inventory tab stack must be intact")
    }

    func testResetLegacySheetOnlyClearsTargetedSheet() {
        // Resetting one sheet's path must not clear any other sheet's path.
        let router = AppRouter()
        router.dashboardSheetPath.append(AppDestination.printerDetail(id: printerId))
        router.maintenanceSheetPath.append(AppDestination.maintenanceAnalytics)
        router.notificationsSheetPath.append(AppDestination.jobDetail(id: printerId))

        router.resetLegacySheet(.dashboard)
        XCTAssertTrue(router.dashboardSheetPath.isEmpty)
        XCTAssertEqual(router.maintenanceSheetPath.count, 1)
        XCTAssertEqual(router.notificationsSheetPath.count, 1)

        router.resetLegacySheet(.maintenance)
        XCTAssertTrue(router.maintenanceSheetPath.isEmpty)
        XCTAssertEqual(router.notificationsSheetPath.count, 1)

        router.resetLegacySheet(.notifications)
        XCTAssertTrue(router.notificationsSheetPath.isEmpty)
    }

    func testLegacySheetPathsDefaultEmpty() {
        // A freshly constructed router must have empty sheet stacks so
        // that opening a legacy sheet always starts at its root.
        let router = AppRouter()
        XCTAssertTrue(router.dashboardSheetPath.isEmpty)
        XCTAssertTrue(router.maintenanceSheetPath.isEmpty)
        XCTAssertTrue(router.notificationsSheetPath.isEmpty)
    }

    // MARK: - Inventory feature visibility

    func testInventorySegmentsKeepSpoolsWhenPrintedPartsAreDisabled() {
        XCTAssertEqual(
            InventoryView.Segment.available(printedPartsInventoryEnabled: false),
            [.spools]
        )
        XCTAssertEqual(
            InventoryView.Segment.resolved(
                .parts,
                printedPartsInventoryEnabled: false
            ),
            .spools
        )
    }

    func testInventorySegmentsIncludePrintedPartsWhenEnabled() {
        XCTAssertEqual(
            InventoryView.Segment.available(printedPartsInventoryEnabled: true),
            [.spools, .parts]
        )
        XCTAssertEqual(
            InventoryView.Segment.resolved(
                .parts,
                printedPartsInventoryEnabled: true
            ),
            .parts
        )
    }

    private func seedAdaptivePaths(_ router: AppRouter) {
        router.oversightPath.append(AppDestination.jobHistory)
        router.overviewPath.append(AppDestination.dispatchDashboard)
        router.fleetPath.append(AppDestination.printerDetail(id: printerId))
        router.oversightJobsPath.append(AppDestination.jobTimeline)
        router.upkeepPath.append(AppDestination.maintenanceAnalytics)
        router.reportsPath.append(AppDestination.uptimeReliability)
    }

    private func adaptivePathCount(
        for tab: AppTab,
        router: AppRouter
    ) -> Int {
        switch tab {
        case .oversight:
            return router.oversightPath.count
        case .overview:
            return router.overviewPath.count
        case .fleet:
            return router.fleetPath.count
        case .jobs:
            return router.oversightJobsPath.count
        case .upkeep:
            return router.upkeepPath.count
        case .reports:
            return router.reportsPath.count
        case .attention, .farm, .tasks, .scan, .inventory:
            XCTFail("Expected an adaptive tab")
            return -1
        }
    }
}
