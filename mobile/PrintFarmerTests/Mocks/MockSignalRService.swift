import Foundation
@testable import PrintFarmer

final class MockSignalRService: SignalRServiceProtocol, @unchecked Sendable {
    var connectionState: SignalRConnectionState = .disconnected
    var connectCalled = false
    var disconnectCalled = false
    var printerUpdateHandler: (@Sendable (PrinterStatusUpdate) -> Void)?
    var jobQueueUpdateHandler: (@Sendable (JobQueueUpdate) -> Void)?
    var attentionChangedHandler: (@Sendable (AttentionChangedEvent) -> Void)?
    var errorToThrow: Error?

    func connect() async throws {
        connectCalled = true
        if let error = errorToThrow { throw error }
        connectionState = .connected
    }

    func disconnect() async {
        disconnectCalled = true
        connectionState = .disconnected
    }

    func onPrinterUpdated(_ handler: @escaping @Sendable (PrinterStatusUpdate) -> Void) {
        printerUpdateHandler = handler
    }

    func onJobQueueUpdated(_ handler: @escaping @Sendable (JobQueueUpdate) -> Void) {
        jobQueueUpdateHandler = handler
    }

    func onAttentionChanged(_ handler: @escaping @Sendable (AttentionChangedEvent) -> Void) {
        attentionChangedHandler = handler
    }

    /// Simulate an attention-invalidation event for testing. Callers use
    /// this to prove that a listener receives the invalidation and triggers
    /// its own refetch.
    func simulateAttentionChanged(_ event: AttentionChangedEvent) {
        attentionChangedHandler?(event)
    }

    /// Simulate a printer status update for testing.
    func simulatePrinterUpdate(_ update: PrinterStatusUpdate) {
        printerUpdateHandler?(update)
    }
}
