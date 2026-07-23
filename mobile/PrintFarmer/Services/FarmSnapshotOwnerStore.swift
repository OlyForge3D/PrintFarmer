import Foundation

// MARK: - Farm Snapshot Owner Store (F10-C1a, #816)
//
// Persists the *non-secret* stable authenticated user UUID for each registered
// server. This is deliberately NOT stored in the Keychain credential store: the
// user id is not a secret, and keeping it out of `ServerCredentialsStore` means
// the existing token storage is neither extended with new secrets nor weakened.
//
// The owner identity is the offline source of truth for snapshot namespacing:
//   * successful login / online-verified restore persists owner[serverID] = userID
//   * activation resolves the *settled active server's own* owner — never a
//     carried cross-server user id — which is what makes a `(serverB, userA)`
//     binding impossible.
//   * a token-only legacy record (no persisted owner) fails closed: no hydrate
//     until an online verification establishes the owner.
//   * explicit logout / server removal clears the owner; a transient offline or
//     network failure must never call `clear`, so the last verified owner
//     survives to drive cold-offline restore.

/// Records who is verified on each server, keyed strictly by the stable server UUID.
final class FarmSnapshotOwnerStore: @unchecked Sendable {
    static let keyPrefix = "pf_snapshot_owner_"

    private let userDefaults: UserDefaults
    private let lock = NSLock()

    init(userDefaults: UserDefaults = .standard) {
        self.userDefaults = userDefaults
    }

    /// Storage key for a server's owner id. Namespaced by the stable server UUID.
    func ownerKey(serverID: UUID) -> String {
        "\(Self.keyPrefix)\(serverID.uuidString)"
    }

    /// Persist the verified owner for a server. Overwrites any prior owner (a new
    /// verified login on the same server legitimately replaces the previous one).
    func setOwner(userID: UUID, serverID: UUID) {
        lock.lock()
        defer { lock.unlock() }
        userDefaults.set(userID.uuidString, forKey: ownerKey(serverID: serverID))
    }

    /// The verified owner user id for a server, or `nil` when none has ever been
    /// persisted (token-only legacy record → fail closed).
    func ownerUserID(serverID: UUID) -> UUID? {
        lock.lock()
        defer { lock.unlock() }
        guard let raw = userDefaults.string(forKey: ownerKey(serverID: serverID)) else {
            return nil
        }
        return UUID(uuidString: raw)
    }

    /// Clear the owner for a server. Call this ONLY on explicit logout / server
    /// removal — never on transient offline/network failures.
    func clearOwner(serverID: UUID) {
        lock.lock()
        defer { lock.unlock() }
        userDefaults.removeObject(forKey: ownerKey(serverID: serverID))
    }
}

// MARK: - Durable tombstone store (F10-C1a, #816, H4)
//
// Persists the set of purged/pending-deletion server UUIDs so a tombstone
// survives the restart/crash window between `purge` and registry removal. A
// durably-tombstoned server can never re-activate or have its cache recreated,
// which is what makes post-purge resurrection impossible even across a crash.

/// Errors that can occur when durably reserving or advancing the persistent
/// monotonic high-water mark (issue #816 H). Callers must NEVER publish a token
/// that was not successfully durably reserved, and must NEVER trap on overflow.
enum FarmSnapshotAuthorityError: Error, Equatable, Sendable {
    /// The 64-bit token space is exhausted; caller must fail closed. Bounded to
    /// UInt64.max via `addingReportingOverflow` — no trap.
    case tokenSpaceExhausted
    /// The durable reservation write failed (verified re-read didn't observe it).
    /// The caller MUST NOT publish or return a token.
    case persistenceFailure
}

/// Persistent record of purged server UUIDs plus a durable, atomic monotonic
/// high-water reservation (issue #816 H). Two live tombstone-store instances
/// pointing at the SAME persistence domain share ONE cross-instance
/// coordination lock keyed on the domain identifier — so their high-water
/// reservations serialize even though the instances are distinct objects. That
/// coordination is what makes a two-live-authority scenario safe: neither can
/// see a stale in-memory cached high-water, and neither can mint the same token.
final class FarmSnapshotTombstoneStore: @unchecked Sendable {
    static let key = "pf_snapshot_tombstones"
    static let highWaterKey = "pf_snapshot_highwater"
    /// Sentinel identifying the shared domain for `.standard` UserDefaults.
    static let standardDomainIdentifier = "pf_snapshot_standard_domain"

    private let userDefaults: UserDefaults
    /// Stable identity for the underlying persistence domain. Two instances with
    /// the same identifier coordinate through the same static lock.
    private let domainIdentifier: String
    private let lock: NSLock

    // MARK: - Cross-instance coordination
    // Domain-keyed NSLocks that let distinct tombstone-store instances on the same
    // persistence domain serialize their read-modify-write CAS on the durable
    // high-water. Without this, two live authorities can each read cached
    // high-water = 0 and both mint token = 1 (H bug).
    private static let domainLocksLock = NSLock()
    nonisolated(unsafe) private static var domainLocks: [String: NSLock] = [:]

