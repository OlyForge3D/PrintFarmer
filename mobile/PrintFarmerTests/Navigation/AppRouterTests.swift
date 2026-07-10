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
}
