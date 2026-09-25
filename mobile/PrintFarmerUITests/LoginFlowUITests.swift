import XCTest

/// UI tests for the login → dashboard flow.
///
/// These tests verify the login screen appears, accepts input, and transitions
/// to the dashboard on successful authentication.
///
/// ## Launch mode
/// Unlike the operator-shell UI tests, these run in the **unauthenticated**
/// bootstrap (`--uitesting-unauthenticated`) so `RootView` renders
/// `LoginView` deterministically. The onboarding / advanced-controls /
/// local-network-permission gates are cleared via the volatile
/// `NSArgumentDomain` overrides below — they apply only to this launched
/// process and are never written to the persistent `UserDefaults.standard`
/// plist. The demo `ServiceContainer` keeps the sign-in path off the real
/// network.
@MainActor
final class LoginFlowUITests: PrintFarmerUITestCase {

    override var navigationAlertDismissals: [String: String] {
        ["Save Password?": "Not Now"]
    }
    override var retryUnchangedNavigationAlertDismissal: Bool { true }

    override var additionalLaunchArguments: [String] {
        [
            // Literal must match UITestBootstrap.unauthenticatedLaunchArgument
            // (pinned by UITestBootstrapTests). UI test targets run
            // out-of-process and cannot import the app module.
            "--uitesting-unauthenticated",
            // Argument-domain overrides for the two @AppStorage gates in
            // RootView so the unauthenticated app lands on LoginView instead
            // of Onboarding / AdvancedPrinterControls / LocalNetworkPermission.
            // These are ephemeral and do not persist to UserDefaults.standard.
            "-hasSeenAdvancedPrinterControlsPrompt", "YES",
            "-hasSeenOnboarding", "YES",
            "-hasCompletedNetworkPermission", "YES",
            // Force demo mode off deterministically: DemoMode.shared reads
            // this key from UserDefaults.standard at init, and if a prior
            // simulator run left it true, DemoAuthService.restoreSession would
            // silently re-authenticate and hide LoginView. Argument-domain
            // overrides are volatile (init reads it but its didSet does not
            // fire during initialization, so nothing is written back).
            "-isDemoModeActive", "NO"
        ]
    }

    // MARK: - Login Screen Presence

    func testLoginScreenAppears() {
        // The login screen should be the first screen for unauthenticated users
        let loginView = app.otherElements["loginView"]
            .waitForExistence(timeout: 5)
        // If the app uses a different identifier, adjust accordingly.
        // Fallback: check for known login UI elements
        let serverField = app.textFields["serverURLField"]
        let usernameField = app.textFields["usernameField"]
        let passwordField = app.secureTextFields["passwordField"]

        // At least one login element should be visible
        let hasLoginUI = serverField.exists || usernameField.exists || passwordField.exists || loginView
        XCTAssertTrue(hasLoginUI, "Login screen should appear on first launch in test mode")
    }

    // MARK: - Form Interaction

    func testCanTypeInLoginFields() {
        let usernameField = app.textFields["usernameField"]
        XCTAssertTrue(usernameField.waitForExistence(timeout: 5))

        assertKeyboardFocus(on: usernameField)
        usernameField.typeText("admin")
        XCTAssertEqual(usernameField.value as? String, "admin")

        let passwordField = app.secureTextFields["passwordField"]
        XCTAssertTrue(passwordField.waitForExistence(timeout: 3))
        assertKeyboardFocus(on: passwordField)
        passwordField.typeText("password123")
        XCTAssertEqual(passwordField.value as? String, "•••••••••••")
    }

    // MARK: - Login to Operator Shell Transition

