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
///
/// H (issue #816): the ownership of `current`, `tombstones` (cache) AND the
/// `withPromotion` critical section has moved into a shared
/// `FarmSnapshotDomainCoordinator` resolved from a weak-ref registry keyed on
/// `tombstoneStore.domain`. Two Authorities constructed with the same domain
/// identifier share ONE coordinator — so a stale Authority cannot promote after
/// a peer Authority adopts/tombstones on the same domain. This class is now a
/// thin facade over the coordinator; its API is preserved for callers.
final class FarmSnapshotAuthority: @unchecked Sendable {
    /// Outcome of a promotion attempt evaluated inside the critical section.
    enum PromotionOutcome: Sendable, Equatable {
        case promoted
        case notNewer
        case integrityFailure
    }

    /// The shared per-domain coordinator that actually owns all mutable state.
    /// Held strongly so the weak-ref registry keeps this domain alive while any
    /// Authority references it.
    private let coordinator: FarmSnapshotDomainCoordinator

    init(
        tombstoneStore: FarmSnapshotTombstoneStore = FarmSnapshotTombstoneStore(),
        durableAuthorityRecord: FarmSnapshotDurableAuthorityRecord? = nil
    ) {
        self.coordinator = FarmSnapshotDomainCoordinator.coordinator(
            for: tombstoneStore,
            durableRecord: durableAuthorityRecord
        )
    }

    /// Reserve a token for a candidate session WITHOUT publishing it as current (P3).
    /// H: delegates through the shared coordinator so a same-domain peer's reservation
    /// is observed.
    func reserve(namespace: FarmSnapshotNamespace, generation: Int) throws -> FarmSnapshotSession? {
        try coordinator.reserve(namespace: namespace, generation: generation)
    }

    /// Mint a fresh authoritative session. H: delegates through the shared coordinator
    /// so a same-domain peer's mint / adopt / tombstone is observed.
    func mint(namespace: FarmSnapshotNamespace, generation: Int) throws -> FarmSnapshotSession? {
        try coordinator.mint(namespace: namespace, generation: generation)
    }

    /// H: durable CAS via the shared coordinator so a same-domain peer's adopt is
    /// observed. Returns whether `session` is authoritative after the call.
    @discardableResult
    func adopt(_ session: FarmSnapshotSession) throws -> Bool {
        try coordinator.adopt(session)
    }

    /// Unconditionally clear the current session on the shared coordinator.
    func revoke() {
        coordinator.revoke()
    }

    /// Conditionally clear the current session ONLY if `session` is still exactly
    /// current on the shared coordinator — a stale deactivate cannot clear a newer
    /// login even if issued from a different Authority instance in the same domain.
    @discardableResult
    func deactivate(_ session: FarmSnapshotSession) -> Bool {
        coordinator.deactivate(session)
    }

    /// Tombstone a server durably on the shared coordinator (and revoke it if it is
    /// the current session).
    ///
    /// H (issue #816 reject, Hicks): now throwing. The durable file record write
    /// is verified; a failure leaves NO in-memory or UserDefaults tombstone
    /// (fail-closed) and surfaces to the caller so `purge` can return `.failed`.
    func tombstone(_ serverID: UUID) throws {
        try coordinator.tombstone(serverID)
    }

    /// Clear a server's tombstone on the shared coordinator once its ID lifecycle
    /// is complete.
    ///
    /// H (issue #816 reject, Hicks): now throwing. Symmetric with `tombstone`.
    func clearTombstone(_ serverID: UUID) throws {
        try coordinator.clearTombstone(serverID)
    }

    func isTombstoned(_ serverID: UUID) -> Bool {
        coordinator.isTombstoned(serverID)
    }

    /// Snapshot of all durably-tombstoned server IDs (for startup residue sweep, H4).
    func tombstonedServerIDs() -> Set<UUID> {
        coordinator.tombstonedServerIDs()
    }

    func currentSession() -> FarmSnapshotSession? {
        coordinator.currentSession()
    }

    func isCurrent(_ session: FarmSnapshotSession) -> Bool {
        coordinator.isCurrent(session)
    }

    /// Run `body` (a synchronous durable step — promotion or quarantine move) IF
    /// `session` is still exactly current, not tombstoned, and not cancelled — all
    /// while holding the SHARED coordinator lock so a concurrent revoke/tombstone/
    /// switch (from ANY Authority on this domain) cannot interleave at the
    /// destructive boundary.
    func withPromotion<T>(
        _ session: FarmSnapshotSession,
        cancelled: () -> Bool,
        _ body: () throws -> T
    ) rethrows -> T? {
        try coordinator.withPromotion(session, cancelled: cancelled, body)
    }
}

