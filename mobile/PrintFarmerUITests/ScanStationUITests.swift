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
@MainActor
final class ScanStationUITests: PrintFarmerUITestCase {

    private let printerID = "10000000-0001-0000-0000-000000000001"

    private func openScanDestination() throws {
        let tabBar = app.tabBars.firstMatch
        if tabBar.waitForExistence(timeout: 5) {
            let scanTab = tabBar.buttons["Scan"]
            if scanTab.exists {
                scanTab.tap()
                return
            }
            throw XCTSkip(
                "Compact Scan re-homing is tracked by #2419; #2416 intentionally removes the legacy top-level tab"
            )
        }

        let sidebarScan = app.buttons["sidebar.scan"]
        if sidebarScan.waitForExistence(timeout: 3) {
            sidebarScan.tap()
            return
        }

        for label in ["Sidebar", "Toggle Sidebar", "Show Sidebar"] {
            let toggle = app.navigationBars.buttons[label]
            if toggle.exists {
                toggle.tap()
                if sidebarScan.waitForExistence(timeout: 3) {
                    sidebarScan.tap()
                    return
                }
            }
        }

        XCTFail("Scan destination should remain reachable from the regular-width sidebar")
    }

    func testScanTabRendersPrimaryScanControl() throws {
        try openScanDestination()

        let scanButton = app.buttons["scan.primary"]
        XCTAssertTrue(scanButton.waitForExistence(timeout: 5),
                      "Primary scan control should be reachable from the Scan tab")
        XCTAssertEqual(scanButton.label, "Scan")
        // No barcode scanner is wired in the demo/UI-test environment, so
        // the control must render disabled rather than silently no-op.
        XCTAssertFalse(scanButton.isEnabled,
                       "Scan control should be disabled when no scanner is available")
    }

    func testScanTabRendersNFCHint() throws {
        try openScanDestination()

        let nfcHint = app.staticTexts["scan.nfc.hint"]
        XCTAssertTrue(nfcHint.waitForExistence(timeout: 5),
                      "NFC hint should explain that printer tags are handled automatically")
    }

    func testLogNewSpoolQuickActionOpensBarcodeIntake() throws {
        try openScanDestination()

        let quickAction = app.buttons["scan.quickAction.spool"]
        XCTAssertTrue(quickAction.waitForExistence(timeout: 5),
                      "Log New Spool quick action should be reachable from the Scan tab")
        quickAction.tap()

        let intakeTitle = app.navigationBars["Barcode Intake"]
        XCTAssertTrue(intakeTitle.waitForExistence(timeout: 5),
                      "Log New Spool should present the existing Barcode Intake flow")
    }

    func testMaintenancePartQuickActionIsNotPresent() throws {
        // #714's mobile scope explicitly excludes maintenance/replacement
        // parts (Dallas's adjudication assigns that domain to #721/#722).
        try openScanDestination()
        XCTAssertTrue(app.buttons["scan.primary"].waitForExistence(timeout: 5))
        XCTAssertFalse(app.staticTexts["Log Maintenance Part"].exists,
                       "Maintenance-part quick action is out of #714's mobile scope and must not appear")
    }

    /// H6 (remediation): "Log Printed Parts" must be a real quick action
    /// that opens a lookup list of printed-part SKUs and forwards a
    /// selection into the existing part-adjustment flow.
    func testLogPrintedPartsQuickActionOpensPartLookup() throws {
        try openScanDestination()

        let quickAction = app.buttons["scan.quickAction.parts"]
        XCTAssertTrue(quickAction.waitForExistence(timeout: 5),
                      "Log Printed Parts quick action should be reachable from the Scan tab")
        quickAction.tap()

        let lookupTitle = app.navigationBars["Printed Parts"]
        XCTAssertTrue(lookupTitle.waitForExistence(timeout: 5),
                      "Log Printed Parts should present the printed-parts lookup list")
    }

    /// H6 (remediation): "Printer Lookup" must be a real quick action that
    /// opens a lookup list of registered printers and, on selection,
    /// navigates to that printer's detail view. Uses the seeded demo
    /// printer "Prusa MK4 #1" for a deterministic assertion.
    func testPrinterLookupQuickActionOpensPrinterLookupAndNavigatesToDetail() throws {
        try openScanDestination()

        let quickAction = app.buttons["scan.quickAction.printerLookup"]
        XCTAssertTrue(quickAction.waitForExistence(timeout: 5),
                      "Printer Lookup quick action should be reachable from the Scan tab")
        quickAction.tap()

        let lookupTitle = app.navigationBars["Printer Lookup"]
        XCTAssertTrue(lookupTitle.waitForExistence(timeout: 5),
                      "Printer Lookup should present the printer lookup list")

        let printerRow = app.buttons["scan.printerLookup.row.\(printerID)"]
        XCTAssertTrue(printerRow.waitForExistence(timeout: 5),
                      "Seeded demo printer should render in the Printer Lookup list")
        XCTAssertTrue(printerRow.label.contains("Prusa MK4 #1"))
        printerRow.tap()

        // Wait for the dismiss animation to settle rather than asserting
        // absence synchronously — on slower/loaded CI simulators the
        // accessibility tree can still report the dismissing sheet's nav
        // bar as present for a moment after the tap.
        XCTAssertTrue(app.navigationBars["Printer Lookup"].waitForNonExistence(timeout: 5),
                      "Selecting a printer should dismiss the lookup list")
        let destination = app.staticTexts["printer.detail.destination.\(printerID)"]
        XCTAssertTrue(destination.waitForExistence(timeout: 8),
                      "Selecting a printer should navigate to that printer's detail view")
        XCTAssertEqual(destination.label, "Prusa MK4 #1, printer detail")
    }
}
