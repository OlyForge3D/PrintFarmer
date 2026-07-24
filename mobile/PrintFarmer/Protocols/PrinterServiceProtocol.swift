import Foundation

// MARK: - Printer Service Protocol

/// Contract for printer operations. Lambert implements the concrete service;
/// Ripley's ViewModels depend only on this protocol.
protocol PrinterServiceProtocol: Sendable {
    func list(includeDisabled: Bool) async throws -> [Printer]
    func get(id: UUID) async throws -> Printer
    /// Fetches the extended printer detail envelope from
    /// `GET /api/printers/{id}/details`. Includes the F6 (issue #711)
    /// per-toolhead attribution surface (`supportsPerToolAttribution` +
    /// per-tool `cumulativePrintHours`) plus configured `fallbackGroups`.
    func getDetails(id: UUID) async throws -> PrinterDetails
    func getStatus(id: UUID) async throws -> PrinterStatusDetail
    func listCameraUrls() async throws -> [PrinterCameraUrls]
    func getCameraUrl(id: UUID) async throws -> PrinterCameraUrl
    func getSnapshot(id: UUID) async throws -> Data
    func getCurrentJob(id: UUID) async throws -> PrintJobStatusInfo?
    /// Recent job history from `GET /api/printers/{id}/history` (Moonraker-style
    /// snake_case payload). Used by the Printer Detail v2 history tail (issue
    /// #712). `limit` caps the number of returned jobs when supported.
    func getHistory(id: UUID, limit: Int?) async throws -> PrinterHistoryList
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

    // MARK: - Filament fallback groups (issue #711, F6)
    //
    // Ordered same-material chains over the printer's existing toolhead IDs.
    // All routes are gated server-side by the `MultiSlotFallback` operator
    // feature — endpoints return `NetworkError.featureDisabled` when the
    // operator has turned the feature off. Write operations additionally
    // require the `farm_admin` role and surface as `NetworkError.forbidden`.
    func listFallbackGroups(printerId: UUID) async throws -> [FilamentFallbackGroup]
    func getFallbackGroup(printerId: UUID, groupId: UUID) async throws -> FilamentFallbackGroup
    func createFallbackGroup(
        printerId: UUID,
        _ request: CreateFilamentFallbackGroupRequest
    ) async throws -> FilamentFallbackGroup
    func updateFallbackGroup(
        printerId: UUID,
        groupId: UUID,
        _ request: UpdateFilamentFallbackGroupRequest
    ) async throws -> FilamentFallbackGroup
    func deleteFallbackGroup(printerId: UUID, groupId: UUID) async throws

    /// Read-only evidence of a currently-available fallback slot matching
    /// `material` on `printerId`, excluding `sourceToolheadId`. Returns
    /// `nil` when the backend answers 204 No Content (no configured backup
    /// currently loaded). Callers use this evidence for runout-attention
    /// downgrade — never as confirmation that a switch actually happened.
    func getAvailableFallback(
        printerId: UUID,
        sourceToolheadId: UUID,
        material: String
    ) async throws -> AvailableFallbackMember?
}

// Convenience overload
extension PrinterServiceProtocol {
    func list() async throws -> [Printer] {
        try await list(includeDisabled: false)
    }

    func getQueueOverview() async throws -> [QueueOverview] {
        try await getQueueOverview(model: nil, nozzle: nil, material: nil)
    }

    func getHistory(id: UUID) async throws -> PrinterHistoryList {
        try await getHistory(id: id, limit: nil)
    }
}
