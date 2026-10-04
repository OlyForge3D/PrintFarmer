import XCTest
import UIKit

/// XCUI acceptance for the Status/Controls paging integration (issue #2522).
///
/// Runs against the deterministic `--uitesting` authenticated operator-shell
/// bootstrap (same as `OperatorShellUITests`) — no fake-service scenario
/// override is needed since these assertions only depend on demo fleet data
/// plus the per-server "Advanced Printer Controls" safety toggle already
/// exercised by `OperatorShellUITests`.
///
/// Deterministic-test discipline: every wait is a bounded
/// `waitForExistence`; no `Thread.sleep`/`Task.sleep`/retry-until-pass. The
/// navigation-entry helpers (`openFirstPrinterDetail`,
/// `enableAdvancedPrinterControls`) assert every REQUIRED step of the
/// deterministic bootstrap — the Farm tab, a printer card, Settings, and its
/// safety toggle all always exist in this environment, so a missing one is a
/// real regression, not tolerated as "environment variance" (Hicks review
/// finding 18). The one deliberate exception is the farm-card-vs-collection-
/// cell choice inside `openFirstPrinterDetail`: that is picking between two
/// equally valid ways to reach the SAME target, not tolerating its absence.
/// Once a test has actually reached printer detail, every assertion about
/// the Status page, the panel selector, the Controls page, and Emergency
/// Stop is likewise a deterministic `XCTAssertTrue`/`XCTAssertFalse`.
@MainActor
final class PrinterDetailPanelsUITests: PrintFarmerUITestCase {
    override var waitsForNavigationReadiness: Bool { true }
    override var additionalLaunchArguments: [String] {
        ["-UIPreferredContentSizeCategoryName", "UICTContentSizeCategoryL"]
    }

    // MARK: - Navigation helpers

    /// Navigates to the first printer's detail screen. Every step is a
    /// REQUIRED precondition of the deterministic `--uitesting` bootstrap
    /// and is asserted, not silently tolerated.
    private func openFirstPrinterDetail() {
        let farm = shellDestinationButton(tabIdentifier: "tab.farm", timeout: 5)
        XCTAssertTrue(
            farm.exists,
            "The Farm destination must be reachable in the deterministic UI-test bootstrap"
        )
        farm.tap()

        let farmCard = app.buttons
            .matching(NSPredicate(format: "identifier BEGINSWITH %@", "farm-card-"))
            .firstMatch
        if farmCard.waitForExistence(timeout: 5) {
            farmCard.tap()
            return
        }
        // Deliberate choice between two equally valid ways to reach the SAME
        // target (a stable farm-card wrapper vs. the raw collection-view
        // cell) — not a tolerance for the fleet being empty.
        let firstPrinter = app.collectionViews.cells.firstMatch
        XCTAssertTrue(
            firstPrinter.waitForExistence(timeout: 5),
            "The deterministic UI-test fleet must expose at least one printer card or collection cell"
        )
        firstPrinter.tap()
    }

    /// Enables the per-server "Advanced Printer Controls" safety toggle from
    /// Settings. Every step is a REQUIRED precondition of the deterministic
    /// bootstrap and is asserted, not silently tolerated.
    private func enableAdvancedPrinterControls() {
        let farm = shellDestinationButton(tabIdentifier: "tab.farm", timeout: 5)
        XCTAssertTrue(
            farm.exists,
            "The Farm destination must be reachable in the deterministic UI-test bootstrap"
        )
        farm.tap()

        // Account is shared root chrome; enter it before opening Settings.
        let account = app.buttons["navigation.account"]
        XCTAssertTrue(
            account.waitForExistence(timeout: 5),
            "The Account entry point must be reachable from Farm in the deterministic UI-test bootstrap"
        )
        account.tap()
        XCTAssertTrue(
            app.descendants(matching: .any)["account.root"].waitForExistence(timeout: 5),
            "The Account root must appear after tapping into it"
        )

        let settingsDestination = app.buttons["account.destination.settings"]
        XCTAssertTrue(
            settingsDestination.waitForExistence(timeout: 3),
            "Settings must be reachable from Account in the deterministic UI-test bootstrap"
        )
        settingsDestination.tap()

        XCTAssertTrue(
            app.navigationBars["Settings"].waitForExistence(timeout: 5),
            "The Settings screen must appear"
        )
        let toggle = app.switches["settings.advancedPrinterControls"]
        XCTAssertTrue(toggle.waitForExistence(timeout: 3))
        for _ in 0..<3 where !toggle.isHittable {
            app.swipeUp()
        }
        XCTAssertTrue(
            toggle.isHittable,
            "The Advanced Printer Controls safety toggle must be visible and hittable in Settings"
        )
        if toggle.value as? String != "1" {
            // The SwiftUI switch element includes its label; target the knob.
            toggle.coordinate(withNormalizedOffset: CGVector(dx: 0.95, dy: 0.5)).tap()
            let becameOn = XCTNSPredicateExpectation(
                predicate: NSPredicate(format: "value == '1'"),
                object: toggle
            )
            let result = XCTWaiter().wait(for: [becameOn], timeout: 5)
            XCTAssertEqual(
                result, .completed,
                "Tapping the Advanced Printer Controls toggle must flip its own value to on"
            )
        }
    }

