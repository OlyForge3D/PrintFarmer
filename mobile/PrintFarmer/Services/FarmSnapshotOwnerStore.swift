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

/// Persistent record of purged server UUIDs.
final class FarmSnapshotTombstoneStore: @unchecked Sendable {
    static let key = "pf_snapshot_tombstones"
    static let highWaterKey = "pf_snapshot_highwater"

    private let userDefaults: UserDefaults
    private let lock = NSLock()

    init(userDefaults: UserDefaults = .standard) {
        self.userDefaults = userDefaults
    }

    /// The durably-persisted authority high-water mark (H). Survives process/store
    /// recreation so a delayed older token can never re-adopt after relaunch.
    func loadHighWater() -> UInt64 {
        lock.lock()
        defer { lock.unlock() }
        return (userDefaults.object(forKey: Self.highWaterKey) as? NSNumber)?.uint64Value ?? 0
    }

    /// Durably advance the persisted high-water to `value` (monotonic; never lowers).
    func storeHighWater(_ value: UInt64) {
        lock.lock()
        defer { lock.unlock() }
        let existing = (userDefaults.object(forKey: Self.highWaterKey) as? NSNumber)?.uint64Value ?? 0
        if value > existing {
            userDefaults.set(NSNumber(value: value), forKey: Self.highWaterKey)
        }
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
