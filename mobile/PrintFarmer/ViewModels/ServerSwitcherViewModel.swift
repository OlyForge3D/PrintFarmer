import Foundation

struct ServerSwitcherMenuItem: Identifiable, Equatable {
    let id: UUID
    let displayName: String
    let normalizedURLString: String
    let isActive: Bool

    var accessibilityLabel: String {
        isActive ? "\(displayName), active server" : "\(displayName), switch server"
    }
}

@MainActor
struct ServerSwitcherViewModel {
    let servers: [RegisteredServer]
    let activeServerID: UUID?

    var isVisible: Bool {
        servers.count > 1
    }

    var activeServerName: String {
        activeServer?.displayName ?? "Select Server"
    }

    var switcherAccessibilityLabel: String {
        "Current server: \(activeServerName). Opens server menu."
    }

    var items: [ServerSwitcherMenuItem] {
        let duplicateDisplayNames = Dictionary(grouping: servers, by: \.displayName)
            .filter { $0.value.count > 1 }
            .keys

        return servers.map { server in
            ServerSwitcherMenuItem(
                id: server.id,
                displayName: duplicateDisplayNames.contains(server.displayName)
                    ? disambiguatedDisplayName(for: server)
                    : server.displayName,
                normalizedURLString: server.normalizedURLString,
                isActive: server.id == activeServerID
            )
        }
    }

    func activate(_ id: UUID, registry: ServerRegistry) throws {
        guard id != activeServerID else { return }
        try registry.setActive(id: id)
    }

    private var activeServer: RegisteredServer? {
        guard let activeServerID else { return nil }
        return servers.first { $0.id == activeServerID }
    }

    private func disambiguatedDisplayName(for server: RegisteredServer) -> String {
        "\(server.displayName) (\(server.baseURL.host ?? server.normalizedURLString))"
    }
}
