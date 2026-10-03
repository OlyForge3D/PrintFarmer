import XCTest

@MainActor
final class OperatorShellUITests: PrintFarmerUITestCase {
    override var waitsForNavigationReadiness: Bool { true }

    func testAppLaunchesOnFarmWithExactlyThreeDestinations() {
        let farm = shellDestinationButton(tabIdentifier: "tab.farm", timeout: 8)
        XCTAssertTrue(farm.exists)
        XCTAssertTrue(farm.isSelected)
        for id in ["tab.farm", "tab.queue", "tab.filament"] {
            XCTAssertTrue(shellDestinationButton(tabIdentifier: id, timeout: 8).exists)
        }
        if app.tabBars.firstMatch.exists {
            XCTAssertEqual(app.tabBars.firstMatch.buttons.count, 3)
        }
        for retired in ["attention", "tasks", "inventory", "oversight", "overview", "fleet", "jobs", "upkeep", "reports"] {
            XCTAssertFalse(compactTabExists(tabIdentifier: "tab.\(retired)"))
            XCTAssertFalse(app.buttons["sidebar.\(retired)"].exists)
        }
        XCTAssertFalse(app.segmentedControls["navigation.modeControl"].exists)
    }

    func testScanAndAccountAreAvailableOnEveryRoot() {
        for id in ["tab.farm", "tab.queue", "tab.filament"] {
            let destination = shellDestinationButton(tabIdentifier: id, timeout: 8)
            XCTAssertTrue(destination.exists)
            destination.tap()
            let scan = app.buttons["navigation.scan"]
            XCTAssertTrue(scan.waitForExistence(timeout: 8))
            XCTAssertGreaterThanOrEqual(scan.frame.height, 44)
            XCTAssertTrue(app.buttons["navigation.account"].waitForExistence(timeout: 8))
            XCTAssertFalse(app.buttons["navigation.serverSwitcher"].exists)
        }
    }

    func testAvatarOpensSettingsAndServers() {
        let avatar = app.buttons["navigation.account"]
        XCTAssertTrue(avatar.waitForExistence(timeout: 8))
        avatar.tap()
        for id in ["settings", "manageServers", "notifications", "offlineQueue"] {
            XCTAssertTrue(app.buttons["account.destination.\(id)"].waitForExistence(timeout: 5))
        }
        app.buttons["account.destination.settings"].tap()
        XCTAssertTrue(app.navigationBars["Settings"].waitForExistence(timeout: 5))
        XCTAssertFalse(app.buttons["settings.navigation"].exists)
    }
}
