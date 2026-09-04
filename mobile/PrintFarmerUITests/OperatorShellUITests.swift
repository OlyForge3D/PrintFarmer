import XCTest

/// UI tests for the F1 operator shell (issue #706).
///
/// Verifies that the app launches into the Attention tab, that the operator
/// shell exposes its stable destinations, and that moved screens remain
/// reachable from Oversight and Account:
///
/// * Oversight → Dashboard
/// * Oversight → Maintenance
/// * Account → Notifications / Settings / Manage Servers / Offline Queue
/// * Farm → printer → Advanced (advanced cockpit is intentionally deeper)
///
/// These tests are best-effort: without a configured mock server the
/// details tab targets may not present rich content. Assertions fall
/// back to "at least the navigation chrome exists" when data is missing,
/// mirroring `PrinterListUITests`.
@MainActor
final class OperatorShellUITests: PrintFarmerUITestCase {

    // MARK: - Shell shape (tab bar on iPhone, sidebar on iPad)

    func testAppLaunchesOnAttentionTab() {
        let attention = operatorDestinationButton(
            tabTitle: "Attention",
            sidebarIdentifier: "sidebar.attention",
            timeout: 5
        )
        XCTAssertTrue(
            attention.exists,
            "Attention destination should be present in the operator shell — tab bar 'Attention' on iPhone or 'sidebar.attention' on iPad"
        )
        XCTAssertTrue(
            attention.isSelected,
            "Attention should be the initial selected destination"
        )
    }

    func testTabBarShowsFourOperatorDestinations() {
        // Operator shell must present a navigation container: iPhone
        // TabView bottom bar OR iPad NavigationSplitView sidebar.
        let tabBar = app.tabBars.firstMatch
        let sidebarAttention = app.buttons["sidebar.attention"]
        let hasTabBar = tabBar.waitForExistence(timeout: 3)
        let hasSidebar = hasTabBar ? false : sidebarAttention.waitForExistence(timeout: 3)
        if !hasTabBar && !hasSidebar {
            revealSidebarIfCollapsed()
        }
        XCTAssertTrue(
            hasTabBar || sidebarAttention.exists,
            "Operator shell must expose either a compact tab bar or an iPad sidebar"
        )

        let expectedDestinations: [(tabTitle: String, sidebarIdentifier: String)] = [
            ("Attention", "sidebar.attention"),
            ("Farm", "sidebar.farm"),
            ("Inventory", "sidebar.inventory"),
            ("Oversight", "sidebar.oversight")
        ]
        for destination in expectedDestinations {
            let button = operatorDestinationButton(
                tabTitle: destination.tabTitle,
                sidebarIdentifier: destination.sidebarIdentifier,
                timeout: 2
            )
            XCTAssertTrue(
                button.exists,
                "Destination '\(destination.tabTitle)' should be present in the operator shell — tab bar '\(destination.tabTitle)' or sidebar '\(destination.sidebarIdentifier)'"
            )
        }

        if hasTabBar {
            XCTAssertFalse(app.tabBars.buttons["Tasks"].exists)
            XCTAssertFalse(app.tabBars.buttons["Scan"].exists)
        }
    }

    func testRetiredTabsAreNotVisible() {
        // The old shell exposed dedicated Notifications, Settings, and Scan tabs.
        // Their flows now live under Account, Farm, and Inventory.
        for retired in ["Notifications", "Settings", "Scan"] {
            let tab = app.tabBars.buttons[retired]
            XCTAssertFalse(tab.exists,
                           "Retired top-level tab '\(retired)' must not appear in F1 shell")
        }
        XCTAssertFalse(app.buttons["sidebar.scan"].exists)
    }

    // MARK: - Re-homed destination reachability

    func testAttentionOverflowIsRemoved() {
        let attention = operatorDestinationButton(
            tabTitle: "Attention",
            sidebarIdentifier: "sidebar.attention",
            timeout: 5
        )
        XCTAssertTrue(attention.exists)
        attention.tap()

        XCTAssertFalse(
            app.buttons["attention.overflow"].exists,
            "Attention must not expose the retired overflow menu"
        )
    }

    func testDashboardReachableFromOversight() {
        openOversightDestination(
            identifier: "oversight.destination.dashboard",
            expectedNavigationBarTitle: "Dashboard"
        )
    }

    func testMaintenanceReachableFromOversight() {
        openOversightDestination(
            identifier: "oversight.destination.maintenance",
            expectedNavigationBarTitle: "Maintenance"
        )
    }

    func testPredictiveInsightsReachableFromOversight() {
        openOversightDestination(
            identifier: "oversight.destination.predictiveInsights",
            expectedNavigationBarTitle: "Predictive Insights"
        )
    }

