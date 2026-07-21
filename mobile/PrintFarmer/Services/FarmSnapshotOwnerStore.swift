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
