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

    func test_unauthenticatedLaunchArgument_matchesUITestsHarness() {
        // Contract with LoginFlowUITests.additionalLaunchArguments, which
        // hardcodes this literal (UI test targets cannot import the app).
        XCTAssertEqual(UITestBootstrap.unauthenticatedLaunchArgument, "--uitesting-unauthenticated")
    }

    // MARK: - Launch mode

    func test_mode_defaultsToAuthenticated() {
        XCTAssertEqual(UITestBootstrap.mode(in: ["--uitesting"]), .authenticated)
        XCTAssertEqual(UITestBootstrap.mode(in: []), .authenticated)
    }

    func test_mode_isUnauthenticated_whenArgumentPresent() {
        XCTAssertEqual(
            UITestBootstrap.mode(in: ["--uitesting", "--uitesting-unauthenticated"]),
            .unauthenticated
        )
    }

    // MARK: - Operator-features-disabled mode (#2117)

    func test_operatorFeaturesDisabledLaunchArgument_matchesUITestsHarness() {
        // Contract with OperatorShellUITests.OperatorFeatureVisibilityUITests,
        // which hardcodes this literal (UI test targets cannot import the app).
        XCTAssertEqual(
            UITestBootstrap.operatorFeaturesDisabledLaunchArgument,
            "--uitesting-operator-features-disabled"
        )
    }

    func test_mode_isOperatorFeaturesDisabled_whenArgumentPresent() {
        XCTAssertEqual(
            UITestBootstrap.mode(in: [
                "--uitesting",
                "--uitesting-operator-features-disabled"
            ]),
            .authenticatedOperatorFeaturesDisabled
        )
    }

    func test_mode_operatorFeaturesDisabled_takesPrecedenceOverUnauthenticated() {
        XCTAssertEqual(
            UITestBootstrap.mode(in: [
                "--uitesting",
                "--uitesting-unauthenticated",
                "--uitesting-operator-features-disabled"
            ]),
            .authenticatedOperatorFeaturesDisabled
        )
    }

    // MARK: - Attention actions mode (#780)

    func test_attentionActionsLaunchArgument_matchesUITestsHarness() {
        XCTAssertEqual(
            UITestBootstrap.attentionActionsLaunchArgument,
            "--uitesting-attention-actions"
        )
    }

    func test_mode_isAttentionActions_whenArgumentPresent() {
        XCTAssertEqual(
            UITestBootstrap.mode(in: [
                "--uitesting",
                "--uitesting-attention-actions",
            ]),
            .authenticatedAttentionActions
        )
    }

    func test_makeBundle_attentionActions_seedsActionMediaAndStableIDs() async throws {
        let defaults = try makeEphemeralDefaults()
        let bundle = UITestBootstrap.makeBundle(
            mode: .authenticatedAttentionActions,
            defaults: defaults
        )
        let attentionService = try XCTUnwrap(
            bundle.services.attentionService as? DemoAttentionService
        )
        let printerService = try XCTUnwrap(
            bundle.services.printerService as? DemoPrinterService
        )

        let feed = try await attentionService.getFeed(
            cursor: nil,
            limit: nil
        )
        XCTAssertEqual(feed.items.count, 3)
        XCTAssertEqual(
            feed.items.first?.printerId,
            UITestBootstrap.duplicateNamePrinterID
        )
        XCTAssertEqual(
            AttentionFeedViewModel.supportedActions(
                in: try XCTUnwrap(feed.items.first)
            ).map(\.kind),
            [.resume, .cancel, .snooze]
        )

        let duplicatePrinter = try await printerService.get(
            id: UITestBootstrap.duplicateNamePrinterID
        )
        let snapshot = try await printerService.getSnapshot(
            id: UITestBootstrap.duplicateNamePrinterID
        )
        XCTAssertEqual(duplicatePrinter.name, "Prusa MK4 #1")
        XCTAssertFalse(snapshot.isEmpty)
        XCTAssertTrue(bundle.authViewModel.isAuthenticated)
    }

    #if DEBUG
    func test_shiftTaskMutationErrorLaunchArgument_matchesUITestsHarness() {
        XCTAssertEqual(
            UITestBootstrap.shiftTaskMutationErrorLaunchArgument,
            "--uitesting-shift-task-mutation-error"
        )
    }
    #endif

    #if DEBUG
    func test_mode_isShiftTaskMutationError_whenArgumentPresent() {
        XCTAssertEqual(
            UITestBootstrap.mode(in: [
                "--uitesting",
                "--uitesting-shift-task-mutation-error"
            ]),
            .authenticatedShiftTaskMutationError
        )
    }
    #endif

    #if DEBUG
    func test_makeBundle_shiftTaskMutationError_usesScriptedTaskService() throws {
        let defaults = try makeEphemeralDefaults()
        let bundle = UITestBootstrap.makeBundle(
            mode: .authenticatedShiftTaskMutationError,
            defaults: defaults
        )

        XCTAssertTrue(bundle.services.shiftTaskService is DemoShiftTaskService)
        XCTAssertTrue(bundle.authViewModel.isAuthenticated)
        XCTAssertTrue(
            bundle.services.capabilitiesService.resolved.shiftPlanEnabled
        )
    }
    #endif

    #if DEBUG
    func test_shiftTaskInitialLoadFailureLaunchArgument_matchesUITestsHarness() {
        XCTAssertEqual(
            UITestBootstrap.shiftTaskInitialLoadFailureLaunchArgument,
            "--uitesting-shift-task-initial-load-failure"
        )
    }
    #endif

    #if DEBUG
    func test_mode_isShiftTaskInitialLoadFailure_whenArgumentPresent() {
        XCTAssertEqual(
            UITestBootstrap.mode(in: [
                "--uitesting",
                "--uitesting-shift-task-initial-load-failure"
            ]),
            .authenticatedShiftTaskInitialLoadFailure
        )
    }
    #endif

    #if DEBUG
    func test_mode_mutationError_takesPrecedenceOverInitialLoadFailure() {
        // The two shift-task scenarios are mutually exclusive; when both
        // flags are present the mutation-error scenario wins so the
        // resolution stays deterministic.
        XCTAssertEqual(
            UITestBootstrap.mode(in: [
                "--uitesting",
                "--uitesting-shift-task-mutation-error",
                "--uitesting-shift-task-initial-load-failure"
            ]),
            .authenticatedShiftTaskMutationError
        )
    }
    #endif

    #if DEBUG
    func test_makeBundle_shiftTaskInitialLoadFailure_usesScriptedTaskService() throws {
        let defaults = try makeEphemeralDefaults()
        let bundle = UITestBootstrap.makeBundle(
            mode: .authenticatedShiftTaskInitialLoadFailure,
            defaults: defaults
        )

        XCTAssertTrue(bundle.services.shiftTaskService is DemoShiftTaskService)
        XCTAssertTrue(bundle.authViewModel.isAuthenticated)
        XCTAssertTrue(
            bundle.services.capabilitiesService.resolved.shiftPlanEnabled
        )
    }
    #endif

    #if DEBUG
    func test_makeBundle_shiftTaskInitialLoadFailure_failsFirstLoadThenRecovers() async throws {
        // Contract for the failed-state pull-to-refresh XCUI proof: the
        // first canonical load throws (drives `.failed`), the next load
        // recovers to the grouped plan.
        let service = DemoShiftTaskService(scenario: .initialLoadFailureThenSuccess)

        do {
            _ = try await service.loadSnapshot(shiftPlanEnabled: true)
            XCTFail("The first load must fail so the view reaches its .failed terminal state")
        } catch {
            // expected
        }

        let recovered = try await service.loadSnapshot(shiftPlanEnabled: true)
        XCTAssertEqual(recovered.mode, .grouped)
        XCTAssertFalse(
            recovered.groups.isEmpty,
            "Recovery must publish the grouped multi-section plan"
        )
    }
    #endif

    func test_makeBundle_operatorFeaturesDisabled_overridesCapabilities() throws {
        let defaults = try makeEphemeralDefaults()
        let bundle = UITestBootstrap.makeBundle(
            mode: .authenticatedOperatorFeaturesDisabled,
            defaults: defaults
        )
        let resolved = bundle.services.capabilitiesService.resolved

        XCTAssertFalse(resolved.attentionEnabled)
        XCTAssertFalse(resolved.filamentCoverageEnabled)
        XCTAssertFalse(resolved.shiftPlanEnabled)
        XCTAssertFalse(resolved.printedPartsInventoryEnabled)
        XCTAssertTrue(resolved.guidedSwapEnabled)
        XCTAssertTrue(resolved.multiSlotFallbackEnabled)
        XCTAssertTrue(resolved.offlineWriteReplayEnabled)
    }

    func test_makeBundle_operatorFeaturesDisabled_staysAuthenticated() throws {
        let defaults = try makeEphemeralDefaults()
        let bundle = UITestBootstrap.makeBundle(
            mode: .authenticatedOperatorFeaturesDisabled,
            defaults: defaults
        )

        XCTAssertTrue(
            bundle.authViewModel.isAuthenticated,
            "The disabled-operator-features mode must still render the authenticated shell"
        )
        XCTAssertEqual(bundle.authViewModel.currentUser?.id, DemoData.demoUser.id)
    }

    func test_makeBundle_authenticated_leavesDefaultCapabilities() throws {
        // Guard against regressions where the disabled-feature override
        // leaks into the default authenticated mode.
        let defaults = try makeEphemeralDefaults()
        let bundle = UITestBootstrap.makeBundle(mode: .authenticated, defaults: defaults)

        XCTAssertTrue(
            bundle.services.capabilitiesService.resolved.attentionEnabled,
            "Default authenticated mode must keep attentionEnabled=true"
        )
    }

    func test_makeBundle_authenticated_enablesPrintedPartsInventory() throws {
        // #1353: `ResolvedSystemCapabilities.defaults.printedPartsInventoryEnabled`
        // is `false` in production so a fresh server without SKUs/mappings
        // does not surface the harvest flow. The demo bootstrap IS a fully-
        // configured operator playground, so the harvest surfaces (JobDetail
        // "Harvest to Inventory", Scan shortcut, ShiftTasks harvest destination)
        // MUST be reachable in the default authenticated mode. Regressing this
        // silently strands every `HarvestUITests` test.
        let defaults = try makeEphemeralDefaults()
        let bundle = UITestBootstrap.makeBundle(mode: .authenticated, defaults: defaults)

        XCTAssertTrue(
            bundle.services.capabilitiesService.resolved.printedPartsInventoryEnabled,
            "Default authenticated demo mode must enable printedPartsInventoryEnabled so the harvest flow is reachable"
        )
    }

    #if DEBUG
    func test_makeBundle_taskActionRouting_enablesPrintedPartsInventory() throws {
        // #1353: `TaskActionRoute.compute` sets `harvestEnabled` from
        // `printedPartsInventoryEnabled` and fails with `featureDisabled` for
        // a shift-task harvest row when the flag is off. The task-action
        // routing bootstrap MUST enable printed-parts inventory so
        // `TaskActionRoutingUITests.testHarvestRowRoutesToExactStableHarvestDestination`
        // routes to the harvest destination instead of hitting the disabled path.
        let defaults = try makeEphemeralDefaults()
        let bundle = UITestBootstrap.makeBundle(
            mode: .authenticatedTaskActionRouting,
            defaults: defaults
        )

        XCTAssertTrue(
            bundle.services.capabilitiesService.resolved.printedPartsInventoryEnabled,
            "Task-action routing mode must enable printedPartsInventoryEnabled so the harvest task row can route to its destination"
        )
    }
    #endif

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

    // MARK: - Unauthenticated (login-flow) mode

    func test_makeBundle_unauthenticated_leavesSessionSignedOut() throws {
        let defaults = try makeEphemeralDefaults()
        let bundle = UITestBootstrap.makeBundle(mode: .unauthenticated, defaults: defaults)

        XCTAssertFalse(bundle.authViewModel.isAuthenticated,
                       "Unauthenticated mode must leave the session signed out so LoginView renders")
        XCTAssertNil(bundle.authViewModel.currentUser)
    }

    func test_makeBundle_unauthenticated_stillSeedsActiveServer() throws {
        let defaults = try makeEphemeralDefaults()
        let bundle = UITestBootstrap.makeBundle(mode: .unauthenticated, defaults: defaults)

        // A registered active server keeps RootView on LoginView instead of
        // AddFirstServerView.
        XCTAssertFalse(bundle.serverRegistry.servers.isEmpty)
        XCTAssertNotNil(bundle.serverRegistry.activeServerID)
    }

    func test_makeBundle_unauthenticated_usesDemoServices_noNetwork() throws {
        let defaults = try makeEphemeralDefaults()
        let bundle = UITestBootstrap.makeBundle(mode: .unauthenticated, defaults: defaults)

        // The sign-in path must stay off the real network in the login flow.
        XCTAssertTrue(bundle.services.authService is DemoAuthService)
        XCTAssertNil(bundle.services.apiClient)
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

    func test_makeBundle_resolvesNavigationIdentityForActiveDemoServer() async throws {
        let defaults = try makeEphemeralDefaults()
        let environment = UITestBootstrap.makeBundle(defaults: defaults)
        let activeServer = try XCTUnwrap(environment.serverRegistry.activeServer)

        let resolution = await environment.services.currentUserForNavigation(
            serverID: activeServer.id,
            generation: environment.services.activeServerGeneration,
            expectedEndpoint: activeServer.normalizedURLString
        )

        XCTAssertEqual(
            resolution,
            .verified(
                NavigationUserIdentity(
                    userID: DemoData.demoUser.id,
                    roles: DemoData.demoUser.roles
                )
            )
        )
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
