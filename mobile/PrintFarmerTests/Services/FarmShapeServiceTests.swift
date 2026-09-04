import XCTest
import KeychainSwift
import Observation
@testable import PrintFarmer

@MainActor
final class FarmShapeServiceTests: XCTestCase {
    private var suiteName: String!
    private var userDefaults: UserDefaults!
    private var store: FarmShapeStore!
    private var serverID: UUID!

    override func setUp() {
        super.setUp()
        suiteName = "FarmShapeServiceTests.\(UUID().uuidString)"
        userDefaults = UserDefaults(suiteName: suiteName)!
        store = FarmShapeStore(userDefaults: userDefaults)
        serverID = UUID()
    }

    override func tearDown() {
        userDefaults.removePersistentDomain(forName: suiteName)
        serverID = nil
        store = nil
        userDefaults = nil
        suiteName = nil
        super.tearDown()
    }

    func testFullPayloadUsesFarmShapeEndpointAndResolvesSession() async {
        let mock = MockAPIClient()
        mock.stubResponse(json: """
        {
            "accountCount": 2,
            "locationCount": 3,
            "printerCount": 21
        }
        """)
        let service = FarmShapeService(
            apiClient: mock.apiClient,
            serverID: serverID,
            store: store
        )

        await service.resolveForAuthenticatedSession(
            serverID: serverID,
            timeout: .seconds(1)
        )

        let expected = FarmShape(accountCount: 2, locationCount: 3, printerCount: 21)
        XCTAssertEqual(service.sessionShape, expected)
        XCTAssertEqual(service.latestShape, expected)
        XCTAssertTrue(service.isSessionResolved)
        XCTAssertEqual(store.shape(serverID: serverID), expected)
        XCTAssertEqual(mock.capturedRequests.map(\.url?.path), ["/api/system/farm-shape"])
    }

    func testUnauthorizedResolvesUnknownSilently() async {
        await assertFailureResolvesUnknown(statusCode: 401)
    }

    func testOlderServerNotFoundResolvesUnknownSilently() async {
        await assertFailureResolvesUnknown(statusCode: 404)
    }

    func testTransportFailureResolvesUnknownSilently() async {
        let service = FarmShapeService(
            serverID: serverID,
            store: store,
            fetchShape: {
                throw URLError(.notConnectedToInternet)
            }
        )

        await service.resolveForAuthenticatedSession(
            serverID: serverID,
            timeout: .seconds(1)
        )

        XCTAssertNil(service.sessionShape)
        XCTAssertNil(service.latestShape)
        XCTAssertTrue(service.isSessionResolved)
    }

    func testServerFailureResolvesUnknownWithoutPersisting() async {
        await assertFailureResolvesUnknown(statusCode: 500)
    }

    func testMalformedPayloadResolvesUnknownWithoutPersisting() async {
        let mock = MockAPIClient()
        mock.stubResponse(json: "{}")
        let service = FarmShapeService(
            apiClient: mock.apiClient,
            serverID: serverID,
            store: store
        )

        await service.resolveForAuthenticatedSession(
            serverID: serverID,
            timeout: .seconds(1)
        )

        XCTAssertNil(service.sessionShape)
        XCTAssertNil(service.latestShape)
        XCTAssertTrue(service.isSessionResolved)
        XCTAssertNil(store.shape(serverID: serverID))
    }

    func testTimeoutResolvesUnknownWithoutWaitingForLateResponse() async {
        let request = AsyncBarrier()
        defer { request.close() }
        let lateShape = FarmShape(accountCount: 4, locationCount: 2, printerCount: 18)
        let service = FarmShapeService(
            serverID: serverID,
            store: store,
            fetchShape: {
                await request.arriveAndWait()
                return lateShape
            },
            sleep: { _ in }
        )

        await service.resolveForAuthenticatedSession(
            serverID: serverID,
            timeout: .milliseconds(1)
        )

        XCTAssertNil(service.sessionShape)
        XCTAssertNil(service.latestShape)
        XCTAssertTrue(service.isSessionResolved)

        let latestChanged = expectation(description: "late shape observed")
        withObservationTracking {
            _ = service.latestShape
        } onChange: {
            latestChanged.fulfill()
        }
        request.release()
        await fulfillment(of: [latestChanged], timeout: 2)
        XCTAssertNil(service.sessionShape)
        XCTAssertEqual(service.latestShape, lateShape)
        XCTAssertEqual(store.shape(serverID: serverID), lateShape)
    }

