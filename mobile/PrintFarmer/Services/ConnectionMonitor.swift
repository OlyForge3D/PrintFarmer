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
        self.apiClient = apiClient
        self.signalRService = signalRService
    }

    /// Starts (or restarts) the periodic connectivity poll loop.
    func start() {
        pollTask?.cancel()
        // Reset to a neutral state so a restart (e.g. a server switch) never
        // surfaces the previous server's status while the first probe is in flight.
        resetState()
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
        // Clear the displayed state immediately so a stopped monitor (e.g. during
        // a server switch) never keeps showing the previous server's status while
        // the next connect attempt is still in flight.
        resetState()
    }

    /// Resets the published state to a neutral "connecting" baseline.
    private func resetState() {
        status = .connecting
        signalRState = .disconnected
        isServerReachable = false
        consecutiveReachabilityFailures = 0
    }

    /// Performs a single connectivity sample and updates ``status``.
    func refresh() async {
        let reachable = await apiClient?.isReachable() ?? false
        let signalR = signalRService?.connectionState ?? .disconnected
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
}
