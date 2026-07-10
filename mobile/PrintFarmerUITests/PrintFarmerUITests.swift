import XCTest

/// Base class for all PrintFarmer UI tests.
///
/// Launches the app with `--uitesting` so the app switches to the
/// deterministic bootstrap. By default this is the **authenticated**
/// operator-shell mode. Subclasses that need a different deterministic
/// launch mode (e.g. the unauthenticated login flow) override
/// `additionalLaunchArguments`; those are applied before the app launches.
class PrintFarmerUITestCase: XCTestCase {

    var app: XCUIApplication!

    /// Extra launch arguments contributed by a subclass, applied before the
    /// app launches. Base tests run in the authenticated operator-shell
    /// bootstrap; override to select a different explicit launch mode.
    var additionalLaunchArguments: [String] { [] }

    override func setUp() {
        super.setUp()
        continueAfterFailure = false
        app = XCUIApplication()
        app.launchArguments.append("--uitesting")
        app.launchArguments.append(contentsOf: additionalLaunchArguments)
        app.launch()
    }

    override func tearDown() {
        app = nil
        super.tearDown()
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
}