    func testNavigationAdapterUsesLoginDismissalPolicy() {
        let alert = ShellNode(.alert, label: "Save Password?", children: [
            ShellNode(.button, label: "Not Now"),
            ShellNode(.button, label: "Save")
        ])
        // iOS 26.5 presents the same prompt as a sheet in the app's own tree (#3032).
        let sheet = ShellNode(.sheet, label: "Save Password?", children: [
            ShellNode(.button, label: "Not Now"),
            ShellNode(.button, label: "Save")
        ])
        let unknown = ShellNode(.alert, label: "Allow access?", children: [
            ShellNode(.button, label: "Not Now")
        ])
        let ready = ShellObservation(ShellNode(.application, children: [
            ShellNode(.tabBar, children: [ShellNode(.button, identifier: "tab.attention")])
        ]))
        let loading = ShellObservation(ShellNode(.application))
        let embedded = ShellObservation(ShellNode(.application, children: [alert]))
        let embeddedUnknown = ShellObservation(ShellNode(.application, children: [unknown]))
        let embeddedSheet = ShellObservation(
            ShellNode(.application, children: [sheet]),
            interruptionSheetTitles: Set(navigationAlertDismissals.keys)
        )
        let scenarios: [(String, [ShellNode?], [ShellObservation], Bool, String, Int)] = [
            ("no interruption", [nil], [ready], true, "none", 0),
            ("separate alert root", [alert, nil], [loading, ready], true, "none", 1),
            ("late alert", [nil, alert, nil], [loading, loading, ready], true, "none", 1),
            ("application-only alert", [nil, nil], [embedded, ready], true, "none", 1),
            ("separate sheet root", [sheet, nil], [loading, ready], true, "none", 1),
            ("late sheet", [nil, sheet, nil], [loading, loading, ready], true, "none", 1),
            ("application-only sheet", [nil, nil], [embeddedSheet, ready], true, "none", 1),
            ("unknown alert", [nil], [embeddedUnknown], true, "interruption rejected by dismissal allowlist", 0),
            ("non-hittable dismissal", [alert], [ready], false, "interruption dismissal is not hittable", 0),
            ("unchanged retry", [alert, alert, nil], [loading, loading, ready], true, "none", 2),
            ("persistent alert", [alert], [ready], true, "interruption did not disappear after dismissal", 2),
            ("changed alert", [alert, nil], [loading, embeddedUnknown], true, "interruption changed after dismissal", 1),
            ("reappearing alert", [alert, nil, alert], [loading, loading, ready], true,
             "interruption reappeared after disappearance", 1)
        ]
        for (name, interruptions, applications, hittable, failure, expectedTaps) in scenarios {
            var clock: TimeInterval = 0
            let budget = UIWaitBudget(timeout: 60, now: { clock })
            var index = -1
            var taps: [String] = []
            let driver = ShellNavigationDriver(
                observeInterruption: { titles in
                    XCTAssertEqual(titles, ["Save Password?"], name)
                    index = min(index + 1, interruptions.count - 1)
                    let interruption = interruptions[index]
                    XCTAssertTrue(interruption == nil || titles.contains(interruption!.label), name)
                    return interruption
                },
                observeApplication: { applications[index] },
                reveal: { _ in XCTFail("Unexpected sidebar reveal: \(name)"); return false },
                leadingEdge: { _ in XCTFail("Unexpected sidebar gesture: \(name)"); return false },
                isDismissalHittable: { _, button in
                    XCTAssertEqual(button.label, "Not Now", name)
                    return hittable
                },
                tapDismissal: { _, button in taps.append(button.label); return true }
            )
            let result = waitForObservedShell(budget: budget, driver: driver, pause: { clock += 0.2 }) {
                $0.isLaunchReady ? true : nil
            }
            XCTAssertEqual(result == true, failure == "none", "\(name): \(budget.shellDiagnostic)")
            XCTAssertEqual(taps, Array(repeating: "Not Now", count: expectedTaps), name)
            XCTAssertLessThan(clock, 4, "\(name) must not consume the navigation or test allowance")
            XCTAssertEqual(budget.shellFailure, failure, name)
        }
    }

    func testLoginTransitionsToOperatorShell() {
        let loginButton = app.buttons["loginButton"]
        XCTAssertTrue(loginButton.waitForExistence(timeout: 5))

        let usernameField = app.textFields["usernameField"]
        XCTAssertTrue(usernameField.waitForExistence(timeout: 3))
        assertKeyboardFocus(on: usernameField)
        usernameField.typeText("admin")

        let passwordField = app.secureTextFields["passwordField"]
        XCTAssertTrue(passwordField.waitForExistence(timeout: 3))
        assertKeyboardFocus(on: passwordField)
        passwordField.typeText("password")

        XCTAssertTrue(loginButton.isEnabled)
        loginButton.tap()

        waitForAuthenticatedShell()
        let attention = shellDestinationButton(
            tabIdentifier: "tab.attention",
            timeout: 10
        )
        XCTAssertTrue(
            attention.exists,
            "Successful authentication should present tab.attention on iPhone or sidebar.attention on iPad"
        )
    }

    private func assertKeyboardFocus(
        on field: XCUIElement,
        file: StaticString = #filePath,
        line: UInt = #line
    ) {
        field.tap()
        var result = waitForKeyboardFocus(on: field, timeout: 3)
        if result != .completed {
            // Xcode 26 can occasionally synthesize a tap without delivering it
            // to the field on a loaded simulator. A second tap is harmless when
            // focus was not acquired and avoids treating that dropped event as
            // an app regression.
            field.tap()
            result = waitForKeyboardFocus(on: field, timeout: 3)
        }
        XCTAssertEqual(
            result,
            .completed,
            "Tapping the field should retain keyboard focus",
            file: file,
            line: line
        )
    }

    private func waitForKeyboardFocus(
        on field: XCUIElement,
        timeout: TimeInterval
    ) -> XCTWaiter.Result {
        let focused = XCTNSPredicateExpectation(
            predicate: NSPredicate(format: "hasKeyboardFocus == true"),
            object: field
        )
        return XCTWaiter.wait(for: [focused], timeout: timeout)
    }
}

