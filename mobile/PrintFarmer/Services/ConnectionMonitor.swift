import Foundation
import Observation

/// Combined connectivity state surfaced by the global connection indicator.
enum ConnectionStatus: String, Equatable, Sendable {
    /// REST reachable and the real-time hub is connected — live updates flowing.
    case connected
    /// A connection attempt is in progress (initial connect or REST reachable
    /// while the hub is still handshaking).
    case connecting
    /// REST reachable but the real-time hub is down/reconnecting — data loads
    /// still work, but live updates are paused.
    case degraded
    /// The server cannot be reached over REST.
    case offline
}

/// Observes REST reachability (`/healthz`) and the SignalR hub state and
/// publishes a single combined ``ConnectionStatus`` for the global connection
/// indicator. Polls on a fixed interval while ``start()`` is active.
@MainActor
@Observable
final class ConnectionMonitor {
    private(set) var status: ConnectionStatus = .connecting
    private(set) var signalRState: SignalRConnectionState = .disconnected
    private(set) var isServerReachable = false
    /// Number of reachability probes that have failed back-to-back. Reset to 0
    /// by the first success. Exposed for tests and diagnostics.
    private(set) var consecutiveReachabilityFailures = 0

    @ObservationIgnored private var apiClient: APIClient?
    @ObservationIgnored private var signalRService: (any SignalRServiceProtocol)?
    @ObservationIgnored private var pollTask: Task<Void, Never>?

    /// Observer supplied by tests. `nil` in production, where ``start()``
    /// creates a real ``NWPathMonitorObserver`` instead.
    @ObservationIgnored private let injectedPathObserver: (any NetworkPathObserving)?
    /// The live observer, owned for the lifetime of a ``start()``/``stop()``
    /// cycle. Exactly one exists at a time — ``startPathObserver()`` cancels any
    /// predecessor — so a server switch cannot leak monitors.
    @ObservationIgnored private var pathObserver: (any NetworkPathObserving)?
    /// Last snapshot seen, for change detection. Cleared on stop so a restart
    /// does not compare against the previous session's path.
    @ObservationIgnored private var lastPathSnapshot: NetworkPathSnapshot?
    /// The in-flight recovery. Cancel-and-replace is what gives ``requestResume(after:)``
    /// its debounce.
    @ObservationIgnored private var resumeTask: Task<Void, Never>?

    /// Monotonic ticket issued to every in-flight ``refresh()``.
    ///
    /// `refresh()` suspends on the network probe, so the foreground-resume
    /// refresh and the poll loop's refresh can overlap. Without a fence a
    /// slower, older probe (whose `isReachable()` may have merely been
    /// cancelled, which the API client reports as `false`) can land after —
    /// or ahead of — a newer healthy sample and paint the banner red. Only
    /// the newest *issued* sample is allowed to publish.
    @ObservationIgnored private var sampleTicket: UInt64 = 0
    /// Ticket of the most recently *published* sample.
    @ObservationIgnored private var appliedTicket: UInt64 = 0

    /// Interval between connectivity samples.
    @ObservationIgnored var pollInterval: Duration = .seconds(5)

    /// Consecutive failed reachability probes required before the monitor
    /// publishes the alarming `.offline` state.
    ///
    /// `APIClient.isReachable()` swallows every transport error and returns
    /// `false`, so a single dropped packet, a Wi-Fi power-save wake, a DHCP
    /// renewal, or an AP roam used to flip the global banner straight to red
    /// while the user was sitting still. Requiring two back-to-back failures
    /// (~one poll interval of genuine unreachability) filters those blips out
    /// without meaningfully delaying a real outage.
    @ObservationIgnored var offlineFailureThreshold = 2

