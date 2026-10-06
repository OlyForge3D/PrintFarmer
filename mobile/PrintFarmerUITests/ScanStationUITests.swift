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
}
