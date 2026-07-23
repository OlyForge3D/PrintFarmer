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

/// J1 (issue #816 reject, Hicks): the prior verified owner captured immediately
/// before this operation published its own owner. Rollback restores this
/// exact prior state (rather than clearing) so an equal-user T2 relogin that
/// happens between T1's write and T1's rollback does not lose its own
/// publication.
struct FarmSnapshotOwnerPriorState: Sendable, Equatable {
    let userID: UUID?
    let operationToken: Int?
}

/// Records who is verified on each server, keyed strictly by the stable server UUID.
final class FarmSnapshotOwnerStore: @unchecked Sendable {
    static let keyPrefix = "pf_snapshot_owner_"
    /// J1 (issue #816 reject, Hicks): storage key suffix carrying the exact
    /// AuthOperationToken.value that most recently wrote the owner. Enables
    /// operation-tagged CAS so an equal-user T2 that reused the same userID
    /// but under a newer operation is distinguishable from T1's own write.
    static let operationKeySuffix = "_op"

    private let userDefaults: UserDefaults
    private let lock = NSLock()

    init(userDefaults: UserDefaults = .standard) {
        self.userDefaults = userDefaults
    }

    /// Storage key for a server's owner id. Namespaced by the stable server UUID.
    func ownerKey(serverID: UUID) -> String {
        "\(Self.keyPrefix)\(serverID.uuidString)"
    }

    /// J1: storage key for the AuthOperationToken.value that most recently
    /// wrote the owner for `serverID`.
    func operationTokenKey(serverID: UUID) -> String {
        "\(ownerKey(serverID: serverID))\(Self.operationKeySuffix)"
    }

    /// Persist the verified owner for a server. Overwrites any prior owner (a new
    /// verified login on the same server legitimately replaces the previous one).
    func setOwner(userID: UUID, serverID: UUID) {
        lock.lock()
        defer { lock.unlock() }
        userDefaults.set(userID.uuidString, forKey: ownerKey(serverID: serverID))
    }

