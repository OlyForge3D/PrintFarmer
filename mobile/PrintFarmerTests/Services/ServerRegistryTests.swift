import XCTest
import KeychainSwift
@testable import PrintFarmer

@MainActor
final class ServerRegistryTests: XCTestCase {
    nonisolated(unsafe) private var mockAPIClient: MockAPIClient!
    private var userDefaults: UserDefaults!
    private var userDefaultsSuiteName: String!

    override func setUp() async throws {
        try await super.setUp()
        mockAPIClient = MockAPIClient()
        userDefaultsSuiteName = "ServerRegistryTests-\(UUID().uuidString)"
        userDefaults = UserDefaults(suiteName: userDefaultsSuiteName)!
        userDefaults.removePersistentDomain(forName: userDefaultsSuiteName)
    }

    private actor PinPurgeRecorder {
        private(set) var values: (serverID: UUID?, remainingIDs: [UUID]) = (nil, [])

        func record(server: RegisteredServer, remaining: [RegisteredServer]) {
            values = (server.id, remaining.map(\.id))
        }
    }

    override func tearDown() async throws {
        mockAPIClient = nil
        userDefaults.removePersistentDomain(forName: userDefaultsSuiteName)
        userDefaults = nil
        userDefaultsSuiteName = nil
        try await super.tearDown()
    }

    func testAddPersistsServerAndSetsFirstServerActive() throws {
        let registry = ServerRegistry(userDefaults: userDefaults, migrateLegacyServerURL: false)

        let server = try registry.add(
            displayName: " Farm ",
            baseURL: URL(string: "https://print.example.com/")!
        )

        XCTAssertEqual(registry.servers, [server])
        XCTAssertEqual(registry.activeServerID, server.id)
        XCTAssertEqual(server.displayName, "Farm")
        XCTAssertEqual(server.normalizedURLString, "https://print.example.com")

        let reloaded = ServerRegistry(userDefaults: userDefaults, migrateLegacyServerURL: false)
        XCTAssertEqual(reloaded.servers, [server])
        XCTAssertEqual(reloaded.activeServerID, server.id)
    }

    func testUpdateReplacesFieldsAndKeepsCreatedAt() throws {
        let date = Date(timeIntervalSince1970: 100)
        let updatedDate = Date(timeIntervalSince1970: 200)
        var currentDate = date
        let registry = ServerRegistry(
            userDefaults: userDefaults,
            now: { currentDate },
            migrateLegacyServerURL: false
        )
        var server = try registry.add(
            displayName: "Old",
            baseURL: URL(string: "https://old.example.com")!
        )

        currentDate = updatedDate
        server.displayName = "New"
        server.baseURL = URL(string: "https://new.example.com/")!
        server.lastKnownStatus = "online"
        server.lastAuthenticatedUsername = "jeff"
        try registry.update(server)

        let updated = try XCTUnwrap(registry.servers.first)
        XCTAssertEqual(updated.displayName, "New")
        XCTAssertEqual(updated.normalizedURLString, "https://new.example.com")
        XCTAssertEqual(updated.lastKnownStatus, "online")
        XCTAssertEqual(updated.lastAuthenticatedUsername, "jeff")
        XCTAssertEqual(updated.createdAt, date)
        XCTAssertEqual(updated.updatedAt, updatedDate)
    }

    func testRemoveActiveServerSelectsNextServer() async throws {
        let registry = ServerRegistry(userDefaults: userDefaults, migrateLegacyServerURL: false)
        registry.snapshotPurgeHandler = { _ in .purged }
        let first = try registry.add(displayName: "One", baseURL: URL(string: "https://one.example.com")!)
        let second = try registry.add(displayName: "Two", baseURL: URL(string: "https://two.example.com")!)

        try registry.setActive(id: first.id)
        try await registry.purgeAndRemove(id: first.id)

        XCTAssertEqual(registry.servers.map(\.id), [second.id])
        XCTAssertEqual(registry.activeServerID, second.id)
    }