// MARK: Store

actor FarmSnapshotStore: FarmSnapshotStoring {
    private let authority: FarmSnapshotAuthority
    private let fileIO: FarmSnapshotFileIO
    private let rootURL: URL
    private let ownerStore: FarmSnapshotOwnerClearing?

    init(
        authority: FarmSnapshotAuthority,
        fileIO: FarmSnapshotFileIO = DiskFarmSnapshotFileIO(),
        rootURL: URL = FarmSnapshotStore.defaultRootURL(),
        ownerStore: FarmSnapshotOwnerClearing? = nil
    ) {
        self.authority = authority
        self.fileIO = fileIO
        self.rootURL = rootURL
        self.ownerStore = ownerStore
    }

    // MARK: Per-server filesystem operation leases (H4)
    //
    // Every write/quarantine operation holds a lease for its server. Purge
    // tombstones durably, refuses new leases, then drains existing leases before
    // its final sweep — so no in-flight operation can recreate a purged namespace.

    private var leaseCounts: [UUID: Int] = [:]
    private var purging: Set<UUID> = []
    private var drainWaiters: [UUID: [CheckedContinuation<Void, Never>]] = [:]
    /// Startup residue-sweep state machine (H4). `startupComplete` is set ONLY after a
    /// fully-successful sweep; a failed sweep leaves it false so the next public op
    /// retries. `startupTask` memoizes an in-flight sweep so concurrent callers await
    /// the same run rather than sweeping twice.
    private var startupComplete = false
    private var startupTask: Task<Bool, Never>?

    /// Acquire a lease for `serverID`. Refused (nil result → caller aborts) when the
    /// server is purging or tombstoned, so no new operation starts against a purged
    /// namespace.
    private func acquireLease(_ serverID: UUID) -> Bool {
        guard !purging.contains(serverID), !authority.isTombstoned(serverID) else { return false }
        leaseCounts[serverID, default: 0] += 1
        return true
    }

    private func releaseLease(_ serverID: UUID) {
        let remaining = (leaseCounts[serverID] ?? 1) - 1
        if remaining <= 0 {
            leaseCounts[serverID] = nil
            if let waiters = drainWaiters[serverID] {
                drainWaiters[serverID] = nil
                waiters.forEach { $0.resume() }
            }
        } else {
            leaseCounts[serverID] = remaining
        }
    }

    /// Suspend until all in-flight leases for `serverID` have drained.
    private func drain(_ serverID: UUID) async {
        guard (leaseCounts[serverID] ?? 0) > 0 else { return }
        await withCheckedContinuation { (continuation: CheckedContinuation<Void, Never>) in
            drainWaiters[serverID, default: []].append(continuation)
        }
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

    @discardableResult
    func activate(session: FarmSnapshotSession) async -> Bool {
        // D/H4: gate on SUCCESSFUL startup preparation — if residue could not be swept,
        // fail closed rather than activate over a possibly-resurrected namespace.
        guard await ensureStartupPreparation() else { return false }
        // H3: honor the compare-and-set result so callers can refuse to bind a
        // session the authority rejected (older/consumed token). A durable
        // persistence failure at the CAS is also a fail-closed outcome.
        do {
            return try authority.adopt(session)
        } catch {
            return false
        }
    }

    /// Conditionally deactivate ONLY the captured session (H3). A newer activation
    /// that landed during an await survives — this never globally revokes.
    @discardableResult
    func deactivate(session: FarmSnapshotSession) async -> Bool {
        authority.deactivate(session)
    }

    func currentSession() async -> FarmSnapshotSession? {
        authority.currentSession()
    }

    // MARK: Hydrate

    func hydrateActive() async -> FarmSnapshotHydration {
        // D/P4: gate on successful startup readiness; a failed sweep is surfaced, not
        // silently proceeded past.
        guard await ensureStartupPreparation() else { return .unreadable }
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
        // H4: hold a lease so purge drains this quarantine operation before sweeping.
        guard acquireLease(session.serverID) else { return .revoked }
        defer { releaseLease(session.serverID) }
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
        // D/P4: gate on successful startup readiness before any commit.
        guard await ensureStartupPreparation() else { return .persistenceFailure(cleanupFailed: false) }
        // 1. Reject unsupported incoming schema before any durable mutation.
        guard envelope.isSupportedSchema else { return .schemaUnsupported }
        // 2. The candidate must belong to the captured session's namespace.
        guard envelope.namespace == capturedSession.namespace else { return .namespaceMismatch }
        // 3. Cheap short-circuit; the authoritative check is inside the promotion.
        guard authority.isCurrent(capturedSession) else { return .superseded }
        // H4: hold a filesystem lease for the whole durable region so purge cannot
        // sweep until this operation drains; refused if the server is purging.
        guard acquireLease(capturedSession.serverID) else { return .superseded }
        defer { releaseLease(capturedSession.serverID) }

        let live = liveURL(capturedSession.namespace)

        // 4. Early integrity + monotonic read (fail-closed on unreadable/corrupt existing).
        let existing: FarmSnapshotEnvelope?
        do {
            if let data = try await fileIO.readData(at: live) {
                guard let decoded = try? FarmSnapshotEnvelope.makeDecoder().decode(FarmSnapshotEnvelope.self, from: data),
                      decoded.isSupportedSchema,
                      decoded.namespace == capturedSession.namespace else {
                    return .integrityFailure(cleanupFailed: false) // no candidate written yet
                }
                existing = decoded
            } else {
                existing = nil
            }
        } catch {
            return .integrityFailure(cleanupFailed: false) // no candidate written yet
        }
        if let existing, existing.lastUpdatedAtMillis >= envelope.lastUpdatedAtMillis {
            return .notNewer(cleanupFailed: false) // no candidate written yet
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
            let cleanupFailed = await cleanup(candidate)
            return .notNewer(cleanupFailed: cleanupFailed)
        case .integrityFailure:
            let cleanupFailed = await cleanup(candidate)
            return .integrityFailure(cleanupFailed: cleanupFailed)
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
        // H4 ordered drain-then-sweep:
        // 0. Replay durable tombstones / residue before touching this server so a
        //    prior crash's residue cannot be mistaken for live state.
        await ensureStartupPreparation()
        // 1. Durable tombstone FIRST — blocks new mints/activation, survives restart.
        //    H (issue #816 reject, Hicks): the tombstone write is now verified
        //    and throwing. A verified-durable failure fails the purge closed:
        //    no on-disk sweep, no purge-success reported, so a caller (registry
        //    deletion) can refuse to remove the server for which we could not
        //    guarantee a durable tombstone barrier.
        do {
            try authority.tombstone(serverID)
        } catch {
            return .failed(failureCount: 1)
        }
        // 2. Refuse new filesystem leases for this server.
        purging.insert(serverID)
        // 3. Clear the persisted owner mapping so a stale owner cannot re-select it.
        ownerStore?.clearOwner(serverID: serverID)
        // 4. Drain all in-flight commit/quarantine leases before touching the disk. The
        //    lease is the real serialization primitive: purge cannot sweep (or return)
        //    until every in-flight operation holding a lease for this server releases it.
        await drain(serverID)
        // 6. Final recursive sweep of live/temp/quarantine; surface removal failures.
        var failures = 0
        for dir in [serverDir(serverID), quarantineDir(serverID)] {
            do {
                try await fileIO.removeItem(at: dir)
            } catch {
                failures += 1
            }
        }
        purging.remove(serverID) // tombstone remains the durable barrier
        return failures == 0 ? .purged : .failed(failureCount: failures)
    }

    /// Replay durable tombstones and sweep any residual namespaces a crash may have
    /// left between purge and registry removal (H4). Safe to call independently of
    /// activation and before any use; memoized so it runs at most once successfully,
    /// and retried on the next call if a removal failed. Surfaces success/failure.
    @discardableResult
    func prepareStartup() async -> Bool {
        await ensureStartupPreparation()
    }

    @discardableResult
    private func ensureStartupPreparation() async -> Bool {
        if startupComplete { return true }
        if let task = startupTask { return await task.value }
        let task = Task { await self.runStartupSweep() }
        startupTask = task
        let ok = await task.value
        startupTask = nil
        if ok { startupComplete = true } // mark complete ONLY after a successful sweep
        return ok
    }

    private func runStartupSweep() async -> Bool {
        var allSucceeded = true
        for serverID in authority.tombstonedServerIDs() {
            for dir in [serverDir(serverID), quarantineDir(serverID)] {
                do {
                    try await fileIO.removeItem(at: dir) // absent == success; genuine errors throw
                } catch {
                    allSucceeded = false // do NOT mark complete; retry on the next op
                }
            }
        }
        return allSucceeded
    }
}
