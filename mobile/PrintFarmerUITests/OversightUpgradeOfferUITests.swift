import XCTest

@MainActor
final class OversightUpgradeOfferUITests: PrintFarmerUITestCase {
    override var additionalLaunchArguments: [String] {
        ["--uitesting-oversight-upgrade-offer"]
    }

    func testOfferAppearsInlineAndCanBeDismissed() throws {
        try requireCompactOversight()

        let turnOn = app.buttons["oversight.upgradeOffer.turnOn"]
        XCTAssertTrue(turnOn.waitForExistence(timeout: 8))

        let notNow = app.buttons["oversight.upgradeOffer.notNow"]
        XCTAssertTrue(notNow.exists)
        notNow.tap()
        XCTAssertTrue(turnOn.waitForNonExistence(timeout: 2))
    }

    func testTurningOnOfferSwitchesToTwoModes() throws {
        try requireCompactOversight()

        let turnOn = app.buttons["oversight.upgradeOffer.turnOn"]
        XCTAssertTrue(turnOn.waitForExistence(timeout: 8))
        turnOn.tap()

        XCTAssertTrue(app.segmentedControls["navigation.modeControl"].waitForExistence(timeout: 5))
    }

    private func requireCompactOversight() throws {
        let oversight = app.tabBars.buttons["Oversight"]
        guard oversight.waitForExistence(timeout: 8) else {
            throw XCTSkip(
                "Inline Oversight upgrade offer is available only in the compact simple shell."
            )
        }
        oversight.tap()
    }
}
