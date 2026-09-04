import Foundation
import Observation
import OSLog

@MainActor
protocol FarmShapeServiceProtocol: AnyObject, Sendable {
    /// The shape frozen for shell derivation in the current authenticated session.
    var sessionShape: FarmShape? { get }

    /// The newest observed shape, including a response that arrived after startup.
    var latestShape: FarmShape? { get }

    var isSessionResolved: Bool { get }

    func beginSession(authToken: Int)
    func resolveForAuthenticatedSession(serverID: UUID, timeout: Duration) async
    func refreshLatest(serverID: UUID) async
    func resetSession()
}

/// Persists the last known shape independently for each registered server.
final class FarmShapeStore: @unchecked Sendable {
    struct Authority: Sendable, Equatable {
        fileprivate let revision: UInt64
    }

    static let keyPrefix = "pf_farm_shape_"

    private let userDefaults: UserDefaults
    private let lock = NSLock()
    private var purgedServerIDs: Set<UUID> = []
    private var revisions: [UUID: UInt64] = [:]

    init(userDefaults: UserDefaults = .standard) {
        self.userDefaults = userDefaults
    }

    func shapeKey(serverID: UUID) -> String {
        "\(Self.keyPrefix)\(serverID.uuidString)"
    }

    func shape(serverID: UUID) -> FarmShape? {
        lock.lock()
        defer { lock.unlock() }
        guard !purgedServerIDs.contains(serverID) else { return nil }
        return storedShape(serverID: serverID)
    }

    func authority(serverID: UUID) -> Authority {
        lock.lock()
        defer { lock.unlock() }
        return Authority(revision: revisions[serverID] ?? 0)
    }

    func shape(serverID: UUID, authority: Authority) -> FarmShape? {
        lock.lock()
        defer { lock.unlock() }
        guard !purgedServerIDs.contains(serverID),
              authority.revision == (revisions[serverID] ?? 0) else {
            return nil
        }
        return storedShape(serverID: serverID)
    }

    private func storedShape(serverID: UUID) -> FarmShape? {
        guard let data = userDefaults.data(forKey: shapeKey(serverID: serverID)) else {
            return nil
        }
        return try? JSONDecoder().decode(FarmShape.self, from: data)
    }

    func setShape(_ shape: FarmShape, serverID: UUID) {
        guard let data = try? JSONEncoder().encode(shape) else { return }
        lock.lock()
        defer { lock.unlock() }
        guard !purgedServerIDs.contains(serverID) else { return }
        userDefaults.set(data, forKey: shapeKey(serverID: serverID))
    }

    @discardableResult
    func setShape(_ shape: FarmShape, serverID: UUID, authority: Authority) -> Bool {
        guard let data = try? JSONEncoder().encode(shape) else { return false }
        lock.lock()
        defer { lock.unlock() }
        guard !purgedServerIDs.contains(serverID),
              authority.revision == (revisions[serverID] ?? 0) else {
            return false
        }
        userDefaults.set(data, forKey: shapeKey(serverID: serverID))
        return true
    }

    func invalidateShape(serverID: UUID) {
        lock.lock()
        defer { lock.unlock() }
        revisions[serverID] = (revisions[serverID] ?? 0) &+ 1
        userDefaults.removeObject(forKey: shapeKey(serverID: serverID))
    }

    func purgeShape(serverID: UUID) {
        lock.lock()
        defer { lock.unlock() }
        revisions[serverID] = (revisions[serverID] ?? 0) &+ 1
        purgedServerIDs.insert(serverID)
        userDefaults.removeObject(forKey: shapeKey(serverID: serverID))
    }
}

/// Fetches and freezes farm shape for one authenticated app session.
///
/// The session value is resolved once: a response that loses the startup race
/// updates ``latestShape`` and persistence, but never reshuffles the live shell.
@MainActor
@Observable
final class FarmShapeService: FarmShapeServiceProtocol, @unchecked Sendable {
    static let startupTimeout: Duration = .milliseconds(750)
    private static let logger = Logger(subsystem: "com.printfarmer.ios", category: "FarmShape")

    typealias FetchShape = @Sendable () async throws -> FarmShape?
    typealias Sleep = @Sendable (Duration) async throws -> Void

    private(set) var sessionShape: FarmShape?
    private(set) var latestShape: FarmShape?
    private(set) var isSessionResolved = true

    @ObservationIgnored private let store: FarmShapeStore
    @ObservationIgnored private let fetchShape: FetchShape
    @ObservationIgnored private let sleep: Sleep
    @ObservationIgnored private var storeAuthorities: [UUID: FarmShapeStore.Authority]
    @ObservationIgnored private var sessionGeneration: UInt64 = 0
    @ObservationIgnored private var sessionAuthToken: Int?
    @ObservationIgnored private var resolvedSessionServerID: UUID?

    init(
        apiClient: APIClient,
        serverID: UUID?,
        store: FarmShapeStore
    ) {
        self.store = store
        var authorities: [UUID: FarmShapeStore.Authority] = [:]
        if let serverID {
            authorities[serverID] = store.authority(serverID: serverID)
        }
        self.storeAuthorities = authorities
        self.fetchShape = {
            try await apiClient.get(
                "/api/system/farm-shape",
                treating: [401, 404]
            )
        }
        self.sleep = {
            try await Task.sleep(for: $0)
        }
        let persisted = serverID.flatMap { id in
            authorities[id].flatMap {
                store.shape(serverID: id, authority: $0)
            }
        }
        self.sessionShape = persisted
        self.latestShape = persisted
    }

