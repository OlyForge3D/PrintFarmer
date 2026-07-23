import Foundation
import XCTest
@testable import PrintFarmer

// MARK: - Deterministic test support for the farm snapshot store (F10-C1a, #816)
//
// All ordering here is mutation-bound: real `Task`s rendezvous on continuation
// barriers, never on sleeps/polling/retries/elapsed time.

/// A one-shot rendezvous barrier. The code under test calls `arriveAndWait()` at
/// a chosen suspension point; the test observes arrival with `waitUntilArrived()`,
/// performs an interleaving mutation, then `release()`s to let execution resume.
final class AsyncBarrier: @unchecked Sendable {
    private let lock = NSLock()
    private var arrived = false
    private var released = false
    private var arrivalWaiters: [CheckedContinuation<Void, Never>] = []
    private var releaseWaiter: CheckedContinuation<Void, Never>?

    func arriveAndWait() async {
        let waiters = markArrived()
        waiters.forEach { $0.resume() }
        await withCheckedContinuation { (continuation: CheckedContinuation<Void, Never>) in
            registerRelease(continuation)
        }
    }

    func waitUntilArrived() async {
        await withCheckedContinuation { (continuation: CheckedContinuation<Void, Never>) in
            registerArrival(continuation)
        }
    }

    func release() {
        let waiter = markReleased()
        waiter?.resume()
    }

    /// Fire-and-forget arrival signal (no wait for release). Lets a background thread
    /// notify the test it completed without blocking that thread.
    func signal() {
        let waiters = markArrived()
        waiters.forEach { $0.resume() }
    }

    /// Secondary rescue (issue #816 I): if the barrier is deallocated while a
    /// continuation is still parked (e.g. a test threw before releasing and nothing
    /// retains the barrier), resume it so the suspended task cannot strand. This is a
    /// backstop only — tests must still `defer { barrier.release() }` after creation,
    /// and `release()`/`markReleased` are idempotent so double-release is a no-op.
    deinit {
        lock.lock()
        let release = releaseWaiter
        releaseWaiter = nil
        let arrivals = arrivalWaiters
        arrivalWaiters = []
        lock.unlock()
        release?.resume()
        arrivals.forEach { $0.resume() }
    }

    // MARK: Synchronous lock helpers (kept out of async scope)

    private func markArrived() -> [CheckedContinuation<Void, Never>] {
        lock.lock()
        defer { lock.unlock() }
        arrived = true
        let waiters = arrivalWaiters
        arrivalWaiters = []
        return waiters
    }

    private func registerRelease(_ continuation: CheckedContinuation<Void, Never>) {
        lock.lock()
        if released {
            lock.unlock()
            continuation.resume()
        } else {
            releaseWaiter = continuation
            lock.unlock()
        }
    }

    private func registerArrival(_ continuation: CheckedContinuation<Void, Never>) {
        lock.lock()
        if arrived {
            lock.unlock()
            continuation.resume()
        } else {
            arrivalWaiters.append(continuation)
            lock.unlock()
        }
    }

    private func markReleased() -> CheckedContinuation<Void, Never>? {
        lock.lock()
        defer { lock.unlock() }
        released = true
        let waiter = releaseWaiter
        releaseWaiter = nil
        return waiter
    }
}

// MARK: - Async-friendly assertion wrappers
//
// XCTAssert*'s `@autoclosure` parameters cannot contain `await`. These plain
// wrappers evaluate their arguments in the caller's async context first.

func XAssertEqual<T: Equatable>(_ lhs: T, _ rhs: T, _ message: String = "", file: StaticString = #filePath, line: UInt = #line) {
    XCTAssertEqual(lhs, rhs, message, file: file, line: line)
}

func XAssertNotEqual<T: Equatable>(_ lhs: T, _ rhs: T, _ message: String = "", file: StaticString = #filePath, line: UInt = #line) {
    XCTAssertNotEqual(lhs, rhs, message, file: file, line: line)
}

func XAssertNil(_ value: Any?, _ message: String = "", file: StaticString = #filePath, line: UInt = #line) {
    XCTAssertNil(value, message, file: file, line: line)
}