    func testRemovePurgesCertificatePinBeforeDroppingRegistryEntry() async throws {
        let registry = ServerRegistry(userDefaults: userDefaults, migrateLegacyServerURL: false)
        registry.snapshotPurgeHandler = { _ in .purged }
        let recorder = PinPurgeRecorder()
        registry.certificatePinPurgeHandler = { server, remaining in
            await recorder.record(server: server, remaining: remaining)
            return true
        }
        let server = try registry.add(
            displayName: "Farm",
            baseURL: URL(string: "https://192.168.1.10")!
        )

        try await registry.purgeAndRemove(id: server.id)
        let recorded = await recorder.values

        XCTAssertEqual(recorded.serverID, server.id)
        XCTAssertTrue(recorded.remainingIDs.isEmpty)
        XCTAssertTrue(registry.servers.isEmpty)
    }

    func testCertificatePinPurgeFailureRetainsRegistryEntry() async throws {
        let registry = ServerRegistry(userDefaults: userDefaults, migrateLegacyServerURL: false)
        registry.snapshotPurgeHandler = { _ in .purged }
        registry.certificatePinPurgeHandler = { _, _ in false }
        let server = try registry.add(
            displayName: "Farm",
            baseURL: URL(string: "https://192.168.1.10")!
        )

        do {
            try await registry.purgeAndRemove(id: server.id)
            XCTFail("Expected certificate pin purge failure")
        } catch {
            XCTAssertEqual(error as? ServerRegistryError, .certificatePinPurgeFailed(server.id))
        }
        XCTAssertEqual(registry.servers.map(\.id), [server.id])
    }

    func testSetActiveCanSelectAndClearActiveServer() throws {
        let registry = ServerRegistry(userDefaults: userDefaults, migrateLegacyServerURL: false)
        let first = try registry.add(displayName: "One", baseURL: URL(string: "https://one.example.com")!)
        let second = try registry.add(displayName: "Two", baseURL: URL(string: "https://two.example.com")!)

        try registry.setActive(id: second.id)
        XCTAssertEqual(registry.activeServer?.id, second.id)

        try registry.setActive(id: nil)
        XCTAssertNil(registry.activeServerID)
        XCTAssertNil(registry.activeServer)

        try registry.setActive(id: first.id)
        XCTAssertEqual(registry.activeServer?.id, first.id)
    }

    func testNavigationLayoutPreferencePersistsPerServer() throws {
        let registry = ServerRegistry(userDefaults: userDefaults, migrateLegacyServerURL: false)
        let first = try registry.add(
            displayName: "One",
            baseURL: URL(string: "https://one.example.com")!
        )
        let second = try registry.add(
            displayName: "Two",
            baseURL: URL(string: "https://two.example.com")!
        )

        try registry.setActive(id: first.id)
        XCTAssertEqual(registry.navigationLayoutPreference, .automatic)
        registry.setNavigationLayoutPreference(.twoModes)

        try registry.setActive(id: second.id)
        XCTAssertEqual(registry.navigationLayoutPreference, .automatic)
        registry.setNavigationLayoutPreference(.simple)

        try registry.setActive(id: first.id)
        XCTAssertEqual(registry.navigationLayoutPreference, .twoModes)

        let reloaded = ServerRegistry(
            userDefaults: userDefaults,
            migrateLegacyServerURL: false
        )
        XCTAssertEqual(reloaded.activeServerID, first.id)
        XCTAssertEqual(reloaded.navigationLayoutPreference, .twoModes)

        try reloaded.setActive(id: second.id)
        XCTAssertEqual(reloaded.navigationLayoutPreference, .simple)
    }

    func testChangingServerEndpointClearsNavigationLayoutPreference() throws {
        let registry = ServerRegistry(userDefaults: userDefaults, migrateLegacyServerURL: false)
        var server = try registry.add(
            displayName: "Farm",
            baseURL: URL(string: "https://old.example.com")!
        )
        registry.setNavigationLayoutPreference(.twoModes)

        server.baseURL = URL(string: "https://new.example.com")!
        try registry.update(server)

        XCTAssertEqual(registry.navigationLayoutPreference, .automatic)
    }

    func testNormalizationAddsHTTPSLowercasesHostAndStripsTrailingSlash() throws {
        let normalized = try ServerRegistry.normalizedURLString(for: "  PRINT.example.COM/  ")

        XCTAssertEqual(normalized, "https://print.example.com")
    }

    func testNormalizationPreservesExplicitHTTPAndPort() throws {
        let normalized = try ServerRegistry.normalizedURLString(for: "http://192.168.1.100:5245/")

        XCTAssertEqual(normalized, "http://192.168.1.100:5245")
    }

