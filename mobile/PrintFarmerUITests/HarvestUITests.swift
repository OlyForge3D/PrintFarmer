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
}
