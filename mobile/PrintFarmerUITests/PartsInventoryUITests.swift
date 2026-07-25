import XCTest

/// UI tests for the F9 printed-parts inventory list (issue #714).
///
/// Verifies the Inventory tab's segmented Spools/Printed Parts switch
/// renders real demo catalog data (seeded by `DemoPartsInventoryService`)
/// and that tapping a part row opens the reusable part-detail sheet with
/// genuine adjustment controls.
@MainActor
final class PartsInventoryUITests: PrintFarmerUITestCase {

    private func openInventory() {
        let inventoryTab = app.tabBars.buttons["Inventory"]
        if inventoryTab.waitForExistence(timeout: 5) {
            inventoryTab.tap()
            return
        }

        let sidebarInventory = app.buttons["sidebar.inventory"]
        if sidebarInventory.waitForExistence(timeout: 3) {
            sidebarInventory.tap()
            return
        }

        for label in ["Sidebar", "Toggle Sidebar", "Show Sidebar"] {
            let toggle = app.navigationBars.buttons[label]
            if toggle.exists {
                toggle.tap()
                if sidebarInventory.waitForExistence(timeout: 3) {
                    sidebarInventory.tap()
                    return
                }
            }
        }

        XCTFail("Inventory destination should be reachable from the operator shell")
    }

    private func openPrintedPartsSegment() {
        openInventory()

        let segmentPicker = app.segmentedControls["inventory.segmentPicker"]
        XCTAssertTrue(segmentPicker.waitForExistence(timeout: 5),
                      "Inventory tab should expose a Spools/Printed Parts segmented control")

        let partsSegment = segmentPicker.buttons["Printed Parts"]
        XCTAssertTrue(partsSegment.waitForExistence(timeout: 5))
        partsSegment.tap()
    }

    func testInventoryTabDefaultsToSpoolsSegment() {
        openInventory()

        let segmentPicker = app.segmentedControls["inventory.segmentPicker"]
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

        XCTAssertTrue(app.steppers["partScan.deltaStepper"].waitForExistence(timeout: 3),
            "Part detail sheet should expose the manual adjustment stepper")

        let applyButton = app.buttons["partScan.applyAdjustment"]
        XCTAssertTrue(applyButton.waitForExistence(timeout: 3))
    }

    func testReorderOnlyToggleFiltersList() {
        openPrintedPartsSegment()

        let clipRow = app.buttons["partsInventory.row.CLIP-02"]
        XCTAssertTrue(clipRow.waitForExistence(timeout: 5))

        let toggle = app.switches["partsInventory.reorderToggle"]
        XCTAssertTrue(toggle.waitForExistence(timeout: 3))
        toggle.coordinate(withNormalizedOffset: CGVector(dx: 0.9, dy: 0.5)).tap()

        let toggleActivated = XCTNSPredicateExpectation(
            predicate: NSPredicate(format: "value == '1'"),
            object: toggle
        )
        XCTAssertEqual(
            XCTWaiter.wait(for: [toggleActivated], timeout: 3),
            .completed,
            "Needs Reorder Only should expose its active state through the accessible switch control"
        )

        let clipRemoved = XCTNSPredicateExpectation(
            predicate: NSPredicate(format: "exists == false"),
            object: clipRow
        )
        XCTAssertEqual(
            XCTWaiter.wait(for: [clipRemoved], timeout: 3),
            .completed,
            "Filtering should remove non-reorder rows from the accessibility hierarchy"
        )
        XCTAssertTrue(app.buttons["partsInventory.row.BRKT-01"].exists,
                      "BRKT-01 needs reorder and should remain visible when the toggle is on")
        XCTAssertFalse(clipRow.exists,
                       "CLIP-02 does not need reorder and should be hidden when the toggle is on")
    }
}
