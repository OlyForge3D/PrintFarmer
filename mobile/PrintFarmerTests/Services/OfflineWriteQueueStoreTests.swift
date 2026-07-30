import XCTest
@testable import PrintFarmer

final class OfflineWriteQueueStoreTests: XCTestCase {
    private var directory: URL!

    override func setUpWithError() throws {
        directory = FileManager.default.temporaryDirectory
            .appendingPathComponent("offline-queue-tests-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
    }

    override func tearDownWithError() throws {
        if let directory { try? FileManager.default.removeItem(at: directory) }
    }

    private var fileURL: URL { directory.appendingPathComponent(FileOfflineWriteQueueStore.fileName) }

    private func makeItem(
        server: UUID = UUID(),
        user: UUID = UUID(),
        key: String = "k1",
        sku: String = "SKU-A",
        createdAt: Date = Date(timeIntervalSince1970: 1_700_000_000),
        status: OfflineWriteItemStatus = .pending
    ) -> OfflineWriteItem {
        OfflineWriteItem(
            serverID: server,
            userID: user,
            createdAt: createdAt,
            idempotencyKey: key,
            operation: .partAdjustment(
                sku: sku,
                request: AdjustPartInventoryRequest(delta: -1, reason: .qcReject, jobId: nil, binCode: nil, notes: nil, operationKey: key)
            ),
            status: status
        )
    }

    private func encodedItemDictionary(_ item: OfflineWriteItem) throws -> [String: Any] {
        let encoder = JSONEncoder()
        encoder.dateEncodingStrategy = .iso8601
        let data = try encoder.encode(item)
        return try JSONSerialization.jsonObject(with: data) as! [String: Any]
    }

    private func writeEnvelope(version: Int, itemDicts: [[String: Any]]) throws {
        let envelope: [String: Any] = ["version": version, "items": itemDicts]
        let data = try JSONSerialization.data(withJSONObject: envelope, options: [])
        try data.write(to: fileURL, options: .atomic)
    }

    // MARK: Round trip / relaunch

    func testSaveThenLoadRoundTripsAcrossInstances() async throws {
        let server = UUID(); let user = UUID()
        let items = [
            makeItem(server: server, user: user, key: "a1", sku: "SKU-A"),
            makeItem(server: server, user: user, key: "b1", sku: "SKU-B", status: .expiredNeedsReview)
        ]
        let writer = FileOfflineWriteQueueStore(directory: directory)
        await writer.saveAll(items)

        // A fresh store instance (simulated relaunch) reads the same bytes.
        let reader = FileOfflineWriteQueueStore(directory: directory)
        let loaded = await reader.loadAll()
        XCTAssertEqual(Set(loaded.map { $0.idempotencyKey }), ["a1", "b1"])
        XCTAssertEqual(loaded.first { $0.idempotencyKey == "b1" }?.status, .expiredNeedsReview)
    }

    func testMissingFileLoadsEmpty() async {
        let store = FileOfflineWriteQueueStore(directory: directory)
        let loaded = await store.loadAll()
        XCTAssertTrue(loaded.isEmpty)
    }

    // MARK: Corruption / version recovery

    func testWhollyCorruptFileIsQuarantinedAndLoadsEmpty() async throws {
        try Data("this is not json".utf8).write(to: fileURL, options: .atomic)
        let store = FileOfflineWriteQueueStore(directory: directory)
        let loaded = await store.loadAll()
        XCTAssertTrue(loaded.isEmpty, "an unreadable file must not crash and must load empty")

        let quarantined = try FileManager.default.contentsOfDirectory(atPath: directory.path)
            .filter { $0.contains("corrupt") }
        XCTAssertFalse(quarantined.isEmpty, "the unreadable blob is preserved for recovery")
    }

    func testUnknownFutureVersionIsQuarantined() async throws {
        try writeEnvelope(version: 999, itemDicts: [])
        let store = FileOfflineWriteQueueStore(directory: directory)
        let loaded = await store.loadAll()
        XCTAssertTrue(loaded.isEmpty, "an unknown future schema must not be misinterpreted")

        let quarantined = try FileManager.default.contentsOfDirectory(atPath: directory.path)
            .filter { $0.contains("corrupt") && $0.contains("999") }
        XCTAssertFalse(quarantined.isEmpty)
    }

    func testUnknownKindItemIsSkippedButValidItemsSurvive() async throws {
        let server = UUID(); let user = UUID()
        let valid = makeItem(server: server, user: user, key: "valid", sku: "SKU-A")
        var validDict = try encodedItemDictionary(valid)

        // Fabricate a record whose operation.kind is a LIVE-command discriminator
        // outside the allowlist — it must be dropped, never decoded into
        // something replayable.
        var unknownDict = validDict
        unknownDict["idempotencyKey"] = "unknown"
        var operation = unknownDict["operation"] as! [String: Any]
        operation["kind"] = "pausePrint"
        unknownDict["operation"] = operation

        // Keep validDict's key stable.
        validDict["idempotencyKey"] = "valid"

        try writeEnvelope(version: 1, itemDicts: [validDict, unknownDict])

        let store = FileOfflineWriteQueueStore(directory: directory)
        let loaded = await store.loadAll()
        XCTAssertEqual(loaded.map { $0.idempotencyKey }, ["valid"], "one bad record must not strand the others, and cannot decode into a replayable op")
    }

    // MARK: Namespace bytes retained for other identities

    func testBothNamespacesArePersistedForCoordinatorFiltering() async {
        let serverA = UUID(); let userA = UUID()
        let serverA_userB = UUID()
        let items = [
            makeItem(server: serverA, user: userA, key: "a"),
            makeItem(server: serverA, user: serverA_userB, key: "b")
        ]
        let store = FileOfflineWriteQueueStore(directory: directory)
        await store.saveAll(items)
        let loaded = await store.loadAll()
        XCTAssertEqual(loaded.count, 2, "another identity's bytes are retained untouched, never dropped")
    }

    // MARK: In-memory store parity

    func testInMemoryStoreSeedsAndCountsSaves() async {
        let seed = [makeItem(key: "seed")]
        let store = InMemoryOfflineWriteQueueStore(seed: seed)
        let loaded = await store.loadAll()
        XCTAssertEqual(loaded.map { $0.idempotencyKey }, ["seed"])
        await store.saveAll([])
        XCTAssertEqual(store.saveCount, 1)
        XCTAssertTrue(store.persistedItems.isEmpty)
    }
}
