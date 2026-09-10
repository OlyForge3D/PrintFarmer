import XCTest

/// Stops starting remote work after one monotonic deadline. An in-flight XCUI
/// query still needs XCTest's enabled per-test watchdog to interrupt a stall.
@MainActor
final class UIWaitBudget {
    private let now: () -> TimeInterval
    private let started: TimeInterval
    private let deadline: TimeInterval
    private(set) var lastOperation = "none"

    init(timeout: TimeInterval, now: @escaping () -> TimeInterval = {
        ProcessInfo.processInfo.systemUptime
    }) {
        self.now = now
        started = now()
        deadline = started + max(0, timeout)
    }

    var remaining: TimeInterval { max(0, deadline - now()) }
    var diagnostic: String {
        "elapsed=\(now() - started)s; last operation=\(lastOperation); remaining=\(remaining)s"
    }

    func perform<T>(_ operation: String, _ body: () -> T) -> T? {
        guard remaining > 0 else { return nil }
        lastOperation = operation
        let result = body()
        return remaining > 0 ? result : nil
    }

    func exists(_ element: XCUIElement, named identifier: String) -> Bool {
        perform("exists: \(identifier)") { element.exists } == true
    }

    func shellSurface(
        compactTabBarExists: () -> Bool,
        sidebarNavigationBarExists: () -> Bool
    ) -> RenderedShellRoot.Surface? {
        if perform("exists: compact tab bar", compactTabBarExists) == true {
            return .tabBar
        }
        if perform("exists: sidebar navigation bar", sidebarNavigationBarExists) == true {
            return .sidebar
        }
        return nil
    }

    func waitFor(
        _ element: XCUIElement,
        named identifier: String,
        upTo timeout: TimeInterval
    ) -> Bool {
        let stepDeadline = now() + min(timeout, remaining)
        while now() < stepDeadline {
            if exists(element, named: identifier) { return true }
            pause(upTo: min(0.2, stepDeadline - now()))
        }
        return false
    }

    func pause(upTo interval: TimeInterval = 0.2) {
        let delay = min(interval, remaining)
        if delay > 0 {
            RunLoop.current.run(until: Date().addingTimeInterval(delay))
        }
    }
}

@MainActor
final class UIWaitBudgetTests: XCTestCase {
    func testCompactShellSkipsAbsentSidebarProbeThatWouldOverrun() {
        var clock: TimeInterval = 0
        var operations: [String] = []
        let budget = UIWaitBudget(timeout: 5, now: { clock })
        let surface = budget.shellSurface(
            compactTabBarExists: {
                operations.append("compact root")
                clock += 1
                return true
            },
            sidebarNavigationBarExists: {
                operations.append("absent sidebar")
                clock += 6
                return false
            }
        )
        XCTAssertEqual(surface, .tabBar)
        XCTAssertEqual(budget.remaining, 4)
        XCTAssertEqual(budget.perform("compact children") {
            operations.append("compact children")
            return "tab.farm"
        }, "tab.farm")
        XCTAssertEqual(operations, ["compact root", "compact children"])
    }

    func testRegularShellChecksCompactRootBeforeSidebarRoot() {
        var clock: TimeInterval = 0
        var operations: [String] = []
        let budget = UIWaitBudget(timeout: 5, now: { clock })
        let surface = budget.shellSurface(
            compactTabBarExists: {
                operations.append("compact root")
                clock += 1
                return false
            },
            sidebarNavigationBarExists: {
                operations.append("sidebar root")
                clock += 1
                return true
            }
        )
        XCTAssertEqual(surface, .sidebar)
        XCTAssertEqual(operations, ["compact root", "sidebar root"])
        XCTAssertEqual(budget.remaining, 3)
    }

    func testShellRootOverrunDoesNotProbeAnotherSurface() {
        var clock: TimeInterval = 0
        var sidebarQueried = false
        let budget = UIWaitBudget(timeout: 2, now: { clock })
        XCTAssertNil(budget.shellSurface(
            compactTabBarExists: { clock = 3; return false },
            sidebarNavigationBarExists: { sidebarQueried = true; return true }
        ))
        XCTAssertFalse(sidebarQueried)
        XCTAssertEqual(budget.lastOperation, "exists: compact tab bar")
    }

    func testMissingShellRootsDoNotIdentifyASurface() {
        let budget = UIWaitBudget(timeout: 5, now: { 0 })
        XCTAssertNil(budget.shellSurface(
            compactTabBarExists: { false },
            sidebarNavigationBarExists: { false }
        ))
    }

