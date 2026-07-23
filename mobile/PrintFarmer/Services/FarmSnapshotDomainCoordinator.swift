import Foundation

// MARK: - Shared per-domain coordinator (issue #816 H)
//
// The reject: "Cross-instance domain coordinator must own current session,
// tombstones, high-water/reserved/adopted state and promotion/destructive
// validation—not allocation only. A stale instance cannot promote after B
// adopts/tombstones."
//
// Before H: two `FarmSnapshotAuthority` instances sharing a persistence domain
// used the SAME NSLock (via a static `[String: NSLock]` dictionary) but each
// held its OWN `current: FarmSnapshotSession?` and `tombstones` cache in memory.
// A `withPromotion` on A validated against A's own `current` — B's mutation to
// its own `current` was invisible. That let A promote a session B had already
// superseded/tombstoned.
//
// After H: `FarmSnapshotDomainCoordinator` is a single object per persistence
// domain, resolved via a WEAK-REF registry. Every Authority bound to a domain
// holds a strong reference to the coordinator; the last Authority to be
// deinitialised drops the coordinator. The coordinator owns:
//   * the lock
//   * `current: FarmSnapshotSession?`
//   * the tombstones cache (durable set stays in `FarmSnapshotTombstoneStore`)
//   * every critical section (`withPromotion`, adopt, deactivate, tombstone…)
// The tombstone store still provides the durable atomic CAS on the persisted
// reserved/adopted high-water and durable tombstone set; the coordinator is the
// in-memory coherence point that shares state across all Authorities in the
// domain.
//
// Lifecycle: weak references in `registry` mean an unused domain is
// automatically evicted as soon as no Authority is bound to it. This satisfies
// the reject's "Static coordinator lifecycle must not retain random domains
// forever or permit two locks while live; use lifecycle-safe weak/lease
// registry."
final class FarmSnapshotDomainCoordinator: @unchecked Sendable {
    private let domainIdentifier: String
    private let tombstoneStore: FarmSnapshotTombstoneStore
    private let durableRecord: FarmSnapshotDurableAuthorityRecord?
    let lock = NSLock()

    private var current: FarmSnapshotSession?
    private var tombstoneCache: Set<UUID>
    /// H1 (issue #816 reject, Vasquez): set when the durable record threw
    /// `.persistenceFailure` at coordinator hydration (corrupt/unreadable
    /// existing file). Any subsequent reserve/mint/adopt/tombstone
    /// operation MUST fail closed with the same typed throw — there is no
    /// path that silently proceeds on a poisoned record. Reset ONLY by
    /// discarding this coordinator (release + rebuild).
    private var durableRecordPoisoned: Bool = false

    fileprivate init(
        domainIdentifier: String,
        tombstoneStore: FarmSnapshotTombstoneStore,
        durableRecord: FarmSnapshotDurableAuthorityRecord?
    ) {
        self.domainIdentifier = domainIdentifier
        self.tombstoneStore = tombstoneStore
        self.durableRecord = durableRecord
        // H (issue #816 reject, Hicks): hydrate the in-memory tombstone cache
        // from BOTH durable sources at reopen — the UserDefaults tombstone store
        // AND the file-backed durable record. A tombstone persisted to the file
        // record must be observable by the fresh coordinator on the next launch
        // (even when the UserDefaults suite is empty), otherwise a purged server
        // could be silently re-activated after a crash between the record write
        // and the UserDefaults write.
        //
        // H1 (issue #816 reject, Vasquez): if the file record is corrupt at
        // hydration, mark the coordinator poisoned and do NOT hydrate any
        // tombstones from it. The UserDefaults tombstone seed alone survives —
        // no attempt to guess bytes, no publish of blank state, no clear of
        // corrupt bytes. Mutations subsequently fail closed at their entry
        // points, so no purged server can silently re-activate and no token
        // can be minted against a poisoned authority.
        var seed = tombstoneStore.load()
        if let durableRecord {
            do {
                seed.formUnion(try durableRecord.loadTombstones())
            } catch {
                self.durableRecordPoisoned = true
            }
        }
        self.tombstoneCache = seed
    }

