import XCTest

/// Shared support for the shift-task UI-test suites. Provides the
/// device-adaptive Tasks-destination navigation used on both iPhone
/// (tab bar) and iPad (sidebar) so every shift-task suite reaches the
/// checklist identically.
@MainActor
class ShiftTasksUITestBase: PrintFarmerUITestCase {
    func openTasksDestination(
        file: StaticString = #filePath,
        line: UInt = #line
    ) {
        let tab = app.tabBars.buttons["Tasks"]
        if tab.waitForExistence(timeout: 8) {
            tab.tap()
            return
        }

        let sidebarTask = app.buttons["sidebar.tasks"]
        if sidebarTask.waitForExistence(timeout: 3) {
            sidebarTask.tap()
            return
        }

        for label in ["Sidebar", "Toggle Sidebar", "Show Sidebar"] {
            let toggle = app.navigationBars.buttons[label]
            if toggle.exists {
                toggle.tap()
                if sidebarTask.waitForExistence(timeout: 3) {
                    sidebarTask.tap()
                    return
                }
            }
        }

        XCTFail(
            "Tasks destination is not reachable on this device",
            file: file,
            line: line
        )
    }
}

@MainActor
final class ShiftTasksUITests: ShiftTasksUITestBase {
    private let taskID = "78200000-0000-0000-0000-000000000001"

    override var additionalLaunchArguments: [String] {
        [
            "--uitesting-shift-task-mutation-error",
            "-UIPreferredContentSizeCategoryName",
            "UICTContentSizeCategoryAccessibilityExtraExtraExtraLarge",
        ]
    }

    func testP6RetryCausallyRunsOnceAndPublishesCanonicalEmptyState() {
        openTasksDestination()
        let row = app.otherElements["shiftTasks.row.info.\(taskID)"]
        XCTAssertTrue(row.waitForExistence(timeout: 8))

        let complete = app.buttons["shiftTasks.action.complete.\(taskID)"]
        XCTAssertTrue(complete.waitForExistence(timeout: 3))
        complete.tap()

        let error = app.descendants(matching: .any)[
            "shiftTasks.mutation.error.\(taskID)"
        ]
        XCTAssertTrue(error.waitForExistence(timeout: 5))
        let retry = app.buttons["shiftTasks.mutation.retry.\(taskID)"]
        XCTAssertTrue(retry.waitForExistence(timeout: 3))
        retry.tap()

        let empty = app.descendants(matching: .any)["shiftTasks.empty"]
        XCTAssertTrue(
            empty.waitForExistence(timeout: 5),
            "Retry must execute the scripted second mutation and publish the canonical empty snapshot"
        )
        XCTAssertTrue(error.waitForNonExistence(timeout: 3))
        XCTAssertTrue(row.waitForNonExistence(timeout: 3))
    }

    func testP7DismissClearsOnlyCurrentErrorWithoutTaskMutation() {
        openTasksDestination()
        let row = app.otherElements["shiftTasks.row.info.\(taskID)"]
        XCTAssertTrue(row.waitForExistence(timeout: 8))

        let complete = app.buttons["shiftTasks.action.complete.\(taskID)"]
        XCTAssertTrue(complete.waitForExistence(timeout: 3))
        complete.tap()

        let error = app.descendants(matching: .any)[
            "shiftTasks.mutation.error.\(taskID)"
        ]
        XCTAssertTrue(error.waitForExistence(timeout: 5))
        let dismiss = app.buttons["shiftTasks.mutation.dismiss.\(taskID)"]
        XCTAssertTrue(dismiss.waitForExistence(timeout: 3))
        dismiss.tap()

        XCTAssertTrue(error.waitForNonExistence(timeout: 3))
        XCTAssertTrue(
            row.exists,
            "Dismissing the error must not dismiss or mutate the task"
        )
        XCTAssertTrue(
            app.buttons["shiftTasks.action.complete.\(taskID)"]
                .waitForExistence(timeout: 3),
            "The original action must be available after error dismissal"
        )
        XCTAssertFalse(
            app.descendants(matching: .any)["shiftTasks.empty"].exists
        )
    }

