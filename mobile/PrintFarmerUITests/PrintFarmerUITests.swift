import XCTest

struct RenderedShellRoot {
    enum Surface {
        case tabBar
        case sidebar
    }

    let title: String
    let identifier: String
    let surface: Surface

    var key: String {
        identifier.isEmpty ? title : identifier
    }
}

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
    @discardableResult
    func revealSidebarIfCollapsed(timeout: TimeInterval = 3) -> Bool {
        let labels = ["Sidebar", "Toggle Sidebar", "Show Sidebar"]
        let toggle = app.buttons
            .matching(NSPredicate(format: "label IN %@", labels))
            .firstMatch
        guard toggle.waitForExistence(timeout: timeout) else {
            return false
        }
        toggle.tap()
        return true
    }

    /// Adaptive locator for a shell destination using its shipped identifier.
    /// Returns the iPhone tab-bar button when a compact tab bar is on screen,
    /// otherwise the iPad `NavigationSplitView` sidebar button — revealing a
    /// collapsed sidebar via the system toggle if needed.
    ///
    /// Gives the compact tab a brief chance to appear, then proactively
    /// reveals a collapsed iPad sidebar before polling both surfaces.
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
        let tabButton = app.tabBars.buttons[tabIdentifier]
        let tabElement = app.tabBars.descendants(matching: .any)
            .matching(identifier: tabIdentifier)
            .firstMatch
        let tabLabel = app.tabBars.buttons[tabTitle(for: tabIdentifier)]
        let sidebarIdentifier = tabIdentifier.replacingOccurrences(
            of: "tab.",
            with: "sidebar."
        )
        let sidebar = app.buttons[sidebarIdentifier]
        if tabButton.waitForExistence(timeout: min(1, timeout)) {
            return tabButton
        }
        if tabElement.exists {
            return tabElement
        }
        if tabLabel.exists {
            recordTabIdentifierCompatibilityFallback(tabIdentifier)
            return tabLabel
        }
        if sidebar.exists {
            return sidebar
        }

        _ = revealSidebarIfCollapsed(timeout: min(3, timeout))
        let deadline = Date().addingTimeInterval(timeout)
        while Date() < deadline {
            if tabButton.exists { return tabButton }
            if tabElement.exists { return tabElement }
            if tabLabel.exists {
                recordTabIdentifierCompatibilityFallback(tabIdentifier)
                return tabLabel
            }
            if sidebar.exists { return sidebar }
            RunLoop.current.run(until: Date().addingTimeInterval(0.2))
        }
        if sidebar.exists {
            return sidebar
        }
        if tabElement.exists {
            return tabElement
        }
        if tabLabel.exists {
            recordTabIdentifierCompatibilityFallback(tabIdentifier)
            return tabLabel
        }
        return tabButton
    }

    private func recordTabIdentifierCompatibilityFallback(_ identifier: String) {
        let message = "SwiftUI did not expose \(identifier) on the tab-bar "
            + "accessibility tree; using its documented title as an OS compatibility fallback."
        XCTContext.runActivity(named: "Warning: \(message)") { _ in
            print("warning: \(message)")
        }
    }

    private func tabTitle(for identifier: String) -> String {
        switch identifier {
        case "tab.attention": "Attention"
        case "tab.farm": "Farm"
        case "tab.tasks": "Tasks"
        case "tab.inventory": "Inventory"
        case "tab.oversight": "Oversight"
        case "tab.overview": "Overview"
        case "tab.fleet": "Fleet"
        case "tab.jobs": "Jobs"
        case "tab.upkeep": "Upkeep"
        case "tab.reports": "Reports"
        default: identifier
        }
    }

    func renderedShellRoots(timeout: TimeInterval = 8) -> [RenderedShellRoot] {
        revealSidebarIfCollapsed()
        let deadline = Date().addingTimeInterval(timeout)
        while Date() < deadline {
            let tabButtons = app.tabBars.firstMatch.buttons.allElementsBoundByIndex
                .filter(\.exists)
            if !tabButtons.isEmpty {
                return tabButtons.map {
                    RenderedShellRoot(
                        title: $0.label,
                        identifier: $0.identifier,
                        surface: .tabBar
                    )
                }
            }

            revealSidebarIfCollapsed()
            let sidebarButtons = app.buttons
                .matching(NSPredicate(format: "identifier BEGINSWITH %@", "sidebar."))
                .allElementsBoundByIndex
                .filter(\.exists)
            if !sidebarButtons.isEmpty {
                var identifiers = Set<String>()
                return sidebarButtons.compactMap {
                    guard identifiers.insert($0.identifier).inserted else { return nil }
                    return RenderedShellRoot(
                        title: $0.label,
                        identifier: $0.identifier,
                        surface: .sidebar
                    )
                }
            }
            RunLoop.current.run(until: Date().addingTimeInterval(0.2))
        }

        return app.buttons
            .matching(NSPredicate(format: "identifier BEGINSWITH %@", "sidebar."))
            .allElementsBoundByIndex
            .filter(\.exists)
            .map {
                RenderedShellRoot(
                    title: $0.label,
                    identifier: $0.identifier,
                    surface: .sidebar
                )
            }
    }

    func selectRoot(_ root: RenderedShellRoot) {
        let button: XCUIElement
        switch root.surface {
        case .tabBar:
            button = root.identifier.isEmpty
                ? app.tabBars.firstMatch.buttons[root.title]
                : app.tabBars.firstMatch.buttons[root.identifier]
        case .sidebar:
            revealSidebarIfCollapsed()
            button = app.buttons[root.identifier]
        }
        XCTAssertTrue(button.waitForExistence(timeout: 5), "Missing rendered root \(root.title)")
        button.tap()
    }

    func requireCompactAdaptiveShell() throws {
        guard app.tabBars.firstMatch.waitForExistence(timeout: 8) else {
            throw XCTSkip("Two Modes is intentionally compact-width only")
        }
    }

    func assertCanonicalRootChrome(
        expectsModeControl: Bool,
        root: RenderedShellRoot,
        file: StaticString = #filePath,
        line: UInt = #line
    ) {
        let server = app.buttons["navigation.serverSwitcher"]
        let account = app.buttons["navigation.account"]
        XCTAssertTrue(
            server.waitForExistence(timeout: 5),
            "\(root.title) must show the leading server chip",
            file: file,
            line: line
        )
        XCTAssertTrue(
            account.waitForExistence(timeout: 5),
            "\(root.title) must show the trailing account button",
            file: file,
            line: line
        )
        XCTAssertLessThan(
            server.frame.midX,
            account.frame.midX,
            "\(root.title) must keep server leading and account trailing",
            file: file,
            line: line
        )

        let systemToolbarButtonLabels = Set([
            "Search",
            "Sidebar",
            "Toggle Sidebar",
            "Show Sidebar",
            "Hide Sidebar"
        ])
        let toolbarButtons = app.navigationBars.buttons.allElementsBoundByIndex.filter {
            $0.exists
                && !$0.frame.isEmpty
                && abs($0.frame.midY - account.frame.midY) < 12
                && $0.identifier != "navigation.account"
                && !systemToolbarButtonLabels.contains($0.label)
        }
        XCTAssertTrue(
            toolbarButtons.allSatisfy { $0.frame.midX < account.frame.midX },
            "\(root.title) must keep Account as the trailing-last toolbar action",
            file: file,
            line: line
        )

        let modeControl = app.segmentedControls
            .matching(identifier: "navigation.modeControl")
            .firstMatch
        if expectsModeControl {
            XCTAssertTrue(
                modeControl.waitForExistence(timeout: 5),
                "\(root.title) must show the Two Modes control",
                file: file,
                line: line
            )
            XCTAssertTrue(modeControl.buttons["Floor"].exists, file: file, line: line)
            XCTAssertTrue(modeControl.buttons["Oversight"].exists, file: file, line: line)
        } else {
            XCTAssertFalse(
                modeControl.exists,
                "\(root.title) must not show a mode control in Simple",
                file: file,
                line: line
            )
        }
    }

    func assertEveryRenderedRootHasCanonicalChrome(
        expectsModeControl: Bool,
        file: StaticString = #filePath,
        line: UInt = #line
    ) {
        let roots = renderedShellRoots()
        XCTAssertFalse(
            roots.isEmpty,
            "The rendered shell definition must expose at least one root",
            file: file,
            line: line
        )
        guard !roots.isEmpty else { return }

        XCTAssertEqual(
            Set(roots.map(\.key)).count,
            roots.count,
            "The rendered shell definition must expose unique roots",
            file: file,
            line: line
        )

        for root in roots {
            selectRoot(root)
            assertCanonicalRootChrome(
                expectsModeControl: expectsModeControl,
                root: root,
                file: file,
                line: line
            )
        }
    }
}
