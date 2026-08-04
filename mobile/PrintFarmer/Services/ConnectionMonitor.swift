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