    func testPredictiveInsightsReachableFromPrinterDetail() {
        guard app.tabBars.buttons["Farm"].waitForExistence(timeout: 5) else { return }
        app.tabBars.buttons["Farm"].tap()

        let farmCard = app.buttons
            .matching(NSPredicate(format: "identifier BEGINSWITH %@", "farm-card-"))
            .firstMatch
        let firstPrinter = farmCard.waitForExistence(timeout: 5)
            ? farmCard
            : app.collectionViews.cells.firstMatch
        guard firstPrinter.waitForExistence(timeout: 5) else {
            // Demo data may not have rendered yet; skip gracefully.
            return
        }
        firstPrinter.tap()

        let predictiveInsights = app.buttons["printer.detail.predictive"]
        if !predictiveInsights.waitForExistence(timeout: 3) {
            app.swipeUp()
        }
        guard predictiveInsights.waitForExistence(timeout: 3) else { return }
        predictiveInsights.tap()

        XCTAssertTrue(
            app.navigationBars["Predictive Insights"].waitForExistence(timeout: 5),
            "Predictive Insights must be reachable from printer detail"
        )
    }

    func testCanonicalAccountDestinationsAreReachable() {
        openAccount()

        for identifier in [
            "account.destination.notifications",
            "account.destination.settings",
            "account.destination.manageServers",
            "account.destination.offlineQueue"
        ] {
            XCTAssertTrue(
                app.buttons[identifier].waitForExistence(timeout: 3),
                "Account destination '\(identifier)' must be present"
            )
        }
    }

    func testSettingsReachableFromAccount() {
        openAccount()

        let settings = app.buttons["account.destination.settings"]
        XCTAssertTrue(settings.waitForExistence(timeout: 3))
        settings.tap()

        XCTAssertTrue(app.navigationBars["Settings"].waitForExistence(timeout: 5))
        let advancedControlsToggle = app.switches["settings.advancedPrinterControls"]
        if !advancedControlsToggle.waitForExistence(timeout: 3) {
            app.swipeUp()
        }
        XCTAssertTrue(
            advancedControlsToggle.waitForExistence(timeout: 3),
            "The Advanced Printer Controls safety toggle must be discoverable in Settings"
        )
        XCTAssertEqual(
            advancedControlsToggle.value as? String,
            "0",
            "Advanced Printer Controls must default off for the active server"
        )
    }

    // MARK: - Advanced controls gating (Farm → printer → Advanced)

    func testAdvancedControlsGatedBehindPrinterDetail() {
        guard app.tabBars.buttons["Farm"].waitForExistence(timeout: 5) else { return }
        app.tabBars.buttons["Farm"].tap()

        // Attempting to reach Advanced controls before entering a printer
        // must not surface them; the button lives only inside a printer's
        // detail view.
        XCTAssertFalse(app.buttons["printer.detail.advanced"].exists,
                       "Advanced controls entry must not appear on the Farm tab root")

        let farmCard = app.buttons
            .matching(NSPredicate(format: "identifier BEGINSWITH %@", "farm-card-"))
            .firstMatch
        let firstPrinter = farmCard.waitForExistence(timeout: 5)
            ? farmCard
            : app.collectionViews.cells.firstMatch
        guard firstPrinter.waitForExistence(timeout: 5) else {
            // Demo data may not have rendered yet; skip gracefully.
            return
        }
        firstPrinter.tap()

        let disclosure = app.buttons["printer.detail.advanced.disclosure"]
        if !disclosure.waitForExistence(timeout: 3) {
            app.swipeUp()
        }
        guard disclosure.waitForExistence(timeout: 3) else { return }
        disclosure.tap()

        XCTAssertFalse(
            app.buttons["printer.detail.advanced"].exists,
            "Advanced controls entry must be omitted while the per-server safety toggle is off"
        )
    }

    private func openAccount(
        file: StaticString = #filePath,
        line: UInt = #line
    ) {
        let attention = operatorDestinationButton(
            tabTitle: "Attention",
            sidebarIdentifier: "sidebar.attention",
            timeout: 5
        )
        XCTAssertTrue(attention.exists, file: file, line: line)
        attention.tap()

        let account = app.buttons["navigation.account"]
        XCTAssertTrue(account.waitForExistence(timeout: 5), file: file, line: line)
        account.tap()
        XCTAssertTrue(
            app.descendants(matching: .any)["account.root"].waitForExistence(timeout: 5),
            file: file,
            line: line
        )
    }

