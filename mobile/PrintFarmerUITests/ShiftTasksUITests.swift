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

        let queueList = app.descendants(matching: .any)["jobList.root"]
        let historyButton = app.buttons["jobList.history.open"]
        for _ in 0..<8 where !historyButton.isHittable {
            if queueList.exists {
                queueList.swipeUp()
            } else {
                app.swipeUp()
            }
        }
        XCTAssertTrue(
            historyButton.waitForExistence(timeout: 8),
            "Queue must expose the separate Job History destination",
            file: file,
            line: line
        )
        XCTAssertTrue(historyButton.isHittable, "Job History menu must be reachable", file: file, line: line)
        let diagnostic = app.launchArguments.contains("--uitesting-history-menu-diagnostics")
        let tapStarted = ProcessInfo.processInfo.systemUptime
        historyButton.tap()
        let tapReturned = ProcessInfo.processInfo.systemUptime

        let selectHistory = app.buttons["jobList.history.select"]
        let menuOpened = selectHistory.waitForExistence(timeout: 5)
        if diagnostic {
            let waitReturned = ProcessInfo.processInfo.systemUptime
            let attachment = XCTAttachment(string: """
                tapStarted=\(tapStarted)
                tapReturned=\(tapReturned)
                waitReturned=\(waitReturned)
                menuOpened=\(menuOpened)
                \(app.debugDescription)
                """)
            attachment.name = "History menu single-tap diagnostic"
            attachment.lifetime = .keepAlways
            add(attachment)
        }
        XCTAssertTrue(menuOpened, file: file, line: line)
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
