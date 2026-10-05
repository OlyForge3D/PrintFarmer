import XCTest

@MainActor
final class PrinterCardFailureUITests: PrintFarmerUITestCase {
    override var waitsForNavigationReadiness: Bool { true }
    override var additionalLaunchArguments: [String] {
        ["--uitesting-attention-actions"]
    }

    func testFailureAndAttentionArePartOfSingleCardLabel() {
        let farm = shellDestinationButton(tabIdentifier: "tab.farm", timeout: 8)
        XCTAssertTrue(farm.exists)
        farm.tap()
        let card = app.buttons["farm-card-10000000-0001-0000-0000-000000000003"]
        XCTAssertTrue(card.waitForExistence(timeout: 8))
        let combined = NSPredicate(format: "label CONTAINS %@ AND label CONTAINS %@",
                                   "Failure suspected:", "1 attention items")
        expectation(for: combined, evaluatedWith: card)
        waitForExpectations(timeout: 8)
        XCTAssertTrue(card.label.contains("Failure suspected"))
        XCTAssertTrue(card.label.contains("The card remains usable when camera data cannot be decoded."))
        XCTAssertEqual(card.descendants(matching: .staticText).count, 0)
    }
}

/// UI tests for deterministic Farm list rendering and stable-ID printer navigation.
@MainActor
final class PrinterListUITests: PrintFarmerUITestCase {
    override var waitsForNavigationReadiness: Bool { true }
    private let printerID = "10000000-0001-0000-0000-000000000001"

    private func openFarm() {
        let farm = shellDestinationButton(
            tabIdentifier: "tab.farm",
            timeout: 8
        )
        XCTAssertTrue(farm.exists)
        farm.tap()
    }

    func testPrinterListDisplayed() {
        openFarm()
        XCTAssertTrue(
            app.staticTexts["navigation.title"].waitForExistence(timeout: 5)
                || app.navigationBars["Farm"].waitForExistence(timeout: 5)
        )

        let printerCard = app.buttons["farm-card-\(printerID)"]
        XCTAssertTrue(
            printerCard.waitForExistence(timeout: 5),
            "The deterministic UI-test fleet should expose its first printer card"
        )
        XCTAssertTrue(printerCard.label.contains("Prusa MK4 #1"))
        XCTAssertTrue(printerCard.label.contains("complete"))
        XCTAssertTrue(printerCard.label.contains("Nozzle"))
        XCTAssertTrue(printerCard.label.contains("Bed"))
        XCTAssertFalse(printerCard.label.lowercased().contains("homed"))
        XCTAssertFalse(printerCard.label.lowercased().contains("coverage"))
        XCTAssertEqual(printerCard.descendants(matching: .staticText).count, 0,
                       "The entire farm card is one VoiceOver element")
    }

    func testTapPrinterNavigatesToDetail() {
        openFarm()
        let printerCard = app.buttons["farm-card-\(printerID)"]
        XCTAssertTrue(printerCard.waitForExistence(timeout: 5))
        printerCard.tap()

        // Query broadly rather than `.scrollViews[...]`: issue #2522
        // restructured the detail root from a single `ScrollView` into the
        // Status/Controls paged host, which can change which concrete
        // element type this identifier bubbles up to.
        let detail = app.descendants(matching: .any)["printer.detail.root.\(printerID)"]
        XCTAssertTrue(
            detail.waitForExistence(timeout: 8),
            "Tapping the stable printer card should open that printer's detail"
        )

        let destination = app.staticTexts["printer.detail.destination.\(printerID)"]
        // Wait rather than a synchronous exists check — on iPad the
        // NavigationSplitView detail column can take longer to fully mount
        // this nested destination text than the parent ScrollView container.
        XCTAssertTrue(destination.waitForExistence(timeout: 5))
        XCTAssertEqual(destination.label, "Prusa MK4 #1, printer detail")
    }

    func testSearchFieldExists() {
        openFarm()
        let searchButton = app.buttons["farm.search"]
        XCTAssertTrue(
            searchButton.waitForExistence(timeout: 5),
            "Farm should expose search from the toolbar"
        )
        XCTAssertFalse(
            app.searchFields.firstMatch.exists,
            "Farm search should stay collapsed until requested"
        )
        searchButton.tap()

        let searchField = app.searchFields.firstMatch
        XCTAssertTrue(
            searchField.waitForExistence(timeout: 5),
            "Farm should expose printer search when the deterministic fleet is loaded"
        )
        searchField.tap()
        searchField.typeText("Prusa")
        XCTAssertTrue(app.buttons["farm-card-\(printerID)"].exists)
    }
}