    func testDeletedServerRejectsLateShapePersistence() async throws {
        let registry = ServerRegistry(
            userDefaults: userDefaults,
            migrateLegacyServerURL: false
        )
        let server = try registry.add(
            displayName: "Deleted",
            baseURL: URL(string: "https://deleted.example.com")!
        )
        let snapshotRoot = FarmSnapshotFixtures.tempRoot()
        addTeardownBlock {
            try? FileManager.default.removeItem(at: snapshotRoot)
        }
        let snapshotAuthority = FarmSnapshotFixtures.makeAuthority(
            tombstoneDefaults: userDefaults
        )
        let request = AsyncBarrier()
        defer { request.close() }
        let lateShape = FarmShape(accountCount: 4, locationCount: 2, printerCount: 18)
        let service = FarmShapeService(
            serverID: server.id,
            store: store,
            fetchShape: {
                await request.arriveAndWait()
                return lateShape
            },
            sleep: { _ in }
        )
        let container = ServiceContainer(
            serverRegistry: registry,
            observeRegistry: false,
            farmSnapshotAuthority: snapshotAuthority,
            farmSnapshotStore: FarmSnapshotStore(
                authority: snapshotAuthority,
                rootURL: snapshotRoot
            ),
            farmShapeStore: store,
            synchronizeOfflineQueueOnStartup: false
        )
        registry.certificatePinPurgeHandler = { _, _ in true }
        _ = container

        let refreshTask = Task {
            await service.refreshLatest(serverID: server.id)
        }
        await request.waitUntilArrived()
        try await registry.purgeAndRemove(id: server.id)

        request.release()
        await refreshTask.value

        XCTAssertTrue(registry.servers.isEmpty)
        XCTAssertNil(store.shape(serverID: server.id))
        XCTAssertNil(
            FarmShapeStore(userDefaults: userDefaults).shape(serverID: server.id),
            "a fresh store must not find persistence recreated by the late response"
        )
    }

    func testEndpointChangePurgesShapeAndRejectsLateOldEndpointWrite() async throws {
        let registry = ServerRegistry(
            userDefaults: userDefaults,
            migrateLegacyServerURL: false
        )
        var server = try registry.add(
            displayName: "Farm",
            baseURL: URL(string: "https://old.example.com")!
        )
        let initialShape = FarmShape(accountCount: 3, locationCount: 2, printerCount: 12)
        let staleShape = FarmShape(accountCount: 7, locationCount: 4, printerCount: 40)
        let replacementShape = FarmShape(accountCount: 1, locationCount: 1, printerCount: 2)
        store.setShape(initialShape, serverID: server.id)

        let request = AsyncBarrier()
        defer { request.close() }
        let staleService = FarmShapeService(
            serverID: server.id,
            store: store,
            fetchShape: {
                await request.arriveAndWait()
                return staleShape
            }
        )
        let container = ServiceContainer(
            serverRegistry: registry,
            observeRegistry: false,
            farmShapeStore: store,
            synchronizeOfflineQueueOnStartup: false
        )
        _ = container

        let refresh = Task {
            await staleService.refreshLatest(serverID: server.id)
        }
        await request.waitUntilArrived()

        server.baseURL = URL(string: "https://new.example.com")!
        try registry.update(server)
        XCTAssertNil(store.shape(serverID: server.id))

        request.release()
        await refresh.value
        XCTAssertNil(
            store.shape(serverID: server.id),
            "the old endpoint must not repopulate the invalidated server identity"
        )
        XCTAssertEqual(
            staleService.latestShape,
            initialShape,
            "an authority-fenced write must not publish stale endpoint data in memory"
        )

        let replacementService = FarmShapeService(
            serverID: server.id,
            store: store,
            fetchShape: { replacementShape }
        )
        await replacementService.refreshLatest(serverID: server.id)
        XCTAssertEqual(store.shape(serverID: server.id), replacementShape)
    }

