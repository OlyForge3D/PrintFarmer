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
}
