import XCTest
@testable import PrintFarmer

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
}
