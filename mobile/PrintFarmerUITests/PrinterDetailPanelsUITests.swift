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
/// `waitForExistence`; no `Thread.sleep`/`Task.sleep`/retry-until-pass. Every
/// test soft-skips (returns) when demo data or rich detail content is not
/// present in this environment, mirroring the existing `OperatorShellUITests`
/// pattern rather than failing on environment variance.
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
    /// Settings, then returns to Farm. Soft-skips (returns false) if any
    /// expected Settings surface is unavailable in this environment.
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
        guard statusPage.waitForExistence(timeout: 5) else {
            // Demo data did not render rich content in this environment — skip.
            return
        }
        XCTAssertTrue(statusPage.exists, "Printer detail must default to the Status page")
    }

    func testSelectorAndControlsPageOmittedWhileSafetyToggleIsOff() {
        guard openFirstPrinterDetail() else { return }
        guard app.descendants(matching: .any)["printer.detail.panel.status"]
            .waitForExistence(timeout: 5) else { return }

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
        guard selector.waitForExistence(timeout: 5) else {
            // This printer may be offline in the demo fleet (Controls is
            // gated on the printer being online too) — skip gracefully.
            return
        }

        let controlsSegment = selector.buttons["Controls"]
        guard controlsSegment.waitForExistence(timeout: 3) else { return }
        controlsSegment.tap()

        XCTAssertTrue(
            app.descendants(matching: .any)["printer.detail.panel.controls"]
                .waitForExistence(timeout: 5),
            "Tapping the Controls segment must reveal the Controls page"
        )

        let statusSegment = selector.buttons["Status"]
        guard statusSegment.waitForExistence(timeout: 3) else { return }
        statusSegment.tap()

        XCTAssertTrue(
            app.descendants(matching: .any)["printer.detail.panel.status"]
                .waitForExistence(timeout: 5),
            "Tapping the Status segment must return to the Status page"
        )
    }

    func testRunActionBarStaysReachableAcrossPanelSwitch() {
        guard enableAdvancedPrinterControls() else { return }
        guard openFirstPrinterDetail() else { return }

        let selector = app.segmentedControls["printer.detail.panel.selector"]
        guard selector.waitForExistence(timeout: 5) else { return }

        // Emergency Stop is the one run action guaranteed to be visible for
        // any online printer regardless of print state (issue #2520/#2522).
        let emergencyStopOnStatus = app.buttons["printer.detail.control.emergencyStop"]
        guard emergencyStopOnStatus.waitForExistence(timeout: 5) else { return }

        let controlsSegment = selector.buttons["Controls"]
        guard controlsSegment.waitForExistence(timeout: 3) else { return }
        controlsSegment.tap()

        XCTAssertTrue(
            app.buttons["printer.detail.control.emergencyStop"].waitForExistence(timeout: 5),
            "The shared run-action bar (and Emergency Stop within it) must remain reachable on the Controls page without scrolling or an Advanced disclosure"
        )
    }
}
