import XCTest

/// UI tests for the F9 harvest entry point (issue #714).
///
/// Verifies that a completed job's detail view exposes a "Harvest to
/// Inventory" action (gated on the shared printed-parts-inventory feature
/// flag, which defaults to enabled in the demo bootstrap) and that tapping
/// it presents the real `HarvestSheetView` with its form controls.
///
/// Demo job `30000000-0003-0000-0000-000000000007` (`DemoData.job7ID`) is
/// seeded as `Completed`, giving a deterministic target without depending
/// on Recent-page sort order.
final class HarvestUITests: PrintFarmerUITestCase {

    private let completedJobIdentifier = "job.row.30000000-0003-0000-0000-000000000007"

    /// Navigates Tasks → Recent page → the seeded completed demo job.
    private func openCompletedJobDetail() {
        let tasksTab = app.tabBars.buttons["Tasks"]
        XCTAssertTrue(tasksTab.waitForExistence(timeout: 5), "Tasks tab should exist in the operator shell")
        tasksTab.tap()

        let recentPageLabel = app.buttons["Recent"]
        XCTAssertTrue(recentPageLabel.waitForExistence(timeout: 5),
                      "Tasks tab should expose a Recent page for completed/failed/cancelled jobs")
        recentPageLabel.tap()

        let jobRow = app.buttons[completedJobIdentifier]
        XCTAssertTrue(jobRow.waitForExistence(timeout: 5),
                      "Seeded completed demo job should render in the Recent page")
        jobRow.tap()
    }

    func testCompletedJobExposesHarvestAction() {
        openCompletedJobDetail()

        let harvestButton = app.buttons["jobDetail.harvestToInventory"]
        XCTAssertTrue(harvestButton.waitForExistence(timeout: 5),
                      "Completed job with printed-parts inventory enabled must offer Harvest to Inventory")
    }

    func testHarvestActionPresentsHarvestSheet() {
        openCompletedJobDetail()

        let harvestButton = app.buttons["jobDetail.harvestToInventory"]
        XCTAssertTrue(harvestButton.waitForExistence(timeout: 5))
        harvestButton.tap()

        let sheetTitle = app.navigationBars["Harvest Plate"]
        XCTAssertTrue(sheetTitle.waitForExistence(timeout: 5),
                      "Harvest action should present the Harvest Plate sheet")

        XCTAssertTrue(app.textFields["harvest.binCode"].waitForExistence(timeout: 3),
                      "Harvest sheet should expose a destination bin code field")
        XCTAssertTrue(app.buttons["harvest.submit"].waitForExistence(timeout: 3),
                      "Harvest sheet should expose a submit action")
    }

    func testCancellingHarvestSheetDismissesWithoutSubmitting() {
        openCompletedJobDetail()

        let harvestButton = app.buttons["jobDetail.harvestToInventory"]
        XCTAssertTrue(harvestButton.waitForExistence(timeout: 5))
        harvestButton.tap()

        let sheetTitle = app.navigationBars["Harvest Plate"]
        XCTAssertTrue(sheetTitle.waitForExistence(timeout: 5))

        app.navigationBars["Harvest Plate"].buttons["Cancel"].tap()

        XCTAssertFalse(app.navigationBars["Harvest Plate"].exists,
                       "Cancel should dismiss the harvest sheet without submitting")
        // The harvest action must remain reachable — cancelling is
        // non-destructive to the job's completed state.
        XCTAssertTrue(app.buttons["jobDetail.harvestToInventory"].waitForExistence(timeout: 3))
    }

    /// H3 (remediation): a manually-added output row (via "Add SKU") must
    /// expose a real SKU picker rather than a free-text field, so operators
    /// can only select from known printed-part SKUs. The row's picker
    /// identifier embeds a per-row UUID, so it is matched with a
    /// `BEGINSWITH` predicate rather than a literal identifier.
    func testAddSkuRowExposesSkuPicker() {
        openCompletedJobDetail()

        let harvestButton = app.buttons["jobDetail.harvestToInventory"]
        XCTAssertTrue(harvestButton.waitForExistence(timeout: 5))
        harvestButton.tap()
        XCTAssertTrue(app.navigationBars["Harvest Plate"].waitForExistence(timeout: 5))

        let addSkuButton = app.buttons["harvest.addSku"]
        XCTAssertTrue(addSkuButton.waitForExistence(timeout: 5))
        addSkuButton.tap()

        let skuPickerPredicate = NSPredicate(format: "identifier BEGINSWITH 'harvest.output.skuPicker.'")
        let skuPicker = app.descendants(matching: .any).matching(skuPickerPredicate).firstMatch
        XCTAssertTrue(skuPicker.waitForExistence(timeout: 5),
                      "A manually-added output row must expose a SKU picker, not a free-text field")
    }

    /// H3 (remediation): the harvest sheet must expose a labeled
    /// destination-bin scan/selection affordance in addition to the manual
    /// bin-code text field. The demo/UI-test `ServiceContainer` runs with no
    /// camera scanner (`barcodeScannerService == nil`, matching
    /// `ScanStationUITests`), so tapping it deterministically falls back to
    /// the `BinPickerView` selection list rather than presenting a camera.
    func testScanOrSelectBinAffordanceFallsBackToBinPickerWithoutScanner() {
        openCompletedJobDetail()

        let harvestButton = app.buttons["jobDetail.harvestToInventory"]
        XCTAssertTrue(harvestButton.waitForExistence(timeout: 5))
        harvestButton.tap()
        XCTAssertTrue(app.navigationBars["Harvest Plate"].waitForExistence(timeout: 5))

        let scanBinButton = app.buttons["harvest.scanBin"]
        XCTAssertTrue(scanBinButton.waitForExistence(timeout: 5),
                      "Harvest sheet should expose a labeled Scan or Select Bin affordance")
        scanBinButton.tap()

        let binPickerTitle = app.navigationBars["Select Bin"]
        XCTAssertTrue(binPickerTitle.waitForExistence(timeout: 5),
                      "Without a scanner, the bin affordance should fall back to a bin selection list")
    }
}
