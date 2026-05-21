import Foundation

// MARK: - Printer Service

actor PrinterService: PrinterServiceProtocol {
    private let apiClient: APIClient

    init(apiClient: APIClient) {
        self.apiClient = apiClient
    }

    func list(includeDisabled: Bool = false) async throws -> [Printer] {
        let query = includeDisabled ? "?includeDisabled=true" : ""
        return try await apiClient.get("/api/printers\(query)")
    }

    func get(id: UUID) async throws -> Printer {
        try await apiClient.get("/api/printers/\(id)")
    }

    func update(id: UUID, _ request: UpdatePrinterRequest) async throws -> Printer {
        try await apiClient.put("/api/printers/\(id)", body: request)
    }

    func delete(id: UUID) async throws {
        try await apiClient.delete("/api/printers/\(id)")
    }

    func setMaintenanceMode(id: UUID, inMaintenance: Bool) async throws -> Printer {
        try await apiClient.put("/api/printers/\(id)/maintenance", body: inMaintenance)
    }

    // MARK: - Printer Commands

    func pause(id: UUID) async throws -> CommandResult {
        try await apiClient.post("/api/printers/\(id)/pause")
    }

    func resume(id: UUID) async throws -> CommandResult {
        try await apiClient.post("/api/printers/\(id)/resume")
    }

    func cancel(id: UUID) async throws -> CommandResult {
        try await apiClient.post("/api/printers/\(id)/cancel")
    }

    func stop(id: UUID) async throws -> CommandResult {
        try await apiClient.post("/api/printers/\(id)/stop")
    }

    func emergencyStop(id: UUID) async throws -> CommandResult {
        try await apiClient.post("/api/printers/\(id)/emergency-stop")
    }

    // MARK: - Status & Data

    func getStatus(id: UUID) async throws -> PrinterStatusDetail {
        try await apiClient.get("/api/printers/\(id)/status")
    }

    func getSnapshot(id: UUID) async throws -> Data {
        try await apiClient.getData("/api/printers/\(id)/snapshot")
    }

    func getCurrentJob(id: UUID) async throws -> PrintJobStatusInfo? {
        try await apiClient.get("/api/printers/\(id)/printjob")
    }

    // MARK: - Queue Overview

    func getQueueOverview(model: String? = nil, nozzle: Double? = nil, material: String? = nil) async throws -> [QueueOverview] {
        var params: [String] = []
        if let model { params.append("model=\(model)") }
        if let nozzle { params.append("nozzle=\(nozzle)") }
        if let material { params.append("material=\(material)") }
        let query = params.isEmpty ? "" : "?\(params.joined(separator: "&"))"
        return try await apiClient.get("/api/job-queue\(query)")
    }

    // MARK: - Filament / Spool

    func setActiveSpool(printerId: UUID, spoolId: Int?) async throws -> CommandResult {
        let body = SetActiveSpoolRequest(spoolId: spoolId)
        return try await apiClient.post("/api/printers/\(printerId)/active-spool", body: body)
    }

    func listAvailableSpools(printerId: UUID) async throws -> [SpoolmanSpool] {
        try await apiClient.get("/api/printers/\(printerId)/spoolman/spools")
    }

    func loadFilament(printerId: UUID) async throws -> CommandResult {
        try await apiClient.post("/api/printers/\(printerId)/filament-load")
    }

    func unloadFilament(printerId: UUID) async throws -> CommandResult {
        try await apiClient.post("/api/printers/\(printerId)/filament-unload")
    }

    func changeFilament(printerId: UUID) async throws -> CommandResult {
        try await apiClient.post("/api/printers/\(printerId)/filament-change")
    }
}
