import XCTest
import UIKit

/// XCUI acceptance for the Overview/Controls paging integration (issue #2522).
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
/// the Overview page, the panel selector, the Controls page, and Emergency
/// Stop is likewise a deterministic `XCTAssertTrue`/`XCTAssertFalse`.
@MainActor
final class PrinterDetailPanelsUITests: PrintFarmerUITestCase {

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
        let attention = shellDestinationButton(tabIdentifier: "tab.attention", timeout: 5)
        XCTAssertTrue(
            attention.exists,
            "The Attention/Account destination must be reachable in the deterministic UI-test bootstrap"
        )
        attention.tap()

        // Matches `OperatorShellUITests.openAccount()`: tapping the Attention
        // tab reveals the Account entry point, which must itself be tapped
        // to reach `account.root` before any `account.destination.*` button
        // exists. Hard-asserting `enableAdvancedPrinterControls` (Hicks
        // review finding 18) is what surfaced this step was missing here —
        // the prior soft-skip silently returned instead of ever reaching
        // Settings, letting every caller "pass" without exercising Controls.
        let account = app.buttons["navigation.account"]
        XCTAssertTrue(
            account.waitForExistence(timeout: 5),
            "The Account entry point must be reachable from Attention in the deterministic UI-test bootstrap"
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
        if !toggle.waitForExistence(timeout: 3) {
            app.swipeUp()
        }
        XCTAssertTrue(
            toggle.waitForExistence(timeout: 3),
            "The Advanced Printer Controls safety toggle must be discoverable in Settings"
        )
        if toggle.value as? String != "1" {
            // A plain `toggle.tap()` targets the center of the accessibility
            // element's frame, which for a SwiftUI `Toggle` row spans the
            // full row width (label text + switch combined into one
            // accessibility element for VoiceOver). The center of that frame
            // sits over the LABEL text, not the switch knob UIKit actually
            // hit-tests against, so a center tap can silently land on inert
            // text instead of flipping the switch. Target the right edge,
            // where the switch control itself renders, instead.
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

    func testOverviewUsesAvailableWidthAcrossRotation() {
        openFirstPrinterDetail()
        defer { XCUIDevice.shared.orientation = .portrait }
        for orientation in [UIDeviceOrientation.portrait, .landscapeLeft] {
            XCUIDevice.shared.orientation = orientation
            let overview = app.descendants(matching: .any)["printer.detail.panel.overview"]
            XCTAssertTrue(overview.waitForExistence(timeout: 8))
            let expectedLayout = overview.frame.width >= 760
                ? "printer.detail.columns" : "printer.detail.readingColumn"
            XCTAssertTrue(app.otherElements[expectedLayout].waitForExistence(timeout: 5))
            let temperatures = app.otherElements["printer.detail.temperatures"]
            XCTAssertTrue(temperatures.waitForExistence(timeout: 5))
            XCTAssertLessThan(temperatures.frame.minY, app.otherElements["printer.detail.job"].frame.minY)
            XCTAssertTrue(app.buttons["printer.detail.control.emergencyStop"].isHittable)
            let screenshot = XCTAttachment(screenshot: XCUIScreen.main.screenshot())
            screenshot.name = "Essential Overview \(orientation == .portrait ? "portrait" : "landscape")"
            screenshot.lifetime = .keepAlways
            add(screenshot)
        }
    }

    func testAccessibilityTextKeepsReadingColumnAndLabeledEmergencyOnBothPages() {
        app.terminate()
        app.launchArguments += [
            "-UIPreferredContentSizeCategoryName", "UICTContentSizeCategoryAccessibilityXXXL",
            "-pf_theme_mode", "dark"
        ]
        app.launch()
        openFirstPrinterDetail()
        XCTAssertTrue(app.otherElements["printer.detail.readingColumn"].waitForExistence(timeout: 8))
        let selector = app.segmentedControls["printer.detail.panel.selector"]
        for title in ["Overview", "Controls"] {
            selector.buttons[title].tap()
            let emergency = app.buttons["printer.detail.control.emergencyStop"]
            XCTAssertTrue(emergency.isHittable)
            XCTAssertGreaterThanOrEqual(emergency.frame.height, 44)
            XCTAssertGreaterThanOrEqual(emergency.frame.width, 44)
            XCTAssertEqual(emergency.label, "Emergency stop printer")
            let screenshot = XCTAttachment(screenshot: XCUIScreen.main.screenshot())
            screenshot.name = "Essential \(title) accessibility text"
            screenshot.lifetime = .keepAlways
            add(screenshot)
        }
    }

    func testDefaultEntryLandsOnOverviewPage() {
        openFirstPrinterDetail()

        let overviewPage = app.descendants(matching: .any)["printer.detail.panel.overview"]
        XCTAssertTrue(
            overviewPage.waitForExistence(timeout: 8),
            "Printer detail must default to the Overview page"
        )
    }

    func testControlsRemainDiscoverableWithExplanationAndExistingSettingsPathWhileDisabled() {
        openFirstPrinterDetail()

        XCTAssertTrue(
            app.descendants(matching: .any)["printer.detail.panel.overview"]
                .waitForExistence(timeout: 8),
            "Printer detail must render the Overview page"
        )
        XCTAssertTrue(
            app.segmentedControls["printer.detail.panel.selector"].exists,
            "Both destinations remain discoverable while the safety preference is off"
        )
        app.segmentedControls["printer.detail.panel.selector"].buttons["Controls"].tap()
        XCTAssertTrue(app.otherElements["printer.detail.controls.unavailable"].waitForExistence(timeout: 5))
        let settings = app.buttons["printer.detail.controls.settings"]
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
        app.segmentedControls["printer.detail.panel.selector"].buttons["Controls"].tap()
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

    func testSelectorTapSwitchesToControlsPageAndBackToOverview() {
        enableAdvancedPrinterControls()
        openFirstPrinterDetail()

        let selector = app.segmentedControls["printer.detail.panel.selector"]
        XCTAssertTrue(
            selector.waitForExistence(timeout: 8),
            "Panel selector must appear once Advanced Printer Controls is enabled for an online printer"
        )

        let controlsSegment = selector.buttons["Controls"]
        XCTAssertTrue(
            controlsSegment.waitForExistence(timeout: 3),
            "Selector must expose a Controls segment once Advanced Printer Controls is enabled"
        )
        controlsSegment.tap()

        XCTAssertTrue(
            app.descendants(matching: .any)["printer.detail.panel.controls"]
                .waitForExistence(timeout: 8),
            "Tapping the Controls segment must reveal the Controls page"
        )

        let statusSegment = selector.buttons["Overview"]
        XCTAssertTrue(
            statusSegment.waitForExistence(timeout: 3),
            "Selector must expose a Overview segment"
        )
        statusSegment.tap()

        XCTAssertTrue(
            app.descendants(matching: .any)["printer.detail.panel.overview"]
                .waitForExistence(timeout: 8),
            "Tapping the Overview segment must return to the Overview page"
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
        let emergencyStopOnStatus = app.buttons["printer.detail.control.emergencyStop"]
        XCTAssertTrue(
            emergencyStopOnStatus.waitForExistence(timeout: 8),
            "Emergency Stop must be reachable on the Overview page for an online printer"
        )
        let initialFrame = emergencyStopOnStatus.frame
        XCTAssertGreaterThanOrEqual(initialFrame.height, 44)
        XCTAssertGreaterThanOrEqual(initialFrame.width, 44)
        XCTAssertLessThan(initialFrame.maxY, selector.frame.minY)
        emergencyStopOnStatus.tap()
        XCTAssertTrue(app.alerts.firstMatch.waitForExistence(timeout: 3))
        app.alerts.firstMatch.buttons["Cancel"].tap()

        let controlsSegment = selector.buttons["Controls"]
        XCTAssertTrue(
            controlsSegment.waitForExistence(timeout: 3),
            "Selector must expose a Controls segment once Advanced Printer Controls is enabled"
        )
        controlsSegment.tap()

        XCTAssertTrue(
            app.buttons["printer.detail.control.emergencyStop"].waitForExistence(timeout: 8),
            "Emergency Stop remains in the same separate top position on Controls"
        )
        XCTAssertEqual(app.buttons["printer.detail.control.emergencyStop"].frame.minY, initialFrame.minY, accuracy: 1)
        app.buttons["printer.detail.control.emergencyStop"].tap()
        XCTAssertTrue(app.alerts.firstMatch.waitForExistence(timeout: 3))
        app.alerts.firstMatch.buttons["Cancel"].tap()
    }

    // MARK: - Native horizontal swipe (Hicks review finding 11)

    func testSwipeLeftToControlsPageSyncsSelectorAndExcludesOverviewFromAccessibility() {
        enableAdvancedPrinterControls()
        openFirstPrinterDetail()

        let selector = app.segmentedControls["printer.detail.panel.selector"]
        XCTAssertTrue(
            selector.waitForExistence(timeout: 8),
            "Panel selector must appear once Advanced Printer Controls is enabled for an online printer"
        )

        let overviewPage = app.descendants(matching: .any)["printer.detail.panel.overview"]
        XCTAssertTrue(overviewPage.waitForExistence(timeout: 8), "Must start on the Overview page")

        // A native horizontal swipe — not a selector tap — must move the
        // pager exactly like tapping the Controls segment does.
        overviewPage.swipeLeft()

        XCTAssertTrue(
            app.descendants(matching: .any)["printer.detail.panel.controls"]
                .waitForExistence(timeout: 8),
            "Swiping left over the Overview page must reveal the Controls page"
        )
        XCTAssertTrue(
            selector.buttons["Controls"].isSelected,
            "The selector must sync to Controls after a native swipe, not just after a segment tap"
        )
        XCTAssertFalse(
            app.descendants(matching: .any)["printer.detail.panel.overview"].exists,
            "The inactive Overview page must be excluded from the accessibility tree (accessibilityHidden), not merely scrolled off"
        )
    }

    func testSwipeRightBackToOverviewPageSyncsSelectorAndExcludesControlsFromAccessibility() {
        enableAdvancedPrinterControls()
        openFirstPrinterDetail()

        let selector = app.segmentedControls["printer.detail.panel.selector"]
        XCTAssertTrue(
            selector.waitForExistence(timeout: 8),
            "Panel selector must appear once Advanced Printer Controls is enabled for an online printer"
        )

        // Reach Controls first via the selector (already covered by
        // testSelectorTapSwitchesToControlsPageAndBackToOverview), then swipe
        // back natively so this test isolates the swipe-back behavior.
        let controlsSegment = selector.buttons["Controls"]
        XCTAssertTrue(controlsSegment.waitForExistence(timeout: 3))
        controlsSegment.tap()

        let controlsPage = app.descendants(matching: .any)["printer.detail.panel.controls"]
        XCTAssertTrue(controlsPage.waitForExistence(timeout: 8), "Must reach the Controls page before swiping back")

        controlsPage.swipeRight()

        XCTAssertTrue(
            app.descendants(matching: .any)["printer.detail.panel.overview"]
                .waitForExistence(timeout: 8),
            "Swiping right over the Controls page must return to the Overview page"
        )
        XCTAssertTrue(
            selector.buttons["Overview"].isSelected,
            "The selector must sync back to Overview after a native swipe, not just after a segment tap"
        )
        XCTAssertFalse(
            app.descendants(matching: .any)["printer.detail.panel.controls"].exists,
            "The inactive Controls page must be excluded from the accessibility tree (accessibilityHidden) once swiped away from"
        )
    }
}
