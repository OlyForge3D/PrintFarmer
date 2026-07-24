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
final class HarvestUITests: ShiftTasksUITestBase {

    private let completedJobIdentifier = "job.row.30000000-0003-0000-0000-000000000007"
    private let failedJobIdentifier = "job.row.30000000-0003-0000-0000-000000000009"
    private let cancelledJobIdentifier = "job.row.30000000-0003-0000-0000-000000000011"

    /// Navigates the operator shell to the seeded completed demo job's
    /// detail view, device-adaptively:
    /// Tasks destination → Print queue (`JobListView`) → Recent → the
    /// seeded completed job. Since #782 the Tasks destination presents the
    /// anchor-grouped checklist (`ShiftTasksView`), so the preserved queue
    /// is reached through the explicit `shiftTasks.printQueue` link on both
    /// iPhone (tab bar) and iPad (sidebar). The final `jobDetail.*` assertion
    /// proves `JobDetailView` is presented in the FOREGROUND navigation
    /// context on both device classes (issue #794).
    func openCompletedJobDetail(
        file: StaticString = #filePath,
        line: UInt = #line
    ) {
        openRecentJobs(file: file, line: line)

        let jobRow = app.buttons[completedJobIdentifier]
        XCTAssertTrue(jobRow.waitForExistence(timeout: 8),
                      "Seeded completed demo job should render in the Recent list",
                      file: file, line: line)
        jobRow.tap()
    }

    private func openRecentJobs(
        file: StaticString = #filePath,
        line: UInt = #line
    ) {
        openTasksDestination(file: file, line: line)

        let printQueue = app.buttons["shiftTasks.printQueue"]
        XCTAssertTrue(printQueue.waitForExistence(timeout: 8),
                      "Tasks destination must expose the preserved Print queue link",
                      file: file, line: line)
        printQueue.tap()

        // iPhone paginates the queue and exposes a Recent page control;
        // iPad renders a single List with an always-visible Recent section.
        revealRecentJobs()
    }

    /// Reveals the Recent (completed/failed/cancelled) jobs in the preserved
    /// `JobListView`, handling both the iPhone paged layout (a "Recent" page
    /// button) and the iPad List layout (a "Recent" section, which may be
    /// collapsed by default).
    private func revealRecentJobs() {
        let jobRow = app.buttons[completedJobIdentifier]
        if jobRow.waitForExistence(timeout: 3) { return }

        // iPhone: swipeable pages expose a "Recent" page control.
        let recentPage = app.buttons["jobList.page.recent"]
        if recentPage.waitForExistence(timeout: 2) {
            XCTAssertEqual(recentPage.label, "Recent")
            XCTAssertGreaterThanOrEqual(recentPage.frame.width, 44)
            XCTAssertGreaterThanOrEqual(recentPage.frame.height, 44)
            XCTAssertTrue(recentPage.isEnabled)
            XCTAssertTrue(recentPage.isHittable)
            recentPage.tap()
            XCTAssertTrue(recentPage.isSelected)
            if jobRow.waitForExistence(timeout: 3) { return }
        }

        // iPad: the Recent section header can be collapsed; tap it to expand.
        let recentHeader = app.staticTexts["Recent"]
        if recentHeader.waitForExistence(timeout: 2) {
            recentHeader.tap()
        }
    }

    func testRecentPageExposesCompletedFailedAndCancelledJobs() {
        openRecentJobs()

        assertRecentJob(
            identifier: completedJobIdentifier,
            name: "benchy_calibration.gcode",
            status: "Completed"
        )
        assertRecentJob(
            identifier: failedJobIdentifier,
            name: "vase_mode_spiral.gcode",
            status: "Failed"
        )
        assertRecentJob(
            identifier: cancelledJobIdentifier,
            name: "test_cube_20mm.gcode",
            status: "Cancelled"
        )
    }

    private func assertRecentJob(
        identifier: String,
        name: String,
        status: String,
        file: StaticString = #filePath,
        line: UInt = #line
    ) {
        let row = app.buttons[identifier]
        if !row.exists {
            let combinedList = app.collectionViews["jobList.combined.list"]
            if combinedList.exists {
                combinedList.swipeUp()
            }
        }
        XCTAssertTrue(
            row.waitForExistence(timeout: 5),
            "Recent must expose the seeded \(status.lowercased()) job",
            file: file,
            line: line
        )
        XCTAssertTrue(row.label.contains(name), file: file, line: line)
        XCTAssertTrue(row.label.contains("\(status) status"), file: file, line: line)
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

    /// Dispute C (#714): "Done" must only dismiss the sheet — `onHarvested`
    /// now fires immediately on the server response via `.onChange(of:
    /// viewModel.result)`, not from this button. The demo harvest service
    /// responds without artificial delay and doesn't track harvested state
    /// on the job itself, so this test covers the reachable, deterministic
    /// piece: submitting successfully reaches the success view and Done
    /// dismisses the sheet.
    func testSubmittingHarvestShowsSuccessAndDoneDismissesSheet() {
        openCompletedJobDetail()

        let harvestButton = app.buttons["jobDetail.harvestToInventory"]
        XCTAssertTrue(harvestButton.waitForExistence(timeout: 5))
        harvestButton.tap()
        XCTAssertTrue(app.navigationBars["Harvest Plate"].waitForExistence(timeout: 5))

        let binField = app.textFields["harvest.binCode"]
        XCTAssertTrue(binField.waitForExistence(timeout: 3))
        binField.tap()
        binField.typeText("BIN-1")

        let submitButton = app.buttons["harvest.submit"]
        XCTAssertTrue(submitButton.waitForExistence(timeout: 3))
        submitButton.tap()

        let doneButton = app.buttons["Done"]
        XCTAssertTrue(doneButton.waitForExistence(timeout: 5),
                      "A successful harvest should present the success view with a Done action")
        doneButton.tap()

        XCTAssertFalse(app.navigationBars["Harvest Plate"].exists,
                       "Done should dismiss the harvest sheet")
    }
}
