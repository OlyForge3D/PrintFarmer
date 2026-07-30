import Foundation

// MARK: - SignalRSubscription

/// Cancellation token returned by every SignalR `on*` subscription. Calling
/// `cancel()` removes the associated handler from its hub; dropping the last
/// strong reference (deinit) also cancels, so a view-model that stops
/// retaining its subscription array cannot leak handlers into the hub.
///
/// `cancel()` is idempotent and thread-safe. It is safe to call `cancel()`
/// from within a callback the subscription itself is delivering, because the
/// underlying hub's cancel path uses queue reentrancy detection (see
/// `SignalRHubQueue`) — an on-queue cancel executes inline (no `sync`
/// deadlock) and an off-queue cancel enqueues onto the hub's serial queue.
///
/// IMPORTANT: every token-returning `on*` registration MUST retain the
/// returned `SignalRSubscription` for the intended observation lifetime.
/// Discarding the token drops the last strong reference immediately, and
/// this class's `deinit` cancels the subscription — silently disabling
/// delivery to that callback. Callsites either store the subscription in a
/// `[SignalRSubscription]` array on the owner (view model / service /
/// service host) and cancel-then-register on reconfiguration, or explicitly
/// document intentional immediate cancellation.
final class SignalRSubscription: @unchecked Sendable {
    private let onCancel: @Sendable () -> Void
    private var cancelled = false
    private let cancelLock = NSLock()

    init(_ onCancel: @escaping @Sendable () -> Void) {
        self.onCancel = onCancel
    }

    func cancel() {
        cancelLock.lock()
        if cancelled {
            cancelLock.unlock()
            return
        }
        cancelled = true
        cancelLock.unlock()
        onCancel()
    }

    deinit {
        cancel()
    }
}

// MARK: - SignalRHubCoordinator

/// Per-service coordinator that owns the serial-executor + service-wide FIFO
/// delivery guard shared by every hub belonging to one `SignalRServiceProtocol`
/// instance. Solves round-four blocker #3: a static/class-level shared queue
/// with per-hub FIFO buffers permits an A→B→A interleave sequence (hub A
/// starts delivering event a1; a1's callback triggers hub B; B's callback
/// triggers hub A's a2; a2 executes AFTER a1's callback finishes but BEFORE
/// a1's outer batch "returns", producing recursive interleaving between
/// hubs). A single service-wide FIFO buffer catches nested deliveries from
/// ANY hub and queues them until the outermost batch drains.
///
/// Multi-server isolation: each SignalR service (production, demo, mock,
/// per-registered-server) constructs its own coordinator so hubs on
/// different services never share state.
final class SignalRHubCoordinator: @unchecked Sendable {
    /// This coordinator's private serial queue. Every hub-side read,
    /// mutation, registration, and delivery routes through here so
    /// submission order is globally FIFO across hubs.
    fileprivate let queue: DispatchQueue

    /// Reentrancy-detection key on the coordinator's queue. Any block
    /// executing on the queue sets this specific; hubs use it to detect
    /// on-queue callers (i.e. callbacks) and inline instead of `sync`
    /// deadlocking.
    fileprivate let key = DispatchSpecificKey<Void>()

    /// Service-wide FIFO delivery guard. `inProgress` + `pending` are ONLY
    /// touched from within blocks running on `queue` — no additional lock
    /// needed because the queue serializes access.
    private var deliveryInProgress: Bool = false
    private var pendingDeliveries: [() -> Void] = []

    init(label: String = "com.printfarmer.signalr.hubs") {
        self.queue = DispatchQueue(label: label)
        self.queue.setSpecific(key: key, value: ())
    }

    /// True iff the current thread is executing on this coordinator's queue.
    var isOnQueue: Bool {
        DispatchQueue.getSpecific(key: key) != nil
    }

    /// Synchronously run `work` on the coordinator's queue. Reentrant-safe:
    /// an on-queue caller inlines instead of `queue.sync`.
    func sync<T>(_ work: () throws -> T) rethrows -> T {
        if isOnQueue { return try work() }
        return try queue.sync(execute: work)
    }

    /// Asynchronously enqueue `work` on the coordinator's queue.
    func async(_ work: @escaping @Sendable () -> Void) {
        queue.async(execute: work)
    }

    /// Enter the service-wide delivery guard. If a delivery is already in
    /// progress on ANY hub belonging to this coordinator, queue this
    /// closure FIFO and return; the outermost delivery drains everything
    /// in submission order. Otherwise run `work`, then drain arrivals.
    ///
    /// MUST be invoked from within a block running on `queue` (either via
    /// `sync` or `async`). Callers are always the hub delivery paths so
    /// this precondition is satisfied naturally.
    func enterDelivery(_ work: @escaping () -> Void) {
        precondition(isOnQueue, "enterDelivery must run on the coordinator queue")
        if deliveryInProgress {
            // The closure will be invoked later from the outer drain loop.
            // We copy the closure into pending storage.
            pendingDeliveries.append(work)
            return
        }
        deliveryInProgress = true
        work()
        while !pendingDeliveries.isEmpty {
            let next = pendingDeliveries.removeFirst()
            next()
        }
        deliveryInProgress = false
    }

    /// Test seam: number of pending deliveries queued by the FIFO guard.
    /// Snapshot from the coordinator queue to keep the read race-free.
    var pendingDeliveryCountForTesting: Int {
        sync { pendingDeliveries.count }
    }
}

// MARK: - SignalRConnectionStateHub

