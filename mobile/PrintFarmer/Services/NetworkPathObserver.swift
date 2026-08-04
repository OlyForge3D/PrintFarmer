import Foundation
import Network

// MARK: - Path snapshot

/// A coarse snapshot of the system network path, reduced to the only two facts
/// the connectivity-recovery logic reacts to: whether a usable path exists, and
/// which interface it runs over.
///
/// `NWPath` is deliberately *not* surfaced past this boundary. Keeping the seam
/// to a small `Sendable` value type is what lets the trigger logic be unit
/// tested without a radio, and it means nothing from the `Network` framework has
/// to cross an actor hop.
struct NetworkPathSnapshot: Equatable, Sendable {
    /// Mirrors `NWPath.Status`.
    enum Reachability: Equatable, Sendable {
        case satisfied
        case unsatisfied
        /// A path exists but needs a connection established first (e.g. VPN
        /// on-demand). Treated as "not yet usable" — same as unsatisfied.
        case requiresConnection
    }

    /// The interface the path currently runs over. A change here while still
    /// `.satisfied` is the Wi-Fi↔cellular handoff case: the old sockets are
    /// dead even though the device never looked offline.
    enum Interface: Equatable, Sendable {
        case wifi
        case cellular
        case wiredEthernet
        case other
        case none
    }

    var reachability: Reachability
    var interface: Interface

    static let unsatisfied = NetworkPathSnapshot(reachability: .unsatisfied, interface: .none)
}

// MARK: - Observation seam

/// Seam over `NWPathMonitor` so path-change handling can be unit tested.
///
/// Implementations must deliver snapshots **on the main actor and in order** —
/// the consumer compares each snapshot against the previous one, so out-of-order
/// delivery would corrupt the comparison.
///
/// ``start(onChange:)`` is idempotent: calling it again replaces the handler
/// rather than running a second monitor. ``cancel()`` is safe when already
/// stopped.
@MainActor
protocol NetworkPathObserving: AnyObject {
    func start(onChange: @escaping @Sendable @MainActor (NetworkPathSnapshot) -> Void)
    func cancel()
}

/// Production ``NetworkPathObserving`` backed by Apple's `NWPathMonitor`.
///
/// `NWPathMonitor` reports path transitions the moment the system knows about
/// them, which is seconds ahead of anything the app could infer from a failed
/// request or a poll tick.
@MainActor
final class NWPathMonitorObserver: NetworkPathObserving {
    private var monitor: NWPathMonitor?

    /// Background delivery queue for the monitor. Snapshots are forwarded to the
    /// main queue (not a `Task`) so delivery stays FIFO.
    private let queue = DispatchQueue(label: "com.printfarmer.network-path", qos: .utility)

    func start(onChange: @escaping @Sendable @MainActor (NetworkPathSnapshot) -> Void) {
        cancel()
        let monitor = NWPathMonitor()
        self.monitor = monitor
        monitor.pathUpdateHandler = { path in
            // Reduce to a Sendable value here, on the monitor queue, so no
            // `NWPath` escapes onto the main actor.
            let snapshot = NetworkPathSnapshot(path)
            DispatchQueue.main.async {
                MainActor.assumeIsolated { onChange(snapshot) }
            }
        }
        monitor.start(queue: queue)
    }

    func cancel() {
        monitor?.cancel()
        monitor = nil
    }
}

// MARK: - NWPath bridging

extension NetworkPathSnapshot {
    init(_ path: NWPath) {
        let reachability: Reachability
        switch path.status {
        case .satisfied: reachability = .satisfied
        case .unsatisfied: reachability = .unsatisfied
        case .requiresConnection: reachability = .requiresConnection
        @unknown default: reachability = .unsatisfied
        }

        let interface: Interface
        if path.usesInterfaceType(.wifi) {
            interface = .wifi
        } else if path.usesInterfaceType(.cellular) {
            interface = .cellular
        } else if path.usesInterfaceType(.wiredEthernet) {
            interface = .wiredEthernet
        } else if reachability == .satisfied {
            interface = .other
        } else {
            interface = .none
        }

        self.init(reachability: reachability, interface: interface)
    }
}
