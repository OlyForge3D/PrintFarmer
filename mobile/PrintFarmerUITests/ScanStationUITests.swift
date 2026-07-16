import XCTest

/// UI tests for the F9 scan station (issue #714).
///
/// Verifies the Scan tab renders its type-dispatch entry point, quick
/// actions, and NFC hint with deterministic accessibility identifiers, and
/// that the "Log New Spool" quick action reaches the existing barcode
/// intake flow. The demo `ServiceContainer` runs with no camera/barcode
/// scanner (`barcodeScannerService == nil`), so the primary scan control is
/// expected to render disabled in this environment — that disabled state
/// itself is asserted as deterministic, real rendered behavior rather than
/// worked around.
final class ScanStationUITests: PrintFarmerUITestCase {

    private func openScanTab() {
        let scanTab = app.tabBars.buttons["Scan"]
        XCTAssertTrue(scanTab.waitForExistence(timeout: 5), "Scan tab should exist in the operator shell")
        scanTab.tap()
    }

    func testScanTabRendersPrimaryScanControl() {
        openScanTab()

        let scanButton = app.buttons["scan.primary"]
        XCTAssertTrue(scanButton.waitForExistence(timeout: 5),
                      "Primary scan control should be reachable from the Scan tab")
        XCTAssertEqual(scanButton.label, "Scan")
        // No barcode scanner is wired in the demo/UI-test environment, so
        // the control must render disabled rather than silently no-op.
        XCTAssertFalse(scanButton.isEnabled,
                       "Scan control should be disabled when no scanner is available")
    }

    func testScanTabRendersNFCHint() {
        openScanTab()

        let nfcHint = app.staticTexts["scan.nfc.hint"]
        XCTAssertTrue(nfcHint.waitForExistence(timeout: 5),
                      "NFC hint should explain that printer tags are handled automatically")
    }

    func testLogNewSpoolQuickActionOpensBarcodeIntake() {
        openScanTab()

        let quickAction = app.buttons["scan.quickAction.spool"]
        XCTAssertTrue(quickAction.waitForExistence(timeout: 5),
                      "Log New Spool quick action should be reachable from the Scan tab")
        quickAction.tap()

        let intakeTitle = app.navigationBars["Barcode Intake"]
        XCTAssertTrue(intakeTitle.waitForExistence(timeout: 5),
                      "Log New Spool should present the existing Barcode Intake flow")
    }

    func testMaintenancePartQuickActionIsNotPresent() {
        // #714's mobile scope explicitly excludes maintenance/replacement
        // parts (Dallas's adjudication assigns that domain to #721/#722).
        openScanTab()
        XCTAssertTrue(app.buttons["scan.primary"].waitForExistence(timeout: 5))
        XCTAssertFalse(app.staticTexts["Log Maintenance Part"].exists,
                       "Maintenance-part quick action is out of #714's mobile scope and must not appear")
    }
}