    /// Quiet period a network-path change must survive before it triggers
    /// recovery.
    ///
    /// `NWPathMonitor` emits several events for a single Wi-Fi↔cellular handoff.
    /// Each trigger cancels the pending resume and starts a new one, so a burst
    /// collapses into a single probe instead of hammering `ensureConnected()`.
    @ObservationIgnored var pathChangeDebounce: Duration = .milliseconds(400)

    /// - Parameter pathObserver: Injected in tests. Production passes `nil`,
    ///   which makes ``start()`` create a real ``NWPathMonitorObserver``.
    init(pathObserver: (any NetworkPathObserving)? = nil) {
        self.injectedPathObserver = pathObserver
    }

    /// Pure state-resolution used by the poll loop (and unit tests).
    /// - Offline when the server is unreachable over REST.
    /// - Otherwise mirrors the hub: connected → connected, connecting →
    ///   connecting, reconnecting/disconnected → degraded.
    static func resolve(isServerReachable: Bool, signalR: SignalRConnectionState) -> ConnectionStatus {
        guard isServerReachable else { return .offline }
        switch signalR {
        case .connected: return .connected
        case .connecting: return .connecting
        case .reconnecting, .disconnected: return .degraded
        }
    }

    /// Hysteresis-aware resolution.
    ///
    /// A failed probe only produces `.offline` once `consecutiveFailures` has
    /// reached `threshold`. Below the threshold the failure is surfaced as
    /// `.degraded` — honest (we have not confirmed the server is live) without
    /// throwing up the full red offline banner for a one-sample blip.
    static func resolve(
        isServerReachable: Bool,
        signalR: SignalRConnectionState,
        consecutiveFailures: Int,
        threshold: Int
    ) -> ConnectionStatus {
        if !isServerReachable && consecutiveFailures < max(threshold, 1) {
            return .degraded
        }
        return resolve(isServerReachable: isServerReachable, signalR: signalR)
    }

    /// Points the monitor at the currently-active services. Safe to call again
    /// after a server switch to rebind to the new client/hub.
    func configure(apiClient: APIClient?, signalRService: any SignalRServiceProtocol) {
        // Invalidate any probe still in flight against the previous client.
        sampleTicket &+= 1
        appliedTicket = sampleTicket
        self.apiClient = apiClient
        self.signalRService = signalRService
    }

    /// Starts (or restarts) the periodic connectivity poll loop and the network
    /// path observer.
    func start() {
        pollTask?.cancel()
        // Reset to a neutral state so a restart (e.g. a server switch) never
        // surfaces the previous server's status while the first probe is in flight.
        resetState()
        startPathObserver()
        pollTask = Task { [weak self] in
            while !Task.isCancelled {
                guard let self else { break }
                await self.refresh()
                let interval = self.pollInterval
                try? await Task.sleep(for: interval)
            }
        }
    }

    /// Stops the poll loop. Call on logout or when the view disappears.
    func stop() {
        pollTask?.cancel()
        pollTask = nil
        resumeTask?.cancel()
        resumeTask = nil
        pathObserver?.cancel()
        pathObserver = nil
        lastPathSnapshot = nil
        // Clear the displayed state immediately so a stopped monitor (e.g. during
        // a server switch) never keeps showing the previous server's status while
        // the next connect attempt is still in flight.
        resetState()
    }

    /// Resets the published state to a neutral "connecting" baseline and
    /// invalidates every in-flight sample so a probe issued against the
    /// previous server/epoch cannot publish after the reset.
    private func resetState() {
        sampleTicket &+= 1
        appliedTicket = sampleTicket
        status = .connecting
        signalRState = .disconnected
        isServerReachable = false
        consecutiveReachabilityFailures = 0
    }

