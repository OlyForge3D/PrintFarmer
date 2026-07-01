import XCTest
@testable import PrintFarmer

@MainActor
final class ServerManagementViewModelTests: XCTestCase {
    private var userDefaults: UserDefaults!
    private var suiteName: String!

    override func setUp() {
        super.setUp()
        suiteName = "ServerManagementViewModelTests-\(UUID().uuidString)"
        userDefaults = UserDefaults(suiteName: suiteName)!
        userDefaults.removePersistentDomain(forName: suiteName)
    }

    override func tearDown() {
        userDefaults.removePersistentDomain(forName: suiteName)
        userDefaults = nil
        suiteName = nil
        MockURLProtocol.reset()
        super.tearDown()
    }

    func testAddServerNormalizesURLChecksHealthAndPersistsStatus() async throws {
        let registry = ServerRegistry(userDefaults: userDefaults, migrateLegacyServerURL: false)
        let viewModel = ServerManagementViewModel(
            registry: registry,
            healthChecker: StubHealthChecker(result: .reachable(statusCode: 200)),
            now: { Date(timeIntervalSince1970: 1_234) }
        )
        viewModel.prepareForAdd()
        viewModel.displayName = " Farm "
        viewModel.serverURL = "PRINT.example.com/"

        let didSave = await viewModel.save()

        XCTAssertTrue(didSave)
        let server = try XCTUnwrap(registry.servers.first)
        XCTAssertEqual(server.displayName, "Farm")
        XCTAssertEqual(server.normalizedURLString, "https://print.example.com")
        XCTAssertEqual(server.lastKnownStatus, "Reachable")
        XCTAssertEqual(server.lastCheckedAt, Date(timeIntervalSince1970: 1_234))
        XCTAssertEqual(registry.activeServerID, server.id)
        XCTAssertEqual(viewModel.healthState, .reachable("Reachable (HTTP 200)"))
    }

    func testValidationRejectsInvalidURL() {
        let registry = ServerRegistry(userDefaults: userDefaults, migrateLegacyServerURL: false)
        let viewModel = ServerManagementViewModel(registry: registry, healthChecker: StubHealthChecker(result: .reachable()))
        viewModel.prepareForAdd()
        viewModel.serverURL = "not a url !@#"

        XCTAssertEqual(viewModel.urlValidationError, "Enter a valid URL (e.g. https://print.example.com)")
        XCTAssertFalse(viewModel.canSave)
    }

    func testDuplicateURLIsSurfacedBeforeSaveAndByRegistry() async throws {
        let registry = ServerRegistry(userDefaults: userDefaults, migrateLegacyServerURL: false)
        _ = try registry.add(displayName: "One", baseURL: URL(string: "https://print.example.com")!)
        let viewModel = ServerManagementViewModel(registry: registry, healthChecker: StubHealthChecker(result: .reachable()))
        viewModel.prepareForAdd()
        viewModel.displayName = "Duplicate"
        viewModel.serverURL = "https://PRINT.example.com/"

        XCTAssertEqual(viewModel.duplicateValidationError, "This server is already registered.")
        let didSave = await viewModel.save()

        XCTAssertFalse(didSave)
        XCTAssertEqual(viewModel.errorMessage, "This server is already registered.")
        XCTAssertEqual(registry.servers.count, 1)
    }

    func testHealthCheckUsesHealthEndpointAndStoresReachableState() async throws {
        let session = MockURLProtocol.mockSession()
        MockURLProtocol.requestHandler = { request in
            XCTAssertEqual(request.url?.path, "/health")
            let response = HTTPURLResponse(
                url: request.url!,
                statusCode: 204,
                httpVersion: nil,
                headerFields: nil
            )!
            return (response, Data())
        }

        let registry = ServerRegistry(userDefaults: userDefaults, migrateLegacyServerURL: false)
        let viewModel = ServerManagementViewModel(
            registry: registry,
            healthChecker: URLSessionServerHealthChecker(session: session)
        )
        viewModel.prepareForAdd()
        viewModel.serverURL = "https://print.example.com"

        let result = await viewModel.checkHealth()

        XCTAssertEqual(result, ServerHealthCheckResult(isReachable: true, statusCode: 204, message: "Reachable (HTTP 204)"))
        XCTAssertEqual(viewModel.healthState, .reachable("Reachable (HTTP 204)"))
        XCTAssertEqual(MockURLProtocol.capturedRequests.count, 1)
    }