    var domain: String { domainIdentifier }

    // MARK: Registry (weak/lease)

    /// Weak wrapper — the registry holds a weak reference so an unused domain's
    /// coordinator is evicted when the last Authority referencing it dies.
    private final class Weak {
        weak var value: FarmSnapshotDomainCoordinator?
        init(_ value: FarmSnapshotDomainCoordinator) { self.value = value }
    }

    private static let registryLock = NSLock()
    nonisolated(unsafe) private static var registry: [String: Weak] = [:]

    /// Resolve (or lazily create) the coordinator for a persistence domain. All
    /// Authorities that pass the same `tombstoneStore.domain` share ONE
    /// coordinator, so their `current`/`tombstones`/`withPromotion` observe the
    /// same in-memory state.
    static func coordinator(
        for tombstoneStore: FarmSnapshotTombstoneStore,
        durableRecord: FarmSnapshotDurableAuthorityRecord? = nil
    ) -> FarmSnapshotDomainCoordinator {
        let identifier = tombstoneStore.domain
        registryLock.lock()
        defer { registryLock.unlock() }
        if let existing = registry[identifier]?.value {
            return existing
        }
        // Clean up dead entries lazily so the registry cannot grow unbounded even
        // in long-lived test-suite runs (H reject: "must not retain random
        // domains forever").
        registry = registry.compactMapValues { $0.value == nil ? nil : $0 }
        let created = FarmSnapshotDomainCoordinator(
            domainIdentifier: identifier,
            tombstoneStore: tombstoneStore,
            durableRecord: durableRecord
        )
        registry[identifier] = Weak(created)
        return created
    }

    /// Test / suite-teardown helper: forcibly drop the coordinator entry for a
    /// domain. Only safe when no live Authority still holds a reference.
    static func releaseCoordinator(forDomain identifier: String) {
        registryLock.lock()
        defer { registryLock.unlock() }
        registry.removeValue(forKey: identifier)
    }

    // MARK: Shared state — current session

    func currentSession() -> FarmSnapshotSession? {
        lock.lock(); defer { lock.unlock() }
        return current
    }

    func isCurrent(_ session: FarmSnapshotSession) -> Bool {
        lock.lock(); defer { lock.unlock() }
        return current == session && !tombstoneCache.contains(session.serverID)
    }

    // MARK: Shared state — reserve/mint/adopt (delegates durable CAS)

    /// H1 (issue #816 reject, Vasquez): every mutation entry point on a poisoned
    /// coordinator fails closed with the exact typed error. The coordinator
    /// cannot resurrect itself by continuing on partial state, so the caller
    /// must observe `.persistenceFailure` and refuse to publish anything.
    private func throwIfPoisonedLocked() throws {
        if durableRecordPoisoned {
            throw FarmSnapshotAuthorityError.persistenceFailure
        }
    }

    func reserve(namespace: FarmSnapshotNamespace, generation: Int) throws -> FarmSnapshotSession? {
        lock.lock(); defer { lock.unlock() }
        try throwIfPoisonedLocked()
        guard !tombstoneCache.contains(namespace.serverID) else { return nil }
        let reserved: UInt64
        if let durableRecord {
            reserved = try durableRecord.reserveNextToken(atLeast: tombstoneStore.loadReservedHighWater())
            _ = tombstoneStore.storeHighWater(reserved)
        } else {
            reserved = try tombstoneStore.reserveNextToken(atLeast: tombstoneStore.loadReservedHighWater())
        }
        return FarmSnapshotSession(namespace: namespace, generation: generation, token: reserved)
    }