    // MARK: - Default entry / gating

    func testEssentialIdentityRemainsAboveBothPagesAndSelectorStaysCompact() {
        openFirstPrinterDetail()
        let identity = app.otherElements["printer.detail.identity"]
        XCTAssertTrue(identity.waitForExistence(timeout: 8))
        let names = app.staticTexts.matching(NSPredicate(
            format: "identifier BEGINSWITH %@", "printer.detail.destination."
        ))
        XCTAssertEqual(names.count, 1)
        let name = names.firstMatch.label
        let selector = app.segmentedControls["printer.detail.panel.selector"]
        XCTAssertTrue(selector.exists)
        XCTAssertLessThan(identity.frame.maxY, selector.frame.minY)
        XCTAssertGreaterThanOrEqual(selector.frame.height, 44)
        XCTAssertEqual(identity.frame.minX, selector.frame.minX, accuracy: 1)
        for title in ["Status", "Control", "Filament", "Queue", "Status"] {
            selector.buttons[title].tap()
            let page = app.descendants(matching: .any)["printer.detail.panel.\(title.lowercased())"]
            XCTAssertTrue(page.waitForExistence(timeout: 5))
            XCTAssertEqual(names.count, 1, "Printer identity must not be duplicated in a page")
            XCTAssertEqual(names.firstMatch.label, name)
            XCTAssertLessThan(identity.frame.maxY, selector.frame.minY)
            XCTAssertLessThanOrEqual(selector.frame.maxY, page.frame.minY)
            if title == "Control" {
                XCTAssertTrue(app.buttons["printer.detail.control.emergencyStop"].isHittable)
            } else {
                XCTAssertFalse(app.buttons["printer.detail.control.emergencyStop"].exists)
            }
            let screenshot = XCTAttachment(screenshot: XCUIScreen.main.screenshot())
            screenshot.name = "Essential complete page \(title)"
            screenshot.lifetime = .keepAlways
            add(screenshot)
        }
    }

    func testStatusUsesAvailableWidthAcrossRotation() {
        openFirstPrinterDetail()
        defer { XCUIDevice.shared.orientation = .portrait }
        for orientation in [UIDeviceOrientation.portrait, .landscapeLeft] {
            XCUIDevice.shared.orientation = orientation
            let overview = app.descendants(matching: .any)["printer.detail.panel.status"]
            XCTAssertTrue(overview.waitForExistence(timeout: 8))
            let expectedLayout = overview.frame.width >= 760
                ? "printer.detail.columns" : "printer.detail.readingColumn"
            XCTAssertTrue(app.otherElements[expectedLayout].waitForExistence(timeout: 5))
            let temperatures = app.otherElements["printer.detail.temperatures"]
            XCTAssertTrue(temperatures.waitForExistence(timeout: 5))
            XCTAssertGreaterThan(temperatures.frame.minY, app.otherElements["printer.detail.job"].frame.minY)
            XCTAssertFalse(app.buttons["printer.detail.control.emergencyStop"].exists)
            let screenshot = XCTAttachment(screenshot: XCUIScreen.main.screenshot())
            screenshot.name = "Essential Status \(orientation == .portrait ? "portrait" : "landscape")"
            screenshot.lifetime = .keepAlways
            add(screenshot)
        }
    }