    func testDuplicateURLsAreRejected() throws {
        let registry = ServerRegistry(userDefaults: userDefaults, migrateLegacyServerURL: false)
        _ = try registry.add(displayName: "One", baseURL: URL(string: "https://print.example.com")!)

        XCTAssertThrowsError(
            try registry.add(displayName: "Duplicate", baseURL: URL(string: "https://PRINT.example.com/")!)
        ) { error in
            XCTAssertEqual(error as? ServerRegistryError, .duplicateURL("https://print.example.com"))
        }
    }

    func testUpdateRejectsDuplicateURL() throws {
        let registry = ServerRegistry(userDefaults: userDefaults, migrateLegacyServerURL: false)
        _ = try registry.add(displayName: "One", baseURL: URL(string: "https://one.example.com")!)
        var second = try registry.add(displayName: "Two", baseURL: URL(string: "https://two.example.com")!)

        second.baseURL = URL(string: "https://ONE.example.com/")!

        XCTAssertThrowsError(try registry.update(second)) { error in
            XCTAssertEqual(error as? ServerRegistryError, .duplicateURL("https://one.example.com"))
        }
    }

    func testLegacyServerURLMigrationSeedsActiveServerWhenRegistryIsEmpty() throws {
        userDefaults.set("http://100.119.81.25", forKey: APIClient.serverURLKey)

        let registry = ServerRegistry(userDefaults: userDefaults)

        let server = try XCTUnwrap(registry.servers.first)
        XCTAssertEqual(registry.servers.count, 1)
        XCTAssertEqual(registry.activeServerID, server.id)
        XCTAssertEqual(server.displayName, "100.119.81.25")
        XCTAssertEqual(server.normalizedURLString, "https://100.119.81.25")
        XCTAssertEqual(server.baseURL.absoluteString, "https://100.119.81.25")
        XCTAssertEqual(userDefaults.string(forKey: APIClient.serverURLKey), "https://100.119.81.25")
    }

    func testLegacyMigrationDoesNotRunWhenRegistryAlreadyHasServers() throws {
        let registry = ServerRegistry(userDefaults: userDefaults, migrateLegacyServerURL: false)
        let existing = try registry.add(displayName: "Existing", baseURL: URL(string: "https://existing.example.com")!)
        userDefaults.set("https://legacy.example.com", forKey: APIClient.serverURLKey)

        let reloaded = ServerRegistry(userDefaults: userDefaults)

        XCTAssertEqual(reloaded.servers.map(\.id), [existing.id])
        XCTAssertEqual(reloaded.servers.first?.normalizedURLString, "https://existing.example.com")
    }

    func testCorruptPersistedRegistryIsPreservedAndDoesNotRunLegacyMigration() throws {
        let corruptData = Data([0xde, 0xad, 0xbe, 0xef])
        userDefaults.set(corruptData, forKey: ServerRegistry.storageKey)
        userDefaults.set("https://legacy.example.com", forKey: APIClient.serverURLKey)

        let registry = ServerRegistry(userDefaults: userDefaults)

        XCTAssertTrue(registry.servers.isEmpty)
        XCTAssertNil(registry.activeServerID)
        XCTAssertEqual(userDefaults.data(forKey: ServerRegistry.storageKey), corruptData)
        XCTAssertEqual(userDefaults.data(forKey: ServerRegistry.corruptBackupKey), corruptData)
        XCTAssertEqual(userDefaults.string(forKey: APIClient.serverURLKey), "https://legacy.example.com")
        XCTAssertFalse(userDefaults.bool(forKey: ServerRegistry.legacyMigrationCompletedKey))
    }

    func testLegacyMigrationDoesNotResurrectDeletedServerWhenRegistryIsEmpty() async throws {
        userDefaults.set("https://legacy.example.com", forKey: APIClient.serverURLKey)
        let registry = ServerRegistry(userDefaults: userDefaults, migrateLegacyServerURL: false)
        registry.snapshotPurgeHandler = { _ in .purged }
        let server = try registry.add(displayName: "Existing", baseURL: URL(string: "https://existing.example.com")!)

        try await registry.purgeAndRemove(id: server.id)
        let reloaded = ServerRegistry(userDefaults: userDefaults)

        XCTAssertTrue(reloaded.servers.isEmpty)
        XCTAssertNil(reloaded.activeServerID)
        XCTAssertEqual(userDefaults.string(forKey: APIClient.serverURLKey), "https://legacy.example.com")
    }

