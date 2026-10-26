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
        for label in ["Ready to seed", "Ready to harvest"] {
            let stateCard = app.descendants(matching: .any)
                .matching(NSPredicate(format: "label CONTAINS %@", label)).firstMatch
            XCTAssertTrue(
                stateCard.waitForExistence(timeout: 5) && stateCard.isHittable,
                "The Farm store image must show the \(label) card state."
            )
        }
        capture("01-farm")

        card.tap()
        let status = app.descendants(matching: .any)["printer.detail.panel.status"]
        XCTAssertTrue(status.waitForExistence(timeout: 10))
        XCTAssertTrue(app.descendants(matching: .any)["printer.detail.hero"].exists)
        XCTAssertTrue(app.staticTexts.matching(NSPredicate(
            format: "label == %@", "Print progress 64 percent"
        )).firstMatch.waitForExistence(timeout: 8))
        // The fixture reports no layer data, so the Layer fact is omitted rather than shown as "--" (#3496).
        let facts = app.descendants(matching: .any)
        XCTAssertTrue(facts.matching(NSPredicate(
            format: "label BEGINSWITH %@ AND label CONTAINS %@", "Left", "38m"
        )).firstMatch.waitForExistence(timeout: 8))
        XCTAssertTrue(facts.matching(NSPredicate(format: "label BEGINSWITH %@", "Done at")).firstMatch.exists)
        XCTAssertFalse(facts.matching(NSPredicate(format: "label BEGINSWITH %@", "Layer")).firstMatch.exists)
        XCTAssertFalse(facts.matching(NSPredicate(
            format: "label == %@ OR label ENDSWITH %@", "--", ", --"
        )).firstMatch.exists)
        XCTAssertFalse(facts.matching(NSPredicate(format: "label CONTAINS[c] %@", "Unavailable")).firstMatch.exists)
        capture("02-printer-status")

        // The redesigned Control tab opens on Tune while printing (#3496, #3540).
        let selector = app.descendants(matching: .any)["printer.detail.panel.selector"]
        XCTAssertTrue(selector.waitForExistence(timeout: 8))
        selector.buttons["Control"].tap()
        XCTAssertTrue(app.descendants(matching: .any)["printer.detail.panel.control"].waitForExistence(timeout: 8))
        let tune = app.buttons["printer.controls.segment.tune"]
        XCTAssertTrue(tune.waitForExistence(timeout: 8))
        XCTAssertTrue(tune.isSelected, "Control opens on Tune while printing.")
        XCTAssertTrue(app.otherElements["printer.controls.runtime"].waitForExistence(timeout: 8))
        capture("06-printer-control")
        selector.buttons["Status"].tap()
        XCTAssertTrue(status.waitForExistence(timeout: 8))
        app.buttons["printer.detail.farm"].tap()

        let queue = shellDestinationButton(tabIdentifier: "tab.queue", timeout: 8)
        XCTAssertTrue(queue.exists)
        queue.tap()
        for section in ["printing", "queued"] {
            let heading = app.descendants(matching: .any)["jobList.section.\(section)"]
            XCTAssertTrue(heading.waitForExistence(timeout: 10))
            XCTAssertTrue(heading.isHittable, "Both queue bands must be visible in the store image.")
        }
        if UIDevice.current.userInterfaceIdiom == .pad {
            let retry = app.buttons["job.retry.30000000-0003-0000-0000-000000000009"]
            XCTAssertTrue(retry.waitForExistence(timeout: 10))
            XCTAssertTrue(retry.isEnabled)
        }
        capture("03-queue")

        let filament = shellDestinationButton(tabIdentifier: "tab.filament", timeout: 8)
        XCTAssertTrue(filament.exists)
        filament.tap()
        XCTAssertTrue(app.buttons["inventory.addSpool"].waitForExistence(timeout: 10))
        XCTAssertTrue(app.buttons["inventory.filter.all"].waitForExistence(timeout: 10))
        XCTAssertFalse(app.staticTexts["Printer assignment unavailable"].exists)
        capture("04-filament")

        app.buttons["navigation.scan"].tap()
        for identifier in ["scan.primary", "scan.nfc"] {
            let button = app.buttons[identifier]
            XCTAssertTrue(button.waitForExistence(timeout: 8))
            XCTAssertTrue(button.isEnabled)
        }
        capture("05-scan")

        // A camera-equipped printer shows the camera header above full-width media (#3496 D2).
        relaunchAppForTest(additionalArguments: ["--uitesting-issue3496-camera-snapshot"])
        XCUIDevice.shared.orientation = .portrait
        let cameraCard = app.buttons["farm-card-10000000-0001-0000-0000-000000000001"]
        XCTAssertTrue(cameraCard.waitForExistence(timeout: 10))
        cameraCard.tap()
        let cameraTitle = app.staticTexts["printer.detail.camera.title"]
        XCTAssertTrue(cameraTitle.waitForExistence(timeout: 10))
        XCTAssertEqual(cameraTitle.label, "Camera")
        XCTAssertTrue(app.descendants(matching: .any)["printer.detail.camera.media"].waitForExistence(timeout: 10))
        for identifier in ["printer.detail.camera.rotate", "printer.detail.camera.refresh"] {
            XCTAssertTrue(app.buttons[identifier].waitForExistence(timeout: 8), identifier)
        }
        capture("07-printer-camera")
    }

    /// Waits for a settled frame, asserts on that rendering, then attaches it only if
    /// a confirming frame is byte-identical, so the assertions cover the exact image.
    private func capture(_ name: String) {
        let deadline = ProcessInfo.processInfo.systemUptime + 20
        var previous: Data?
        var identicalFrames = 0
        while ProcessInfo.processInfo.systemUptime < deadline {
            RunLoop.current.run(until: Date().addingTimeInterval(0.5))
            let data = XCUIScreen.main.screenshot().pngRepresentation
            identicalFrames = data == previous ? identicalFrames + 1 : 0
            previous = data
            guard identicalFrames >= 3 else { continue }
            // Store images must never show an unresolved status (whole word, any case).
            let unknown = app.descendants(matching: .any)
                .matching(NSPredicate(format: "label MATCHES[c] %@", "(?s).*\\bunknown\\b.*"))
            XCTAssertEqual(
                unknown.count, 0,
                "Store screen \(name) shows unknown text: \(unknown.allElementsBoundByIndex.map(\.label))"
            )
            let confirmed = XCUIScreen.main.screenshot()
            guard confirmed.pngRepresentation == data else {
                identicalFrames = 0
                continue
            }
            let attachment = XCTAttachment(screenshot: confirmed)
            attachment.name = "app-store-\(name)"
            attachment.lifetime = .keepAlways
            add(attachment)
            return
        }
        XCTFail("Store screen \(name) did not settle within 20 seconds.")
    }
}
