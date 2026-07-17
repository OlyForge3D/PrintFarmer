import XCTest

/// UI tests for the F9 printed-parts inventory list (issue #714).
///
/// Verifies the Inventory tab's segmented Spools/Printed Parts switch
/// renders real demo catalog data (seeded by `DemoPartsInventoryService`)
/// and that tapping a part row opens the reusable part-detail sheet with
/// genuine adjustment controls.
final class PartsInventoryUITests: PrintFarmerUITestCase {

    // The Inventory tab's segmented Spools/Printed Parts control surfaces
    // under the app-wide "tab.<name>" identifier that `ContentView` assigns
    // to each tab's root content (see `InventoryView.swift`), rather than a
    // separately-named identifier — it is the first native accessibility
    // element in that tab's view tree.
    private func openPrintedPartsSegment() {
        let inventoryTab = app.tabBars.buttons["Inventory"]
        XCTAssertTrue(inventoryTab.waitForExistence(timeout: 5), "Inventory tab should exist in the operator shell")
        inventoryTab.tap()

        let segmentPicker = app.segmentedControls["tab.inventory"]
        XCTAssertTrue(segmentPicker.waitForExistence(timeout: 5),
                      "Inventory tab should expose a Spools/Printed Parts segmented control")

        let partsSegment = segmentPicker.buttons["Printed Parts"]
        XCTAssertTrue(partsSegment.waitForExistence(timeout: 5))
        partsSegment.tap()
    }

    func testInventoryTabDefaultsToSpoolsSegment() {
        let inventoryTab = app.tabBars.buttons["Inventory"]
        XCTAssertTrue(inventoryTab.waitForExistence(timeout: 5))
        inventoryTab.tap()

        let segmentPicker = app.segmentedControls["tab.inventory"]
        XCTAssertTrue(segmentPicker.waitForExistence(timeout: 5))
        XCTAssertTrue(segmentPicker.buttons["Spools"].isSelected,
                      "Inventory tab should default to the existing Spools segment")
    }

    func testPrintedPartsSegmentRendersDemoCatalog() {
        openPrintedPartsSegment()

        let bracketRow = app.buttons["partsInventory.row.BRKT-01"]
        XCTAssertTrue(bracketRow.waitForExistence(timeout: 5),
                      "Demo SKU BRKT-01 should render in the printed-parts list")

        let clipRow = app.buttons["partsInventory.row.CLIP-02"]
        XCTAssertTrue(clipRow.waitForExistence(timeout: 3),
                      "Demo SKU CLIP-02 should render in the printed-parts list")
    }

    func testReorderNeededPartExposesWarningInAccessibilityLabel() {
        openPrintedPartsSegment()

        let bracketRow = app.buttons["partsInventory.row.BRKT-01"]
        XCTAssertTrue(bracketRow.waitForExistence(timeout: 5))
        XCTAssertTrue(bracketRow.label.contains("needs reorder"),
                     "BRKT-01 (onHand 4, reorderPoint 10) must surface a non-color-only reorder cue")

        let clipRow = app.buttons["partsInventory.row.CLIP-02"]
        XCTAssertTrue(clipRow.waitForExistence(timeout: 3))
        XCTAssertFalse(clipRow.label.contains("needs reorder"),
                      "CLIP-02 (onHand 32, reorderPoint 15) should not report a reorder cue")
    }

    func testTappingPartRowOpensAdjustmentSheet() {
        openPrintedPartsSegment()

        let bracketRow = app.buttons["partsInventory.row.BRKT-01"]
        XCTAssertTrue(bracketRow.waitForExistence(timeout: 5))
        bracketRow.tap()

        let title = app.navigationBars["Mounting Bracket"]
        XCTAssertTrue(title.waitForExistence(timeout: 5),
                      "Tapping a part row should present its detail sheet titled with the part's name")

        XCTAssertTrue(app.otherElements["partScan.deltaStepper"].waitForExistence(timeout: 3)
            || app.steppers["partScan.deltaStepper"].waitForExistence(timeout: 3),
            "Part detail sheet should expose the manual adjustment stepper")

        let applyButton = app.buttons["partScan.applyAdjustment"]
        XCTAssertTrue(applyButton.waitForExistence(timeout: 3))
    }

    func testReorderOnlyToggleFiltersList() {
        openPrintedPartsSegment()

        XCTAssertTrue(app.buttons["partsInventory.row.CLIP-02"].waitForExistence(timeout: 5))

        let toggle = app.switches["partsInventory.reorderToggle"]
        XCTAssertTrue(toggle.waitForExistence(timeout: 3))
        // Tap the nested native switch control rather than the identified
        // row container — the row's synthesized tap coordinate does not
        // reliably land on the interactive switch inside a List/Form.
        toggle.switches.firstMatch.tap()

        XCTAssertTrue(app.buttons["partsInventory.row.BRKT-01"].waitForExistence(timeout: 3),
                      "BRKT-01 needs reorder and should remain visible when the toggle is on")
        XCTAssertFalse(app.buttons["partsInventory.row.CLIP-02"].exists,
                       "CLIP-02 does not need reorder and should be hidden when the toggle is on")
    }
}