    func testP8AccessibilityTreeHasOneRetryAndOneDismissAction() {
        openTasksDestination()
        let complete = app.buttons["shiftTasks.action.complete.\(taskID)"]
        XCTAssertTrue(complete.waitForExistence(timeout: 8))
        complete.tap()

        let retryQuery = app.buttons.matching(
            identifier: "shiftTasks.mutation.retry.\(taskID)"
        )
        let dismissQuery = app.buttons.matching(
            identifier: "shiftTasks.mutation.dismiss.\(taskID)"
        )
        XCTAssertEqual(retryQuery.count, 1)
        XCTAssertEqual(dismissQuery.count, 1)

        let retry = retryQuery.element
        let dismiss = dismissQuery.element
        XCTAssertEqual(retry.label, "Retry")
        XCTAssertEqual(dismiss.label, "Dismiss")
        XCTAssertEqual(retry.elementType, .button)
        XCTAssertEqual(dismiss.elementType, .button)
        XCTAssertTrue(retry.isEnabled)
        XCTAssertTrue(dismiss.isEnabled)
        XCTAssertGreaterThanOrEqual(retry.frame.width, 44)
        XCTAssertGreaterThanOrEqual(retry.frame.height, 44)
        XCTAssertGreaterThanOrEqual(dismiss.frame.width, 44)
        XCTAssertGreaterThanOrEqual(dismiss.frame.height, 44)

        let info = app.otherElements["shiftTasks.row.info.\(taskID)"]
        XCTAssertTrue(info.exists)
        XCTAssertTrue(info.label.contains("Harvest completed plate"))
        XCTAssertTrue(info.label.contains("High priority"))
        XCTAssertTrue(info.label.contains("Now"))
        XCTAssertTrue(info.label.contains("Harvest"))
    }

    func testExistingPrintQueueRemainsExplicitlyReachable() {
        openTasksDestination()
        let queue = app.buttons["shiftTasks.printQueue"]
        XCTAssertTrue(queue.waitForExistence(timeout: 8))
        XCTAssertEqual(queue.label, "Print queue")
        queue.tap()

        XCTAssertTrue(
            app.staticTexts["raspberry_pi_case.gcode"]
                .waitForExistence(timeout: 5),
            "The preserved JobListView queue page must be reachable from Tasks"
        )
        XCTAssertFalse(app.otherElements["shiftTasks.list"].exists)
    }
}

/// Grouped anchor-checklist proof (issue #782 acceptance). Runs under the
/// default authenticated bootstrap, whose demo shift-task service seeds a
/// deterministic multi-group snapshot: **Now** (harvest), **Timeline**
/// (timed filament coverage), and **Anytime Today** (spool restock). This
/// suite is executed on one iPhone and one iPad destination to prove the
/// grouped presentation and server ordering on both device classes.
@MainActor
final class ShiftTasksGroupedUITests: ShiftTasksUITestBase {
    private let nowTaskID = "78200000-0000-0000-0000-000000000001"
    private let timelineTaskID = "78200000-0000-0000-0000-000000000002"
    private let anytimeTaskID = "78200000-0000-0000-0000-000000000003"

    func testGroupedChecklistRendersServerGroupsInServerOrder() {
        openTasksDestination()

        let nowHeader = app.descendants(matching: .any)["shiftTasks.group.now"]
        let timelineHeader = app.descendants(matching: .any)["shiftTasks.group.timeline"]
        let anytimeHeader = app.descendants(matching: .any)["shiftTasks.group.anytimeToday"]

        XCTAssertTrue(
            nowHeader.waitForExistence(timeout: 10),
            "The Now anchor group header must render"
        )
        XCTAssertTrue(
            timelineHeader.waitForExistence(timeout: 5),
            "The Timeline anchor group header must render"
        )
        XCTAssertTrue(
            anytimeHeader.waitForExistence(timeout: 5),
            "The Anytime Today anchor group header must render"
        )

        let nowRow = app.otherElements["shiftTasks.row.info.\(nowTaskID)"]
        let timelineRow = app.otherElements["shiftTasks.row.info.\(timelineTaskID)"]
        let anytimeRow = app.otherElements["shiftTasks.row.info.\(anytimeTaskID)"]

        XCTAssertTrue(nowRow.waitForExistence(timeout: 5))
        XCTAssertTrue(timelineRow.waitForExistence(timeout: 5))
        XCTAssertTrue(anytimeRow.waitForExistence(timeout: 5))

        // Causal server order: Now precedes Timeline precedes Anytime Today.
        // Assert on stable row geometry (no sleeps / polling).
        XCTAssertLessThan(
            nowRow.frame.minY, timelineRow.frame.minY,
            "The Now group must be presented above the Timeline group"
        )
        XCTAssertLessThan(
            timelineRow.frame.minY, anytimeRow.frame.minY,
            "The Timeline group must be presented above the Anytime Today group"
        )

        // Group headers share the same server ordering as their tasks.
        XCTAssertLessThan(nowHeader.frame.minY, timelineHeader.frame.minY)
        XCTAssertLessThan(timelineHeader.frame.minY, anytimeHeader.frame.minY)

        // The grouped list keeps the preserved print queue reachable
        // from this destination.
        XCTAssertTrue(app.buttons["shiftTasks.printQueue"].exists)
    }
}

