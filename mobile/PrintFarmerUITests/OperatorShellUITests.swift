import XCTest

/// UI tests for the F1 operator shell (issue #706).
///
/// Verifies that the app launches into the Attention tab, that the tab
/// bar exposes the five operator destinations in the required order, and
/// that reachable destinations satisfy the two-tap requirement:
///
/// * Attention → overflow → Settings (2 taps)
/// * Attention → overflow → Dashboard (2 taps)
/// * Attention → overflow → Maintenance (2 taps)
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

    func testTabBarShowsFiveOperatorDestinations() {
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
            ("Tasks", "sidebar.tasks"),
            ("Scan", "sidebar.scan"),
            ("Inventory", "sidebar.inventory")
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
    }

    func testRetiredTabsAreNotVisible() {
        // The old shell exposed dedicated Notifications and Settings tabs.
        // F1 collapses them into the Attention tab / overflow menu.
        for retired in ["Notifications", "Settings"] {
            let tab = app.tabBars.buttons[retired]
            XCTAssertFalse(tab.exists,
                           "Retired top-level tab '\(retired)' must not appear in F1 shell")
        }
    }

    // MARK: - Attention overflow reachability (two-tap gate)

    func testSettingsReachableFromAttentionOverflow() {
        guard app.tabBars.buttons["Attention"].waitForExistence(timeout: 5) else { return }
        app.tabBars.buttons["Attention"].tap()

        let overflow = app.buttons["attention.overflow"]
        guard overflow.waitForExistence(timeout: 5) else {
            XCTFail("Attention overflow control should be reachable in one tap")
            return
        }
        overflow.tap()

        let settingsItem = app.buttons["Settings"]
        XCTAssertTrue(settingsItem.waitForExistence(timeout: 3),
                      "Settings must be reachable within two taps via Attention overflow")
        settingsItem.tap()

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

    func testDashboardReachableFromAttentionOverflow() {
        guard app.tabBars.buttons["Attention"].waitForExistence(timeout: 5) else { return }
        app.tabBars.buttons["Attention"].tap()

        let overflow = app.buttons["attention.overflow"]
        guard overflow.waitForExistence(timeout: 5) else { return }
        overflow.tap()

        let dashboardItem = app.buttons["Dashboard"]
        XCTAssertTrue(dashboardItem.waitForExistence(timeout: 3),
                      "Dashboard must be reachable within two taps via Attention overflow")
    }

    func testMaintenanceReachableFromAttentionOverflow() {
        guard app.tabBars.buttons["Attention"].waitForExistence(timeout: 5) else { return }
        app.tabBars.buttons["Attention"].tap()

        let overflow = app.buttons["attention.overflow"]
        guard overflow.waitForExistence(timeout: 5) else { return }
        overflow.tap()

        let maintenanceItem = app.buttons["Maintenance"]
        XCTAssertTrue(maintenanceItem.waitForExistence(timeout: 3),
                      "Maintenance must be reachable within two taps via Attention overflow")
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

    // MARK: - Legacy sheet dismiss → reopen (#727)
    //
    // Contract these tests enforce:
    //   1. Open a legacy fallback sheet from the Attention overflow (or
    //      every still-reachable legacy sheet).
    //   2. Push a NESTED destination inside that sheet's NavigationStack.
    //   3. Dismiss the sheet.
    //   4. Reopen it. It MUST land on its documented root; the previously
    //      pushed child MUST be absent.
    //
    // A test that never creates stale nested state cannot detect broken
    // #727 wiring: removing the `.onChange { resetLegacySheet }` handlers
    // on AttentionView would still let a root-only reopen appear correct.
    // Every helper therefore fails loudly on missing prerequisites via
    // XCTFail — silent `guard ... return` is banned.

    /// Dismisses a sheet presented from the Attention overflow via a
    /// swipe-down gesture on its navigation bar. Returns true only when
    /// the navigation bar has disappeared afterward.
    private func dismissAttentionSheet(
        navigationBarTitle: String,
        file: StaticString = #filePath,
        line: UInt = #line
    ) -> Bool {
        let bar = app.navigationBars[navigationBarTitle]
        guard bar.waitForExistence(timeout: 3) else {
            XCTFail("Cannot dismiss \(navigationBarTitle) sheet — its navigation bar is not on screen",
                    file: file, line: line)
            return false
        }
        let start = bar.coordinate(withNormalizedOffset: CGVector(dx: 0.5, dy: 0.5))
        let end = start.withOffset(CGVector(dx: 0, dy: 700))
        start.press(forDuration: 0.05, thenDragTo: end)

        // Poll for absence — the swipe animates for ~300ms.
        let deadline = Date().addingTimeInterval(3)
        while Date() < deadline {
            if !bar.exists { return true }
            RunLoop.current.run(until: Date().addingTimeInterval(0.1))
        }
        return false
    }

    private func selectAttentionSurface(
        file: StaticString = #filePath,
        line: UInt = #line
    ) {
        // Compact-width devices (iPhone) surface a bottom TabBar. Tap
        // the Attention tab directly.
        let tabAttention = app.tabBars.buttons["Attention"]
        if tabAttention.waitForExistence(timeout: 5) {
            tabAttention.tap()
            return
        }

        // Regular-width devices (iPad) use NavigationSplitView. The
        // Attention destination is the default tab, so the Attention
        // navigation bar is already on screen — no selection needed.
        // If the sidebar happens to be collapsed, tapping the visible
        // Attention nav bar is a no-op that still leaves us on Attention.
        if app.navigationBars["Attention"].waitForExistence(timeout: 5) {
            return
        }

        // Last resort: try the explicit sidebar identifier. On iPad
        // portrait the sidebar may be behind a system-provided toggle;
        // attempt to reveal it.
        if let toggle = firstMatchingSidebarToggle(), toggle.waitForExistence(timeout: 1) {
            toggle.tap()
        }
        let sidebarAttention = app.buttons["sidebar.attention"]
        if sidebarAttention.waitForExistence(timeout: 3) {
            sidebarAttention.tap()
            return
        }

        XCTFail("Attention destination is not reachable — no tab bar, no Attention nav bar, no sidebar entry",
                file: file, line: line)
    }

    /// Returns the system-provided sidebar toggle if present. The
    /// element is unlabeled by identifier; XCUI exposes it as a
    /// navigation-bar button with label 'Sidebar' or 'Toggle Sidebar'.
    private func firstMatchingSidebarToggle() -> XCUIElement? {
        let candidates = ["Sidebar", "Toggle Sidebar", "Show Sidebar"]
        for label in candidates {
            let button = app.navigationBars.buttons[label]
            if button.exists { return button }
        }
        return nil
    }

    /// Taps the Attention overflow menu and its named item. Fails loudly
    /// if either control is missing or if the sheet's navigation bar
    /// never appears — silent skipping is banned.
    private func openAttentionOverflowSheet(
        itemIdentifier: String,
        expectedNavigationBarTitle: String,
        file: StaticString = #filePath,
        line: UInt = #line
    ) {
        let overflow = app.buttons["attention.overflow"]
        guard overflow.waitForExistence(timeout: 5) else {
            XCTFail("Attention overflow control is missing — required to open \(expectedNavigationBarTitle) sheet",
                    file: file, line: line)
            return
        }
        overflow.tap()

        let item = app.buttons[itemIdentifier]
        guard item.waitForExistence(timeout: 3) else {
            XCTFail("Overflow item '\(itemIdentifier)' is missing — cannot present \(expectedNavigationBarTitle) sheet",
                    file: file, line: line)
            return
        }
        item.tap()

        XCTAssertTrue(
            app.navigationBars[expectedNavigationBarTitle].waitForExistence(timeout: 5),
            "\(expectedNavigationBarTitle) sheet did not present after tapping overflow item '\(itemIdentifier)'",
            file: file, line: line
        )
    }

    /// Verifies the strict #727 contract: the sheet is opened, a nested
    /// destination is pushed on its NavigationStack, the sheet is
    /// dismissed, and on reopen the sheet must be back at its root with
    /// the nested destination absent.
    ///
    /// `pushNestedDestination` must push exactly one child destination
    /// and return `true` when the child is on screen; if it cannot make
    /// nested state deterministically (e.g. required fixture missing),
    /// it must call `XCTFail` itself and return `false`. Silent success
    /// or silent skip is banned.
    private func assertLegacySheetResetsAfterDismissal(
        sheetLabel: String,
        openSheet: () -> Void,
        rootNavigationBarTitle: String,
        pushNestedDestination: () -> Bool,
        nestedNavigationBarTitle: String,
        file: StaticString = #filePath,
        line: UInt = #line
    ) {
        // 1. Open the sheet.
        openSheet()
        guard app.navigationBars[rootNavigationBarTitle].waitForExistence(timeout: 5) else {
            XCTFail("[\(sheetLabel)] sheet failed to present its root '\(rootNavigationBarTitle)'",
                    file: file, line: line)
            return
        }

        // 2. Push a nested destination. If the helper cannot, it must
        // have failed already.
        guard pushNestedDestination() else {
            XCTFail("[\(sheetLabel)] could not push nested destination '\(nestedNavigationBarTitle)' — test cannot verify #727 without stale nested state",
                    file: file, line: line)
            return
        }
        XCTAssertTrue(
            app.navigationBars[nestedNavigationBarTitle].waitForExistence(timeout: 5),
            "[\(sheetLabel)] nested destination '\(nestedNavigationBarTitle)' did not appear after push",
            file: file, line: line
        )

        // 3. Dismiss the sheet (from the nested destination).
        guard dismissAttentionSheet(navigationBarTitle: nestedNavigationBarTitle) else {
            XCTFail("[\(sheetLabel)] failed to dismiss sheet while '\(nestedNavigationBarTitle)' was on screen",
                    file: file, line: line)
            return
        }

        // 4. Reopen. The sheet MUST be back at its root, and the
        // previously pushed nested destination MUST NOT be on screen.
        openSheet()
        XCTAssertTrue(
            app.navigationBars[rootNavigationBarTitle].waitForExistence(timeout: 5),
            "[\(sheetLabel)] reopened sheet must present its root '\(rootNavigationBarTitle)' — #727 regression: nested state survived dismissal",
            file: file, line: line
        )
        XCTAssertFalse(
            app.navigationBars[nestedNavigationBarTitle].exists,
            "[\(sheetLabel)] nested destination '\(nestedNavigationBarTitle)' must be gone after dismissal + reopen — #727 regression",
            file: file, line: line
        )
    }

    // MARK: - Nested-destination push helpers

    /// Swipes the Dashboard sheet horizontally until the Active-jobs page
    /// (currentPage=1) is on screen. iPad renders the whole page linearly
    /// so no swipe is needed and the row is already queryable after a
    /// short scroll.
    private func revealFirstDashboardActiveJob() -> XCUIElement {
        let row = app.buttons["dashboard.activeJob.0"]
        if row.waitForExistence(timeout: 3), row.isHittable { return row }

        // Compact size class uses TabView paging — swipe left up to three
        // times to reach the Active page. The row's presence check
        // succeeds once the page is realized.
        for _ in 0..<3 {
            app.swipeLeft()
            if row.waitForExistence(timeout: 2), row.isHittable {
                return row
            }
        }
        // Last resort — scroll the current page (iPad iPadContent is a
        // ScrollView; the row may just be below the fold).
        app.swipeUp()
        _ = row.waitForExistence(timeout: 2)
        return row
    }

    private func pushDashboardPrinterDetail(
        file: StaticString = #filePath,
        line: UInt = #line
    ) -> Bool {
        let row = revealFirstDashboardActiveJob()
        guard row.exists else {
            XCTFail("Dashboard active-job row is missing — required demo fixture 'dashboard.activeJob.0' is not on screen",
                    file: file, line: line)
            return false
        }
        row.tap()
        return app.navigationBars["Printer"].waitForExistence(timeout: 5)
    }

    private func pushMaintenanceAnalytics(
        file: StaticString = #filePath,
        line: UInt = #line
    ) -> Bool {
        let analytics = app.buttons["maintenance.analytics.link"]
        if !analytics.waitForExistence(timeout: 3) {
            // Compact iPhone renders MaintenanceView pages; Alerts page
            // (currentPage=0) already contains analyticsLink but it may
            // sit below the fold — scroll to reveal.
            app.swipeUp()
        }
        guard analytics.waitForExistence(timeout: 3) else {
            XCTFail("Maintenance analytics link ('maintenance.analytics.link') is missing — required to push Maintenance Analytics",
                    file: file, line: line)
            return false
        }
        analytics.tap()
        return app.navigationBars["Maintenance Analytics"].waitForExistence(timeout: 5)
    }

    private func pushSettingsManageServers(
        file: StaticString = #filePath,
        line: UInt = #line
    ) -> Bool {
        let manage = app.buttons["settings.manageServers"]
        if !manage.waitForExistence(timeout: 3) {
            app.swipeUp()
        }
        guard manage.waitForExistence(timeout: 3) else {
            XCTFail("Settings 'Manage Servers' link ('settings.manageServers') is missing — required to push ServersView",
                    file: file, line: line)
            return false
        }
        manage.tap()
        return app.navigationBars["Servers"].waitForExistence(timeout: 5)
    }

    // MARK: - Strict #727 scenarios (default authenticated bootstrap)

    func testDashboardSheetResetsPushedPrinterDetailAfterDismissal() {
        selectAttentionSurface()

        assertLegacySheetResetsAfterDismissal(
            sheetLabel: "Dashboard",
            openSheet: {
                self.openAttentionOverflowSheet(
                    itemIdentifier: "attention.overflow.dashboard",
                    expectedNavigationBarTitle: "Dashboard"
                )
            },
            rootNavigationBarTitle: "Dashboard",
            pushNestedDestination: { self.pushDashboardPrinterDetail() },
            nestedNavigationBarTitle: "Printer"
        )
    }

    func testMaintenanceSheetResetsPushedAnalyticsAfterDismissal() {
        selectAttentionSurface()

        assertLegacySheetResetsAfterDismissal(
            sheetLabel: "Maintenance",
            openSheet: {
                self.openAttentionOverflowSheet(
                    itemIdentifier: "attention.overflow.maintenance",
                    expectedNavigationBarTitle: "Maintenance"
                )
            },
            rootNavigationBarTitle: "Maintenance",
            pushNestedDestination: { self.pushMaintenanceAnalytics() },
            nestedNavigationBarTitle: "Maintenance Analytics"
        )
    }

    func testSettingsSheetResetsPushedManageServersAfterDismissal() {
        selectAttentionSurface()

        assertLegacySheetResetsAfterDismissal(
            sheetLabel: "Settings",
            openSheet: {
                self.openAttentionOverflowSheet(
                    itemIdentifier: "attention.overflow.settings",
                    expectedNavigationBarTitle: "Settings"
                )
            },
            rootNavigationBarTitle: "Settings",
            pushNestedDestination: { self.pushSettingsManageServers() },
            nestedNavigationBarTitle: "Servers"
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
        let scan = operatorDestinationButton(
            tabTitle: "Scan",
            sidebarIdentifier: "sidebar.scan",
            timeout: 8
        )
        XCTAssertTrue(scan.exists)
        scan.tap()

        XCTAssertTrue(app.buttons["scan.quickAction.spool"].waitForExistence(timeout: 8))
        XCTAssertFalse(app.buttons["scan.quickAction.parts"].exists)
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
