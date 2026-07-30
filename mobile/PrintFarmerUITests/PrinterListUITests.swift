import XCTest

/// UI tests for deterministic Farm list rendering and stable-ID printer navigation.
@MainActor
final class PrinterListUITests: PrintFarmerUITestCase {
    private let printerID = "10000000-0001-0000-0000-000000000001"

    private func openFarm() {
        let farmTab = app.tabBars.buttons["Farm"]
        if farmTab.waitForExistence(timeout: 5) {
            farmTab.tap()
            return
        }

        let sidebarFarm = app.buttons["sidebar.farm"]
        if sidebarFarm.waitForExistence(timeout: 3) {
            sidebarFarm.tap()
            return
        }

        for label in ["Sidebar", "Toggle Sidebar", "Show Sidebar"] {
            let toggle = app.navigationBars.buttons[label]
            if toggle.exists {
                toggle.tap()
                if sidebarFarm.waitForExistence(timeout: 3) {
                    sidebarFarm.tap()
                    return
                }
            }
        }

        XCTFail("Farm destination should be reachable from the operator shell")
    }

    func testPrinterListDisplayed() {
        openFarm()
        XCTAssertTrue(app.navigationBars["Farm"].waitForExistence(timeout: 5))

        let printerCard = app.buttons["farm-card-\(printerID)"]
        XCTAssertTrue(
            printerCard.waitForExistence(timeout: 5),
            "The deterministic UI-test fleet should expose its first printer card"
        )
        XCTAssertTrue(printerCard.label.contains("Prusa MK4 #1"))
    }

    func testTapPrinterNavigatesToDetail() {
        openFarm()
        let printerCard = app.buttons["farm-card-\(printerID)"]
        XCTAssertTrue(printerCard.waitForExistence(timeout: 5))
        printerCard.tap()

        let detail = app.scrollViews["printer.detail.root.\(printerID)"]
        XCTAssertTrue(
            detail.waitForExistence(timeout: 8),
            "Tapping the stable printer card should open that printer's detail"
        )

        let destination = app.staticTexts["printer.detail.destination.\(printerID)"]
        XCTAssertTrue(destination.exists)
        XCTAssertEqual(destination.label, "Prusa MK4 #1, printer detail")
    }

    func testSearchFieldExists() {
        openFarm()
        let searchField = app.searchFields.firstMatch
        XCTAssertTrue(
            searchField.waitForExistence(timeout: 5),
            "Farm should expose printer search when the deterministic fleet is loaded"
        )
        searchField.tap()
        searchField.typeText("Prusa")
        XCTAssertTrue(app.buttons["farm-card-\(printerID)"].exists)
    }
}
