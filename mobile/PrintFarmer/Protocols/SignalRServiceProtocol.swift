import Foundation

// MARK: - SignalR Service Protocol

protocol SignalRServiceProtocol: AnyObject, Sendable {
    var connectionState: SignalRConnectionState { get }
    func connect() async throws
    func disconnect() async
    func onPrinterUpdated(_ handler: @escaping @Sendable (PrinterStatusUpdate) -> Void)
    func onJobQueueUpdated(_ handler: @escaping @Sendable (JobQueueUpdate) -> Void)
}