    func testDefaultHTTPSPortIsDeduplicated() throws {
        let registry = ServerRegistry(userDefaults: userDefaults, migrateLegacyServerURL: false)
        _ = try registry.add(displayName: "One", baseURL: URL(string: "https://print.example.com")!)

        XCTAssertThrowsError(
            try registry.add(displayName: "Duplicate", baseURL: URL(string: "https://print.example.com:443")!)
        ) { error in
            XCTAssertEqual(error as? ServerRegistryError, .duplicateURL("https://print.example.com"))
        }
    }

    func testDefaultHTTPPortIsDeduplicated() throws {
        let registry = ServerRegistry(userDefaults: userDefaults, migrateLegacyServerURL: false)
        _ = try registry.add(displayName: "One", baseURL: URL(string: "http://printfarmer.local")!)

        XCTAssertThrowsError(
            try registry.add(displayName: "Duplicate", baseURL: URL(string: "http://printfarmer.local:80")!)
        ) { error in
            XCTAssertEqual(error as? ServerRegistryError, .duplicateURL("http://printfarmer.local"))
        }
    }

    func testServiceContainerSwitchesWhenActiveServerChanges() async throws {
        let registry = ServerRegistry(userDefaults: userDefaults, migrateLegacyServerURL: false)
        let first = try registry.add(displayName: "One", baseURL: URL(string: "https://one.example.com")!)
        let second = try registry.add(displayName: "Two", baseURL: URL(string: "https://two.example.com")!)
        try registry.setActive(id: first.id)

        let credentialsStore = isolatedCredentialsStore()
        credentialsStore.save(ServerCredentials(accessToken: "token-one", expiresAt: nil), serverId: first.id)
        credentialsStore.save(ServerCredentials(accessToken: "token-two", expiresAt: nil), serverId: second.id)
        let signalRRecorder = SignalRRecorder()
        let container = switchingTestContainer(
            registry: registry,
            credentialsStore: credentialsStore,
            signalRRecorder: signalRRecorder
        )

        try registry.setActive(id: second.id)
        try await waitForBaseURL(second.baseURL, in: container)

        let currentBaseURL = await container.apiClient?.currentBaseURL()
        let currentAccessToken = await container.apiClient?.currentAccessToken()
        XCTAssertEqual(currentBaseURL, second.baseURL)
        XCTAssertEqual(currentAccessToken, "token-two")
        XCTAssertTrue(signalRRecorder.services.first?.disconnectCalled ?? false)
        XCTAssertTrue(signalRRecorder.services.last?.connectCalled ?? false)
    }

    func testNavigationIdentityIsVerifiedAgainstSettledDestinationServer() async throws {
        let registry = ServerRegistry(userDefaults: userDefaults, migrateLegacyServerURL: false)
        let first = try registry.add(
            displayName: "One",
            baseURL: URL(string: "https://one.example.com")!
        )
        let second = try registry.add(
            displayName: "Two",
            baseURL: URL(string: "https://two.example.com")!
        )
        try registry.setActive(id: first.id)

        let credentialsStore = isolatedCredentialsStore()
        credentialsStore.save(
            ServerCredentials(accessToken: "token-one", expiresAt: nil),
            serverId: first.id
        )
        credentialsStore.save(
            ServerCredentials(accessToken: "token-two", expiresAt: nil),
            serverId: second.id
        )
        let container = switchingTestContainer(
            registry: registry,
            credentialsStore: credentialsStore,
            signalRRecorder: SignalRRecorder()
        )
        mockAPIClient.requestHandler = { request in
            if request.url?.path == "/api/auth/me" {
                return (
                    TestData.httpResponse(url: request.url, statusCode: 200),
                    Data(TestJSON.userDTO.utf8)
                )
            }
            return (
                TestData.httpResponse(url: request.url, statusCode: 404),
                Data()
            )
        }

        try registry.setActive(id: second.id)
        try await waitForBaseURL(second.baseURL, in: container)
        let resolution = await container.currentUserForNavigation(
            serverID: second.id,
            generation: container.activeServerGeneration,
            expectedEndpoint: second.normalizedURLString
        )

        guard case .verified(let identity) = resolution else {
            return XCTFail("Expected verified destination identity, got \(resolution)")
        }
        XCTAssertEqual(
            identity.userID,
            UUID(uuidString: "aab2c3d4-e5f6-7890-abcd-ef1234567890")
        )
        XCTAssertEqual(identity.roles, ["Admin"])
        XCTAssertEqual(
            mockAPIClient.capturedRequests.last(where: {
                $0.url?.path == "/api/auth/me"
            })?.url?.host,
            "two.example.com"
        )
    }

