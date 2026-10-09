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
final class HarvestUITests: QueueUITestBase {
    override var waitsForNavigationReadiness: Bool { true }

    private let completedJobIdentifier = "job.row.30000000-0003-0000-0000-000000000007"
    private let failedJobIdentifier = "job.row.30000000-0003-0000-0000-000000000009"
    private let cancelledJobIdentifier = "job.row.30000000-0003-0000-0000-000000000011"

    func testQueueDestinationSurvivesIdentifierPromotionAndChangedBadgeLabel() throws {
        let queue = shellDestinationButton(tabIdentifier: "tab.queue", timeout: 8)
        let captured = ShellNode(try queue.snapshot())
        let observation = ShellObservation(ShellNode(try app.snapshot()))
        let destination = try XCTUnwrap(observation.destination(
            tab: "tab.queue", sidebar: "sidebar.queue", title: "Queue"
        ))
        let expectedID = destination.surface == .tabBar ? "tab.queue" : "sidebar.queue"
        let scope = destination.surface == .tabBar
            ? app.buttons
            : app.descendants(matching: captured.type)
        let stable = scope.matching(identifier: expectedID).firstMatch
        XCTAssertTrue(stable.waitForExistence(timeout: 8))
        // Recreate the earlier identifierless snapshot without changing app state.
        var earlier = captured
        earlier.identifier = ""
        earlier.label = "Queue, obsolete badge count"
        let promoted = observedElement(earlier, within: scope, allowingPromotionTo: expectedID)
        XCTAssertEqual(promoted.identifier, expectedID)
        XCTAssertTrue(promoted.isHittable)
        promoted.tap()
        XCTAssertTrue(app.descendants(matching: .any)["jobList.root"].waitForExistence(timeout: 8),
                      "The promoted identity must navigate to the actual Queue destination")
    }

    func testQueueReorderDragUpdatesTheRenderedQueueWithinOneGroup() throws {
        launchQueueReorderScenario()
        openQueueDestination()

        let alpha = app.buttons["job.row.32340000-0000-0000-0000-000000000003"]
        let beta = app.buttons["job.row.32340000-0000-0000-0000-000000000004"]
        let gamma = app.buttons["job.row.32340000-0000-0000-0000-000000000007"]
        for row in [alpha, beta, gamma] {
            XCTAssertTrue(row.waitForExistence(timeout: 8))
        }
        XCTAssertTrue(app.buttons["job.row.32340000-0000-0000-0000-000000000002"].exists,
                      "Assigned work remains visible outside the reorder group.")

        let alphaHandle = reorderHandle(for: "Queue reorder alpha.gcode")
        let gammaHandle = reorderHandle(for: "Queue reorder gamma.gcode")
        XCTAssertTrue(alphaHandle.waitForExistence(timeout: 5),
                      "Eligible queued rows should expose the drag-to-reorder control.")
        XCTAssertTrue(gammaHandle.exists)
        XCTAssertTrue(gammaHandle.isHittable)
        let originalAlphaY = alpha.frame.minY
        let source = alphaHandle.coordinate(withNormalizedOffset: CGVector(dx: 0.5, dy: 0.5))
        let destination = beta.coordinate(withNormalizedOffset: CGVector(dx: 0.5, dy: 0.5))
        source.press(
            forDuration: 1,
            thenDragTo: destination,
            withVelocity: .slow,
            thenHoldForDuration: 0.5
        )
        let reordered = XCTNSPredicateExpectation(
            predicate: NSPredicate { _, _ in
                beta.frame.minY < alpha.frame.minY && alpha.frame.minY > originalAlphaY + 10
            },
            object: nil
        )
        XCTAssertEqual(XCTWaiter.wait(for: [reordered], timeout: 5), .completed,
                       "Dragging alpha down within its group should move it below beta.")

        if app.frame.width > 600 {
            let priorityBoundary = app.buttons["job.row.32340000-0000-0000-0000-000000000005"]
            let printerBoundary = app.buttons["job.row.32340000-0000-0000-0000-000000000006"]
            XCTAssertTrue(priorityBoundary.waitForExistence(timeout: 5))
            XCTAssertTrue(printerBoundary.waitForExistence(timeout: 5))
            XCTAssertTrue(priorityBoundary.isHittable)
            XCTAssertTrue(printerBoundary.isHittable)

            let currentGroupOrder = {
                [alpha, beta, gamma]
                    .sorted { $0.frame.minY < $1.frame.minY }
                    .map(\.identifier)
            }
            let reorderedGroup = currentGroupOrder()
            let alphaCoordinate = alphaHandle.coordinate(
                withNormalizedOffset: CGVector(dx: 0.5, dy: 0.5)
            )
            alphaCoordinate.press(
                forDuration: 1,
                thenDragTo: priorityBoundary.coordinate(
                    withNormalizedOffset: CGVector(dx: 0.5, dy: 0.5)
                ),
                withVelocity: .slow,
                thenHoldForDuration: 0.5
            )
            XCTAssertEqual(currentGroupOrder(), reorderedGroup,
                           "Dragging to another priority group must not reorder this group.")

            alphaHandle.coordinate(
                withNormalizedOffset: CGVector(dx: 0.5, dy: 0.5)
            ).press(
                forDuration: 1,
                thenDragTo: printerBoundary.coordinate(
                    withNormalizedOffset: CGVector(dx: 0.5, dy: 0.5)
                ),
                withVelocity: .slow,
                thenHoldForDuration: 0.5
            )
            XCTAssertEqual(currentGroupOrder(), reorderedGroup,
                           "Dragging to another printer group must not reorder this group.")
        }
    }