/// Failed-state pull-to-refresh recovery proof (issue #782). The scripted
/// Failed-state recovery proof (issue #782, item 1/3). The scripted service
/// fails its first canonical load so the view lands in its `.failed`
/// terminal state, then recovers to the grouped plan on the next canonical
/// load. This proves the production fix: the failed state now hosts a
/// genuine refreshable **scroll container** (`shiftTasks.load.errorList`) —
/// the prior bare `ContentUnavailableView` was not a scroll view, so its
/// `.refreshable` pull-to-refresh was inert — while preserving the explicit
/// Retry control, and the failed state's canonical retry/recovery path (the
/// same canonical reload a pull-to-refresh drives) recovers without a false
/// all-clear. Runs on one iPhone and one iPad destination.
///
/// NOTE: the literal pull-to-refresh *gesture* cannot be driven
/// deterministically under XCUI in the current Xcode/simulator toolchain
/// (synthesized drags do not engage SwiftUI's `.refreshable` control), so
/// this suite asserts the refreshable-container affordance structurally and
/// exercises the canonical recovery through the failed state's retry
/// control. The bounded first-fail-then-recover scenario is additionally
/// proven deterministically in `UITestBootstrapTests`.
@MainActor
final class ShiftTasksFailedRefreshUITests: ShiftTasksUITestBase {
    override var additionalLaunchArguments: [String] {
        ["--uitesting-shift-task-initial-load-failure"]
    }

    func testFailedStateHostsRefreshableScrollContainerAndRecoversCanonically() {
        openTasksDestination()

        // The terminal `.failed` state renders inside a genuine refreshable
        // scroll container — the affordance that makes pull-to-refresh
        // functionally available from the failed state (the prior bare
        // ContentUnavailableView was not a scroll view).
        let errorList = app.scrollViews["shiftTasks.load.errorList"]
        XCTAssertTrue(
            errorList.waitForExistence(timeout: 10),
            "The failed state must render inside a genuine refreshable scroll container"
        )
        // The explicit Retry control is preserved in the failed state. The
        // ContentUnavailableView container propagates the
        // `shiftTasks.load.error` identifier to its children, so the Retry
        // action surfaces as the sole button under that identifier.
        let retry = app.buttons["shiftTasks.load.error"]
        XCTAssertTrue(
            retry.exists,
            "The explicit Retry control must remain available in the failed state"
        )
        XCTAssertEqual(retry.label, "Retry task refresh")
        // No false all-clear: the grouped plan must not be present yet.
        XCTAssertFalse(
            app.descendants(matching: .any)["shiftTasks.group.now"].exists
        )

        // The failed state's canonical retry/recovery path — the same
        // canonical reload the pull-to-refresh gesture drives — recovers to
        // the grouped plan and clears the failed terminal state.
        retry.tap()
        let nowGroup = app.descendants(matching: .any)["shiftTasks.group.now"]
        XCTAssertTrue(
            nowGroup.waitForExistence(timeout: 10),
            "The failed state's canonical recovery path must publish the grouped plan"
        )
        XCTAssertTrue(
            errorList.waitForNonExistence(timeout: 5),
            "The failed terminal state must clear once the canonical recovery publishes"
        )
    }
}