func XAssertNotNil(_ value: Any?, _ message: String = "", file: StaticString = #filePath, line: UInt = #line) {
    XCTAssertNotNil(value, message, file: file, line: line)
}

func XAssertTrue(_ value: Bool, _ message: String = "", file: StaticString = #filePath, line: UInt = #line) {
    XCTAssertTrue(value, message, file: file, line: line)
}

func XAssertFalse(_ value: Bool, _ message: String = "", file: StaticString = #filePath, line: UInt = #line) {
    XCTAssertFalse(value, message, file: file, line: line)
}

/// Injectable file I/O that wraps a real on-disk backing (so real atomic
/// `FileManager` behavior is exercised) and adds per-operation fault injection,
/// barriers, and exact call counts.
final class ControlledFarmSnapshotFileIO: FarmSnapshotFileIO, @unchecked Sendable {
    struct IOFailure: Error {}

    private let backing: DiskFarmSnapshotFileIO
    private let lock = NSLock()

    // Fault toggles.
    var failWriteCandidate = false
    var failPromote = false
    var failMove = false
    var failRemove = false
    var failReadDataSync = false

    // Barriers (fire on first call of each op unless nil).
    var writeCandidateBarrier: AsyncBarrier?
    /// Fires AFTER the candidate is fully written+closed and returns, i.e. exactly
    /// between candidate durability and the atomic promote (issue #816 H7). This is
    /// the correct phase to prove the externally observable live path is old-or-new,
    /// never torn. The synchronous `promoteAtomically` primitive is left untouched so
    /// the atomic section is never made reentrant.
    var postWriteCandidateBarrier: AsyncBarrier?
    var readDataBarrier: AsyncBarrier?
    var createDirectoryBarrier: AsyncBarrier?
    /// Fires on the first `removeItem` (e.g. the startup tombstone sweep inside
    /// `store.activate`). Lets a test park an activation mid-await and advance the
    /// auth epoch to prove the final snapshot-publication CAS (issue #816 H2, Bishop).
    var removeItemBarrier: AsyncBarrier?

    // Exact counts.
    private(set) var readCount = 0
    private(set) var readSyncCount = 0
    private(set) var writeCount = 0
    private(set) var promoteCount = 0
    private(set) var removeCount = 0
    private(set) var moveCount = 0

    init(backing: DiskFarmSnapshotFileIO = DiskFarmSnapshotFileIO()) {
        self.backing = backing
    }

    private func bump(_ keyPath: ReferenceWritableKeyPath<ControlledFarmSnapshotFileIO, Int>) {
        lock.lock()
        self[keyPath: keyPath] += 1
        lock.unlock()
    }

    func readData(at url: URL) async throws -> Data? {
        bump(\.readCount)
        if let barrier = readDataBarrier {
            readDataBarrier = nil
            await barrier.arriveAndWait()
        }
        return try backing.readDataSync(at: url)
    }

    func writeCandidate(_ data: Data, to url: URL) async throws {
        bump(\.writeCount)
        if let barrier = writeCandidateBarrier {
            writeCandidateBarrier = nil
            await barrier.arriveAndWait()
        }
        if failWriteCandidate { throw IOFailure() }
        try await backing.writeCandidate(data, to: url)
        // Post-write seam: candidate bytes are now fully durable on disk but not yet
        // promoted. A test can park here and observe live==old, candidate==new (H7).
        if let barrier = postWriteCandidateBarrier {
            postWriteCandidateBarrier = nil
            await barrier.arriveAndWait()
        }
    }

    func removeItem(at url: URL) async throws {
        bump(\.removeCount)
        if let barrier = removeItemBarrier {
            removeItemBarrier = nil
            await barrier.arriveAndWait()
        }
        if failRemove { throw IOFailure() }
        try await backing.removeItem(at: url)
    }

    func createDirectory(at url: URL) async throws {
        if let barrier = createDirectoryBarrier {
            createDirectoryBarrier = nil
            await barrier.arriveAndWait()
        }
        try await backing.createDirectory(at: url)
    }