    /// J1 (issue #816 reject, Hicks): publish this operation's verified owner
    /// AND tag the write with the operation token, atomically returning the
    /// PRIOR verified state (userID + operationToken). Callers use the
    /// returned state to compare-and-RESTORE on rollback instead of clearing —
    /// so a rollback preserves the previous verified owner rather than
    /// destroying it, and the operation-token CAS makes the rollback
    /// ABA-safe against an equal-user T2 relogin.
    @discardableResult
    func setOwnerCapturingPrior(userID: UUID, serverID: UUID, operationToken: Int) -> FarmSnapshotOwnerPriorState {
        lock.lock()
        defer { lock.unlock() }
        let priorUserRaw = userDefaults.string(forKey: ownerKey(serverID: serverID))
        let priorUser = priorUserRaw.flatMap(UUID.init(uuidString:))
        let priorOp = (userDefaults.object(forKey: operationTokenKey(serverID: serverID)) as? NSNumber)?.intValue
        userDefaults.set(userID.uuidString, forKey: ownerKey(serverID: serverID))
        userDefaults.set(NSNumber(value: operationToken), forKey: operationTokenKey(serverID: serverID))
        return FarmSnapshotOwnerPriorState(userID: priorUser, operationToken: priorOp)
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

    /// J1: read-only view of the operation-token tag on the current owner
    /// write. Returns nil for legacy owners written before J1 (no tag) or
    /// when no owner exists. Used by tests to prove ABA-safe rollback
    /// behavior.
    func ownerOperationToken(serverID: UUID) -> Int? {
        lock.lock()
        defer { lock.unlock() }
        return (userDefaults.object(forKey: operationTokenKey(serverID: serverID)) as? NSNumber)?.intValue
    }

    /// Clear the owner for a server. Call this ONLY on explicit logout / server
    /// removal — never on transient offline/network failures.
    func clearOwner(serverID: UUID) {
        lock.lock()
        defer { lock.unlock() }
        userDefaults.removeObject(forKey: ownerKey(serverID: serverID))
        userDefaults.removeObject(forKey: operationTokenKey(serverID: serverID))
    }

    /// J (issue #816 reject, Hicks): compare-and-clear the owner IFF the
    /// currently-persisted owner UUID equals `expectedUserID` (nil-nil also
    /// matches — a rollback of a token-only login that cleared prior owner).
    /// Rollback primitive for a login/restore that persisted the owner and
    /// then failed a subsequent operation-fenced destination. If a newer T2
    /// login has already written its own owner, the compare fails and T2's
    /// owner is preserved. Returns whether the clear happened (an equal
    /// nil-nil match returns true with no side effects).
    ///
    /// J1: this variant is SUPERSEDED by `restoreOwnerIfOperationMatches`
    /// for login rollback paths because it (a) clears instead of restoring
    /// the prior verified owner and (b) is ABA-unsafe when T1 and T2 share
    /// the same userID. Preserved for callers that legitimately want
    /// clear-semantics.
    @discardableResult
    func clearOwnerIfMatches(serverID: UUID, expectedUserID: UUID?) -> Bool {
        lock.lock()
        defer { lock.unlock() }
        let currentRaw = userDefaults.string(forKey: ownerKey(serverID: serverID))
        let current = currentRaw.flatMap(UUID.init(uuidString:))
        guard current == expectedUserID else { return false }
        userDefaults.removeObject(forKey: ownerKey(serverID: serverID))
        userDefaults.removeObject(forKey: operationTokenKey(serverID: serverID))
        return true
    }

    /// J1 (issue #816 reject, Hicks): compare-and-RESTORE the prior owner
    /// state IFF the currently-persisted owner-operation-token equals
    /// `expectedOperationToken`. Used to roll back a login's owner
    /// publication: if this operation still owns the destination, the
    /// captured prior state is restored (an existing prior owner is put
    /// back, or the owner is cleared if there was none). If a newer T2
    /// has already written its own owner-operation-token, the compare
    /// fails and T2's state is preserved untouched. Returns whether the
    /// restore happened.
    @discardableResult
    func restoreOwnerIfOperationMatches(
        serverID: UUID,
        expectedOperationToken: Int,
        prior: FarmSnapshotOwnerPriorState
    ) -> Bool {
        lock.lock()
        defer { lock.unlock() }
        let currentOp = (userDefaults.object(forKey: operationTokenKey(serverID: serverID)) as? NSNumber)?.intValue
        guard currentOp == expectedOperationToken else { return false }
        if let priorUser = prior.userID {
            userDefaults.set(priorUser.uuidString, forKey: ownerKey(serverID: serverID))
        } else {
            userDefaults.removeObject(forKey: ownerKey(serverID: serverID))
        }
        if let priorOp = prior.operationToken {
            userDefaults.set(NSNumber(value: priorOp), forKey: operationTokenKey(serverID: serverID))
        } else {
            userDefaults.removeObject(forKey: operationTokenKey(serverID: serverID))
        }
        return true
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
    /// The durable reservation write failed (verified re-read didn't observe it),
    /// OR an existing record was unreadable/undecodable/semantically invalid, OR
    /// a permission/traversal/metadata error prevented a fail-closed read. The
    /// caller MUST NOT publish or return a token. When a write fails but the exact
    /// prior bytes were successfully restored, this is the surfaced error (the
    /// record is left byte-identical to before the failed mutation).
    case persistenceFailure
    /// C (issue #816 reject, Hicks + replacement Vasquez): a durable write failed
    /// AND the subsequent restoration of the exact prior bytes ALSO failed, so the
    /// record may be left holding neither the old nor the new payload. Both the
    /// primary (write/verify) failure and the recovery (restore) failure context
    /// are retained here rather than swallowed, so the caller can surface a
    /// genuine double-fault instead of a plain persistence failure. String
    /// descriptions keep the typed error `Equatable`/`Sendable`.
    case restorationFailure(primary: String, recovery: String)
}

/// Persistent record of purged server UUIDs plus TWO durable, atomic monotonic
/// counters (issue #816 H): a `reservedHighWater` that advances at every token
/// reservation, and an `adoptedHighWater` that advances only at adoption. Two
/// live tombstone-store instances pointing at the SAME persistence domain share
/// ONE cross-instance coordination lock keyed on the domain identifier — so
/// their read-modify-write CAS on either counter serializes even though the
/// instances are distinct objects. That coordination is what makes a
/// two-live-authority scenario safe: neither can see a stale in-memory cached
/// counter, and neither can adopt (or mint) the same token.
final class FarmSnapshotTombstoneStore: @unchecked Sendable {
    static let key = "pf_snapshot_tombstones"
    /// Highest token EVER RESERVED (via reserve or mint or adopt-of-external).
    /// Advances monotonically; a token above this value cannot be a reservation
    /// this domain has issued or accepted.
    static let reservedHighWaterKey = "pf_snapshot_highwater"
    /// Highest token EVER ADOPTED as the current session. Advances monotonically
    /// AT adopt time (only). A delayed adopt with token <= this value is REJECTED
    /// — never re-adopts a stale reservation.
    static let adoptedHighWaterKey = "pf_snapshot_adopted_highwater"
    /// Sentinel identifying the shared domain for `.standard` UserDefaults.
    static let standardDomainIdentifier = "pf_snapshot_standard_domain"

    private let userDefaults: UserDefaults
    /// Stable identity for the underlying persistence domain. Two instances with
    /// the same identifier coordinate through the same static lock.
    private let domainIdentifier: String
    private let lock: NSLock

    // MARK: - Cross-instance coordination
    // Domain-keyed NSLocks that let distinct tombstone-store instances on the same
    // persistence domain serialize their read-modify-write CAS on either durable
    // counter. Without this, two live authorities can each read cached
    // counter = 0 and both mint token = 1 (H bug).
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

    /// The durably-persisted RESERVED high-water (highest token EVER reserved via
    /// reserve/mint/external-adopt). Survives process/store recreation.
    func loadReservedHighWater() -> UInt64 {
        lock.lock()
        defer { lock.unlock() }
        return readReservedLocked()
    }

    /// The durably-persisted ADOPTED high-water (highest token EVER adopted).
    /// Survives process/store recreation.
    func loadAdoptedHighWater() -> UInt64 {
        lock.lock()
        defer { lock.unlock() }
        return readAdoptedLocked()
    }

    /// Backwards-compatible alias for `loadReservedHighWater()` used by callers
    /// that predate the split (issue #816 H). New code should use the two named
    /// accessors above.
    func loadHighWater() -> UInt64 { loadReservedHighWater() }

    private func readReservedLocked() -> UInt64 {
        (userDefaults.object(forKey: Self.reservedHighWaterKey) as? NSNumber)?.uint64Value ?? 0
    }

    private func readAdoptedLocked() -> UInt64 {
        (userDefaults.object(forKey: Self.adoptedHighWaterKey) as? NSNumber)?.uint64Value ?? 0
    }

    /// Durably advance the persisted RESERVED high-water to at least `value`
    /// (monotonic; never lowers). Verifies via re-read; returns whether the
    /// requested value is now durably observed at-or-above `value`.
    @discardableResult
    func storeHighWater(_ value: UInt64) -> Bool {
        lock.lock()
        defer { lock.unlock() }
        let existing = readReservedLocked()
        let target = max(existing, value)
        if target > existing {
            userDefaults.set(NSNumber(value: target), forKey: Self.reservedHighWaterKey)
        }
        let verified = readReservedLocked()
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
    /// The RESERVED counter alone advances; adoption is a separate durable step so
    /// a reserved-but-not-yet-adopted candidate can still be adopted at its exact
    /// reserved token (a plain "advance high-water on reserve then require adopt >
    /// high-water" model would deadlock this).
    ///
    /// - Parameter atLeast: a hint of the caller's minimum acceptable next value
    ///   (usually the in-memory counter). The reserved value is guaranteed to be
    ///   strictly greater than the previous durable value AND >= `atLeast`.
    /// - Returns: the newly reserved token (durably persisted before return).
    func reserveNextToken(atLeast: UInt64 = 0) throws -> UInt64 {
        lock.lock()
        defer { lock.unlock() }
        let existing = readReservedLocked()
        // Base is strictly greater than any previously reserved token.
        let base = max(existing, atLeast)
        let (next, overflow) = base.addingReportingOverflow(1)
        guard !overflow else { throw FarmSnapshotAuthorityError.tokenSpaceExhausted }
        userDefaults.set(NSNumber(value: next), forKey: Self.reservedHighWaterKey)
        let verified = readReservedLocked()
        guard verified >= next else { throw FarmSnapshotAuthorityError.persistenceFailure }
        return next
    }

    /// H: attempt to durably ADOPT `token`. Serialized under the domain's shared
    /// coordination lock; returns true iff `token > durable adoptedHighWater` at
    /// the CAS instant AND the adopted-high-water write is verified durable. Also
    /// advances the RESERVED high-water when adopting an externally-minted token
    /// larger than the current reservation, so a later mint on the same or another
    /// authority in the domain cannot rewind. Throws on persistence failure — the
    /// caller MUST NOT publish the session in that case.
    ///
    /// - Returns: `true` when `token` is now durably the highest adopted token;
    ///   `false` when a peer has already adopted `token`-or-higher (delayed old).
    func tryAdopt(token: UInt64) throws -> Bool {
        lock.lock()
        defer { lock.unlock() }
        let currentAdopted = readAdoptedLocked()
        guard token > currentAdopted else { return false }
        // Advance the adopted high-water atomically.
        userDefaults.set(NSNumber(value: token), forKey: Self.adoptedHighWaterKey)
        let verifiedAdopted = readAdoptedLocked()
        guard verifiedAdopted >= token else { throw FarmSnapshotAuthorityError.persistenceFailure }
        // Also advance the RESERVED counter so a subsequent reserve/mint never rewinds.
        let currentReserved = readReservedLocked()
        if token > currentReserved {
            userDefaults.set(NSNumber(value: token), forKey: Self.reservedHighWaterKey)
            let verifiedReserved = readReservedLocked()
            guard verifiedReserved >= token else { throw FarmSnapshotAuthorityError.persistenceFailure }
        }
        return true
    }

    /// The persisted tombstone set (server UUIDs). Lenient: silently drops any
    /// malformed entry. Retained for non-authority callers; the authority
    /// hydration path MUST use `loadStrict()` so a corrupt UserDefaults store
    /// fails closed instead of silently shrinking the tombstone set.
    func load() -> Set<UUID> {
        lock.lock()
        defer { lock.unlock() }
        guard let raw = userDefaults.array(forKey: Self.key) as? [String] else { return [] }
        return Set(raw.compactMap(UUID.init(uuidString:)))
    }

    /// B (issue #816 reject, Hicks + replacement Vasquez): strict, fail-closed
    /// load of the durable UserDefaults tombstone set. NEVER `compactMap`s
    /// malformed entries: a value that is not a `[String]`, an element that is
    /// not a valid UUID, or a duplicate UUID makes the whole store
    /// schema-inconsistent and throws `.persistenceFailure`. The authority
    /// coordinator uses this so a tampered/corrupt tombstone store cannot
    /// silently drop a purged server (which would let it re-activate).
    func loadStrict() throws -> Set<UUID> {
        lock.lock()
        defer { lock.unlock() }
        guard let stored = userDefaults.object(forKey: Self.key) else { return [] }
        guard let raw = stored as? [String] else {
            throw FarmSnapshotAuthorityError.persistenceFailure
        }
        var set = Set<UUID>()
        for element in raw {
            guard let uuid = UUID(uuidString: element) else {
                throw FarmSnapshotAuthorityError.persistenceFailure
            }
            let (inserted, _) = set.insert(uuid)
            guard inserted else {
                throw FarmSnapshotAuthorityError.persistenceFailure
            }
        }
        return set
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