/// One coherent serial-executor boundary for connection-state reads,
/// mutation, handler registration, initial-state observation, and ordered
/// transition delivery. All operations dispatch through the injected
/// `SignalRHubCoordinator` — cross-hub cycles inline (no deadlock) and
/// nested deliveries across hubs are FIFO'd through the coordinator's
/// service-wide guard.
final class SignalRConnectionStateHub: @unchecked Sendable {
    private let coordinator: SignalRHubCoordinator
    private var state: SignalRConnectionState
    /// Insertion-ordered handler storage. Delivery iterates `order` and
    /// resolves each id through `handlers`, guaranteeing FIFO delivery in
    /// registration order — the reviewer contract (r7 blocker #1). Using
    /// `Dictionary.values` cannot be relied on; its ordering is
    /// unspecified and can shuffle between runs, producing the observed
    /// cross-hub / registration-order flake.
    private var handlers: [UUID: @Sendable (SignalRConnectionState) -> Void] = [:]
    private var order: [UUID] = []

    init(
        coordinator: SignalRHubCoordinator,
        initialState: SignalRConnectionState = .disconnected
    ) {
        self.coordinator = coordinator
        self.state = initialState
    }

    /// Race-free read of the current state. Reentrant-safe.
    func snapshot() -> SignalRConnectionState {
        coordinator.sync { state }
    }

    @discardableResult
    func subscribe(
        _ handler: @escaping @Sendable (SignalRConnectionState) -> Void
    ) -> (initial: SignalRConnectionState, subscription: SignalRSubscription) {
        let id = UUID()
        let initial = coordinator.sync { () -> SignalRConnectionState in
            handlers[id] = handler
            order.append(id)
            return state
        }
        let subscription = SignalRSubscription { [weak self] in
            guard let self else { return }
            if self.coordinator.isOnQueue {
                self.removeHandlerLocked(id: id)
            } else {
                self.coordinator.async { [weak self] in
                    self?.removeHandlerLocked(id: id)
                }
            }
        }
        return (initial, subscription)
    }

    /// MUST be invoked from within a block running on the coordinator's
    /// queue (either reentrantly or via `async`).
    private func removeHandlerLocked(id: UUID) {
        handlers.removeValue(forKey: id)
        if let idx = order.firstIndex(of: id) {
            order.remove(at: idx)
        }
    }

    func setState(_ newState: SignalRConnectionState) {
        coordinator.async { [weak self] in
            guard let self, self.state != newState else { return }
            self.coordinator.enterDelivery {
                self.state = newState
                // Snapshot in registration order so delivery cannot be
                // reordered by cancellation racing against iteration.
                let currentHandlers = self.order.compactMap { self.handlers[$0] }
                for handler in currentHandlers {
                    handler(newState)
                }
            }
        }
    }

    /// Test-only synchronous version — reentrant-safe and FIFO through the
    /// service-wide guard.
    func setStateSync(_ newState: SignalRConnectionState) {
        coordinator.sync {
            guard state != newState else { return }
            coordinator.enterDelivery {
                self.state = newState
                let currentHandlers = self.order.compactMap { self.handlers[$0] }
                for handler in currentHandlers {
                    handler(newState)
                }
            }
        }
    }

    var handlerCountForTesting: Int {
        coordinator.sync { handlers.count }
    }
}

// MARK: - SignalREventHub

/// Serial-executor hub for a single SignalR event type. Same guarantees as
/// `SignalRConnectionStateHub`; delivery routes through the injected
/// coordinator's service-wide FIFO guard so nested cross-hub events do not
/// interleave.
final class SignalREventHub<Event: Sendable>: @unchecked Sendable {
    private let coordinator: SignalRHubCoordinator
    /// Insertion-ordered handler storage — see `SignalRConnectionStateHub`
    /// for the ordering contract (r7 blocker #1).
    private var handlers: [UUID: @Sendable (Event) -> Void] = [:]
    private var order: [UUID] = []

    init(coordinator: SignalRHubCoordinator) {
        self.coordinator = coordinator
    }

    @discardableResult
    func subscribe(_ handler: @escaping @Sendable (Event) -> Void) -> SignalRSubscription {
        let id = UUID()
        coordinator.sync {
            handlers[id] = handler
            order.append(id)
        }
        return SignalRSubscription { [weak self] in
            guard let self else { return }
            if self.coordinator.isOnQueue {
                self.removeHandlerLocked(id: id)
            } else {
                self.coordinator.async { [weak self] in
                    self?.removeHandlerLocked(id: id)
                }
            }
        }
    }

    /// MUST run on the coordinator's queue.
    private func removeHandlerLocked(id: UUID) {
        handlers.removeValue(forKey: id)
        if let idx = order.firstIndex(of: id) {
            order.remove(at: idx)
        }
    }

    func deliver(_ event: Event) {
        coordinator.async { [weak self] in
            guard let self else { return }
            self.coordinator.enterDelivery {
                let currentHandlers = self.order.compactMap { self.handlers[$0] }
                for handler in currentHandlers {
                    handler(event)
                }
            }
        }
    }

    func deliverSync(_ event: Event) {
        coordinator.sync {
            coordinator.enterDelivery { [self] in
                let currentHandlers = self.order.compactMap { self.handlers[$0] }
                for handler in currentHandlers {
                    handler(event)
                }
            }
        }
    }

    var handlerCountForTesting: Int {
        coordinator.sync { handlers.count }
    }
}

