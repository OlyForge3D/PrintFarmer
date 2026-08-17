import Foundation
import Observation

enum ServerRegistryError: LocalizedError, Equatable {
    case invalidURL(String)
    case duplicateURL(String)
    case serverNotFound(UUID)
    case purgeUnavailable(UUID)
    case purgeFailed(UUID)
    case certificatePinPurgeFailed(UUID)

    var errorDescription: String? {
        switch self {
        case .invalidURL(let value):
            return "Invalid server URL: \(value)"
        case .duplicateURL(let value):
            return "Server already registered: \(value)"
        case .serverNotFound(let id):
            return "Server not found: \(id.uuidString)"
        case .purgeUnavailable:
            return "Cannot remove this server because its cached data cannot be cleared safely."
        case .purgeFailed:
            return "Could not clear this server's cached data. The server was not removed."
        case .certificatePinPurgeFailed:
            return "Could not clear this server's trusted certificate. The server was not removed."
        }
    }
}

@MainActor
@Observable
final class ServerRegistry {
    static let storageKey = "pf_server_registry"
    static let corruptBackupKey = "pf_server_registry_corrupt_backup"
    static let legacyMigrationCompletedKey = "pf_server_registry_legacy_migration_completed"

    private struct PersistedRegistry: Codable {
        var servers: [RegisteredServer]
        var activeServerID: UUID?
    }

    var servers: [RegisteredServer]
    var activeServerID: UUID?

    /// Awaited snapshot purge authority, wired by `ServiceContainer`. Server
    /// removal is gated on this: a successful purge must complete before the
    /// registry entry is dropped, and its absence fails closed (see
    /// `purgeAndRemove`). Kept out of observation — it is infrastructure wiring,
    /// not view state.
    @ObservationIgnored var snapshotPurgeHandler: (@Sendable (UUID) async -> FarmSnapshotPurgeResult)?
    @ObservationIgnored var certificatePinPurgeHandler: (@Sendable (RegisteredServer, [RegisteredServer]) async -> Bool)?

    @ObservationIgnored private let userDefaults: UserDefaults
    @ObservationIgnored private let now: () -> Date

    // MARK: V3 — monotonic add-revision ownership (issue #816 reject, Vasquez)
    //
    // Registry rollback previously compared only a (createdAt, updatedAt,
    // normalizedURLString) tuple. With a fixed clock (tests) or a renamed/updated
    // reused entry, that tuple can collide, letting one login's rollback delete a
    // NEWER login's reused entry. Each add stamps a process-wide monotonic
    // revision on the entry's id; any subsequent REUSE of that entry by another
    // login bumps the revision. `rollbackAdd` CAS-compares the exact revision the
    // creating login captured, so a rollback whose entry was since reused (bumped)
    // is refused. The map is process-wide (shared across the multiple short-lived
    // ServerRegistry instances the legacy fallback constructs) and NSLock-guarded.
    private static let revisionLock = NSLock()
    nonisolated(unsafe) private static var addRevisions: [UUID: UInt64] = [:]
    nonisolated(unsafe) private static var revisionCounter: UInt64 = 0

    /// Stamp a fresh monotonic revision for a newly-added entry; returns it.
    @discardableResult
    static func stampAddRevision(_ id: UUID) -> UInt64 {
        revisionLock.lock()
        defer { revisionLock.unlock() }
        revisionCounter &+= 1
        addRevisions[id] = revisionCounter
        return revisionCounter
    }

    /// Bump the revision for an entry being REUSED by a (different) login, so any
    /// prior creator's captured revision no longer matches and its rollback CAS
    /// fails. Always advances (defines a revision even for a pre-existing/persisted
    /// entry that had none).
    static func claimReuse(_ id: UUID) {
        revisionLock.lock()
        defer { revisionLock.unlock() }
        revisionCounter &+= 1
        addRevisions[id] = revisionCounter
    }

