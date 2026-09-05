import XCTest

/// UI coverage for scanner flows after the dedicated Scan tab was retired.
@MainActor
final class ScanStationUITests: PrintFarmerUITestCase {
    private let printerID = "10000000-0001-0000-0000-000000000001"

    private func openDestination(_ tabIdentifier: String) {
        let destination = shellDestinationButton(
            tabIdentifier: tabIdentifier,
            timeout: 8
        )
        XCTAssertTrue(destination.exists)
        destination.tap()
    }

    private func openInventoryScanMenu() {
        openDestination("tab.inventory")
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
        openDestination("tab.inventory")

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

        let partRow = app.buttons["inventory.partLookup.row.BRKT-01"]
        XCTAssertTrue(partRow.waitForExistence(timeout: 5))
        partRow.tap()
        XCTAssertTrue(app.navigationBars["Mounting Bracket"].waitForExistence(timeout: 5))
    }

    func testFarmPrinterLookupNavigatesToPrinterDetail() {
        openDestination("tab.farm")

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

@MainActor
final class AttentionHarvestScanUITests: PrintFarmerUITestCase {
    private let harvestAttentionID =
        "harvest:78000000-0000-0000-0000-000000000004"

    override var additionalLaunchArguments: [String] {
        ["--uitesting-attention-harvest-scan"]
    }

    func testHarvestScanOpensFromAttentionItem() {
        let attention = shellDestinationButton(
            tabIdentifier: "tab.attention",
            timeout: 8
        )
        XCTAssertTrue(attention.exists)
        attention.tap()

        let scanBin = app.buttons[
            "attention.item.\(harvestAttentionID).action.scanBin"
        ]
        if !scanBin.waitForExistence(timeout: 8) {
            app.swipeUp()
        }
        XCTAssertTrue(scanBin.waitForExistence(timeout: 5))
        scanBin.tap()

        XCTAssertTrue(app.navigationBars["Harvest Plate"].waitForExistence(timeout: 8))
        XCTAssertTrue(app.buttons["harvest.scanBin"].waitForExistence(timeout: 5))
    }
}
