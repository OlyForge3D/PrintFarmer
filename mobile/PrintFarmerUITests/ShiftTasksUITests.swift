import XCTest

@MainActor
final class ShiftTasksUITests: PrintFarmerUITestCase {
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

    private func openTasksDestination(
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
