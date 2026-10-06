import XCTest
import UIKit

@MainActor
final class AppStoreScreenshotUITests: PrintFarmerUITestCase {
    override var waitsForNavigationReadiness: Bool { true }
    override var additionalLaunchArguments: [String] {
        [
            "--uitesting-issue3259-visual-acceptance",
            "--uitesting-app-store-screenshots",
            "-pf_theme_mode", "dark",
            "-UIPreferredContentSizeCategoryName", "UICTContentSizeCategoryL",
            "-AppleLanguages", "(en)",
            "-AppleLocale", "en_US",
        ]
    }

    func testCaptureStoreScreens() {
        XCUIDevice.shared.orientation = .portrait
        let card = app.buttons["farm-card-10000000-0001-0000-0000-000000000001"]
        XCTAssertTrue(card.waitForExistence(timeout: 10))
        let eta = XCTNSPredicateExpectation(
            predicate: NSPredicate(format: "label CONTAINS %@", "38m left"),
            object: card
        )
        XCTAssertEqual(XCTWaiter.wait(for: [eta], timeout: 10), .completed)
        capture("01-farm")

        card.tap()
        let status = app.descendants(matching: .any)["printer.detail.panel.status"]
        XCTAssertTrue(status.waitForExistence(timeout: 10))
        XCTAssertTrue(app.descendants(matching: .any)["printer.detail.hero"].exists)
        XCTAssertTrue(app.descendants(matching: .any)["printer.detail.job.progress"].exists)
        capture("02-printer-status")
        app.buttons["printer.detail.farm"].tap()

        let queue = shellDestinationButton(tabIdentifier: "tab.queue", timeout: 8)
        XCTAssertTrue(queue.exists)
        queue.tap()
        for section in ["printing", "queued"] {
            let heading = app.descendants(matching: .any)["jobList.section.\(section)"]
            XCTAssertTrue(heading.waitForExistence(timeout: 10))
            XCTAssertTrue(heading.isHittable, "Both queue bands must be visible in the store image.")
        }
        capture("03-queue")

        let filament = shellDestinationButton(tabIdentifier: "tab.filament", timeout: 8)
        XCTAssertTrue(filament.exists)
        filament.tap()
        XCTAssertTrue(app.buttons["inventory.addSpool"].waitForExistence(timeout: 10))
        XCTAssertTrue(app.buttons["inventory.filter.all"].waitForExistence(timeout: 10))
        capture("04-filament")

        app.buttons["navigation.scan"].tap()
        for identifier in ["scan.primary", "scan.nfc"] {
            let button = app.buttons[identifier]
            XCTAssertTrue(button.waitForExistence(timeout: 8))
            XCTAssertTrue(button.isEnabled)
        }
        capture("05-scan")
    }

    private func capture(_ name: String) {
        // Require a settled rendering, including asynchronous fixture thumbnails.
        let deadline = ProcessInfo.processInfo.systemUptime + 15
        var previous: Data?
        var identicalFrames = 0
        while ProcessInfo.processInfo.systemUptime < deadline {
            RunLoop.current.run(until: Date().addingTimeInterval(0.5))
            let screenshot = XCUIScreen.main.screenshot()
            let data = screenshot.pngRepresentation
            identicalFrames = data == previous ? identicalFrames + 1 : 0
            previous = data
            if identicalFrames >= 3 {
                let attachment = XCTAttachment(screenshot: screenshot)
                attachment.name = "app-store-\(name)"
                attachment.lifetime = .keepAlways
                add(attachment)
                return
            }
        }
        XCTFail("Store screen \(name) did not settle within 15 seconds.")
    }
}