    private func openOversightDestination(
        identifier: String,
        expectedNavigationBarTitle: String,
        file: StaticString = #filePath,
        line: UInt = #line
    ) {
        let oversight = operatorDestinationButton(
            tabTitle: "Oversight",
            sidebarIdentifier: "sidebar.oversight",
            timeout: 5
        )
        XCTAssertTrue(oversight.exists, file: file, line: line)
        oversight.tap()

        let destination = app.buttons[identifier]
        XCTAssertTrue(destination.waitForExistence(timeout: 5), file: file, line: line)
        destination.tap()
        XCTAssertTrue(
            app.navigationBars[expectedNavigationBarTitle].waitForExistence(timeout: 5),
            file: file,
            line: line
        )
    }
}

// MARK: - #2117 capability-driven visibility
@MainActor
final class OperatorFeatureVisibilityUITests: PrintFarmerUITestCase {

    override var additionalLaunchArguments: [String] {
        // Contract with UITestBootstrap.operatorFeaturesDisabledLaunchArgument.
        // UI test targets cannot import the app target, so this literal
        // is verified by the corresponding UITestBootstrap unit test.
        ["--uitesting-operator-features-disabled"]
    }

    func testDisabledDestinationsAreAbsentAndFarmIsSelected() {
        let farm = operatorDestinationButton(
            tabTitle: "Farm",
            sidebarIdentifier: "sidebar.farm",
            timeout: 8
        )
        XCTAssertTrue(farm.exists)
        XCTAssertTrue(farm.isSelected)

        revealSidebarIfCollapsed()
        XCTAssertFalse(app.tabBars.buttons["Attention"].exists)
        XCTAssertFalse(app.buttons["sidebar.attention"].exists)
        XCTAssertFalse(app.tabBars.buttons["Tasks"].exists)
        XCTAssertFalse(app.buttons["sidebar.tasks"].exists)
        XCTAssertFalse(app.buttons["attention.fallback.notifications"].exists)
    }

    func testPrintedPartsAreAbsentWhileSpoolInventoryRemainsVisible() {
        let inventory = operatorDestinationButton(
            tabTitle: "Inventory",
            sidebarIdentifier: "sidebar.inventory",
            timeout: 8
        )
        XCTAssertTrue(inventory.exists)
        inventory.tap()

        XCTAssertTrue(app.navigationBars["Spool Inventory"].waitForExistence(timeout: 8))
        XCTAssertFalse(app.segmentedControls["inventory.segmentPicker"].exists)
        XCTAssertFalse(app.buttons["Printed Parts"].exists)
    }

    func testPrintedPartsScanActionIsAbsentWhileSpoolActionRemainsVisible() {
        let inventory = operatorDestinationButton(
            tabTitle: "Inventory",
            sidebarIdentifier: "sidebar.inventory",
            timeout: 8
        )
        XCTAssertTrue(inventory.exists)
        inventory.tap()

        let scanMenu = app.buttons["inventory.scan"]
        XCTAssertTrue(scanMenu.waitForExistence(timeout: 8))
        scanMenu.tap()

        XCTAssertTrue(app.buttons["Log new spools"].waitForExistence(timeout: 5))
        XCTAssertFalse(app.buttons["Look up printed part"].exists)
    }

    func testFilamentCoverageIsAbsentFromFarmAndPrinterDetail() {
        let farm = operatorDestinationButton(
            tabTitle: "Farm",
            sidebarIdentifier: "sidebar.farm",
            timeout: 8
        )
        XCTAssertTrue(farm.exists)
        farm.tap()

        let firstCard = app.buttons
            .matching(NSPredicate(format: "identifier BEGINSWITH %@", "farm-card-"))
            .firstMatch
        XCTAssertTrue(firstCard.waitForExistence(timeout: 8))
        XCTAssertFalse(app.descendants(matching: .any)["filament-coverage-badge-covers"].exists)
        XCTAssertFalse(app.descendants(matching: .any)["filament-coverage-badge-runout-eta"].exists)
        XCTAssertFalse(app.descendants(matching: .any)["filament-coverage-badge-runout-no-eta"].exists)

        firstCard.tap()
        XCTAssertTrue(
            app.descendants(matching: .any)
                .matching(NSPredicate(format: "identifier BEGINSWITH %@", "printer.detail.root."))
                .firstMatch
                .waitForExistence(timeout: 8)
        )
        XCTAssertFalse(app.descendants(matching: .any)["filament-coverage-section"].exists)
    }

