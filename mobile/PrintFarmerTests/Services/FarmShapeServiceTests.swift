import XCTest
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

        request.release()
        await waitUntil { service.latestShape == lateShape }
        XCTAssertNil(service.sessionShape)
        XCTAssertEqual(store.shape(serverID: serverID), lateShape)
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

        request.release()
        await waitUntil { service.latestShape == changed }

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

        let preparation = Task {
            await container.prepareAuthenticatedStartup()
        }
        await shape.gate.waitUntilArrived()
        await capabilities.gate.waitUntilArrived()

        shape.gate.release()
        XCTAssertFalse(capabilities.completed)

        capabilities.gate.release()
        await preparation.value
        await waitUntil { capabilities.completed }
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

    private func waitUntil(
        attempts: Int = 100,
        _ predicate: @escaping @MainActor () -> Bool
    ) async {
        for _ in 0..<attempts {
            if predicate() {
                return
            }
            await Task.yield()
        }
        XCTFail("Condition did not become true")
    }
}

@MainActor
private final class BlockingFarmShapeService: FarmShapeServiceProtocol, @unchecked Sendable {
    let gate = AsyncBarrier()
    private(set) var sessionShape: FarmShape?
    private(set) var latestShape: FarmShape?
    private(set) var isSessionResolved = false
    private(set) var serverID: UUID?

    func resolveForAuthenticatedSession(serverID: UUID, timeout: Duration) async {
        self.serverID = serverID
        await gate.arriveAndWait()
        isSessionResolved = true
    }

    func refreshLatest(serverID: UUID) async {}
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
