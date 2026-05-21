import Foundation
@testable import PrintFarmer

final class MockSignalRService: SignalRServiceProtocol, @unchecked Sendable {
    var connectionState: SignalRConnectionState = .disconnected
    var connectCalled = false
    var disconnectCalled = false
    var printerUpdateHandler: (@Sendable (PrinterStatusUpdate) -> Void)?
    var jobQueueUpdateHandler: (@Sendable (JobQueueUpdate) -> Void)?
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

    /// Simulate a printer status update for testing.
    func simulatePrinterUpdate(_ update: PrinterStatusUpdate) {
        printerUpdateHandler?(update)
    }
}
