import XCTest

/// UI tests for the login → dashboard flow.
///
/// These tests verify the login screen appears, accepts input, and transitions
/// to the dashboard on successful authentication.
///
/// ## Launch mode
/// Unlike the operator-shell UI tests, these run in the **unauthenticated**
/// bootstrap (`--uitesting-unauthenticated`) so `RootView` renders
/// `LoginView` deterministically. The onboarding / local-network-permission
/// gates are cleared via the volatile `NSArgumentDomain` overrides below —
/// they apply only to this launched process and are never written to the
/// persistent `UserDefaults.standard` plist. The demo `ServiceContainer`
/// keeps the sign-in path off the real network.
@MainActor
final class LoginFlowUITests: PrintFarmerUITestCase {

    override var additionalLaunchArguments: [String] {
        [
            // Literal must match UITestBootstrap.unauthenticatedLaunchArgument
            // (pinned by UITestBootstrapTests). UI test targets run
            // out-of-process and cannot import the app module.
            "--uitesting-unauthenticated",
            // Argument-domain overrides for the two @AppStorage gates in
            // RootView so the unauthenticated app lands on LoginView instead
            // of Onboarding / LocalNetworkPermission. These are ephemeral and
            // do not persist to UserDefaults.standard.
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