    func mint(namespace: FarmSnapshotNamespace, generation: Int) throws -> FarmSnapshotSession? {
        lock.lock(); defer { lock.unlock() }
        try throwIfPoisonedLocked()
        guard !tombstoneCache.contains(namespace.serverID) else { return nil }
        let reserved: UInt64
        let adopted: Bool
        if let durableRecord {
            reserved = try durableRecord.reserveNextToken(atLeast: tombstoneStore.loadReservedHighWater())
            _ = tombstoneStore.storeHighWater(reserved)
            adopted = try durableRecord.tryAdopt(token: reserved)
            _ = try tombstoneStore.tryAdopt(token: reserved)
        } else {
            reserved = try tombstoneStore.reserveNextToken(atLeast: tombstoneStore.loadReservedHighWater())
            adopted = try tombstoneStore.tryAdopt(token: reserved)
        }
        guard adopted else { throw FarmSnapshotAuthorityError.persistenceFailure }
        let session = FarmSnapshotSession(namespace: namespace, generation: generation, token: reserved)
        current = session
        return session
    }

    @discardableResult
    func adopt(_ session: FarmSnapshotSession) throws -> Bool {
        lock.lock(); defer { lock.unlock() }
        try throwIfPoisonedLocked()
        guard !tombstoneCache.contains(session.serverID) else { return false }
        if session == current { return true }
        let accepted: Bool
        if let durableRecord {
            accepted = try durableRecord.tryAdopt(token: session.token)
            _ = try tombstoneStore.tryAdopt(token: session.token)
        } else {
            accepted = try tombstoneStore.tryAdopt(token: session.token)
        }
        guard accepted else { return false }
        current = session
        return true
    }

    // MARK: Shared state — deactivate / revoke / tombstone

    func revoke() {
        lock.lock(); defer { lock.unlock() }
        current = nil
    }

    @discardableResult
    func deactivate(_ session: FarmSnapshotSession) -> Bool {
        lock.lock(); defer { lock.unlock() }
        guard current == session else { return false }
        current = nil
        return true
    }

    /// H (issue #816 reject, Hicks): tombstone `serverID` durably. Writes to the
    /// file-backed durable record (verified) BEFORE mutating the UserDefaults
    /// tombstone store or the in-memory cache, so a durable-write failure surfaces
    /// as a typed throw with NO advertised tombstone (production callers can
    /// treat the purge as failed and refuse to remove the server). On success,
    /// updates the UserDefaults store, the in-memory cache, and clears
    /// `current` if it matches.
    func tombstone(_ serverID: UUID) throws {
        lock.lock(); defer { lock.unlock() }
        try throwIfPoisonedLocked()
        // Durable file record first (throws on verified-read mismatch). If this
        // throws, nothing observable changes and purge/promotion callers can
        // refuse to proceed.
        if let durableRecord {
            try durableRecord.insertTombstone(serverID)
        }
        tombstoneCache.insert(serverID)
        tombstoneStore.insert(serverID)
        if current?.serverID == serverID {
            current = nil
        }
    }

    /// H (issue #816 reject, Hicks): clear a tombstone durably. Durable file
    /// record write is verified and throwing; failure leaves the tombstone in
    /// place (fail-closed for the tombstone barrier) and surfaces to the caller.
    func clearTombstone(_ serverID: UUID) throws {
        lock.lock(); defer { lock.unlock() }
        try throwIfPoisonedLocked()
        if let durableRecord {
            try durableRecord.removeTombstone(serverID)
        }
        tombstoneCache.remove(serverID)
        tombstoneStore.remove(serverID)
    }

    func isTombstoned(_ serverID: UUID) -> Bool {
        lock.lock(); defer { lock.unlock() }
        return tombstoneCache.contains(serverID)
    }

    func tombstonedServerIDs() -> Set<UUID> {
        lock.lock(); defer { lock.unlock() }
        return tombstoneCache
    }

    /// Cross-instance promotion critical section. Evaluated under the coordinator
    /// lock so ANY authority bound to this domain observes the same
    /// `current`/`tombstones`/cancellation — a stale Authority can never promote
    /// after a peer has adopted or tombstoned.
    func withPromotion<T>(
        _ session: FarmSnapshotSession,
        cancelled: () -> Bool,
        _ body: () throws -> T
    ) rethrows -> T? {
        lock.lock(); defer { lock.unlock() }
        guard current == session, !tombstoneCache.contains(session.serverID), !cancelled() else {
            return nil
        }
        return try body()
    }
}