    func testAuthorityRejectedStartupFetchStillResolvesSession() async {
        let initialShape = FarmShape(accountCount: 1, locationCount: 1, printerCount: 3)
        let staleShape = FarmShape(accountCount: 4, locationCount: 3, printerCount: 30)
        store.setShape(initialShape, serverID: serverID)
        let request = AsyncBarrier()
        defer { request.close() }
        let service = FarmShapeService(
            serverID: serverID,
            store: store,
            fetchShape: {
                await request.arriveAndWait()
                return staleShape
            }
        )
        service.beginSession(authToken: 1)

        let resolution = Task {
            await service.resolveForAuthenticatedSession(
                serverID: serverID,
                timeout: .seconds(1)
            )
        }
        await request.waitUntilArrived()
        store.invalidateShape(serverID: serverID)
        request.release()
        await resolution.value

        XCTAssertTrue(service.isSessionResolved)
        XCTAssertEqual(service.latestShape, initialShape)
        XCTAssertNil(store.shape(serverID: serverID))
    }

    func testUnknownDiffersFromKnownShapeOfOne() {
        let unknown: FarmShape? = nil
        let knownOne = FarmShape(accountCount: 1, locationCount: 1, printerCount: 1)

        XCTAssertNotEqual(unknown, knownOne)
    }

    func testStoreIsolatesShapesByServer() {
        let otherServerID = UUID()
        let first = FarmShape(accountCount: 1, locationCount: 1, printerCount: 4)
        let second = FarmShape(accountCount: 8, locationCount: 3, printerCount: 42)

        store.setShape(first, serverID: serverID)
        store.setShape(second, serverID: otherServerID)

        XCTAssertEqual(store.shape(serverID: serverID), first)
        XCTAssertEqual(store.shape(serverID: otherServerID), second)
    }

    func testPersistedShapeIsAvailableImmediatelyOnRelaunch() {
        let persisted = FarmShape(accountCount: 3, locationCount: 2, printerCount: 12)
        store.setShape(persisted, serverID: serverID)

        let relaunched = FarmShapeService(
            serverID: serverID,
            store: store,
            fetchShape: {
                return persisted
            }
        )

        XCTAssertEqual(relaunched.sessionShape, persisted)
        XCTAssertEqual(relaunched.latestShape, persisted)
        XCTAssertTrue(relaunched.isSessionResolved)
    }

    func testNewAuthTokenAllowsFreshResolutionForSameServerLogin() async {
        let mock = MockAPIClient()
        mock.stubResponse(json: """
        {
            "accountCount": 1,
            "locationCount": 1,
            "printerCount": 2
        }
        """)
        let service = FarmShapeService(
            apiClient: mock.apiClient,
            serverID: serverID,
            store: store
        )
        service.beginSession(authToken: 1)
        await service.resolveForAuthenticatedSession(
            serverID: serverID,
            timeout: .seconds(1)
        )

        service.beginSession(authToken: 2)
        mock.stubResponse(json: """
        {
            "accountCount": 2,
            "locationCount": 3,
            "printerCount": 9
        }
        """)
        await service.resolveForAuthenticatedSession(
            serverID: serverID,
            timeout: .seconds(1)
        )

        XCTAssertEqual(
            service.sessionShape,
            FarmShape(accountCount: 2, locationCount: 3, printerCount: 9)
        )
        XCTAssertEqual(
            mock.capturedRequests.filter {
                $0.url?.path == "/api/system/farm-shape"
            }.count,
            2
        )
    }

