import Foundation

// MARK: - Printer Service

actor PrinterService: PrinterServiceProtocol {
    private let apiClient: APIClient
    private var capabilitiesCache: [UUID: PrinterBackendCapabilities] = [:]

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

    func listCameraUrls() async throws -> [PrinterCameraUrls] {
        try await apiClient.get("/api/printers/camera-urls")
    }

    func getCameraUrl(id: UUID) async throws -> PrinterCameraUrl {
        try await apiClient.get("/api/printers/\(id)/camera-url")
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

    // MARK: - Capabilities

    /// Returns merged backend capabilities for the given printer.
    ///
    /// Fetches `/api/printers/{id}/backend-capabilities` and overlays the
    /// authoritative `supportsMovement` / `supportsTemperatureControl` values
    /// onto the static fallback derived from the wire DTO's `backend` field.
    /// Falls back to the static table when the endpoint is unavailable.
    /// Results are cached in-memory keyed by `printerId`.
    func getBackendCapabilities(printerId: UUID) async throws -> PrinterBackendCapabilities {
        if let cached = capabilitiesCache[printerId] {
            return cached
        }

        let merged: PrinterBackendCapabilities
        do {
            let wire: PrinterBackendCapabilitiesWireDto = try await apiClient.get(
                "/api/printers/\(printerId)/backend-capabilities"
            )
            let backend = wire.backend ?? .unknown
            let base = PrinterBackendCapabilities.fallback(for: backend)
            merged = PrinterBackendCapabilities(
                supportsMovement: wire.supportsMovement ?? base.supportsMovement,
                supportsTemperatureControl: wire.supportsTemperatureControl ?? base.supportsTemperatureControl,
                supportsBedTemperature: base.supportsBedTemperature,
                supportsFanControl: base.supportsFanControl,
                supportsHoming: base.supportsHoming,
                supportedAxes: base.supportedAxes
            )
        } catch let error as NetworkError {
            // Endpoint missing or printer unknown to capabilities service:
            // derive from the printer's backend type.
            switch error {
            case .notFound, .serverError:
                let printer = try await get(id: printerId)
                merged = PrinterBackendCapabilities.fallback(for: printer.backend)
            default:
                throw error
            }
        }

        capabilitiesCache[printerId] = merged
        return merged
    }

    // MARK: - Temperature & Motion Controls

    func setTemperatures(printerId: UUID, hotend: Double?, bed: Double?) async throws {
        let body = SetTemperaturesRequest(hotend: hotend, bed: bed)
        try await apiClient.postVoid("/api/printers/\(printerId)/temps", body: body)
    }

    func home(printerId: UUID, axes: [String]) async throws {
        let path = PrinterService.homePath(forAxes: axes, printerId: printerId)
        try await apiClient.postVoid(path)
    }

    func homeXY(printerId: UUID) async throws {
        try await home(printerId: printerId, axes: ["X", "Y"])
    }

    func homeZ(printerId: UUID) async throws {
        try await home(printerId: printerId, axes: ["Z"])
    }

    func move(printerId: UUID, axis: String, distanceMm: Double, feedrateMmMin: Int) async throws {
        let body = MovePrinterRequest(axis: axis, distanceMm: distanceMm, feedrateMmMin: feedrateMmMin)
        try await apiClient.postVoid("/api/printers/\(printerId)/move", body: body)
    }

    /// Routes a home request to the correct backend endpoint based on the axes set.
    /// Backend exposes three distinct routes: `/home` (all), `/homexy`, `/homez` — no body.
    static func homePath(forAxes axes: [String], printerId: UUID) -> String {
        let set = Set(axes.map { $0.uppercased() })
        if set == ["X", "Y"] {
            return "/api/printers/\(printerId)/homexy"
        }
        if set == ["Z"] {
            return "/api/printers/\(printerId)/homez"
        }
        return "/api/printers/\(printerId)/home"
    }
}

// MARK: - Request bodies

/// Encodes only non-nil fields per backend `TempTargets(double? Hotend, double? Bed)` contract.
struct SetTemperaturesRequest: Encodable {
    let hotend: Double?
    let bed: Double?

    enum CodingKeys: String, CodingKey {
        case hotend
        case bed
    }

    func encode(to encoder: Encoder) throws {
        var container = encoder.container(keyedBy: CodingKeys.self)
        if let hotend { try container.encode(hotend, forKey: .hotend) }
        if let bed { try container.encode(bed, forKey: .bed) }
    }
}

/// Encodes a single-axis relative move into backend `MoveRequest(double? X, double? Y, double? Z, double? F)`.
/// Caller-supplied axis ("X"/"Y"/"Z") populates the matching field; others are omitted.
struct MovePrinterRequest: Encodable {
    let axis: String
    let distanceMm: Double
    let feedrateMmMin: Int

    enum CodingKeys: String, CodingKey {
        case x
        case y
        case z
        case f
    }

    func encode(to encoder: Encoder) throws {
        var container = encoder.container(keyedBy: CodingKeys.self)
        switch axis.uppercased() {
        case "X": try container.encode(distanceMm, forKey: .x)
        case "Y": try container.encode(distanceMm, forKey: .y)
        case "Z": try container.encode(distanceMm, forKey: .z)
        default: try container.encode(distanceMm, forKey: .x)
        }
        try container.encode(Double(feedrateMmMin), forKey: .f)
    }
}
