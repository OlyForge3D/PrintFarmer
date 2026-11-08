import XCTest
import UIKit
import CryptoKit

@MainActor
class ScreenshotCaptureUITestCase: QueueUITestBase {
    var capturesCatalog: Bool { false }
    private(set) var catalogAuditFailures: [String] = []
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

    func captureScreens() throws {
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

        try capture("01-farm")

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
        try capture("02-printer-status")

        // The redesigned Control tab opens on Tune while printing (#3496, #3540).
        let selector = app.descendants(matching: .any)["printer.detail.panel.selector"]
        XCTAssertTrue(selector.waitForExistence(timeout: 8))
        selector.buttons["Control"].tap()
        XCTAssertTrue(app.descendants(matching: .any)["printer.detail.panel.control"].waitForExistence(timeout: 8))
        let tune = app.buttons["printer.controls.segment.tune"]
        XCTAssertTrue(tune.waitForExistence(timeout: 8))
        XCTAssertTrue(tune.isSelected, "Control opens on Tune while printing.")
        XCTAssertTrue(app.otherElements["printer.controls.runtime"].waitForExistence(timeout: 8))
        try capture("06-printer-control")
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
        try capture("03-queue")

        let filament = shellDestinationButton(tabIdentifier: "tab.filament", timeout: 8)
        XCTAssertTrue(filament.exists)
        filament.tap()
        XCTAssertTrue(app.buttons["inventory.addSpool"].waitForExistence(timeout: 10))
        XCTAssertTrue(app.buttons["inventory.filter.all"].waitForExistence(timeout: 10))
        XCTAssertFalse(app.staticTexts["Printer assignment unavailable"].exists)
        try capture("04-filament")

        app.buttons["navigation.scan"].tap()
        for identifier in ["scan.primary", "scan.nfc"] {
            let button = app.buttons[identifier]
            XCTAssertTrue(button.waitForExistence(timeout: 8))
            XCTAssertTrue(button.isEnabled)
        }
        try capture("05-scan")

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
        try capture("07-printer-camera")
    }

    /// Waits for a settled frame, asserts on that rendering, then attaches it only if
    /// a confirming frame is byte-identical, so the assertions cover the exact image.
    func capture(_ name: String) throws {
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
            if !capturesCatalog {
                let unknown = app.descendants(matching: .any)
                    .matching(NSPredicate(format: "label MATCHES[c] %@", "(?s).*\\bunknown\\b.*"))
                XCTAssertEqual(
                    unknown.count, 0,
                    "Store screen \(name) shows unknown text: \(unknown.allElementsBoundByIndex.map(\.label))"
                )
            }
            var auditIssues: [String] = []
            if capturesCatalog {
                // Handle every finding in the receipt; the catalog test fails
                // after all screens so one defect cannot hide later audits.
                try app.performAccessibilityAudit(for: .all) { issue in
                    auditIssues.append("\(issue.compactDescription): \(issue.detailedDescription)")
                    return true
                }
            }
            let confirmed = XCUIScreen.main.screenshot()
            guard confirmed.pngRepresentation == data else {
                identicalFrames = 0
                continue
            }
            if capturesCatalog {
                let catalogImage = XCTAttachment(data: data, uniformTypeIdentifier: "public.png")
                catalogImage.name = "catalog-image-\(name)"
                catalogImage.lifetime = .keepAlways
                add(catalogImage)
                let receipt: [String: String] = [
                    "scenarioId": name,
                    "audit": auditIssues.isEmpty ? "passed" : "failed",
                    "auditIssues": auditIssues.joined(separator: "\n"),
                    "sha256": SHA256.hash(data: data).map { String(format: "%02x", $0) }.joined(),
                    "appearance": "dark",
                    "contentSize": "UICTContentSizeCategoryL",
                    "locale": Locale.current.identifier,
                    "orientation": XCUIDevice.shared.orientation == .portrait ? "portrait" : "other",
                    "device": ProcessInfo.processInfo.environment["SIMULATOR_MODEL_IDENTIFIER"] ?? "unknown",
                    "runtime": UIDevice.current.systemVersion,
                ]
                let receiptAttachment = XCTAttachment(
                    data: try JSONSerialization.data(withJSONObject: receipt, options: [.sortedKeys]),
                    uniformTypeIdentifier: "public.json"
                )
                receiptAttachment.name = "catalog-receipt-\(name)"
                receiptAttachment.lifetime = .keepAlways
                add(receiptAttachment)
                catalogAuditFailures.append(contentsOf: auditIssues.map { "\(name): \($0)" })
            } else {
                let attachment = XCTAttachment(screenshot: confirmed)
                attachment.name = "app-store-\(name)"
                attachment.lifetime = .keepAlways
                add(attachment)
            }
            return
        }
        XCTFail("Store screen \(name) did not settle within 20 seconds.")
    }
}

@MainActor
final class AppStoreScreenshotUITests: ScreenshotCaptureUITestCase {
    func testCaptureStoreScreens() throws {
        try captureScreens()
    }
}

@MainActor
final class CatalogVisualAccessibilityUITests: ScreenshotCaptureUITestCase {
    override var capturesCatalog: Bool { true }

