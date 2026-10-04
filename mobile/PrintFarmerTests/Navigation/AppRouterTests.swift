import XCTest
import Observation
@testable import PrintFarmer

/// Tests for shell-aware tab selection, deep-link mapping, and path isolation.
@MainActor
final class AppRouterTests: XCTestCase {

    private let printerId = UUID(uuidString: "00000000-0000-0000-0000-000000000001")!
    private let spoolId = 42
    private let originServerId = UUID(uuidString: "00000000-0000-0000-0000-000000000010")!
    private let capabilities = ResolvedSystemCapabilities.defaults

    func testDefaultAndRetiredSelectionsFallBackToFarm() {
        XCTAssertEqual(AppRouter().selectedTab, .farm)
        for raw in [nil, "attention", "tasks", "oversight", "overview", "fleet", "upkeep", "reports", "unknown"] {
            XCTAssertEqual(AppRouter.restoredTab(from: raw), .farm)
        }
        XCTAssertEqual(AppRouter.restoredTab(from: "inventory"), .filament)
        XCTAssertEqual(AppRouter.restoredTab(from: "jobs"), .queue)
    }

    func testThreeTabTitlesAndIdentifiers() {
        XCTAssertEqual(AppTab.allCases, [.farm, .queue, .filament])
        XCTAssertEqual(AppTab.allCases.map(\.title), ["Farm", "Queue", "Filament"])
        XCTAssertEqual(AppTab.allCases.map(\.tabAccessibilityIdentifier), ["tab.farm", "tab.queue", "tab.filament"])
        XCTAssertEqual(AppTab.allCases.map(\.sidebarAccessibilityIdentifier), ["sidebar.farm", "sidebar.queue", "sidebar.filament"])
    }

    func testAttentionPushRoutesToFarmFilterEvenWhenAttentionIsDisabled() {
        var disabled = capabilities
        disabled.attentionEnabled = false
        let router = AppRouter()
        router.selectedTab = .queue
        router.printersPath.append(AppDestination.account)
        router.navigate(to: .attentionItem(id: "failure:printer"), capabilities: disabled)
        XCTAssertEqual(router.selectedTab, .farm)
        XCTAssertTrue(router.printersPath.isEmpty)
        XCTAssertTrue(router.pendingNeedsAttentionFilter)
    }

    func testRetiredDeepLinksOpenFarm() throws {
        for host in ["upkeep", "reports", "maintenance", "maintenanceAnalytics", "uptimeReliability",
                     "filamentCoverage", "predictive", "dispatchDashboard", "locations", "jobHistory", "jobTimeline"] {
            let url = try XCTUnwrap(URL(string: "printfarmer://\(host)"))
            XCTAssertEqual(DeepLinkHandler.parse(url: url), .farm, host)
            let router = AppRouter()
            router.selectedTab = .filament
            router.navigate(to: .farm, capabilities: capabilities)
            XCTAssertEqual(router.selectedTab, .farm)
        }
        XCTAssertEqual(DeepLinkHandler.parse(url: try XCTUnwrap(URL(string: "printfarmer://attention/item-1"))), .attentionItem(id: "item-1"))
    }

    func testQueueRoutingAndSessionResetClearOnlyOwnedState() {
        let router = AppRouter()
        router.printersPath.append(AppDestination.account)
        router.jobsPath.append(AppDestination.jobDetail(id: printerId))
        router.routeToJobQueue(capabilities: capabilities)
        XCTAssertEqual(router.selectedTab, .queue)
        XCTAssertTrue(router.jobsPath.isEmpty)
        XCTAssertEqual(router.printersPath.count, 1)
        router.resetAdaptiveShellSession()
        XCTAssertEqual(router.selectedTab, .farm)
        XCTAssertTrue(router.printersPath.isEmpty)
    }

    // MARK: - Defaults

    func testLegacyPersistedScanSelectionRestoresInventory() {
        XCTAssertEqual(AppRouter.restoredTab(from: "scan"), .filament)
    }

    func testScanDeepLinkSelectsInventoryAndCreatesConsumableRequest() {
        let router = AppRouter()

        router.navigate(to: .scan, capabilities: capabilities)

        XCTAssertEqual(router.selectedTab, .filament)
        XCTAssertNotNil(router.pendingExternalScanRequestID)
        XCTAssertTrue(router.consumeExternalScanRequest())
        XCTAssertNil(router.pendingExternalScanRequestID)
        XCTAssertFalse(router.consumeExternalScanRequest())
    }

