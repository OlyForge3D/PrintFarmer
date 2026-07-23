import Foundation
@testable import PrintFarmer

final class MockSignalRService: SignalRServiceProtocol, @unchecked Sendable {
    /// Per-service coordinator: identical semantics to production's
    /// SignalRService — service-wide FIFO delivery guard, multi-server
    /// isolation. Each `MockSignalRService()` gets its own coordinator.
    private let coordinator = SignalRHubCoordinator(label: "com.printfarmer.mock.signalr.hubs")
    /// Same hubs as the production/demo services so tests exercise the
    /// same cancellable-subscription and ordered-delivery paths. Tests that
    /// want "the state change has been observed by the time this returns"
    /// use the `*Sync` variants below, which mirror the previous
    /// direct-callback semantics without giving up subscription tracking.
    private let connectionStateHub: SignalRConnectionStateHub
    private let printerUpdateHub: SignalREventHub<PrinterStatusUpdate>
    private let jobQueueUpdateHub: SignalREventHub<JobQueueUpdate>
    private let attentionChangedHub: SignalREventHub<AttentionChangedEvent>
    private let taskInvalidationHub: SignalREventHub<ShiftTaskInvalidation>
    private let filamentCoverageChangedHub: SignalREventHub<FilamentCoverageChangedEvent>
    private let capturedAttentionLock = NSLock()
    private var capturedAttentionHandlers:
        [@Sendable (AttentionChangedEvent) -> Void] = []

    init() {
        self.connectionStateHub = SignalRConnectionStateHub(coordinator: coordinator)
        self.printerUpdateHub = SignalREventHub<PrinterStatusUpdate>(coordinator: coordinator)
        self.jobQueueUpdateHub = SignalREventHub<JobQueueUpdate>(coordinator: coordinator)
        self.attentionChangedHub = SignalREventHub<AttentionChangedEvent>(coordinator: coordinator)
        self.taskInvalidationHub = SignalREventHub<ShiftTaskInvalidation>(coordinator: coordinator)
        self.filamentCoverageChangedHub = SignalREventHub<FilamentCoverageChangedEvent>(coordinator: coordinator)
    }

    /// Race-free connection-state read; drains any pending mutation before
    /// returning.
    var connectionState: SignalRConnectionState {
        get { connectionStateHub.snapshot() }
        set { connectionStateHub.setStateSync(newValue) }
    }

    var connectCalled = false
    var disconnectCalled = false
    var fallbackGroupsUpdatedHandler: (@Sendable (FallbackGroupsUpdatedEvent) -> Void)?
    var errorToThrow: Error?

    func connect() async throws {
        connectCalled = true
        if let error = errorToThrow { throw error }
        connectionStateHub.setStateSync(.connected)
    }

    func disconnect() async {
        disconnectCalled = true
        connectionStateHub.setStateSync(.disconnected)
    }

    @discardableResult
    func onConnectionStateChanged(
        _ handler: @escaping @Sendable (SignalRConnectionState) -> Void
    ) -> (initial: SignalRConnectionState, subscription: SignalRSubscription) {
        connectionStateHub.subscribe(handler)
    }

    @discardableResult
    func onPrinterUpdated(_ handler: @escaping @Sendable (PrinterStatusUpdate) -> Void) -> SignalRSubscription {
        printerUpdateHub.subscribe(handler)
    }

    @discardableResult
    func onJobQueueUpdated(_ handler: @escaping @Sendable (JobQueueUpdate) -> Void) -> SignalRSubscription {
        jobQueueUpdateHub.subscribe(handler)
    }

    func onFallbackGroupsUpdated(_ handler: @escaping @Sendable (FallbackGroupsUpdatedEvent) -> Void) {
        fallbackGroupsUpdatedHandler = handler
    }

    @discardableResult
    func onAttentionChanged(_ handler: @escaping @Sendable (AttentionChangedEvent) -> Void) -> SignalRSubscription {
        capturedAttentionLock.lock()
        capturedAttentionHandlers.append(handler)
        capturedAttentionLock.unlock()
        return attentionChangedHub.subscribe(handler)
    }

    @discardableResult
    func onFilamentCoverageChanged(
        _ handler: @escaping @Sendable (FilamentCoverageChangedEvent) -> Void
    ) -> SignalRSubscription {
        filamentCoverageChangedHub.subscribe(handler)
    }

    @discardableResult
    func onTaskInvalidated(
        _ handler: @escaping @Sendable (ShiftTaskInvalidation) -> Void
    ) -> SignalRSubscription {
        taskInvalidationHub.subscribe(handler)
    }

    /// Simulate an attention-invalidation event for testing. Uses the hub's
    /// synchronous delivery path so callers observe the effect immediately
    /// on return without a fixed sleep.
    func simulateAttentionChanged(_ event: AttentionChangedEvent) {
        attentionChangedHub.deliverSync(event)
    }

    /// Delivers through the raw handler captured at registration time, even
    /// after its cancellable hub subscription was removed. This models a
    /// transport callback already in flight during authority replacement.
    func simulateCapturedAttentionChanged(
        at index: Int,
        event: AttentionChangedEvent
    ) {
        capturedAttentionLock.lock()
        let handler = capturedAttentionHandlers[index]
        capturedAttentionLock.unlock()
        handler(event)
    }

    /// Simulate a `filamentcoveragechanged` invalidation event for
    /// testing. Uses the hub's synchronous delivery so tests observe the
    /// effect before the call returns — no sleeps required.
    func simulateFilamentCoverageChanged(_ event: FilamentCoverageChangedEvent) {
        filamentCoverageChangedHub.deliverSync(event)
    }

    func simulateTaskInvalidation(target: String) {
        taskInvalidationHub.deliverSync(ShiftTaskInvalidation(target: target))
    }

    func simulateConnectionStateChange(_ state: SignalRConnectionState) {
        connectionStateHub.setStateSync(state)
    }

    /// Simulate a printer status update for testing.
    func simulatePrinterUpdate(_ update: PrinterStatusUpdate) {
        printerUpdateHub.deliverSync(update)
    }

    /// Simulate a fallback-groups invalidation for testing (issue #711).
    func simulateFallbackGroupsUpdated(_ event: FallbackGroupsUpdatedEvent) {
        fallbackGroupsUpdatedHandler?(event)
    }

    /// Simulate a job-queue update. Used by cross-hub FIFO tests to trigger
    /// nested deliveries across two hubs owned by the same coordinator.
    func simulateJobQueueUpdate(_ update: JobQueueUpdate) {
        jobQueueUpdateHub.deliverSync(update)
    }

    // MARK: - Test introspection

    var connectionStateSubscriberCount: Int { connectionStateHub.handlerCountForTesting }
    var attentionSubscriberCount: Int { attentionChangedHub.handlerCountForTesting }
    var capturedAttentionHandlerCount: Int {
        capturedAttentionLock.lock()
        defer { capturedAttentionLock.unlock() }
        return capturedAttentionHandlers.count
    }
    var filamentCoverageSubscriberCount: Int { filamentCoverageChangedHub.handlerCountForTesting }
    var taskInvalidationSubscriberCount: Int { taskInvalidationHub.handlerCountForTesting }
    var printerUpdateSubscriberCount: Int { printerUpdateHub.handlerCountForTesting }
    var jobQueueSubscriberCount: Int { jobQueueUpdateHub.handlerCountForTesting }
}
