import XCTest

/// N3552-01 (#3561): pushing printer detail must not leave a blank band above
/// its "‹ Farm" header. The detail header replaces the root header in place, so
/// both sit directly below the top safe area on iPhone (compact header) and
/// at the root navigation bar's position on iPad.
@MainActor
final class PrinterDetailHeaderUITests: PrintFarmerUITestCase {
    override var waitsForNavigationReadiness: Bool { true }
    override var additionalLaunchArguments: [String] {
        ["-UIPreferredContentSizeCategoryName", "UICTContentSizeCategoryL"]
    }

    func testIdleDetailHeaderStartsWhereRootHeaderStarts() {
        let farm = shellDestinationButton(tabIdentifier: "tab.farm", timeout: 8)
        XCTAssertTrue(farm.exists, "The Farm destination must be reachable")
        farm.tap()

        let compactTitle = app.staticTexts["navigation.title"]
        let rootBar = app.navigationBars["Farm"]
        let isCompact = compactTitle.waitForExistence(timeout: 8)
        if !isCompact {
            XCTAssertTrue(rootBar.waitForExistence(timeout: 8), "The regular-width Farm root must show its bar")
        }
        attach("root")
        // Compact header: 52pt, title centered. Regular: the root nav bar's top.
        let rootAnchor = isCompact ? compactTitle.frame.midY - 26 : rootBar.frame.minY

        let card = app.buttons
            .matching(NSPredicate(format: "identifier BEGINSWITH %@", "farm-card-"))
            .firstMatch
        XCTAssertTrue(card.waitForExistence(timeout: 8), "The deterministic fleet must expose a printer card")
        card.tap()

        let back = app.buttons["printer.detail.farm"]
        XCTAssertTrue(back.waitForExistence(timeout: 8))
        XCTAssertEqual(back.label, "‹ Farm")
        let identity = app.otherElements["printer.detail.identity"]
        XCTAssertTrue(identity.waitForExistence(timeout: 8))
        attach("detail")

        // Detail header: 48pt with the 44pt back button centered (top + 2).
        let detailAnchor = back.frame.minY - 2
        XCTAssertEqual(
            detailAnchor, rootAnchor, accuracy: 4,
            "No blank band may sit above ‹ Farm: back=\(back.frame) rootAnchor=\(rootAnchor)"
        )
        XCTAssertLessThanOrEqual(back.frame.maxY, identity.frame.minY + 1, "The header must stay above the printer identity")
    }

    private func attach(_ name: String) {
        let shot = XCTAttachment(screenshot: app.screenshot())
        shot.name = "Issue 3561 \(UIDevice.current.userInterfaceIdiom == .pad ? "iPad" : "iPhone") \(name)"
        shot.lifetime = .keepAlways
        add(shot)
    }
}
