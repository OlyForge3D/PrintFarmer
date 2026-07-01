import Foundation
import Observation

struct ServerHealthCheckResult: Equatable, Sendable {
    let isReachable: Bool
    let statusCode: Int?
    let message: String
}

protocol ServerHealthChecking: Sendable {
    func check(baseURL: URL) async -> ServerHealthCheckResult
}

struct URLSessionServerHealthChecker: ServerHealthChecking {
    private let session: URLSession

    init(session: URLSession = APIClient.makePrivateNetworkSession()) {
        self.session = session
    }

    func check(baseURL: URL) async -> ServerHealthCheckResult {
        let endpoints = ["health", "healthz"]
        var lastNetworkError: URLError?

        for endpoint in endpoints {
            let url = baseURL.appending(path: endpoint)
            var request = URLRequest(url: url)
            request.httpMethod = "GET"
            request.timeoutInterval = 5

            do {
                let (_, response) = try await session.data(for: request)
                let statusCode = (response as? HTTPURLResponse)?.statusCode
                return ServerHealthCheckResult(
                    isReachable: true,
                    statusCode: statusCode,
                    message: statusCode.map { "Reachable (HTTP \($0))" } ?? "Reachable"
                )
            } catch let error as URLError {
                lastNetworkError = error
            } catch {
                return ServerHealthCheckResult(
                    isReachable: true,
                    statusCode: nil,
                    message: "Reachable"
                )
            }
        }

        return ServerHealthCheckResult(
            isReachable: false,
            statusCode: nil,
            message: lastNetworkError?.localizedDescription ?? "Server is unreachable"
        )
    }
}

@MainActor
@Observable
final class ServerManagementViewModel {
    enum HealthState: Equatable {
        case notChecked
        case checking
        case reachable(String)
        case unreachable(String)
    }

    enum Mode: Equatable {
        case add
        case edit(UUID)
    }

    var displayName = ""
    var serverURL = ""
    var healthState: HealthState = .notChecked
    var errorMessage: String?
    var mode: Mode = .add

    @ObservationIgnored private let registry: ServerRegistry
    @ObservationIgnored private let healthChecker: any ServerHealthChecking
    @ObservationIgnored private let now: () -> Date

    init(
        registry: ServerRegistry,
        healthChecker: any ServerHealthChecking = URLSessionServerHealthChecker(),
        now: @escaping () -> Date = Date.init
    ) {
        self.registry = registry
        self.healthChecker = healthChecker
        self.now = now
    }

    var servers: [RegisteredServer] { registry.servers }
    var activeServerID: UUID? { registry.activeServerID }
    var hasServers: Bool { !registry.servers.isEmpty }

    var normalizedURLString: String? {
        try? ServerRegistry.normalizedURLString(for: serverURL)
    }

    var urlValidationError: String? {
        let trimmed = serverURL.trimmingCharacters(in: .whitespacesAndNewlines)
        if trimmed.isEmpty { return "Server URL is required" }
        if normalizedURLString == nil { return "Enter a valid URL (e.g. https://print.example.com)" }
        return nil
    }

    var duplicateValidationError: String? {
        guard let normalizedURLString else { return nil }
        let ignoredID: UUID? = if case .edit(let id) = mode { id } else { nil }
        if registry.servers.contains(where: { $0.id != ignoredID && $0.normalizedURLString == normalizedURLString }) {
            return "This server is already registered."
        }
        return nil
    }

    var formValidationError: String? {
        urlValidationError ?? duplicateValidationError
    }

    var canSave: Bool {
        formValidationError == nil && healthState != .checking
    }

    func prepareForAdd() {
        mode = .add
        displayName = ""
        serverURL = ""
        healthState = .notChecked
        errorMessage = nil
    }

    func prepareForEdit(_ server: RegisteredServer) {
        mode = .edit(server.id)
        displayName = server.displayName
        serverURL = server.normalizedURLString
        errorMessage = nil
        if let status = server.lastKnownStatus {
            healthState = status == "Reachable" ? .reachable(lastCheckedText(for: server)) : .unreachable(lastCheckedText(for: server))
        } else {
            healthState = .notChecked
        }
    }

    func switchToServer(_ server: RegisteredServer) {
        do {
            try registry.setActive(id: server.id)
            errorMessage = nil
        } catch {
            errorMessage = error.localizedDescription
        }
    }

    func delete(_ server: RegisteredServer) {
        do {
            try registry.remove(id: server.id)
            errorMessage = nil
        } catch {
            errorMessage = error.localizedDescription
        }
    }

    @discardableResult
    func checkHealth() async -> ServerHealthCheckResult? {
        guard formValidationError == nil, let normalizedURLString, let url = URL(string: normalizedURLString) else {
            healthState = .notChecked
            errorMessage = formValidationError
            return nil
        }

        healthState = .checking
        let result = await healthChecker.check(baseURL: url)
        healthState = result.isReachable ? .reachable(result.message) : .unreachable(result.message)
        errorMessage = nil
        return result
    }

    @discardableResult
    func save() async -> Bool {
        guard formValidationError == nil, let normalizedURLString, let url = URL(string: normalizedURLString) else {
            errorMessage = formValidationError
            return false
        }

        let healthResult = await checkHealth()
        let healthStatus = healthResult?.isReachable == true ? "Reachable" : "Unreachable"
        let checkedAt = now()

        do {
            switch mode {
            case .add:
                var server = try registry.add(displayName: displayName, baseURL: url)
                server.lastKnownStatus = healthStatus
                server.lastCheckedAt = checkedAt
                try registry.update(server)
            case .edit(let id):
                guard var server = registry.servers.first(where: { $0.id == id }) else {
                    throw ServerRegistryError.serverNotFound(id)
                }
                server.displayName = displayName
                server.baseURL = url
                server.lastKnownStatus = healthStatus
                server.lastCheckedAt = checkedAt
                try registry.update(server)
            }
            errorMessage = nil
            return true
        } catch {
            errorMessage = error.localizedDescription
            return false
        }
    }

    func lastCheckedText(for server: RegisteredServer) -> String {
        guard let date = server.lastCheckedAt else { return server.lastKnownStatus ?? "Not checked" }
        return "\(server.lastKnownStatus ?? "Checked") • \(date.formatted(date: .abbreviated, time: .shortened))"
    }
}