// MARK: - Durable file-backed authority record (issue #816 H)
//
// The reject: "Replace cache-only UserDefaults readback as durability proof
// with a genuinely durable atomic persistence mechanism suitable for app (e.g.
// atomic file-backed authority record under snapshot root with errors surfaced)
// or otherwise ensure confirmed durable transaction."
//
// This record persists the highest RESERVED token, highest ADOPTED token, and
// the tombstone set as an atomic file under the snapshot root:
//
//     <rootURL>/farm_snapshot_authority.json
//
// Every mutation writes with `Data.write(to:options:[.atomic])` (posix atomic
// rename), then verifies the write by re-reading the file. A verification
// mismatch throws `.persistenceFailure`. No mutation returns a token / adoption
// before the durable atomic write is verified. Read is best-effort — a corrupt
// file resets in-memory state and is overwritten on next write.
//
// This record is OPTIONAL and additive: production wires it in via
// `ServiceContainer`, existing UserDefaults-based tests keep working without
// it. When present, mutations must succeed on BOTH the tombstone store
// (UserDefaults) and the file record; when absent, tombstone store alone
// remains the durable source.
final class FarmSnapshotDurableAuthorityRecord: @unchecked Sendable {
    private struct Payload: Codable, Equatable {
        var reservedHighWater: UInt64
        var adoptedHighWater: UInt64
        var tombstones: [String]

        static let empty = Payload(reservedHighWater: 0, adoptedHighWater: 0, tombstones: [])
    }

    private let recordURL: URL
    private let fileManager = FileManager.default
    /// H2 (issue #816 reject, Vasquez): the read-modify-write lock is
    /// canonicalized by absolute file path so that two record instances
    /// pointing at the same file share the same critical section. Without
    /// this, distinct instances would each hold their own `NSLock` and two
    /// concurrent read-modify-write sequences on the same file could
    /// interleave (same-domain, different-object bug).
    private let lock: NSLock

    /// Path relative to the snapshot root. The file name is stable so multiple
    /// instances pointing at the same root converge on the same file.
    static let filename = "farm_snapshot_authority.json"

    /// H2 cross-instance file-path lock registry. Every record instance
    /// resolves its lock through this registry keyed on the standardized
    /// file path, so two records pointing at the same file share a lock.
    private static let pathLocksLock = NSLock()
    nonisolated(unsafe) private static var pathLocks: [String: NSLock] = [:]

    private static func lock(forPath path: String) -> NSLock {
        pathLocksLock.lock()
        defer { pathLocksLock.unlock() }
        if let existing = pathLocks[path] { return existing }
        let created = NSLock()
        pathLocks[path] = created
        return created
    }

    /// Test / suite-teardown helper: forcibly drop the cached lock entry for a
    /// canonical path. Only safe when no live record on that path is still in
    /// use. Symmetric with `FarmSnapshotTombstoneStore.releaseCoordinator`.
    static func releasePathLock(forURL url: URL) {
        let key = url.standardizedFileURL.path
        pathLocksLock.lock()
        defer { pathLocksLock.unlock() }
        pathLocks.removeValue(forKey: key)
    }

    init(rootURL: URL) {
        let url = rootURL.appendingPathComponent(Self.filename, isDirectory: false)
        self.recordURL = url
        self.lock = FarmSnapshotDurableAuthorityRecord.lock(forPath: url.standardizedFileURL.path)
    }

    // MARK: Read / write primitives

    /// H1 (issue #816 reject, Vasquez): classify a physical read into three
    /// exact outcomes so `absent` (initialize-to-empty) can NEVER be
    /// conflated with `corrupt` (unreadable or undecodable existing file).
    /// The corrupt case is what a silent `try?` used to swallow into an
    /// empty payload, silently resetting authority state; classified this
    /// way, corrupt propagates through `readLocked` as a typed throw.
    private enum ReadOutcome {
        case absent
        case present(Payload)
        case corrupt(underlying: Error?)
    }

