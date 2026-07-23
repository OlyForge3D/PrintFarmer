import XCTest
@testable import PrintFarmer

// MARK: - Dallas #816 revision — durable-authority hardening proofs
//
// Deterministic proofs for the Hicks + replacement-Vasquez findings this
// revision addresses on top of the frozen head:
//   A — fail-closed reads: only a TRUE file-not-found initializes empty; any
//       other read failure (a present-but-unreadable path) fails closed as a
//       typed `.persistenceFailure` and NEVER silently resets to zero/empty.
//   B — semantic validation: reserved < adopted, a non-UUID tombstone, or a
//       duplicate tombstone in the durable record OR the UserDefaults authority
//       store is rejected as corrupt/persistence failure — never `compactMap`ped
//       away. Canonical physical identity coalesces symlink/case aliases; the
//       coordinator registry is keyed on the durable root; a live path-lock
//       lease cannot be force-released.
//   C — recoverable writes: on a write-verify failure the exact prior bytes are
//       restored (no `try?`); when the restore ALSO fails a typed COMPOSITE
//       `.restorationFailure(primary:recovery:)` retains both contexts.
//   F — the after-write test seam is per-instance and synchronized (no shipping
//       global): a hook on one record does not leak to another.
//
// All proofs are deterministic: no sleeps, yields, polls, or wall-clock waits.
final class DallasSnapshotAuthorityHardeningTests: XCTestCase {

    private func tempRoot() -> URL {
        FileManager.default.temporaryDirectory
            .appendingPathComponent("dallas-\(UUID().uuidString)", isDirectory: true)
    }

    private func recordURL(in root: URL) -> URL {
        root.appendingPathComponent(FarmSnapshotDurableAuthorityRecord.filename, isDirectory: false)
    }

    private func writeRecordJSON(_ json: String, in root: URL) throws {
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        try Data(json.utf8).write(to: recordURL(in: root), options: [.atomic])
    }

    // MARK: A — non-ENOENT read is fail-closed, not absence

    /// A: a record whose path exists but is NOT a readable regular file (here a
    /// directory occupies the record path, so `Data(contentsOf:)` fails with a
    /// non-ENOENT error) MUST surface `.persistenceFailure` — it must NOT be
    /// mistaken for absence and reset to zero.
    func testNonFileNotFoundReadFailsClosed() throws {
        let root = tempRoot()
        defer { try? FileManager.default.removeItem(at: root) }
        // Create a DIRECTORY where the record file is expected.
        try FileManager.default.createDirectory(at: recordURL(in: root), withIntermediateDirectories: true)

        let record = FarmSnapshotDurableAuthorityRecord(rootURL: root)
        XCTAssertThrowsError(try record.loadReservedHighWater()) {
            XCTAssertEqual($0 as? FarmSnapshotAuthorityError, .persistenceFailure,
                           "a present-but-unreadable record must fail closed, not read as absent")
        }
        XCTAssertThrowsError(try record.reserveNextToken()) {
            XCTAssertEqual($0 as? FarmSnapshotAuthorityError, .persistenceFailure)
        }
    }

    /// A: a genuinely absent file (true ENOENT) still initializes to empty — the
    /// fail-closed change must not break the legitimate first-run path.
    func testTrueAbsenceInitializesEmpty() throws {
        let root = tempRoot()
        defer { try? FileManager.default.removeItem(at: root) }
        let record = FarmSnapshotDurableAuthorityRecord(rootURL: root)
        XCTAssertEqual(try record.loadReservedHighWater(), 0)
        XCTAssertEqual(try record.loadAdoptedHighWater(), 0)
        XCTAssertTrue(try record.loadTombstones().isEmpty)
    }

    // MARK: B — semantic validation of the durable payload

