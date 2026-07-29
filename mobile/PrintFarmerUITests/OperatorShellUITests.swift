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

        let firstPrinter = app.collectionViews.cells.firstMatch
        guard firstPrinter.waitForExistence(timeout: 5) else {
            // Mock data may not include an online printer; skip gracefully.
            return
        }
        firstPrinter.tap()

        // Advanced entry may be hidden for offline printers; both outcomes
        // are acceptable — we assert only that if visible it opens a new
        // destination.
        let advanced = app.buttons["printer.detail.advanced"]
        if advanced.waitForExistence(timeout: 5) {
            advanced.tap()
            let backButton = app.navigationBars.buttons.firstMatch
            XCTAssertTrue(backButton.waitForExistence(timeout: 3),
                          "Advanced controls should push a new navigation destination")
        }
    }

    // MARK: - Legacy sheet dismiss → reopen (#727)
    //
    // Contract these tests enforce:
    //   1. Open a legacy fallback sheet from the Attention overflow (or
    //      the disabled-attention fallback for Notifications).
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

// MARK: - #727 Notifications-fallback coverage
//
// The legacy Notifications sheet is only reachable from
// `AttentionView.disabledFallback`, which renders when the operator
// feature gate resolves `attentionEnabled == false`. This subclass
// launches with the deterministic attention-disabled bootstrap so the
// fallback surface is guaranteed to be on screen.
@MainActor
final class AttentionDisabledFallbackUITests: PrintFarmerUITestCase {

    override var additionalLaunchArguments: [String] {
        // Contract with UITestBootstrap.attentionDisabledLaunchArgument.
        // UI test targets cannot import the app target, so this literal
        // is verified by `test_attentionDisabledLaunchArgument_matchesUITestsHarness`.
        ["--uitesting-attention-disabled"]
    }

    // Reuse the strict helpers via file-scope private extensions would
    // require exposing them; the fallback flow is a single scenario so
    // it's cheapest to inline the strict contract here.

    private func openFallbackNotifications(
        file: StaticString = #filePath,
        line: UInt = #line
    ) {
        // The disabled fallback is the AttentionView.disabledFallback
        // surface, exposed at the Attention destination root. On iPhone
        // this is a tab; on iPad it is the default sidebar column.
        let tabAttention = app.tabBars.buttons["Attention"]
        if tabAttention.waitForExistence(timeout: 5) {
            tabAttention.tap()
        } else if app.navigationBars["Attention"].waitForExistence(timeout: 3) {
            // iPad regular width — Attention nav bar already visible
            // because it is the default destination.
        } else if app.buttons["sidebar.attention"].waitForExistence(timeout: 2) {
            app.buttons["sidebar.attention"].tap()
        } else {
            XCTFail("Attention destination is not reachable under disabled-attention bootstrap",
                    file: file, line: line)
            return
        }

        let fallbackNotificationsButton = app.buttons["attention.fallback.notifications"]
        if !fallbackNotificationsButton.waitForExistence(timeout: 10) {
            let dump = app.debugDescription
            XCTFail("Fallback Notifications button (id='attention.fallback.notifications') must be on screen under --uitesting-attention-disabled. App hierarchy:\n\(dump)",
                    file: file, line: line)
            return
        }

        XCTAssertEqual(fallbackNotificationsButton.label, "Notifications", file: file, line: line)
        XCTAssertGreaterThanOrEqual(fallbackNotificationsButton.frame.width, 44, file: file, line: line)
        XCTAssertGreaterThanOrEqual(fallbackNotificationsButton.frame.height, 44, file: file, line: line)

        guard fallbackNotificationsButton.isHittable else {
            XCTFail("attention.fallback.notifications is not hittable — fallback surface is present but obscured",
                    file: file, line: line)
            return
        }
        fallbackNotificationsButton.tap()
        XCTAssertTrue(
            app.navigationBars["Notifications"].waitForExistence(timeout: 5),
            "Notifications fallback sheet did not present after tapping attention.fallback.notifications",
            file: file, line: line
        )
    }