    /// Performs a single connectivity sample and updates ``status``.
    ///
    /// Safe to call concurrently with the poll loop (the app-foreground hook
    /// does exactly that): stale samples are discarded by the ticket fence.
    func refresh() async {
        sampleTicket &+= 1
        let ticket = sampleTicket
        let reachable = await apiClient?.isReachable() ?? false
        let signalR = signalRService?.connectionState ?? .disconnected
        // Only the newest *issued* sample may publish. Comparing against
        // `appliedTicket` alone is insufficient: if an older probe finishes
        // while a newer one is still in flight it would publish first and
        // transiently restore the wrong banner before the newer sample
        // corrects it.
        guard ticket == sampleTicket, ticket > appliedTicket else { return }
        appliedTicket = ticket
        if reachable {
            consecutiveReachabilityFailures = 0
        } else {
            consecutiveReachabilityFailures += 1
        }
        isServerReachable = reachable
        signalRState = signalR
        status = Self.resolve(
            isServerReachable: reachable,
            signalR: signalR,
            consecutiveFailures: consecutiveReachabilityFailures,
            threshold: offlineFailureThreshold
        )
    }

    // MARK: - Network path observation

    /// Decides whether a path transition warrants re-arming connectivity.
    ///
    /// Pure, so the policy is testable without a radio. Three rules:
    ///
    /// 1. A path that is not `.satisfied` never triggers anything. A path change
    ///    is only ever a *hint to probe* — the failure-threshold hysteresis in
    ///    ``refresh()`` remains the sole authority on publishing `.offline`, so
    ///    a momentary flap cannot paint the red banner.
    /// 2. The very first snapshot is ignored. `NWPathMonitor` delivers the
    ///    current path immediately on start, and ``start()`` already probes.
    /// 3. Otherwise any *difference* triggers — which covers both regaining a
    ///    path and an interface change (Wi-Fi↔cellular handoff, where the device
    ///    never looked offline but every existing socket is dead) — while
    ///    identical repeat events, which the handler emits freely, are dropped.
    static func shouldTriggerRecovery(
        previous: NetworkPathSnapshot?,
        current: NetworkPathSnapshot
    ) -> Bool {
        guard current.reachability == .satisfied else { return false }
        guard let previous else { return false }
        return previous != current
    }

    /// Handles one snapshot from the path observer. Internal rather than private
    /// so tests can drive it directly.
    func handlePathChange(_ snapshot: NetworkPathSnapshot) {
        let previous = lastPathSnapshot
        lastPathSnapshot = snapshot
        guard Self.shouldTriggerRecovery(previous: previous, current: snapshot) else { return }
        requestResume(after: pathChangeDebounce)
    }

    private func startPathObserver() {
        pathObserver?.cancel()
        lastPathSnapshot = nil
        let observer = injectedPathObserver ?? NWPathMonitorObserver()
        pathObserver = observer
        observer.start { [weak self] snapshot in
            self?.handlePathChange(snapshot)
        }
    }

    // MARK: - Recovery

    /// Schedules the recovery sequence, cancelling any resume already pending.
    ///
    /// - Parameter delay: Debounce window. Foreground resumes pass `.zero`
    ///   because they are a single discrete event; path changes pass
    ///   ``pathChangeDebounce`` because they arrive in bursts.
    func requestResume(after delay: Duration = .zero) {
        resumeTask?.cancel()
        resumeTask = Task { [weak self] in
            if delay > .zero {
                try? await Task.sleep(for: delay)
            }
            // Checked after the sleep *and* when there was no sleep at all: a
            // superseded task still runs its body, so without this a burst of
            // path events would fan out into concurrent recoveries.
            guard !Task.isCancelled else { return }
            await self?.resumeConnectivity()
        }
    }

    /// Awaits the pending resume, if any. Test seam — production never needs to
    /// join this task.
    func awaitPendingResume() async {
        guard let resumeTask else { return }
        await resumeTask.value
    }

