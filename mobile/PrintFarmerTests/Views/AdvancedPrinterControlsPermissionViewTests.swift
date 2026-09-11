import XCTest
@testable import PrintFarmer

@MainActor
final class AdvancedPrinterControlsPermissionViewTests: XCTestCase {
    private var userDefaults: UserDefaults!
    private var suiteName: String!

    override func setUp() async throws {
        try await super.setUp()
        suiteName = "AdvancedPrinterControlsPermissionViewTests-\(UUID().uuidString)"
        userDefaults = UserDefaults(suiteName: suiteName)!
        userDefaults.removePersistentDomain(forName: suiteName)
    }

    override func tearDown() async throws {
        userDefaults.removePersistentDomain(forName: suiteName)
        userDefaults = nil
        suiteName = nil
        try await super.tearDown()
    }

    func testEnableOptInMarksPromptSeenAndEnablesAdvancedControls() throws {
        let registry = ServerRegistry(
            userDefaults: userDefaults,
            migrateLegacyServerURL: false
        )
        _ = try registry.add(
            displayName: "Farm",
            baseURL: URL(string: "https://farm.example.com")!
        )

        AdvancedPrinterControlsPromptState.enableOptIn(
            serverRegistry: registry,
            defaults: userDefaults
        )

        XCTAssertTrue(registry.advancedPrinterControlsEnabled)
        let reloaded = ServerRegistry(
            userDefaults: userDefaults,
            migrateLegacyServerURL: false
        )
        XCTAssertTrue(reloaded.advancedPrinterControlsEnabled)
        XCTAssertTrue(
            userDefaults.bool(
                forKey: AdvancedPrinterControlsPromptState.hasSeenPromptKey
            )
        )
    }

    func testDeferOptInMarksPromptSeenAndLeavesAdvancedControlsOff() throws {
        let registry = ServerRegistry(
            userDefaults: userDefaults,
            migrateLegacyServerURL: false
        )
        _ = try registry.add(
            displayName: "Farm",
            baseURL: URL(string: "https://farm.example.com")!
        )

        AdvancedPrinterControlsPromptState.deferOptIn(defaults: userDefaults)

        XCTAssertFalse(registry.advancedPrinterControlsEnabled)
        let reloaded = ServerRegistry(
            userDefaults: userDefaults,
            migrateLegacyServerURL: false
        )
        XCTAssertFalse(reloaded.advancedPrinterControlsEnabled)
        XCTAssertTrue(
            userDefaults.bool(
                forKey: AdvancedPrinterControlsPromptState.hasSeenPromptKey
            )
        )
    }

    func testUnauthenticatedRouteShowsPromptBeforeOnboarding() {
        XCTAssertEqual(
            UnauthenticatedRootRoute.resolve(
                hasSeenAdvancedPrinterControlsPrompt: false,
                hasSeenOnboarding: false,
                hasCompletedNetworkPermission: false
            ),
            .advancedPrinterControls
        )
    }

    func testUnauthenticatedRouteSkipsPromptAfterSeen() {
        XCTAssertEqual(
            UnauthenticatedRootRoute.resolve(
                hasSeenAdvancedPrinterControlsPrompt: true,
                hasSeenOnboarding: false,
                hasCompletedNetworkPermission: false
            ),
            .onboarding
        )
    }

    func testNotNowLeavesControlsOffAndAllowsNavigationForward() throws {
        let registry = ServerRegistry(
            userDefaults: userDefaults,
            migrateLegacyServerURL: false
        )
        _ = try registry.add(
            displayName: "Farm",
            baseURL: URL(string: "https://farm.example.com")!
        )

        AdvancedPrinterControlsPromptState.deferOptIn(defaults: userDefaults)

        XCTAssertFalse(registry.advancedPrinterControlsEnabled)
        XCTAssertEqual(
            UnauthenticatedRootRoute.resolve(
                hasSeenAdvancedPrinterControlsPrompt: userDefaults.bool(
                    forKey: AdvancedPrinterControlsPromptState.hasSeenPromptKey
                ),
                hasSeenOnboarding: false,
                hasCompletedNetworkPermission: false
            ),
            .onboarding
        )
    }
}
