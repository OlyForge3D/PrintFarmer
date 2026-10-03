import XCTest

@MainActor
final class PartsInventoryUITests: PrintFarmerUITestCase {
    override var waitsForNavigationReadiness: Bool { true }

    func testFilamentHasSpoolsWithoutTheRetiredInventorySegments() {
        let filament = shellDestinationButton(tabIdentifier: "tab.filament", timeout: 8)
        XCTAssertTrue(filament.exists)
        filament.tap()
        XCTAssertTrue(app.navigationBars["Filament"].waitForExistence(timeout: 8))
        XCTAssertTrue(app.buttons["inventory.addSpool"].exists)
        XCTAssertFalse(app.segmentedControls["inventory.segmentPicker"].exists)
    }

    func testPrintedPartsEntryOpensStockRowAndQuantityAdjustment() {
        shellDestinationButton(tabIdentifier: "tab.filament", timeout: 8).tap()
        let parts = app.buttons["filament.printedParts"]
        XCTAssertTrue(parts.waitForExistence(timeout: 8))
        parts.tap()
        XCTAssertTrue(app.navigationBars["Printed Parts"].waitForExistence(timeout: 8))
        let row = app.buttons["partsInventory.row.BRKT-01"]
        XCTAssertTrue(row.waitForExistence(timeout: 8))
        row.tap()
        XCTAssertTrue(app.steppers["partScan.deltaStepper"].waitForExistence(timeout: 5))
        XCTAssertTrue(app.buttons["partScan.applyAdjustment"].exists)
    }

    func testDisabledCapabilityHidesPrintedPartsEntry() {
        app.terminate()
        app.launchArguments.append("--uitesting-operator-features-disabled")
        app.launch()
        let filament = shellDestinationButton(tabIdentifier: "tab.filament", timeout: 8)
        XCTAssertTrue(filament.exists)
        filament.tap()
        XCTAssertTrue(app.buttons["inventory.addSpool"].waitForExistence(timeout: 8))
        XCTAssertFalse(app.buttons["filament.printedParts"].exists)
    }
}