    /// The shared "re-arm connectivity now" sequence, used by both the
    /// app-foreground hook and the network-path observer.
    ///
    /// Probes REST first: it is a single fast request and it clears a stale
    /// offline banner without waiting on the hub handshake. Then re-arms the hub
    /// rather than sitting out the remainder of a backoff sleep, and re-samples
    /// so the bar reflects the hub result immediately instead of on the next
    /// poll tick. Every step is idempotent.
    func resumeConnectivity() async {
        await refresh()
        guard !Task.isCancelled else { return }
        await signalRService?.ensureConnected()
        guard !Task.isCancelled else { return }
        await refresh()
    }
}

enum BackendServiceEndpoint: String, CaseIterable, Hashable, Sendable {
    case api
    case systemCapabilities
    case signalR
    case printers
    case jobs
    case locations
    case statistics
    case notifications
    case spoolInventory
    case maintenance
    case attention
    case filamentCoverage
    case shiftTasks
    case partsInventory
    case autoDispatch
    case jobAnalytics
    case predictiveAnalytics
    case dispatch
    case failureDetection

    var displayName: String {
        switch self {
        case .api: "PrintFarmer API"
        case .systemCapabilities: "System capabilities"
        case .signalR: "Live updates"
        case .printers: "Printers"
        case .jobs: "Jobs"
        case .locations: "Locations"
        case .statistics: "Statistics"
        case .notifications: "Notifications"
        case .spoolInventory: "Spool inventory"
        case .maintenance: "Maintenance"
        case .attention: "Attention"
        case .filamentCoverage: "Filament coverage"
        case .shiftTasks: "Shift tasks"
        case .partsInventory: "Parts inventory"
        case .autoDispatch: "Auto dispatch"
        case .jobAnalytics: "Job analytics"
        case .predictiveAnalytics: "Predictive analytics"
        case .dispatch: "Dispatch"
        case .failureDetection: "Failure detection"
        }
    }

    var sortOrder: Int {
        Self.allCases.firstIndex(of: self) ?? Self.allCases.count
    }
}

struct BackendServiceFailure: Identifiable, Equatable, Sendable {
    let endpoint: BackendServiceEndpoint

    var id: BackendServiceEndpoint { endpoint }
    var displayName: String { endpoint.displayName }
}

struct BackendReadinessResult: Equatable, Sendable {
    let failures: [BackendServiceFailure]
    let wasCancelled: Bool

    static let cancelled = BackendReadinessResult(failures: [], wasCancelled: true)
}

struct BackendReadinessProbe: Sendable {
    let endpoint: BackendServiceEndpoint
    let isEnabled: @Sendable (ResolvedSystemCapabilities) -> Bool
    let treatsUnsupportedAsAvailable: Bool
    let operation: @Sendable () async throws -> Void

    init(
        endpoint: BackendServiceEndpoint,
        isEnabled: @escaping @Sendable (ResolvedSystemCapabilities) -> Bool = { _ in true },
        treatsUnsupportedAsAvailable: Bool = false,
        operation: @escaping @Sendable () async throws -> Void
    ) {
        self.endpoint = endpoint
        self.isEnabled = isEnabled
        self.treatsUnsupportedAsAvailable = treatsUnsupportedAsAvailable
        self.operation = operation
    }
}

struct BackendReadinessPlan: Sendable {
    let capabilitiesService: any SystemCapabilitiesServiceProtocol
    let probes: [BackendReadinessProbe]

    init(
        capabilitiesService: any SystemCapabilitiesServiceProtocol,
        probes: [BackendReadinessProbe]
    ) {
        self.capabilitiesService = capabilitiesService
        self.probes = probes
    }