    func testResetDiscardsInFlightLatestRefresh() async {
        let persisted = FarmShape(accountCount: 1, locationCount: 1, printerCount: 2)
        let changed = FarmShape(accountCount: 2, locationCount: 3, printerCount: 9)
        store.setShape(persisted, serverID: serverID)
        let request = AsyncBarrier()
        defer { request.close() }
        let service = FarmShapeService(
            serverID: serverID,
            store: store,
            fetchShape: {
                await request.arriveAndWait()
                return changed
            }
        )

        let refresh = Task {
            await service.refreshLatest(serverID: serverID)
        }
        await request.waitUntilArrived()
        service.resetSession()
        request.release()
        await refresh.value

        XCTAssertNil(service.latestShape)
        XCTAssertEqual(store.shape(serverID: serverID), persisted)
    }

    func testSwitchingActiveServerSelectsThatServersPersistedShape() throws {
        let registry = ServerRegistry(
            userDefaults: userDefaults,
            migrateLegacyServerURL: false
        )
        let serverA = try registry.add(
            displayName: "A",
            baseURL: URL(string: "https://a.example.com")!
        )
        let serverB = try registry.add(
            displayName: "B",
            baseURL: URL(string: "https://b.example.com")!
        )
        let shapeA = FarmShape(accountCount: 1, locationCount: 2, printerCount: 3)
        let shapeB = FarmShape(accountCount: 4, locationCount: 5, printerCount: 6)
        store.setShape(shapeA, serverID: serverA.id)
        store.setShape(shapeB, serverID: serverB.id)
        try registry.setActive(id: serverA.id)
        let container = ServiceContainer(
            serverRegistry: registry,
            observeRegistry: false,
            farmShapeStore: store,
            synchronizeOfflineQueueOnStartup: false
        )
        XCTAssertEqual(container.farmShapeService.sessionShape, shapeA)

        try registry.setActive(id: serverB.id)
        container.switchToReal()

        XCTAssertEqual(container.farmShapeService.sessionShape, shapeB)
    }