    // MARK: - Printer Detail v2 operator-first order + Advanced demotion (#712)
    //
    // Best-effort like the other operator-shell tests: without demo data the
    // detail targets may not render, so every step falls back gracefully. When
    // data IS present the test asserts the F7 contract — operator sections are
    // reachable immediately while temperatures/console/jog stay demoted inside
    // a collapsed Advanced disclosure until explicitly expanded.

    /// Navigate Farm → first printer detail. Returns false (skip) if demo data
    /// is unavailable in this environment.
    private func openFirstPrinterDetail() -> Bool {
        guard app.tabBars.buttons["Farm"].waitForExistence(timeout: 5) else { return false }
        app.tabBars.buttons["Farm"].tap()

        // Prefer the stable demo farm-card wrapper; fall back to the first
        // collection cell, mirroring testAdvancedControlsGatedBehindPrinterDetail.
        let farmCard = app.buttons
            .matching(NSPredicate(format: "identifier BEGINSWITH %@", "farm-card-"))
            .firstMatch
        if farmCard.waitForExistence(timeout: 5) {
            farmCard.tap()
            return true
        }
        let firstPrinter = app.collectionViews.cells.firstMatch
        guard firstPrinter.waitForExistence(timeout: 5) else { return false }
        firstPrinter.tap()
        return true
    }

    func testPrinterDetailV2OperatorFirstOrderAndAdvancedDemotion() {
        guard openFirstPrinterDetail() else { return }

        // The header controls block anchors the operator layout; if the detail
        // rendered at all it must be reachable without scrolling gymnastics.
        let headerControls = app.otherElements["printer.detail.header.controls"]
        guard headerControls.waitForExistence(timeout: 5) else {
            // Detail did not present rich content in this environment — skip.
            return
        }

        // Advanced is demoted into a collapsed DisclosureGroup. Its inner entry
        // (`printer.detail.advanced`) must NOT be in the accessibility tree
        // until the operator explicitly expands the disclosure.
        let disclosure = app.buttons["printer.detail.advanced.disclosure"]
        guard disclosure.waitForExistence(timeout: 5) else {
            XCTFail("Advanced disclosure must exist in Printer Detail v2")
            return
        }
        XCTAssertFalse(app.buttons["printer.detail.advanced"].exists,
                       "Advanced controls entry must stay collapsed (demoted) until the disclosure is expanded")

        // Tap-to-live camera toggle lives at the top of the operator layout.
        let liveToggle = app.buttons["printer.detail.camera.livetoggle"]
        if liveToggle.exists {
            liveToggle.tap() // toggles snapshot ⇄ live; must not crash or navigate away
            XCTAssertTrue(headerControls.exists,
                          "Camera tap-to-live must stay within the detail view")
        }

        // Advanced printer controls are a safety interlock and default off.
        // Expanding the disclosure must not reveal an entry until Settings
        // explicitly enables it for the active server.
        disclosure.tap()
        XCTAssertFalse(
            app.buttons["printer.detail.advanced"].exists,
            "Advanced controls entry must be omitted while the per-server safety toggle is off"
        )
    }

    func testPrinterDetailV2DispatchOpensSheet() {
        guard openFirstPrinterDetail() else { return }
        guard app.otherElements["printer.detail.header.controls"].waitForExistence(timeout: 5) else { return }

        // Dispatch-to is only offered when this printer has assigned queue jobs;
        // both presence and absence are acceptable in the demo environment.
        let dispatchButton = app.buttons
            .matching(NSPredicate(format: "identifier BEGINSWITH %@", "printer.detail.queue.dispatch."))
            .firstMatch
        guard dispatchButton.waitForExistence(timeout: 3) else { return }
        dispatchButton.tap()

        let sheet = app.otherElements["printer.detail.dispatch.sheet"]
        XCTAssertTrue(sheet.waitForExistence(timeout: 5),
                      "Tapping dispatch-to must present the candidate sheet")

        let cancel = app.buttons["printer.detail.dispatch.cancel"]
        if cancel.exists { cancel.tap() }
    }
}

@MainActor
final class TwoModesOperatorShellUITests: PrintFarmerUITestCase {
    override var additionalLaunchArguments: [String] {
        ["--uitesting-two-modes"]
    }

    func testFloorModeShowsRequiredCompactDestinations() {
        let tabBar = app.tabBars.firstMatch
        XCTAssertTrue(tabBar.waitForExistence(timeout: 5))

        for destination in ["Attention", "Farm", "Tasks", "Inventory"] {
            XCTAssertTrue(
                tabBar.buttons[destination].exists,
                "Two-modes Floor must expose \(destination)"
            )
        }
        XCTAssertFalse(tabBar.buttons["Scan"].exists)
        XCTAssertFalse(tabBar.buttons["Oversight"].exists)
    }
}