    private static func lock(forDomain identifier: String) -> NSLock {
        domainLocksLock.lock()
        defer { domainLocksLock.unlock() }
        if let existing = domainLocks[identifier] { return existing }
        let created = NSLock()
        domainLocks[identifier] = created
        return created
    }

    /// Release the cached coordinator lock for `identifier`. Callers should use this
    /// only when the domain is being torn down (e.g. suite cleanup between test
    /// runs) — never while any live authority may still be using it.
    static func releaseCoordinator(forDomain identifier: String) {
        domainLocksLock.lock()
        defer { domainLocksLock.unlock() }
        domainLocks.removeValue(forKey: identifier)
    }

    init(userDefaults: UserDefaults = .standard, domainIdentifier: String = FarmSnapshotTombstoneStore.standardDomainIdentifier) {
        self.userDefaults = userDefaults
        self.domainIdentifier = domainIdentifier
        self.lock = FarmSnapshotTombstoneStore.lock(forDomain: domainIdentifier)
    }

    /// The stable durable-domain identifier this store coordinates on.
    var domain: String { domainIdentifier }

    /// The durably-persisted authority high-water mark (H). Survives process/store
    /// recreation so a delayed older token can never re-adopt after relaunch.
    func loadHighWater() -> UInt64 {
        lock.lock()
        defer { lock.unlock() }
        return (userDefaults.object(forKey: Self.highWaterKey) as? NSNumber)?.uint64Value ?? 0
    }

    /// Durably advance the persisted high-water to `value` (monotonic; never lowers).
    /// Verifies via re-read; returns whether the requested value is now durably
    /// observed at-or-above `value`.
    @discardableResult
    func storeHighWater(_ value: UInt64) -> Bool {
        lock.lock()
        defer { lock.unlock() }
        let existing = (userDefaults.object(forKey: Self.highWaterKey) as? NSNumber)?.uint64Value ?? 0
        let target = max(existing, value)
        if target > existing {
            userDefaults.set(NSNumber(value: target), forKey: Self.highWaterKey)
        }
        let verified = (userDefaults.object(forKey: Self.highWaterKey) as? NSNumber)?.uint64Value ?? 0
        return verified >= value
    }

    /// H: atomically reserve a NEW durable token strictly greater than every prior
    /// reservation on this persistence domain. Read-modify-write is serialized under
    /// the domain's shared coordination lock, so two live tombstone-store instances
    /// on the SAME domain (e.g. two authorities racing a mint) cannot both reserve
    /// the same token. Overflow at `UInt64.max` returns `.tokenSpaceExhausted` with
    /// NO token published; a verified-read mismatch after write returns
    /// `.persistenceFailure` with NO token published.
    ///
    /// - Parameter atLeast: a hint of the caller's minimum acceptable next value
    ///   (usually the in-memory counter). The reserved value is guaranteed to be
    ///   strictly greater than the previous durable value AND >= `atLeast`.
    /// - Returns: the newly reserved token (durably persisted before return).
    func reserveNextToken(atLeast: UInt64 = 0) throws -> UInt64 {
        lock.lock()
        defer { lock.unlock() }
        let existing = (userDefaults.object(forKey: Self.highWaterKey) as? NSNumber)?.uint64Value ?? 0
        // Base is strictly greater than any previously reserved token.
        let base = max(existing, atLeast)
        let (next, overflow) = base.addingReportingOverflow(1)
        guard !overflow else { throw FarmSnapshotAuthorityError.tokenSpaceExhausted }
        userDefaults.set(NSNumber(value: next), forKey: Self.highWaterKey)
        let verified = (userDefaults.object(forKey: Self.highWaterKey) as? NSNumber)?.uint64Value ?? 0
        guard verified >= next else { throw FarmSnapshotAuthorityError.persistenceFailure }
        return next
    }

    /// The persisted tombstone set (server UUIDs).
    func load() -> Set<UUID> {
        lock.lock()
        defer { lock.unlock() }
        guard let raw = userDefaults.array(forKey: Self.key) as? [String] else { return [] }
        return Set(raw.compactMap(UUID.init(uuidString:)))
    }

    /// Durably mark a server as tombstoned. Idempotent.
    func insert(_ serverID: UUID) {
        lock.lock()
        defer { lock.unlock() }
        var set = Set((userDefaults.array(forKey: Self.key) as? [String]) ?? [])
        set.insert(serverID.uuidString)
        userDefaults.set(Array(set), forKey: Self.key)
    }

    /// Remove a server's tombstone. Only valid once the server's identity/ID
    /// lifecycle is complete (registry removal done); server UUIDs are never
    /// reused, so this is purely housekeeping.
    func remove(_ serverID: UUID) {
        lock.lock()
        defer { lock.unlock() }
        var set = Set((userDefaults.array(forKey: Self.key) as? [String]) ?? [])
        set.remove(serverID.uuidString)
        userDefaults.set(Array(set), forKey: Self.key)
    }
}