    func testAccessibilityTextKeepsReadingColumnAndLabeledEmergencyOnBothPages() {
        app.terminate()
        app.launchArguments.removeAll {
            $0 == "-UIPreferredContentSizeCategoryName" || $0 == "UICTContentSizeCategoryL"
        }
        app.launchArguments += [
            "-UIPreferredContentSizeCategoryName", "UICTContentSizeCategoryAccessibilityXXXL",
            "-pf_theme_mode", "dark"
        ]
        app.launchForPrintFarmerUITest()
        openFirstPrinterDetail()
        XCTAssertTrue(app.otherElements["printer.detail.readingColumn"].waitForExistence(timeout: 8))
        let identityScroll = app.scrollViews["printer.detail.identity.scroll"]
        let printerName = identityScroll.staticTexts.matching(
            NSPredicate(format: "identifier BEGINSWITH %@", "printer.detail.destination.")
        ).firstMatch
        XCTAssertTrue(printerName.exists)
        XCTAssertGreaterThanOrEqual(identityScroll.frame.height, printerName.frame.height)
        XCTAssertGreaterThanOrEqual(printerName.frame.minY, identityScroll.frame.minY)
        XCTAssertLessThanOrEqual(printerName.frame.maxY, identityScroll.frame.maxY)
        let selector = app.descendants(matching: .any)
            .matching(identifier: "printer.detail.panel.selector").firstMatch
        XCTAssertTrue(selector.exists)
        for title in ["Status", "Control", "Filament", "Queue"] {
            XCTAssertTrue(selector.buttons[title].isHittable)
            selector.buttons[title].tap()
            XCTAssertTrue(selector.buttons[title].isSelected)
            let page = app.descendants(matching: .any)["printer.detail.panel.\(title.lowercased())"]
            XCTAssertTrue(page.exists)
            XCTAssertGreaterThanOrEqual(page.frame.height, 100, "The pinned header must leave a usable page viewport")
            if title == "Status" {
                let temperatures = app.otherElements["printer.detail.temperatures"]
                let beforeScroll = temperatures.frame.minY
                page.swipeUp()
                XCTAssertLessThan(temperatures.frame.minY, beforeScroll, "The reading column must actually scroll")
            }
            let emergency = app.buttons["printer.detail.control.emergencyStop"]
            if title == "Control" {
            XCTAssertTrue(emergency.isHittable)
            XCTAssertGreaterThanOrEqual(emergency.frame.height, 44)
            XCTAssertGreaterThanOrEqual(emergency.frame.width, 44)
            XCTAssertEqual(emergency.label, "Emergency stop printer")
            } else {
                XCTAssertFalse(emergency.exists)
            }
            let screenshot = XCTAttachment(screenshot: XCUIScreen.main.screenshot())
            screenshot.name = "Essential \(title) accessibility text"
            screenshot.lifetime = .keepAlways
            add(screenshot)
        }
    }

    func testDefaultEntryLandsOnStatusPage() {
        openFirstPrinterDetail()

        let overviewPage = app.descendants(matching: .any)["printer.detail.panel.status"]
        XCTAssertTrue(
            overviewPage.waitForExistence(timeout: 8),
            "Printer detail must default to the Status page"
        )
    }

    func testControlsRemainDiscoverableWithExplanationAndExistingSettingsPathWhileDisabled() {
        openFirstPrinterDetail()

        XCTAssertTrue(
            app.descendants(matching: .any)["printer.detail.panel.status"]
                .waitForExistence(timeout: 8),
            "Printer detail must render the Status page"
        )
        XCTAssertTrue(
            app.segmentedControls["printer.detail.panel.selector"].exists,
            "Both destinations remain discoverable while the safety preference is off"
        )
        app.segmentedControls["printer.detail.panel.selector"].buttons["Control"].tap()
        XCTAssertTrue(app.otherElements["printer.detail.control.unavailable"].waitForExistence(timeout: 5))
        let settings = app.buttons["printer.detail.control.settings"]
        XCTAssertTrue(settings.waitForExistence(timeout: 5))
        settings.tap()
        XCTAssertTrue(app.navigationBars["Settings"].waitForExistence(timeout: 5))
        let toggle = app.switches["settings.advancedPrinterControls"]
        if !toggle.isHittable { app.swipeUp() }
        XCTAssertTrue(toggle.waitForExistence(timeout: 5))
        XCTAssertEqual(toggle.value as? String, "0", "Discovering Controls must never enable the safety preference")
    }

    // MARK: - Selector reachability once Controls is available