    func testQueueAccessibilityReorderControlsRespectEligibility() {
        launchQueueReorderScenario()
        openQueueDestination()

        let alphaHandle = reorderHandle(for: "Queue reorder alpha.gcode")
        let betaHandle = reorderHandle(for: "Queue reorder beta.gcode")
        XCTAssertTrue(alphaHandle.waitForExistence(timeout: 8))
        XCTAssertTrue(betaHandle.exists)
        XCTAssertFalse(reorderHandle(for: "Queue pinned assigned.gcode").exists,
                       "Assigned jobs must not expose drag-to-reorder controls.")

        let priorityBoundaryHandle = reorderHandle(for: "Queue priority boundary.gcode")
        let printerBoundaryHandle = reorderHandle(for: "Queue printer boundary.gcode")
        let priorityBoundary = app.buttons["job.row.32340000-0000-0000-0000-000000000005"]
        let printerBoundary = app.buttons["job.row.32340000-0000-0000-0000-000000000006"]
        for _ in 0..<3 where !priorityBoundary.isHittable || !printerBoundary.isHittable {
            app.swipeUp()
        }
        XCTAssertTrue(priorityBoundary.exists)
        XCTAssertTrue(printerBoundary.exists)
        XCTAssertTrue(
            priorityBoundary.isHittable,
            "The priority-boundary row must be visible before checking reorder eligibility."
        )
        XCTAssertTrue(
            printerBoundary.isHittable,
            "The printer-boundary row must be visible before checking reorder eligibility."
        )
        XCTAssertFalse(priorityBoundaryHandle.exists,
                       "A single-row priority group must not expose a reorder handle.")
        XCTAssertFalse(printerBoundaryHandle.exists,
                       "A single-row printer group must not expose a reorder handle.")

        let printing = app.buttons.matching(
            NSPredicate(format: "label CONTAINS %@", "Queue pinned printing.gcode")
        ).firstMatch
        for _ in 0..<8 where !printing.isHittable {
            app.swipeDown()
        }
        XCTAssertTrue(printing.waitForExistence(timeout: 5))
        XCTAssertTrue(printing.isHittable, "The Printing row must remain reachable in the combined Queue.")
        XCTAssertFalse(reorderHandle(for: "Queue pinned printing.gcode").exists,
                       "Printing jobs must not expose native reorder controls.")
    }

    private func launchQueueReorderScenario() {
        relaunchAppForTest(additionalArguments: ["--uitesting-queue-reorder"])
    }