    func readDataSync(at url: URL) throws -> Data? {
        bump(\.readSyncCount)
        if failReadDataSync { throw IOFailure() }
        return try backing.readDataSync(at: url)
    }

    func promoteAtomically(candidate: URL, to live: URL) throws {
        bump(\.promoteCount)
        if failPromote { throw IOFailure() }
        try backing.promoteAtomically(candidate: candidate, to: live)
    }

    func moveIfContentEquals(from: URL, to: URL, expected: Data) throws -> Bool {
        bump(\.moveCount)
        if failMove { throw IOFailure() }
        return try backing.moveIfContentEquals(from: from, to: to, expected: expected)
    }
}

// MARK: - Factories

/// Central, test-owned UserDefaults suite hygiene (issue #816). The exact-domain
/// removal primitive is shared by `trackedSuiteName`'s teardown and is directly
/// callable so a focused proof can exercise the same cleanup without spawning an
/// untracked suite.
enum TrackedDefaults {
    /// Removes a suite's persistent domain and returns whether the domain is empty
    /// afterwards (nil/empty). This is the single cleanup primitive used everywhere.
    @discardableResult
    static func removeDomain(_ suite: String) -> Bool {
        UserDefaults().removePersistentDomain(forName: suite)
        let residual = UserDefaults().persistentDomain(forName: suite) ?? [:]
        return residual.isEmpty
    }
}

extension XCTestCase {
    /// Returns a uniquely-named, TEST-OWNED suite name and registers a teardown block
    /// that removes the persistent domain (on success or failure) via the shared
    /// `TrackedDefaults.removeDomain` primitive and asserts it no longer contains any
    /// keys. Prevents leaked persistent domains from accumulating in
    /// `~/Library/Preferences` across runs (issue #816 test hygiene). Callers build
    /// their own `UserDefaults(suiteName:)` from this name so fixtures never construct
    /// an untracked random suite. Returning only the `Sendable` String also keeps the
    /// non-Sendable `UserDefaults` within the caller's isolation domain.
    func trackedSuiteName(_ prefix: String, file: StaticString = #filePath, line: UInt = #line) -> String {
        let suite = "\(prefix)-\(UUID().uuidString)"
        addTeardownBlock {
            XCTAssertTrue(TrackedDefaults.removeDomain(suite),
                          "leaked persistent domain \(suite)", file: file, line: line)
        }
        return suite
    }
}

enum FarmSnapshotFixtures {
    static func tempRoot() -> URL {
        FileManager.default.temporaryDirectory
            .appendingPathComponent("FarmSnapshotTests-\(UUID().uuidString)", isDirectory: true)
    }

    /// Authority backed by a caller-provided, test-owned UserDefaults suite so
    /// durable tombstones never leak into `.standard` or across tests (H4). The
    /// suite MUST be tracked by the test (see `XCTestCase.trackedSuiteName`).
    static func makeAuthority(tombstoneDefaults: UserDefaults) -> FarmSnapshotAuthority {
        FarmSnapshotAuthority(tombstoneStore: FarmSnapshotTombstoneStore(userDefaults: tombstoneDefaults))
    }

    /// Isolated tombstone store on a caller-provided, test-owned suite (used by
    /// restart-durability fixtures that recreate the store on the SAME suite).
    static func makeTombstoneStore(_ defaults: UserDefaults) -> FarmSnapshotTombstoneStore {
        FarmSnapshotTombstoneStore(userDefaults: defaults)
    }

    static func makeOwnerStore(_ defaults: UserDefaults) -> FarmSnapshotOwnerStore {
        FarmSnapshotOwnerStore(userDefaults: defaults)
    }

    static func namespace(server: UUID = UUID(), user: UUID = UUID()) -> FarmSnapshotNamespace {
        FarmSnapshotNamespace(serverID: server, userID: user)
    }

    static func envelope(
        namespace: FarmSnapshotNamespace,
        millis: Int64,
        printers: [FarmSnapshotPrinter] = []
    ) -> FarmSnapshotEnvelope {
        FarmSnapshotEnvelope(namespace: namespace, payload: printers, lastUpdatedAtMillis: millis)
    }