    @MainActor
    init(services: ServiceContainer) {
        let apiClient = services.apiClient
        let signalRService = services.signalRService
        let printerService = services.printerService
        let jobService = services.jobService
        let locationService = services.locationService
        let statisticsService = services.statisticsService
        let notificationService = services.notificationService
        let spoolService = services.spoolService
        let maintenanceService = services.maintenanceService
        let attentionService = services.attentionService
        let filamentCoverageService = services.filamentCoverageService
        let shiftTaskService = services.shiftTaskService
        let partsInventoryService = services.partsInventoryService
        let autoPrintService = services.autoPrintService
        let jobAnalyticsService = services.jobAnalyticsService
        let predictiveService = services.predictiveService
        let dispatchService = services.dispatchService
        let failureDetectionService = services.failureDetectionService

        self.capabilitiesService = services.capabilitiesService
        self.probes = [
            BackendReadinessProbe(endpoint: .api) {
                guard let apiClient, await apiClient.isReachable() else {
                    throw BackendReadinessProbeError.unavailable
                }
            },
            BackendReadinessProbe(endpoint: .signalR) {
                try await signalRService.connectForReadiness()
            },
            BackendReadinessProbe(endpoint: .printers) {
                _ = try await printerService.list(includeDisabled: false)
            },
            BackendReadinessProbe(endpoint: .jobs) {
                _ = try await jobService.list()
            },
            BackendReadinessProbe(endpoint: .locations) {
                _ = try await locationService.list()
            },
            BackendReadinessProbe(endpoint: .statistics) {
                _ = try await statisticsService.getSummary()
            },
            BackendReadinessProbe(endpoint: .notifications) {
                _ = try await notificationService.getUnreadCount()
            },
            BackendReadinessProbe(endpoint: .spoolInventory) {
                _ = try await spoolService.listSpools(limit: 1, offset: 0)
            },
            BackendReadinessProbe(endpoint: .maintenance) {
                _ = try await maintenanceService.getAlerts()
            },
            BackendReadinessProbe(
                endpoint: .attention,
                isEnabled: { $0.attentionEnabled },
                treatsUnsupportedAsAvailable: true
            ) {
                _ = try await attentionService.getFeed(cursor: nil, limit: 1)
            },
            BackendReadinessProbe(
                endpoint: .filamentCoverage,
                isEnabled: { $0.filamentCoverageEnabled },
                treatsUnsupportedAsAvailable: true
            ) {
                _ = try await filamentCoverageService.getForFleet()
            },
            BackendReadinessProbe(
                endpoint: .shiftTasks,
                isEnabled: { $0.shiftPlanEnabled },
                treatsUnsupportedAsAvailable: true
            ) {
                _ = try await shiftTaskService.loadSnapshot(shiftPlanEnabled: true)
            },
            BackendReadinessProbe(
                endpoint: .partsInventory,
                isEnabled: { $0.printedPartsInventoryEnabled },
                treatsUnsupportedAsAvailable: true
            ) {
                _ = try await partsInventoryService.listParts(includeInactive: false)
            },
            BackendReadinessProbe(
                endpoint: .autoDispatch,
                treatsUnsupportedAsAvailable: true
            ) {
                _ = try await autoPrintService.getAllStatus()
            },
            BackendReadinessProbe(
                endpoint: .jobAnalytics,
                treatsUnsupportedAsAvailable: true
            ) {
                _ = try await jobAnalyticsService.getStats()
            },
            BackendReadinessProbe(
                endpoint: .predictiveAnalytics,
                treatsUnsupportedAsAvailable: true
            ) {
                _ = try await predictiveService.getActiveAlerts(printerId: nil)
            },
            BackendReadinessProbe(
                endpoint: .dispatch,
                treatsUnsupportedAsAvailable: true
            ) {
                _ = try await dispatchService.getQueueStatus()
            },
            BackendReadinessProbe(
                endpoint: .failureDetection,
                treatsUnsupportedAsAvailable: true
            ) {
                _ = try await failureDetectionService.getStatus()
            },
        ]
    }
}

private enum BackendReadinessProbeError: Error {
    case unavailable
}

private enum BackendProbeExecutionResult: Sendable {
    case succeeded
    case unsupported
    case failed
    case cancelled
}

private enum BackendTimedResult<Value: Sendable>: Sendable {
    case completed(Value)
    case timedOut
    case cancelled
}

