import Foundation

// MARK: - Farm Snapshot Store & Lifecycle Authority (F10-C1a, #816)
//
// Central invariant: the *live* record for a namespace is only ever mutated by a
// single synchronous, authority-validated promotion executed while holding the
// authority lock. Candidate bytes are written to a temp file first — they are
// never the addressable live path — so a rejected candidate can never become the
// live record even if every subsequent cleanup also fails, and no replace-then-
// rollback window exists. A synchronous `revoke()`/`tombstone()` from the
// container therefore has program-order happens-before with the promotion: it
// either lands fully before (authority no longer current → nothing promoted,
// prior bytes intact) or fully after (promotion already durable).

// MARK: File I/O seam

/// Filesystem primitives behind the store. The async methods are the suspendable
/// (test-barrier-injectable) operations the actor awaits. The synchronous
/// methods are invoked ONLY inside the authority critical section so promotion
/// and compare-and-move cannot interleave with a revoke or another commit.
protocol FarmSnapshotFileIO: Sendable {
    func readData(at url: URL) async throws -> Data?
    func writeCandidate(_ data: Data, to url: URL) async throws
    func removeItem(at url: URL) async throws
    func createDirectory(at url: URL) async throws

    /// Synchronous, non-suspending. Re-read the live record for the restart-safe
    /// monotonic + integrity check performed at the durable boundary.
    func readDataSync(at url: URL) throws -> Data?
    /// Synchronous, non-suspending, atomic install of `candidate` as `live`
    /// (handles both the absent and present cases). Leaves either the old or the
    /// new fully-valid file, never a torn record.
    func promoteAtomically(candidate: URL, to live: URL) throws
    /// Synchronous, non-suspending compare-and-move. Moves `from`→`to` only if the
    /// current bytes at `from` still equal `expected`; returns `false` (no move)
    /// when the bytes have changed so a newer valid commit is never destroyed.
    func moveIfContentEquals(from: URL, to: URL, expected: Data) throws -> Bool
}

/// Real filesystem implementation. A missing file reads as `nil` (absence) and a
/// missing target removes as success; genuine errors are thrown.
struct DiskFarmSnapshotFileIO: FarmSnapshotFileIO {
    private var fileManager: FileManager { .default }

    init() {}

    func readData(at url: URL) async throws -> Data? {
        try readDataSync(at: url)
    }

    func readDataSync(at url: URL) throws -> Data? {
        guard fileManager.fileExists(atPath: url.path) else { return nil }
        return try Data(contentsOf: url)
    }

    func writeCandidate(_ data: Data, to url: URL) async throws {
        try createDirectorySync(at: url.deletingLastPathComponent())
        try data.write(to: url, options: .atomic)
    }

    func removeItem(at url: URL) async throws {
        guard fileManager.fileExists(atPath: url.path) else { return }
        try fileManager.removeItem(at: url)
    }

    func createDirectory(at url: URL) async throws {
        try createDirectorySync(at: url)
    }

    func promoteAtomically(candidate: URL, to live: URL) throws {
        try createDirectorySync(at: live.deletingLastPathComponent())
        if fileManager.fileExists(atPath: live.path) {
            _ = try fileManager.replaceItemAt(live, withItemAt: candidate)
        } else {
            try fileManager.moveItem(at: candidate, to: live)
        }
    }

    func moveIfContentEquals(from: URL, to: URL, expected: Data) throws -> Bool {
        guard fileManager.fileExists(atPath: from.path) else { return false }
        let current = try Data(contentsOf: from)
        guard current == expected else { return false }
        try createDirectorySync(at: to.deletingLastPathComponent())
        try fileManager.moveItem(at: from, to: to)
        return true
    }

    private func createDirectorySync(at url: URL) throws {
        try fileManager.createDirectory(at: url, withIntermediateDirectories: true)
    }
}

// MARK: Shared authority

/// Synchronous, lock-guarded holder for the current session, the strictly
/// monotonic activation token, and the durable server tombstone set. Shared by
/// `ServiceContainer` (which mints/revokes/tombstones synchronously) and the
/// actor store (which validates at each durable boundary).
final class FarmSnapshotAuthority: @unchecked Sendable {
    /// Outcome of a promotion attempt evaluated inside the critical section.
    enum PromotionOutcome: Sendable, Equatable {
        case promoted
        case notNewer
        case integrityFailure
    }

    private let lock = NSLock()
    private var tokenCounter: UInt64 = 0
    private var current: FarmSnapshotSession?
    private var tombstones: Set<UUID>
    private let tombstoneStore: FarmSnapshotTombstoneStore