    func testAuthenticatedServerSwitchResolvesFreshSessionShape() async throws {
        let registry = ServerRegistry(
            userDefaults: userDefaults,
            migrateLegacyServerURL: false
        )
        let serverA = try registry.add(
            displayName: "A",
            baseURL: URL(string: "https://a.example.com")!
        )
        let serverB = try registry.add(
            displayName: "B",
            baseURL: URL(string: "https://b.example.com")!
        )
        try registry.setActive(id: serverA.id)
        let keychain = KeychainSwift(keyPrefix: "FarmShapeServiceTests.\(UUID().uuidString).")
        defer { keychain.clear() }
        let credentials = ServerCredentialsStore(keychain: keychain)
        credentials.save(
            ServerCredentials(
                accessToken: "token-b",
                expiresAt: Date().addingTimeInterval(3_600)
            ),
            serverId: serverB.id
        )
        let ownerStore = FarmSnapshotOwnerStore(userDefaults: userDefaults)
        let ownerID = UUID()
        ownerStore.setOwner(userID: ownerID, serverID: serverB.id)
        let snapshotRoot = FarmSnapshotFixtures.tempRoot()
        addTeardownBlock {
            try? FileManager.default.removeItem(at: snapshotRoot)
        }
        let snapshotAuthority = FarmSnapshotFixtures.makeAuthority(
            tombstoneDefaults: userDefaults
        )
        let snapshotStore = FarmSnapshotStore(
            authority: snapshotAuthority,
            rootURL: snapshotRoot
        )
        let mock = MockAPIClient()
        let shapeRequest = AsyncBarrier()
        let capabilitiesRequest = AsyncBarrier()
        defer {
            shapeRequest.close()
            capabilitiesRequest.close()
        }
        mock.asyncRequestHandler = { request in
            switch request.url?.path {
            case "/api/system/farm-shape":
                await shapeRequest.arriveAndWait()
                return (
                    TestData.httpResponse(url: request.url, statusCode: 200),
                    Data(
                        #"{"accountCount":4,"locationCount":5,"printerCount":6}"#.utf8
                    )
                )
            case "/api/system/capabilities":
                await capabilitiesRequest.arriveAndWait()
                return (
                    TestData.httpResponse(url: request.url, statusCode: 200),
                    Data(#"{"operatorFeatures":{"attentionEnabled":true}}"#.utf8)
                )
            default:
                return (
                    TestData.httpResponse(url: request.url, statusCode: 404),
                    Data("{}".utf8)
                )
            }
        }
        let container = ServiceContainer(
            serverRegistry: registry,
            credentialsStore: credentials,
            userDefaultsBox: AuthServiceUserDefaultsBox(userDefaults),
            observeRegistry: false,
            farmSnapshotAuthority: snapshotAuthority,
            farmSnapshotStore: snapshotStore,
            farmSnapshotOwnerStore: ownerStore,
            farmShapeStore: store,
            synchronizeOfflineQueueOnStartup: false,
            apiClientFactory: { baseURL, generation, accessToken, authSessionToken, serverID in
                let identity = accessToken.flatMap { token in
                    serverID.map {
                        AuthenticatedIdentity(
                            accessToken: token,
                            serverID: $0,
                            authSessionToken: authSessionToken
                        )
                    }
                }
                return APIClient(
                    baseURL: baseURL,
                    session: mock.urlSession,
                    serverGeneration: generation,
                    authenticated: identity
                )
            },
            signalRServiceFactory: { _, _ in MockSignalRService() }
        )
        let authToken = container.authOperationEpoch.advance()
        try registry.setActive(id: serverB.id)

        let switchTask = Task {
            await container.switchToServer(serverB)
        }
        await shapeRequest.waitUntilArrived()
        await capabilitiesRequest.waitUntilArrived()
        shapeRequest.release()
        capabilitiesRequest.release()
        await switchTask.value
        XCTAssertEqual(
            container.currentOfflineWriteReplayIdentity,
            OfflineWriteReplayIdentity(serverID: serverB.id, userID: ownerID)
        )
        XCTAssertEqual(
            mock.capturedRequests.filter {
                $0.url?.path == "/api/system/capabilities"
            }.count,
            1
        )

        await container.prepareAuthenticatedStartup(authToken: authToken)
        container.authorizeOfflineWriteReplayBinding()
        await container.syncOfflineWriteQueue()
        XCTAssertEqual(
            mock.capturedRequests.filter {
                $0.url?.path == "/api/system/capabilities"
            }.count,
            1
        )

        container.capabilitiesService = SystemCapabilitiesService(apiClient: mock.apiClient)
        container.authorizeOfflineWriteReplayBinding()
        await container.syncOfflineWriteQueue()
        let readiness = BackendReadinessChecker(timeout: .seconds(1))
        _ = await readiness.check(
            plan: BackendReadinessPlan(
                capabilitiesService: container.capabilitiesService,
                probes: []
            )
        )

        XCTAssertEqual(
            container.farmShapeService.sessionShape,
            FarmShape(accountCount: 4, locationCount: 5, printerCount: 6)
        )
        XCTAssertEqual(
            mock.capturedRequests.filter {
                $0.url?.path == "/api/system/farm-shape"
            }.count,
            1
        )
        XCTAssertEqual(
            mock.capturedRequests.filter {
                $0.url?.path == "/api/system/capabilities"
            }.count,
            2
        )
    }

    func testLateMidSessionChangeUpdatesPersistenceWithoutChangingSessionShape() async {
        let initial = FarmShape(accountCount: 1, locationCount: 1, printerCount: 5)
        let changed = FarmShape(accountCount: 2, locationCount: 2, printerCount: 9)
        store.setShape(initial, serverID: serverID)
        let request = AsyncBarrier()
        defer { request.close() }
        let service = FarmShapeService(
            serverID: serverID,
            store: store,
            fetchShape: {
                await request.arriveAndWait()
                return changed
            },
            sleep: { _ in }
        )

        await service.resolveForAuthenticatedSession(
            serverID: serverID,
            timeout: .milliseconds(1)
        )
        XCTAssertEqual(service.sessionShape, initial)

        let latestChanged = expectation(description: "changed shape observed")
        withObservationTracking {
            _ = service.latestShape
        } onChange: {
            latestChanged.fulfill()
        }
        request.release()
        await fulfillment(of: [latestChanged], timeout: 2)

        XCTAssertEqual(service.sessionShape, initial)
        XCTAssertEqual(service.latestShape, changed)
        XCTAssertEqual(store.shape(serverID: serverID), changed)
    }

    func testAuthenticatedStartupBeginsShapeAndCapabilitiesInParallel() async throws {
        let registrySuiteName = "FarmShapeServiceTests.Registry.\(UUID().uuidString)"
        let registryDefaults = UserDefaults(suiteName: registrySuiteName)!
        defer {
            registryDefaults.removePersistentDomain(forName: registrySuiteName)
        }
        let registry = ServerRegistry(
            userDefaults: registryDefaults,
            migrateLegacyServerURL: false
        )
        let server = try registry.add(
            displayName: "Test",
            baseURL: URL(string: "https://print.example.com")!
        )
        let container = ServiceContainer(
            serverRegistry: registry,
            observeRegistry: false,
            synchronizeOfflineQueueOnStartup: false
        )
        let shape = BlockingFarmShapeService()
        let capabilities = BlockingCapabilitiesService()
        defer {
            shape.gate.close()
            capabilities.gate.close()
        }
        container.farmShapeService = shape
        container.capabilitiesService = capabilities
        let authToken = container.authOperationEpoch.advance()

        let preparation = Task {
            await container.prepareAuthenticatedStartup(authToken: authToken)
        }
        await shape.gate.waitUntilArrived()
        await capabilities.gate.waitUntilArrived()

        shape.gate.release()
        XCTAssertFalse(capabilities.completed)

        capabilities.gate.release()
        await preparation.value
        XCTAssertTrue(capabilities.completed)
        XCTAssertEqual(shape.serverID, server.id)
    }

    private func assertFailureResolvesUnknown(statusCode: Int) async {
        let mock = MockAPIClient()
        mock.stubResponse(json: "{}", statusCode: statusCode)
        let service = FarmShapeService(
            apiClient: mock.apiClient,
            serverID: serverID,
            store: store
        )

        await service.resolveForAuthenticatedSession(
            serverID: serverID,
            timeout: .seconds(1)
        )

        XCTAssertNil(service.sessionShape)
        XCTAssertNil(service.latestShape)
        XCTAssertTrue(service.isSessionResolved)
        XCTAssertNil(store.shape(serverID: serverID))
    }

}

@MainActor
private final class BlockingFarmShapeService: FarmShapeServiceProtocol, @unchecked Sendable {
    let gate = AsyncBarrier()
    private(set) var sessionShape: FarmShape?
    private(set) var latestShape: FarmShape?
    private(set) var isSessionResolved = false
    private(set) var serverID: UUID?

    func beginSession(authToken: Int) {}

    func resolveForAuthenticatedSession(serverID: UUID, timeout: Duration) async {
        self.serverID = serverID
        await gate.arriveAndWait()
        isSessionResolved = true
    }

    func refreshLatest(serverID: UUID) async {}

    func resetSession() {
        sessionShape = nil
        latestShape = nil
        isSessionResolved = true
    }
}

@MainActor
private final class BlockingCapabilitiesService:
    SystemCapabilitiesServiceProtocol,
    @unchecked Sendable
{
    let gate = AsyncBarrier()
    private(set) var resolved = ResolvedSystemCapabilities.defaults
    private(set) var completed = false

    func refresh() async -> SystemCapabilitiesRefreshOutcome {
        await gate.arriveAndWait()
        completed = true
        return .loaded
    }
}
