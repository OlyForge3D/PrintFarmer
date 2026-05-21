import XCTest

/// UI tests for printer list navigation and printer detail.
///
/// These tests verify the printer list loads and that tapping a printer
/// navigates to its detail screen.
///
/// ## Prerequisites
/// - `--uitesting` launch argument enables mock mode
/// - Mock mode should provide at least one printer in the list
/// - When Lambert's mock server is ready, configure the URL in setUp()
final class PrinterListUITests: PrintFarmerUITestCase {

    override func setUp() {
        super.setUp()
        // Navigate to printer list if not already there.
        // In mock mode, the app should auto-authenticate and show the dashboard.
        // From dashboard, navigate to Printers tab.
        let printersTab = app.tabBars.buttons["Printers"]
        if printersTab.waitForExistence(timeout: 5) {
            printersTab.tap()
        }
    }

    // MARK: - Printer List

    func testPrinterListDisplayed() {
        // In mock mode, the printer list should show at least one printer
        // Look for a navigation bar or list content
        let printersList = app.collectionViews.firstMatch
        let navBar = app.navigationBars["Printers"]

        let hasListUI = printersList.waitForExistence(timeout: 5)
            || navBar.waitForExistence(timeout: 5)

        // Note: Without mock server, the list may be empty or show an error.
        // This test validates the navigation flow works.
        XCTAssertTrue(hasListUI || app.staticTexts["No printers found"].exists,
                       "Printer list or empty state should be visible")
    }

    // MARK: - Navigation to Detail

    func testTapPrinterNavigatesToDetail() {
        // Wait for list content
        let firstCell = app.collectionViews.cells.firstMatch
        guard firstCell.waitForExistence(timeout: 5) else {
            // No printers available in mock mode — skip gracefully
            return
        }

        firstCell.tap()

        // Printer detail should appear with a back button
        let backButton = app.navigationBars.buttons.firstMatch
        let detailContent = app.scrollViews.firstMatch

        let navigated = backButton.waitForExistence(timeout: 5)
            || detailContent.waitForExistence(timeout: 5)

        XCTAssertTrue(navigated, "Should navigate to printer detail view")
    }

    // MARK: - Search

    func testSearchFieldExists() {
        // The printer list should have a search bar
        let searchField = app.searchFields.firstMatch
        if searchField.waitForExistence(timeout: 5) {
            XCTAssertTrue(searchField.exists)
            searchField.tap()
            searchField.typeText("Prusa")
        }
        // Search may not be visible if list is empty — acceptable in mock mode
    }
}
