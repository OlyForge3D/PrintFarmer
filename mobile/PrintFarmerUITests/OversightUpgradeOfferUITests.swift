import XCTest

@MainActor
final class OversightUpgradeOfferUITests: PrintFarmerUITestCase {
    override var additionalLaunchArguments: [String] {
        ["--uitesting-oversight-upgrade-offer"]
    }

    func testOfferAppearsInlineAndCanBeDismissed() {
        requireCompactOversight()

        let turnOn = app.buttons["oversight.upgradeOffer.turnOn"]
        XCTAssertTrue(turnOn.waitForExistence(timeout: 8))

        let notNow = app.buttons["oversight.upgradeOffer.notNow"]
        XCTAssertTrue(notNow.exists)
        notNow.tap()
        XCTAssertTrue(turnOn.waitForNonExistence(timeout: 2))
    }

    func testTurningOnOfferSwitchesToTwoModes() {
        requireCompactOversight()

        let turnOn = app.buttons["oversight.upgradeOffer.turnOn"]
        XCTAssertTrue(turnOn.waitForExistence(timeout: 8))
        turnOn.tap()

        XCTAssertTrue(app.segmentedControls["navigation.modeControl"].waitForExistence(timeout: 5))
    }

    private func requireCompactOversight() {
        let oversight = app.tabBars.buttons["Oversight"]
        XCTAssertTrue(oversight.waitForExistence(timeout: 8))
        oversight.tap()
    }
}
