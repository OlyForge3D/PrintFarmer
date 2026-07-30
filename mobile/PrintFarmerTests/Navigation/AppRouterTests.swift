import XCTest
@testable import PrintFarmer

/// Tests for `AppRouter` deep-link tab mapping and root reset behavior.
///
/// F1 (#706) redefines the operator shell as five tabs (attention, farm,
/// tasks, scan, inventory). This suite verifies that:
/// * `printerDetail` / `printerReady` deep links land on the Farm tab.
/// * `spoolDetail` deep links land on the Inventory tab.
/// * `resetToRoot(tab:)` clears the path bound to each new tab without
///   throwing on the previously-existing `.dashboard`, `.notifications`,
///   `.jobs`, `.maintenance`, or `.settings` cases (which have been
///   removed from `AppTab`).
/// * The default `selectedTab` is `.attention` so the app launches into
///   Attention as required by issue #706.
@MainActor
final class AppRouterTests: XCTestCase {

    private let printerId = UUID(uuidString: "00000000-0000-0000-0000-000000000001")!
    private let spoolId = 42

    // MARK: - Defaults

    func testDefaultSelectedTabIsAttention() {
        let router = AppRouter()
        XCTAssertEqual(router.selectedTab, .attention)
    }

    func testAppTabHasFiveOperatorDestinations() {
        // Guard against accidental re-introduction of dashboard / jobs /
        // notifications / maintenance / settings tabs. F1 collapses those
        // into the five operator destinations.
        let expected: Set<AppTab> = [.attention, .farm, .tasks, .scan, .inventory]
        XCTAssertEqual(Set(AppTab.allCases), expected)
        XCTAssertEqual(AppTab.allCases.count, 5)
    }

    // MARK: - Deep link routing

    func testPrinterDetailDeepLinkSelectsFarmTab() async {
        let router = AppRouter()
        router.selectedTab = .attention

        router.navigate(to: .printerDetail(id: printerId))
        XCTAssertEqual(router.selectedTab, .farm)

        // navigate() schedules a delayed append; wait past the 50 ms delay.
        try? await Task.sleep(for: .milliseconds(120))
        XCTAssertFalse(router.printersPath.isEmpty)
    }

    func testPrinterReadyDeepLinkSelectsFarmTabAndSetsPending() async {
        let router = AppRouter()
        router.selectedTab = .attention

        router.navigate(to: .printerReady(id: printerId))

        XCTAssertEqual(router.selectedTab, .farm)
        XCTAssertEqual(router.pendingNFCReadyPrinterId, printerId)

        try? await Task.sleep(for: .milliseconds(120))
        XCTAssertFalse(router.printersPath.isEmpty)
    }

    func testSpoolDetailDeepLinkSelectsInventoryTab() {
        let router = AppRouter()
        router.selectedTab = .attention

        router.navigate(to: .spoolDetail(id: spoolId))

        XCTAssertEqual(router.selectedTab, .inventory)
        XCTAssertEqual(router.pendingSpoolHighlightId, spoolId)
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
        router.jobsPath.append(AppDestination.jobDetail(id: printerId))
        XCTAssertFalse(router.jobsPath.isEmpty)

        router.resetToRoot(tab: .tasks)
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
}
