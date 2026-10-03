import XCTest

@MainActor
final class PartsInventoryUITests: PrintFarmerUITestCase {
    override var waitsForNavigationReadiness: Bool { true }

    func testFilamentHasSpoolsWithoutTheRetiredInventorySegments() {
        let filament = shellDestinationButton(tabIdentifier: "tab.filament", timeout: 8)
        XCTAssertTrue(filament.exists)
        filament.tap()
        XCTAssertTrue(app.navigationBars["Filament"].waitForExistence(timeout: 8))
        XCTAssertTrue(app.buttons["inventory.addSpool"].exists)
        XCTAssertFalse(app.segmentedControls["inventory.segmentPicker"].exists)
    }
}
