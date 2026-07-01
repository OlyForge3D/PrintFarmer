import Foundation
import Observation

enum ServerRegistryError: LocalizedError, Equatable {
    case invalidURL(String)
    case duplicateURL(String)
    case serverNotFound(UUID)

    var errorDescription: String? {
        switch self {
        case .invalidURL(let value):
            return "Invalid server URL: \(value)"
        case .duplicateURL(let value):
            return "Server already registered: \(value)"
        case .serverNotFound(let id):
            return "Server not found: \(id.uuidString)"
        }
    }
}

@Observable
final class ServerRegistry {
    static let storageKey = "pf_server_registry"

    private struct PersistedRegistry: Codable {
        var servers: [RegisteredServer]
        var activeServerID: UUID?
    }

    var servers: [RegisteredServer]
    var activeServerID: UUID?

    @ObservationIgnored private let userDefaults: UserDefaults
    @ObservationIgnored private let now: () -> Date

    init(
        userDefaults: UserDefaults = .standard,
        now: @escaping () -> Date = Date.init,
        migrateLegacyServerURL: Bool = true
    ) {
        self.userDefaults = userDefaults
        self.now = now

        if let data = userDefaults.data(forKey: Self.storageKey),
           let persisted = try? JSONDecoder().decode(PersistedRegistry.self, from: data) {
            self.servers = persisted.servers
            self.activeServerID = persisted.activeServerID
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
    func add(displayName: String, baseURL: URL) throws -> RegisteredServer {
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
        if activeServerID == nil {
            activeServerID = server.id
        }
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
        updated.displayName = normalizedDisplayName(updated.displayName, fallbackURL: updated.baseURL)
        updated.baseURL = URL(string: normalized)!
        updated.normalizedURLString = normalized
        updated.updatedAt = now()

        servers[index] = updated
        persist()
    }

    func remove(id: UUID) throws {
        guard let index = servers.firstIndex(where: { $0.id == id }) else {
            throw ServerRegistryError.serverNotFound(id)
        }

        servers.remove(at: index)
        if activeServerID == id {
            activeServerID = servers.first?.id
        }
        persist()
    }

    func setActive(id: UUID?) throws {
        if let id, !servers.contains(where: { $0.id == id }) {
            throw ServerRegistryError.serverNotFound(id)
        }
        activeServerID = id
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

        components.scheme = scheme.lowercased()
        components.host = host.lowercased()
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

        if scheme.lowercased() == "http", isIPv4Address(host) {
            components.scheme = "https"
        }

        guard let url = components.url else { return nil }
        return canonicalURLString(url)
    }

    private static func isIPv4Address(_ host: String) -> Bool {
        host.range(
            of: #"^\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}$"#,
            options: .regularExpression
        ) != nil
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
        guard servers.isEmpty,
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
