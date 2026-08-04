import Foundation

// MARK: - SignalR Service Protocol

protocol SignalRServiceProtocol: AnyObject, Sendable {
    /// Race-free snapshot of the current connection state. Backed by the
    /// service's serial state hub so it drains any pending mutations before
    /// returning; safe to call from any actor.
    var connectionState: SignalRConnectionState { get }
    func connect() async throws
    func disconnect() async

    /// Idempotent reconnect entry point. Does nothing when already connected
    /// or handshaking; otherwise (re)establishes the hub connection. Never
    /// throws. Called from app-foreground lifecycle hooks so a connection lost
    /// while the app was suspended is re-armed immediately instead of waiting
    /// out a backoff sleep.
    func ensureConnected() async

    /// Register a connection-state observer.
    ///
    /// Returns a tuple containing the state snapshot captured at registration
    /// time and a `SignalRSubscription` cancellation token. The subscription
    /// removes the handler when cancelled or deallocated, so callers (view
    /// models) can prevent unbounded handler accumulation across repeated
    /// configuration or view-model lifecycles by retaining the token only
    /// while they want to observe. Every registered handler either observes
    /// the current state via the return value OR receives it as a transition
    /// callback later, never both, never neither.
    @discardableResult
    func onConnectionStateChanged(
        _ handler: @escaping @Sendable (SignalRConnectionState) -> Void
    ) -> (initial: SignalRConnectionState, subscription: SignalRSubscription)

    @discardableResult
    func onPrinterUpdated(_ handler: @escaping @Sendable (PrinterStatusUpdate) -> Void) -> SignalRSubscription

    @discardableResult
    func onJobQueueUpdated(_ handler: @escaping @Sendable (JobQueueUpdate) -> Void) -> SignalRSubscription

    /// Subscribes to the lowercase `attentionchanged` invalidation event
    /// (issue #707). The event is a refetch hint, not a source of item
    /// truth — handlers should trigger a `GET /api/attention` fetch and
    /// never persist any field of the payload as the item's canonical
    /// state.
    @discardableResult
    func onAttentionChanged(_ handler: @escaping @Sendable (AttentionChangedEvent) -> Void) -> SignalRSubscription

    /// Subscribes to the lowercase `filamentcoveragechanged` invalidation
    /// event (issue #778 / PR #732). The payload is a refetch hint;
    /// handlers must trigger a canonical
    /// `GET /api/printers/{id}/filament-coverage` or
    /// `GET /api/printers/filament-coverage` refetch and never persist
    /// any field of the payload as the coverage state.
    @discardableResult
    func onFilamentCoverageChanged(
        _ handler: @escaping @Sendable (FilamentCoverageChangedEvent) -> Void
    ) -> SignalRSubscription

    /// Subscribes to the exact lowercase task invalidation targets shipped by
    /// the server: `taskcreated`, `taskupdated`, and `pendingtaskcount`.
    /// Payloads are refetch hints only; handlers must load `/api/tasks`
    /// canonically before publishing task state.
    @discardableResult
    func onTaskInvalidated(
        _ handler: @escaping @Sendable (ShiftTaskInvalidation) -> Void
    ) -> SignalRSubscription

    /// Subscribes to the lowercase `fallbackgroupsupdated` invalidation
    /// event (issue #711, F6) emitted after any fallback-group mutation.
    /// The payload is a refetch hint — handlers must trigger
    /// `GET /api/printers/{printerId}/fallback-groups` and never persist
    /// any field of the payload as the canonical group state.
    func onFallbackGroupsUpdated(_ handler: @escaping @Sendable (FallbackGroupsUpdatedEvent) -> Void)
}

extension SignalRServiceProtocol {
    /// Default: only connect when the hub is not already live or handshaking.
    /// `SignalRService` overrides this with a lifecycle-queue-atomic version
    /// that also honours an intentional disconnect.
    func ensureConnected() async {
        let state = connectionState
        guard state != .connected, state != .connecting else { return }
        try? await connect()
    }
}
