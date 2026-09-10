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

    func perform<T>(_ operation: String, _ body: () throws -> T) rethrows -> T? {
        guard remaining > 0 else { return nil }
        lastOperation = operation
        let result = try body()
        return remaining > 0 ? result : nil
    }

    func exists(_ element: XCUIElement, named identifier: String) -> Bool {
        perform("exists: \(identifier)") { element.exists } == true
    }

    private(set) var lastShellObservation = "not observed"

    func waitForShell<T>(
        observe: () throws -> ShellObservation,
        resolve: (ShellObservation) -> T?,
        reveal: (ShellNode) -> Bool,
        leadingEdge: (ShellNode) -> Bool,
        pause: (() -> Void)? = nil
    ) rethrows -> T? {
        var revealedAt: TimeInterval?
        var usedLeadingEdge = false
        while remaining > 0 {
            guard let observation = try perform("shell snapshot", observe) else { return nil }
            lastShellObservation = observation.diagnostic
            if let result = perform("resolve observed shell", { resolve(observation) }) ?? nil {
                return result
            }
            if case .collapsed(let toggle) = observation.state {
                if revealedAt == nil {
                    if perform("reveal observed sidebar", { reveal(toggle) }) == true {
                        revealedAt = now()
                    }
                } else if toggle.label == "Show Sidebar",
                          now() - (revealedAt ?? now()) >= 1, !usedLeadingEdge {
                    // A second observation must still advertise Show Sidebar.
                    // Never toggle a now-visible sidebar closed.
                    usedLeadingEdge = true
                    _ = perform("reveal observed sidebar from edge", { leadingEdge(toggle) })
                }
            }
            if remaining > 0 {
                if let pause { pause() } else { self.pause() }
            }
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

    /// Values copied from XCTest's public snapshot API. All tree traversal below
    /// is local: identifier misses never initiate additional accessibility queries.
    @MainActor
    struct ShellNode {
        let type: XCUIElement.ElementType
        var identifier = ""
        var label = ""
        var enabled = true
        var frame = CGRect(x: 0, y: 0, width: 100, height: 100)
        var children: [ShellNode] = []

        init(
            _ type: XCUIElement.ElementType,
            identifier: String = "",
            label: String = "",
            enabled: Bool = true,
            frame: CGRect = CGRect(x: 0, y: 0, width: 100, height: 100),
            children: [ShellNode] = []
        ) {
            self.type = type
            self.identifier = identifier
            self.label = label
            self.enabled = enabled
            self.frame = frame
            self.children = children
        }

        init(_ snapshot: any XCUIElementSnapshot) {
            type = snapshot.elementType
            identifier = snapshot.identifier
            label = snapshot.label
            enabled = snapshot.isEnabled
            frame = snapshot.frame
            children = snapshot.children.map(ShellNode.init)
        }

        var descendants: [ShellNode] { children.flatMap { [$0] + $0.descendants } }

        var liveIdentityPredicate: NSPredicate {
            // Badge counts can change after capture; a stable ID owns identity.
            identifier.isEmpty
                ? NSPredicate(format: "elementType == %lu AND identifier == '' AND label == %@",
                              type.rawValue, label)
                : NSPredicate(format: "elementType == %lu AND identifier == %@",
                              type.rawValue, identifier)
        }

        func liveIdentityPredicate(allowingPromotionTo expectedIdentifier: String?) -> NSPredicate {
            guard identifier.isEmpty, let expectedIdentifier else { return liveIdentityPredicate }
            // SwiftUI can attach the stable ID after the initial tab snapshot.
            return NSPredicate(
                format: "elementType == %lu AND (identifier == %@ OR (identifier == '' AND label == %@))",
                type.rawValue, expectedIdentifier, label
            )
        }
    }

    @MainActor
    struct ShellObservation {
        enum State {
            case notReady
            case compact([ShellNode])
            case sidebar([ShellNode])
            case collapsed(ShellNode)
        }

        struct Destination {
            let node: ShellNode
            let surface: RenderedShellRoot.Surface
            var titleFallback = false
            var promotionIdentifier: String?
        }

        let state: State

        init(_ root: ShellNode) {
            let visible = root.descendants.filter {
                !$0.frame.isEmpty && $0.frame.intersects(root.frame)
            }
            if visible.contains(where: {
                $0.identifier == "launchSplash" || $0.identifier == "navigation.shellLoading"
            }) {
                state = .notReady
            } else if let tabBar = visible.first(where: { $0.type == .tabBar }) {
                let nodes = tabBar.descendants.filter {
                    !$0.frame.isEmpty && $0.frame.intersects(tabBar.frame)
                        && $0.frame.intersects(root.frame)
                }
                state = nodes.isEmpty ? .notReady : .compact(nodes)
            } else if visible.contains(where: {
                $0.type == .navigationBar && $0.identifier == "PrintFarmer"
            }) {
                let buttons = visible.filter {
                    $0.type == .button && $0.identifier.hasPrefix("sidebar.")
                }
                state = buttons.isEmpty ? .notReady : .sidebar(buttons)
            } else if let toggle = visible.filter({ $0.type == .navigationBar })
                .flatMap(\.descendants).first(where: {
                    $0.type == .button && $0.enabled
                        && ["Sidebar", "Toggle Sidebar", "Show Sidebar"].contains($0.label)
                        && !$0.frame.isEmpty && $0.frame.intersects(root.frame)
                }) {
                state = .collapsed(toggle)
            } else {
                state = .notReady
            }
        }

        func destination(tab: String, sidebar: String, title: String) -> Destination? {
            switch state {
            case .compact(let nodes):
                if let node = nodes.first(where: { $0.enabled && $0.type == .button && $0.identifier == tab })
                    ?? nodes.first(where: { $0.enabled && $0.identifier == tab }) {
                    return Destination(node: node, surface: .tabBar)
                }
                if let node = nodes.first(where: {
                    $0.enabled && $0.type == .button && $0.label == title
                        && ($0.identifier.isEmpty || $0.identifier == title)
                }) {
                    return Destination(
                        node: node, surface: .tabBar, titleFallback: true,
                        promotionIdentifier: node.identifier.isEmpty ? tab : nil
                    )
                }
            case .sidebar(let nodes):
                if let node = nodes.first(where: { $0.identifier == sidebar && $0.enabled }) {
                    return Destination(node: node, surface: .sidebar)
                }
            case .notReady, .collapsed:
                break
            }
            return nil
        }

        var roots: [RenderedShellRoot] {
            let nodes: [ShellNode]
            let surface: RenderedShellRoot.Surface
            switch state {
            case .compact(let elements):
                nodes = elements.filter { $0.type == .button }
                surface = .tabBar
            case .sidebar(let elements):
                nodes = elements
                surface = .sidebar
            case .notReady, .collapsed:
                return []
            }
            var identifiers = Set<String>()
            return nodes.map {
                RenderedShellRoot(title: $0.label, identifier: $0.identifier, surface: surface)
            }.filter { surface == .tabBar || identifiers.insert($0.key).inserted }
        }

        var diagnostic: String {
            switch state {
            case .notReady: "not ready"
            case .collapsed(let toggle): "collapsed; toggle=\(toggle.label)"
            case .compact: "compact; roots=\(roots.map(\.key))"
            case .sidebar: "sidebar; roots=\(roots.map(\.key))"
            }
        }
    }

@MainActor
final class UIWaitBudgetTests: XCTestCase {
    private func compact(_ nodes: [ShellNode]) -> ShellObservation {
        ShellObservation(ShellNode(.application, children: [
            ShellNode(.tabBar, children: nodes)
        ]))
    }

    private func sidebar() -> ShellObservation {
        ShellObservation(ShellNode(.application, children: [
            ShellNode(.navigationBar, identifier: "PrintFarmer"),
            ShellNode(.button, identifier: "sidebar.overview", label: "Overview")
        ]))
    }

    private func collapsed() -> ShellObservation {
        ShellObservation(ShellNode(.application, children: [
            ShellNode(.navigationBar, children: [
                ShellNode(.button, label: "Show Sidebar")
            ])
        ]))
    }

    func testLoadingThenEmptyCompactRootThenDestinationUsesOnlySnapshots() {
        var clock: TimeInterval = 0
        var operations: [String] = []
        let budget = UIWaitBudget(timeout: 5, now: { clock })
        var observations = [
            ShellObservation(ShellNode(.application, children: [
                ShellNode(.other, identifier: "navigation.shellLoading")
            ])),
            compact([]),
            compact([ShellNode(.button, identifier: "tab.farm", label: "Farm")])
        ]
        let destination = budget.waitForShell(
            observe: {
                operations.append("snapshot")
                clock += 0.5
                return observations.removeFirst()
            },
            resolve: { $0.destination(tab: "tab.farm", sidebar: "sidebar.farm", title: "Farm") },
            reveal: { _ in
                operations.append("absent sidebar toggle")
                clock += 6
                return false
            },
            leadingEdge: { _ in XCTFail("No collapsed sidebar was observed"); return false },
            pause: { clock += 0.2 }
        )
        XCTAssertEqual(destination?.node.identifier, "tab.farm")
        XCTAssertEqual(destination?.surface, .tabBar)
        XCTAssertEqual(operations, ["snapshot", "snapshot", "snapshot"])
        XCTAssertEqual(budget.remaining, 3.1, accuracy: 0.001)
    }

    func testLoadingThenCollapsedThenVisibleSidebarRequiresPositiveToggleEvidence() {
        var clock: TimeInterval = 0
        var reveals: [String] = []
        var observations = [ShellObservation(ShellNode(.application)), collapsed(), sidebar()]
        let budget = UIWaitBudget(timeout: 5, now: { clock })
        let destination = budget.waitForShell(
            observe: { clock += 0.5; return observations.removeFirst() },
            resolve: {
                $0.destination(tab: "tab.oversight", sidebar: "sidebar.overview", title: "Oversight")
            },
            reveal: { reveals.append($0.label); return true },
            leadingEdge: { _ in XCTFail("The sidebar opened after its toggle"); return false },
            pause: { clock += 0.2 }
        )
        XCTAssertEqual(destination?.node.identifier, "sidebar.overview")
        XCTAssertEqual(destination?.surface, .sidebar)
        XCTAssertEqual(reveals, ["Show Sidebar"])
    }

    func testSnapshotOverrunDoesNotResolveRevealOrPause() {
        var clock: TimeInterval = 0
        let budget = UIWaitBudget(timeout: 2, now: { clock })
        let result: Bool? = budget.waitForShell(
            observe: { clock = 3; return self.collapsed() },
            resolve: { _ in XCTFail("Expired snapshot"); return true },
            reveal: { _ in XCTFail("Expired snapshot"); return true },
            leadingEdge: { _ in XCTFail("Expired snapshot"); return true },
            pause: { XCTFail("Expired deadline") }
        )
        XCTAssertNil(result)
        XCTAssertEqual(budget.lastOperation, "shell snapshot")
        XCTAssertEqual(budget.lastShellObservation, "not observed")
    }

    func testTitleFallbackAndIdentifierPriorityAreResolvedInTheSameTree() {
        let observation = compact([
            ShellNode(.button, label: "Farm"),
            ShellNode(.button, identifier: "tab.farm", label: "Farm"),
            ShellNode(.button, label: "Oversight")
        ])
        let farm = observation.destination(tab: "tab.farm", sidebar: "sidebar.farm", title: "Farm")
        XCTAssertEqual(farm?.node.identifier, "tab.farm")
        XCTAssertEqual(farm?.titleFallback, false)
        let oversight = observation.destination(
            tab: "tab.oversight", sidebar: "sidebar.overview", title: "Oversight"
        )
        XCTAssertEqual(oversight?.node.label, "Oversight")
        XCTAssertEqual(oversight?.surface, .tabBar)
        XCTAssertEqual(oversight?.titleFallback, true)
    }

    func testAnyTypeIdentifierIsPreservedAndWrongIdentifiedTitleIsNotAFallback() {
        let observation = compact([
            ShellNode(.other, identifier: "tab.farm", label: "Farm"),
            ShellNode(.button, identifier: "tab.jobs", label: "Oversight")
        ])
        XCTAssertEqual(observation.destination(
            tab: "tab.farm", sidebar: "sidebar.farm", title: "Farm"
        )?.node.type, .other)
        XCTAssertNil(observation.destination(
            tab: "tab.oversight", sidebar: "sidebar.overview", title: "Oversight"
        ))
    }

    func testLiveIdentitySurvivesBadgeLabelMutationButRejectsWrongIDOrType() {
        let captured = ShellNode(.button, identifier: "sidebar.farm", label: "Farm, 3 ready")
        var live: [String: Any] = [
            "elementType": XCUIElement.ElementType.button.rawValue,
            "identifier": "sidebar.farm",
            "label": "Farm, 3 ready"
        ]
        XCTAssertTrue(captured.liveIdentityPredicate.evaluate(with: live))
        live["label"] = "Farm, 4 ready"
        XCTAssertTrue(captured.liveIdentityPredicate.evaluate(with: live))
        live["identifier"] = "sidebar.tasks"
        XCTAssertFalse(captured.liveIdentityPredicate.evaluate(with: live))
        live["identifier"] = "sidebar.farm"
        live["elementType"] = XCUIElement.ElementType.staticText.rawValue
        XCTAssertFalse(captured.liveIdentityPredicate.evaluate(with: live))
    }

    func testIdentifierlessLiveFallbackStillRequiresItsCapturedTitleAndType() {
        let captured = ShellNode(.button, label: "Farm")
        var live: [String: Any] = [
            "elementType": XCUIElement.ElementType.button.rawValue,
            "identifier": "",
            "label": "Farm"
        ]
        XCTAssertTrue(captured.liveIdentityPredicate.evaluate(with: live))
        live["label"] = "Tasks"
        XCTAssertFalse(captured.liveIdentityPredicate.evaluate(with: live))
        live["label"] = "Farm"
        live["identifier"] = "tab.tasks"
        XCTAssertFalse(captured.liveIdentityPredicate.evaluate(with: live))
        live["identifier"] = ""
        live["elementType"] = XCUIElement.ElementType.staticText.rawValue
        XCTAssertFalse(captured.liveIdentityPredicate.evaluate(with: live))
    }

    func testIdentifierlessDestinationPromotesOnlyToItsExpectedIDWithoutBindingOldLabel() throws {
        let destination = try XCTUnwrap(compact([
            ShellNode(.button, label: "Tasks")
        ]).destination(tab: "tab.tasks", sidebar: "sidebar.tasks", title: "Tasks"))
        XCTAssertEqual(destination.promotionIdentifier, "tab.tasks")
        let predicate = destination.node.liveIdentityPredicate(
            allowingPromotionTo: destination.promotionIdentifier
        )
        var live: [String: Any] = [
            "elementType": XCUIElement.ElementType.button.rawValue,
            "identifier": "", "label": "Tasks"
        ]
        XCTAssertTrue(predicate.evaluate(with: live))
        live["identifier"] = "tab.tasks"
        live["label"] = "Tasks, 4 pending"
        XCTAssertTrue(predicate.evaluate(with: live))
        live["identifier"] = "tab.inventory"
        live["label"] = "Tasks"
        XCTAssertFalse(predicate.evaluate(with: live))
        live["identifier"] = ""
        live["label"] = "Inventory"
        XCTAssertFalse(predicate.evaluate(with: live))
        live["identifier"] = "tab.tasks"
        live["elementType"] = XCUIElement.ElementType.staticText.rawValue
        XCTAssertFalse(predicate.evaluate(with: live))
    }

    func testIdentifiedDestinationNeverFallsBackOrChangesItsStableIdentity() {
        let node = ShellNode(.button, identifier: "tab.tasks", label: "Tasks")
        let predicate = node.liveIdentityPredicate(allowingPromotionTo: "tab.inventory")
        XCTAssertTrue(predicate.evaluate(with: [
            "elementType": XCUIElement.ElementType.button.rawValue,
            "identifier": "tab.tasks", "label": "Tasks, changed badge"
        ]))
        for identifier in ["", "tab.inventory"] {
            XCTAssertFalse(predicate.evaluate(with: [
                "elementType": XCUIElement.ElementType.button.rawValue,
                "identifier": identifier, "label": "Tasks"
            ]))
        }
    }

    func testOffscreenDisabledAndUnscopedNodesDoNotAuthorizeNavigation() {
        let observation = compact([
            ShellNode(.button, identifier: "tab.farm", label: "Farm", enabled: false),
            ShellNode(.button, identifier: "tab.oversight", label: "Oversight",
                      frame: CGRect(x: 1000, y: 0, width: 44, height: 44))
        ])
        XCTAssertNil(observation.destination(tab: "tab.farm", sidebar: "sidebar.farm", title: "Farm"))
        XCTAssertNil(observation.destination(
            tab: "tab.oversight", sidebar: "sidebar.overview", title: "Oversight"
        ))
        let unscoped = ShellObservation(ShellNode(.application, children: [
            ShellNode(.button, label: "Show Sidebar"),
            ShellNode(.button, identifier: "tab.farm", label: "Farm")
        ]))
        if case .notReady = unscoped.state {} else { XCTFail("No navigation surface exists") }
    }

    func testLoadingMarkerOverridesUnreadyNavigationChrome() {
        let observation = ShellObservation(ShellNode(.application, children: [
            ShellNode(.other, identifier: "launchSplash"),
            ShellNode(.tabBar, children: [ShellNode(.button, identifier: "tab.farm", label: "Farm")]),
            ShellNode(.navigationBar, children: [ShellNode(.button, label: "Show Sidebar")])
        ]))
        XCTAssertNil(observation.destination(tab: "tab.farm", sidebar: "sidebar.farm", title: "Farm"))
        if case .notReady = observation.state {} else { XCTFail("Startup is not a ready shell") }
    }

    func testRootEnumerationPreservesDisabledAndDuplicateCompactRootsForAssertions() {
        let farm = ShellNode(.button, identifier: "tab.farm", label: "Farm")
        let observation = compact([
            farm, farm,
            ShellNode(.button, identifier: "tab.tasks", label: "Tasks", enabled: false)
        ])
        XCTAssertEqual(observation.roots.map(\.identifier), ["tab.farm", "tab.farm", "tab.tasks"])
        XCTAssertNil(observation.destination(tab: "tab.tasks", sidebar: "sidebar.tasks", title: "Tasks"))
    }

    func testNonHittableCompactDestinationWaitsWithoutProbingSidebar() {
        var clock: TimeInterval = 0
        let budget = UIWaitBudget(timeout: 5, now: { clock })
        var hitTests = 0
        let result = budget.waitForShell(
            observe: {
                clock += 0.5
                return self.compact([ShellNode(.button, identifier: "tab.farm", label: "Farm")])
            },
            resolve: { observation -> ShellObservation.Destination? in
                guard let destination = observation.destination(
                    tab: "tab.farm", sidebar: "sidebar.farm", title: "Farm"
                ) else { return nil }
                let hittable = budget.perform("live hit test") { hitTests += 1; return hitTests == 2 }
                return hittable == true ? destination : nil
            },
            reveal: { _ in XCTFail("An obstructed compact destination is not a sidebar"); return false },
            leadingEdge: { _ in XCTFail("No sidebar gesture"); return false },
            pause: { clock += 0.2 }
        )
        XCTAssertEqual(hitTests, 2)
        XCTAssertEqual(result?.node.identifier, "tab.farm")
        XCTAssertEqual(budget.remaining, 3.8, accuracy: 0.001)
    }

    func testTitleFallbackDoesNotSpendBudgetOnMissingIdentifierQueries() {
        var clock: TimeInterval = 0
        let budget = UIWaitBudget(timeout: 5, now: { clock })
        var observations = 0
        let destination = budget.waitForShell(
            observe: {
                observations += 1
                clock += 4
                return self.compact([ShellNode(.button, label: "Oversight")])
            },
            resolve: { $0.destination(tab: "tab.oversight", sidebar: "sidebar.overview", title: "Oversight") },
            reveal: { _ in XCTFail("No sidebar query"); return false },
            leadingEdge: { _ in XCTFail("No gesture"); return false },
            pause: { XCTFail("Fallback is already in the captured tree") }
        )
        XCTAssertEqual(observations, 1)
        XCTAssertEqual(destination?.titleFallback, true)
        XCTAssertEqual(budget.remaining, 1)
    }

    func testPositiveMatchStillMustPassLiveResolutionBeforeDeadline() {
        var clock: TimeInterval = 0
        let budget = UIWaitBudget(timeout: 2, now: { clock })
        var reveals = 0
        let result: Bool? = budget.waitForShell(
            observe: { clock += 1; return self.collapsed() },
            resolve: { _ in clock += 2; return true },
            reveal: { _ in reveals += 1; return true },
            leadingEdge: { _ in reveals += 1; return true },
            pause: { XCTFail("No pause after overrun") }
        )
        XCTAssertNil(result)
        XCTAssertEqual(reveals, 0)
    }

    func testRevealOverrunNeverStartsGestureOrAnotherObservation() {
        var clock: TimeInterval = 0
        let budget = UIWaitBudget(timeout: 2, now: { clock })
        var observations = 0
        let result: Bool? = budget.waitForShell(
            observe: { observations += 1; return self.collapsed() },
            resolve: { _ in nil },
            reveal: { _ in clock = 3; return true },
            leadingEdge: { _ in XCTFail("Deadline elapsed"); return true },
            pause: { XCTFail("Deadline elapsed") }
        )
        XCTAssertNil(result)
        XCTAssertEqual(observations, 1)
    }

    func testPersistentPositiveShowSidebarAllowsOnlyOneTapAndOneEdgeGesture() {
        var clock: TimeInterval = 0
        let budget = UIWaitBudget(timeout: 5, now: { clock })
        var taps = 0
        var gestures = 0
        let result: Bool? = budget.waitForShell(
            observe: { clock += 0.5; return self.collapsed() },
            resolve: { _ in nil },
            reveal: { _ in taps += 1; return true },
            leadingEdge: { _ in gestures += 1; return true },
            pause: { clock += 0.2 }
        )
        XCTAssertNil(result)
        XCTAssertEqual(taps, 1)
        XCTAssertEqual(gestures, 1)
    }

    func testSnapshotErrorIsPropagatedWithoutFallback() {
        enum SnapshotFailure: Error { case unavailable }
        let budget = UIWaitBudget(timeout: 5)
        XCTAssertThrowsError(try budget.waitForShell(
            observe: { throw SnapshotFailure.unavailable },
            resolve: { _ -> Bool? in XCTFail("No snapshot"); return nil },
            reveal: { _ in XCTFail("No snapshot"); return true },
            leadingEdge: { _ in XCTFail("No snapshot"); return true }
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
        let budget = UIWaitBudget(timeout: timeout)
        return waitForObservedShell(budget: budget) { observation in
            switch observation.state {
            case .sidebar: true
            case .compact: false
            case .notReady, .collapsed: nil
            }
        } ?? false
    }

    @discardableResult
    func revealSidebarFromLeadingEdge(timeout: TimeInterval = 3) -> Bool {
        revealSidebarFromLeadingEdge(budget: UIWaitBudget(timeout: timeout))
    }

    private func revealSidebarFromLeadingEdge(budget: UIWaitBudget) -> Bool {
        guard performSidebarLeadingEdge(budget: budget) else { return false }
        return budget.waitFor(sidebarNavigationBar, named: "sidebar navigation bar", upTo: budget.remaining)
    }

    private func performSidebarLeadingEdge(budget: UIWaitBudget) -> Bool {
        budget.perform("leading-edge sidebar gesture", {
            let window = app.windows.firstMatch
            let start = window.coordinate(withNormalizedOffset: CGVector(dx: 0.01, dy: 0.5))
            let end = window.coordinate(withNormalizedOffset: CGVector(dx: 0.35, dy: 0.5))
            start.press(forDuration: 0.1, thenDragTo: end)
            return true
        }) == true
    }

    func observedElement(
        _ node: ShellNode, within scope: XCUIElementQuery,
        allowingPromotionTo expectedIdentifier: String? = nil
    ) -> XCUIElement {
        scope.matching(node.liveIdentityPredicate(allowingPromotionTo: expectedIdentifier)).firstMatch
    }

    private func observedToggle(_ node: ShellNode) -> XCUIElement {
        observedElement(node, within: app.navigationBars.descendants(matching: .button))
    }

    private func waitForObservedShell<T>(
        budget: UIWaitBudget,
        file: StaticString = #filePath,
        line: UInt = #line,
        resolve: (ShellObservation) -> T?
    ) -> T? {
        do {
            return try budget.waitForShell(
                observe: { ShellObservation(ShellNode(try self.app.snapshot())) },
                resolve: resolve,
                reveal: { node in
                    let toggle = self.observedToggle(node)
                    guard budget.perform("hittable observed sidebar toggle", {
                        toggle.isHittable
                    }) == true else { return false }
                    return budget.perform("tap observed sidebar toggle", {
                        toggle.tap()
                        return true
                    }) == true
                },
                leadingEdge: { node in
                    guard budget.perform("hittable observed Show Sidebar", {
                        self.observedToggle(node).isHittable
                    }) == true else { return false }
                    return self.performSidebarLeadingEdge(budget: budget)
                }
            )
        } catch {
            recordQueryFailure(
                "Shell snapshot failed: \(error); \(budget.diagnostic); "
                    + "last snapshot: \(budget.lastShellObservation)",
                file: file, line: line
            )
            return nil
        }
    }

    /// Adaptive locator for a shell destination using its shipped identifier.
    /// Returns the iPhone tab-bar button when a compact tab bar is on screen,
    /// otherwise the iPad `NavigationSplitView` sidebar button — revealing a
    /// collapsed sidebar via the system toggle if needed.
    ///
    /// Resolve layout and ID/title compatibility from one public snapshot.
    /// Startup is not evidence of a collapsed sidebar; only a positively
    /// observed toggle can initiate reveal work.
    ///
    /// - Parameters:
    ///   - tabIdentifier: The compact tab identifier (e.g. `tab.attention`).
    ///     The matching iPad identifier is derived as `sidebar.attention`.
    ///   - timeout: Maximum time to wait for either surface.
    /// - Returns: The located `XCUIElement`. Failure is recorded here without
    ///   resolving another remote element or hierarchy after the deadline.
    func shellDestinationButton(
        tabIdentifier: String,
        sidebarIdentifier: String? = nil,
        timeout: TimeInterval = 5,
        file: StaticString = #filePath,
        line: UInt = #line
    ) -> XCUIElement {
        let budget = UIWaitBudget(timeout: timeout)
        let sidebarIdentifier = sidebarIdentifier ?? tabIdentifier.replacingOccurrences(
            of: "tab.",
            with: "sidebar."
        )
        if let element = waitForObservedShell(budget: budget, file: file, line: line, resolve: { observation in
            guard let destination = observation.destination(
                tab: tabIdentifier, sidebar: sidebarIdentifier, title: self.tabTitle(for: tabIdentifier)
            ) else { return nil as XCUIElement? }
            let scope = destination.surface == .tabBar
                ? self.app.tabBars.descendants(matching: destination.node.type)
                : self.app.descendants(matching: destination.node.type)
            let element = self.observedElement(
                destination.node, within: scope, allowingPromotionTo: destination.promotionIdentifier
            )
            guard budget.perform("hittable observed \(destination.node.identifier)", {
                element.isHittable
            }) == true else { return nil }
            if destination.titleFallback {
                self.recordTabIdentifierCompatibilityFallback(tabIdentifier)
            }
            return element
        }) { return element }
        recordQueryFailure(
            "Missing \(tabIdentifier) or \(sidebarIdentifier); \(budget.diagnostic); "
                + "last snapshot: \(budget.lastShellObservation)",
            file: file,
            line: line
        )
        return app.tabBars.buttons[tabIdentifier]
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
        if let roots = waitForObservedShell(budget: budget, resolve: { observation in
            let roots = observation.roots
            return roots.isEmpty ? nil : roots
        }) { return roots }
        recordQueryFailure(
            "No rendered shell roots; \(budget.diagnostic); last snapshot: \(budget.lastShellObservation)"
        )
        return []
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
