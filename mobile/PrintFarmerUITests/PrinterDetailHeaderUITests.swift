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
        // Poll both shapes so neither size class pays the other's timeout.
        var isCompact: Bool?
        let deadline = Date().addingTimeInterval(8)
        while isCompact == nil && Date() < deadline {
            if compactTitle.exists {
                isCompact = true
            } else if rootBar.exists {
                isCompact = false
            } else {
                RunLoop.current.run(until: Date().addingTimeInterval(0.2))
            }
        }
        guard let isCompact else {
            XCTFail("The Farm root must show its compact header or navigation bar")
            return
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

/// DO NOT MERGE — disposable N3552-01 (#3561) isolation diagnostic.
/// Same fixture and flags as `Issue3259ControlIdleUITests` (visual acceptance,
/// control idle, dark, L), reaching the same idle card two ways: by scrolling
/// (no search) and by the existing Farm search filter. Each method records its
/// screens and geometry before asserting, so either path's failure cannot
/// suppress the other path's evidence.
@MainActor
final class Issue3561HeaderBandComparisonUITests: PrintFarmerUITestCase {
    private let printerID = "10000000-0001-0000-0000-000000000001"
    private let printerName = "Prusa MK4 #1"
    private let contentSizeCategory = "UICTContentSizeCategoryL"

    override var waitsForNavigationReadiness: Bool { true }
    override var additionalLaunchArguments: [String] {
        [
            "--uitesting-issue3259-visual-acceptance",
            "-pf_theme_mode", "dark",
            "-UIPreferredContentSizeCategoryName", contentSizeCategory,
            "--uitesting-issue3259-control-idle",
        ]
    }

    func testControlPathWithoutSearchHasNoBandAboveFarmBack() {
        guard let rootAnchor = openFarmRoot(path: "no-search") else { return }
        let card = app.buttons["farm-card-\(printerID)"]
        for _ in 0..<16 where tapPoint(for: card) == nil {
            farmScroll.swipeUp()
        }
        recordAndAssertDetail(path: "no-search", rootAnchor: rootAnchor, card: card)
    }

    func testSearchFilterPathHasNoBandAboveFarmBack() {
        guard let rootAnchor = openFarmRoot(path: "search") else { return }
        let card = app.buttons["farm-card-\(printerID)"]
        // Mirrors Issue3259MockupCaptureUITests.searchFarm from an empty query.
        let searchButton = app.buttons["farm.search"]
        XCTAssertTrue(searchButton.waitForExistence(timeout: 8))
        searchButton.tap()
        let field = app.searchFields.firstMatch
        XCTAssertTrue(field.waitForExistence(timeout: 8))
        field.tap()
        field.typeText(printerName + "\n")
        _ = card.waitForExistence(timeout: 8)
        recordAndAssertDetail(path: "search", rootAnchor: rootAnchor, card: card)
    }

    private var farmScroll: XCUIElement {
        app.scrollViews
            .matching(NSPredicate(format: "identifier IN %@", ["farm.printerList", "farm.root"]))
            .firstMatch
    }

    /// Opens the unsearched Farm root and returns its compact header top (52pt, title centered).
    private func openFarmRoot(path: String) -> CGFloat? {
        attachProvenance(path: path)
        let farm = shellDestinationButton(tabIdentifier: "tab.farm", timeout: 8)
        XCTAssertTrue(farm.waitForExistence(timeout: 8))
        farm.tap()
        XCTAssertTrue(farmScroll.waitForExistence(timeout: 8))
        let title = app.staticTexts["navigation.title"]
        guard title.waitForExistence(timeout: 8) else {
            attachScreen(path, "farm-root-no-compact-header")
            XCTFail("The iPhone Farm root must show its compact header")
            return nil
        }
        attachScreen(path, "farm-root-before-navigation")
        XCTAssertFalse(app.searchFields.firstMatch.exists, "The unsearched Farm root must not present search")
        return title.frame.midY - 26
    }

    /// Mirrors Issue3259MockupCaptureUITests.openFarmCard's top-edge tap point clear of the dock and keyboard.
    private func tapPoint(for card: XCUIElement) -> CGPoint? {
        guard card.exists, card.isHittable else { return nil }
        let frame = card.frame
        let point = CGPoint(x: frame.midX, y: frame.minY + min(44, frame.height / 2))
        var bottom = farmScroll.frame.maxY
        let scan = app.buttons["navigation.scan"]
        if scan.exists { bottom = min(bottom, scan.frame.minY - 8) }
        let keyboard = app.keyboards.firstMatch
        if keyboard.exists { bottom = min(bottom, keyboard.frame.minY - 8) }
        return farmScroll.frame.contains(point) && point.y < bottom ? point : nil
    }

    private func recordAndAssertDetail(path: String, rootAnchor: CGFloat, card: XCUIElement) {
        let searchAtTap = app.searchFields.firstMatch
        let searchAtTapDescription = searchAtTap.exists ? "present \(searchAtTap.frame)" : "absent"
        attachScreen(path, "farm-root-before-card-tap")
        guard let point = tapPoint(for: card) else {
            attachGeometry(path, ["rootAnchor=\(rootAnchor)", "searchFieldAtTap=\(searchAtTapDescription)", "card=unreachable"])
            XCTFail("The top of the \(printerName) card must be on screen before tapping")
            return
        }
        // Farm elements leave the hierarchy once detail is pushed; record them as scalars now.
        let cardFrameAtTap = card.frame
        app.coordinate(withNormalizedOffset: .zero)
            .withOffset(CGVector(dx: point.x, dy: point.y))
            .tap()

        let back = app.buttons["printer.detail.farm"]
        let identity = app.otherElements["printer.detail.identity"]
        let backExists = back.waitForExistence(timeout: 8)
        let identityExists = identity.waitForExistence(timeout: 8)
        attachScreen(path, "detail")
        let searchAtDetail = app.searchFields.firstMatch
        let navigationBars = app.navigationBars.allElementsBoundByIndex.map { "\($0.identifier)@\($0.frame)" }
        attachGeometry(path, [
            "window=\(app.windows.firstMatch.frame)",
            "rootAnchor=\(rootAnchor)",
            "cardFrameAtTap=\(cardFrameAtTap) tapPoint=\(point)",
            "searchFieldAtTap=\(searchAtTapDescription)",
            "searchFieldAtDetail=\(searchAtDetail.exists ? "present \(searchAtDetail.frame)" : "absent")",
            "navigationBarsAtDetail=\(navigationBars)",
            "back=\(backExists ? "\(back.frame) label=\(back.label)" : "missing")",
            "detailAnchor=\(backExists ? "\(back.frame.minY - 2)" : "missing")",
            "identity=\(identityExists ? "\(identity.frame)" : "missing")",
        ])

        XCTAssertTrue(backExists, "Printer detail must show its Farm back button")
        XCTAssertTrue(identityExists, "Printer detail must show the printer identity")
        guard backExists, identityExists else { return }
        XCTAssertEqual(back.label, "‹ Farm")
        // Absolute: the anchor was measured on the unsearched root, before any search state.
        XCTAssertEqual(
            back.frame.minY - 2, rootAnchor, accuracy: 4,
            "[\(path)] No blank band may sit above ‹ Farm: back=\(back.frame) rootAnchor=\(rootAnchor)"
        )
        XCTAssertLessThanOrEqual(back.frame.maxY, identity.frame.minY + 1, "The header must stay above the printer identity")
    }

    private func attachScreen(_ path: String, _ step: String) {
        let shot = XCTAttachment(screenshot: XCUIScreen.main.screenshot())
        shot.name = "Issue 3561 \(path) \(step)"
        shot.lifetime = .keepAlways
        add(shot)
    }

    private func attachGeometry(_ path: String, _ lines: [String]) {
        let attachment = XCTAttachment(string: lines.joined(separator: "\n"))
        attachment.name = "Issue 3561 \(path) geometry"
        attachment.lifetime = .keepAlways
        add(attachment)
    }

    private func attachProvenance(path: String) {
        let environment = ProcessInfo.processInfo.environment
        let screen = XCUIScreen.main.screenshot().image
        let lines = [
            "test=\(name)",
            "path=\(path)",
            "simulatorDevice=\(environment["SIMULATOR_DEVICE_NAME"] ?? "unknown")",
            "simulatorModel=\(environment["SIMULATOR_MODEL_IDENTIFIER"] ?? "unknown")",
            "simulatorRuntime=\(environment["SIMULATOR_RUNTIME_VERSION"] ?? "unknown")",
            "systemVersion=\(UIDevice.current.systemVersion)",
            "screenPoints=\(screen.size.width)x\(screen.size.height) scale=\(screen.scale)",
            "contentSizeCategory=\(contentSizeCategory)",
            "launchArguments=\(app.launchArguments.joined(separator: " "))",
        ]
        let attachment = XCTAttachment(string: lines.joined(separator: "\n"))
        attachment.name = "Issue 3561 \(path) provenance"
        attachment.lifetime = .keepAlways
        add(attachment)
    }
}
