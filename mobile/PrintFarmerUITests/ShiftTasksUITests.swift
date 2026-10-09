import XCTest

/// Shared queue navigation for harvest and iPad regression suites.
@MainActor
class QueueUITestBase: PrintFarmerUITestCase {
    func openQueueDestination(file: StaticString = #filePath, line: UInt = #line) {
        let queue = shellDestinationButton(tabIdentifier: "tab.queue", timeout: 8)
        XCTAssertTrue(queue.exists, "Queue must be reachable", file: file, line: line)
        queue.tap()
        XCTAssertTrue(
            app.descendants(matching: .any)["jobList.root"].waitForExistence(timeout: 8),
            file: file, line: line
        )
    }

    func openJobHistoryMenuAndSelect(
        file: StaticString = #filePath,
        line: UInt = #line
    ) {
        openQueueDestination(file: file, line: line)

        let queueList = app.collectionViews["jobList.combined.list"]
        let historyButton = app.buttons["jobList.history.open"]
        for _ in 0..<8 where !historyButton.isHittable {
            queueList.swipeUp()
        }
        XCTAssertTrue(
            historyButton.waitForExistence(timeout: 8),
            "Queue must expose the separate Job History destination",
            file: file,
            line: line
        )
        historyButton.tap()

        let selectHistory = app.buttons["jobList.history.select"]
        XCTAssertTrue(selectHistory.waitForExistence(timeout: 5), file: file, line: line)
        selectHistory.tap()
        XCTAssertTrue(
            app.descendants(matching: .any)["jobList.history"]
                .waitForExistence(timeout: 8),
            "Job History sheet must open",
            file: file,
            line: line
        )
    }
}
