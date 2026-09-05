import XCTest

/// Strict iPad navigation coverage for issue #794.
///
/// Before the fix, the Tasks destination presented the preserved
/// `JobListView` inside the operator shell's `NavigationSplitView` detail
/// column, and the iPad List layout gated its Recent (completed / failed /
/// cancelled) jobs behind a `Section(isExpanded:)`. Under the `.plain` list
/// style that collapsible section renders no disclosure control, so on iPad
/// the section stayed permanently collapsed: completed jobs — and the
/// `jobDetail.harvestToInventory` entry point reached through them — were
/// unreachable, blocking the harvest flow.
///
/// This suite proves, with strict (non-optional) assertions on the core
/// path, that on the iPad (regular width) layout a seeded completed job is
/// reachable in the Recent section **without any expansion affordance** and
/// that selecting it presents `JobDetailView` in the FOREGROUND navigation
/// context (inside the visible queue), exposing the harvest action.
///
/// The equivalent iPhone flow is proven by `HarvestUITests` (6/6) and is
/// intentionally skipped here: this suite asserts the regular-width shell
/// specifically, so it no-ops on the compact (iPhone) layout.
@MainActor
final class JobDetailIPadNavigationUITests: ShiftTasksUITestBase {

    private let completedJobIdentifier = "job.row.30000000-0003-0000-0000-000000000007"

    /// Skips on the compact (iPhone) layout so the strict assertions below
    /// only run against the iPad (regular-width) sidebar shell. Selection is
    /// by layout, not best-effort: on iPad every assertion on the core path
    /// is required. The regular-width shell is identified by the ABSENCE of
    /// the compact tab bar; sidebar navigation itself is exercised by
    /// `openTasksDestination()`, which handles a collapsed iPad sidebar.
    private func requireRegularWidthShell() throws {
        if app.tabBars.buttons["tab.tasks"].waitForExistence(timeout: 8) {
            throw XCTSkip("iPad-only navigation coverage; compact (iPhone) shell is covered by HarvestUITests")
        }
        revealSidebarIfCollapsed()
        XCTAssertTrue(
            app.buttons["sidebar.tasks"].waitForExistence(timeout: 5),
            "The regular-width two-modes shell must expose sidebar.tasks"
        )
    }

    /// #794 core path: the completed job must be reachable in the iPad
    /// Recent section and present `JobDetailView` with the harvest action in
    /// the foreground.
    func testIPadCompletedJobPresentsHarvestActionInForeground() throws {
        try requireRegularWidthShell()

        openTasksDestination()

        let printQueue = app.buttons["shiftTasks.printQueue"]
        XCTAssertTrue(printQueue.waitForExistence(timeout: 8),
                      "The iPad Tasks destination must expose the preserved Print queue link")
        printQueue.tap()

        // Regression guard: the completed job must be reachable directly in
        // the Recent section with NO expansion tap. Before the fix this row
        // never rendered on iPad because the Recent section was collapsed
        // with no disclosure control.
        let jobRow = app.buttons[completedJobIdentifier]
        XCTAssertTrue(
            jobRow.waitForExistence(timeout: 8),
            "The seeded completed job must be reachable in the iPad Recent section without any expansion affordance (#794)"
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
    func testIPadHarvestActionPresentsHarvestSheet() throws {
        try requireRegularWidthShell()

        openTasksDestination()

        let printQueue = app.buttons["shiftTasks.printQueue"]
        XCTAssertTrue(printQueue.waitForExistence(timeout: 8))
        printQueue.tap()

        let jobRow = app.buttons[completedJobIdentifier]
        XCTAssertTrue(jobRow.waitForExistence(timeout: 8),
                      "The seeded completed job must be reachable in the iPad Recent section (#794)")
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