    /// The current add-revision for an entry, or nil when untracked.
    static func currentAddRevision(_ id: UUID) -> UInt64? {
        revisionLock.lock()
        defer { revisionLock.unlock() }
        return addRevisions[id]
    }

    /// CAS: succeeds (and clears the tracked revision) iff the entry's current
    /// revision equals `expected`. Refused when the revision has advanced (reuse).
    private static func consumeAddRevision(_ id: UUID, expected: UInt64) -> Bool {
        revisionLock.lock()
        defer { revisionLock.unlock() }
        guard addRevisions[id] == expected else { return false }
        addRevisions[id] = nil
        return true
    }

    init(
        userDefaults: UserDefaults = .standard,
        now: @escaping () -> Date = Date.init,
        migrateLegacyServerURL: Bool = true
    ) {
        self.userDefaults = userDefaults
        self.now = now

        if let data = userDefaults.data(forKey: Self.storageKey) {
            do {
                let persisted = try JSONDecoder().decode(PersistedRegistry.self, from: data)
                self.servers = persisted.servers
                self.activeServerID = persisted.activeServerID
            } catch {
                // Keep the unreadable registry blob intact and copy it aside for recovery.
                // Treating this as a fresh install would let migration/sanitize overwrite it.
                userDefaults.set(data, forKey: Self.corruptBackupKey)
                self.servers = []
                self.activeServerID = nil
                return
            }
        } else {
            self.servers = []
            self.activeServerID = nil
        }

        if migrateLegacyServerURL {
            migrateLegacyServerIfNeeded()
        }
        sanitizeActiveSelection()
    }

    var activeServer: RegisteredServer? {
        guard let activeServerID else { return nil }
        return servers.first { $0.id == activeServerID }
    }

    @discardableResult
    func add(displayName: String, baseURL: URL, makeActiveIfNeeded: Bool = true) throws -> RegisteredServer {
        let normalized = try Self.normalizedURLString(for: baseURL.absoluteString)
        try rejectDuplicate(normalizedURLString: normalized)

        let timestamp = now()
        let server = RegisteredServer(
            displayName: normalizedDisplayName(displayName, fallbackURL: baseURL),
            baseURL: URL(string: normalized)!,
            normalizedURLString: normalized,
            createdAt: timestamp,
            updatedAt: timestamp
        )
        servers.append(server)
        if makeActiveIfNeeded && activeServerID == nil {
            activeServerID = server.id
        }
        Self.stampAddRevision(server.id) // V3: monotonic ownership for rollback CAS
        persist()
        return server
    }

    func update(_ server: RegisteredServer) throws {
        guard let index = servers.firstIndex(where: { $0.id == server.id }) else {
            throw ServerRegistryError.serverNotFound(server.id)
        }

        let normalized = try Self.normalizedURLString(for: server.baseURL.absoluteString)
        try rejectDuplicate(normalizedURLString: normalized, ignoring: server.id)

        var updated = server
        if updated.normalizedURLString != normalized {
            updated.originServerId = nil
        }
        updated.displayName = normalizedDisplayName(updated.displayName, fallbackURL: updated.baseURL)
        updated.baseURL = URL(string: normalized)!
        updated.normalizedURLString = normalized
        updated.updatedAt = now()

        servers[index] = updated
        persist()
    }

    /// Raw registry removal. Deliberately `private` so no caller outside this
    /// type can drop a server without going through the awaited
    /// `purgeAndRemove(id:)` purge gate (issue #816, Gate E).
    private func removeEntry(id: UUID) throws {
        guard let index = servers.firstIndex(where: { $0.id == id }) else {
            throw ServerRegistryError.serverNotFound(id)
        }

        servers.remove(at: index)
        if activeServerID == id {
            activeServerID = servers.first?.id
        }
        persist()
    }

