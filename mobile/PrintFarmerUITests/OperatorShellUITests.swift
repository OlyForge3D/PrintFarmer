import XCTest

// swiftlint:disable file_length

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

    func testMockShellDoesNotShowLiveConnectionBanner() {
        let attention = shellDestinationButton(
            tabIdentifier: "tab.attention",
            timeout: 8
        )
        XCTAssertTrue(attention.exists)

        // Cover the monitor's startup grace and subsequent reachability polls.
        // The mock server must not cause a late banner that moves tap targets.
        let liveBanner = XCTNSPredicateExpectation(
            predicate: NSPredicate(format: "exists == true"),
            object: app.buttons["connection-status-bar"]
        )
        liveBanner.isInverted = true
        wait(for: [liveBanner], timeout: 15)
    }

    func testAppLaunchesOnAttentionTab() {
        let attention = shellDestinationButton(
            tabIdentifier: "tab.attention",
            timeout: 5
        )
        XCTAssertTrue(
            attention.exists,
            "Attention must use tab.attention on iPhone or sidebar.attention on iPad"
        )
        XCTAssertTrue(
            attention.isSelected,
            "Attention should be the initial selected destination"
        )
    }

    func testSimpleShellShowsCapabilityEnabledOperatorDestinations() {
        assertOperatorDestinations(
            compact: ["Attention", "Farm", "Tasks", "Inventory", "Oversight"],
            floor: ["attention", "farm", "tasks", "inventory"],
            expectsCompactModeControl: false
        )
    }

    func testRetiredTabsAreNotVisible() {
        for retired in ["tab.notifications", "tab.settings", "tab.scan"] {
            XCTAssertFalse(
                compactTabExists(tabIdentifier: retired),
                "Retired top-level tab '\(retired)' must not appear in F1 shell"
            )
        }
        for retired in ["sidebar.notifications", "sidebar.settings", "sidebar.scan"] {
            XCTAssertFalse(
                app.buttons[retired].exists,
                "Retired sidebar destination '\(retired)' must not appear in F1 shell"
            )
        }
    }

    // MARK: - Re-homed destination reachability

    func testAttentionOverflowIsRemoved() {
        let attention = shellDestinationButton(
            tabIdentifier: "tab.attention",
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
            sidebarRootIdentifier: "sidebar.overview",
            identifier: "oversight.destination.dashboard",
            expectedNavigationBarTitle: "Dashboard"
        )
    }

    func testMaintenanceReachableFromOversight() {
        openOversightDestination(
            sidebarRootIdentifier: "sidebar.upkeep",
            identifier: "oversight.destination.maintenance",
            expectedNavigationBarTitle: "Maintenance"
        )
    }

    func testPredictiveInsightsReachableFromOversight() {
        openOversightDestination(
            sidebarRootIdentifier: "sidebar.upkeep",
            identifier: "oversight.destination.predictiveInsights",
            expectedNavigationBarTitle: "Predictive Insights"
        )
    }

    func testPredictiveInsightsReachableFromPrinterDetail() {
        let farm = shellDestinationButton(tabIdentifier: "tab.farm", timeout: 5)
        guard farm.exists else { return }
        farm.tap()

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

    func testNotificationsReachableFromAccount() {
        openAccount()

        let notifications = app.buttons["account.destination.notifications"]
        XCTAssertTrue(notifications.waitForExistence(timeout: 3))
        notifications.tap()

        XCTAssertTrue(app.navigationBars["Notifications"].waitForExistence(timeout: 5))
    }

    func testManageServersReachableFromAccount() {
        openAccount()

        let manageServers = app.buttons["account.destination.manageServers"]
        XCTAssertTrue(manageServers.waitForExistence(timeout: 3))
        manageServers.tap()

        XCTAssertTrue(app.navigationBars["Servers"].waitForExistence(timeout: 5))
    }

    func testNavigationLayoutIdentifiersReachableFromAccountSettings() {
        openAccount()

        let settings = app.buttons["account.destination.settings"]
        XCTAssertTrue(settings.waitForExistence(timeout: 3))
        settings.tap()

        let navigation = app.descendants(matching: .any)
            .matching(identifier: "settings.navigation")
            .firstMatch
        if !navigation.waitForExistence(timeout: 3) {
            app.swipeUp()
        }
        XCTAssertTrue(navigation.waitForExistence(timeout: 5))
        navigation.tap()

        XCTAssertTrue(
            app.descendants(matching: .any)["navigation.settings"]
                .waitForExistence(timeout: 5)
        )
        for identifier in [
            "navigation.layout.automatic",
            "navigation.layout.simple",
            "navigation.layout.twoModes"
        ] {
            XCTAssertTrue(
                app.buttons[identifier].exists,
                "Navigation settings must expose \(identifier)"
            )
        }
    }

    // MARK: - Advanced controls gating (Farm → printer → Controls page)

    func testAdvancedControlsGatedBehindPrinterDetail() {
        let farm = shellDestinationButton(tabIdentifier: "tab.farm", timeout: 5)
        guard farm.exists else { return }
        farm.tap()

        // Attempting to reach the Controls page before entering a printer
        // must not surface it; the selector only exists inside a printer's
        // detail view, and only when the per-server safety toggle is on.
        XCTAssertFalse(app.segmentedControls["printer.detail.panel.selector"].exists,
                       "Controls page selector must not appear on the Farm tab root")

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

        guard app.descendants(matching: .any)["printer.detail.panel.status"]
            .waitForExistence(timeout: 5) else {
            // Detail did not render rich content in this environment; skip.
            return
        }

        // Advanced printer controls are a safety interlock and default off.
        // With the per-server toggle off, the Controls page (and its
        // selector) must be entirely omitted — not merely collapsed behind
        // a disclosure — so there is zero "Advanced" surface to reach.
        XCTAssertFalse(
            app.segmentedControls["printer.detail.panel.selector"].exists,
            "Controls page selector must be omitted while the per-server safety toggle is off"
        )
        XCTAssertFalse(
            app.descendants(matching: .any)["printer.detail.panel.controls"].exists,
            "Controls page must be omitted while the per-server safety toggle is off"
        )
    }

    private func openAccount(
        file: StaticString = #filePath,
        line: UInt = #line
    ) {
        let attention = shellDestinationButton(
            tabIdentifier: "tab.attention",
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
        sidebarRootIdentifier: String,
        identifier: String,
        expectedNavigationBarTitle: String,
        file: StaticString = #filePath,
        line: UInt = #line
    ) {
        let hasTabBar = app.tabBars.firstMatch.waitForExistence(timeout: 3)
        let tabIdentifier = hasTabBar
            ? "tab.oversight"
            : sidebarRootIdentifier.replacingOccurrences(
                of: "sidebar.",
                with: "tab."
            )
        let root = shellDestinationButton(
            tabIdentifier: tabIdentifier,
            timeout: 5
        )
        XCTAssertTrue(root.exists, file: file, line: line)
        root.tap()

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
        let farm = shellDestinationButton(
            tabIdentifier: "tab.farm",
            timeout: 8
        )
        XCTAssertTrue(farm.exists)
        XCTAssertTrue(farm.isSelected)

        assertOperatorDestinations(
            compact: ["Farm", "Inventory", "Oversight"],
            floor: ["farm", "inventory"],
            expectsCompactModeControl: false
        )

        revealSidebarIfCollapsed()
        XCTAssertFalse(compactTabExists(tabIdentifier: "tab.attention"))
        XCTAssertFalse(app.buttons["sidebar.attention"].exists)
        XCTAssertFalse(compactTabExists(tabIdentifier: "tab.tasks"))
        XCTAssertFalse(app.buttons["sidebar.tasks"].exists)
        XCTAssertFalse(app.buttons["attention.fallback.notifications"].exists)
    }

    func testPrintedPartsAreAbsentWhileSpoolInventoryRemainsVisible() {
        let inventory = shellDestinationButton(
            tabIdentifier: "tab.inventory",
            timeout: 8
        )
        XCTAssertTrue(inventory.exists)
        inventory.tap()

        XCTAssertTrue(app.navigationBars["Spool Inventory"].waitForExistence(timeout: 8))
        XCTAssertFalse(app.segmentedControls["inventory.segmentPicker"].exists)
        XCTAssertFalse(app.buttons["Printed Parts"].exists)
    }

    func testPrintedPartsScanActionIsAbsentWhileSpoolActionRemainsVisible() {
        let inventory = shellDestinationButton(
            tabIdentifier: "tab.inventory",
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
        let farm = shellDestinationButton(
            tabIdentifier: "tab.farm",
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
        // The old FilamentCoverageDetailSection (#778) is gone; #2519's
        // PrinterFilamentSection now owns the one Filament block and renders
        // unconditionally, but with feature-disabled coverage state — never
        // a covers/runout badge — when this capability is off.
        XCTAssertFalse(app.descendants(matching: .any)["filament-coverage-badge-covers"].exists)
        XCTAssertFalse(app.descendants(matching: .any)["filament-coverage-badge-runout-eta"].exists)
        XCTAssertFalse(app.descendants(matching: .any)["filament-coverage-badge-runout-no-eta"].exists)

        // Assert the new component's actual presentation rather than only
        // the absence of legacy identifiers: it must render and must show
        // the explicit "Coverage disabled" state, with no coverage summary
        // or aggregate verdict data.
        let filamentHeading = app.descendants(matching: .any)["printer.filament.heading"]
        XCTAssertTrue(
            filamentHeading.waitForExistence(timeout: 8),
            "PrinterFilamentSection must render on printer detail"
        )
        XCTAssertTrue(
            app.staticTexts["Coverage disabled"].waitForExistence(timeout: 5),
            "PrinterFilamentSection must show the explicit feature-disabled coverage state"
        )
        XCTAssertFalse(
            app.descendants(matching: .any)["printer.filament.summary"].exists,
            "No coverage summary/aggregate verdict may render while coverage is disabled"
        )
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
        let farm = shellDestinationButton(tabIdentifier: "tab.farm", timeout: 5)
        guard farm.exists else { return false }
        farm.tap()

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

        // The Status page anchors the operator layout (issue #2522); if the
        // detail rendered at all it must be reachable without scrolling
        // gymnastics.
        let statusPage = app.descendants(matching: .any)["printer.detail.panel.status"]
        guard statusPage.waitForExistence(timeout: 5) else {
            // Detail did not present rich content in this environment — skip.
            return
        }

        // Advanced printer controls are a safety interlock and default off.
        // With the per-server toggle off there must be ZERO Advanced surface
        // at all — no selector, no Controls page, and (critically) no nested
        // "Advanced → Advanced" the pre-#2522 disclosure link used to allow.
        XCTAssertFalse(
            app.segmentedControls["printer.detail.panel.selector"].exists,
            "Panel selector must be omitted while the per-server safety toggle is off"
        )
        XCTAssertFalse(
            app.descendants(matching: .any)["printer.detail.panel.controls"].exists,
            "Controls page must be omitted while the per-server safety toggle is off"
        )

        // Tap-to-live camera toggle lives at the top of the operator layout.
        let liveToggle = app.buttons["printer.detail.camera.livetoggle"]
        if liveToggle.exists {
            liveToggle.tap() // toggles snapshot ⇄ live; must not crash or navigate away
            XCTAssertTrue(statusPage.exists,
                          "Camera tap-to-live must stay within the detail view")
        }
    }

    func testPrinterDetailV2DispatchOpensSheet() {
        guard openFirstPrinterDetail() else { return }
        guard app.descendants(matching: .any)["printer.detail.panel.status"]
            .waitForExistence(timeout: 5) else { return }

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
        assertOperatorDestinations(
            compact: ["Attention", "Farm", "Tasks", "Inventory"],
            floor: ["attention", "farm", "tasks", "inventory"],
            expectsCompactModeControl: true
        )
    }
}

@MainActor
private extension PrintFarmerUITestCase {
    func assertOperatorDestinations(
        compact expectedTitles: [String],
        floor expectedFloor: [String],
        expectsCompactModeControl: Bool,
        file: StaticString = #filePath,
        line: UInt = #line
    ) {
        let roots = renderedShellRoots()
        let evidence = XCTAttachment(string: """
            Launch arguments: \(app.launchArguments)
            Rendered roots: \(roots.map { "\($0.identifier): \($0.title)" })
            \(app.debugDescription)
            """)
        evidence.name = "Operator shell navigation contract"
        evidence.lifetime = .keepAlways
        add(evidence)

        XCTAssertFalse(roots.isEmpty, "The operator navigation must render", file: file, line: line)
        if app.tabBars.firstMatch.exists {
            // Native SwiftUI tabs can expose titles without tab.* identifiers.
            // Compare every rendered button, not a filtered expected subset.
            XCTAssertEqual(roots.count, expectedTitles.count, file: file, line: line)
            XCTAssertEqual(roots.map(\.title), expectedTitles, file: file, line: line)
            XCTAssertEqual(
                app.segmentedControls.matching(identifier: "navigation.modeControl").count,
                expectsCompactModeControl ? 1 : 0,
                file: file,
                line: line
            )
        } else {
            let expected = (expectedFloor + ["overview", "fleet", "jobs", "upkeep", "reports"])
                .map { "sidebar.\($0)" }
            XCTAssertEqual(roots.map(\.identifier), expected, file: file, line: line)
            XCTAssertTrue(app.staticTexts["sidebar.section.floor"].exists, file: file, line: line)
            XCTAssertTrue(app.staticTexts["sidebar.section.oversight"].exists, file: file, line: line)
            XCTAssertFalse(app.buttons["sidebar.oversight"].exists, file: file, line: line)
            XCTAssertEqual(
                app.segmentedControls.matching(identifier: "navigation.modeControl").count,
                0,
                "The expanded iPad sidebar must not render a Floor/Oversight control",
                file: file,
                line: line
            )
        }

        for retired in ["notifications", "settings", "scan"] {
            XCTAssertFalse(compactTabExists(tabIdentifier: "tab.\(retired)"), file: file, line: line)
            XCTAssertFalse(app.buttons["sidebar.\(retired)"].exists, file: file, line: line)
        }
    }
}

@MainActor
final class TwoModesOversightShellUITests: PrintFarmerUITestCase {
    override var additionalLaunchArguments: [String] {
        [
            "--uitesting-two-modes",
            "--uitesting-oversight-mode",
        ]
    }

    func testOversightModeShowsRequiredTabsAndMovedDestinations() throws {
        try requireCompactAdaptiveShell()

        XCTAssertTrue(
            app.segmentedControls["navigation.modeControl"]
                .waitForExistence(timeout: 8)
        )
        XCTAssertEqual(
            app.segmentedControls
                .matching(identifier: "navigation.modeControl")
                .count,
            1,
            "Two-modes Oversight roots must render exactly one Floor/Oversight control"
        )

        for identifier in [
            "tab.overview",
            "tab.fleet",
            "tab.jobs",
            "tab.upkeep",
            "tab.reports"
        ] {
            let destination = shellDestinationButton(
                tabIdentifier: identifier,
                timeout: 8
            )
            XCTAssertTrue(
                destination.exists,
                "Two-modes Oversight must expose \(identifier)"
            )
        }

        shellDestinationButton(tabIdentifier: "tab.overview").tap()
        let dashboard = app.buttons["oversight.destination.dashboard"]
        XCTAssertTrue(dashboard.waitForExistence(timeout: 5))
        dashboard.tap()
        XCTAssertTrue(app.navigationBars["Dashboard"].waitForExistence(timeout: 5))

        shellDestinationButton(tabIdentifier: "tab.upkeep").tap()
        let maintenance = app.buttons["oversight.destination.maintenance"]
        XCTAssertTrue(maintenance.waitForExistence(timeout: 5))
        maintenance.tap()
        XCTAssertTrue(app.navigationBars["Maintenance"].waitForExistence(timeout: 5))
    }
}

@MainActor
final class SimpleShellChromeRegressionUITests: PrintFarmerUITestCase {
    override var additionalLaunchArguments: [String] {
        ["--uitesting-navigation-chrome"]
    }

    func testEveryRenderedSimpleRootUsesCanonicalChrome() {
        assertEveryRenderedRootHasCanonicalChrome(expectsModeControl: false)
    }
}

@MainActor
final class TwoModesChromeRegressionUITests: PrintFarmerUITestCase {
    override var additionalLaunchArguments: [String] {
        ["--uitesting-navigation-chrome", "--uitesting-two-modes"]
    }

    func testEveryRenderedRootInBothModesUsesCanonicalChrome() throws {
        try requireCompactAdaptiveShell()
        assertEveryRenderedRootHasCanonicalChrome(expectsModeControl: true)

        let modeControl = app.segmentedControls
            .matching(identifier: "navigation.modeControl")
            .firstMatch
        XCTAssertTrue(modeControl.waitForExistence(timeout: 5))
        modeControl.buttons["Oversight"].tap()

        assertEveryRenderedRootHasCanonicalChrome(expectsModeControl: true)
    }

    func testPushedScreenCarriesNoRootChrome() throws {
        try requireCompactAdaptiveShell()
        let modeControl = app.segmentedControls
            .matching(identifier: "navigation.modeControl")
            .firstMatch
        XCTAssertTrue(modeControl.waitForExistence(timeout: 8))
        modeControl.buttons["Oversight"].tap()

        let overview = renderedShellRoots().first
        XCTAssertNotNil(overview)
        if let overview {
            selectRoot(overview)
        }

        let destination = app.buttons["oversight.destination.dashboard"]
        XCTAssertTrue(destination.waitForExistence(timeout: 5))
        destination.tap()
        XCTAssertTrue(app.navigationBars["Dashboard"].waitForExistence(timeout: 5))

        XCTAssertFalse(app.buttons["navigation.serverSwitcher"].isHittable)
        XCTAssertFalse(app.buttons["navigation.account"].isHittable)
        XCTAssertEqual(
            app.segmentedControls
                .matching(identifier: "navigation.modeControl")
                .count,
            0,
            "Pushed screens must render no Floor/Oversight control"
        )
    }
}