    func testCompositeOperationsShareOneDeadline() {
        var clock: TimeInterval = 10
        let budget = UIWaitBudget(timeout: 5, now: { clock })
        XCTAssertEqual(budget.perform("initial wait") { clock += 2; return true }, true)
        XCTAssertEqual(budget.remaining, 3)
        XCTAssertEqual(budget.perform("sidebar wait") { clock += 2; return true }, true)
        XCTAssertEqual(budget.remaining, 1)
        clock += 1
        var called = false
        XCTAssertNil(budget.perform("late fallback") { called = true; return true })
        XCTAssertFalse(called)
    }

    func testInFlightOverrunDoesNotStartAnotherQueryOrDiagnostic() {
        var clock: TimeInterval = 0
        let budget = UIWaitBudget(timeout: 2, now: { clock })
        XCTAssertNil(budget.perform("slow query") { clock = 3; return true })
        var queryCount = 0
        XCTAssertNil(budget.perform("fallback") { queryCount += 1 })
        XCTAssertTrue(budget.diagnostic.contains("last operation=slow query"))
        XCTAssertEqual(queryCount, 0)
    }

    func testZeroBudgetNeverStartsRemoteWork() {
        let budget = UIWaitBudget(timeout: 0)
        var called = false
        XCTAssertNil(budget.perform("query") { called = true })
        XCTAssertFalse(called)
        XCTAssertEqual(budget.lastOperation, "none")
    }
}

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

#if PFARM_TIMEOUT_DIAGNOSTICS
/// Opt-in failing probes; never compiled into ordinary local or CI test runs.
@MainActor
final class QueryTimeoutDiagnosticUITests: PrintFarmerUITestCase {
    func testDeliberateRunnerStall() {
        executionTimeAllowance = 60
        print("TIMEOUT_PROBE: blocking the runner for 300s; XCTest must interrupt at 60s")
        Thread.sleep(forTimeInterval: 300)
        XCTFail("XCTest watchdog did not interrupt the deliberately stalled runner")
    }