    func testUnsettledControlsContextCannotExposeMaterialActuationAndKeepsEmergencyIndependent() {
        enableAdvancedPrinterControls()
        openFirstPrinterDetail()
        app.segmentedControls["printer.detail.panel.selector"].buttons["Control"].tap()
        XCTAssertTrue(app.staticTexts[
            "Controls require a settled registered server connection. Reopen this printer after reconnecting."
        ].waitForExistence(timeout: 8))
        XCTAssertFalse(app.buttons["printer.controls.extrude"].exists,
                       "Demo targets and spool assignment cannot establish physical-control access")
        for operation in ["load", "unload", "change"] {
            let action = app.buttons["printer.controls.filament-\(operation)"]
            XCTAssertFalse(action.exists, "An unsettled control composition must not expose physical actuation")
        }
        XCTAssertFalse(app.buttons["printer.controls.calibration-start"].exists)
        let emergency = app.buttons["printer.detail.control.emergencyStop"]
        XCTAssertTrue(emergency.isHittable)
        XCTAssertTrue(emergency.isEnabled)
        XCTAssertGreaterThanOrEqual(emergency.frame.height, 44)
        emergency.tap()
        XCTAssertTrue(app.alerts.firstMatch.waitForExistence(timeout: 3))
        app.alerts.firstMatch.buttons["Cancel"].tap()
        let evidence = XCTAttachment(screenshot: XCUIScreen.main.screenshot())
        evidence.name = "Unsettled material context blocked; independent confirmed emergency"
        evidence.lifetime = .keepAlways
        add(evidence)
    }

    func testSelectorTapSwitchesToControlsPageAndBackToStatus() {
        enableAdvancedPrinterControls()
        openFirstPrinterDetail()

        let selector = app.segmentedControls["printer.detail.panel.selector"]
        XCTAssertTrue(
            selector.waitForExistence(timeout: 8),
            "Panel selector must appear once Advanced Printer Controls is enabled for an online printer"
        )

        let controlsSegment = selector.buttons["Control"]
        XCTAssertTrue(
            controlsSegment.waitForExistence(timeout: 3),
            "Selector must expose a Control segment once Advanced Printer Controls is enabled"
        )
        controlsSegment.tap()

        XCTAssertTrue(
            app.descendants(matching: .any)["printer.detail.panel.control"]
                .waitForExistence(timeout: 8),
            "Tapping the Control segment must reveal the Controls page"
        )

        let statusSegment = selector.buttons["Status"]
        XCTAssertTrue(
            statusSegment.waitForExistence(timeout: 3),
            "Selector must expose a Status segment"
        )
        statusSegment.tap()

        XCTAssertTrue(
            app.descendants(matching: .any)["printer.detail.panel.status"]
                .waitForExistence(timeout: 8),
            "Tapping the Status segment must return to the Status page"
        )
    }

    func testRunActionBarStaysReachableAcrossPanelSwitch() {
        enableAdvancedPrinterControls()
        openFirstPrinterDetail()

        let selector = app.segmentedControls["printer.detail.panel.selector"]
        XCTAssertTrue(
            selector.waitForExistence(timeout: 8),
            "Panel selector must appear once Advanced Printer Controls is enabled for an online printer"
        )

        // Emergency Stop is the one run action guaranteed to be visible for
        // any online printer regardless of print state (issue #2520/#2522).
        // Queried by its own stable identifier (Hicks review finding 22):
        // `PrinterRunActionBar`'s container previously combined
        // `.accessibilityIdentifier(...)` with `.accessibilityElement
        // (children: .contain)` in the wrong order, which made every
        // descendant button report the CONTAINER's identifier instead of
        // its own; fixed at the source by reordering those two modifiers
        // (`.contain` first, then the container's own identifier) so child
        // identifiers are reachable again.
        XCTAssertFalse(app.buttons["printer.detail.control.emergencyStop"].exists)

        let controlsSegment = selector.buttons["Control"]
        XCTAssertTrue(
            controlsSegment.waitForExistence(timeout: 3),
            "Selector must expose a Control segment once Advanced Printer Controls is enabled"
        )
        controlsSegment.tap()

        XCTAssertTrue(
            app.buttons["printer.detail.control.emergencyStop"].waitForExistence(timeout: 8),
            "Emergency Stop remains in the same separate top position on Controls"
        )
        let emergency = app.buttons["printer.detail.control.emergencyStop"]
        XCTAssertGreaterThanOrEqual(emergency.frame.height, 44)
        XCTAssertGreaterThanOrEqual(emergency.frame.width, 44)
        XCTAssertLessThan(emergency.frame.maxY, selector.frame.minY)
        app.descendants(matching: .any)["printer.detail.panel.control"].swipeUp()
        XCTAssertTrue(emergency.isHittable, "Emergency Stop must stay pinned while Control scrolls")
        app.buttons["printer.detail.control.emergencyStop"].tap()
        XCTAssertTrue(app.alerts.firstMatch.waitForExistence(timeout: 3))
        app.alerts.firstMatch.buttons["Cancel"].tap()
    }

