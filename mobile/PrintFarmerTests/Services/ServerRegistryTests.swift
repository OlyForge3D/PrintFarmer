import XCTest
import KeychainSwift
@testable import PrintFarmer

@MainActor
final class ServerRegistryTests: XCTestCase {
    private var userDefaults: UserDefaults!
    private var userDefaultsSuiteName: String!

    override func setUp() {
        super.setUp()
        userDefaultsSuiteName = "ServerRegistryTests-\(UUID().uuidString)"
        userDefaults = UserDefaults(suiteName: userDefaultsSuiteName)!
        userDefaults.removePersistentDomain(forName: userDefaultsSuiteName)
    }

    override func tearDown() {
        userDefaults.removePersistentDomain(forName: userDefaultsSuiteName)
        userDefaults = nil
        userDefaultsSuiteName = nil
        super.tearDown()
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

    func testRemoveActiveServerSelectsNextServer() throws {
        let registry = ServerRegistry(userDefaults: userDefaults, migrateLegacyServerURL: false)
        let first = try registry.add(displayName: "One", baseURL: URL(string: "https://one.example.com")!)
        let second = try registry.add(displayName: "Two", baseURL: URL(string: "https://two.example.com")!)

        try registry.setActive(id: first.id)
        try registry.remove(id: first.id)

        XCTAssertEqual(registry.servers.map(\.id), [second.id])
        XCTAssertEqual(registry.activeServerID, second.id)
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

    func testLegacyMigrationDoesNotResurrectDeletedServerWhenRegistryIsEmpty() throws {
        userDefaults.set("https://legacy.example.com", forKey: APIClient.serverURLKey)
        let registry = ServerRegistry(userDefaults: userDefaults, migrateLegacyServerURL: false)
        let server = try registry.add(displayName: "Existing", baseURL: URL(string: "https://existing.example.com")!)

        try registry.remove(id: server.id)
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
        _ = try registry.add(displayName: "One", baseURL: URL(string: "http://print.example.com")!)

        XCTAssertThrowsError(
            try registry.add(displayName: "Duplicate", baseURL: URL(string: "http://print.example.com:80")!)
        ) { error in
            XCTAssertEqual(error as? ServerRegistryError, .duplicateURL("http://print.example.com"))
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
        MockURLProtocol.requestHandler = { request in
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
        MockURLProtocol.requestHandler = { request in
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

    private func isolatedCredentialsStore() -> ServerCredentialsStore {
        let keychain = KeychainSwift(keyPrefix: "ServiceContainerSwitchingTests_\(UUID().uuidString)_")
        keychain.clear()
        return ServerCredentialsStore(keychain: keychain)
    }

    private func switchingTestContainer(
        registry: ServerRegistry,
        credentialsStore: ServerCredentialsStore,
        signalRRecorder: SignalRRecorder
    ) -> ServiceContainer {
        ServiceContainer(
            serverRegistry: registry,
            credentialsStore: credentialsStore,
            userDefaultsBox: AuthServiceUserDefaultsBox(userDefaults),
            apiClientFactory: { baseURL, generation, accessToken in
                APIClient(
                    baseURL: baseURL,
                    session: MockURLProtocol.mockSession(),
                    serverGeneration: generation,
                    accessToken: accessToken
                )
            },
            signalRServiceFactory: { _, _ in
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
}
