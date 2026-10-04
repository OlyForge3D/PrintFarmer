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

    func testScanFromPrintedPartsSheetReturnsInPlace() {
        shellDestinationButton(tabIdentifier: "tab.filament", timeout: 8).tap()
        app.buttons["filament.printedParts"].tap()
        XCTAssertTrue(app.navigationBars["Printed Parts"].waitForExistence(timeout: 8))

        let scanMenu = app.navigationBars["Printed Parts"].buttons["inventory.scan"]
        XCTAssertTrue(scanMenu.waitForExistence(timeout: 5))
        scanMenu.tap()
        app.buttons["Scan code"].tap()
        XCTAssertTrue(app.navigationBars["Scan"].waitForExistence(timeout: 5))
        app.navigationBars["Scan"].buttons["Done"].tap()

        XCTAssertTrue(app.navigationBars["Printed Parts"].waitForExistence(timeout: 5))
        XCTAssertTrue(app.buttons["partsInventory.row.BRKT-01"].exists)
    }

    func testDisabledCapabilityHidesPrintedPartsEntry() {
        app.terminate()
        app.launchArguments.append("--uitesting-operator-features-disabled")
        app.launchForPrintFarmerUITest()
        let filament = shellDestinationButton(tabIdentifier: "tab.filament", timeout: 8)
        XCTAssertTrue(filament.exists)
        filament.tap()
        XCTAssertTrue(app.buttons["inventory.addSpool"].waitForExistence(timeout: 8))
        XCTAssertFalse(app.buttons["filament.printedParts"].exists)
    }

    func testReorderWarningAndFilterRemainAvailableInSecondaryStockList() {
        shellDestinationButton(tabIdentifier: "tab.filament", timeout: 8).tap()
        let parts = app.buttons["filament.printedParts"]
        XCTAssertTrue(parts.waitForExistence(timeout: 8))
        parts.tap()
        let bracket = app.buttons["partsInventory.row.BRKT-01"]
        let clip = app.buttons["partsInventory.row.CLIP-02"]
        XCTAssertTrue(bracket.waitForExistence(timeout: 8))
        XCTAssertTrue(clip.exists)
        XCTAssertTrue(bracket.label.contains("needs reorder"))
        XCTAssertFalse(clip.label.contains("needs reorder"))
        let reorder = app.switches["partsInventory.reorderToggle"]
        XCTAssertTrue(reorder.exists)
        reorder.coordinate(withNormalizedOffset: CGVector(dx: 0.9, dy: 0.5)).tap()
        let activated = XCTNSPredicateExpectation(
            predicate: NSPredicate(format: "value == '1'"), object: reorder
        )
        XCTAssertEqual(XCTWaiter.wait(for: [activated], timeout: 3), .completed)
        let clipRemoved = XCTNSPredicateExpectation(
            predicate: NSPredicate(format: "exists == false"), object: clip
        )
        XCTAssertEqual(XCTWaiter.wait(for: [clipRemoved], timeout: 3), .completed)
        XCTAssertTrue(bracket.exists)
        XCTAssertFalse(clip.exists)
        bracket.tap()
        XCTAssertTrue(app.staticTexts["partScan.reorderWarning"].waitForExistence(timeout: 5))
        XCTAssertTrue(app.steppers["partScan.deltaStepper"].exists)
    }
}