private final class BackendReadinessRace<Value: Sendable>: @unchecked Sendable {
    private let lock = NSLock()
    private var result: Value?
    private var continuation: CheckedContinuation<Value, Never>?

    func resolve(_ value: Value) {
        lock.lock()
        guard result == nil else {
            lock.unlock()
            return
        }
        result = value
        let waiter = continuation
        continuation = nil
        lock.unlock()
        waiter?.resume(returning: value)
    }

    func value() async -> Value {
        await withCheckedContinuation { waiter in
            lock.lock()
            if let result {
                lock.unlock()
                waiter.resume(returning: result)
            } else {
                continuation = waiter
                lock.unlock()
            }
        }
    }
}

struct BackendReadinessChecker: Sendable {
    let timeout: Duration
    let capabilitiesTimeout: Duration
    private let probeTimeoutSleep: @Sendable (Duration) async throws -> Void

    init(
        timeout: Duration = .seconds(10),
        capabilitiesTimeout: Duration = .seconds(5),
        probeTimeoutSleep: @escaping @Sendable (Duration) async throws -> Void = {
            try await Task.sleep(for: $0)
        }
    ) {
        self.timeout = timeout
        self.capabilitiesTimeout = capabilitiesTimeout
        self.probeTimeoutSleep = probeTimeoutSleep
    }

    @MainActor
    func check(plan: BackendReadinessPlan) async -> BackendReadinessResult {
        if let apiProbe = plan.probes.first(where: { $0.endpoint == .api }) {
            let apiResult = await run(
                probe: apiProbe,
                timeout: min(timeout, .seconds(6))
            )
            switch apiResult {
            case .completed(.succeeded), .completed(.unsupported):
                break
            case .completed(.failed), .timedOut:
                return BackendReadinessResult(
                    failures: [BackendServiceFailure(endpoint: .api)],
                    wasCancelled: false
                )
            case .completed(.cancelled), .cancelled:
                return .cancelled
            }
        }

        let capabilitiesResult = await refreshCapabilities(
            plan.capabilitiesService,
            timeout: capabilitiesTimeout
        )
        guard !Task.isCancelled else { return .cancelled }

        var failures: [BackendServiceFailure] = []
        switch capabilitiesResult {
        case .completed(.loaded), .completed(.legacyDefaults):
            break
        case .completed(.failed), .timedOut:
            failures.append(BackendServiceFailure(endpoint: .systemCapabilities))
        case .cancelled:
            return .cancelled
        }

        let capabilities = plan.capabilitiesService.resolved
        let enabledProbes = plan.probes.filter {
            $0.endpoint != .api && $0.isEnabled(capabilities)
        }
        let probeFailures = await withTaskGroup(of: BackendServiceFailure?.self) { group in
            for probe in enabledProbes {
                group.addTask {
                    let result = await run(probe: probe, timeout: timeout)
                    switch result {
                    case .completed(.succeeded), .completed(.unsupported):
                        return nil
                    case .completed(.failed), .timedOut:
                        return BackendServiceFailure(endpoint: probe.endpoint)
                    case .completed(.cancelled), .cancelled:
                        return nil
                    }
                }
            }

            var collected: [BackendServiceFailure] = []
            for await failure in group {
                if let failure {
                    collected.append(failure)
                }
            }
            return collected
        }

        guard !Task.isCancelled else { return .cancelled }
        failures.append(contentsOf: probeFailures)
        failures.sort { $0.endpoint.sortOrder < $1.endpoint.sortOrder }
        return BackendReadinessResult(failures: failures, wasCancelled: false)
    }

    private func refreshCapabilities(
        _ service: any SystemCapabilitiesServiceProtocol,
        timeout: Duration
    ) async -> BackendTimedResult<SystemCapabilitiesRefreshOutcome> {
        await runWithTimeout(timeout: timeout) {
            await service.refresh()
        }
    }

