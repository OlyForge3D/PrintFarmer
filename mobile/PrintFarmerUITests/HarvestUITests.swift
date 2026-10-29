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
        try requireDraggable(alphaHandle, "alpha reorder handle")
        try requireDraggable(beta, "beta drop target")
        let originalAlphaY = alpha.frame.minY
        let legalDiagnosticsBefore = try reorderCounts(in: app)
        let source = alphaHandle.coordinate(withNormalizedOffset: CGVector(dx: 0.5, dy: 0.5))
        let destination = beta.coordinate(withNormalizedOffset: CGVector(dx: 0.5, dy: 0.5))
        source.press(
            forDuration: 1,
            thenDragTo: destination,
            withVelocity: .slow,
            thenHoldForDuration: 0.5
        )
        let movedBelowBeta = {
            beta.frame.minY < alpha.frame.minY && alpha.frame.minY > originalAlphaY + 10
        }
        let reordered = XCTNSPredicateExpectation(
            predicate: NSPredicate { _, _ in
                movedBelowBeta()
                    && self.reorderDiagnosticsShow(repositoryMoves: legalDiagnosticsBefore.repositoryMoves + 1)
            },
            object: nil
        )
        _ = XCTWaiter.wait(for: [reordered], timeout: 5)
        let legalDiagnosticsAfter = try reorderCounts(in: app)
        attachReorderDiagnostics("legal move down", before: legalDiagnosticsBefore, after: legalDiagnosticsAfter)
        XCTAssertTrue(movedBelowBeta(),
                      "Dragging alpha down within its group should move it below beta.")
        XCTAssertEqual(legalDiagnosticsAfter.repositoryMoves, legalDiagnosticsBefore.repositoryMoves + 1,
                       "A legal in-group drop must persist exactly one move.")

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
            XCTAssertEqual(reorderedGroup, [beta.identifier, alpha.identifier, gamma.identifier])
            let crossGroupError = app.descendants(matching: .any).matching(
                NSPredicate(format: "label CONTAINS %@", "same printer and priority group")
            ).firstMatch
            XCTAssertFalse(crossGroupError.exists)

            // #3457 option A (issue comment 6493170669): on iPad the native List
            // prevents a cross-group drop, so no onMove reaches the app. Prove that
            // prevention: order, error state and both counters stay unchanged for the
            // whole observation window. Drop past the boundary row's midpoint, because
            // its centre is also the legal end-of-group slot.
            try requireDraggable(alphaHandle, "alpha reorder handle before priority drag")
            try requireDraggable(priorityBoundary, "priority-boundary row")
            try assertCrossGroupDragIsNativelyPrevented(
                "priority",
                drag: alphaHandle,
                to: priorityBoundary,
                expectedOrder: reorderedGroup,
                currentOrder: currentGroupOrder,
                error: crossGroupError
            )

            // A valid move back must still be delivered and persisted after the
            // prevented priority gesture.
            try requireDraggable(beta, "beta drop target before move back")
            let moveBackDiagnosticsBefore = try reorderCounts(in: app)
            alphaHandle.coordinate(withNormalizedOffset: CGVector(dx: 0.5, dy: 0.5)).press(
                forDuration: 1,
                thenDragTo: beta.coordinate(withNormalizedOffset: CGVector(dx: 0.5, dy: 0.5)),
                withVelocity: .slow,
                thenHoldForDuration: 0.5
            )
            let restoredGroup = [alpha.identifier, beta.identifier, gamma.identifier]
            let movedBack = XCTNSPredicateExpectation(
                predicate: NSPredicate { _, _ in
                    !crossGroupError.exists && currentGroupOrder() == restoredGroup
                        && self.reorderDiagnosticsShow(
                            repositoryMoves: moveBackDiagnosticsBefore.repositoryMoves + 1
                        )
                },
                object: nil
            )
            _ = XCTWaiter.wait(for: [movedBack], timeout: 5)
            let moveBackDiagnosticsAfter = try reorderCounts(in: app)
            attachReorderDiagnostics(
                "legal move back", before: moveBackDiagnosticsBefore, after: moveBackDiagnosticsAfter
            )
            XCTAssertEqual(currentGroupOrder(), restoredGroup,
                           "Dragging alpha back above beta should reorder it.")
            XCTAssertFalse(crossGroupError.exists,
                           "A legal in-group drop must not show a rejection.")
            XCTAssertEqual(moveBackDiagnosticsAfter.repositoryMoves, moveBackDiagnosticsBefore.repositoryMoves + 1,
                           "A legal in-group drop must persist exactly one move.")

            try requireDraggable(alphaHandle, "alpha reorder handle before printer drag")
            try requireDraggable(printerBoundary, "printer-boundary row")
            try assertCrossGroupDragIsNativelyPrevented(
                "printer",
                drag: alphaHandle,
                to: printerBoundary,
                expectedOrder: restoredGroup,
                currentOrder: currentGroupOrder,
                error: crossGroupError
            )
        }
    }

    /// #3457: drags `handle` past `boundary`'s midpoint into another reorder group
    /// and asserts the native List prevented it. Any `onMove` or repository move,
    /// visible error, or order change during the 5s window fails the test.
    private func assertCrossGroupDragIsNativelyPrevented(
        _ name: String,
        drag handle: XCUIElement,
        to boundary: XCUIElement,
        expectedOrder: [String],
        currentOrder: @escaping () -> [String],
        error: XCUIElement,
        file: StaticString = #filePath,
        line: UInt = #line
    ) throws {
        let before = try reorderCounts(in: app, file: file, line: line)
        XCTAssertEqual(currentOrder(), expectedOrder, file: file, line: line)
        handle.coordinate(withNormalizedOffset: CGVector(dx: 0.5, dy: 0.5)).press(
            forDuration: 1,
            thenDragTo: boundary.coordinate(withNormalizedOffset: CGVector(dx: 0.5, dy: 0.95)),
            withVelocity: .slow,
            thenHoldForDuration: 0.5
        )
        let diagnostics = app.descendants(matching: .any)["jobList.reorderDiagnostics"]
        let changed = XCTNSPredicateExpectation(
            predicate: NSPredicate { _, _ in
                error.exists || currentOrder() != expectedOrder
                    || !diagnostics.exists || diagnostics.label != before.summary
            },
            object: nil
        )
        changed.isInverted = true
        let observed = XCTWaiter.wait(for: [changed], timeout: 5)
        let after = try reorderCounts(in: app, file: file, line: line)
        attachReorderDiagnostics("\(name) boundary", before: before, after: after)
        XCTAssertEqual(observed, .completed,
                       "The \(name)-boundary drag must leave the queue untouched.", file: file, line: line)
        XCTAssertEqual(after.onMove, before.onMove,
                       "The native List must prevent a drop into another \(name) group.", file: file, line: line)
        XCTAssertEqual(after.repositoryMoves, before.repositoryMoves,
                       "A \(name)-boundary drag must never call the repository.", file: file, line: line)
        XCTAssertFalse(error.exists,
                       "A natively prevented \(name)-boundary drag has no rejection to show.", file: file, line: line)
        XCTAssertEqual(currentOrder(), expectedOrder,
                       "Dragging to another \(name) group must not reorder this group.", file: file, line: line)
    }

    private struct ReorderCounts {
        let summary: String
        let onMove: Int
        let repositoryMoves: Int
    }

    /// Reads the DEBUG queue-reorder fixture's `onMove` and repository-move counters
    /// (`QueueReorderUITestDiagnostics`). Fails if the label is missing or malformed.
    private func reorderCounts(
        in app: XCUIApplication,
        file: StaticString = #filePath,
        line: UInt = #line
    ) throws -> ReorderCounts {
        let label = app.descendants(matching: .any)["jobList.reorderDiagnostics"]
        guard label.waitForExistence(timeout: 5) else {
            XCTFail("The queue-reorder diagnostics label must be present.", file: file, line: line)
            throw DragPreconditionFailure()
        }
        guard let counts = Self.parseReorderCounts(label.label) else {
            XCTFail("Unreadable queue-reorder diagnostics: \(label.label)", file: file, line: line)
            throw DragPreconditionFailure()
        }
        return counts
    }

    /// The label refreshes only after `moveQueuedRows` returns, so a legal drop waits
    /// for its persisted repository move rather than just the optimistic reorder.
    private func reorderDiagnosticsShow(repositoryMoves expected: Int) -> Bool {
        let label = app.descendants(matching: .any)["jobList.reorderDiagnostics"]
        guard label.exists else { return false }
        return Self.parseReorderCounts(label.label)?.repositoryMoves == expected
    }

    private static func parseReorderCounts(_ summary: String) -> ReorderCounts? {
        func count(_ key: String) -> Int? {
            summary.split(separator: " ")
                .first { $0.hasPrefix("\(key)=") }
                .flatMap { Int($0.dropFirst(key.count + 1)) }
        }
        guard let onMove = count("onMove"), let repositoryMoves = count("repositoryMoves") else {
            return nil
        }
        return ReorderCounts(summary: summary, onMove: onMove, repositoryMoves: repositoryMoves)
    }

    private func attachReorderDiagnostics(_ name: String, before: ReorderCounts, after: ReorderCounts) {
        let attachment = XCTAttachment(string: "before: \(before.summary)\nafter: \(after.summary)")
        attachment.name = "Queue reorder diagnostics (\(name))"
        attachment.lifetime = .deleteOnSuccess
        add(attachment)
    }

    /// Fails with a diagnosable message instead of letting XCTest synthesise a
    /// touch at an infinite point when a row has not settled into the hierarchy.
    private func requireDraggable(
        _ element: XCUIElement,
        _ name: String,
        file: StaticString = #filePath,
        line: UInt = #line
    ) throws {
        let settled = XCTNSPredicateExpectation(
            predicate: NSPredicate { _, _ in
                guard element.exists, element.isHittable else { return false }
                let frame = element.frame
                return frame.minX.isFinite && frame.minY.isFinite
                    && frame.width.isFinite && frame.height.isFinite
                    && !frame.isEmpty
            },
            object: nil
        )
        guard XCTWaiter.wait(for: [settled], timeout: 5) == .completed else {
            XCTFail(
                "The \(name) must be hittable with a finite frame before dragging; exists=\(element.exists).",
                file: file,
                line: line
            )
            throw DragPreconditionFailure()
        }
    }

    private struct DragPreconditionFailure: Error {}

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

        let printing = app.buttons["job.row.32340000-0000-0000-0000-000000000001"]
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
        // In split view SwiftUI promotes the root identifier onto the List itself.
        app.descendants(matching: .any)["jobList.root"].buttons.matching(
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
        let combinedList = app.descendants(matching: .any)["jobList.root"]
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