    init(
        serverID: UUID?,
        store: FarmShapeStore,
        fetchShape: @escaping FetchShape,
        sleep: @escaping Sleep = {
            try await Task.sleep(for: $0)
        }
    ) {
        self.store = store
        var authorities: [UUID: FarmShapeStore.Authority] = [:]
        if let serverID {
            authorities[serverID] = store.authority(serverID: serverID)
        }
        self.storeAuthorities = authorities
        self.fetchShape = fetchShape
        self.sleep = sleep
        let persisted = serverID.flatMap { id in
            authorities[id].flatMap {
                store.shape(serverID: id, authority: $0)
            }
        }
        self.sessionShape = persisted
        self.latestShape = persisted
    }

    func beginSession(authToken: Int) {
        guard sessionAuthToken != authToken else { return }
        sessionGeneration &+= 1
        sessionAuthToken = authToken
        resolvedSessionServerID = nil
        sessionShape = nil
        latestShape = nil
        isSessionResolved = true
    }

    func resolveForAuthenticatedSession(
        serverID: UUID,
        timeout: Duration
    ) async {
        guard resolvedSessionServerID != serverID else { return }
        resolvedSessionServerID = serverID
        sessionGeneration &+= 1
        let generation = sessionGeneration
        let authority = storeAuthority(for: serverID)
        let persisted = store.shape(serverID: serverID, authority: authority)
        sessionShape = persisted
        latestShape = persisted
        isSessionResolved = false

        let race = FarmShapeResolutionRace()
        Task { @MainActor [weak self] in
            guard let self else {
                race.resolve()
                return
            }
            let shape = await self.fetchShapeOrUnknown()
            self.completeFetch(
                shape,
                serverID: serverID,
                authority: authority,
                generation: generation
            )
            race.resolve()
        }

        let sleep = self.sleep
        let timeoutTask = Task { @MainActor [weak self] in
            do {
                try await sleep(timeout)
                self?.freezeSession(generation: generation)
                race.resolve()
            } catch {
                // The request completed before the bounded startup wait elapsed.
            }
        }

        await race.wait()
        timeoutTask.cancel()
    }

    func refreshLatest(serverID: UUID) async {
        let generation = sessionGeneration
        let authority = storeAuthority(for: serverID)
        guard let shape = await fetchShapeOrUnknown() else { return }
        guard generation == sessionGeneration else { return }
        guard store.setShape(shape, serverID: serverID, authority: authority) else {
            return
        }
        latestShape = shape
    }

    func resetSession() {
        sessionGeneration &+= 1
        sessionAuthToken = nil
        resolvedSessionServerID = nil
        sessionShape = nil
        latestShape = nil
        isSessionResolved = true
    }

    private func completeFetch(
        _ shape: FarmShape?,
        serverID: UUID,
        authority: FarmShapeStore.Authority,
        generation: UInt64
    ) {
        guard generation == sessionGeneration else { return }
        if let shape {
            guard store.setShape(
                shape,
                serverID: serverID,
                authority: authority
            ) else {
                return
            }
            latestShape = shape
            if !isSessionResolved {
                sessionShape = shape
            }
        }
        isSessionResolved = true
    }

    private func storeAuthority(for serverID: UUID) -> FarmShapeStore.Authority {
        if let authority = storeAuthorities[serverID] {
            return authority
        }
        let authority = store.authority(serverID: serverID)
        storeAuthorities[serverID] = authority
        return authority
    }

    private func freezeSession(generation: UInt64) {
        guard generation == sessionGeneration else { return }
        isSessionResolved = true
    }

    private func fetchShapeOrUnknown() async -> FarmShape? {
        do {
            return try await fetchShape()
        } catch let error as NetworkError {
            switch error {
            case .unauthorized,
                 .notFound,
                 .noConnection,
                 .timeout,
                 .serverUnreachable,
                 .transportError,
                 .staleServerResponse,
                 .insecureTransportBlocked,
                 .certificateChanged,
                 .certificateNotTrusted:
                return nil
            default:
                Self.logger.warning(
                    "Unexpected farm-shape failure: \(error.localizedDescription, privacy: .public)"
                )
                return nil
            }
        } catch is URLError {
            return nil
        } catch {
            Self.logger.warning(
                "Unexpected farm-shape failure: \(error.localizedDescription, privacy: .public)"
            )
            return nil
        }
    }
}

@MainActor
final class StubFarmShapeService: FarmShapeServiceProtocol, @unchecked Sendable {
    private(set) var sessionShape: FarmShape?
    private(set) var latestShape: FarmShape?
    private(set) var isSessionResolved = true

    init(shape: FarmShape? = nil) {
        sessionShape = shape
        latestShape = shape
    }

    func beginSession(authToken: Int) {}

    func resolveForAuthenticatedSession(serverID: UUID, timeout: Duration) async {
        isSessionResolved = true
    }

    func refreshLatest(serverID: UUID) async {}

    func resetSession() {
        sessionShape = nil
        latestShape = nil
        isSessionResolved = true
    }
}

private final class FarmShapeResolutionRace: @unchecked Sendable {
    private let lock = NSLock()
    private var resolved = false
    private var continuation: CheckedContinuation<Void, Never>?

    func resolve() {
        lock.lock()
        guard !resolved else {
            lock.unlock()
            return
        }
        resolved = true
        let waiter = continuation
        continuation = nil
        lock.unlock()
        waiter?.resume()
    }

    func wait() async {
        await withCheckedContinuation { waiter in
            lock.lock()
            if resolved {
                lock.unlock()
                waiter.resume()
            } else {
                continuation = waiter
                lock.unlock()
            }
        }
    }
}