    override func tearDownWithError() throws {
        XCTAssertTrue(catalogAuditFailures.isEmpty, catalogAuditFailures.joined(separator: "\n"))
        try super.tearDownWithError()
    }

    func testCaptureCatalogWithAccessibilityAudits() throws {
        try captureScreens()
    }

    func testCaptureAccount() throws {
        try openAccount()
        try capture("08-account")
    }

    func testCaptureSettings() throws {
        try openAccount()
        try tapRequired(app.buttons["account.destination.settings"])
        try require(app.navigationBars["Settings"])
        try capture("09-settings")
    }

    func testCaptureNotifications() throws {
        try openAccount()
        try tapRequired(app.buttons["account.destination.notifications"])
        try require(app.navigationBars["Notifications"])
        try capture("10-notifications")
    }

    func testCaptureServersAndEditor() throws {
        try openAccount()
        try tapRequired(app.buttons["account.destination.manageServers"])
        try require(app.navigationBars["Servers"])
        try require(app.descendants(matching: .any)["serversList"])
        try capture("11-servers")
        try tapRequired(app.navigationBars["Servers"].buttons["Add Server"])
        try require(app.textFields["serverNameField"])
        try require(app.textFields["serverURLField"])
        try capture("12-server-editor")
    }

    func testCaptureAddSpool() throws {
        try openFilament()
        try tapRequired(app.buttons["inventory.addSpool"])
        try require(app.navigationBars["Add Spool"])
        try capture("13-add-spool")
    }

    func testCapturePartsAndSelectedPart() throws {
        try openFilament()
        try tapRequired(app.buttons["inventory.actions"])
        try tapRequired(app.buttons["filament.printedParts"])
        try require(app.navigationBars["Printed Parts"])
        let bracket = app.buttons["partsInventory.row.BRKT-01"]
        try require(bracket)
        try capture("14-parts-inventory")
        try tapRequired(bracket)
        try require(app.steppers["partScan.deltaStepper"])
        try require(app.buttons["partScan.applyAdjustment"])
        try capture("15-selected-part")
    }

    func testCaptureBarcodeIntake() throws {
        try openFilament()
        try tapRequired(app.buttons["inventory.actions"])
        try tapRequired(app.buttons["inventory.scan.barcodeIntake"])
        try require(app.navigationBars["Barcode Intake"])
        try capture("16-barcode-intake")
    }

    func testCaptureHistoryJobAndHarvest() throws {
        XCUIDevice.shared.orientation = .portrait
        openJobHistoryMenuAndSelect()
        try require(app.descendants(matching: .any)["jobList.history"])
        let job = app.buttons["job.row.30000000-0003-0000-0000-000000000007"]
        try require(job)
        try capture("17-job-history")
        try tapRequired(job)
        try require(app.buttons["jobDetail.harvestToInventory"])
        try capture("18-job-detail")
        try tapRequired(app.buttons["jobDetail.harvestToInventory"])
        try require(app.navigationBars["Harvest Plate"])
        try require(app.textFields["harvest.binCode"])
        try require(app.buttons["harvest.submit"])
        try capture("19-harvest")
    }

    func testCaptureSpoolPicker() throws {
        XCUIDevice.shared.orientation = .portrait
        try tapRequired(app.buttons["farm-card-10000000-0001-0000-0000-000000000001"])
        let selector = app.descendants(matching: .any)["printer.detail.panel.selector"]
        try require(selector)
        try tapRequired(selector.buttons["Filament"])
        try require(app.descendants(matching: .any)["printer.detail.panel.filament"])
        let change = app.buttons.matching(
            NSPredicate(format: "identifier CONTAINS %@", "/printer/change")
        ).firstMatch
        try tapRequired(change)
        try require(app.navigationBars["Select Material"])
        try capture("20-material-picker")
        try tapRequired(app.buttons["spoolPicker.material.PLA"])
        try require(app.navigationBars["Select Spool"])
        try require(app.buttons["spoolPicker.spool.2"])
        try capture("21-spool-picker")
    }

    private func openAccount() throws {
        XCUIDevice.shared.orientation = .portrait
        try tapRequired(app.descendants(matching: .any)["navigation.account"])
        try require(app.navigationBars["Account"])
    }

    private func openFilament() throws {
        XCUIDevice.shared.orientation = .portrait
        try tapRequired(shellDestinationButton(tabIdentifier: "tab.filament", timeout: 8))
        try require(app.buttons["inventory.addSpool"])
    }

    private func require(_ element: XCUIElement) throws {
        guard element.waitForExistence(timeout: 10) else {
            XCTFail("Required catalog navigation element missing: \(element)")
            throw CatalogNavigationError.missingElement
        }
    }

    private func tapRequired(_ element: XCUIElement) throws {
        try require(element)
        guard element.isHittable else {
            XCTFail("Required catalog navigation element is not hittable: \(element)")
            throw CatalogNavigationError.missingElement
        }
        element.tap()
    }

    private enum CatalogNavigationError: Error {
        case missingElement
    }
}
