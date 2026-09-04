import XCTest

/// UI coverage for scanner flows after the dedicated Scan tab was retired.
@MainActor
final class ScanStationUITests: PrintFarmerUITestCase {
    private let printerID = "10000000-0001-0000-0000-000000000001"

    private func openDestination(tabTitle: String, sidebarIdentifier: String) {
        let tab = app.tabBars.buttons[tabTitle]
        if tab.waitForExistence(timeout: 5) {
            tab.tap()
            return
        }

        let sidebar = app.buttons[sidebarIdentifier]
        if sidebar.waitForExistence(timeout: 3) {
            sidebar.tap()
            return
        }

        for label in ["Sidebar", "Toggle Sidebar", "Show Sidebar"] {
            let toggle = app.navigationBars.buttons[label]
            if toggle.exists {
                toggle.tap()
                if sidebar.waitForExistence(timeout: 3) {
                    sidebar.tap()
                    return
                }
            }
        }

        XCTFail("\(tabTitle) should be reachable from the operator shell")
    }

    private func openInventoryScanMenu() {
        openDestination(tabTitle: "Inventory", sidebarIdentifier: "sidebar.inventory")
        let scanMenu = app.buttons["inventory.scan"]
        XCTAssertTrue(scanMenu.waitForExistence(timeout: 5))
        XCTAssertEqual(
            app.buttons.matching(identifier: "inventory.scan").count,
            1,
            "Inventory must expose exactly one root scan affordance"
        )
        scanMenu.tap()
    }

    func testInventoryScanMenuOpensPrimaryScannerAndNFCHint() {
        openInventoryScanMenu()

        let scanCode = app.buttons["Scan code"]
        XCTAssertTrue(scanCode.waitForExistence(timeout: 5))
        XCTAssertTrue(app.buttons["inventory.scan.nfc"].exists)
        scanCode.tap()

        let scanButton = app.buttons["scan.primary"]
        XCTAssertTrue(scanButton.waitForExistence(timeout: 5))
        XCTAssertEqual(scanButton.label, "Scan")
        // Demo/UI-test services intentionally provide no camera scanner.
        XCTAssertFalse(scanButton.isEnabled)
        XCTAssertTrue(app.staticTexts["scan.nfc.hint"].exists)
    }

    func testInventoryScanMenuOpensContinuousSpoolIntake() {
        openInventoryScanMenu()

        let intakeAction = app.buttons["inventory.scan.barcodeIntake"]
        XCTAssertTrue(intakeAction.waitForExistence(timeout: 5))
        intakeAction.tap()

        XCTAssertTrue(app.navigationBars["Barcode Intake"].waitForExistence(timeout: 5))
    }

    func testPrintedPartsSegmentOffersPartLookupBehindSingleScanMenu() {
        openDestination(tabTitle: "Inventory", sidebarIdentifier: "sidebar.inventory")

        let partsSegment = app.buttons["Printed Parts"]
        XCTAssertTrue(partsSegment.waitForExistence(timeout: 5))
        partsSegment.tap()

        let scanMenu = app.buttons["inventory.scan"]
        XCTAssertTrue(scanMenu.waitForExistence(timeout: 5))
        XCTAssertEqual(app.buttons.matching(identifier: "inventory.scan").count, 1)
        scanMenu.tap()

        let partLookup = app.buttons["inventory.partLookup"]
        XCTAssertTrue(partLookup.waitForExistence(timeout: 5))
        partLookup.tap()

        XCTAssertTrue(
            app.buttons["Cancel"].waitForExistence(timeout: 5),
            "Printed-part lookup should present from Inventory"
        )

        let partRow = app.buttons["inventory.partLookup.row.BRKT-01"]
        XCTAssertTrue(partRow.waitForExistence(timeout: 5))
        partRow.tap()

        XCTAssertTrue(
            app.navigationBars["Mounting Bracket"].waitForExistence(timeout: 5),
            "Selecting a lookup result must present the printed-part detail"
        )
        XCTAssertTrue(app.steppers["partScan.deltaStepper"].exists)
    }

    func testFarmPrinterLookupNavigatesToPrinterDetail() {
        openDestination(tabTitle: "Farm", sidebarIdentifier: "sidebar.farm")

        let lookup = app.buttons["farm.printerLookup"]
        XCTAssertTrue(lookup.waitForExistence(timeout: 5))
        lookup.tap()

        XCTAssertTrue(app.navigationBars["Find Printer"].waitForExistence(timeout: 5))
        let printerRow = app.buttons["farm.printerLookup.row.\(printerID)"]
        XCTAssertTrue(printerRow.waitForExistence(timeout: 5))
        XCTAssertTrue(printerRow.label.contains("Prusa MK4 #1"))
        printerRow.tap()

        XCTAssertTrue(app.navigationBars["Find Printer"].waitForNonExistence(timeout: 5))
        let destination = app.staticTexts["printer.detail.destination.\(printerID)"]
        XCTAssertTrue(destination.waitForExistence(timeout: 8))
        XCTAssertEqual(destination.label, "Prusa MK4 #1, printer detail")
    }

    func testMaintenancePartActionRemainsAbsent() {
        openInventoryScanMenu()
        XCTAssertFalse(app.staticTexts["Log Maintenance Part"].exists)
    }
}