    func testPendingExternalScanSurvivesAuthenticationAndServerNavigationReset() {
        let router = AppRouter()
        router.navigate(to: .scan, capabilities: capabilities)

        router.invalidatePendingNavigation()

        XCTAssertEqual(router.selectedTab, .filament)
        XCTAssertNotNil(router.pendingExternalScanRequestID)
        XCTAssertTrue(router.consumeExternalScanRequest())
    }

    // MARK: - External scan lifecycle (#2480)

    func testCancelPendingExternalScanClearsTheRequestAndDismissalLatch() {
        let router = AppRouter()
        router.navigate(to: .scan, capabilities: capabilities)
        router.beginScanFlowDismissal(queuedExternalRequestID: UUID())

        router.cancelPendingExternalScan()

        XCTAssertNil(router.pendingExternalScanRequestID)
        XCTAssertFalse(router.isScanFlowDismissing)
        XCTAssertFalse(router.consumeExternalScanRequest())
    }

    func testSignedOutLaunchRetainsAndScopesTheRequestInsteadOfConsumingIt() {
        let request = PendingExternalScanRequest(
            id: UUID(),
            requestedAt: Date(),
            scopedServerID: nil
        )

        let decision = ExternalScanRouting.decide(
            pending: request,
            isShowingMainContent: false,
            activeServerID: originServerId
        )

        XCTAssertEqual(decision, .retain(scopeTo: originServerId))
    }

    func testSuccessfulLoginOnTheScopedServerDeliversTheRetainedRequest() {
        let request = PendingExternalScanRequest(
            id: UUID(),
            requestedAt: Date(),
            scopedServerID: originServerId
        )

        let decision = ExternalScanRouting.decide(
            pending: request,
            isShowingMainContent: true,
            activeServerID: originServerId
        )

        XCTAssertEqual(decision, .deliver)
    }

    func testAuthenticatedDirectLaunchDeliversAnUnscopedRequest() {
        let request = PendingExternalScanRequest(
            id: UUID(),
            requestedAt: Date(),
            scopedServerID: nil
        )

        let decision = ExternalScanRouting.decide(
            pending: request,
            isShowingMainContent: true,
            activeServerID: originServerId
        )

        XCTAssertEqual(decision, .deliver)
    }

    func testSigningIntoADifferentServerCancelsTheRetainedRequest() {
        let request = PendingExternalScanRequest(
            id: UUID(),
            requestedAt: Date(),
            scopedServerID: originServerId
        )

        let decision = ExternalScanRouting.decide(
            pending: request,
            isShowingMainContent: true,
            activeServerID: UUID()
        )

        XCTAssertEqual(decision, .cancel)
    }

    func testAbandonedLoginExpiresRatherThanReplayingIntoALaterSession() {
        let now = Date()
        let request = PendingExternalScanRequest(
            id: UUID(),
            requestedAt: now.addingTimeInterval(-(ExternalScanRequestStore.expiry + 1)),
            scopedServerID: originServerId
        )

        XCTAssertEqual(
            ExternalScanRouting.decide(
                pending: request,
                isShowingMainContent: true,
                activeServerID: originServerId,
                now: now
            ),
            .cancel
        )
        XCTAssertEqual(
            ExternalScanRouting.decide(
                pending: request,
                isShowingMainContent: false,
                activeServerID: originServerId,
                now: now
            ),
            .cancel
        )
    }

    func testNoPendingRequestIsIdle() {
        XCTAssertEqual(
            ExternalScanRouting.decide(
                pending: nil,
                isShowingMainContent: true,
                activeServerID: originServerId
            ),
            .idle
        )
    }