    func testNavigationIdentityVerificationCanRetryAfterTransientFailure() async throws {
        let registry = ServerRegistry(userDefaults: userDefaults, migrateLegacyServerURL: false)
        let server = try registry.add(
            displayName: "Farm",
            baseURL: URL(string: "https://farm.example.com")!
        )
        let credentialsStore = isolatedCredentialsStore()
        credentialsStore.save(
            ServerCredentials(accessToken: "token", expiresAt: nil),
            serverId: server.id
        )
        let container = switchingTestContainer(
            registry: registry,
            credentialsStore: credentialsStore,
            signalRRecorder: SignalRRecorder()
        )
        var attempts = 0
        mockAPIClient.requestHandler = { request in
            guard request.url?.path == "/api/auth/me" else {
                return (
                    TestData.httpResponse(url: request.url, statusCode: 404),
                    Data()
                )
            }
            attempts += 1
            if attempts == 1 {
                return (
                    TestData.httpResponse(url: request.url, statusCode: 503),
                    Data()
                )
            }
            return (
                TestData.httpResponse(url: request.url, statusCode: 200),
                Data(TestJSON.userDTO.utf8)
            )
        }

        let offlineResolution = await container.currentUserForNavigation(
            serverID: server.id,
            generation: container.activeServerGeneration,
            expectedEndpoint: server.normalizedURLString
        )
        XCTAssertEqual(offlineResolution, .offline)
        XCTAssertEqual(attempts, 1)

        let verifiedResolution = await container.currentUserForNavigation(
            serverID: server.id,
            generation: container.activeServerGeneration,
            expectedEndpoint: server.normalizedURLString
        )

        guard case .verified(let identity) = verifiedResolution else {
            return XCTFail("Expected retry to verify identity, got \(verifiedResolution)")
        }
        XCTAssertEqual(
            identity.userID,
            UUID(uuidString: "aab2c3d4-e5f6-7890-abcd-ef1234567890")
        )
        XCTAssertEqual(identity.roles, ["Admin"])
        XCTAssertEqual(attempts, 2)
    }