    private func run(
        probe: BackendReadinessProbe,
        timeout: Duration
    ) async -> BackendTimedResult<BackendProbeExecutionResult> {
        await runWithTimeout(
            timeout: timeout,
            timeoutSleep: probeTimeoutSleep
        ) {
            do {
                try Task.checkCancellation()
                try await probe.operation()
                try Task.checkCancellation()
                return .succeeded
            } catch is CancellationError {
                return .cancelled
            } catch {
                if probe.treatsUnsupportedAsAvailable,
                   Self.isUnsupported(error) {
                    return .unsupported
                }
                return .failed
            }
        }
    }

    private func runWithTimeout<Value: Sendable>(
        timeout: Duration,
        timeoutSleep: @escaping @Sendable (Duration) async throws -> Void = {
            try await Task.sleep(for: $0)
        },
        operation: @escaping @Sendable () async -> Value
    ) async -> BackendTimedResult<Value> {
        let race = BackendReadinessRace<BackendTimedResult<Value>>()
        let operationTask = Task {
            race.resolve(.completed(await operation()))
        }
        let timeoutTask = Task {
            do {
                try await timeoutSleep(timeout)
                race.resolve(.timedOut)
            } catch {
                // The operation completed or the caller cancelled the race.
            }
        }

        let result = await withTaskCancellationHandler {
            await race.value()
        } onCancel: {
            race.resolve(.cancelled)
        }

        timeoutTask.cancel()
        operationTask.cancel()
        return result
    }

    private static func isUnsupported(_ error: Error) -> Bool {
        guard let networkError = error as? NetworkError else { return false }
        switch networkError {
        case .notFound, .featureDisabled, .methodNotAllowed:
            return true
        default:
            return false
        }
    }
}

enum BackendConnectionGateState: Equatable, Sendable {
    case idle
    case checking
    case ready
    case failed([BackendServiceFailure])
    case proceedingOffline
}

@MainActor
@Observable
final class BackendConnectionGate {
    private(set) var state: BackendConnectionGateState = .idle
    private(set) var retryRevision: UInt64 = 0
    @ObservationIgnored private let checker: BackendReadinessChecker
    @ObservationIgnored private var attemptID: UInt64 = 0
    @ObservationIgnored private var activeGeneration: Int?

    init(timeout: Duration = .seconds(10)) {
        checker = BackendReadinessChecker(timeout: timeout)
    }

    var allowsMainContent: Bool {
        state == .ready || state == .proceedingOffline
    }

    var isChecking: Bool {
        state == .idle || state == .checking
    }

    var failures: [BackendServiceFailure]? {
        guard case .failed(let failures) = state else { return nil }
        return failures
    }

    var failureMessage: String? {
        guard let failures, !failures.isEmpty else { return nil }
        let names = failures.map(\.displayName).joined(separator: ", ")
        return "Unavailable: \(names). You can continue with cached data and any services that are available."
    }

    func check(
        plan: BackendReadinessPlan,
        generation: Int,
        isCurrent: @escaping @MainActor @Sendable () -> Bool
    ) async {
        attemptID &+= 1
        let attempt = attemptID
        activeGeneration = generation
        state = .checking
        let result = await checker.check(plan: plan)
        guard attemptID == attempt,
              activeGeneration == generation else {
            return
        }
        guard !result.wasCancelled,
              !Task.isCancelled,
              isCurrent() else {
            state = .idle
            return
        }

        if result.failures.isEmpty {
            state = .ready
        } else {
            state = .failed(result.failures)
        }
    }

    func continueOffline() {
        guard case .failed = state else { return }
        state = .proceedingOffline
    }

    func retry() {
        attemptID &+= 1
        activeGeneration = nil
        retryRevision &+= 1
        state = .idle
    }

    func reset() {
        attemptID &+= 1
        activeGeneration = nil
        state = .idle
    }
}