    /// J1 (issue #816 reject, Hicks): rollback of an `add()` that happened
    /// as part of a login that later failed to publish credentials /
    /// activate. The removal is safe ONLY when the exact entry we added is
    /// still present (matched by `id`, `createdAt`, and `updatedAt` — a
    /// concurrent update would advance updatedAt) AND when we did NOT
    /// activate it. The caller MUST have ensured no credentials were saved
    /// and no snapshot bytes exist for this server before invoking; this
    /// method does NOT invoke the snapshot purge handler because there is
    /// nothing to purge (login failed before publishing anything durable).
    /// Fails silently (returns false) when the entry no longer matches —
    /// preserves any concurrent state a peer operation may have written.
    @discardableResult
    func rollbackAdd(_ candidate: RegisteredServer, expectedRevision: UInt64) -> Bool {
        guard let index = servers.firstIndex(where: { $0.id == candidate.id }) else {
            return false
        }
        let existing = servers[index]
        // V3 (issue #816 reject, Vasquez): the PRIMARY gate is the monotonic
        // add-revision CAS — a rollback is refused if the entry's revision has
        // advanced since this login created it (i.e. another login reused it).
        // This is robust to a fixed clock / renamed entry that would defeat the
        // timestamp-tuple compare below. The timestamp/URL tuple is retained as an
        // ADDITIONAL guard (defense in depth) so an intervening `update` (rename)
        // that does not bump the revision still blocks a stale rollback.
        guard existing.createdAt == candidate.createdAt,
              existing.updatedAt == candidate.updatedAt,
              existing.normalizedURLString == candidate.normalizedURLString else {
            return false
        }
        // Refuse removal if this login somehow activated the server (should
        // not happen: our resolveActiveServer passes makeActiveIfNeeded=false,
        // and only `activate()` sets it active). Defense in depth.
        if activeServerID == candidate.id { return false }
        // Consume the revision CAS FIRST (side-effect-free on failure).
        guard Self.consumeAddRevision(candidate.id, expected: expectedRevision) else { return false }
        servers.remove(at: index)
        persist()
        return true
    }

    /// Backward-compatible overload: derives the current revision. Retained for
    /// callers that did not capture the add-revision. Prefer the
    /// `expectedRevision:` variant so a reused entry cannot be deleted.
    @discardableResult
    func rollbackAdd(_ candidate: RegisteredServer) -> Bool {
        guard let revision = Self.currentAddRevision(candidate.id) else { return false }
        return rollbackAdd(candidate, expectedRevision: revision)
    }

    /// Remove a server only after its snapshot namespace has been fully purged.
    /// Fails closed: without a wired purge handler, or when the purge reports a
    /// failure, the server is retained so its cached bytes cannot be orphaned.
    func purgeAndRemove(id: UUID) async throws {
        guard let index = servers.firstIndex(where: { $0.id == id }) else {
            throw ServerRegistryError.serverNotFound(id)
        }
        guard let handler = snapshotPurgeHandler else {
            throw ServerRegistryError.purgeUnavailable(id)
        }
        let result = await handler(id)
        guard case .purged = result else {
            throw ServerRegistryError.purgeFailed(id)
        }
        if let certificatePinPurgeHandler {
            let server = servers[index]
            let remaining = servers.filter { $0.id != id }
            guard await certificatePinPurgeHandler(server, remaining) else {
                throw ServerRegistryError.certificatePinPurgeFailed(id)
            }
        }
        try removeEntry(id: id)
    }

    func setActive(id: UUID?) throws {
        if let id, !servers.contains(where: { $0.id == id }) {
            throw ServerRegistryError.serverNotFound(id)
        }
        activeServerID = id
        persist()
    }

    func associateOriginServerId(_ originServerId: UUID, with serverID: UUID) throws {
        guard let index = servers.firstIndex(where: { $0.id == serverID }) else {
            throw ServerRegistryError.serverNotFound(serverID)
        }
        guard servers[index].originServerId != originServerId else { return }
        servers[index].originServerId = originServerId
        servers[index].updatedAt = now()
        persist()
    }