    func testHealthCheckFallsBackToHealthzAfterNetworkFailure() async throws {
        let session = MockURLProtocol.mockSession()
        var requestCount = 0
        MockURLProtocol.requestHandler = { request in
            requestCount += 1
            if request.url?.path == "/health" {
                throw URLError(.cannotConnectToHost)
            }
            XCTAssertEqual(request.url?.path, "/healthz")
            let response = HTTPURLResponse(
                url: request.url!,
                statusCode: 200,
                httpVersion: nil,
                headerFields: nil
            )!
            return (response, Data())
        }

        let registry = ServerRegistry(userDefaults: userDefaults, migrateLegacyServerURL: false)
        let viewModel = ServerManagementViewModel(
            registry: registry,
            healthChecker: URLSessionServerHealthChecker(session: session)
        )
        viewModel.prepareForAdd()
        viewModel.serverURL = "https://print.example.com"

        let result = await viewModel.checkHealth()

        XCTAssertEqual(result?.isReachable, true)
        XCTAssertEqual(result?.statusCode, 200)
        XCTAssertEqual(requestCount, 2)
    }

    func testDeleteActiveServerHandsOffToAnotherServer() throws {
        let registry = ServerRegistry(userDefaults: userDefaults, migrateLegacyServerURL: false)
        let first = try registry.add(displayName: "One", baseURL: URL(string: "https://one.example.com")!)
        let second = try registry.add(displayName: "Two", baseURL: URL(string: "https://two.example.com")!)
        try registry.setActive(id: first.id)
        let viewModel = ServerManagementViewModel(registry: registry, healthChecker: StubHealthChecker(result: .reachable()))

        viewModel.delete(first)

        XCTAssertEqual(registry.servers.map(\.id), [second.id])
        XCTAssertEqual(registry.activeServerID, second.id)
        XCTAssertNil(viewModel.errorMessage)
    }

    func testEditServerRevalidatesURLAndUpdatesStatus() async throws {
        let registry = ServerRegistry(userDefaults: userDefaults, migrateLegacyServerURL: false)
        let server = try registry.add(displayName: "Old", baseURL: URL(string: "https://old.example.com")!)
        let viewModel = ServerManagementViewModel(
            registry: registry,
            healthChecker: StubHealthChecker(result: .unreachable("The Internet connection appears to be offline.")),
            now: { Date(timeIntervalSince1970: 2_000) }
        )
        viewModel.prepareForEdit(server)
        viewModel.displayName = "New"
        viewModel.serverURL = "https://new.example.com/"

        let didSave = await viewModel.save()

        XCTAssertTrue(didSave)
        let updated = try XCTUnwrap(registry.servers.first)
        XCTAssertEqual(updated.displayName, "New")
        XCTAssertEqual(updated.normalizedURLString, "https://new.example.com")
        XCTAssertEqual(updated.lastKnownStatus, "Unreachable")
        XCTAssertEqual(updated.lastCheckedAt, Date(timeIntervalSince1970: 2_000))
    }
}

private struct StubHealthChecker: ServerHealthChecking {
    let result: ServerHealthCheckResult

    func check(baseURL: URL) async -> ServerHealthCheckResult {
        result
    }
}

private extension ServerHealthCheckResult {
    static func reachable(statusCode: Int? = 200) -> ServerHealthCheckResult {
        ServerHealthCheckResult(
            isReachable: true,
            statusCode: statusCode,
            message: statusCode.map { "Reachable (HTTP \($0))" } ?? "Reachable"
        )
    }

    static func unreachable(_ message: String = "Server is unreachable") -> ServerHealthCheckResult {
        ServerHealthCheckResult(isReachable: false, statusCode: nil, message: message)
    }
}