    private func classifyReadLocked() -> ReadOutcome {
        guard fileManager.fileExists(atPath: recordURL.path) else { return .absent }
        let data: Data
        do {
            data = try Data(contentsOf: recordURL)
        } catch {
            return .corrupt(underlying: error)
        }
        do {
            let decoded = try JSONDecoder().decode(Payload.self, from: data)
            return .present(decoded)
        } catch {
            return .corrupt(underlying: error)
        }
    }

    private func readLocked() throws -> Payload {
        switch classifyReadLocked() {
        case .absent: return .empty
        case .present(let payload): return payload
        case .corrupt:
            // H1: an existing-but-unreadable/undecodable file MUST propagate
            // as the exact typed error. Callers use this to fail closed;
            // there is no code path that silently overwrites corrupt bytes.
            throw FarmSnapshotAuthorityError.persistenceFailure
        }
    }

    private func writeLocked(_ payload: Payload) throws {
        try fileManager.createDirectory(
            at: recordURL.deletingLastPathComponent(),
            withIntermediateDirectories: true
        )
        // H3 (issue #816 reject, Vasquez): capture the exact prior-verified
        // bytes BEFORE the atomic write so that a write/verify failure can
        // restore them and never leave the record with data neither the old
        // payload nor the new payload matches. Prior bytes are captured
        // only when the current file is a valid `present` payload (absent →
        // nothing to restore; corrupt → refuse to write at all).
        let priorBytes: Data?
        switch classifyReadLocked() {
        case .absent:
            priorBytes = nil
        case .present:
            priorBytes = try? Data(contentsOf: recordURL)
        case .corrupt:
            // Never overwrite corrupt bytes; a mutation on a corrupt record
            // must fail closed with no side effects (H1 invariant).
            throw FarmSnapshotAuthorityError.persistenceFailure
        }

        let encoded = try JSONEncoder().encode(payload)
        try encoded.write(to: recordURL, options: [.atomic])
        // H (issue #816 reject, Hicks): test-only injection point BETWEEN the
        // acknowledged write and the verifying re-read, so an
        // acknowledged-but-lost persistence event can be deterministically
        // reproduced (delete the file to simulate loss). The interceptor is a
        // static test hook: production callers never touch it, and it is nil
        // in every production composition.
        Self.testInterceptAfterAtomicWrite?(recordURL)
        // Verify via re-read — if the OS reported success but a crash / partial
        // flush left different bytes, we must surface that as a typed failure.
        // H3: on failure, restore the exact captured prior bytes (or remove
        // the file if there were none) so the record is never left holding
        // the failed write's partially-applied state.
        let verifiedOK: Bool
        switch classifyReadLocked() {
        case .absent, .corrupt:
            verifiedOK = false
        case .present(let verified):
            verifiedOK = verified == payload
        }
        guard verifiedOK else {
            restoreLocked(bytes: priorBytes)
            throw FarmSnapshotAuthorityError.persistenceFailure
        }
    }

    /// H3: restore captured prior-verified bytes best-effort. A failed
    /// restore leaves the file in whatever state the failed write left it,
    /// but the caller has already thrown `.persistenceFailure` so no
    /// downstream publication can happen.
    private func restoreLocked(bytes priorBytes: Data?) {
        if let priorBytes {
            try? priorBytes.write(to: recordURL, options: [.atomic])
        } else {
            try? fileManager.removeItem(at: recordURL)
        }
    }

    /// H (issue #816 reject, Hicks): test-only static interceptor invoked
    /// AFTER the atomic write and BEFORE the verifying re-read of the durable
    /// file record. Tests set this to inject an acknowledged-but-lost
    /// persistence event (e.g. delete the file), driving the exact typed
    /// `.persistenceFailure` throw path deterministically. MUST be nil in
    /// production; MUST be reset by test teardown.
    nonisolated(unsafe) static var testInterceptAfterAtomicWrite: (@Sendable (URL) -> Void)?

    // MARK: Public API

    /// H1 (issue #816 reject, Vasquez): throwing accessor — a corrupt on-disk
    /// record surfaces as `.persistenceFailure` instead of silently
    /// returning zero. An absent file returns zero. Callers that participate
    /// in the fail-closed contract MUST use this.
    func loadReservedHighWater() throws -> UInt64 {
        lock.lock(); defer { lock.unlock() }
        return try readLocked().reservedHighWater
    }