    func testStoreScopesRetainsAndCancelsAcrossIdentityBoundaries() throws {
        let suiteName = "ExternalScanStore-\(UUID().uuidString)"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suiteName))
        defer { defaults.removePersistentDomain(forName: suiteName) }

        XCTAssertNil(ExternalScanRequestStore.pending(userDefaults: defaults))

        ExternalScanRequestStore.request(userDefaults: defaults)
        let pending = try XCTUnwrap(ExternalScanRequestStore.pending(userDefaults: defaults))
        XCTAssertNil(pending.scopedServerID)

        // Signed-out launch scopes without consuming — the request survives login.
        ExternalScanRequestStore.scope(to: originServerId, userDefaults: defaults)
        XCTAssertEqual(
            ExternalScanRequestStore.pending(userDefaults: defaults)?.scopedServerID,
            originServerId
        )

        // Scoping is sticky: a later identity cannot rebind an existing request.
        ExternalScanRequestStore.scope(to: UUID(), userDefaults: defaults)
        XCTAssertEqual(
            ExternalScanRequestStore.pending(userDefaults: defaults)?.scopedServerID,
            originServerId
        )

        // Logout / server switch cancels it outright.
        ExternalScanRequestStore.cancel(userDefaults: defaults)
        XCTAssertNil(ExternalScanRequestStore.pending(userDefaults: defaults))
        XCTAssertFalse(ExternalScanRequestStore.consume(userDefaults: defaults))
    }

    func testOpenScannerIntentRequestsForegroundLaunchAndPersistsRequest() async throws {
        let suiteName = "OpenScannerIntent-\(UUID().uuidString)"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suiteName))
        defer { defaults.removePersistentDomain(forName: suiteName) }

        XCTAssertTrue(OpenScannerIntent.openAppWhenRun)
        if #available(iOS 26.0, *) {
            XCTAssertEqual(OpenScannerIntent.supportedModes, .foreground(.immediate))
        }

        _ = try await OpenScannerIntent().perform(userDefaults: defaults)

        XCTAssertNotNil(ExternalScanRequestStore.pending(userDefaults: defaults))
    }

    func testExternalScanRequestDefaultsUseSharedAppGroupSuite() throws {
        let sharedDefaults = try XCTUnwrap(
            UserDefaults(suiteName: ExternalScanRequestStore.suiteName)
        )
        let existingSharedValue = sharedDefaults.object(
            forKey: ExternalScanRequestStore.pendingKey
        )
        let existingStandardValue = UserDefaults.standard.object(
            forKey: ExternalScanRequestStore.pendingKey
        )
        defer {
            restore(
                existingSharedValue,
                forKey: ExternalScanRequestStore.pendingKey,
                in: sharedDefaults
            )
            restore(
                existingStandardValue,
                forKey: ExternalScanRequestStore.pendingKey,
                in: .standard
            )
        }

        sharedDefaults.removeObject(forKey: ExternalScanRequestStore.pendingKey)
        UserDefaults.standard.removeObject(forKey: ExternalScanRequestStore.pendingKey)
        let requestID = UUID()

        ExternalScanRequestStore.request(id: requestID)

        XCTAssertEqual(
            ExternalScanRequestStore.pending(userDefaults: sharedDefaults)?.id,
            requestID
        )
        XCTAssertNil(UserDefaults.standard.object(forKey: ExternalScanRequestStore.pendingKey))
    }

    private func restore(_ value: Any?, forKey key: String, in defaults: UserDefaults) {
        if let value {
            defaults.set(value, forKey: key)
        } else {
            defaults.removeObject(forKey: key)
        }
    }

    func testLegacyBooleanRequestIsUpgradedRatherThanDropped() throws {
        let suiteName = "ExternalScanLegacy-\(UUID().uuidString)"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suiteName))
        defer { defaults.removePersistentDomain(forName: suiteName) }

        defaults.set(true, forKey: ExternalScanRequestStore.pendingKey)

        let now = Date()
        let upgraded = try XCTUnwrap(
            ExternalScanRequestStore.pending(userDefaults: defaults, now: now)
        )
        XCTAssertNil(upgraded.scopedServerID)
        XCTAssertEqual(
            upgraded.requestedAt.timeIntervalSince1970,
            now.timeIntervalSince1970,
            accuracy: 1
        )
        XCTAssertEqual(ExternalScanRequestStore.pending(userDefaults: defaults)?.id, upgraded.id)
        XCTAssertTrue(ExternalScanRequestStore.consume(userDefaults: defaults))
    }

    func testLegacyRequestMigratesItsCompletePayloadIntoSharedDefaults() throws {
        let legacySuiteName = "ExternalScanLegacySource-\(UUID().uuidString)"
        let sharedSuiteName = "ExternalScanSharedDestination-\(UUID().uuidString)"
        let legacyDefaults = try XCTUnwrap(UserDefaults(suiteName: legacySuiteName))
        let sharedDefaults = try XCTUnwrap(UserDefaults(suiteName: sharedSuiteName))
        defer {
            legacyDefaults.removePersistentDomain(forName: legacySuiteName)
            sharedDefaults.removePersistentDomain(forName: sharedSuiteName)
        }

        let requestID = UUID()
        let requestedAt = Date(timeIntervalSince1970: 1_700_000_000)
        ExternalScanRequestStore.request(
            userDefaults: legacyDefaults,
            now: requestedAt,
            id: requestID
        )
        ExternalScanRequestStore.scope(to: originServerId, userDefaults: legacyDefaults)
        let expected = try XCTUnwrap(
            ExternalScanRequestStore.pending(userDefaults: legacyDefaults)
        )

        ExternalScanRouting.migrateLegacyPendingRequest(
            from: legacyDefaults,
            to: sharedDefaults,
            now: requestedAt.addingTimeInterval(1)
        )

        XCTAssertEqual(
            ExternalScanRequestStore.pending(userDefaults: sharedDefaults),
            expected
        )
        XCTAssertNil(legacyDefaults.object(forKey: ExternalScanRequestStore.pendingKey))
    }

    func testLegacyMigrationPreservesANewerSharedRequest() throws {
        let legacySuiteName = "ExternalScanOlderLegacy-\(UUID().uuidString)"
        let sharedSuiteName = "ExternalScanNewerShared-\(UUID().uuidString)"
        let legacyDefaults = try XCTUnwrap(UserDefaults(suiteName: legacySuiteName))
        let sharedDefaults = try XCTUnwrap(UserDefaults(suiteName: sharedSuiteName))
        defer {
            legacyDefaults.removePersistentDomain(forName: legacySuiteName)
            sharedDefaults.removePersistentDomain(forName: sharedSuiteName)
        }

        ExternalScanRequestStore.request(
            userDefaults: legacyDefaults,
            now: Date(timeIntervalSince1970: 1_700_000_000),
            id: UUID()
        )
        let sharedRequestID = UUID()
        ExternalScanRequestStore.request(
            userDefaults: sharedDefaults,
            now: Date(timeIntervalSince1970: 1_700_000_100),
            id: sharedRequestID
        )
        ExternalScanRequestStore.scope(to: originServerId, userDefaults: sharedDefaults)
        let expected = try XCTUnwrap(
            ExternalScanRequestStore.pending(userDefaults: sharedDefaults)
        )

        ExternalScanRouting.migrateLegacyPendingRequest(
            from: legacyDefaults,
            to: sharedDefaults
        )

        XCTAssertEqual(
            ExternalScanRequestStore.pending(userDefaults: sharedDefaults),
            expected
        )
        XCTAssertEqual(expected.id, sharedRequestID)
        XCTAssertNil(legacyDefaults.object(forKey: ExternalScanRequestStore.pendingKey))
    }

    func testLegacyBooleanDoesNotOverwriteAnExistingSharedRequest() throws {
        let legacySuiteName = "ExternalScanBooleanLegacy-\(UUID().uuidString)"
        let sharedSuiteName = "ExternalScanExistingShared-\(UUID().uuidString)"
        let legacyDefaults = try XCTUnwrap(UserDefaults(suiteName: legacySuiteName))
        let sharedDefaults = try XCTUnwrap(UserDefaults(suiteName: sharedSuiteName))
        defer {
            legacyDefaults.removePersistentDomain(forName: legacySuiteName)
            sharedDefaults.removePersistentDomain(forName: sharedSuiteName)
        }
        legacyDefaults.set(true, forKey: ExternalScanRequestStore.pendingKey)
        let sharedRequestID = UUID()
        ExternalScanRequestStore.request(
            userDefaults: sharedDefaults,
            now: Date(timeIntervalSince1970: 1_700_000_000),
            id: sharedRequestID
        )

        ExternalScanRouting.migrateLegacyPendingRequest(
            from: legacyDefaults,
            to: sharedDefaults,
            now: Date(timeIntervalSince1970: 1_700_000_100)
        )

        XCTAssertEqual(
            ExternalScanRequestStore.pending(userDefaults: sharedDefaults)?.id,
            sharedRequestID
        )
        XCTAssertNil(legacyDefaults.object(forKey: ExternalScanRequestStore.pendingKey))
    }

    func testAppRoutingMigratesLegacyBooleanBeforeReadingSharedDefaults() throws {
        let legacySuiteName = "ExternalScanLegacyBoolean-\(UUID().uuidString)"
        let sharedSuiteName = "ExternalScanBooleanShared-\(UUID().uuidString)"
        let legacyDefaults = try XCTUnwrap(UserDefaults(suiteName: legacySuiteName))
        let sharedDefaults = try XCTUnwrap(UserDefaults(suiteName: sharedSuiteName))
        defer {
            legacyDefaults.removePersistentDomain(forName: legacySuiteName)
            sharedDefaults.removePersistentDomain(forName: sharedSuiteName)
        }
        legacyDefaults.set(true, forKey: ExternalScanRequestStore.pendingKey)
        let now = Date(timeIntervalSince1970: 1_700_000_000)

        ExternalScanRouting.routeFromApp(
            router: AppRouter(),
            activeServerID: originServerId,
            isShowingMainContent: false,
            capabilities: capabilities,
            legacyUserDefaults: legacyDefaults,
            sharedUserDefaults: sharedDefaults,
            now: now
        )

        let migrated = try XCTUnwrap(
            ExternalScanRequestStore.pending(userDefaults: sharedDefaults)
        )
        XCTAssertEqual(
            migrated.requestedAt.timeIntervalSince1970,
            now.timeIntervalSince1970,
            accuracy: 1
        )
        XCTAssertEqual(migrated.scopedServerID, originServerId)
        XCTAssertNil(legacyDefaults.object(forKey: ExternalScanRequestStore.pendingKey))
    }

    // MARK: - RootView lifecycle wiring (#2480)

    /// Drives the shared lifecycle entry point `RootView` delegates every scan
    /// hook to, so removing the wiring cannot leave these tests green.
    private func makeScanLifecycleHarness() throws -> (
        router: AppRouter,
        defaults: UserDefaults,
        suiteName: String
    ) {
        let suiteName = "ExternalScanLifecycle-\(UUID().uuidString)"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suiteName))
        return (AppRouter(), defaults, suiteName)
    }

    func testLogoutCancelsThePendingRequestThroughTheLifecycleWiring() throws {
        let harness = try makeScanLifecycleHarness()
        defer { harness.defaults.removePersistentDomain(forName: harness.suiteName) }
        ExternalScanRequestStore.request(userDefaults: harness.defaults)
        harness.router.navigate(to: .scan, capabilities: capabilities)

        ExternalScanRouting.apply(
            .authenticationChanged(isAuthenticated: false),
            router: harness.router,
            activeServerID: originServerId,
            isShowingMainContent: false,
            capabilities: capabilities,
            userDefaults: harness.defaults
        )

        XCTAssertNil(ExternalScanRequestStore.pending(userDefaults: harness.defaults))
        XCTAssertNil(harness.router.pendingExternalScanRequestID)
    }

    func testSwitchingAwayFromAKnownServerCancelsThroughTheLifecycleWiring() throws {
        let harness = try makeScanLifecycleHarness()
        defer { harness.defaults.removePersistentDomain(forName: harness.suiteName) }
        ExternalScanRequestStore.request(userDefaults: harness.defaults)
        ExternalScanRequestStore.scope(to: originServerId, userDefaults: harness.defaults)

        ExternalScanRouting.apply(
            .activeServerChanged(previousServerID: originServerId, newServerID: UUID()),
            router: harness.router,
            activeServerID: originServerId,
            isShowingMainContent: false,
            capabilities: capabilities,
            userDefaults: harness.defaults
        )

        XCTAssertNil(ExternalScanRequestStore.pending(userDefaults: harness.defaults))
    }

    func testRegisteringTheFirstServerKeepsARequestRetainedPreSignIn() throws {
        let harness = try makeScanLifecycleHarness()
        defer { harness.defaults.removePersistentDomain(forName: harness.suiteName) }
        ExternalScanRequestStore.request(userDefaults: harness.defaults)

        // nil -> A is the AddFirstServerView flow, not a switch (#2480).
        ExternalScanRouting.apply(
            .activeServerChanged(previousServerID: nil, newServerID: originServerId),
            router: harness.router,
            activeServerID: originServerId,
            isShowingMainContent: false,
            capabilities: capabilities,
            userDefaults: harness.defaults
        )

        XCTAssertNotNil(
            ExternalScanRequestStore.pending(userDefaults: harness.defaults),
            "Registering the first server must not destroy a request retained pre-sign-in (#2480)."
        )
    }

    func testBackgroundingDuringLoginKeepsTheRequestAlive() throws {
        let harness = try makeScanLifecycleHarness()
        defer { harness.defaults.removePersistentDomain(forName: harness.suiteName) }
        ExternalScanRequestStore.request(userDefaults: harness.defaults)

        // Reaching for a password manager during 2FA must not abandon the scan.
        ExternalScanRouting.apply(
            .scenePhaseChanged(
                isBackground: true,
                isActive: false,
                isShowingMainContent: false
            ),
            router: harness.router,
            activeServerID: originServerId,
            isShowingMainContent: false,
            capabilities: capabilities,
            userDefaults: harness.defaults
        )

        XCTAssertNotNil(
            ExternalScanRequestStore.pending(userDefaults: harness.defaults),
            "The 10-minute expiry covers abandonment; backgrounding alone must not (#2480)."
        )
    }

    func testReturningToTheForegroundAfterLoginDeliversTheRetainedRequest() throws {
        let harness = try makeScanLifecycleHarness()
        defer { harness.defaults.removePersistentDomain(forName: harness.suiteName) }
        ExternalScanRequestStore.request(userDefaults: harness.defaults)
        ExternalScanRequestStore.scope(to: originServerId, userDefaults: harness.defaults)

        ExternalScanRouting.apply(
            .scenePhaseChanged(
                isBackground: false,
                isActive: true,
                isShowingMainContent: true
            ),
            router: harness.router,
            activeServerID: originServerId,
            isShowingMainContent: true,
            capabilities: capabilities,
            userDefaults: harness.defaults
        )

        XCTAssertNil(ExternalScanRequestStore.pending(userDefaults: harness.defaults))
        XCTAssertEqual(harness.router.selectedTab, .filament)
    }

    func testBackgroundingThenExpiringStillDropsTheRequestOnReturn() throws {
        let harness = try makeScanLifecycleHarness()
        defer { harness.defaults.removePersistentDomain(forName: harness.suiteName) }
        ExternalScanRequestStore.request(userDefaults: harness.defaults)

        let wayLater = Date().addingTimeInterval(ExternalScanRequestStore.expiry + 60)
        ExternalScanRouting.apply(
            .scenePhaseChanged(
                isBackground: false,
                isActive: true,
                isShowingMainContent: true
            ),
            router: harness.router,
            activeServerID: originServerId,
            isShowingMainContent: true,
            capabilities: capabilities,
            userDefaults: harness.defaults,
            now: wayLater
        )

        XCTAssertNil(
            ExternalScanRequestStore.pending(userDefaults: harness.defaults),
            "Expiry, not backgrounding, is what abandons a request (#2480)."
        )
    }

    func testSignedOutForegroundScopesTheRequestThroughTheLifecycleWiring() throws {
        let harness = try makeScanLifecycleHarness()
        defer { harness.defaults.removePersistentDomain(forName: harness.suiteName) }
        ExternalScanRequestStore.request(userDefaults: harness.defaults)

        ExternalScanRouting.apply(
            .scenePhaseChanged(
                isBackground: false,
                isActive: true,
                isShowingMainContent: false
            ),
            router: harness.router,
            activeServerID: originServerId,
            isShowingMainContent: false,
            capabilities: capabilities,
            userDefaults: harness.defaults
        )

        XCTAssertEqual(
            ExternalScanRequestStore.pending(userDefaults: harness.defaults)?.scopedServerID,
            originServerId
        )
    }

    func testLifecycleActionsCoverEveryHookRootViewWiresUp() {
        XCTAssertEqual(
            ExternalScanRouting.lifecycleAction(
                for: .authenticationChanged(isAuthenticated: false)
            ),
            .cancel
        )
        XCTAssertEqual(
            ExternalScanRouting.lifecycleAction(
                for: .authenticationChanged(isAuthenticated: true)
            ),
            .route
        )
        XCTAssertEqual(
            ExternalScanRouting.lifecycleAction(
                for: .scenePhaseChanged(
                    isBackground: true,
                    isActive: false,
                    isShowingMainContent: false
                )
            ),
            .none
        )
        XCTAssertEqual(
            ExternalScanRouting.lifecycleAction(
                for: .scenePhaseChanged(
                    isBackground: false,
                    isActive: true,
                    isShowingMainContent: false
                )
            ),
            .route
        )
        XCTAssertEqual(
            ExternalScanRouting.lifecycleAction(
                for: .activeServerChanged(previousServerID: nil, newServerID: originServerId)
            ),
            .none
        )
        XCTAssertEqual(
            ExternalScanRouting.lifecycleAction(
                for: .activeServerChanged(
                    previousServerID: originServerId,
                    newServerID: originServerId
                )
            ),
            .none
        )
        XCTAssertEqual(
            ExternalScanRouting.lifecycleAction(
                for: .activeServerChanged(previousServerID: originServerId, newServerID: UUID())
            ),
            .cancel
        )
    }

    func testDeferredExternalScanWaitsForDismissalWithoutCancellingPrinterNavigation() async {
        let router = AppRouter()
        let requestID = UUID()
        router.beginScanFlowDismissal(queuedExternalRequestID: requestID)
        router.navigate(to: .printerDetail(id: printerId), capabilities: capabilities)
        XCTAssertEqual(router.selectedTab, .farm)
        XCTAssertFalse(router.consumeExternalScanRequest())
        let navigated = expectation(description: "Original printer destination pushed")
        withObservationTracking {
            _ = router.printersPath
        } onChange: {
            navigated.fulfill()
        }

        router.completeScanFlowDismissal(capabilities: capabilities)

        XCTAssertEqual(router.selectedTab, .filament)
        XCTAssertEqual(router.pendingExternalScanRequestID, requestID)
        XCTAssertTrue(router.consumeExternalScanRequest())
        XCTAssertFalse(router.consumeExternalScanRequest())
        await fulfillment(of: [navigated], timeout: 2)
        XCTAssertEqual(router.printersPath.count, 1)
    }

    func testDeferredExternalScanPreservesSpoolResultAfterDismissal() {
        let router = AppRouter()
        let requestID = UUID()
        router.beginScanFlowDismissal(queuedExternalRequestID: requestID)
        router.navigate(to: .spoolDetail(id: spoolId), capabilities: capabilities)
        XCTAssertEqual(router.pendingSpoolHighlightId, spoolId)
        XCTAssertFalse(router.consumeExternalScanRequest())

        router.completeScanFlowDismissal(capabilities: capabilities)

        XCTAssertEqual(router.pendingSpoolHighlightId, spoolId)
        XCTAssertEqual(router.pendingExternalScanRequestID, requestID)
        XCTAssertTrue(router.consumeExternalScanRequest())
    }

    func testNewExternalRequestDuringDismissalCoalescesWithoutEarlyConsumption() {
        let router = AppRouter()
        router.beginScanFlowDismissal(queuedExternalRequestID: UUID())
        router.navigate(to: .scan, capabilities: capabilities)
        let latestRequestID = router.pendingExternalScanRequestID
        XCTAssertNotNil(latestRequestID)
        XCTAssertFalse(router.consumeExternalScanRequest())

        router.completeScanFlowDismissal(capabilities: capabilities)

        XCTAssertEqual(router.pendingExternalScanRequestID, latestRequestID)
        XCTAssertTrue(router.consumeExternalScanRequest())
        XCTAssertFalse(router.consumeExternalScanRequest())
    }

    func testDeferredExternalScanSurvivesAuthenticationAndServerNavigationReset() {
        let router = AppRouter()
        let requestID = UUID()
        router.beginScanFlowDismissal(queuedExternalRequestID: requestID)

        router.invalidatePendingNavigation()

        XCTAssertFalse(router.isScanFlowDismissing)
        XCTAssertEqual(router.pendingExternalScanRequestID, requestID)
        XCTAssertTrue(router.consumeExternalScanRequest())
    }

    func testExternalScanRequestStoreConsumesPersistedRequestExactlyOnce() throws {
        let suiteName = "ExternalScanRequestStoreTests.\(UUID().uuidString)"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suiteName))
        defer { defaults.removePersistentDomain(forName: suiteName) }

        ExternalScanRequestStore.request(userDefaults: defaults)

        XCTAssertTrue(ExternalScanRequestStore.consume(userDefaults: defaults))
        XCTAssertFalse(ExternalScanRequestStore.consume(userDefaults: defaults))
    }

    func testPersistedSelectionRoundTripsWithoutAffectingDefaultRouterTests() throws {
        let suiteName = "AppRouterTests.\(UUID().uuidString)"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suiteName))
        defer { defaults.removePersistentDomain(forName: suiteName) }
        defaults.set("scan", forKey: AppRouter.selectedTabDefaultsKey)

        let router = AppRouter(userDefaults: defaults)

        XCTAssertEqual(router.selectedTab, .filament)
        router.selectedTab = .farm
        XCTAssertEqual(
            defaults.string(forKey: AppRouter.selectedTabDefaultsKey),
            AppTab.farm.rawValue
        )
    }

    func testAccountDestinationRowsPreserveCanonicalIdentifiersAndCapabilityGate() {
        let enabledRows = AccountDestinationRow.available(offlineQueueEnabled: true)

        XCTAssertEqual(
            enabledRows.map(\.destination),
            [.notifications, .settings, .manageServers, .offlineQueue]
        )
        XCTAssertEqual(
            enabledRows.map(\.accessibilityIdentifier),
            [
                "account.destination.notifications",
                "account.destination.settings",
                "account.destination.manageServers",
                "account.destination.offlineQueue"
            ]
        )
        XCTAssertEqual(
            AccountDestinationRow.available(offlineQueueEnabled: false).map(\.destination),
            [.notifications, .settings, .manageServers]
        )
    }

    func testCoreTabsSurviveDisabledOperatorFeatures() {
        var disabled = capabilities
        disabled.attentionEnabled = false
        disabled.shiftPlanEnabled = false

        XCTAssertEqual(AppTab.visibleTabs(for: disabled), [.farm, .queue, .filament])
        XCTAssertEqual(AppTab.fallbackTab(for: disabled), .farm)
    }

    // MARK: - Tasks reachability (#2479)

    // MARK: - Deep link routing

    func testFilamentSwapDeepLinkSelectsFarmAndPreservesDestination() async {
        let jobId = UUID(uuidString: "00000000-0000-0000-0000-000000000002")!
        let router = AppRouter()

        router.navigate(
            to: .filamentSwap(printerId: printerId, toolheadIndex: 2, jobId: jobId),
            capabilities: capabilities
        )

        XCTAssertEqual(router.selectedTab, .farm)
        XCTAssertEqual(
            router.pendingFilamentSwap,
            .init(printerId: printerId, toolheadIndex: 2, jobId: jobId)
        )

        try? await Task.sleep(for: .milliseconds(120))
        XCTAssertFalse(router.printersPath.isEmpty)
    }

    func testDisabledGuidedSwapDeepLinkOpensPrinterWithoutOpeningSwap() async {
        var disabled = capabilities
        disabled.guidedSwapEnabled = false
        let router = AppRouter()

        router.navigate(
            to: .filamentSwap(printerId: printerId, toolheadIndex: 2, jobId: nil),
            capabilities: disabled
        )

        XCTAssertEqual(router.selectedTab, .farm)
        XCTAssertNil(router.pendingFilamentSwap)
        try? await Task.sleep(for: .milliseconds(120))
        XCTAssertFalse(router.printersPath.isEmpty)
    }

    func testCapabilityReconciliationClearsPendingGuidedSwap() {
        let router = AppRouter()
        router.pendingFilamentSwap = .init(
            printerId: printerId,
            toolheadIndex: 2,
            jobId: nil
        )
        var disabled = capabilities
        disabled.guidedSwapEnabled = false

        router.reconcileCapabilities(disabled)

        XCTAssertNil(router.pendingFilamentSwap)
    }

    func testAdvancedControlsAccessRequiresOptInAndOnlinePrinter() throws {
        var printer = try TestData.decodePrinter()

        XCTAssertFalse(
            AdvancedPrinterControlsAccess.isEntryVisible(isEnabled: false, for: printer)
        )
        XCTAssertTrue(
            AdvancedPrinterControlsAccess.isEntryVisible(isEnabled: true, for: printer)
        )

        printer.isOnline = false
        XCTAssertFalse(
            AdvancedPrinterControlsAccess.isEntryVisible(isEnabled: true, for: printer)
        )
    }

    // MARK: - Reset to root

    func testResetToRootClearsFarmPath() {
        let router = AppRouter()
        router.printersPath.append(AppDestination.printerDetail(id: printerId))
        XCTAssertFalse(router.printersPath.isEmpty)

        router.resetToRoot(tab: .farm)
        XCTAssertTrue(router.printersPath.isEmpty)
    }

    func testResetToRootClearsInventoryPath() {
        let router = AppRouter()
        router.inventoryPath.append(AppDestination.jobDetail(id: printerId))
        XCTAssertFalse(router.inventoryPath.isEmpty)

        router.resetToRoot(tab: .filament)
        XCTAssertTrue(router.inventoryPath.isEmpty)
    }

    // MARK: - AppDestination migrations

    func testAdvancedPrinterControlsDestinationEncodesPrinterId() {
        // F1 (#706): jog/preheat/z-offset controls are only reachable via
        // this destination, which must round-trip the printer id.
        let destination = AppDestination.advancedPrinterControls(printerId: printerId)
        if case .advancedPrinterControls(let id) = destination {
            XCTAssertEqual(id, printerId)
        } else {
            XCTFail("Expected advancedPrinterControls case")
        }
    }

    // MARK: - Inventory feature visibility

}