    static func normalizedURLString(for raw: String) throws -> String {
        guard let normalized = APIClient.normalizedServerURLString(raw),
              let url = URL(string: normalized) else {
            throw ServerRegistryError.invalidURL(raw)
        }
        return canonicalURLString(url)
    }

    private static func canonicalURLString(_ url: URL) -> String {
        guard var components = URLComponents(url: url, resolvingAgainstBaseURL: false),
              let scheme = components.scheme,
              let host = components.host else {
            var value = url.absoluteString
            while value.hasSuffix("/") { value.removeLast() }
            return value
        }

        let normalizedScheme = scheme.lowercased()
        components.scheme = normalizedScheme
        components.host = host.lowercased()
        if (normalizedScheme == "https" && components.port == 443)
            || (normalizedScheme == "http" && components.port == 80) {
            components.port = nil
        }
        components.path = stripTrailingSlashes(from: components.path)
        var value = components.url?.absoluteString ?? url.absoluteString
        while value.hasSuffix("/") { value.removeLast() }
        return value
    }

    private static func stripTrailingSlashes(from path: String) -> String {
        guard path.count > 1 else { return path }
        var path = path
        while path.hasSuffix("/") { path.removeLast() }
        return path
    }

    private static func normalizedLegacyURLString(_ raw: String) -> String? {
        guard let normalized = APIClient.normalizedServerURLString(raw),
              var components = URLComponents(string: normalized),
              let scheme = components.scheme,
              let host = components.host else {
            return nil
        }

        guard let canonicalHost = NetworkHostClassifier.canonicalize(host) else {
            return nil
        }
        if scheme.lowercased() == "http",
           canonicalHost.classification == .public || canonicalHost.isIPLiteral {
            components.scheme = "https"
        }
        components.host = canonicalHost.value

        guard let url = components.url else { return nil }
        return canonicalURLString(url)
    }

    private func rejectDuplicate(normalizedURLString: String, ignoring id: UUID? = nil) throws {
        if servers.contains(where: { $0.id != id && $0.normalizedURLString == normalizedURLString }) {
            throw ServerRegistryError.duplicateURL(normalizedURLString)
        }
    }

    private func normalizedDisplayName(_ displayName: String, fallbackURL: URL) -> String {
        let trimmed = displayName.trimmingCharacters(in: .whitespacesAndNewlines)
        if !trimmed.isEmpty { return trimmed }
        return fallbackURL.host ?? "PrintFarmer"
    }

    private func migrateLegacyServerIfNeeded() {
        guard userDefaults.data(forKey: Self.storageKey) == nil,
              !userDefaults.bool(forKey: Self.legacyMigrationCompletedKey),
              let legacyURLString = userDefaults.string(forKey: APIClient.serverURLKey),
              let normalized = Self.normalizedLegacyURLString(legacyURLString),
              let url = URL(string: normalized) else {
            return
        }

        if legacyURLString != normalized {
            userDefaults.set(normalized, forKey: APIClient.serverURLKey)
        }

        let timestamp = now()
        let server = RegisteredServer(
            displayName: url.host ?? "PrintFarmer",
            baseURL: url,
            normalizedURLString: normalized,
            createdAt: timestamp,
            updatedAt: timestamp
        )
        servers = [server]
        activeServerID = server.id
        userDefaults.set(true, forKey: Self.legacyMigrationCompletedKey)
        persist()
    }

    private func sanitizeActiveSelection() {
        guard let activeServerID else { return }
        if !servers.contains(where: { $0.id == activeServerID }) {
            self.activeServerID = servers.first?.id
            persist()
        }
    }

    private func persist() {
        let registry = PersistedRegistry(servers: servers, activeServerID: activeServerID)
        if let data = try? JSONEncoder().encode(registry) {
            userDefaults.set(data, forKey: Self.storageKey)
        }
    }
}