    /// Pushes a nested `JobDetailView` from the fallback Notifications
    /// sheet. Uses the deterministic first-notification row emitted by
    /// `DemoNotificationService`, which carries a `jobId`.
    private func pushFirstNotificationJobDetail(
        file: StaticString = #filePath,
        line: UInt = #line
    ) -> Bool {
        // Any notification whose `jobId` is non-nil pushes a jobDetail
        // destination when tapped (see `NotificationsView.handleTap`).
        // notif-001 is the first row in `DemoData.notifications` and its
        // `jobId == DemoData.job1ID`.
        let identifier = "notifications.row.notif-001"
        let row = app.descendants(matching: .any).matching(identifier: identifier).firstMatch
        guard row.waitForExistence(timeout: 3) else {
            XCTFail("Deterministic notification row '\(identifier)' is missing — DemoNotificationService fixture drift",
                    file: file, line: line)
            return false
        }
        row.tap()

        // The push targets JobDetailView, whose navigationTitle is the
        // job's name (falls back to 'Job' if unloaded). The global
        // 'Notifications' back button — a nav-bar button whose label
        // reads 'Notifications' anywhere in the hierarchy — is the
        // child sentinel this test relies on. Its absence here is a
        // hard failure: without a pushed child there is nothing to
        // regress in the #727 fallback flow.
        let childSentinel = app.navigationBars.buttons["Notifications"]
        guard childSentinel.waitForExistence(timeout: 5) else {
            XCTFail("Nested Notifications → job detail push did not surface the global 'Notifications' back button within 5s — the child sentinel this test depends on never appeared",
                    file: file, line: line)
            return false
        }
        return true
    }

    func testNotificationsFallbackSheetResetsPushedJobDetailAfterDismissal() {
        // The child sentinel is the identical query used before
        // dismissal AND after reopen: any nav bar back button labeled
        // 'Notifications'. On the Notifications root this must be
        // absent (a root has no parent to point back to). When a
        // nested JobDetailView is pushed, the pushed screen's back
        // button carries this label. Using the SAME query both times
        // is what makes this test non-vacuous — a scoped query after
        // reopen would trivially return absent whenever the top bar
        // is not titled 'Notifications' and silently pass on the
        // exact bug #727 fixes.
        let childSentinel = app.navigationBars.buttons["Notifications"]
        let notificationsRootBar = app.navigationBars["Notifications"]

        // 1. Open the fallback Notifications sheet.
        openFallbackNotifications()
        XCTAssertTrue(
            notificationsRootBar.waitForExistence(timeout: 5),
            "Notifications fallback root did not present — cannot exercise the #727 regression path"
        )
        guard notificationsRootBar.exists else { return }

        // 2. Push nested job detail. pushFirstNotificationJobDetail
        // now fails loudly on a missing row OR a missing child
        // sentinel; we only stop here after those failures are
        // reported so the acceptance path never silently passes.
        guard pushFirstNotificationJobDetail() else { return }

        // 3. Confirm the child sentinel is present before dismissal
        // using the identical query used after reopen.
        XCTAssertTrue(
            childSentinel.waitForExistence(timeout: 5),
            "Nested Notifications → job detail push did not surface the global 'Notifications' back button — child sentinel absent before dismissal"
        )

        // 4. Dismiss while nested. Swipe on the top nav bar (which is
        // the job detail bar, not 'Notifications').
        let currentBar = app.navigationBars.element(boundBy: 0)
        guard currentBar.waitForExistence(timeout: 3) else {
            XCTFail("No navigation bar on screen to swipe-dismiss the nested Notifications sheet")
            return
        }
        let start = currentBar.coordinate(withNormalizedOffset: CGVector(dx: 0.5, dy: 0.5))
        let end = start.withOffset(CGVector(dx: 0, dy: 700))
        start.press(forDuration: 0.05, thenDragTo: end)

        // Wait for the sheet to dismiss — the fallback Notifications
        // button (an unambiguous sentinel of the disabled-attention
        // fallback surface) must be back on screen.
        let fallbackNotificationsButton = app.buttons["attention.fallback.notifications"]
        XCTAssertTrue(
            fallbackNotificationsButton.waitForExistence(timeout: 5),
            "Dismissing the nested Notifications fallback did not return to the disabled-attention fallback surface"
        )

        // 5. Reopen. Notifications sheet MUST land at its root.
        openFallbackNotifications()

        // 5a. Root visibility is a positive precondition — without it
        // any assertion about the child sentinel would be vacuous.
        XCTAssertTrue(
            notificationsRootBar.waitForExistence(timeout: 5),
            "Reopened fallback Notifications sheet did not present its root — #727 regression indicator"
        )

        // 5b. The root-bar expectation above is the post-dismissal UI-idle
        // barrier. If a stale child were restored, this root bar would no
        // longer be the visible navigation surface.
        XCTAssertTrue(
            notificationsRootBar.exists,
            "Notifications root disappeared after reopen — a stale child was restored"
        )

        // 5c. The IDENTICAL global sentinel used before dismissal
        // must now be absent. Using the same query proves the
        // pushed child is truly gone — this is the #727 contract.
        XCTAssertFalse(
            childSentinel.exists,
            "Reopened fallback Notifications sheet retained the pushed job-detail child (global 'Notifications' back button still present) — #727 regression"
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

        // Expanding the disclosure surfaces the demoted advanced entry.
        disclosure.tap()
        let advanced = app.buttons["printer.detail.advanced"]
        XCTAssertTrue(advanced.waitForExistence(timeout: 5),
                      "Expanding the Advanced disclosure must reveal the advanced controls entry")
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
