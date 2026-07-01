import XCTest
@testable import PrintFarmer

@MainActor
final class ServerSwitcherViewModelTests: XCTestCase {
    private var userDefaults: UserDefaults!
    private var suiteName: String!

    override func setUp() async throws {
        try await super.setUp()
        suiteName = "ServerSwitcherViewModelTests-\(UUID().uuidString)"
        userDefaults = UserDefaults(suiteName: suiteName)!
        userDefaults.removePersistentDomain(forName: suiteName)
    }

    override func tearDown() async throws {
        userDefaults.removePersistentDomain(forName: suiteName)
        userDefaults = nil
        suiteName = nil
        try await super.tearDown()
    }

    func testActiveServerNameUsesActiveServerDisplayName() throws {
        let registry = ServerRegistry(userDefaults: userDefaults, migrateLegacyServerURL: false)
        let home = try registry.add(displayName: "Home Farm", baseURL: URL(string: "https://home.example.com")!)
        _ = try registry.add(displayName: "Shop Farm", baseURL: URL(string: "https://shop.example.com")!, makeActiveIfNeeded: false)
        try registry.setActive(id: home.id)

        let viewModel = ServerSwitcherViewModel(servers: registry.servers, activeServerID: registry.activeServerID)

        XCTAssertEqual(viewModel.activeServerName, "Home Farm")
        XCTAssertEqual(viewModel.switcherAccessibilityLabel, "Current server: Home Farm. Opens server menu.")
    }

    func testItemsMarkOnlyActiveServer() throws {
        let registry = ServerRegistry(userDefaults: userDefaults, migrateLegacyServerURL: false)
        _ = try registry.add(displayName: "Home Farm", baseURL: URL(string: "https://home.example.com")!)
        let shop = try registry.add(displayName: "Shop Farm", baseURL: URL(string: "https://shop.example.com")!, makeActiveIfNeeded: false)
        try registry.setActive(id: shop.id)

        let viewModel = ServerSwitcherViewModel(servers: registry.servers, activeServerID: registry.activeServerID)

        XCTAssertEqual(viewModel.items.map(\.displayName), ["Home Farm", "Shop Farm"])
        XCTAssertEqual(viewModel.items.map(\.isActive), [false, true])
        XCTAssertEqual(viewModel.items[1].accessibilityLabel, "Shop Farm, active server")
    }

    func testItemsDisambiguateDuplicateDisplayNamesWithHost() throws {
        let registry = ServerRegistry(userDefaults: userDefaults, migrateLegacyServerURL: false)
        _ = try registry.add(displayName: "Farm", baseURL: URL(string: "https://home.example.com")!)
        _ = try registry.add(displayName: "Farm", baseURL: URL(string: "https://shop.example.com")!, makeActiveIfNeeded: false)
        _ = try registry.add(displayName: "Unique Farm", baseURL: URL(string: "https://unique.example.com")!, makeActiveIfNeeded: false)

        let viewModel = ServerSwitcherViewModel(servers: registry.servers, activeServerID: registry.activeServerID)
        let displayNames = viewModel.items.map(\.displayName)

        XCTAssertNotEqual(displayNames[0], displayNames[1])
        XCTAssertEqual(displayNames[0], "Farm (home.example.com)")
        XCTAssertEqual(displayNames[1], "Farm (shop.example.com)")
        XCTAssertEqual(displayNames[2], "Unique Farm")
        XCTAssertEqual(viewModel.items[0].accessibilityLabel, "Farm (home.example.com), active server")
        XCTAssertEqual(viewModel.items[1].accessibilityLabel, "Farm (shop.example.com), switch server")
    }

    func testVisibilityDegradesForZeroOrSingleServer() throws {
        let registry = ServerRegistry(userDefaults: userDefaults, migrateLegacyServerURL: false)
        var viewModel = ServerSwitcherViewModel(servers: registry.servers, activeServerID: registry.activeServerID)

        XCTAssertFalse(viewModel.isVisible)
        XCTAssertEqual(viewModel.activeServerName, "Select Server")

        _ = try registry.add(displayName: "Only Farm", baseURL: URL(string: "https://only.example.com")!)
        viewModel = ServerSwitcherViewModel(servers: registry.servers, activeServerID: registry.activeServerID)

        XCTAssertFalse(viewModel.isVisible)
        XCTAssertEqual(viewModel.activeServerName, "Only Farm")
    }

    func testActivateCallsServerRegistrySetActive() throws {
        let registry = ServerRegistry(userDefaults: userDefaults, migrateLegacyServerURL: false)
        let home = try registry.add(displayName: "Home Farm", baseURL: URL(string: "https://home.example.com")!)
        let shop = try registry.add(displayName: "Shop Farm", baseURL: URL(string: "https://shop.example.com")!, makeActiveIfNeeded: false)
        let viewModel = ServerSwitcherViewModel(servers: registry.servers, activeServerID: home.id)

        try viewModel.activate(shop.id, registry: registry)

        XCTAssertEqual(registry.activeServerID, shop.id)
    }
}
