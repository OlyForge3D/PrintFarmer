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

/// Regression coverage for #3550: the Farm root title must share the leading
/// inset of the Queue and Filament root titles instead of bleeding off-screen.
@MainActor
class RootTitleAlignmentUITests: PrintFarmerUITestCase {
    override var waitsForNavigationReadiness: Bool { true }

    /// Whether the Farm chip row is expected to overflow a compact window.
    var expectsFarmChipOverflow: Bool { false }

    /// Element that proves which Farm root mounted (live list vs fallback shell).
    var farmRootIdentifier: String { "farm.filter.All" }

    func testFarmTitleSharesQueueAndFilamentLeadingInset() {
        let window = app.windows.firstMatch.frame
        let farm = titleFrame(tab: "tab.farm", title: "Farm")
        let farmRoot = app.descendants(matching: .any)[farmRootIdentifier]
        XCTAssertTrue(farmRoot.waitForExistence(timeout: 8), "Farm must mount \(farmRootIdentifier)")
        if farmRootIdentifier == "farm.filter.All" {
            verifyFarmChipRow(window: window, title: farm)
        }
        let queue = titleFrame(tab: "tab.queue", title: "Queue")
        let filament = titleFrame(tab: "tab.filament", title: "Filament")
        let titles = [("Farm", farm), ("Queue", queue), ("Filament", filament)]

        for (name, title) in titles {
            XCTAssertEqual(title.inNavigationBar, farm.inNavigationBar, "\(name) must use the same title display as Farm")
            XCTAssertGreaterThanOrEqual(
                title.frame.minX - window.minX, 8,
                "\(name) title must keep a standard leading margin; frame=\(title.frame) window=\(window)"
            )
            XCTAssertLessThanOrEqual(
                title.frame.maxX, window.maxX,
                "\(name) title must not be clipped; frame=\(title.frame) window=\(window)"
            )
        }
        // Compact draws a leading-aligned header; regular width uses the
        // system inline navigation bar title, which is centered.
        let anchor: (CGRect) -> CGFloat = farm.inNavigationBar ? { $0.midX } : { $0.minX }
        XCTAssertEqual(anchor(farm.frame), anchor(queue.frame), accuracy: 1, "Farm=\(farm.frame) Queue=\(queue.frame)")
        XCTAssertEqual(anchor(farm.frame), anchor(filament.frame), accuracy: 1, "Farm=\(farm.frame) Filament=\(filament.frame)")
        XCTAssertEqual(farm.frame.minY, queue.frame.minY, accuracy: 1, "Farm and Queue titles share a display mode")
        XCTAssertEqual(farm.frame.minY, filament.frame.minY, accuracy: 1, "Farm and Filament titles share a display mode")
    }

    private func verifyFarmChipRow(window: CGRect, title: (frame: CGRect, inNavigationBar: Bool)) {
        let filters = app.descendants(matching: .any)["farm.filters"]
        let first = app.buttons["farm.filter.All"]
        let last = app.buttons["farm.filter.Idle"]
        XCTAssertTrue(first.waitForExistence(timeout: 5) && last.exists, "Farm filter chips must render")

        if expectsFarmChipOverflow && !title.inNavigationBar {
            XCTAssertGreaterThan(
                last.frame.maxX, window.maxX,
                "Fixture must overflow the chip row; last=\(last.frame) window=\(window)"
            )
            // Scroll affordance: the row bleeds to the screen edges (XCUI frames
            // ignore clipping, so assert the clipping row itself), letting the
            // first off-screen chip peek in instead of ending at the inset.
            XCTAssertEqual(filters.frame.minX, window.minX, accuracy: 0.5,
                           "Chip row must bleed to the leading edge; row=\(filters.frame)")
            XCTAssertEqual(filters.frame.maxX, window.maxX, accuracy: 0.5,
                           "Chip row must bleed to the trailing edge; row=\(filters.frame)")
            XCTAssertLessThan(
                last.frame.minX, window.maxX,
                "Next chip must peek in at the trailing edge; last=\(last.frame) window=\(window)"
            )
        }
        if !title.inNavigationBar {
            XCTAssertEqual(first.frame.minX, title.frame.minX, accuracy: 1,
                           "Resting chips share the title inset; first=\(first.frame) title=\(title.frame)")
        }
        // The farm.filters container is only exposed to XCUI in compact width.
        let bounded = title.inNavigationBar ? [first, last] : [filters, app.staticTexts["navigation.title"]]
        for element in bounded {
            XCTAssertGreaterThanOrEqual(element.frame.minX, window.minX - 0.5, "\(element.frame) left of \(window)")
            XCTAssertLessThanOrEqual(element.frame.maxX, window.maxX + 0.5, "\(element.frame) right of \(window)")
        }

        if expectsFarmChipOverflow && !title.inNavigationBar {
            filters.swipeLeft()
            let scrolled = XCTAttachment(screenshot: app.screenshot())
            scrolled.name = "root-title-farm-chips-scrolled"
            scrolled.lifetime = .keepAlways
            add(scrolled)
            XCTAssertLessThan(first.frame.minX, window.minX + 16,
                              "Scrolled chips must pass under the inset to the screen edge; first=\(first.frame)")
            XCTAssertLessThanOrEqual(filters.frame.maxX, window.maxX + 0.5, "Scrolling must not widen the root")
            filters.swipeRight()
        }
    }

    private func titleFrame(tab: String, title: String) -> (frame: CGRect, inNavigationBar: Bool) {
        let destination = shellDestinationButton(tabIdentifier: tab, timeout: 8)
        XCTAssertTrue(destination.exists, "\(tab) must be reachable")
        destination.tap()

        let labelled = NSPredicate(format: "label == %@", title)
        let compactTitle = app.staticTexts.matching(identifier: "navigation.title")
            .matching(labelled).firstMatch
        let barTitle = app.navigationBars.staticTexts.matching(labelled).firstMatch
        // Poll both shapes so neither size class pays the other's timeout.
        var found: (XCUIElement, Bool)?
        let deadline = Date().addingTimeInterval(8)
        while found == nil && Date() < deadline {
            if compactTitle.exists {
                found = (compactTitle, false)
            } else if barTitle.exists {
                found = (barTitle, true)
            } else {
                RunLoop.current.run(until: Date().addingTimeInterval(0.2))
            }
        }
        guard let (element, inNavigationBar) = found else {
            XCTFail("\(title) title must render")
            return (.zero, false)
        }

        let shot = XCTAttachment(screenshot: app.screenshot())
        shot.name = "root-title-\(title.lowercased())"
        shot.lifetime = .keepAlways
        add(shot)
        return (element.frame, inNavigationBar)
    }
}

/// Larger (non-accessibility) text widens the Farm filter chips past the
/// compact width, which previously widened the whole root and shifted the title.
@MainActor
final class RootTitleAlignmentLargeTypeUITests: RootTitleAlignmentUITests {
    override var additionalLaunchArguments: [String] {
        ["-UIPreferredContentSizeCategoryName", "UICTContentSizeCategoryXXXL"]
    }

    override var expectsFarmChipOverflow: Bool { true }
}

/// The cold-offline launch mounts the DashboardView fallback NavigationStack,
/// whose Farm title must match Queue and Filament as well.
@MainActor
final class RootTitleAlignmentColdOfflineUITests: RootTitleAlignmentUITests {
    override var additionalLaunchArguments: [String] {
        ["--uitesting-cold-offline-shell"]
    }

    override var farmRootIdentifier: String { "cold-offline-shell" }
}
