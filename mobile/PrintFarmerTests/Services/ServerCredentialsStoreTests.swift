import KeychainSwift
import XCTest
@testable import PrintFarmer

final class ServerCredentialsStoreTests: XCTestCase {
    private var keychain: KeychainSwift!
    private var keyPrefix: String!
    private var store: ServerCredentialsStore!

    override func setUp() {
        super.setUp()
        keyPrefix = "ServerCredentialsStoreTests_\(UUID().uuidString)_"
        keychain = KeychainSwift(keyPrefix: keyPrefix)
        store = ServerCredentialsStore(keychain: keychain)
        keychain.clear()
    }

    override func tearDown() {
        keychain.clear()
        store = nil
        keychain = nil
        keyPrefix = nil
        super.tearDown()
    }

    func testSaveAndLoadRoundTripsCredentialsForServerId() throws {
        let serverId = UUID()
        let expiry = Date(timeIntervalSince1970: 1_800)

        store.save(ServerCredentials(accessToken: "token-a", expiresAt: expiry), serverId: serverId)

        let loaded = try XCTUnwrap(store.load(serverId: serverId))
        XCTAssertEqual(loaded.accessToken, "token-a")
        XCTAssertEqual(loaded.expiresAt?.timeIntervalSince1970, expiry.timeIntervalSince1970)
    }

    func testCredentialsAreIsolatedByServerId() throws {
        let firstServerId = UUID()
        let secondServerId = UUID()
        store.save(ServerCredentials(accessToken: "token-one", expiresAt: nil), serverId: firstServerId)
        store.save(ServerCredentials(accessToken: "token-two", expiresAt: nil), serverId: secondServerId)

        store.clear(serverId: firstServerId)

        XCTAssertNil(store.load(serverId: firstServerId))
        XCTAssertEqual(store.load(serverId: secondServerId)?.accessToken, "token-two")
    }

    func testDeleteClearsOnlyRequestedServer() {
        let firstServerId = UUID()
        let secondServerId = UUID()
        store.save(ServerCredentials(accessToken: "token-one", expiresAt: nil), serverId: firstServerId)
        store.save(ServerCredentials(accessToken: "token-two", expiresAt: nil), serverId: secondServerId)

        store.delete(serverId: firstServerId)

        XCTAssertNil(store.load(serverId: firstServerId))
        XCTAssertNotNil(store.load(serverId: secondServerId))
    }

    func testExpiryHandlingUsesFiveMinuteBuffer() {
        let serverId = UUID()
        let now = Date(timeIntervalSince1970: 1_000)

        store.save(
            ServerCredentials(accessToken: "soon", expiresAt: now.addingTimeInterval(60)),
            serverId: serverId
        )
        XCTAssertTrue(store.isExpired(serverId: serverId, now: now))

        store.save(
            ServerCredentials(accessToken: "later", expiresAt: now.addingTimeInterval(600)),
            serverId: serverId
        )
        XCTAssertFalse(store.isExpired(serverId: serverId, now: now))
    }

    func testMissingOrInvalidExpiryIsNotExpired() {
        let serverId = UUID()
        store.save(ServerCredentials(accessToken: "token", expiresAt: nil), serverId: serverId)

        XCTAssertFalse(store.isExpired(serverId: serverId, now: Date(timeIntervalSince1970: 1_000)))
    }

    func testLegacyTokenMigrationMovesTokenToActiveServerAndDeletesLegacyKeys() throws {
        let serverId = UUID()
        let expiry = Date(timeIntervalSince1970: 2_000)
        keychain.set("legacy-token", forKey: ServerCredentialsStore.legacyTokenKey)
        keychain.set(String(expiry.timeIntervalSince1970), forKey: ServerCredentialsStore.legacyTokenExpiryKey)

        XCTAssertTrue(store.migrateLegacyCredentialsIfNeeded(to: serverId))

        let migrated = try XCTUnwrap(store.load(serverId: serverId))
        XCTAssertEqual(migrated.accessToken, "legacy-token")
        XCTAssertEqual(migrated.expiresAt?.timeIntervalSince1970, expiry.timeIntervalSince1970)
        XCTAssertNil(keychain.get(ServerCredentialsStore.legacyTokenKey))
        XCTAssertNil(keychain.get(ServerCredentialsStore.legacyTokenExpiryKey))
    }

    func testLegacyMigrationIsIdempotentAndDoesNotOverwriteExistingServerToken() {
        let serverId = UUID()
        store.save(ServerCredentials(accessToken: "existing", expiresAt: nil), serverId: serverId)
        keychain.set("legacy-token", forKey: ServerCredentialsStore.legacyTokenKey)

        XCTAssertFalse(store.migrateLegacyCredentialsIfNeeded(to: serverId))

        XCTAssertEqual(store.load(serverId: serverId)?.accessToken, "existing")
        XCTAssertNil(keychain.get(ServerCredentialsStore.legacyTokenKey))
    }
}
