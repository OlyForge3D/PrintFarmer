import Foundation
import KeychainSwift

struct ServerCredentials: Equatable, Sendable {
    var accessToken: String
    var expiresAt: Date?
}

final class ServerCredentialsStore: @unchecked Sendable {
    static let legacyTokenKey = "pf_jwt_token"
    static let legacyTokenExpiryKey = "pf_token_expiry"

    private let keychain: KeychainSwift
    private let keychainAccess: KeychainSwiftAccessOptions = .accessibleAfterFirstUnlockThisDeviceOnly
    private let lock = NSLock()

    init(keychain: KeychainSwift = KeychainSwift()) {
        self.keychain = keychain
    }

    func save(_ credentials: ServerCredentials, serverId: UUID) {
        lock.lock()
        defer { lock.unlock() }

        keychain.set(credentials.accessToken, forKey: tokenKey(serverId: serverId), withAccess: keychainAccess)
        if let expiresAt = credentials.expiresAt {
            keychain.set(
                String(expiresAt.timeIntervalSince1970),
                forKey: expiryKey(serverId: serverId),
                withAccess: keychainAccess
            )
        } else {
            keychain.delete(expiryKey(serverId: serverId))
        }
    }

    func load(serverId: UUID) -> ServerCredentials? {
        lock.lock()
        defer { lock.unlock() }

        guard let accessToken = keychain.get(tokenKey(serverId: serverId)) else { return nil }
        return ServerCredentials(
            accessToken: accessToken,
            expiresAt: expiryDate(forKey: expiryKey(serverId: serverId))
        )
    }

    func delete(serverId: UUID) {
        clear(serverId: serverId)
    }

    func clear(serverId: UUID) {
        lock.lock()
        defer { lock.unlock() }

        keychain.delete(tokenKey(serverId: serverId))
        keychain.delete(expiryKey(serverId: serverId))
    }

    /// J (issue #816 reject, Hicks): compare-and-clear the stored credentials for
    /// `serverId` IFF the currently-persisted access token equals
    /// `expectedAccessToken`. Rollback primitive for a login/restore that
    /// published its credentials and then failed a subsequent operation-fenced
    /// destination (e.g. apiClient CAS, owner write, or activate). If a newer
    /// T2 login has already re-saved credentials for the SAME server (a
    /// different access token by construction), the compare fails and T2's
    /// credentials are left untouched. Returns whether the clear happened.
    @discardableResult
    func clearIfAccessTokenMatches(serverId: UUID, expectedAccessToken: String) -> Bool {
        lock.lock()
        defer { lock.unlock() }
        guard let current = keychain.get(tokenKey(serverId: serverId)),
              current == expectedAccessToken else {
            return false
        }
        keychain.delete(tokenKey(serverId: serverId))
        keychain.delete(expiryKey(serverId: serverId))
        return true
    }

    @discardableResult
    func migrateLegacyCredentialsIfNeeded(to serverId: UUID) -> Bool {
        lock.lock()
        defer { lock.unlock() }

        guard keychain.get(tokenKey(serverId: serverId)) == nil,
              let legacyToken = keychain.get(Self.legacyTokenKey) else {
            clearLegacyCredentialsLocked()
            return false
        }

        keychain.set(legacyToken, forKey: tokenKey(serverId: serverId), withAccess: keychainAccess)
        if let legacyExpiry = keychain.get(Self.legacyTokenExpiryKey) {
            keychain.set(legacyExpiry, forKey: expiryKey(serverId: serverId), withAccess: keychainAccess)
        }
        clearLegacyCredentialsLocked()
        return true
    }

    func clearLegacyCredentials() {
        lock.lock()
        defer { lock.unlock() }

        clearLegacyCredentialsLocked()
    }

    func isExpired(serverId: UUID, now: Date = Date(), bufferSeconds: TimeInterval = 5 * 60) -> Bool {
        guard let expiresAt = load(serverId: serverId)?.expiresAt else { return false }
        return now.addingTimeInterval(bufferSeconds) >= expiresAt
    }

    func tokenKey(serverId: UUID) -> String {
        "pf_server_\(serverId.uuidString)_jwt_token"
    }

    func expiryKey(serverId: UUID) -> String {
        "pf_server_\(serverId.uuidString)_token_expiry"
    }

    private func expiryDate(forKey key: String) -> Date? {
        guard let expiryString = keychain.get(key),
              let expiryInterval = Double(expiryString) else {
            return nil
        }
        return Date(timeIntervalSince1970: expiryInterval)
    }

    private func clearLegacyCredentialsLocked() {
        keychain.delete(Self.legacyTokenKey)
        keychain.delete(Self.legacyTokenExpiryKey)
    }
}
