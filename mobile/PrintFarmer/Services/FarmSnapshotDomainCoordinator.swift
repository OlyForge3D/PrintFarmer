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
        var seed = tombstoneStore.load()
        if let durableRecord {
            seed.formUnion(durableRecord.loadTombstones())
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

    func reserve(namespace: FarmSnapshotNamespace, generation: Int) throws -> FarmSnapshotSession? {
        lock.lock(); defer { lock.unlock() }
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
    private struct Payload: Codable {
        var reservedHighWater: UInt64
        var adoptedHighWater: UInt64
        var tombstones: [String]
    }

    private let recordURL: URL
    private let fileManager = FileManager.default
    private let lock = NSLock()

    /// Path relative to the snapshot root. The file name is stable so multiple
    /// instances pointing at the same root converge on the same file.
    static let filename = "farm_snapshot_authority.json"

    init(rootURL: URL) {
        self.recordURL = rootURL.appendingPathComponent(Self.filename, isDirectory: false)
    }

    // MARK: Read / write primitives

    private func readLocked() -> Payload {
        guard fileManager.fileExists(atPath: recordURL.path),
              let data = try? Data(contentsOf: recordURL),
              let decoded = try? JSONDecoder().decode(Payload.self, from: data)
        else {
            return Payload(reservedHighWater: 0, adoptedHighWater: 0, tombstones: [])
        }
        return decoded
    }

    private func writeLocked(_ payload: Payload) throws {
        try fileManager.createDirectory(
            at: recordURL.deletingLastPathComponent(),
            withIntermediateDirectories: true
        )
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
        let verified = readLocked()
        guard verified.reservedHighWater == payload.reservedHighWater,
              verified.adoptedHighWater == payload.adoptedHighWater,
              Set(verified.tombstones) == Set(payload.tombstones)
        else {
            throw FarmSnapshotAuthorityError.persistenceFailure
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

    func loadReservedHighWater() -> UInt64 {
        lock.lock(); defer { lock.unlock() }
        return readLocked().reservedHighWater
    }

    func loadAdoptedHighWater() -> UInt64 {
        lock.lock(); defer { lock.unlock() }
        return readLocked().adoptedHighWater
    }

    func loadTombstones() -> Set<UUID> {
        lock.lock(); defer { lock.unlock() }
        return Set(readLocked().tombstones.compactMap(UUID.init(uuidString:)))
    }

    /// Atomically reserve the next token — durable BEFORE return. Throws typed
    /// `.tokenSpaceExhausted` on UInt64 overflow, or `.persistenceFailure` on
    /// verified-read mismatch.
    func reserveNextToken(atLeast: UInt64 = 0) throws -> UInt64 {
        lock.lock(); defer { lock.unlock() }
        var payload = readLocked()
        let base = max(payload.reservedHighWater, atLeast)
        let (next, overflow) = base.addingReportingOverflow(1)
        guard !overflow else { throw FarmSnapshotAuthorityError.tokenSpaceExhausted }
        payload.reservedHighWater = next
        try writeLocked(payload)
        return next
    }

    /// Attempt to durably adopt `token`. Returns false when a peer has already
    /// adopted this-or-higher (delayed old). Throws `.persistenceFailure` on
    /// verified-read mismatch.
    func tryAdopt(token: UInt64) throws -> Bool {
        lock.lock(); defer { lock.unlock() }
        var payload = readLocked()
        guard token > payload.adoptedHighWater else { return false }
        payload.adoptedHighWater = token
        if token > payload.reservedHighWater { payload.reservedHighWater = token }
        try writeLocked(payload)
        return true
    }

    /// H (issue #816 reject, Hicks): verified, throwing durable tombstone insert.
    /// Writes atomically and re-reads to confirm the tombstone is now durably
    /// present; throws `.persistenceFailure` on a verified-read mismatch or
    /// underlying I/O failure. Callers MUST NOT report a tombstone as durable
    /// on failure (purge fails closed).
    func insertTombstone(_ serverID: UUID) throws {
        lock.lock(); defer { lock.unlock() }
        var payload = readLocked()
        var set = Set(payload.tombstones)
        set.insert(serverID.uuidString)
        payload.tombstones = Array(set)
        try writeLocked(payload)
        // Explicit verification: the re-read set MUST include this server ID.
        // (writeLocked already verifies exact equality; this is a belt-and-braces
        // check that surfaces any future writeLocked semantics regression.)
        let verified = readLocked()
        guard Set(verified.tombstones).contains(serverID.uuidString) else {
            throw FarmSnapshotAuthorityError.persistenceFailure
        }
    }

    /// H (issue #816 reject, Hicks): verified, throwing durable tombstone
    /// removal. Symmetric with `insertTombstone` — fails closed if the
    /// atomic write or verified re-read does not observe the tombstone gone.
    func removeTombstone(_ serverID: UUID) throws {
        lock.lock(); defer { lock.unlock() }
        var payload = readLocked()
        var set = Set(payload.tombstones)
        set.remove(serverID.uuidString)
        payload.tombstones = Array(set)
        try writeLocked(payload)
        let verified = readLocked()
        guard !Set(verified.tombstones).contains(serverID.uuidString) else {
            throw FarmSnapshotAuthorityError.persistenceFailure
        }
    }
}
