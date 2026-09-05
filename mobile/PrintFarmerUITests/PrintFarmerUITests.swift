import XCTest

/// Base class for all PrintFarmer UI tests.
///
/// Launches the app with `--uitesting` so the app switches to the
/// deterministic bootstrap. By default this is the **authenticated**
/// operator-shell mode. Subclasses that need a different deterministic
/// launch mode (e.g. the unauthenticated login flow) override
/// `additionalLaunchArguments`; those are applied before the app launches.
@MainActor
class PrintFarmerUITestCase: XCTestCase {

    var app: XCUIApplication!

    /// Extra launch arguments contributed by a subclass, applied before the
    /// app launches. Base tests run in the authenticated operator-shell
    /// bootstrap; override to select a different explicit launch mode.
    var additionalLaunchArguments: [String] { [] }

    override func setUp() async throws {
        try await super.setUp()
        continueAfterFailure = false
        app = XCUIApplication()
        app.launchEnvironment["PFARM_UI_TESTING"] = "1"
        app.launchArguments.append("--uitesting")
        app.launchArguments.append(contentsOf: additionalLaunchArguments)
        app.launch()
    }

    override func tearDown() async throws {
        app = nil
        try await super.tearDown()
    }

    // MARK: - Helpers

    /// Wait for an element to exist with a timeout.
    func waitForElement(_ element: XCUIElement, timeout: TimeInterval = 5) {
        let exists = element.waitForExistence(timeout: timeout)
        XCTAssertTrue(exists, "Expected element \(element) to exist within \(timeout)s")
    }

    /// Dismiss any system alert (e.g., notification permission).
    func dismissSystemAlertIfNeeded() {
        let springboard = XCUIApplication(bundleIdentifier: "com.apple.springboard")
        let allowButton = springboard.buttons["Allow"]
        if allowButton.waitForExistence(timeout: 2) {
            allowButton.tap()
        }
    }

    // MARK: - Adaptive shell navigation (iPhone tab bar / iPad sidebar)

    /// Reveal the iPad NavigationSplitView sidebar via the system-provided
    /// nav-bar toggle if it appears to be collapsed. No-op on compact width
    /// (iPhone) or when the sidebar is already visible.
    func revealSidebarIfCollapsed() {
        for label in ["Sidebar", "Toggle Sidebar", "Show Sidebar"] {
            let toggle = app.navigationBars.buttons[label]
            if toggle.exists {
                toggle.tap()
                return
            }
        }
    }

    /// Adaptive locator for a shell destination using its shipped identifier.
    /// Returns the iPhone tab-bar button when a compact tab bar is on screen,
    /// otherwise the iPad `NavigationSplitView` sidebar button — revealing a
    /// collapsed sidebar via the system toggle if needed.
    ///
    /// Polls both surfaces at 200 ms intervals and returns whichever
    /// materializes first, keeping the wait budget shared instead of
    /// serially blocking on the non-adaptive `tabBars.buttons[...]`
    /// query that fails deterministically on iPad regular size class.
    ///
    /// - Parameters:
    ///   - tabIdentifier: The compact tab identifier (e.g. `tab.attention`).
    ///     The matching iPad identifier is derived as `sidebar.attention`.
    ///   - timeout: Maximum time to wait for either surface.
    /// - Returns: The located `XCUIElement`. Callers should assert
    ///   `.exists` on the returned element so the failure message
    ///   describes both surfaces explicitly.
    func shellDestinationButton(
        tabIdentifier: String,
        timeout: TimeInterval = 5
    ) -> XCUIElement {
        let tab = app.tabBars.buttons[tabIdentifier]
        let sidebarIdentifier = tabIdentifier.replacingOccurrences(
            of: "tab.",
            with: "sidebar."
        )
        let sidebar = app.buttons[sidebarIdentifier]
        let deadline = Date().addingTimeInterval(timeout)
        while Date() < deadline {
            if tab.exists { return tab }
            if sidebar.exists { return sidebar }
            RunLoop.current.run(until: Date().addingTimeInterval(0.2))
        }
        // iPad portrait may keep the sidebar collapsed behind the system
        // toggle. Try once to reveal it, then give the sidebar button a
        // short window to materialize before giving up.
        revealSidebarIfCollapsed()
        _ = sidebar.waitForExistence(timeout: 2)
        return sidebar.exists ? sidebar : tab
    }
}