    // MARK: - Native horizontal swipe (Hicks review finding 11)

    func testSwipingTraversesAllFourPagesWithoutStatusMotionBadges() {
        openFirstPrinterDetail()
        let selector = app.segmentedControls["printer.detail.panel.selector"]
        XCTAssertTrue(selector.waitForExistence(timeout: 8))
        for (current, next) in [("status", "Control"), ("control", "Filament"), ("filament", "Queue")] {
            let page = app.descendants(matching: .any)["printer.detail.panel.\(current)"]
            XCTAssertTrue(page.waitForExistence(timeout: 5))
            if current == "status" {
                XCTAssertFalse(app.staticTexts["Homed axes"].exists)
                XCTAssertFalse(app.staticTexts["Not homed"].exists)
            }
            page.swipeLeft()
            XCTAssertTrue(app.descendants(matching: .any)["printer.detail.panel.\(next.lowercased())"]
                .waitForExistence(timeout: 5))
            XCTAssertTrue(selector.buttons[next].isSelected)
        }
        for (current, next) in [("queue", "Filament"), ("filament", "Control"), ("control", "Status")] {
            app.descendants(matching: .any)["printer.detail.panel.\(current)"].swipeRight()
            XCTAssertTrue(app.descendants(matching: .any)["printer.detail.panel.\(next.lowercased())"]
                .waitForExistence(timeout: 5))
            XCTAssertTrue(selector.buttons[next].isSelected)
        }
    }

    func testSwipeLeftToControlsPageSyncsSelectorAndExcludesStatusFromAccessibility() {
        enableAdvancedPrinterControls()
        openFirstPrinterDetail()

        let selector = app.segmentedControls["printer.detail.panel.selector"]
        XCTAssertTrue(
            selector.waitForExistence(timeout: 8),
            "Panel selector must appear once Advanced Printer Controls is enabled for an online printer"
        )

        let overviewPage = app.descendants(matching: .any)["printer.detail.panel.status"]
        XCTAssertTrue(overviewPage.waitForExistence(timeout: 8), "Must start on the Status page")

        // A native horizontal swipe — not a selector tap — must move the
        // pager exactly like tapping the Control segment does.
        overviewPage.swipeLeft()

        XCTAssertTrue(
            app.descendants(matching: .any)["printer.detail.panel.control"]
                .waitForExistence(timeout: 8),
            "Swiping left over the Status page must reveal the Controls page"
        )
        XCTAssertTrue(
            selector.buttons["Control"].isSelected,
            "The selector must sync to Controls after a native swipe, not just after a segment tap"
        )
        XCTAssertFalse(
            app.descendants(matching: .any)["printer.detail.panel.status"].exists,
            "The inactive Status page must be excluded from the accessibility tree (accessibilityHidden), not merely scrolled off"
        )
    }

    func testSwipeRightBackToStatusPageSyncsSelectorAndExcludesControlsFromAccessibility() {
        enableAdvancedPrinterControls()
        openFirstPrinterDetail()

        let selector = app.segmentedControls["printer.detail.panel.selector"]
        XCTAssertTrue(
            selector.waitForExistence(timeout: 8),
            "Panel selector must appear once Advanced Printer Controls is enabled for an online printer"
        )

        // Reach Controls first via the selector (already covered by
        // testSelectorTapSwitchesToControlsPageAndBackToStatus), then swipe
        // back natively so this test isolates the swipe-back behavior.
        let controlsSegment = selector.buttons["Control"]
        XCTAssertTrue(controlsSegment.waitForExistence(timeout: 3))
        controlsSegment.tap()

        let controlsPage = app.descendants(matching: .any)["printer.detail.panel.control"]
        XCTAssertTrue(controlsPage.waitForExistence(timeout: 8), "Must reach the Controls page before swiping back")

        controlsPage.swipeRight()

        XCTAssertTrue(
            app.descendants(matching: .any)["printer.detail.panel.status"]
                .waitForExistence(timeout: 8),
            "Swiping right over the Controls page must return to the Status page"
        )
        XCTAssertTrue(
            selector.buttons["Status"].isSelected,
            "The selector must sync back to Status after a native swipe, not just after a segment tap"
        )
        XCTAssertFalse(
            app.descendants(matching: .any)["printer.detail.panel.control"].exists,
            "The inactive Controls page must be excluded from the accessibility tree (accessibilityHidden) once swiped away from"
        )
    }
}
