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
}