    func testReservedBelowAdoptedIsCorrupt() throws {
        let root = tempRoot()
        defer { try? FileManager.default.removeItem(at: root) }
        try writeRecordJSON(#"{"reservedHighWater":1,"adoptedHighWater":9,"tombstones":[]}"#, in: root)
        let record = FarmSnapshotDurableAuthorityRecord(rootURL: root)
        XCTAssertThrowsError(try record.loadAdoptedHighWater()) {
            XCTAssertEqual($0 as? FarmSnapshotAuthorityError, .persistenceFailure)
        }
        // The corrupt bytes must NOT be overwritten by a subsequent mutation.
        let before = try Data(contentsOf: recordURL(in: root))
        XCTAssertThrowsError(try record.reserveNextToken())
        let after = try Data(contentsOf: recordURL(in: root))
        XCTAssertEqual(before, after, "a mutation on a corrupt record must leave the bytes byte-identical")
    }

    func testInvalidTombstoneUUIDIsCorrupt() throws {
        let root = tempRoot()
        defer { try? FileManager.default.removeItem(at: root) }
        try writeRecordJSON(#"{"reservedHighWater":2,"adoptedHighWater":1,"tombstones":["not-a-uuid"]}"#, in: root)
        let record = FarmSnapshotDurableAuthorityRecord(rootURL: root)
        XCTAssertThrowsError(try record.loadTombstones()) {
            XCTAssertEqual($0 as? FarmSnapshotAuthorityError, .persistenceFailure,
                           "a malformed tombstone must fail closed, never be compactMapped away")
        }
    }

    func testDuplicateTombstoneIsCorrupt() throws {
        let root = tempRoot()
        defer { try? FileManager.default.removeItem(at: root) }
        let dup = UUID().uuidString
        try writeRecordJSON(#"{"reservedHighWater":2,"adoptedHighWater":1,"tombstones":["\#(dup)","\#(dup)"]}"#, in: root)
        let record = FarmSnapshotDurableAuthorityRecord(rootURL: root)
        XCTAssertThrowsError(try record.loadTombstones()) {
            XCTAssertEqual($0 as? FarmSnapshotAuthorityError, .persistenceFailure)
        }
    }

    // MARK: B — UserDefaults authority store strict load

    func testUserDefaultsTombstoneStoreStrictLoadRejectsMalformed() {
        let suite = "dallas-strict-\(UUID().uuidString)"
        let defaults = UserDefaults(suiteName: suite)!
        defer { defaults.removePersistentDomain(forName: suite) }
        let store = FarmSnapshotTombstoneStore(userDefaults: defaults, domainIdentifier: suite)

        // Valid entries load fine.
        let valid = UUID()
        defaults.set([valid.uuidString], forKey: FarmSnapshotTombstoneStore.key)
        XCTAssertEqual(try store.loadStrict(), [valid])

        // A malformed entry fails closed instead of silently shrinking the set.
        defaults.set([valid.uuidString, "garbage"], forKey: FarmSnapshotTombstoneStore.key)
        XCTAssertThrowsError(try store.loadStrict()) {
            XCTAssertEqual($0 as? FarmSnapshotAuthorityError, .persistenceFailure)
        }
    }

    /// B: a corrupt UserDefaults tombstone store poisons the coordinator so every
    /// mutation fails closed — a purged server can never silently re-activate.
    func testCoordinatorPoisonsOnCorruptUserDefaultsTombstones() {
        let suite = "dallas-poison-\(UUID().uuidString)"
        let defaults = UserDefaults(suiteName: suite)!
        defer {
            defaults.removePersistentDomain(forName: suite)
            FarmSnapshotDomainCoordinator.releaseCoordinator(forDomain: suite)
        }
        defaults.set(["not-a-uuid"], forKey: FarmSnapshotTombstoneStore.key)
        let store = FarmSnapshotTombstoneStore(userDefaults: defaults, domainIdentifier: suite)
        let coordinator = FarmSnapshotDomainCoordinator.coordinator(for: store)
        let ns = FarmSnapshotNamespace(serverID: UUID(), userID: UUID())
        XCTAssertThrowsError(try coordinator.mint(namespace: ns, generation: 1)) {
            XCTAssertEqual($0 as? FarmSnapshotAuthorityError, .persistenceFailure)
        }
    }

    // MARK: B — canonical physical identity + registry keying + lease lifecycle

    func testCanonicalIdentityCoalescesCaseAliases() {
        let base = tempRoot()
        let lower = base.appendingPathComponent("Case", isDirectory: true)
            .appendingPathComponent(FarmSnapshotDurableAuthorityRecord.filename)
        let upper = base.appendingPathComponent("CASE", isDirectory: true)
            .appendingPathComponent(FarmSnapshotDurableAuthorityRecord.filename)
        // On the default case-insensitive volume these two alias the same file;
        // their canonical identity must be equal so they share ONE lock.
        XCTAssertEqual(
            FarmSnapshotDurableAuthorityRecord.canonicalIdentity(for: lower),
            FarmSnapshotDurableAuthorityRecord.canonicalIdentity(for: upper)
        )
    }

    func testCoordinatorRegistryKeyedOnDurableRoot() {
        let suite = "dallas-root-\(UUID().uuidString)"
        let defaults = UserDefaults(suiteName: suite)!
        defer {
            defaults.removePersistentDomain(forName: suite)
            FarmSnapshotDomainCoordinator.releaseCoordinator(forDomain: suite)
        }
        let rootA = tempRoot()
        let rootB = tempRoot()
        defer {
            try? FileManager.default.removeItem(at: rootA)
            try? FileManager.default.removeItem(at: rootB)
        }
        let store = FarmSnapshotTombstoneStore(userDefaults: defaults, domainIdentifier: suite)
        let recA = FarmSnapshotDurableAuthorityRecord(rootURL: rootA)
        let recB = FarmSnapshotDurableAuthorityRecord(rootURL: rootB)

        // Same domain, DIFFERENT durable roots → distinct registry keys and
        // therefore distinct coordinators (cannot share/discard one record).
        XCTAssertNotEqual(
            FarmSnapshotDomainCoordinator.registryKey(domain: suite, durableRecord: recA),
            FarmSnapshotDomainCoordinator.registryKey(domain: suite, durableRecord: recB)
        )
        let coordA = FarmSnapshotDomainCoordinator.coordinator(for: store, durableRecord: recA)
        let coordB = FarmSnapshotDomainCoordinator.coordinator(for: store, durableRecord: recB)
        XCTAssertFalse(coordA === coordB, "different durable roots must not share one coordinator")

        // Same domain + same root → same coordinator (shared in-memory coherence).
        let coordA2 = FarmSnapshotDomainCoordinator.coordinator(for: store, durableRecord: recA)
        XCTAssertTrue(coordA === coordA2)
    }

    func testLivePathLockLeaseCannotBeForceReleased() {
        let root = tempRoot()
        defer { try? FileManager.default.removeItem(at: root) }
        let url = recordURL(in: root)
        var record: FarmSnapshotDurableAuthorityRecord? = FarmSnapshotDurableAuthorityRecord(rootURL: root)
        _ = record // keep alive
        // A live record still holds the lease → force-release is refused.
        XCTAssertFalse(FarmSnapshotDurableAuthorityRecord.releasePathLock(forURL: url))
        // Drop the record; the weak lease evicts, so release now succeeds.
        record = nil
        XCTAssertTrue(FarmSnapshotDurableAuthorityRecord.releasePathLock(forURL: url))
    }

    // MARK: C — composite restoration failure retains both contexts

    /// C: force a write-verify failure (delete the file via the after-write hook)
    /// AND make the prior-byte restore fail (the record's directory is made
    /// read-only inside the same hook). The record must surface the typed
    /// COMPOSITE `.restorationFailure(primary:recovery:)`, retaining both the
    /// primary write failure and the recovery failure — never swallowing either.
    func testCompositeRestorationFailureRetainsBothContexts() throws {
        let root = tempRoot()
        let url = recordURL(in: root)
        addTeardownBlock {
            // Restore writability so cleanup can succeed.
            try? FileManager.default.setAttributes([.posixPermissions: 0o755], ofItemAtPath: root.path)
            try? FileManager.default.removeItem(at: root)
        }
        let record = FarmSnapshotDurableAuthorityRecord(rootURL: root)

        // Baseline present record with prior bytes to restore.
        XCTAssertEqual(try record.reserveNextToken(), 1)
        XCTAssertTrue(try record.tryAdopt(token: 1))

        record.setAfterAtomicWriteHookForTesting { hookURL in
            // 1) Lose the acknowledged write → verify re-read fails.
            try? FileManager.default.removeItem(at: hookURL)
            // 2) Make restoration of prior bytes impossible → read-only directory.
            try? FileManager.default.setAttributes(
                [.posixPermissions: 0o555], ofItemAtPath: hookURL.deletingLastPathComponent().path)
        }
        addTeardownBlock { record.setAfterAtomicWriteHookForTesting(nil) }

        XCTAssertThrowsError(try record.reserveNextToken()) { err in
            guard case let .restorationFailure(primary, recovery)? = err as? FarmSnapshotAuthorityError else {
                return XCTFail("expected composite .restorationFailure, got \(err)")
            }
            XCTAssertFalse(primary.isEmpty)
            XCTAssertFalse(recovery.isEmpty)
        }
        _ = url
    }

    // MARK: F — per-instance test seam, no global leakage

    /// F: installing an after-write hook on one record instance must NOT affect a
    /// different record instance (proving the seam is per-instance state, not a
    /// shipping global). Record A's hook deletes-on-write and fails; record B (no
    /// hook) writes normally.
    func testAfterWriteHookIsPerInstance() throws {
        let rootA = tempRoot()
        let rootB = tempRoot()
        defer {
            try? FileManager.default.removeItem(at: rootA)
            try? FileManager.default.removeItem(at: rootB)
        }
        let recordA = FarmSnapshotDurableAuthorityRecord(rootURL: rootA)
        let recordB = FarmSnapshotDurableAuthorityRecord(rootURL: rootB)
        addTeardownBlock { recordA.setAfterAtomicWriteHookForTesting(nil) }

        recordA.setAfterAtomicWriteHookForTesting { url in
            try? FileManager.default.removeItem(at: url)
        }

        // Record A: hooked → write-verify fails.
        XCTAssertThrowsError(try recordA.reserveNextToken()) {
            XCTAssertEqual($0 as? FarmSnapshotAuthorityError, .persistenceFailure)
        }
        // Record B: no hook installed → writes succeed normally, proving A's hook
        // did not leak through any shared/global state.
        XCTAssertEqual(try recordB.reserveNextToken(), 1)
        XCTAssertEqual(try recordB.loadReservedHighWater(), 1)
    }
}

// MARK: - Dallas #816 revision — finding D: operation-revision credential rollback
//
// Proves the credential rollback compares the per-publication OPERATION token
// (full-tuple CAS), not just the access-token text. The frozen head's
// token-text compare would clobber a T2 that republished the same bearer with a
// new expiry under a newer operation.
final class DallasAuthRollbackTests: XCTestCase {

    private let e1 = Date(timeIntervalSince1970: 100)
    private let e2 = Date(timeIntervalSince1970: 999)

    /// D: T1 publishes a bearer; T2 republishes the SAME bearer with a NEW expiry
    /// under a newer operation token. T1's rollback CAS on its own (older) token
    /// MUST fail — T2's full tuple (bearer + fresh expiry) is preserved. A
    /// token-text-only compare would have matched and destroyed T2's expiry.
    func testCredentialRollbackIsOperationCASNotTokenText() {
        let store = ServerCredentialsStore()
        let sid = UUID()
        addTeardownBlock { store.clear(serverId: sid) }

        let priorT1 = store.saveCapturingPriorState(
            ServerCredentials(accessToken: "bearer", expiresAt: e1), serverId: sid, operationToken: 1)
        XCTAssertNil(priorT1.credentials, "no prior credentials existed")

        let priorT2 = store.saveCapturingPriorState(
            ServerCredentials(accessToken: "bearer", expiresAt: e2), serverId: sid, operationToken: 2)
        XCTAssertEqual(priorT2.credentials?.accessToken, "bearer")

        // T1 rollback: current op tag is 2 (T2), not 1 → CAS fails → no-op.
        XCTAssertFalse(store.restoreIfOperationMatches(serverId: sid, expectedOperationToken: 1, prior: priorT1))
        let afterT1Rollback = store.load(serverId: sid)
        XCTAssertEqual(afterT1Rollback?.accessToken, "bearer")
        XCTAssertEqual(afterT1Rollback?.expiresAt, e2,
                       "T2's fresh expiry MUST survive T1's rollback (token-text compare would have clobbered it)")
        XCTAssertEqual(store.credentialOperationToken(serverId: sid), 2)
    }

    /// D: the operation that actually owns the destination CAN roll back — its
    /// CAS matches and it restores the exact prior tuple.
    func testCredentialRollbackByOwnerRestoresPriorTuple() {
        let store = ServerCredentialsStore()
        let sid = UUID()
        addTeardownBlock { store.clear(serverId: sid) }

        _ = store.saveCapturingPriorState(
            ServerCredentials(accessToken: "bearer-1", expiresAt: e1), serverId: sid, operationToken: 1)
        let priorT2 = store.saveCapturingPriorState(
            ServerCredentials(accessToken: "bearer-2", expiresAt: e2), serverId: sid, operationToken: 2)

        // T2 owns the destination (op tag 2) → rollback succeeds, restoring T1.
        XCTAssertTrue(store.restoreIfOperationMatches(serverId: sid, expectedOperationToken: 2, prior: priorT2))
        let restored = store.load(serverId: sid)
        XCTAssertEqual(restored?.accessToken, "bearer-1")
        XCTAssertEqual(restored?.expiresAt, e1)
        XCTAssertEqual(store.credentialOperationToken(serverId: sid), 1, "prior op tag restored")
    }

    /// D: clearing credentials removes the operation tag too, so a later
    /// publication starts from an untagged destination.
    func testClearRemovesOperationTag() {
        let store = ServerCredentialsStore()
        let sid = UUID()
        addTeardownBlock { store.clear(serverId: sid) }
        _ = store.saveCapturingPriorState(
            ServerCredentials(accessToken: "bearer", expiresAt: e1), serverId: sid, operationToken: 7)
        XCTAssertEqual(store.credentialOperationToken(serverId: sid), 7)
        store.clear(serverId: sid)
        XCTAssertNil(store.credentialOperationToken(serverId: sid))
        XCTAssertNil(store.load(serverId: sid))
    }
}
