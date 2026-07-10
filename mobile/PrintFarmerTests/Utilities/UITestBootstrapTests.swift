import XCTest
@testable import PrintFarmer

/// Tests for the launch-mode decision that gates the UI-test bootstrap
/// (issue #706). The bootstrap is production-safe: it only activates
/// when `--uitesting` is present in `CommandLine.arguments`, and it uses
/// a dedicated `UserDefaults` suite so nothing leaks into normal
/// launches.
@MainActor
final class UITestBootstrapTests: XCTestCase {

    // MARK: - Launch-mode decision

    func test_isEnabled_returnsFalse_forEmptyArgs() {
        XCTAssertFalse(UITestBootstrap.isEnabled(in: []))
    }

    func test_isEnabled_returnsFalse_forNormalLaunchArgs() {
        XCTAssertFalse(UITestBootstrap.isEnabled(in: ["/path/to/PrintFarmer.app/PrintFarmer"]))
        XCTAssertFalse(UITestBootstrap.isEnabled(in: ["Xcode", "-NSDocumentRevisionsDebugMode", "YES"]))
    }

    func test_isEnabled_returnsTrue_whenLaunchArgumentPresent() {
        XCTAssertTrue(UITestBootstrap.isEnabled(in: ["--uitesting"]))
        XCTAssertTrue(UITestBootstrap.isEnabled(in: ["/App", "--uitesting", "-extra"]))
    }

    func test_launchArgument_matchesUITestsHarness() {
        // The value is contract with PrintFarmerUITests.PrintFarmerUITestCase.
        // Changing it silently would break every UI test.
        XCTAssertEqual(UITestBootstrap.launchArgument, "--uitesting")
    }

    // MARK: - Bundle wiring

    func test_makeBundle_seedsActiveServer() throws {
        let defaults = try makeEphemeralDefaults()
        let bundle = UITestBootstrap.makeBundle(defaults: defaults)

        XCTAssertFalse(bundle.serverRegistry.servers.isEmpty,
                       "Bootstrap must register at least one server so RootView skips AddFirstServerView")
        XCTAssertNotNil(bundle.serverRegistry.activeServerID,
                        "Bootstrap must select an active server")
        XCTAssertEqual(bundle.serverRegistry.activeServer?.id,
                       bundle.serverRegistry.servers.first?.id)
    }

    func test_makeBundle_marksAuthenticated_withDemoUser() throws {
        let defaults = try makeEphemeralDefaults()
        let bundle = UITestBootstrap.makeBundle(defaults: defaults)

        XCTAssertTrue(bundle.authViewModel.isAuthenticated,
                      "ContentView is only rendered when isAuthenticated is true")
        XCTAssertNotNil(bundle.authViewModel.currentUser)
        XCTAssertEqual(bundle.authViewModel.currentUser?.id, DemoData.demoUser.id)
        // `hasCheckedAuth` gates RootView past the launch splash.
        // Without it the app renders the launch screen forever.
        // (Verified indirectly by the second restoreSession call being a no-op below.)
    }

    func test_makeBundle_usesDemoServices() throws {
        let defaults = try makeEphemeralDefaults()
        let bundle = UITestBootstrap.makeBundle(defaults: defaults)

        // Demo services keep the operator shell fully renderable on a
        // fresh simulator without hitting the network.
        XCTAssertTrue(bundle.services.authService is DemoAuthService)
        XCTAssertTrue(bundle.services.printerService is DemoPrinterService)
        XCTAssertNil(bundle.services.apiClient,
                     "Demo container must not carry a live APIClient")
    }

    func test_restoreSession_isNoOp_afterBootstrap() async throws {
        let defaults = try makeEphemeralDefaults()
        let bundle = UITestBootstrap.makeBundle(defaults: defaults)

        let userBefore = bundle.authViewModel.currentUser
        XCTAssertTrue(bundle.authViewModel.isAuthenticated)

        // The RootView `.task` calls `restoreSession()` after init. It
        // must not clobber the pre-bootstrapped authenticated state
        // (DemoAuthService.restoreSession returns nil when
        // DemoMode.shared.isActive is false, which is the case here).
        await bundle.authViewModel.restoreSession()

        XCTAssertTrue(bundle.authViewModel.isAuthenticated,
                      "restoreSession must not de-authenticate a UI-test bootstrapped session")
        XCTAssertEqual(bundle.authViewModel.currentUser?.id, userBefore?.id)
    }

    // MARK: - Isolation from production state

    func test_makeBundle_doesNotActivateDemoModeSingleton() throws {
        // Preserve/restore whatever the running process had so unit
        // tests don't accidentally flip the shared singleton.
        let wasActive = DemoMode.shared.isActive
        defer { if wasActive { DemoMode.shared.activate() } else { DemoMode.shared.deactivate() } }
        DemoMode.shared.deactivate()

        let defaults = try makeEphemeralDefaults()
        _ = UITestBootstrap.makeBundle(defaults: defaults)

        XCTAssertFalse(DemoMode.shared.isActive,
                       "Bootstrap must not touch the shared DemoMode singleton; that state persists to real UserDefaults")
    }

    func test_makeBundle_doesNotWriteToStandardUserDefaults() throws {
        let key = ServerRegistry.storageKey
        let before = UserDefaults.standard.data(forKey: key)

        let defaults = try makeEphemeralDefaults()
        _ = UITestBootstrap.makeBundle(defaults: defaults)

        let after = UserDefaults.standard.data(forKey: key)
        XCTAssertEqual(before, after,
                       "Bootstrap must not persist the test server into UserDefaults.standard")
    }

    func test_makeBundle_persistsIntoInjectedDefaults() throws {
        let defaults = try makeEphemeralDefaults()
        _ = UITestBootstrap.makeBundle(defaults: defaults)

        // Registry writes on `add(...)`, so the seeded server must be
        // present in the injected suite.
        XCTAssertNotNil(defaults.data(forKey: ServerRegistry.storageKey))
    }

    // MARK: - Helpers

    /// A fresh in-memory-ish UserDefaults suite unique per test, to keep
    /// the shared bootstrap suite untouched.
    private func makeEphemeralDefaults() throws -> UserDefaults {
        let suite = "com.printfarmer.uitest.tests.\(UUID().uuidString)"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        defaults.removePersistentDomain(forName: suite)
        addTeardownBlock {
            defaults.removePersistentDomain(forName: suite)
        }
        return defaults
    }
}
