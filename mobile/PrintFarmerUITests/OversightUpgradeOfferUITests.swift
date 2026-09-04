import XCTest

@MainActor
final class OversightUpgradeOfferUITests: PrintFarmerUITestCase {
    override var additionalLaunchArguments: [String] {
        ["--uitesting-oversight-upgrade-offer"]
    }

    func testOfferAppearsInlineAndCanBeDismissed() {
        let oversight = operatorDestinationButton(
            tabTitle: "Oversight",
            sidebarIdentifier: "sidebar.oversight"
        )
        XCTAssertTrue(oversight.waitForExistence(timeout: 5))
        oversight.tap()

        let turnOn = app.buttons["oversight.upgradeOffer.turnOn"]
        XCTAssertTrue(turnOn.waitForExistence(timeout: 8))

        let notNow = app.buttons["oversight.upgradeOffer.notNow"]
        XCTAssertTrue(notNow.exists)
        notNow.tap()
        XCTAssertFalse(turnOn.waitForExistence(timeout: 2))
    }

    func testTurningOnOfferSwitchesToTwoModes() {
        let oversight = operatorDestinationButton(
            tabTitle: "Oversight",
            sidebarIdentifier: "sidebar.oversight"
        )
        XCTAssertTrue(oversight.waitForExistence(timeout: 5))
        oversight.tap()

        let turnOn = app.buttons["oversight.upgradeOffer.turnOn"]
        XCTAssertTrue(turnOn.waitForExistence(timeout: 8))
        turnOn.tap()

        XCTAssertTrue(app.segmentedControls["navigation.modeControl"].waitForExistence(timeout: 5))
    }
}