    /// H1: throwing accessor — corrupt → `.persistenceFailure`, absent → 0.
    func loadAdoptedHighWater() throws -> UInt64 {
        lock.lock(); defer { lock.unlock() }
        return try readLocked().adoptedHighWater
    }

    /// H1: throwing accessor — corrupt → `.persistenceFailure`, absent → ∅.
    /// A caller that fails to propagate this throw would silently drop
    /// durable tombstones (letting a purged server reactivate), so it must
    /// be handled explicitly by every reader.
    func loadTombstones() throws -> Set<UUID> {
        lock.lock(); defer { lock.unlock() }
        return Set(try readLocked().tombstones.compactMap(UUID.init(uuidString:)))
    }

    /// Atomically reserve the next token — durable BEFORE return. Throws typed
    /// `.tokenSpaceExhausted` on UInt64 overflow, or `.persistenceFailure` on
    /// verified-read mismatch. H1: also throws `.persistenceFailure` when the
    /// existing file is unreadable/undecodable (never resets to zero).
    func reserveNextToken(atLeast: UInt64 = 0) throws -> UInt64 {
        lock.lock(); defer { lock.unlock() }
        var payload = try readLocked()
        let base = max(payload.reservedHighWater, atLeast)
        let (next, overflow) = base.addingReportingOverflow(1)
        guard !overflow else { throw FarmSnapshotAuthorityError.tokenSpaceExhausted }
        payload.reservedHighWater = next
        try writeLocked(payload)
        return next
    }

    /// Attempt to durably adopt `token`. Returns false when a peer has already
    /// adopted this-or-higher (delayed old). Throws `.persistenceFailure` on
    /// verified-read mismatch or a corrupt existing record (H1).
    func tryAdopt(token: UInt64) throws -> Bool {
        lock.lock(); defer { lock.unlock() }
        var payload = try readLocked()
        guard token > payload.adoptedHighWater else { return false }
        payload.adoptedHighWater = token
        if token > payload.reservedHighWater { payload.reservedHighWater = token }
        try writeLocked(payload)
        return true
    }

    /// H (issue #816 reject, Hicks): verified, throwing durable tombstone insert.
    /// Writes atomically and re-reads to confirm the tombstone is now durably
    /// present; throws `.persistenceFailure` on a verified-read mismatch or
    /// underlying I/O failure or when the existing file is corrupt (H1).
    /// Callers MUST NOT report a tombstone as durable on failure (purge fails
    /// closed).
    func insertTombstone(_ serverID: UUID) throws {
        lock.lock(); defer { lock.unlock() }
        var payload = try readLocked()
        var set = Set(payload.tombstones)
        set.insert(serverID.uuidString)
        payload.tombstones = Array(set)
        try writeLocked(payload)
        // Explicit verification: the re-read set MUST include this server ID.
        // (writeLocked already verifies exact equality; this is a belt-and-braces
        // check that surfaces any future writeLocked semantics regression.)
        let verified = try readLocked()
        guard Set(verified.tombstones).contains(serverID.uuidString) else {
            throw FarmSnapshotAuthorityError.persistenceFailure
        }
    }

    /// H (issue #816 reject, Hicks): verified, throwing durable tombstone
    /// removal. Symmetric with `insertTombstone` — fails closed if the
    /// atomic write or verified re-read does not observe the tombstone gone,
    /// or when the existing file is corrupt (H1).
    func removeTombstone(_ serverID: UUID) throws {
        lock.lock(); defer { lock.unlock() }
        var payload = try readLocked()
        var set = Set(payload.tombstones)
        set.remove(serverID.uuidString)
        payload.tombstones = Array(set)
        try writeLocked(payload)
        let verified = try readLocked()
        guard !Set(verified.tombstones).contains(serverID.uuidString) else {
            throw FarmSnapshotAuthorityError.persistenceFailure
        }
    }
}
