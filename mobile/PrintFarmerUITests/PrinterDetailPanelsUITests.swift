import XCTest

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

    func testDefaultEntryLandsOnStatusPage() {
        openFirstPrinterDetail()

        let statusPage = app.descendants(matching: .any)["printer.detail.panel.status"]
        XCTAssertTrue(
            statusPage.waitForExistence(timeout: 8),
            "Printer detail must default to the Status page"
        )
    }

    func testSelectorAndControlsPageOmittedWhileSafetyToggleIsOff() {
        openFirstPrinterDetail()

        XCTAssertTrue(
            app.descendants(matching: .any)["printer.detail.panel.status"]
                .waitForExistence(timeout: 8),
            "Printer detail must render the Status page"
        )
        XCTAssertFalse(
            app.segmentedControls["printer.detail.panel.selector"].exists,
            "Panel selector must not appear while the per-server safety toggle is off"
        )
        XCTAssertFalse(
            app.descendants(matching: .any)["printer.detail.panel.controls"].exists,
            "Controls page must not appear while the per-server safety toggle is off"
        )
    }

    // MARK: - Selector reachability once Controls is available

    func testSelectorTapSwitchesToControlsPageAndBackToStatus() {
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
        //
        // Queried by its `PrinterRunActionLabels.accessibilityLabel(for:)`
        // text, not its `PrinterRunActionLabels.accessibilityIdentifier(for:)`
        // identifier: `PrinterRunActionBar`'s own root `VStack` combines
        // `.accessibilityElement(children: .contain)` with its own
        // `containerAccessibilityIdentifier`, and in this iOS/Xcode
        // toolchain that container identifier is what XCUITest reports for
        // EVERY descendant button — each button's own, more specific
        // `.accessibilityIdentifier(...)` is unreachable via an identifier
        // query. `PrinterRunActionBar` is a merged #2520 component outside
        // this issue's edit scope (`mobile/PrintFarmer/Views/Printers/
        // PrinterDetailView.swift` alone), and its labels remain distinct
        // and correct for real VoiceOver users regardless of this
        // identifier-query limitation, so this test adapts its query
        // strategy rather than modifying that file.
        func emergencyStopButton() -> XCUIElement {
            app.buttons.matching(
                NSPredicate(format: "label == %@", "Emergency stop printer")
            ).firstMatch
        }

        XCTAssertTrue(
            emergencyStopButton().waitForExistence(timeout: 8),
            "Emergency Stop must be reachable on the Status page for an online printer"
        )

        let controlsSegment = selector.buttons["Controls"]
        XCTAssertTrue(
            controlsSegment.waitForExistence(timeout: 3),
            "Selector must expose a Controls segment once Advanced Printer Controls is enabled"
        )
        controlsSegment.tap()

        XCTAssertTrue(
            emergencyStopButton().waitForExistence(timeout: 8),
            "The shared run-action bar (and Emergency Stop within it) must remain reachable on the Controls page without scrolling or an Advanced disclosure"
        )
    }

    // MARK: - Native horizontal swipe (Hicks review finding 11)

    func testSwipeLeftToControlsPageSyncsSelectorAndExcludesStatusFromAccessibility() {
        enableAdvancedPrinterControls()
        openFirstPrinterDetail()

        let selector = app.segmentedControls["printer.detail.panel.selector"]
        XCTAssertTrue(
            selector.waitForExistence(timeout: 8),
            "Panel selector must appear once Advanced Printer Controls is enabled for an online printer"
        )

        let statusPage = app.descendants(matching: .any)["printer.detail.panel.status"]
        XCTAssertTrue(statusPage.waitForExistence(timeout: 8), "Must start on the Status page")

        // A native horizontal swipe — not a selector tap — must move the
        // pager exactly like tapping the Controls segment does.
        statusPage.swipeLeft()

        XCTAssertTrue(
            app.descendants(matching: .any)["printer.detail.panel.controls"]
                .waitForExistence(timeout: 8),
            "Swiping left over the Status page must reveal the Controls page"
        )
        XCTAssertTrue(
            selector.buttons["Controls"].isSelected,
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
        let controlsSegment = selector.buttons["Controls"]
        XCTAssertTrue(controlsSegment.waitForExistence(timeout: 3))
        controlsSegment.tap()

        let controlsPage = app.descendants(matching: .any)["printer.detail.panel.controls"]
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
            app.descendants(matching: .any)["printer.detail.panel.controls"].exists,
            "The inactive Controls page must be excluded from the accessibility tree (accessibilityHidden) once swiped away from"
        )
    }
}