@MainActor
final class AdvancedPrinterControlsNotNowUITests: PrintFarmerUITestCase {
    override var additionalLaunchArguments: [String] {
        [
            "--uitesting-unauthenticated",
            "--uitesting-network-permission-complete",
            "-isDemoModeActive", "NO"
        ]
    }

    func testNotNowAdvancesFromOnboardingToLogin() {
        completeOnboarding()

        XCTAssertTrue(
            app.otherElements["advancedPrinterControlsPermissionView"].waitForExistence(timeout: 8),
            "Completing onboarding should advance to the advanced printer controls prompt"
        )

        let notNow = app.buttons["advancedPrinterControls.notNow"]
        XCTAssertTrue(notNow.waitForExistence(timeout: 5))
        notNow.tap()

        let usernameField = app.textFields["usernameField"]
        XCTAssertTrue(
            usernameField.waitForExistence(timeout: 8),
            "Tapping Not Now should mark the prompt seen and continue to login when network permission is already complete"
        )

        relaunchAuthenticatedPreservingState()
        openSettingsFromAccount()

        let advancedControlsToggle = app.switches["settings.advancedPrinterControls"]
        if !advancedControlsToggle.waitForExistence(timeout: 3) {
            app.swipeUp()
        }
        XCTAssertTrue(advancedControlsToggle.waitForExistence(timeout: 3))
        XCTAssertEqual(advancedControlsToggle.value as? String, "0")
    }

    private func completeOnboarding() {
        let skip = app.buttons["Skip"]
        XCTAssertTrue(skip.waitForExistence(timeout: 8))
        skip.tap()
    }

    private func relaunchAuthenticatedPreservingState() {
        app.terminate()
        app = .printFarmerUITest(arguments: [
            "--uitesting",
            "--uitesting-preserve-state",
            "-isDemoModeActive", "NO"
        ])
        app.launchForPrintFarmerUITest()
    }

    private func openSettingsFromAccount() {
        let attention = shellDestinationButton(tabIdentifier: "tab.attention", timeout: 20)
        XCTAssertTrue(attention.exists)
        attention.tap()

        let account = app.buttons["navigation.account"]
        XCTAssertTrue(account.waitForExistence(timeout: 5))
        account.tap()
        XCTAssertTrue(app.descendants(matching: .any)["account.root"].waitForExistence(timeout: 5))

        let settings = app.buttons["account.destination.settings"]
        XCTAssertTrue(settings.waitForExistence(timeout: 5))
        settings.tap()

        XCTAssertTrue(app.navigationBars["Settings"].waitForExistence(timeout: 5))
    }
}

@MainActor
final class AdvancedPrinterControlsEnableUITests: PrintFarmerUITestCase {
    override var additionalLaunchArguments: [String] {
        [
            "--uitesting-unauthenticated",
            "--uitesting-onboarding-seen",
            "--uitesting-network-permission-complete",
            "-isDemoModeActive", "NO"
        ]
    }

    func testEnableTurnsOnControlsForActiveServer() {
        XCTAssertTrue(app.otherElements["advancedPrinterControlsPermissionView"].waitForExistence(timeout: 8))

        let enable = app.buttons["advancedPrinterControls.enable"]
        XCTAssertTrue(enable.waitForExistence(timeout: 5))
        enable.tap()

        let usernameField = app.textFields["usernameField"]
        XCTAssertTrue(usernameField.waitForExistence(timeout: 8))
        relaunchAuthenticatedPreservingState()
        openSettingsFromAccount()

        let advancedControlsToggle = app.switches["settings.advancedPrinterControls"]
        if !advancedControlsToggle.waitForExistence(timeout: 3) {
            app.swipeUp()
        }
        XCTAssertTrue(advancedControlsToggle.waitForExistence(timeout: 3))
        XCTAssertEqual(advancedControlsToggle.value as? String, "1")
    }

    private func relaunchAuthenticatedPreservingState() {
        app.terminate()
        app = .printFarmerUITest(arguments: [
            "--uitesting",
            "--uitesting-preserve-state",
            "-isDemoModeActive", "NO"
        ])
        app.launchForPrintFarmerUITest()
    }

    private func openSettingsFromAccount() {
        let attention = shellDestinationButton(tabIdentifier: "tab.attention", timeout: 20)
        XCTAssertTrue(attention.exists)
        attention.tap()

        let account = app.buttons["navigation.account"]
        XCTAssertTrue(account.waitForExistence(timeout: 5))
        account.tap()
        XCTAssertTrue(app.descendants(matching: .any)["account.root"].waitForExistence(timeout: 5))

        let settings = app.buttons["account.destination.settings"]
        XCTAssertTrue(settings.waitForExistence(timeout: 5))
        settings.tap()

        XCTAssertTrue(app.navigationBars["Settings"].waitForExistence(timeout: 5))
    }
}
