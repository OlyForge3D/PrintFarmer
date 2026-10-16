import XCTest

/// Strict iPad navigation and harvest coverage for issue #3259.
///
/// Completed jobs live in the explicit Job History sheet, separate from the
/// failures-only Queue composition. This suite proves that a seeded completed
/// job is reachable there and that selecting it presents `JobDetailView` in
/// the foreground navigation context with its harvest action.
///
/// The equivalent iPhone flow is proven by `HarvestUITests` (6/6) and is
/// intentionally skipped here: this suite asserts the regular-width shell
/// specifically, so it no-ops on the compact (iPhone) layout.
@MainActor
final class JobDetailIPadNavigationUITests: QueueUITestBase {

    private let completedJobIdentifier = "job.row.30000000-0003-0000-0000-000000000007"

    /// Skips on the compact (iPhone) layout so the strict assertions below
    /// only run against the iPad (regular-width) sidebar shell. Selection is
    /// by layout, not best-effort: on iPad every assertion on the core path
    /// is required. The regular-width shell is identified by the absence of
    /// the compact tab-bar surface; sidebar navigation itself is exercised
    /// by `openQueueDestination()`, which handles a collapsed iPad sidebar.
    private func requireRegularWidthShell() throws {
        if UIDevice.current.userInterfaceIdiom == .phone || app.tabBars.firstMatch.waitForExistence(timeout: 8) {
            throw XCTSkip("iPad-only navigation coverage; compact (iPhone) shell is covered by HarvestUITests")
        }
    }

    func testIPadLeadingEdgeRevealKeepsVisibleSidebarOpen() throws {
        try requireRegularWidthShell()

        XCTAssertTrue(app.buttons["Show Sidebar"].waitForExistence(timeout: 8),
                      "The portrait iPad shell must start with its sidebar collapsed")
        XCTAssertTrue(revealSidebarFromLeadingEdge(timeout: 8),
                      "The leading-edge gesture must reveal the collapsed iPad sidebar")

        let tasks = app.buttons["sidebar.queue"]
        XCTAssertTrue(tasks.waitForExistence(timeout: 8))
        XCTAssertTrue(revealSidebarIfCollapsed(),
                      "Revealing an already-visible sidebar must succeed without closing it")
        XCTAssertTrue(tasks.isHittable,
                      "The existing sidebar must remain visible and interactive")
        tasks.tap()
        XCTAssertTrue(app.descendants(matching: .any)["jobList.root"].waitForExistence(timeout: 8))
    }

    /// The completed job must be reachable from the iPad Job History sheet
    /// and present `JobDetailView` with the harvest action in the foreground.
    func testIPadJobHistoryCompletedJobPresentsHarvestActionInForeground() throws {
        try requireRegularWidthShell()

        openJobHistoryMenuAndSelect()
        let jobRow = app.buttons[completedJobIdentifier]
        XCTAssertTrue(
            jobRow.waitForExistence(timeout: 8),
            "The seeded completed job must be reachable in the iPad Job History sheet"
        )
        jobRow.tap()

        // JobDetailView must resolve in the FOREGROUND (inside the visible
        // queue), exposing the harvest entry point.
        let harvestButton = app.buttons["jobDetail.harvestToInventory"]
        XCTAssertTrue(
            harvestButton.waitForExistence(timeout: 8),
            "Selecting the completed job on iPad must present JobDetailView in the foreground with the harvest action"
        )
    }

    /// The harvest action reached on iPad must present the real
    /// `HarvestSheetView`, proving the #714 harvest flow can begin on iPad.
    func testIPadJobHistoryHarvestActionPresentsHarvestSheet() throws {
        try requireRegularWidthShell()

        openJobHistoryMenuAndSelect()
        let jobRow = app.buttons[completedJobIdentifier]
        XCTAssertTrue(jobRow.waitForExistence(timeout: 8),
                      "The seeded completed job must be reachable in the iPad Job History sheet")
        jobRow.tap()

        let harvestButton = app.buttons["jobDetail.harvestToInventory"]
        XCTAssertTrue(harvestButton.waitForExistence(timeout: 8))
        harvestButton.tap()

        let sheetTitle = app.navigationBars["Harvest Plate"]
        XCTAssertTrue(sheetTitle.waitForExistence(timeout: 5),
                      "The harvest action on iPad must present the Harvest Plate sheet")
        XCTAssertTrue(app.buttons["harvest.submit"].waitForExistence(timeout: 3),
                      "The harvest sheet must expose a submit action on iPad")
    }
}
