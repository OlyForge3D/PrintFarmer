import Foundation
import KeychainSwift

struct ServerCredentials: Equatable, Sendable {
    var accessToken: String
    var expiresAt: Date?
}

/// D (issue #816 reject, Hicks + replacement Vasquez): the prior credential state
/// captured immediately before a publication, including the operation token that
/// most recently wrote the destination. A login/restore rollback restores this
/// EXACT tuple under a compare-and-set on the operation token — so a T2 that
/// republished the SAME bearer with a new expiry/session under a newer operation
/// is NOT clobbered by T1's rollback (the frozen head compared only the access
/// token text and would overwrite T2's fresh expiry).
struct ServerCredentialsPriorState: Sendable, Equatable {
    let credentials: ServerCredentials?
    let operationToken: Int?
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

    /// J1 (issue #816 reject, Hicks): save credentials AND atomically return the
    /// PRIOR credentials for this server, so a rollback can restore that
    /// exact prior state rather than clearing. A missing prior returns nil.
    @discardableResult
    func saveCapturingPrior(_ credentials: ServerCredentials, serverId: UUID) -> ServerCredentials? {
        lock.lock()
        defer { lock.unlock() }
        let priorAccessToken = keychain.get(tokenKey(serverId: serverId))
        let priorExpiry = expiryDate(forKey: expiryKey(serverId: serverId))
        let prior: ServerCredentials?
        if let priorAccessToken {
            prior = ServerCredentials(accessToken: priorAccessToken, expiresAt: priorExpiry)
        } else {
            prior = nil
        }
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
        return prior
    }

    /// D (issue #816 reject): publish credentials AND tag the write with the
    /// operation token, atomically returning the PRIOR full state (credentials +
    /// operation token). Callers roll back via `restoreIfOperationMatches`, which
    /// CASes on the operation token so an equal-bearer T2 relogin under a newer
    /// operation is not lost.
    @discardableResult
    func saveCapturingPriorState(
        _ credentials: ServerCredentials,
        serverId: UUID,
        operationToken: Int
    ) -> ServerCredentialsPriorState {
        lock.lock()
        defer { lock.unlock() }
        let priorAccessToken = keychain.get(tokenKey(serverId: serverId))
        let priorExpiry = expiryDate(forKey: expiryKey(serverId: serverId))
        let priorCredentials = priorAccessToken.map {
            ServerCredentials(accessToken: $0, expiresAt: priorExpiry)
        }
        let priorOperationToken = operationTokenValue(serverId: serverId)

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
        keychain.set(String(operationToken), forKey: operationTokenKey(serverId: serverId), withAccess: keychainAccess)
        return ServerCredentialsPriorState(credentials: priorCredentials, operationToken: priorOperationToken)
    }

    /// D (issue #816 reject): compare-and-RESTORE the prior credential state IFF
    /// the currently-persisted credential-operation-token equals
    /// `expectedOperationToken`. If a newer T2 login has re-published its own
    /// credentials under a newer operation token, the compare fails and T2's
    /// full tuple (bearer + expiry + operation) is preserved untouched. Returns
    /// whether the restore happened.
    @discardableResult
    func restoreIfOperationMatches(
        serverId: UUID,
        expectedOperationToken: Int,
        prior: ServerCredentialsPriorState
    ) -> Bool {
        lock.lock()
        defer { lock.unlock() }
        guard operationTokenValue(serverId: serverId) == expectedOperationToken else {
            return false
        }
        if let priorCredentials = prior.credentials {
            keychain.set(priorCredentials.accessToken, forKey: tokenKey(serverId: serverId), withAccess: keychainAccess)
            if let expiresAt = priorCredentials.expiresAt {
                keychain.set(
                    String(expiresAt.timeIntervalSince1970),
                    forKey: expiryKey(serverId: serverId),
                    withAccess: keychainAccess
                )
            } else {
                keychain.delete(expiryKey(serverId: serverId))
            }
        } else {
            keychain.delete(tokenKey(serverId: serverId))
            keychain.delete(expiryKey(serverId: serverId))
        }
        if let priorOperationToken = prior.operationToken {
            keychain.set(String(priorOperationToken), forKey: operationTokenKey(serverId: serverId), withAccess: keychainAccess)
        } else {
            keychain.delete(operationTokenKey(serverId: serverId))
        }
        return true
    }

    /// D: read-only view of the credential-operation-token tag (for tests /
    /// diagnostics). Nil when untagged.
    func credentialOperationToken(serverId: UUID) -> Int? {
        lock.lock()
        defer { lock.unlock() }
        return operationTokenValue(serverId: serverId)
    }