    init(tombstoneStore: FarmSnapshotTombstoneStore = FarmSnapshotTombstoneStore()) {
        self.tombstoneStore = tombstoneStore
        // Durable tombstones survive process restart / the crash window between
        // purge and registry removal (H4).
        self.tombstones = tombstoneStore.load()
    }

    /// Mint a fresh authoritative session for a settled server + verified owner.
    /// Returns `nil` when the server is tombstoned (purged) so nothing can
    /// resurrect it. Every mint advances the monotonic token, so a same-user /
    /// same-server relogin supersedes any in-flight pre-logout session.
    func mint(namespace: FarmSnapshotNamespace, generation: Int) -> FarmSnapshotSession? {
        lock.lock()
        defer { lock.unlock() }
        guard !tombstones.contains(namespace.serverID) else { return nil }
        tokenCounter += 1
        let session = FarmSnapshotSession(namespace: namespace, generation: generation, token: tokenCounter)
        current = session
        return session
    }

    /// Adopt an externally-minted session as current — monotonic compare-and-set:
    /// a delayed OLDER session (token ≤ the current token) can never replace a
    /// newer one (H3). Returns whether the session became current.
    @discardableResult
    func adopt(_ session: FarmSnapshotSession) -> Bool {
        lock.lock()
        defer { lock.unlock() }
        guard !tombstones.contains(session.serverID) else { return false }
        if let current, session.token <= current.token { return false }
        current = session
        return true
    }

    /// Unconditionally clear the current session (explicit logout / no-server).
    func revoke() {
        lock.lock()
        defer { lock.unlock() }
        current = nil
    }

    /// Conditionally clear the current session ONLY if `session` is still exactly
    /// current — a stale deactivate cannot clear a newer login (H3). Returns
    /// whether it cleared.
    @discardableResult
    func deactivate(_ session: FarmSnapshotSession) -> Bool {
        lock.lock()
        defer { lock.unlock() }
        guard current == session else { return false }
        current = nil
        return true
    }

    /// Tombstone a server durably and revoke it if it is the current session (H4).
    func tombstone(_ serverID: UUID) {
        lock.lock()
        defer { lock.unlock() }
        tombstones.insert(serverID)
        tombstoneStore.insert(serverID)
        if current?.serverID == serverID {
            current = nil
        }
    }

    /// Clear a server's tombstone once its ID lifecycle is complete (registry
    /// removal done). Server UUIDs are never reused, so this is housekeeping only.
    func clearTombstone(_ serverID: UUID) {
        lock.lock()
        defer { lock.unlock() }
        tombstones.remove(serverID)
        tombstoneStore.remove(serverID)
    }

    func isTombstoned(_ serverID: UUID) -> Bool {
        lock.lock()
        defer { lock.unlock() }
        return tombstones.contains(serverID)
    }

    func currentSession() -> FarmSnapshotSession? {
        lock.lock()
        defer { lock.unlock() }
        return current
    }

    func isCurrent(_ session: FarmSnapshotSession) -> Bool {
        lock.lock()
        defer { lock.unlock() }
        return current == session && !tombstones.contains(session.serverID)
    }

    /// Run `body` (a synchronous durable step — promotion or quarantine move) IF
    /// `session` is still exactly current, not tombstoned, and not cancelled — all
    /// while holding the lock so a concurrent revoke/tombstone/switch cannot
    /// interleave at the destructive boundary. Returns `nil` when the session is
    /// no longer authoritative (body not run). Used by both commit promotion and
    /// quarantine move (H5).
    func withPromotion<T>(
        _ session: FarmSnapshotSession,
        cancelled: () -> Bool,
        _ body: () throws -> T
    ) rethrows -> T? {
        lock.lock()
        defer { lock.unlock() }
        guard current == session, !tombstones.contains(session.serverID), !cancelled() else {
            return nil
        }
        return try body()
    }
}

// MARK: Store

