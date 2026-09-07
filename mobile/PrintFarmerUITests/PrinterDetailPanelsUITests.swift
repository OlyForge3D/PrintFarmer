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
/// `waitForExistence`; no `Thread.sleep`/`Task.sleep`/retry-until-pass. Only
/// the initial navigation-entry helpers (`openFirstPrinterDetail`,
/// `enableAdvancedPrinterControls`) soft-skip (return) when the shell/demo
/// fleet/Settings surface they depend on is not present at all — mirroring
/// the existing `OperatorShellUITests` convention for environment variance
/// unrelated to this feature. Once a test has actually reached printer
/// detail, every assertion about the Status page, the panel selector, the
/// Controls page, and Emergency Stop is a deterministic `XCTAssertTrue`/
/// `XCTAssertFalse` — a bare `return` there would let a real regression
/// (the page/selector/action never appearing) silently pass.
@MainActor
final class PrinterDetailPanelsUITests: PrintFarmerUITestCase {

    // MARK: - Navigation helpers

    private func openFirstPrinterDetail() -> Bool {
        let farm = shellDestinationButton(tabIdentifier: "tab.farm", timeout: 5)
        guard farm.exists else { return false }
        farm.tap()

        let farmCard = app.buttons
            .matching(NSPredicate(format: "identifier BEGINSWITH %@", "farm-card-"))
            .firstMatch
        if farmCard.waitForExistence(timeout: 5) {
            farmCard.tap()
            return true
        }
        let firstPrinter = app.collectionViews.cells.firstMatch
        guard firstPrinter.waitForExistence(timeout: 5) else { return false }
        firstPrinter.tap()
        return true
    }

    /// Enables the per-server "Advanced Printer Controls" safety toggle from
    /// Settings, then returns to Farm. Soft-skips (returns false) only if the
    /// Settings navigation itself is unavailable in this environment — not a
    /// concern of the panels feature under test.
    @discardableResult
    private func enableAdvancedPrinterControls() -> Bool {
        let attention = shellDestinationButton(tabIdentifier: "tab.attention", timeout: 5)
        guard attention.exists else { return false }
        attention.tap()

        let settingsDestination = app.buttons["account.destination.settings"]
        guard settingsDestination.waitForExistence(timeout: 3) else { return false }
        settingsDestination.tap()

        guard app.navigationBars["Settings"].waitForExistence(timeout: 5) else { return false }
        let toggle = app.switches["settings.advancedPrinterControls"]
        if !toggle.waitForExistence(timeout: 3) {
            app.swipeUp()
        }
        guard toggle.waitForExistence(timeout: 3) else { return false }
        if toggle.value as? String != "1" {
            toggle.tap()
        }
        return true
    }

    // MARK: - Default entry / gating

    func testDefaultEntryLandsOnStatusPage() {
        guard openFirstPrinterDetail() else { return }

        let statusPage = app.descendants(matching: .any)["printer.detail.panel.status"]
        XCTAssertTrue(
            statusPage.waitForExistence(timeout: 8),
            "Printer detail must default to the Status page"
        )
    }

    func testSelectorAndControlsPageOmittedWhileSafetyToggleIsOff() {
        guard openFirstPrinterDetail() else { return }

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
        guard enableAdvancedPrinterControls() else { return }
        guard openFirstPrinterDetail() else { return }

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
        guard enableAdvancedPrinterControls() else { return }
        guard openFirstPrinterDetail() else { return }

        let selector = app.segmentedControls["printer.detail.panel.selector"]
        XCTAssertTrue(
            selector.waitForExistence(timeout: 8),
            "Panel selector must appear once Advanced Printer Controls is enabled for an online printer"
        )

        // Emergency Stop is the one run action guaranteed to be visible for
        // any online printer regardless of print state (issue #2520/#2522).
        let emergencyStopOnStatus = app.buttons["printer.detail.control.emergencyStop"]
        XCTAssertTrue(
            emergencyStopOnStatus.waitForExistence(timeout: 8),
            "Emergency Stop must be reachable on the Status page for an online printer"
        )

        let controlsSegment = selector.buttons["Controls"]
        XCTAssertTrue(
            controlsSegment.waitForExistence(timeout: 3),
            "Selector must expose a Controls segment once Advanced Printer Controls is enabled"
        )
        controlsSegment.tap()

        XCTAssertTrue(
            app.buttons["printer.detail.control.emergencyStop"].waitForExistence(timeout: 8),
            "The shared run-action bar (and Emergency Stop within it) must remain reachable on the Controls page without scrolling or an Advanced disclosure"
        )
    }

    // MARK: - Native horizontal swipe (Hicks review finding 11)

    func testSwipeLeftToControlsPageSyncsSelectorAndExcludesStatusFromAccessibility() {
        guard enableAdvancedPrinterControls() else { return }
        guard openFirstPrinterDetail() else { return }

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
        guard enableAdvancedPrinterControls() else { return }
        guard openFirstPrinterDetail() else { return }

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
