import XCTest

/// UI tests for the F9 printed-parts inventory list (issue #714).
///
/// Verifies the accessibility cue and critical adjustment-sheet interaction
/// against the deterministic printed-parts catalog. Repository loading and
/// list filtering are covered by `PartsInventoryViewModelTests`.
@MainActor
final class PartsInventoryUITests: PrintFarmerUITestCase {
    override var waitsForNavigationReadiness: Bool { true }

    private func openInventory() {
        let inventory = shellDestinationButton(
            tabIdentifier: "tab.inventory",
            timeout: 8
        )
        XCTAssertTrue(inventory.exists)
        inventory.tap()
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

}