    /// Sentinel secret markers embedded in a decoded `Printer` — every one of
    /// these must be structurally impossible to find in the encoded envelope.
    static let secretSentinels: [String] = [
        "SENTINEL_APIKEY",
        "SENTINEL_ORIGINURL",
        "SENTINEL_BACKENDURL",
        "SENTINEL_FRONTENDURL",
        "SENTINEL_CAMSTREAM",
        "SENTINEL_CAMSNAP",
        "SENTINEL_THUMBNAIL",
        "SENTINEL_NOTES"
    ]

    /// Keys that must never appear anywhere in the encoded JSON.
    static let forbiddenKeys: Set<String> = [
        "apiKey", "originalServerUrl", "backendUrl", "frontendUrl",
        "backendPort", "frontendPort", "backend",
        "cameraStreamUrl", "cameraSnapshotUrl", "cameraAccessMode",
        "cameraStreamFormat", "cameraSnapshotStrategy",
        "thumbnailUrl", "x", "y", "z", "homedAxes", "notes",
        "manufacturerId", "modelId", "motionType",
        "token", "password", "cookie", "header", "accessToken"
    ]

    /// A `Printer` decoded from JSON that carries every non-secret card field AND
    /// sentinel secrets, so projection can be proved to drop the secrets.
    static func printerWithSecrets(id: UUID = UUID(), locationID: UUID = UUID()) -> Printer {
        let json = """
        {
          "id": "\(id.uuidString)",
          "name": "Voron-01",
          "notes": "SENTINEL_NOTES",
          "manufacturerId": "\(UUID().uuidString)",
          "manufacturerName": "Voron Design",
          "modelId": "\(UUID().uuidString)",
          "modelName": "Trident",
          "motionType": "coreXY",
          "backend": "moonraker",
          "apiKey": "SENTINEL_APIKEY",
          "originalServerUrl": "https://SENTINEL_ORIGINURL.example.com",
          "backendPort": 7125,
          "frontendPort": 80,
          "inMaintenance": false,
          "isEnabled": true,
          "isOnline": true,
          "state": "printing",
          "progress": 42.0,
          "jobName": "bracket.gcode",
          "fileName": "bracket.gcode",
          "thumbnailUrl": "https://SENTINEL_THUMBNAIL.example.com/t.png",
          "cameraStreamUrl": "https://SENTINEL_CAMSTREAM.example.com/stream",
          "cameraSnapshotUrl": "https://SENTINEL_CAMSNAP.example.com/snap",
          "cameraAccessMode": "direct",
          "x": 10.0, "y": 20.0, "z": 30.0,
          "hotendTemp": 210.0, "bedTemp": 60.0,
          "hotendTarget": 215.0, "bedTarget": 60.0,
          "homedAxes": "xyz",
          "spoolInfo": {
            "hasActiveSpool": true,
            "activeSpoolId": 7,
            "spoolName": "PLA Black",
            "material": "PLA",
            "colorHex": "#000000",
            "filamentName": "Prusament PLA",
            "vendor": "Prusa",
            "remainingWeightG": 812.5,
            "spoolInUse": true
          },
          "backendUrl": "https://SENTINEL_BACKENDURL.example.com",
          "frontendUrl": "https://SENTINEL_FRONTENDURL.example.com",
          "location": { "id": "\(locationID.uuidString)", "name": "Rack A", "description": "Top shelf" },
          "obicoEnabled": true
        }
        """
        return try! JSONDecoder().decode(Printer.self, from: Data(json.utf8))
    }

    /// Recursively collect every object key in a decoded JSON value.
    static func collectKeys(_ value: Any, into keys: inout Set<String>) {
        if let dict = value as? [String: Any] {
            for (key, nested) in dict {
                keys.insert(key)
                collectKeys(nested, into: &keys)
            }
        } else if let array = value as? [Any] {
            for element in array {
                collectKeys(element, into: &keys)
            }
        }
    }
}
