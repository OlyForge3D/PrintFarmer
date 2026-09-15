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
    private let capturedConnectionStateLock = NSLock()
    private var capturedConnectionStateHandlers:
        [@Sendable (SignalRConnectionState) -> Void] = []
    private let printerSubscriptionLock = NSLock()
    private var recordedPrinterSubscriptionCalls: [[UUID]] = []
    private var printerSubscriptionWaiters:
        [(target: Int, continuation: CheckedContinuation<Void, Never>)] = []

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
    /// Number of times ``ensureConnected()`` was invoked. Counts *entries*, not
    /// connects — the whole point of the debounce tests is to prove the entry
    /// point is not called repeatedly, and the default implementation short-
    /// circuits once the hub reports `.connected`.
    private(set) var ensureConnectedCallCount = 0
    var fallbackGroupsUpdatedHandler: (@Sendable (FallbackGroupsUpdatedEvent) -> Void)?
    var errorToThrow: Error?
    /// Optional deterministic hook awaited at the start of `disconnect()`. Lets a
    /// test park a switch operation exactly at its outgoing-service teardown
    /// suspension point (issue #816 H1) without sleeps/polling.
    var disconnectHook: (@Sendable () async -> Void)?
    /// Optional deterministic hook awaited at the start of `connect()`. Lets a test
    /// park a switch at its incoming-service connect suspension point (issue #816 C).
    var connectHook: (@Sendable () async -> Void)?

    func connect() async throws {
        connectCalled = true
        if let connectHook { await connectHook() }
        if let error = errorToThrow { throw error }
        connectionStateHub.setStateSync(.connected)
    }

    func disconnect() async {
        disconnectCalled = true
        if let disconnectHook { await disconnectHook() }
        connectionStateHub.setStateSync(.disconnected)
    }

    /// Mirrors the `SignalRServiceProtocol` default implementation, adding a
    /// call counter. Overriding rather than relying on the protocol extension is
    /// deliberate: an extension member is statically dispatched, so a counter
    /// added there would not be observable through the existential.
    func ensureConnected() async {
        ensureConnectedCallCount += 1
        let state = connectionState
        guard state != .connected, state != .connecting else { return }
        try? await connect()
    }

    func replacePrinterSubscriptions(_ printerIds: [UUID]) async {
        recordPrinterSubscriptionCall(printerIds)
    }

    private func recordPrinterSubscriptionCall(_ printerIds: [UUID]) {
        printerSubscriptionLock.lock()
        recordedPrinterSubscriptionCalls.append(printerIds)
        let callCount = recordedPrinterSubscriptionCalls.count
        let ready = printerSubscriptionWaiters.filter { callCount >= $0.target }
        printerSubscriptionWaiters.removeAll { callCount >= $0.target }
        printerSubscriptionLock.unlock()
        ready.forEach { $0.continuation.resume() }
    }

    @discardableResult
    func onConnectionStateChanged(
        _ handler: @escaping @Sendable (SignalRConnectionState) -> Void
    ) -> (initial: SignalRConnectionState, subscription: SignalRSubscription) {
        capturedConnectionStateLock.lock()
        capturedConnectionStateHandlers.append(handler)
        capturedConnectionStateLock.unlock()
        return connectionStateHub.subscribe(handler)
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

    func simulateCapturedConnectionStateChange(
        at index: Int,
        state: SignalRConnectionState
    ) {
        capturedConnectionStateLock.lock()
        let handler = capturedConnectionStateHandlers[index]
        capturedConnectionStateLock.unlock()
        handler(state)
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
    var capturedConnectionStateHandlerCount: Int {
        capturedConnectionStateLock.lock()
        defer { capturedConnectionStateLock.unlock() }
        return capturedConnectionStateHandlers.count
    }
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
    var printerSubscriptionCalls: [[UUID]] {
        printerSubscriptionLock.lock()
        defer { printerSubscriptionLock.unlock() }
        return recordedPrinterSubscriptionCalls
    }

    func waitForPrinterSubscriptionCallCount(_ target: Int) async {
        if hasPrinterSubscriptionCallCount(target) { return }

        await withCheckedContinuation { continuation in
            registerPrinterSubscriptionWaiter(
                target: target,
                continuation: continuation
            )
        }
    }

    private func hasPrinterSubscriptionCallCount(_ target: Int) -> Bool {
        printerSubscriptionLock.lock()
        defer { printerSubscriptionLock.unlock() }
        return recordedPrinterSubscriptionCalls.count >= target
    }

    private func registerPrinterSubscriptionWaiter(
        target: Int,
        continuation: CheckedContinuation<Void, Never>
    ) {
        printerSubscriptionLock.lock()
        if recordedPrinterSubscriptionCalls.count >= target {
            printerSubscriptionLock.unlock()
            continuation.resume()
        } else {
            printerSubscriptionWaiters.append((target, continuation))
            printerSubscriptionLock.unlock()
        }
    }
}
