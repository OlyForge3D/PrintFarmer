import XCTest

@MainActor
final class PartsInventoryUITests: PrintFarmerUITestCase {
    override var waitsForNavigationReadiness: Bool { true }

    func testFilamentHasSpoolsWithoutTheRetiredInventorySegments() {
        let filament = shellDestinationButton(tabIdentifier: "tab.filament", timeout: 8)
        XCTAssertTrue(filament.exists)
        filament.tap()
        XCTAssertTrue(
            app.staticTexts["navigation.title"].waitForExistence(timeout: 8)
                || app.navigationBars["Filament"].waitForExistence(timeout: 8)
        )
        XCTAssertTrue(app.buttons["inventory.addSpool"].exists)
        XCTAssertFalse(app.segmentedControls["inventory.segmentPicker"].exists)
    }

    func testPrintedPartsEntryOpensStockRowAndQuantityAdjustment() {
        shellDestinationButton(tabIdentifier: "tab.filament", timeout: 8).tap()
        app.buttons["inventory.actions"].tap()
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

    func testAddSpoolSwatchesHaveMinimumTouchTargetsAndPaddedCornersSelectColor() {
        shellDestinationButton(tabIdentifier: "tab.filament", timeout: 8).tap()
        let addSpool = app.buttons["inventory.addSpool"]
        XCTAssertTrue(addSpool.waitForExistence(timeout: 8))
        addSpool.tap()
        let createFilament = app.switches["addSpool.createFilament"]
        XCTAssertTrue(createFilament.waitForExistence(timeout: 8))
        if createFilament.value as? String != "1" {
            createFilament.tap()
        }
        XCTAssertEqual(createFilament.value as? String, "1")

        let form = app.collectionViews.containing(.switch, identifier: "addSpool.createFilament").firstMatch
        XCTAssertTrue(form.waitForExistence(timeout: 5))
        let red = app.buttons["Red"]
        for _ in 0..<12 {
            if red.exists && red.isHittable { break }
            form.swipeUp()
        }
        XCTAssertTrue(red.isHittable, "The Red swatch must be reachable in the Add Spool form")
        for name in ["Black", "White", "Red"] {
            let swatch = app.buttons[name]
            XCTAssertTrue(swatch.exists, name)
            XCTAssertEqual(swatch.label, name)
            XCTAssertGreaterThanOrEqual(swatch.frame.width, 44, name)
            XCTAssertGreaterThanOrEqual(swatch.frame.height, 44, name)
        }

        red.coordinate(withNormalizedOffset: CGVector(dx: 0.05, dy: 0.05)).tap()
        let selected = XCTNSPredicateExpectation(
            predicate: NSPredicate(format: "selected == true"), object: red
        )
        XCTAssertEqual(XCTWaiter.wait(for: [selected], timeout: 3), .completed)
        XCTAssertEqual(app.textFields["#RRGGBB"].value as? String, "#E53E3E")
        XCTAssertFalse(app.buttons["Black"].isSelected)
    }

    func testScanFromPrintedPartsSheetReturnsInPlace() {
        shellDestinationButton(tabIdentifier: "tab.filament", timeout: 8).tap()
        app.buttons["inventory.actions"].tap()
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
        app.buttons["inventory.actions"].tap()
        XCTAssertFalse(app.buttons["filament.printedParts"].exists)
    }

    func testReorderWarningAndFilterRemainAvailableInSecondaryStockList() {
        shellDestinationButton(tabIdentifier: "tab.filament", timeout: 8).tap()
        app.buttons["inventory.actions"].tap()
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
