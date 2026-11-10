import XCTest

@MainActor
final class ScanStationUITests: PrintFarmerUITestCase {
    override var waitsForNavigationReadiness: Bool { true }

    func testFloatingScanOpensBarcodeAndNFCFromEveryTab() {
        for id in ["tab.farm", "tab.queue", "tab.filament"] {
            let destination = shellDestinationButton(tabIdentifier: id, timeout: 8)
            XCTAssertTrue(destination.exists)
            destination.tap()
            let scan = app.buttons["navigation.scan"]
            XCTAssertTrue(scan.waitForExistence(timeout: 5))
            scan.tap()
            XCTAssertTrue(app.buttons["scan.primary"].waitForExistence(timeout: 5))
            XCTAssertTrue(app.buttons["scan.nfc"].exists)
            XCTAssertTrue(app.staticTexts["scan.nfc.hint"].exists)
            app.buttons["Done"].tap()
            XCTAssertTrue(scan.waitForExistence(timeout: 5))
        }
    }

    func testFilamentKeepsContinuousBarcodeIntake() {
        let filament = shellDestinationButton(tabIdentifier: "tab.filament", timeout: 8)
        XCTAssertTrue(filament.exists)
        filament.tap()
        app.buttons["inventory.actions"].tap()
        let intake = app.buttons["inventory.scan.barcodeIntake"]
        XCTAssertTrue(intake.waitForExistence(timeout: 5))
        intake.tap()
        XCTAssertTrue(app.navigationBars["Barcode Intake"].waitForExistence(timeout: 5))
    }

    func testUnrecognizedNonGtinScanOffersManualAddSpool() {
        relaunchAppForTest(additionalArguments: ["--uitesting-scan-code", "LOT-A1B2"])
        openScanAndScan()
        XCTAssertTrue(app.staticTexts["Unrecognized Code"].waitForExistence(timeout: 5))
        let addSpool = app.buttons["scan.unrecognized.addSpool"]
        XCTAssertTrue(addSpool.waitForExistence(timeout: 5))
        addSpool.tap()
        XCTAssertTrue(app.navigationBars["Add Spool"].waitForExistence(timeout: 8))
    }

    func testUnknownGtinScanOpensBarcodeIntake() {
        relaunchAppForTest(additionalArguments: ["--uitesting-scan-code", "4006381333931"])
        openScanAndScan()
        XCTAssertTrue(app.navigationBars["Barcode Intake"].waitForExistence(timeout: 8))
        XCTAssertTrue(app.navigationBars["Map Barcode"].waitForExistence(timeout: 8))
    }

    func testMissingSpoolLinkOffersManualAddSpool() {
        relaunchAppForTest(additionalArguments: ["--uitesting-scan-code", "printfarmer://spool/987654"])
        openScanAndScan()
        XCTAssertTrue(app.staticTexts["Spool Not Found"].waitForExistence(timeout: 8))
        let addSpool = app.buttons["scan.unrecognized.addSpool"]
        XCTAssertTrue(addSpool.waitForExistence(timeout: 5))
        addSpool.tap()
        XCTAssertTrue(app.navigationBars["Add Spool"].waitForExistence(timeout: 8))
        XCTAssertFalse(app.navigationBars["Barcode Intake"].exists, "a spool link never enters GTIN intake")
    }

    private func openScanAndScan() {
        let scan = app.buttons["navigation.scan"]
        XCTAssertTrue(scan.waitForExistence(timeout: 8))
        scan.tap()
        let primary = app.buttons["scan.primary"]
        XCTAssertTrue(primary.waitForExistence(timeout: 5))
        primary.tap()
    }
}
