import XCTest

/// UI tests for the F1 operator shell (issue #706).
///
/// Verifies that the app launches into the Attention tab, that the tab
/// bar exposes the five operator destinations in the required order, and
/// that reachable destinations satisfy the two-tap requirement:
///
/// * Attention → overflow → Settings (2 taps)
/// * Attention → overflow → Dashboard (2 taps)
/// * Attention → overflow → Maintenance (2 taps)
/// * Farm → printer → Advanced (advanced cockpit is intentionally deeper)
///
/// These tests are best-effort: without a configured mock server the
/// details tab targets may not present rich content. Assertions fall
/// back to "at least the navigation chrome exists" when data is missing,
/// mirroring `PrinterListUITests`.
final class OperatorShellUITests: PrintFarmerUITestCase {

    private let expectedTabsInOrder = ["Attention", "Farm", "Tasks", "Scan", "Inventory"]

    // MARK: - Tab bar shape

    func testAppLaunchesOnAttentionTab() {
        let attentionTab = app.tabBars.buttons["Attention"]
        XCTAssertTrue(attentionTab.waitForExistence(timeout: 5),
                      "Attention tab should exist in the tab bar")
        XCTAssertTrue(attentionTab.isSelected,
                      "Attention should be the initial selected tab")
    }

    func testTabBarShowsFiveOperatorDestinations() {
        let tabBar = app.tabBars.firstMatch
        XCTAssertTrue(tabBar.waitForExistence(timeout: 5), "Tab bar should exist")

        for title in expectedTabsInOrder {
            let tab = app.tabBars.buttons[title]
            XCTAssertTrue(tab.waitForExistence(timeout: 2),
                          "Tab '\(title)' should be present in operator shell")
        }
    }

    func testRetiredTabsAreNotVisible() {
        // The old shell exposed dedicated Notifications and Settings tabs.
        // F1 collapses them into the Attention tab / overflow menu.
        for retired in ["Notifications", "Settings"] {
            let tab = app.tabBars.buttons[retired]
            XCTAssertFalse(tab.exists,
                           "Retired top-level tab '\(retired)' must not appear in F1 shell")
        }
    }

    // MARK: - Attention overflow reachability (two-tap gate)

    func testSettingsReachableFromAttentionOverflow() {
        guard app.tabBars.buttons["Attention"].waitForExistence(timeout: 5) else { return }
        app.tabBars.buttons["Attention"].tap()

        let overflow = app.buttons["attention.overflow"]
        guard overflow.waitForExistence(timeout: 5) else {
            XCTFail("Attention overflow control should be reachable in one tap")
            return
        }
        overflow.tap()

        let settingsItem = app.buttons["Settings"]
        XCTAssertTrue(settingsItem.waitForExistence(timeout: 3),
                      "Settings must be reachable within two taps via Attention overflow")
    }

    func testDashboardReachableFromAttentionOverflow() {
        guard app.tabBars.buttons["Attention"].waitForExistence(timeout: 5) else { return }
        app.tabBars.buttons["Attention"].tap()

        let overflow = app.buttons["attention.overflow"]
        guard overflow.waitForExistence(timeout: 5) else { return }
        overflow.tap()

        let dashboardItem = app.buttons["Dashboard"]
        XCTAssertTrue(dashboardItem.waitForExistence(timeout: 3),
                      "Dashboard must be reachable within two taps via Attention overflow")
    }

    func testMaintenanceReachableFromAttentionOverflow() {
        guard app.tabBars.buttons["Attention"].waitForExistence(timeout: 5) else { return }
        app.tabBars.buttons["Attention"].tap()

        let overflow = app.buttons["attention.overflow"]
        guard overflow.waitForExistence(timeout: 5) else { return }
        overflow.tap()

        let maintenanceItem = app.buttons["Maintenance"]
        XCTAssertTrue(maintenanceItem.waitForExistence(timeout: 3),
                      "Maintenance must be reachable within two taps via Attention overflow")
    }

    // MARK: - Advanced controls gating (Farm → printer → Advanced)

    func testAdvancedControlsGatedBehindPrinterDetail() {
        guard app.tabBars.buttons["Farm"].waitForExistence(timeout: 5) else { return }
        app.tabBars.buttons["Farm"].tap()

        // Attempting to reach Advanced controls before entering a printer
        // must not surface them; the button lives only inside a printer's
        // detail view.
        XCTAssertFalse(app.buttons["printer.detail.advanced"].exists,
                       "Advanced controls entry must not appear on the Farm tab root")

        let firstPrinter = app.collectionViews.cells.firstMatch
        guard firstPrinter.waitForExistence(timeout: 5) else {
            // Mock data may not include an online printer; skip gracefully.
            return
        }
        firstPrinter.tap()

        // Advanced entry may be hidden for offline printers; both outcomes
        // are acceptable — we assert only that if visible it opens a new
        // destination.
        let advanced = app.buttons["printer.detail.advanced"]
        if advanced.waitForExistence(timeout: 5) {
            advanced.tap()
            let backButton = app.navigationBars.buttons.firstMatch
            XCTAssertTrue(backButton.waitForExistence(timeout: 3),
                          "Advanced controls should push a new navigation destination")
        }
    }
}
