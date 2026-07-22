import Foundation

// MARK: - Printer Service Protocol

/// Contract for printer operations. Lambert implements the concrete service;
/// Ripley's ViewModels depend only on this protocol.
protocol PrinterServiceProtocol: Sendable {
    func list(includeDisabled: Bool) async throws -> [Printer]
    func get(id: UUID) async throws -> Printer
    func getStatus(id: UUID) async throws -> PrinterStatusDetail
    func listCameraUrls() async throws -> [PrinterCameraUrls]
    func getCameraUrl(id: UUID) async throws -> PrinterCameraUrl
    func getSnapshot(id: UUID) async throws -> Data
    func getCurrentJob(id: UUID) async throws -> PrintJobStatusInfo?
    func pause(id: UUID) async throws -> CommandResult
    func resume(id: UUID) async throws -> CommandResult
    func cancel(id: UUID) async throws -> CommandResult
    func stop(id: UUID) async throws -> CommandResult
    func emergencyStop(id: UUID) async throws -> CommandResult
    func setMaintenanceMode(id: UUID, inMaintenance: Bool) async throws -> Printer
    func getQueueOverview(model: String?, nozzle: Double?, material: String?) async throws -> [QueueOverview]

    // Filament / Spool
    func setActiveSpool(printerId: UUID, spoolId: Int?) async throws -> CommandResult
    func listAvailableSpools(printerId: UUID) async throws -> [SpoolmanSpool]
    func loadFilament(printerId: UUID) async throws -> CommandResult
    func unloadFilament(printerId: UUID) async throws -> CommandResult
    func changeFilament(printerId: UUID) async throws -> CommandResult

    // Capabilities
    func getBackendCapabilities(printerId: UUID) async throws -> PrinterBackendCapabilities

    // Temperature & Motion Controls
    func setTemperatures(printerId: UUID, hotend: Double?, bed: Double?) async throws
    func home(printerId: UUID, axes: [String]) async throws
    func homeXY(printerId: UUID) async throws
    func homeZ(printerId: UUID) async throws
    func move(printerId: UUID, axis: String, distanceMm: Double, feedrateMmMin: Int) async throws
}

// Convenience overload
extension PrinterServiceProtocol {
    func list() async throws -> [Printer] {
        try await list(includeDisabled: false)
    }

    func getQueueOverview() async throws -> [QueueOverview] {
        try await getQueueOverview(model: nil, nozzle: nil, material: nil)
    }
}
