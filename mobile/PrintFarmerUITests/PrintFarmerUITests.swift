import XCTest

/// Base class for all PrintFarmer UI tests.
///
/// Launches the app with `--uitesting` flag so the app can switch to mock mode.
/// Subclasses should override `setUp()` to add additional launch arguments.
///
/// ## Connecting to a Mock Server
/// The `--uitesting` launch argument signals the app to use mock/stub data.
/// Lambert is building a separate mock server; when ready, add:
///   `app.launchArguments.append("--mock-server-url=http://localhost:8080")`
/// The app's `ServiceContainer` should check for these arguments at init time.
class PrintFarmerUITestCase: XCTestCase {

    var app: XCUIApplication!

    override func setUp() {
        super.setUp()
        continueAfterFailure = false
        app = XCUIApplication()
        app.launchArguments.append("--uitesting")
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
