import Foundation

// MARK: - SignalR Service Protocol

protocol SignalRServiceProtocol: AnyObject, Sendable {
    var connectionState: SignalRConnectionState { get }
    func connect() async throws
    func disconnect() async
    func onPrinterUpdated(_ handler: @escaping @Sendable (PrinterStatusUpdate) -> Void)
    func onJobQueueUpdated(_ handler: @escaping @Sendable (JobQueueUpdate) -> Void)
    /// Subscribes to the lowercase `attentionchanged` invalidation event
    /// (issue #707). The event is a refetch hint, not a source of item
    /// truth — handlers should trigger a `GET /api/attention` fetch and
    /// never persist any field of the payload as the item's canonical
    /// state.
    func onAttentionChanged(_ handler: @escaping @Sendable (AttentionChangedEvent) -> Void)

    /// Subscribes to the lowercase `fallbackgroupsupdated` invalidation
    /// event (issue #711, F6) emitted after any fallback-group mutation.
    /// The payload is a refetch hint — handlers must trigger
    /// `GET /api/printers/{printerId}/fallback-groups` and never persist
    /// any field of the payload as the canonical group state.
    func onFallbackGroupsUpdated(_ handler: @escaping @Sendable (FallbackGroupsUpdatedEvent) -> Void)
}
