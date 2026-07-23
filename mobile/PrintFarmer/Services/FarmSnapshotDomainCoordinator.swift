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
        // H1 (issue #816 reject, Vasquez): if the file record is corrupt at
        // hydration, mark the coordinator poisoned and do NOT hydrate any
        // tombstones from it. The UserDefaults tombstone seed alone survives —
        // no attempt to guess bytes, no publish of blank state, no clear of
        // corrupt bytes. Mutations subsequently fail closed at their entry
        // points, so no purged server can silently re-activate and no token
        // can be minted against a poisoned authority.
        //
        // B (issue #816 reject, Hicks + replacement Vasquez): the UserDefaults
        // tombstone seed is also read fail-closed (`loadStrict`). A corrupt /
        // schema-inconsistent UserDefaults authority store (non-`[String]`,
        // invalid UUID, or duplicate) poisons the coordinator instead of
        // silently `compactMap`-dropping a purged server from the set.
        var seed: Set<UUID>
        do {
            seed = try tombstoneStore.loadStrict()
        } catch {
            seed = []
            self.durableRecordPoisoned = true
        }
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

    /// Resolve (or lazily create) the coordinator for a persistence domain.
    ///
    /// B (issue #816 reject, Hicks + replacement Vasquez): the registry is keyed
    /// on BOTH the UserDefaults domain AND the durable file record's canonical
    /// physical root — not the domain alone. Two live containers that share a
    /// UserDefaults suite but point at DIFFERENT durable roots therefore resolve
    /// to DIFFERENT coordinators and can never share/discard one file record; and
    /// a rootless (`durableRecord == nil`) composition is keyed distinctly from a
    /// rooted one, so incompatible reuse is rejected by construction rather than
    /// silently sharing an unrelated record.
    static func registryKey(
        domain: String,
        durableRecord: FarmSnapshotDurableAuthorityRecord?
    ) -> String {
        "\(domain)|root=\(durableRecord?.canonicalPathIdentity ?? "none")"
    }

    static func coordinator(
        for tombstoneStore: FarmSnapshotTombstoneStore,
        durableRecord: FarmSnapshotDurableAuthorityRecord? = nil
    ) -> FarmSnapshotDomainCoordinator {
        let identifier = tombstoneStore.domain
        let key = registryKey(domain: identifier, durableRecord: durableRecord)
        registryLock.lock()
        defer { registryLock.unlock() }
        if let existing = registry[key]?.value {
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
        registry[key] = Weak(created)
        return created
    }

    /// Test / suite-teardown helper: forcibly drop coordinator entries for a
    /// domain. Only safe when no live Authority still holds a reference. Because
    /// the registry key now composes the durable root, all root variants for the
    /// domain are removed.
    static func releaseCoordinator(forDomain identifier: String) {
        registryLock.lock()
        defer { registryLock.unlock() }
        let prefix = "\(identifier)|root="
        for key in registry.keys where key == identifier || key.hasPrefix(prefix) {
            registry.removeValue(forKey: key)
        }
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
    /// B (issue #816 reject, Hicks + replacement Vasquez): the read-modify-write
    /// lock is shared by *canonical physical identity* (not merely
    /// `standardizedFileURL.path`) so symlink aliases and case aliases that
    /// resolve to the SAME physical file share ONE critical section. The lease
    /// is reference-counted via a weak registry: the record holds the lease
    /// strongly, so the lock lives exactly as long as any live record on that
    /// physical path — a force-release while a live record still holds the lease
    /// is refused.
    private let lockLease: PathLockLease
    private var lock: NSLock { lockLease.lock }

    /// The canonical physical identity of this record's file, used both as the
    /// shared-lock key (B) and as part of the coordinator registry key so two
    /// containers with different durable roots never share/discard one record.
    let canonicalPathIdentity: String

    // MARK: F/V1 — constructor-injected test seam (no shipping global, no #if DEBUG)

    /// F/V1 (issue #816 reject, Vasquez): the after-atomic-write hook is injected
    /// at construction as an immutable dependency — NOT a `#if DEBUG` settable
    /// var. This closes two defects: (F) there is no shipping global/mutable test
    /// state, and (V1) the seam compiles in EVERY configuration including a
    /// Release test build (a `#if DEBUG` method would not exist there, so the test
    /// call site failed to compile). Production compositions construct the record
    /// WITHOUT a hook (`nil`), so the seam is fully inert/absent in the shipping
    /// Release product. Tests pass an arm-able closure to simulate an
    /// acknowledged-but-lost persistence event.
    private let afterAtomicWriteHook: (@Sendable (URL) -> Void)?

    /// Path relative to the snapshot root. The file name is stable so multiple
    /// instances pointing at the same root converge on the same file.
    static let filename = "farm_snapshot_authority.json"

    /// B: a reference-counted lock lease. The registry holds it weakly; live
    /// records hold it strongly. When the last record on a physical path dies the
    /// lease deinits and is evicted automatically — so the registry cannot retain
    /// dead paths forever, and no two live locks can exist for one physical file.
    final class PathLockLease {
        let lock = NSLock()
        let key: String
        init(key: String) { self.key = key }
    }

    private final class WeakLease {
        weak var value: PathLockLease?
        init(_ value: PathLockLease) { self.value = value }
    }

    private static let pathLocksLock = NSLock()
    nonisolated(unsafe) private static var pathLeases: [String: WeakLease] = [:]

    /// B: derive a canonical *physical* identity for `url`, safe BEFORE the file
    /// exists. Resolves symlinks on the (already-existing-or-createable) parent
    /// directory, re-appends the stable filename, applies canonical Unicode
    /// mapping, and lowercases (default APFS/HFS+ are case-insensitive, so case
    /// aliases must map to one identity). This closes the symlink/case-alias
    /// lock-split the reject called out.
    static func canonicalIdentity(for url: URL) -> String {
        let standardized = url.standardizedFileURL
        let parent = standardized.deletingLastPathComponent()
        let resolvedParent = parent.resolvingSymlinksInPath()
        let combined = resolvedParent.appendingPathComponent(standardized.lastPathComponent, isDirectory: false)
        return combined.path.precomposedStringWithCanonicalMapping.lowercased()
    }

    private static func lease(forIdentity identity: String) -> PathLockLease {
        pathLocksLock.lock()
        defer { pathLocksLock.unlock() }
        if let existing = pathLeases[identity]?.value { return existing }
        // Evict dead entries lazily so the registry cannot grow unbounded.
        pathLeases = pathLeases.filter { $0.value.value != nil }
        let created = PathLockLease(key: identity)
        pathLeases[identity] = WeakLease(created)
        return created
    }

    /// Test / suite-teardown helper: drop the cached lease entry for a physical
    /// path ONLY if no live record still holds it. B: force-release while a live
    /// record retains the lease is refused (the entry is left in place), so a
    /// live record can never have its lock silently swapped out from under it.
    @discardableResult
    static func releasePathLock(forURL url: URL) -> Bool {
        let identity = canonicalIdentity(for: url)
        pathLocksLock.lock()
        defer { pathLocksLock.unlock() }
        if pathLeases[identity]?.value != nil {
            // A live record still holds the lease — refuse to force-release.
            return false
        }
        pathLeases.removeValue(forKey: identity)
        return true
    }

    init(rootURL: URL, afterAtomicWriteHook: (@Sendable (URL) -> Void)? = nil) {
        let url = rootURL.appendingPathComponent(Self.filename, isDirectory: false)
        self.recordURL = url
        let identity = FarmSnapshotDurableAuthorityRecord.canonicalIdentity(for: url)
        self.canonicalPathIdentity = identity
        self.lockLease = FarmSnapshotDurableAuthorityRecord.lease(forIdentity: identity)
        self.afterAtomicWriteHook = afterAtomicWriteHook
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

    /// A (issue #816 reject): distinguish a *true* file-not-found (ENOENT →
    /// initialize-to-empty is legitimate) from any other read failure
    /// (permission, traversal, I/O, metadata → corrupt/fail-closed). A generic
    /// `fileExists == false` is NEVER treated as absence, because `stat` can fail
    /// for reasons other than non-existence.
    private static func isFileNotFound(_ error: Error) -> Bool {
        let ns = error as NSError
        if ns.domain == NSCocoaErrorDomain,
           ns.code == NSFileReadNoSuchFileError || ns.code == NSFileNoSuchFileError {
            return true
        }
        if ns.domain == NSPOSIXErrorDomain, ns.code == Int(ENOENT) { return true }
        if let underlying = ns.userInfo[NSUnderlyingErrorKey] as? NSError {
            if underlying.domain == NSPOSIXErrorDomain, underlying.code == Int(ENOENT) { return true }
            if underlying.domain == NSCocoaErrorDomain,
               underlying.code == NSFileReadNoSuchFileError || underlying.code == NSFileNoSuchFileError {
                return true
            }
        }
        return false
    }

    /// B (issue #816 reject): validate a decoded payload's semantics. A payload
    /// whose invariants are violated is `corrupt`, NOT silently repaired:
    ///   * `reservedHighWater` must be >= `adoptedHighWater` (a reservation
    ///     high-water below the adopted high-water is impossible for a
    ///     well-formed authority record).
    ///   * every tombstone string must be a valid UUID (never `compactMap`ped
    ///     away) and must be unique (no duplicate/aliased entries).
    private static func isSemanticallyValid(_ payload: Payload) -> Bool {
        guard payload.reservedHighWater >= payload.adoptedHighWater else { return false }
        var seen = Set<UUID>()
        for raw in payload.tombstones {
            guard let uuid = UUID(uuidString: raw) else { return false }
            let (inserted, _) = seen.insert(uuid)
            guard inserted else { return false }
        }
        return true
    }

    private func classifyReadLocked() -> ReadOutcome {
        let data: Data
        do {
            data = try Data(contentsOf: recordURL)
        } catch {
            // A: only a genuine ENOENT is absence; everything else fails closed.
            return Self.isFileNotFound(error) ? .absent : .corrupt(underlying: error)
        }
        do {
            let decoded = try JSONDecoder().decode(Payload.self, from: data)
            guard Self.isSemanticallyValid(decoded) else { return .corrupt(underlying: nil) }
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
            // A/H1: an existing-but-unreadable/undecodable/semantically-invalid
            // file MUST propagate as the exact typed error. Callers use this to
            // fail closed; there is no code path that silently overwrites corrupt
            // bytes.
            throw FarmSnapshotAuthorityError.persistenceFailure
        }
    }

    /// C (issue #816 reject): throwing raw-byte read of an existing present
    /// record, with NO `try?`. A present payload whose bytes cannot be re-read
    /// (permission/I/O) fails closed rather than proceeding with a nil backup.
    private func readPresentBytesLocked() throws -> Data {
        do {
            return try Data(contentsOf: recordURL)
        } catch {
            throw FarmSnapshotAuthorityError.persistenceFailure
        }
    }

    private func writeLocked(_ payload: Payload) throws {
        do {
            try fileManager.createDirectory(
                at: recordURL.deletingLastPathComponent(),
                withIntermediateDirectories: true
            )
        } catch {
            // A: a directory-creation failure (permission/traversal) is a typed
            // persistence failure — never a silently-swallowed no-op.
            throw FarmSnapshotAuthorityError.persistenceFailure
        }
        // C (issue #816 reject, Hicks + replacement Vasquez): capture the exact
        // prior-verified bytes BEFORE the atomic write so a write/verify failure
        // can restore them byte-for-byte. NO `try?`: a present record whose bytes
        // cannot be re-read fails closed; absent → nothing to restore; corrupt →
        // refuse to write at all.
        let priorBytes: Data?
        switch classifyReadLocked() {
        case .absent:
            priorBytes = nil
        case .present:
            priorBytes = try readPresentBytesLocked()
        case .corrupt:
            throw FarmSnapshotAuthorityError.persistenceFailure
        }

        let encoded = try JSONEncoder().encode(payload)
        do {
            try encoded.write(to: recordURL, options: [.atomic])
        } catch {
            // The atomic write itself failed — the OS rename never landed, so the
            // prior bytes are still intact. Surface as a typed failure.
            throw FarmSnapshotAuthorityError.persistenceFailure
        }
        // F: per-instance test hook between the acknowledged write and the
        // verifying re-read, so an acknowledged-but-lost persistence event can be
        // deterministically reproduced. Nil in production.
        afterAtomicWriteHook?(recordURL)
        // Verify via re-read — if the OS reported success but a crash / partial
        // flush left different bytes, surface that as a typed failure. C: on
        // failure, restore the exact captured prior bytes (or remove the file if
        // there were none); if restoration ALSO fails, surface a typed COMPOSITE
        // failure retaining both contexts rather than swallowing the recovery
        // error.
        let verifiedOK: Bool
        switch classifyReadLocked() {
        case .absent, .corrupt:
            verifiedOK = false
        case .present(let verified):
            verifiedOK = verified == payload
        }
        guard verifiedOK else {
            try restoreLocked(bytes: priorBytes, primaryContext: "durable write verification failed")
            throw FarmSnapshotAuthorityError.persistenceFailure
        }
    }

    /// C: restore captured prior-verified bytes. NO `try?`: on a restore failure
    /// the recovery error is captured and re-thrown as a typed composite
    /// `.restorationFailure(primary:recovery:)` so a genuine double-fault is
    /// surfaced (never swallowed). A successful restore returns normally and the
    /// caller then throws the primary `.persistenceFailure` — the record is left
    /// byte-identical to before the failed mutation.
    private func restoreLocked(bytes priorBytes: Data?, primaryContext: String) throws {
        do {
            if let priorBytes {
                try priorBytes.write(to: recordURL, options: [.atomic])
            } else {
                if fileManager.fileExists(atPath: recordURL.path) {
                    try fileManager.removeItem(at: recordURL)
                }
            }
        } catch {
            throw FarmSnapshotAuthorityError.restorationFailure(
                primary: primaryContext,
                recovery: String(describing: error)
            )
        }
        // V6 (issue #816 reject, Vasquez): a restore is not trustworthy until it is
        // VERIFIED. Re-read the record and confirm it is byte-identical to the
        // captured prior bytes (or genuinely absent when there were none). If the
        // acknowledged restore write did not actually land, surface the typed
        // COMPOSITE failure rather than reporting a plain persistence failure that
        // falsely implies the prior bytes are intact.
        do {
            if let priorBytes {
                let readback = try Data(contentsOf: recordURL)
                guard readback == priorBytes else {
                    throw FarmSnapshotAuthorityError.restorationFailure(
                        primary: primaryContext,
                        recovery: "restored bytes did not verify (readback mismatch)"
                    )
                }
            } else {
                guard !fileManager.fileExists(atPath: recordURL.path) else {
                    throw FarmSnapshotAuthorityError.restorationFailure(
                        primary: primaryContext,
                        recovery: "record still present after restore-to-absent"
                    )
                }
            }
        } catch let error as FarmSnapshotAuthorityError {
            throw error
        } catch {
            throw FarmSnapshotAuthorityError.restorationFailure(
                primary: primaryContext,
                recovery: "restoration readback failed: \(String(describing: error))"
            )
        }
    }

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

    /// H1/B: throwing accessor — corrupt/invalid → `.persistenceFailure`,
    /// absent → ∅. Uses the semantically-validated payload (`readLocked`), so a
    /// tombstone list containing an invalid or duplicate UUID has ALREADY failed
    /// closed before this point — the map below can never silently drop an entry.
    /// A caller that fails to propagate this throw would silently drop durable
    /// tombstones (letting a purged server reactivate), so it must be handled
    /// explicitly by every reader.
    func loadTombstones() throws -> Set<UUID> {
        lock.lock(); defer { lock.unlock() }
        let payload = try readLocked()
        var set = Set<UUID>()
        for raw in payload.tombstones {
            guard let uuid = UUID(uuidString: raw) else {
                throw FarmSnapshotAuthorityError.persistenceFailure
            }
            set.insert(uuid)
        }
        return set
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