actor FarmSnapshotStore: FarmSnapshotStoring {
    private let authority: FarmSnapshotAuthority
    private let fileIO: FarmSnapshotFileIO
    private let rootURL: URL
    private let ownerStore: FarmSnapshotOwnerStore?

    init(
        authority: FarmSnapshotAuthority,
        fileIO: FarmSnapshotFileIO = DiskFarmSnapshotFileIO(),
        rootURL: URL = FarmSnapshotStore.defaultRootURL(),
        ownerStore: FarmSnapshotOwnerStore? = nil
    ) {
        self.authority = authority
        self.fileIO = fileIO
        self.rootURL = rootURL
        self.ownerStore = ownerStore
    }

    static func defaultRootURL() -> URL {
        let base = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask).first
            ?? FileManager.default.temporaryDirectory
        return base.appendingPathComponent("FarmSnapshots", isDirectory: true)
    }

    // MARK: Paths (namespaced strictly by UUID)

    private func serverDir(_ serverID: UUID) -> URL {
        rootURL.appendingPathComponent("servers", isDirectory: true)
            .appendingPathComponent(serverID.uuidString, isDirectory: true)
    }

    private func quarantineDir(_ serverID: UUID) -> URL {
        rootURL.appendingPathComponent("quarantine", isDirectory: true)
            .appendingPathComponent(serverID.uuidString, isDirectory: true)
    }

    private func liveURL(_ namespace: FarmSnapshotNamespace) -> URL {
        serverDir(namespace.serverID).appendingPathComponent("\(namespace.userID.uuidString).json")
    }

    private func candidateURL(_ namespace: FarmSnapshotNamespace) -> URL {
        serverDir(namespace.serverID)
            .appendingPathComponent(".\(namespace.userID.uuidString).\(UUID().uuidString).tmp")
    }

    private func quarantineURL(_ namespace: FarmSnapshotNamespace) -> URL {
        quarantineDir(namespace.serverID)
            .appendingPathComponent("\(namespace.userID.uuidString).\(UUID().uuidString).json")
    }

    // MARK: Lifecycle

    func activate(session: FarmSnapshotSession) async {
        authority.adopt(session)
    }

    func deactivate() async {
        authority.revoke()
    }

    func currentSession() async -> FarmSnapshotSession? {
        authority.currentSession()
    }

    // MARK: Hydrate

    func hydrateActive() async -> FarmSnapshotHydration {
        guard let session = authority.currentSession() else { return .inactive }
        let live = liveURL(session.namespace)

        let data: Data?
        do {
            data = try await fileIO.readData(at: live)
        } catch {
            // Revalidate after the suspension; a revoke/switch that landed during
            // the read wins over reporting an I/O error.
            guard authority.isCurrent(session) else { return .inactive }
            return .unreadable
        }

        guard authority.isCurrent(session) else { return .inactive }

        guard let data else { return .absent }

        if let decoded = try? FarmSnapshotEnvelope.makeDecoder().decode(FarmSnapshotEnvelope.self, from: data),
           decoded.isSupportedSchema,
           decoded.namespace == session.namespace {
            return .snapshot(decoded)
        }

        // Corrupt / unknown-schema / wrong-namespace record → recover via
        // authority-revalidated, compare-and-move quarantine.
        switch await recover(live: live, expected: data, session: session) {
        case .recovered:
            return .recovered
        case .changed, .revoked:
            // Compare-false (a newer valid commit landed) or authority loss — the
            // surviving live file is left intact and recovery is not claimed.
            return .inactive
        case .failed:
            return .unreadable
        }
    }

    private enum RecoverResult: Sendable { case recovered, changed, revoked, failed }

    private func recover(live: URL, expected: Data, session: FarmSnapshotSession) async -> RecoverResult {
        let dest = quarantineURL(session.namespace)
        do {
            try await fileIO.createDirectory(at: quarantineDir(session.serverID))
        } catch {
            return .failed
        }
        // H5: the destructive compare-and-move runs INSIDE the authority lock at
        // the move boundary, so a revoke/switch/tombstone landing between the
        // authority check and the move cannot mutate disk or be reported as
        // recovered for a stale session.
        do {
            let moved: Bool? = try authority.withPromotion(session, cancelled: { Task.isCancelled }) {
                try self.fileIO.moveIfContentEquals(from: live, to: dest, expected: expected)
            }
            switch moved {
            case .none:
                return .revoked
            case .some(true):
                return .recovered
            case .some(false):
                return .changed
            }
        } catch {
            return .failed
        }
    }

    // MARK: Commit

    func commit(_ envelope: FarmSnapshotEnvelope, capturedSession: FarmSnapshotSession) async -> FarmSnapshotCommitResult {
        // 1. Reject unsupported incoming schema before any durable mutation.
        guard envelope.isSupportedSchema else { return .schemaUnsupported }
        // 2. The candidate must belong to the captured session's namespace.
        guard envelope.namespace == capturedSession.namespace else { return .namespaceMismatch }
        // 3. Cheap short-circuit; the authoritative check is inside the promotion.
        guard authority.isCurrent(capturedSession) else { return .superseded }

        let live = liveURL(capturedSession.namespace)

        // 4. Early integrity + monotonic read (fail-closed on unreadable/corrupt existing).
        let existing: FarmSnapshotEnvelope?
        do {
            if let data = try await fileIO.readData(at: live) {
                guard let decoded = try? FarmSnapshotEnvelope.makeDecoder().decode(FarmSnapshotEnvelope.self, from: data),
                      decoded.isSupportedSchema,
                      decoded.namespace == capturedSession.namespace else {
                    return .integrityFailure
                }
                existing = decoded
            } else {
                existing = nil
            }
        } catch {
            return .integrityFailure
        }
        if let existing, existing.lastUpdatedAtMillis >= envelope.lastUpdatedAtMillis {
            return .notNewer
        }

        guard authority.isCurrent(capturedSession) else { return .superseded }

        // 5. Encode + write candidate (suspendable / barrier point). The candidate
        //    is never the live path.
        guard let data = try? FarmSnapshotEnvelope.makeEncoder().encode(envelope) else {
            return .persistenceFailure(cleanupFailed: false)
        }
        // H4: refuse to write into a purged namespace. Combined with the durable
        // tombstone and the post-promotion sweep below, a commit that was
        // suspended before its candidate write can never recreate a purged
        // namespace after `.purged`.
        guard !authority.isTombstoned(capturedSession.serverID) else { return .superseded }
        let candidate = candidateURL(capturedSession.namespace)
        do {
            try await fileIO.writeCandidate(data, to: candidate)
        } catch {
            let cleanupFailed = await cleanup(candidate)
            return .persistenceFailure(cleanupFailed: cleanupFailed)
        }

        // 6. Synchronous, authority-validated durable promotion. Re-reads the live
        //    record from disk for restart-safe monotonic + integrity, then does a
        //    single atomic install — all while holding the authority lock.
        let outcome: FarmSnapshotAuthority.PromotionOutcome?
        do {
            outcome = try authority.withPromotion(capturedSession, cancelled: { Task.isCancelled }) {
                let liveData = try self.fileIO.readDataSync(at: live)
                if let liveData {
                    guard let decoded = try? FarmSnapshotEnvelope.makeDecoder().decode(FarmSnapshotEnvelope.self, from: liveData),
                          decoded.isSupportedSchema,
                          decoded.namespace == capturedSession.namespace else {
                        return .integrityFailure
                    }
                    if decoded.lastUpdatedAtMillis >= envelope.lastUpdatedAtMillis {
                        return .notNewer
                    }
                }
                try self.fileIO.promoteAtomically(candidate: candidate, to: live)
                return .promoted
            }
        } catch {
            // Atomic promotion threw — the live record still holds the exact prior
            // accepted bytes. Surface primary + cleanup failure without masking.
            let cleanupFailed = await cleanup(candidate)
            await sweepIfTombstoned(capturedSession.serverID)
            return .persistenceFailure(cleanupFailed: cleanupFailed)
        }

        switch outcome {
        case .promoted:
            return .committed
        case .notNewer:
            _ = await cleanup(candidate)
            return .notNewer
        case .integrityFailure:
            _ = await cleanup(candidate)
            return .integrityFailure
        case nil:
            // Authority lost during the candidate write (revoke / tombstone /
            // generation advance / cancellation). Prior bytes intact.
            let cleanupFailed = await cleanup(candidate)
            // H4: if the namespace was purged while this write was in flight, sweep
            // any directory/bytes this late write may have recreated so the purge
            // stays durable (no resurrection).
            await sweepIfTombstoned(capturedSession.serverID)
            return cleanupFailed ? .persistenceFailure(cleanupFailed: true) : .superseded
        }
    }

    /// Remove a server's on-disk subtree if it has been tombstoned — used to undo
    /// any directory/bytes a late in-flight commit may have recreated after purge.
    private func sweepIfTombstoned(_ serverID: UUID) async {
        guard authority.isTombstoned(serverID) else { return }
        try? await fileIO.removeItem(at: serverDir(serverID))
        try? await fileIO.removeItem(at: quarantineDir(serverID))
    }

    /// Best-effort candidate cleanup. Returns `true` when cleanup itself failed so
    /// the caller can surface it; a missing candidate (already consumed by a
    /// successful promotion) is not a failure.
    private func cleanup(_ url: URL) async -> Bool {
        do {
            try await fileIO.removeItem(at: url)
            return false
        } catch {
            return true
        }
    }

    // MARK: Purge

    func purge(serverID: UUID) async -> FarmSnapshotPurgeResult {
        // H4: establish the durable tombstone BEFORE any wait and revoke a matching
        // active session, so no activation/commit can resurrect the namespace
        // mid-purge and the tombstone survives a restart/crash between here and
        // registry removal.
        authority.tombstone(serverID)
        // Clear the persisted owner mapping so a stale owner can never re-select
        // this server's cache after purge.
        ownerStore?.clearOwner(serverID: serverID)

        var failures = 0
        for dir in [serverDir(serverID), quarantineDir(serverID)] {
            do {
                try await fileIO.removeItem(at: dir)
            } catch {
                failures += 1
            }
        }
        return failures == 0 ? .purged : .failed(failureCount: failures)
    }
}