    func testNavigationIdentityResolvesForDemoComposition() async throws {
        let registry = ServerRegistry(userDefaults: userDefaults, migrateLegacyServerURL: false)
        let server = try registry.add(
            displayName: "Demo",
            baseURL: URL(string: "https://demo.printfarmer.local")!
        )
        let container = ServiceContainer.demo(serverRegistry: registry)

        let resolution = await container.currentUserForNavigation(
            serverID: server.id,
            generation: container.activeServerGeneration,
            expectedEndpoint: server.normalizedURLString
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

    func testEndpointEditCannotVerifyAgainstOldServiceComposition() async throws {
        let registry = ServerRegistry(userDefaults: userDefaults, migrateLegacyServerURL: false)
        var server = try registry.add(
            displayName: "Farm",
            baseURL: URL(string: "https://old.example.com")!
        )
        let credentialsStore = isolatedCredentialsStore()
        credentialsStore.save(
            ServerCredentials(accessToken: "token", expiresAt: nil),
            serverId: server.id
        )
        let initialSignalRService = BlockingSignalRService()
        var serviceIndex = 0
        let container = switchingTestContainer(
            registry: registry,
            credentialsStore: credentialsStore,
            signalRRecorder: SignalRRecorder(),
            signalRServiceFactory: { _, _ in
                defer { serviceIndex += 1 }
                return serviceIndex == 0 ? initialSignalRService : MockSignalRService()
            }
        )
        mockAPIClient.requestHandler = { request in
            guard request.url?.path == "/api/auth/me" else {
                return (
                    TestData.httpResponse(url: request.url, statusCode: 404),
                    Data()
                )
            }
            return (
                TestData.httpResponse(url: request.url, statusCode: 200),
                Data(TestJSON.userDTO.utf8)
            )
        }

        server.baseURL = URL(string: "https://new.example.com")!
        try registry.update(server)
        let expectedServer = try XCTUnwrap(registry.activeServer)
        let didStartDisconnect = await waitForSemaphore(
            initialSignalRService.disconnectStarted,
            timeout: 5
        )
        XCTAssertTrue(didStartDisconnect)

        let unsettled = await container.currentUserForNavigation(
            serverID: expectedServer.id,
            generation: container.activeServerGeneration,
            expectedEndpoint: expectedServer.normalizedURLString
        )
        XCTAssertEqual(unsettled, .notSettled)
        XCTAssertFalse(
            mockAPIClient.capturedRequests.contains {
                $0.url?.path == "/api/auth/me"
            },
            "the old endpoint must not be asked to verify the new endpoint's identity"
        )

        initialSignalRService.resumeDisconnect()
        try await waitForBaseURL(expectedServer.baseURL, in: container)

        var verified: NavigationIdentityResolution = .notSettled
        for _ in 0..<40 {
            verified = await container.currentUserForNavigation(
                serverID: expectedServer.id,
                generation: container.activeServerGeneration,
                expectedEndpoint: expectedServer.normalizedURLString
            )
            if case .verified = verified { break }
            try await Task.sleep(for: .milliseconds(25))
        }

        guard case .verified = verified else {
            return XCTFail("Expected the replacement endpoint to be verified, got \(verified)")
        }
        XCTAssertEqual(
            mockAPIClient.capturedRequests.last(where: {
                $0.url?.path == "/api/auth/me"
            })?.url?.host,
            "new.example.com"
        )
    }

    func testServiceContainerReconcilesRapidSwitchesToLatestServer() async throws {
        let registry = ServerRegistry(userDefaults: userDefaults, migrateLegacyServerURL: false)
        let first = try registry.add(displayName: "One", baseURL: URL(string: "https://one.example.com")!)
        let second = try registry.add(displayName: "Two", baseURL: URL(string: "https://two.example.com")!)
        let third = try registry.add(displayName: "Three", baseURL: URL(string: "https://three.example.com")!)
        try registry.setActive(id: first.id)

        let credentialsStore = isolatedCredentialsStore()
        credentialsStore.save(ServerCredentials(accessToken: "token-one", expiresAt: nil), serverId: first.id)
        credentialsStore.save(ServerCredentials(accessToken: "token-two", expiresAt: nil), serverId: second.id)
        credentialsStore.save(ServerCredentials(accessToken: "token-three", expiresAt: nil), serverId: third.id)

        let initialSignalRService = BlockingSignalRService()
        var serviceIndex = 0
        let container = switchingTestContainer(
            registry: registry,
            credentialsStore: credentialsStore,
            signalRRecorder: SignalRRecorder(),
            signalRServiceFactory: { _, _ in
                defer { serviceIndex += 1 }
                return serviceIndex == 0 ? initialSignalRService : MockSignalRService()
            }
        )

        try registry.setActive(id: second.id)
        let didStartDisconnect = await waitForSemaphore(initialSignalRService.disconnectStarted, timeout: 5)
        XCTAssertTrue(didStartDisconnect)

        try registry.setActive(id: third.id)
        initialSignalRService.resumeDisconnect()
        try await waitForBaseURL(third.baseURL, in: container)

        let currentBaseURL = await container.apiClient?.currentBaseURL()
        let currentAccessToken = await container.apiClient?.currentAccessToken()
        XCTAssertEqual(currentBaseURL, third.baseURL)
        XCTAssertEqual(currentAccessToken, "token-three")
        XCTAssertEqual(registry.activeServerID, third.id)
    }

    func testServiceContainerRebuildsPrinterServiceCacheOnSwitch() async throws {
        let registry = ServerRegistry(userDefaults: userDefaults, migrateLegacyServerURL: false)
        let first = try registry.add(displayName: "One", baseURL: URL(string: "https://one.example.com")!)
        let second = try registry.add(displayName: "Two", baseURL: URL(string: "https://two.example.com")!)
        try registry.setActive(id: first.id)

        let credentialsStore = isolatedCredentialsStore()
        let signalRRecorder = SignalRRecorder()
        let container = switchingTestContainer(
            registry: registry,
            credentialsStore: credentialsStore,
            signalRRecorder: signalRRecorder
        )
        let printerID = UUID()
        mockAPIClient.requestHandler = { request in
            let supportsMovement = request.url?.host == "two.example.com"
            let json = Self.capabilitiesJSON(printerID: printerID, supportsMovement: supportsMovement)
            return (TestData.httpResponse(url: request.url, statusCode: 200), Data(json.utf8))
        }

        let firstCapabilities = try await container.printerService.getBackendCapabilities(printerId: printerID)
        XCTAssertFalse(firstCapabilities.supportsMovement)

        try registry.setActive(id: second.id)
        try await waitForBaseURL(second.baseURL, in: container)
        let secondCapabilities = try await container.printerService.getBackendCapabilities(printerId: printerID)

        XCTAssertTrue(secondCapabilities.supportsMovement)
    }

    func testOldInFlightAPIResponseIsIgnoredAfterSwitch() async throws {
        let registry = ServerRegistry(userDefaults: userDefaults, migrateLegacyServerURL: false)
        let first = try registry.add(displayName: "One", baseURL: URL(string: "https://one.example.com")!)
        let second = try registry.add(displayName: "Two", baseURL: URL(string: "https://two.example.com")!)
        try registry.setActive(id: first.id)

        let credentialsStore = isolatedCredentialsStore()
        let signalRRecorder = SignalRRecorder()
        let container = switchingTestContainer(
            registry: registry,
            credentialsStore: credentialsStore,
            signalRRecorder: signalRRecorder
        )
        let oldClient = try XCTUnwrap(container.apiClient)
        let requestStarted = DispatchSemaphore(value: 0)
        let allowResponse = DispatchSemaphore(value: 0)
        mockAPIClient.requestHandler = { request in
            requestStarted.signal()
            _ = allowResponse.wait(timeout: .now() + 5)
            return (TestData.httpResponse(url: request.url, statusCode: 200), Data(TestJSON.printerArray.utf8))
        }

        let requestTask = Task {
            let _: [Printer] = try await oldClient.get("/api/printers")
        }
        let didStartRequest = await waitForSemaphore(requestStarted, timeout: 5)
        XCTAssertTrue(didStartRequest)

        try registry.setActive(id: second.id)
        try await waitForBaseURL(second.baseURL, in: container)
        allowResponse.signal()

        do {
            try await requestTask.value
            XCTFail("Expected stale server response to be ignored")
        } catch let error as NetworkError {
            guard case .staleServerResponse = error else {
                XCTFail("Expected staleServerResponse, got \(error)")
                return
            }
        }
    }

    func testOldInFlightGetDataResponseIsIgnoredAfterSwitch() async throws {
        let registry = ServerRegistry(userDefaults: userDefaults, migrateLegacyServerURL: false)
        let first = try registry.add(displayName: "One", baseURL: URL(string: "https://one.example.com")!)
        let second = try registry.add(displayName: "Two", baseURL: URL(string: "https://two.example.com")!)
        try registry.setActive(id: first.id)

        let credentialsStore = isolatedCredentialsStore()
        let signalRRecorder = SignalRRecorder()
        let container = switchingTestContainer(
            registry: registry,
            credentialsStore: credentialsStore,
            signalRRecorder: signalRRecorder
        )
        let oldClient = try XCTUnwrap(container.apiClient)
        let requestStarted = DispatchSemaphore(value: 0)
        let allowResponse = DispatchSemaphore(value: 0)
        mockAPIClient.requestHandler = { request in
            requestStarted.signal()
            _ = allowResponse.wait(timeout: .now() + 5)
            return (TestData.httpResponse(url: request.url, statusCode: 200), Data("old-server-bytes".utf8))
        }

        let requestTask = Task {
            try await oldClient.getData("/api/printers/\(UUID())/snapshot")
        }
        let didStartRequest = await waitForSemaphore(requestStarted, timeout: 5)
        XCTAssertTrue(didStartRequest)

        try registry.setActive(id: second.id)
        try await waitForBaseURL(second.baseURL, in: container)
        allowResponse.signal()

        do {
            _ = try await requestTask.value
            XCTFail("Expected stale getData response to be ignored")
        } catch let error as NetworkError {
            guard case .staleServerResponse = error else {
                XCTFail("Expected staleServerResponse, got \(error)")
                return
            }
        }
    }

    private func isolatedCredentialsStore() -> ServerCredentialsStore {
        let keychain = KeychainSwift(keyPrefix: "ServiceContainerSwitchingTests_\(UUID().uuidString)_")
        keychain.clear()
        return ServerCredentialsStore(keychain: keychain)
    }

    private func switchingTestContainer(
        registry: ServerRegistry,
        credentialsStore: ServerCredentialsStore,
        signalRRecorder: SignalRRecorder,
        signalRServiceFactory: ServiceContainer.SignalRServiceFactory? = nil
    ) -> ServiceContainer {
        ServiceContainer(
            serverRegistry: registry,
            credentialsStore: credentialsStore,
            userDefaultsBox: AuthServiceUserDefaultsBox(userDefaults),
            apiClientFactory: { baseURL, generation, accessToken, authSessionToken, serverID in
                let identity = accessToken.flatMap { token in
                    serverID.map { AuthenticatedIdentity(accessToken: token, serverID: $0, authSessionToken: authSessionToken) }
                }
                return APIClient(
                    baseURL: baseURL,
                    session: self.mockAPIClient.urlSession,
                    serverGeneration: generation,
                    authenticated: identity
                )
            },
            signalRServiceFactory: { baseURL, client in
                if let signalRServiceFactory {
                    return signalRServiceFactory(baseURL, client)
                }
                let service = MockSignalRService()
                signalRRecorder.append(service)
                return service
            }
        )
    }

    private func waitForSemaphore(_ semaphore: DispatchSemaphore, timeout: TimeInterval) async -> Bool {
        await withCheckedContinuation { continuation in
            DispatchQueue.global().async {
                continuation.resume(returning: semaphore.wait(timeout: .now() + timeout) == .success)
            }
        }
    }

    private func waitForBaseURL(_ expectedURL: URL, in container: ServiceContainer) async throws {
        for _ in 0..<40 {
            if await container.apiClient?.currentBaseURL() == expectedURL {
                return
            }
            try await Task.sleep(for: .milliseconds(25))
        }
        XCTFail("Timed out waiting for ServiceContainer to switch to \(expectedURL.absoluteString)")
    }

    private static func capabilitiesJSON(printerID: UUID, supportsMovement: Bool) -> String {
        """
        {
            "printerId": "\(printerID)",
            "printerName": "Test",
            "backend": "Moonraker",
            "supportsMovement": \(supportsMovement),
            "supportsTemperatureControl": true
        }
        """
    }

    private final class SignalRRecorder {
        private(set) var services: [MockSignalRService] = []

        func append(_ service: MockSignalRService) {
            services.append(service)
        }
    }

    private final class BlockingSignalRService: SignalRServiceProtocol, @unchecked Sendable {
        var connectionState: SignalRConnectionState = .disconnected
        let disconnectStarted = DispatchSemaphore(value: 0)
        private let lock = NSLock()
        private var disconnectContinuation: CheckedContinuation<Void, Never>?

        func connect() async throws {
            connectionState = .connected
        }

        func disconnect() async {
            await withCheckedContinuation { continuation in
                lock.lock()
                disconnectContinuation = continuation
                lock.unlock()
                disconnectStarted.signal()
            }
            connectionState = .disconnected
        }

        @discardableResult
        func onConnectionStateChanged(
            _ handler: @escaping @Sendable (SignalRConnectionState) -> Void
        ) -> (initial: SignalRConnectionState, subscription: SignalRSubscription) {
            (connectionState, SignalRSubscription {})
        }

        @discardableResult
        func onPrinterUpdated(_ handler: @escaping @Sendable (PrinterStatusUpdate) -> Void) -> SignalRSubscription {
            SignalRSubscription {}
        }

        @discardableResult
        func onJobQueueUpdated(_ handler: @escaping @Sendable (JobQueueUpdate) -> Void) -> SignalRSubscription {
            SignalRSubscription {}
        }

        @discardableResult
        func onAttentionChanged(_ handler: @escaping @Sendable (AttentionChangedEvent) -> Void) -> SignalRSubscription {
            SignalRSubscription {}
        }

        @discardableResult
        func onFilamentCoverageChanged(
            _ handler: @escaping @Sendable (FilamentCoverageChangedEvent) -> Void
        ) -> SignalRSubscription {
            SignalRSubscription {}
        }

        @discardableResult
        func onTaskInvalidated(
            _ handler: @escaping @Sendable (ShiftTaskInvalidation) -> Void
        ) -> SignalRSubscription {
            SignalRSubscription {}
        }

        func onFallbackGroupsUpdated(_ handler: @escaping @Sendable (FallbackGroupsUpdatedEvent) -> Void) {}

        func resumeDisconnect() {
            lock.lock()
            let continuation = disconnectContinuation
            disconnectContinuation = nil
            lock.unlock()
            continuation?.resume()
        }
    }
}
