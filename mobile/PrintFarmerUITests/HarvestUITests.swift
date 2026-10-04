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
            ? app.tabBars.descendants(matching: captured.type)
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

        let alphaHandle = app.buttons["Reorder Queue reorder alpha.gcode"]
        let gammaHandle = app.buttons["Reorder Queue reorder gamma.gcode"]
        XCTAssertTrue(alphaHandle.waitForExistence(timeout: 5),
                      "Eligible queued rows should expose the native reorder handle.")
        XCTAssertTrue(gammaHandle.exists)
        let originalAlphaY = alpha.frame.minY
        let source = alphaHandle.coordinate(withNormalizedOffset: CGVector(dx: 0.5, dy: 0.5))
        let destination = gamma.coordinate(withNormalizedOffset: CGVector(dx: 0.93, dy: 0.5))
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
            let alphaReorderHandle = app.buttons["Reorder Queue reorder alpha.gcode"]
            let alphaCoordinate = alphaReorderHandle.coordinate(
                withNormalizedOffset: CGVector(dx: 0.5, dy: 0.5)
            )
            alphaCoordinate.press(
                forDuration: 1,
                thenDragTo: priorityBoundary.coordinate(
                    withNormalizedOffset: CGVector(dx: 0.93, dy: 0.5)
                ),
                withVelocity: .slow,
                thenHoldForDuration: 0.5
            )
            XCTAssertEqual(currentGroupOrder(), reorderedGroup,
                           "Dragging to another priority group must not reorder this group.")

            alphaReorderHandle.coordinate(
                withNormalizedOffset: CGVector(dx: 0.5, dy: 0.5)
            ).press(
                forDuration: 1,
                thenDragTo: printerBoundary.coordinate(
                    withNormalizedOffset: CGVector(dx: 0.93, dy: 0.5)
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

        let alphaHandle = app.buttons["Reorder Queue reorder alpha.gcode"]
        let betaHandle = app.buttons["Reorder Queue reorder beta.gcode"]
        XCTAssertTrue(alphaHandle.waitForExistence(timeout: 8))
        XCTAssertTrue(betaHandle.exists)
        XCTAssertFalse(app.buttons["Reorder Queue pinned assigned.gcode"].exists,
                       "Assigned jobs must not expose native reorder controls.")

        let priorityBoundaryHandle = app.buttons["Reorder Queue priority boundary.gcode"]
        let printerBoundaryHandle = app.buttons["Reorder Queue printer boundary.gcode"]
        for _ in 0..<3 where !priorityBoundaryHandle.exists || !printerBoundaryHandle.exists {
            app.descendants(matching: .any)["jobList.root"].swipeUp()
        }
        XCTAssertTrue(app.buttons["job.row.32340000-0000-0000-0000-000000000005"].exists)
        XCTAssertTrue(app.buttons["job.row.32340000-0000-0000-0000-000000000006"].exists)
        XCTAssertFalse(priorityBoundaryHandle.exists,
                       "A single-row priority group must not expose a reorder handle.")
        XCTAssertFalse(printerBoundaryHandle.exists,
                       "A single-row printer group must not expose a reorder handle.")

        if app.buttons["jobList.page.printing"].exists {
            app.buttons["jobList.page.printing"].tap()
        }
        let printing = app.buttons.matching(
            NSPredicate(format: "label CONTAINS %@", "Queue pinned printing.gcode")
        ).firstMatch
        XCTAssertTrue(printing.waitForExistence(timeout: 5))
        XCTAssertFalse(app.buttons["Reorder Queue pinned printing.gcode"].exists,
                       "Printing jobs must not expose native reorder controls.")
    }

    private func launchQueueReorderScenario() {
        app.terminate()
        app.launchArguments.append("--uitesting-queue-reorder")
        app.launchForPrintFarmerUITest()
    }

    /// Navigates the operator shell to the seeded completed demo job's
    /// detail view, device-adaptively:
    /// Queue → Recent → the seeded completed job. Queue opens `JobListView`
    /// directly on iPhone (tab bar) and iPad (sidebar). The `jobDetail.*` assertion
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
        openQueueDestination(file: file, line: line)

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
            // The page control's `.isSelected` trait follows `currentPage`,
            // which changes inside an animated paging transition; the
            // accessibility snapshot can lag the tap by a frame or more, so
            // wait (bounded) for the real final selected state (#3001).
            let recentSelected = XCTNSPredicateExpectation(
                predicate: NSPredicate(format: "isSelected == true"),
                object: recentPage
            )
            XCTAssertEqual(
                XCTWaiter.wait(for: [recentSelected], timeout: 5),
                .completed,
                "Tapping the Recent page control must select the Recent page"
            )
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
            // #2445 wrapped `JobListView.screenContent` in an outer
            // `.accessibilityIdentifier("jobList.root")`, which collapses
            // onto the same underlying collection view as the inner
            // `jobList` list's own `"jobList.combined.list"` identifier —
            // only the outer identifier survives at runtime on iPad. Swipe
            // by the identifier that is actually present so the off-screen
            // cancelled job row becomes reachable.
            let combinedList = app.collectionViews["jobList.root"]
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
