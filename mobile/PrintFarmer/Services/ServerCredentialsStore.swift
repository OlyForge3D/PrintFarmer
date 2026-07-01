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
    private let lock = NSLock()

    init(keychain: KeychainSwift = KeychainSwift()) {
        self.keychain = keychain
    }

    func save(_ credentials: ServerCredentials, serverId: UUID) {
        lock.lock()
        defer { lock.unlock() }

        keychain.set(credentials.accessToken, forKey: tokenKey(serverId: serverId))
        if let expiresAt = credentials.expiresAt {
            keychain.set(String(expiresAt.timeIntervalSince1970), forKey: expiryKey(serverId: serverId))
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

    @discardableResult
    func migrateLegacyCredentialsIfNeeded(to serverId: UUID) -> Bool {
        lock.lock()
        defer { lock.unlock() }

        guard keychain.get(tokenKey(serverId: serverId)) == nil,
              let legacyToken = keychain.get(Self.legacyTokenKey) else {
            clearLegacyCredentialsLocked()
            return false
        }

        keychain.set(legacyToken, forKey: tokenKey(serverId: serverId))
        if let legacyExpiry = keychain.get(Self.legacyTokenExpiryKey) {
            keychain.set(legacyExpiry, forKey: expiryKey(serverId: serverId))
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
