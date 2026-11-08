import XCTest
import UIKit
import CryptoKit

@MainActor
class ScreenshotCaptureUITestCase: QueueUITestBase {
    var capturesCatalog: Bool { false }
    var captureThemeMode = "dark"
    var captureContentSize = "UICTContentSizeCategoryL"
    private(set) var catalogAuditFailures: [String] = []
    override var waitsForNavigationReadiness: Bool { true }
    override var additionalLaunchArguments: [String] {
        [
            "--uitesting-issue3259-visual-acceptance",
            "--uitesting-app-store-screenshots",
        ] + renderProfileArguments
    }

    var renderProfileArguments: [String] {
        [
            "-pf_theme_mode", captureThemeMode,
            "-UIPreferredContentSizeCategoryName", captureContentSize,
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
        var penultimate: Data?
        var identicalFrames = 0
        while ProcessInfo.processInfo.systemUptime < deadline {
            RunLoop.current.run(until: Date().addingTimeInterval(0.5))
            let data = XCUIScreen.main.screenshot().pngRepresentation
            identicalFrames = data == previous ? identicalFrames + 1 : 0
            penultimate = previous
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
                    "appearance": captureThemeMode == "system"
                        ? (XCUIDevice.shared.appearance == .light ? "light" : "dark")
                        : captureThemeMode,
                    "themeMode": captureThemeMode,
                    "contentSize": captureContentSize,
                    "locale": Locale.current.identifier,
                    "orientation": XCUIDevice.shared.orientation == .portrait ? "portrait"
                        : (XCUIDevice.shared.orientation == .landscapeLeft
                            || XCUIDevice.shared.orientation == .landscapeRight ? "landscape" : "other"),
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
        if capturesCatalog {
            for (label, data) in [("previous", penultimate), ("last", previous)] {
                if let data {
                    let attachment = XCTAttachment(data: data, uniformTypeIdentifier: "public.png")
                    attachment.name = "catalog-unsettled-\(name)-\(label)"
                    attachment.lifetime = .keepAlways
                    add(attachment)
                }
            }
            let tree = XCTAttachment(string: app.debugDescription)
            tree.name = "catalog-unsettled-\(name)-accessibility-tree"
            tree.lifetime = .keepAlways
            add(tree)
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

    func testCaptureNavigationAxis() throws {
        XCUIDevice.shared.orientation = .portrait
        for identifier in ["tab.farm", "tab.queue", "tab.filament"] {
            try require(shellDestinationButton(tabIdentifier: identifier, timeout: 8))
        }
        try require(app.buttons["navigation.scan"])
        try require(app.descendants(matching: .any)["navigation.account"])
        try tapRequired(shellDestinationButton(tabIdentifier: "tab.farm", timeout: 8))
        try require(app.buttons["farm-card-10000000-0001-0000-0000-000000000001"])
        try capture("41-navigation-farm")
        try tapRequired(shellDestinationButton(tabIdentifier: "tab.queue", timeout: 8))
        try require(app.descendants(matching: .any)["jobList.section.queued"])
        try capture("42-navigation-queue")
        try openFilament()
        try capture("43-navigation-filament")
        try openAccount()
        try require(app.buttons["account.destination.settings"])
        try capture("44-navigation-account")
    }

    func testCaptureAppearanceAxis() throws {
        let previousAppearance = XCUIDevice.shared.appearance
        defer { XCUIDevice.shared.appearance = previousAppearance }
        XCUIDevice.shared.appearance = .light
        try relaunchProfile(theme: "light")
        try capture("47-appearance-light")
        try relaunchProfile(theme: "dark")
        try capture("48-appearance-dark")
        try relaunchProfile(theme: "system")
        XCTAssertEqual(XCUIDevice.shared.appearance, .light)
        try capture("49-appearance-system-light")
    }

    func testCaptureViewportAxis() throws {
        defer { XCUIDevice.shared.orientation = .portrait }
        try relaunchProfile(theme: "dark", contentSize: "UICTContentSizeCategoryAccessibilityXXXL")
        try capture("50-viewport-largest-text")
        try relaunchProfile(theme: "dark")
        XCUIDevice.shared.orientation = .landscapeLeft
        let deadline = ProcessInfo.processInfo.systemUptime + 10
        while app.windows.firstMatch.frame.width <= app.windows.firstMatch.frame.height
            && ProcessInfo.processInfo.systemUptime < deadline {
            RunLoop.current.run(until: Date().addingTimeInterval(0.2))
        }
        XCTAssertGreaterThan(app.windows.firstMatch.frame.width, app.windows.firstMatch.frame.height)
        try capture("51-viewport-landscape")
    }

    private func relaunchProfile(
        theme: String, contentSize: String = "UICTContentSizeCategoryL"
    ) throws {
        captureThemeMode = theme
        captureContentSize = contentSize
        app.launchArguments = ["--uitesting"] + additionalLaunchArguments
        relaunchAppForTest(additionalArguments: [])
        XCUIDevice.shared.orientation = .portrait
        try require(app.buttons["farm-card-10000000-0001-0000-0000-000000000001"])
    }

    func testCaptureIdleInlineControls() throws {
        relaunchAppForTest(additionalArguments: ["--uitesting-issue3259-control-idle"])
        XCUIDevice.shared.orientation = .portrait
        try tapRequired(app.buttons["farm-card-10000000-0001-0000-0000-000000000001"])
        let selector = app.descendants(matching: .any)["printer.detail.panel.selector"]
        try tapRequired(selector.buttons["Control"])
        try require(app.descendants(matching: .any)["printer.detail.panel.control"])
        let move = app.buttons["printer.controls.segment.move"]
        try require(move)
        XCTAssertTrue(move.isSelected)
        XCTAssertFalse(app.buttons["printer.controls.segment.tune"].isEnabled)
        try require(app.otherElements["printer.controls.position"])
        try require(app.otherElements["printer.controls.motion-group"])
        try require(app.buttons["printer.detail.control.emergencyStop"])
        for identifier in ["home.all", "home.xy", "home.z", "jog.step.1", "jog.step.10", "jog.step.50"] {
            try require(app.buttons["printer.controls.\(identifier)"])
        }
        // Inspect controls only: no motion, temperature, preset or emergency command.
        try capture("45-inline-idle-move")
        try tapRequired(app.buttons["printer.controls.segment.heat"])
        try require(app.staticTexts["printer.controls.hotend.measured"])
        XCTAssertEqual(app.staticTexts["printer.controls.hotend.measured"].label, "Current 24 degrees")
        XCTAssertEqual(app.staticTexts["printer.controls.bed.measured"].label, "Current 23 degrees")
        try require(app.buttons["printer.controls.hotend.increase"])
        XCTAssertTrue(app.buttons["printer.controls.hotend.increase"].isEnabled)
        try require(app.buttons["printer.detail.control.emergencyStop"])
        try capture("46-inline-idle-heat")
    }

    func testCaptureIdleSensors() throws {
        relaunchAppForTest(additionalArguments: [
            "--uitesting-issue3259-control-idle", "--uitesting-issue3496-tune-sensors"
        ])
        XCUIDevice.shared.orientation = .portrait
        try tapRequired(app.buttons["farm-card-10000000-0001-0000-0000-000000000001"])
        let selector = app.descendants(matching: .any)["printer.detail.panel.selector"]
        try tapRequired(selector.buttons["Control"])
        try tapRequired(app.buttons["printer.controls.segment.heat"])
        let control = app.descendants(matching: .any)["printer.detail.panel.control"]
        let sensor = "printer.controls.sensor.heater_generic chamber.TargetTemperature"
        let target = app.descendants(matching: .any)["\(sensor).value"]
        for _ in 0..<4 where !target.isHittable { control.swipeUp() }
        try require(target)
        try require(app.buttons["\(sensor).increase"])
        XCTAssertTrue(app.buttons["\(sensor).increase"].isEnabled)
        try require(app.buttons["\(sensor).decrease"])
        // Present the synthetic heater controls without changing their targets.
        try capture("52-inline-idle-sensors")
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
        relaunchAppForTest(additionalArguments: ["--uitesting-catalog-read-only", "--uitesting-catalog-parts"])
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

    func testCapturePartLookup() throws {
        relaunchAppForTest(additionalArguments: ["--uitesting-catalog-read-only", "--uitesting-catalog-parts"])
        try openFilament()
        try tapRequired(app.buttons["inventory.actions"])
        try tapRequired(app.buttons["filament.printedParts"])
        try require(app.buttons["partsInventory.row.BRKT-01"])
        try tapRequired(app.buttons["inventory.scan"])
        try tapRequired(app.buttons["inventory.partLookup"])
        try require(app.buttons["inventory.partLookup.row.BRKT-01"])
        try require(app.buttons["Cancel"])
        try capture("38-part-lookup")
    }

    func testCaptureNFCWritePreview() throws {
        try openFilament()
        let spool = app.descendants(matching: .any)["inventory.spool.3"]
        try require(spool)
        guard spool.isHittable else {
            XCTFail("Synthetic spool must be visible before opening its context menu.")
            throw CatalogNavigationError.missingElement
        }
        spool.press(forDuration: 1)
        try tapRequired(app.buttons["Write NFC Tag"])
        try require(app.navigationBars["Write NFC Tag"])
        try require(app.buttons["Write Tag"])
        // Preview only: never invoke the NFC session or submit a spool update.
        try capture("40-nfc-write-preview")
    }

    func testCaptureHistoryJobAndHarvest() throws {
        relaunchAppForTest(additionalArguments: ["--uitesting-catalog-read-only", "--uitesting-catalog-history"])
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
        try tapRequired(app.buttons["Scan QR code"])
        try require(app.navigationBars["Scan QR Code"])
        try require(app.staticTexts["Scanner Unavailable"])
        XCTAssertFalse(app.alerts["Camera Access Required"].exists)
        try capture("39-qr-scanner-unavailable")
    }

    func testCaptureScannedBinAndEligibleJobs() throws {
        try openSyntheticScan("--uitesting-catalog-scan-bin")
        try require(app.navigationBars["Bin Scanned"])
        try require(app.steppers["binScan.quantityStepper"])
        try capture("29-bin-scan-result")
        try tapRequired(app.buttons["binScan.harvestShortcut"])
        try require(app.navigationBars["Select Completed Job"])
        try require(app.buttons["Cancel"])
        XCTAssertFalse(app.activityIndicators.firstMatch.exists)
        try capture("30-eligible-job-picker")
    }

    func testCaptureScannedPart() throws {
        try openSyntheticScan("--uitesting-catalog-scan-part")
        try require(app.steppers["partScan.deltaStepper"])
        try require(app.buttons["partScan.applyAdjustment"])
        try capture("31-part-scan-result")
    }

    func testCaptureScanPermissionError() throws {
        try openSyntheticScan("--uitesting-catalog-scan-error")
        try require(app.alerts["Scan Error"])
        try require(app.alerts["Scan Error"].buttons["OK"])
        try capture("32-scan-permission-error")
    }

    private func openSyntheticScan(_ scenario: String) throws {
        relaunchAppForTest(additionalArguments: ["--uitesting-catalog-scanner", scenario])
        XCUIDevice.shared.orientation = .portrait
        try tapRequired(app.buttons["navigation.scan"])
        try tapRequired(app.buttons["scan.primary"])
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

@MainActor
final class CatalogRootVisualAccessibilityUITests: ScreenshotCaptureUITestCase {
    override var capturesCatalog: Bool { true }
    override var waitsForNavigationReadiness: Bool { false }
    override var additionalLaunchArguments: [String] {
        rootArguments(onboardingSeen: true, permissionCompleted: true)
    }

    override func tearDownWithError() throws {
        XCTAssertTrue(catalogAuditFailures.isEmpty, catalogAuditFailures.joined(separator: "\n"))
        try super.tearDownWithError()
    }

    func testCaptureLogin() throws {
        XCUIDevice.shared.orientation = .portrait
        XCTAssertTrue(app.textFields["usernameField"].waitForExistence(timeout: 10))
        XCTAssertTrue(app.secureTextFields["passwordField"].exists)
        XCTAssertTrue(app.buttons["loginButton"].exists)
        try capture("22-login")
    }

    func testCaptureOnboardingPages() throws {
        relaunchRoot(onboardingSeen: false, permissionCompleted: false)
        XCTAssertTrue(app.staticTexts["Monitor Your Farm"].waitForExistence(timeout: 10))
        try capture("23-onboarding-monitor")
        let queue = app.buttons["pageIndicator.page.queue"]
        XCTAssertTrue(queue.waitForExistence(timeout: 10) && queue.isHittable)
        queue.tap()
        XCTAssertTrue(app.staticTexts["Smart Job Queue"].waitForExistence(timeout: 10))
        try capture("24-onboarding-queue")
        let alerts = app.buttons["pageIndicator.page.alerts"]
        XCTAssertTrue(alerts.waitForExistence(timeout: 10) && alerts.isHittable)
        alerts.tap()
        XCTAssertTrue(app.staticTexts["Stay Informed"].waitForExistence(timeout: 10))
        try capture("25-onboarding-alerts")
    }

    func testCaptureLocalNetworkExplanation() throws {
        relaunchRoot(onboardingSeen: true, permissionCompleted: false)
        XCTAssertTrue(app.staticTexts["Local Network Access"].waitForExistence(timeout: 10))
        XCTAssertTrue(app.buttons["Enable Network Access"].exists)
        // Capture the app-owned explanation, not an OS permission prompt.
        try capture("26-local-network-explanation")
    }

    func testCaptureColdOfflineFarmAndWriteReview() throws {
        app.launchArguments = ["--uitesting", "--uitesting-cold-offline-shell"] + renderProfileArguments
        relaunchAppForTest(additionalArguments: [])
        XCUIDevice.shared.orientation = .portrait
        let farm = shellDestinationButton(tabIdentifier: "tab.farm", timeout: 10)
        XCTAssertTrue(farm.waitForExistence(timeout: 10) && farm.isHittable)
        farm.tap()
        XCTAssertTrue(app.buttons["connection-status-bar-stale"].waitForExistence(timeout: 15))
        XCTAssertTrue(app.descendants(matching: .any)["cold-offline-shell"].exists)
        try capture("27-cold-offline-farm")
        let account = app.buttons["navigation.account"]
        XCTAssertTrue(account.waitForExistence(timeout: 10) && account.isHittable)
        account.tap()
        let offlineQueue = app.buttons["account.destination.offlineQueue"]
        XCTAssertTrue(offlineQueue.waitForExistence(timeout: 10) && offlineQueue.isHittable)
        offlineQueue.tap()
        XCTAssertTrue(app.navigationBars["Offline Queue"].waitForExistence(timeout: 10))
        try capture("28-offline-write-review")
    }

    func testCaptureHeldRootStates() throws {
        relaunchCatalogRoot("splash")
        XCTAssertTrue(app.descendants(matching: .any)["launchSplash"].waitForExistence(timeout: 10))
        try capture("33-launch-splash")

        relaunchCatalogRoot("first-server")
        XCTAssertTrue(app.staticTexts["Add Your First Server"].waitForExistence(timeout: 10))
        try capture("34-first-server")

        relaunchCatalogRoot("loading")
        XCTAssertTrue(app.descendants(matching: .any)["backendConnectionGate"].waitForExistence(timeout: 10))
        XCTAssertTrue(app.staticTexts["Connecting to services..."].exists)
        try capture("35-backend-loading")

        relaunchCatalogRoot("snapshot")
        XCTAssertTrue(app.descendants(matching: .any)["snapshotActivationPendingView"].waitForExistence(timeout: 10))
        try capture("36-snapshot-activation")
    }

    func testCaptureSyntheticCertificate() throws {
        relaunchCatalogRoot("certificate")
        XCTAssertTrue(app.descendants(matching: .any)["certificateTrustSheet"].waitForExistence(timeout: 10))
        XCTAssertTrue(app.staticTexts["certificateTrustEndpointText"].exists)
        XCTAssertTrue(app.buttons["certificateTrustCancelButton"].exists)
        try capture("37-certificate-trust")
        app.buttons["certificateTrustCancelButton"].tap()
        let dismissed = XCTNSPredicateExpectation(
            predicate: NSPredicate(format: "exists == false"),
            object: app.descendants(matching: .any)["certificateTrustSheet"]
        )
        XCTAssertEqual(XCTWaiter.wait(for: [dismissed], timeout: 5), .completed)
    }

    private func relaunchCatalogRoot(_ state: String) {
        app.launchArguments = ["--uitesting", "--uitesting-catalog-root",
                               "--uitesting-catalog-root-\(state)"] + rootArguments(
            onboardingSeen: true, permissionCompleted: true
        )
        relaunchAppForTest(additionalArguments: [])
        XCUIDevice.shared.orientation = .portrait
    }

    private func rootArguments(onboardingSeen: Bool, permissionCompleted: Bool) -> [String] {
        [
            "--uitesting-unauthenticated",
            "-hasSeenOnboarding", onboardingSeen ? "YES" : "NO",
            "-hasCompletedNetworkPermission", permissionCompleted ? "YES" : "NO",
            "-isDemoModeActive", "NO",
        ] + renderProfileArguments
    }

    private func relaunchRoot(onboardingSeen: Bool, permissionCompleted: Bool) {
        app.launchArguments = ["--uitesting"] + rootArguments(
            onboardingSeen: onboardingSeen, permissionCompleted: permissionCompleted
        )
        relaunchAppForTest(additionalArguments: [])
        XCUIDevice.shared.orientation = .portrait
    }
}