    /// V4 (issue #816 reject, Vasquez): advance the credential operation tag for a
    /// server WITHOUT rewriting the bearer, used by a successful restore that
    /// re-verified an existing credential under a newer operation. This makes a
    /// concurrent stale login's rollback CAS (on an older tag) fail, so the
    /// restore's freshly-verified credential is not clobbered. No-op when no
    /// credential is stored (nothing to retag).
    @discardableResult
    func retagOperation(serverId: UUID, operationToken: Int) -> Bool {
        lock.lock()
        defer { lock.unlock() }
        guard keychain.get(tokenKey(serverId: serverId)) != nil else { return false }
        keychain.set(String(operationToken), forKey: operationTokenKey(serverId: serverId), withAccess: keychainAccess)
        return true
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
        keychain.delete(operationTokenKey(serverId: serverId))
    }

    /// J1 (issue #816 reject, Hicks): clear both keychain entries and surface
    /// each per-entry deletion outcome. `KeychainSwift.delete` returns `false`
    /// when the item was already absent OR when the underlying SecItemDelete
    /// failed; from the caller's perspective a subsequent successful save
    /// proves the entry can be written, so absence-after-delete is treated as
    /// success (whether the item existed to begin with is not observable via
    /// KeychainSwift's Bool result). This exists so tests can prove clear was
    /// actually invoked; the return value is Bool for tokenDeleted and
    /// expiryDeleted booleans intended for diagnostic use.
    @discardableResult
    func clearReportingKeychainOutcome(serverId: UUID) -> (tokenDeleted: Bool, expiryDeleted: Bool) {
        lock.lock()
        defer { lock.unlock() }
        // KeychainSwift.delete returns whether the delete actually removed an
        // existing item. When no item existed, false is returned — treat that
        // as success (nothing to delete). We surface the raw Bool so tests
        // can prove the API was invoked.
        let tokenExisted = keychain.get(tokenKey(serverId: serverId)) != nil
        let expiryExisted = keychain.get(expiryKey(serverId: serverId)) != nil
        _ = keychain.delete(tokenKey(serverId: serverId))
        _ = keychain.delete(expiryKey(serverId: serverId))
        let tokenGone = keychain.get(tokenKey(serverId: serverId)) == nil
        let expiryGone = keychain.get(expiryKey(serverId: serverId)) == nil
        return (
            tokenDeleted: !tokenExisted || tokenGone,
            expiryDeleted: !expiryExisted || expiryGone
        )
    }

    /// J (issue #816 reject, Hicks): compare-and-clear the stored credentials for
    /// `serverId` IFF the currently-persisted access token equals
    /// `expectedAccessToken`. Rollback primitive for a login/restore that
    /// published its credentials and then failed a subsequent operation-fenced
    /// destination (e.g. apiClient CAS, owner write, or activate). If a newer
    /// T2 login has already re-saved credentials for the SAME server (a
    /// different access token by construction), the compare fails and T2's
    /// credentials are left untouched. Returns whether the clear happened.
    ///
    /// J1: `restoreIfAccessTokenMatches` is preferred for login rollback
    /// because it restores the prior verified credentials (an existing
    /// prior owner is put back rather than the server being left with no
    /// credentials). Retained for callers that legitimately want clear.
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

    /// J1 (issue #816 reject, Hicks): compare-and-RESTORE the prior credentials
    /// IFF the currently-persisted access token equals `expectedAccessToken`.
    /// Used to roll back a login's credential publication: if this operation
    /// still owns the destination, the captured prior credentials are put
    /// back (or the entries are removed if there were none). A newer T2
    /// login for the same server (a different access token by construction)
    /// fails the compare and its state is preserved.
    @discardableResult
    func restoreIfAccessTokenMatches(
        serverId: UUID,
        expectedAccessToken: String,
        prior: ServerCredentials?
    ) -> Bool {
        lock.lock()
        defer { lock.unlock() }
        guard let current = keychain.get(tokenKey(serverId: serverId)),
              current == expectedAccessToken else {
            return false
        }
        if let prior {
            keychain.set(prior.accessToken, forKey: tokenKey(serverId: serverId), withAccess: keychainAccess)
            if let expiresAt = prior.expiresAt {
                keychain.set(
                    String(expiresAt.timeIntervalSince1970),
                    forKey: expiryKey(serverId: serverId),
                    withAccess: keychainAccess
                )
            } else {
                keychain.delete(expiryKey(serverId: serverId))
            }
        } else {
            keychain.delete(tokenKey(serverId: serverId))
            keychain.delete(expiryKey(serverId: serverId))
        }
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

    /// D: keychain key carrying the operation token that most recently published
    /// this server's credentials, enabling operation-CAS rollback.
    func operationTokenKey(serverId: UUID) -> String {
        "pf_server_\(serverId.uuidString)_cred_op"
    }

    private func operationTokenValue(serverId: UUID) -> Int? {
        keychain.get(operationTokenKey(serverId: serverId)).flatMap(Int.init)
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