    func testMissingShellDestination() {
        let start = ProcessInfo.processInfo.systemUptime
        print("TIMEOUT_PROBE: missing shell start uptime=\(start); budget=2s")
        _ = shellDestinationButton(tabIdentifier: "tab.timeout-probe-missing", timeout: 2)
        XCTFail("Deliberately missing shell destination")
    }
}
#endif

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
        let budget = UIWaitBudget(timeout: timeout)
        let exists = budget.waitFor(element, named: "requested element", upTo: timeout)
        // Interpolating XCUIElement on failure resolves its remote description.
        XCTAssertTrue(exists, "Expected element within \(timeout)s; \(budget.diagnostic)")
    }

    /// Dismiss any system alert (e.g., notification permission).
    func dismissSystemAlertIfNeeded() {
        let springboard = XCUIApplication(bundleIdentifier: "com.apple.springboard")
        let allowButton = springboard.buttons["Allow"]
        if allowButton.waitForExistence(timeout: 2) {
            allowButton.tap()
        }
    }

    /// Coverage facts are disclosed separately from the compact assignment summary.
    func openPrinterFilamentDetails(printerID: String) -> XCUIElement {
        let root = app.descendants(matching: .any)
            .matching(identifier: "printer.detail.root.\(printerID)").firstMatch
        XCTAssertTrue(root.waitForExistence(timeout: 10),
                      "Navigation must reach the exact printer's detail, not a same-name sibling.")
        let overview = root.descendants(matching: .any)
            .matching(identifier: "printer.detail.panel.overview").firstMatch
        XCTAssertTrue(overview.waitForExistence(timeout: 5))
        let heading = overview.descendants(matching: .any)
            .matching(identifier: "printer.filament.heading").firstMatch
        XCTAssertTrue(heading.waitForExistence(timeout: 10))
        let disclosure = overview.buttons["printer.filament.disclosure"]
        XCTAssertTrue(disclosure.waitForExistence(timeout: 5))
        for _ in 0..<3 {
            if disclosure.isHittable { break }
            overview.swipeUp()
        }
        XCTAssertTrue(disclosure.isHittable, "Coverage remains reachable through Filament details.")
        disclosure.tap()
        return overview
    }

    // MARK: - Adaptive shell navigation (iPhone tab bar / iPad sidebar)

    // ContentView's split-view sidebar owns this navigation title; destination
    // buttons are not surface probes because they may not exist on compact UI.
    private var sidebarNavigationBar: XCUIElement {
        app.navigationBars["PrintFarmer"]
    }

    /// Reveal the iPad NavigationSplitView sidebar via the system-provided
    /// nav-bar toggle if it appears to be collapsed. No-op on compact width
    /// (iPhone) or when the sidebar is already visible.
    @discardableResult
    func revealSidebarIfCollapsed(timeout: TimeInterval = 3) -> Bool {
        revealSidebarIfCollapsed(budget: UIWaitBudget(timeout: timeout), toggleWait: timeout)
    }

    private func revealSidebarIfCollapsed(budget: UIWaitBudget, toggleWait: TimeInterval) -> Bool {
        let sidebar = sidebarNavigationBar
        if budget.exists(sidebar, named: "sidebar navigation bar") {
            return true
        }
        let labels = ["Sidebar", "Toggle Sidebar", "Show Sidebar"]
        let toggle = app.buttons
            .matching(NSPredicate(format: "label IN %@", labels))
            .firstMatch
        guard budget.waitFor(toggle, named: "sidebar toggle", upTo: toggleWait) else {
            return false
        }
        guard budget.perform("tap sidebar toggle", {
            toggle.tap()
            return true
        }) == true else {
            return false
        }
        if budget.waitFor(sidebar, named: "sidebar navigation bar", upTo: min(1, budget.remaining)) {
            return true
        }

        // On a cold iPad launch the native toggle can consume its first tap
        // without opening. Only use the alternate gesture if it still says
        // Show Sidebar; never blindly toggle again and close a visible sidebar.
        guard budget.exists(app.buttons["Show Sidebar"], named: "Show Sidebar") else {
            return false
        }
        return revealSidebarFromLeadingEdge(budget: budget)
    }

    @discardableResult
    func revealSidebarFromLeadingEdge(timeout: TimeInterval = 3) -> Bool {
        revealSidebarFromLeadingEdge(budget: UIWaitBudget(timeout: timeout))
    }

    private func revealSidebarFromLeadingEdge(budget: UIWaitBudget) -> Bool {
        guard budget.perform("leading-edge sidebar gesture", {
            let window = app.windows.firstMatch
            let start = window.coordinate(withNormalizedOffset: CGVector(dx: 0.01, dy: 0.5))
            let end = window.coordinate(withNormalizedOffset: CGVector(dx: 0.35, dy: 0.5))
            start.press(forDuration: 0.1, thenDragTo: end)
            return true
        }) == true else { return false }
        return budget.waitFor(sidebarNavigationBar, named: "sidebar navigation bar", upTo: budget.remaining)
    }

    /// Adaptive locator for a shell destination using its shipped identifier.
    /// Returns the iPhone tab-bar button when a compact tab bar is on screen,
    /// otherwise the iPad `NavigationSplitView` sidebar button — revealing a
    /// collapsed sidebar via the system toggle if needed.
    ///
    /// Inspect the rendered surface before querying its children. Repeated
    /// missing compact-tab queries can exhaust the budget on a cold iPad
    /// before its sidebar toggle is ever tapped.
    ///
    /// - Parameters:
    ///   - tabIdentifier: The compact tab identifier (e.g. `tab.attention`).
    ///     The matching iPad identifier is derived as `sidebar.attention`.
    ///   - timeout: Maximum time to wait for either surface.
    /// - Returns: The located `XCUIElement`. Failure is recorded here without
    ///   resolving another remote element or hierarchy after the deadline.
    func shellDestinationButton(
        tabIdentifier: String,
        timeout: TimeInterval = 5,
        file: StaticString = #filePath,
        line: UInt = #line
    ) -> XCUIElement {
        let budget = UIWaitBudget(timeout: timeout)
        let tabBar = app.tabBars.firstMatch
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
        while budget.remaining > 0 {
            switch budget.shellSurface(
                compactTabBarExists: { tabBar.exists },
                sidebarNavigationBarExists: { self.sidebarNavigationBar.exists }
            ) {
            case .tabBar:
                if budget.exists(tabButton, named: tabIdentifier) { return tabButton }
                if budget.exists(tabElement, named: "\(tabIdentifier) descendant") { return tabElement }
                if budget.exists(tabLabel, named: "\(tabIdentifier) title fallback") {
                    recordTabIdentifierCompatibilityFallback(tabIdentifier)
                    return tabLabel
                }
            case .sidebar:
                if budget.exists(sidebar, named: sidebarIdentifier) { return sidebar }
            case nil:
                _ = revealSidebarIfCollapsed(budget: budget, toggleWait: 1)
            }
            budget.pause()
        }
        recordQueryFailure(
            "Missing \(tabIdentifier) or \(sidebarIdentifier); \(budget.diagnostic)",
            file: file,
            line: line
        )
        return tabButton
    }

    private func recordQueryFailure(
        _ message: String,
        file: StaticString = #filePath,
        line: UInt = #line
    ) {
        let attachment = XCTAttachment(string: message)
        attachment.name = "Bounded query diagnostic"
        attachment.lifetime = .keepAlways
        add(attachment)
        XCTFail(message, file: file, line: line)
    }

    private func recordTabIdentifierCompatibilityFallback(_ identifier: String) {
        let message = "SwiftUI did not expose \(identifier) on the tab-bar "
            + "accessibility tree; using its documented title as an OS compatibility fallback."
        XCTContext.runActivity(named: "Warning: \(message)") { _ in
            print("warning: \(message)")
        }
    }

    /// Checks whether a compact tab is rendered without treating a
    /// label-only SwiftUI accessibility node as a positive-navigation
    /// compatibility fallback.
    func compactTabExists(tabIdentifier: String) -> Bool {
        let tabBar = app.tabBars.firstMatch
        guard tabBar.exists else { return false }

        let identifierMatch = tabBar.descendants(matching: .any)
            .matching(identifier: tabIdentifier)
            .firstMatch
        return identifierMatch.exists
            || tabBar.buttons[tabTitle(for: tabIdentifier)].exists
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
        case "tab.notifications": "Notifications"
        case "tab.settings": "Settings"
        case "tab.scan": "Scan"
        default: identifier
        }
    }

    func renderedShellRoots(timeout: TimeInterval = 8) -> [RenderedShellRoot] {
        let budget = UIWaitBudget(timeout: timeout)
        let tabBar = app.tabBars.firstMatch
        let sidebar = app.buttons
            .matching(NSPredicate(format: "identifier BEGINSWITH %@", "sidebar."))
        while budget.remaining > 0 {
            switch budget.shellSurface(
                compactTabBarExists: { tabBar.exists },
                sidebarNavigationBarExists: { self.sidebarNavigationBar.exists }
            ) {
            case .sidebar:
                let buttons = budget.perform("enumerate sidebar buttons") {
                    sidebar.allElementsBoundByIndex
                } ?? []
                if !buttons.isEmpty {
                    var identifiers = Set<String>()
                    return shellRoots(buttons, surface: .sidebar, budget: budget).filter {
                        identifiers.insert($0.identifier).inserted
                    }
                }
            case .tabBar:
                let buttons = budget.perform("enumerate tab buttons") {
                    tabBar.buttons.allElementsBoundByIndex
                } ?? []
                if !buttons.isEmpty {
                    return shellRoots(buttons, surface: .tabBar, budget: budget)
                }
            case nil:
                if revealSidebarIfCollapsed(budget: budget, toggleWait: 0.5) {
                    continue
                }
            }
            budget.pause()
        }
        recordQueryFailure("No rendered shell roots; \(budget.diagnostic)")
        return []
    }

    private func shellRoots(
        _ buttons: [XCUIElement],
        surface: RenderedShellRoot.Surface,
        budget: UIWaitBudget
    ) -> [RenderedShellRoot] {
        var roots: [RenderedShellRoot] = []
        for button in buttons {
            guard let title = budget.perform("read root label", { button.label }),
                  let identifier = budget.perform("read root identifier", { button.identifier }) else {
                recordQueryFailure("Incomplete rendered shell roots; \(budget.diagnostic)")
                return []
            }
            roots.append(RenderedShellRoot(title: title, identifier: identifier, surface: surface))
        }
        return roots
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

        let modeControls = app.segmentedControls
            .matching(identifier: "navigation.modeControl")
        let modeControl = modeControls.firstMatch
        if expectsModeControl {
            XCTAssertTrue(
                modeControl.waitForExistence(timeout: 5),
                "\(root.title) must show the Two Modes control",
                file: file,
                line: line
            )
            XCTAssertEqual(
                modeControls.count,
                1,
                "\(root.title) must render exactly one Floor/Oversight control",
                file: file,
                line: line
            )
            XCTAssertTrue(modeControl.buttons["Floor"].exists, file: file, line: line)
            XCTAssertTrue(modeControl.buttons["Oversight"].exists, file: file, line: line)
        } else {
            XCTAssertEqual(
                modeControls.count,
                0,
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