    private func reorderHandle(for jobName: String) -> XCUIElement {
        app.buttons.matching(
            NSPredicate(format: "label CONTAINS %@ AND label CONTAINS %@", "Reorder", jobName)
        ).firstMatch
    }

    /// Navigates Queue → Job History → the completed demo job. Queue opens
    /// `JobListView` directly on iPhone (tab bar) and iPad (sidebar), while
    /// completed jobs remain separate from the failures-only Queue section.
    func openCompletedJobDetail(
        file: StaticString = #filePath,
        line: UInt = #line
    ) {
        openJobHistory(file: file, line: line)

        let jobRow = app.buttons[completedJobIdentifier]
        XCTAssertTrue(jobRow.waitForExistence(timeout: 8),
                      "Seeded completed demo job should render in Job History",
                      file: file, line: line)
        jobRow.tap()
    }

    private func openJobHistory(
        file: StaticString = #filePath,
        line: UInt = #line
    ) {
        openJobHistoryMenuAndSelect(file: file, line: line)

        XCTAssertTrue(
            app.buttons[completedJobIdentifier].waitForExistence(timeout: 8),
            "Completed jobs must be reachable from Job History",
            file: file,
            line: line
        )
    }

    func testQueueRecentShowsOnlyFailuresAndHistoryShowsCompletedJobs() {
        openQueueDestination()

        assertRecentJob(
            identifier: failedJobIdentifier,
            name: "vase_mode_spiral.gcode",
            status: "Failed"
        )
        XCTAssertFalse(app.buttons[completedJobIdentifier].exists)
        XCTAssertFalse(app.buttons[cancelledJobIdentifier].exists)

        openJobHistoryMenuAndSelect()
        assertHistoryJob(
            identifier: completedJobIdentifier,
            name: "benchy_calibration.gcode",
            status: "Completed"
        )
        assertHistoryJob(
            identifier: cancelledJobIdentifier,
            name: "test_cube_20mm.gcode",
            status: "Cancelled"
        )
        let history = app.descendants(matching: .any)["jobList.history"]
        XCTAssertTrue(history.exists)
        XCTAssertFalse(
            history.descendants(matching: .any)[failedJobIdentifier].exists,
            "Recent failures must not be included in the separate completed/cancelled Job History."
        )
    }

    private func assertRecentJob(
        identifier: String,
        name: String,
        status: String,
        file: StaticString = #filePath,
        line: UInt = #line
    ) {
        let row = app.descendants(matching: .any)[identifier]
        let combinedList = app.collectionViews["jobList.combined.list"]
        for _ in 0..<8 where !row.isHittable {
            combinedList.swipeUp()
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

    private func assertHistoryJob(
        identifier: String,
        name: String,
        status: String,
        file: StaticString = #filePath,
        line: UInt = #line
    ) {
        let history = app.descendants(matching: .any)["jobList.history"]
        let row = app.descendants(matching: .any)[identifier]
        for _ in 0..<5 where !row.isHittable && history.exists {
            history.swipeUp()
        }
        XCTAssertTrue(
            row.waitForExistence(timeout: 5),
            "Job History must expose the seeded \(status.lowercased()) job",
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

        // Sheet dismissal is animated; wait (bounded) for the real final
        // dismissed state instead of sampling mid-animation (#3001).
        XCTAssertTrue(app.navigationBars["Harvest Plate"].waitForNonExistence(timeout: 5),
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

        let doneButton = app.buttons["harvest.done"]
        XCTAssertTrue(doneButton.waitForExistence(timeout: 5),
                      "A successful harvest should present the success view with a Done action")
        XCTAssertEqual(doneButton.label, "Done")
        doneButton.tap()

        // Sheet dismissal is animated and CI runners are slow; `.exists`
        // sampled in the same frame as the tap can still see the outgoing
        // sheet. Wait (bounded) for the real final dismissed state (#3001).
        XCTAssertTrue(app.navigationBars["Harvest Plate"].waitForNonExistence(timeout: 5),
                      "Done should dismiss the harvest sheet")
    }
}
