import Foundation

// MARK: - Filament Coverage DTOs (F4-M / issue #778)
//
// Mirrors the backend contract merged in PR #732:
//   - GET /api/printers/{id}/filament-coverage → PrinterFilamentCoverageDto
//   - GET /api/printers/filament-coverage      → FleetFilamentCoverageDto
//
// The coverage feature uses a *feature-local* wire vocabulary
// (`unknown | covers | runout`) rather than the repository-wide enum
// converter. Migration decoders (see `FilamentCoverageStatus.init(from:)`)
// also accept the legacy PascalCase spellings (`Unknown`, `Covers`,
// `Runout`) and the old `Insufficient` token (mapped to `.runout`). Integer
// enum values are rejected — an integer here would silently misalign with
// the local converter's string vocabulary and produce false "covers"
// claims. See src/infra/Dtos/FilamentCoverageDtos.cs for the authoritative
// server shapes.
//
// The client MUST NEVER surface a runout claim while status is `.unknown`.

/// Canonical per-slot / aggregate coverage verdict. Wire values are
/// lowercase; migration tokens are accepted read-only.
enum FilamentCoverageStatus: String, Codable, Sendable, Equatable, CaseIterable {
    /// Known remaining filament covers all known demand (active job +
    /// assigned queued jobs) with any configured safety buffer applied.
    case covers
    /// Known demand exceeds known remaining filament. Response *may*
    /// carry a predicted runout time / layer, but the client must not
    /// require them to render a runout affordance.
    case runout
    /// Coverage cannot be safely determined (no Spoolman remaining, no
    /// per-extruder gcode metadata, gcode silent about filament usage).
    /// The client MUST NOT surface any runout / covers claim in this
    /// state.
    case unknown

    // Decoded from any of: `unknown|covers|runout` (canonical),
    // `Unknown|Covers|Runout` (migration), or `Insufficient` (legacy,
    // mapped to `.runout`). Anything else — including a JSON number —
    // throws `DecodingError.dataCorrupted` so a schema drift is loud.
    init(from decoder: Decoder) throws {
        let container = try decoder.singleValueContainer()
        let raw: String
        do {
            raw = try container.decode(String.self)
        } catch {
            throw DecodingError.dataCorruptedError(
                in: container,
                debugDescription: "FilamentCoverageStatus must be a string; got a non-string token."
            )
        }
        switch raw {
        case "covers", "Covers":
            self = .covers
        case "runout", "Runout", "Insufficient":
            self = .runout
        case "unknown", "Unknown":
            self = .unknown
        default:
            throw DecodingError.dataCorruptedError(
                in: container,
                debugDescription: "Unknown FilamentCoverageStatus value: \(raw)"
            )
        }
    }

    // Always emit the canonical lowercase spelling on encode; no client
    // path should ever produce a migration or legacy token on the wire.
    func encode(to encoder: Encoder) throws {
        var container = encoder.singleValueContainer()
        try container.encode(rawValue)
    }
}

/// Coverage snapshot for a single toolhead slot on one printer.
///
/// The `toolheadIndex` is the zero-based gcode T-command index. The
/// optional `toolheadId` (a stable `UUID` supplied when the backend
/// distinguishes MMU/AMS gate identity from the persisted index) is
/// additive — it lets clients keep rows distinct even when persisted
/// indices overlap. The UI MUST prefer `toolheadId` when present and
/// fall back to `(printerId, toolheadIndex)` otherwise; it MUST NEVER
/// rely on `toolheadName` for identity because names duplicate.
struct ToolheadFilamentCoverage: Codable, Sendable, Equatable, Identifiable {
    let toolheadIndex: Int
    let toolheadId: UUID?
    let toolheadName: String
    let spoolId: Int?
    let material: String?
    let filamentColor: String?
    let remainingGrams: Double?
    let currentJobRequiredGrams: Double?
    let currentJobRemainingGrams: Double?
    let queuedRequiredGrams: Double?
    let totalDemandGrams: Double?
    let status: FilamentCoverageStatus
    let statusReason: String?
    let predictedRunoutAt: Date?
    let predictedRunoutLayer: Int?
    let availableForNewDemandGrams: Double?

    /// Stable list-diffable id derived from either the backend-supplied
    /// `toolheadId` (preferred) or the `toolheadIndex`. Never derived from
    /// the display name — two toolheads with duplicate names must remain
    /// distinct rows.
    var id: String {
        if let toolheadId {
            return "id:\(toolheadId.uuidString)"
        }
        return "index:\(toolheadIndex)"
    }

    init(
        toolheadIndex: Int,
        toolheadId: UUID? = nil,
        toolheadName: String,
        spoolId: Int? = nil,
        material: String? = nil,
        filamentColor: String? = nil,
        remainingGrams: Double? = nil,
        currentJobRequiredGrams: Double? = nil,
        currentJobRemainingGrams: Double? = nil,
        queuedRequiredGrams: Double? = nil,
        totalDemandGrams: Double? = nil,
        status: FilamentCoverageStatus,
        statusReason: String? = nil,
        predictedRunoutAt: Date? = nil,
        predictedRunoutLayer: Int? = nil,
        availableForNewDemandGrams: Double? = nil
    ) {
        self.toolheadIndex = toolheadIndex
        self.toolheadId = toolheadId
        self.toolheadName = toolheadName
        self.spoolId = spoolId
        self.material = material
        self.filamentColor = filamentColor
        self.remainingGrams = remainingGrams
        self.currentJobRequiredGrams = currentJobRequiredGrams
        self.currentJobRemainingGrams = currentJobRemainingGrams
        self.queuedRequiredGrams = queuedRequiredGrams
        self.totalDemandGrams = totalDemandGrams
        self.status = status
        self.statusReason = statusReason
        self.predictedRunoutAt = predictedRunoutAt
        self.predictedRunoutLayer = predictedRunoutLayer
        self.availableForNewDemandGrams = availableForNewDemandGrams
    }

    private enum CodingKeys: String, CodingKey {
        case toolheadIndex, toolheadId, toolheadName, spoolId, material,
             filamentColor, remainingGrams, currentJobRequiredGrams,
             currentJobRemainingGrams, queuedRequiredGrams, totalDemandGrams,
             status, statusReason, predictedRunoutAt, predictedRunoutLayer,
             availableForNewDemandGrams
    }
}

/// Coverage snapshot for one printer.
struct PrinterFilamentCoverage: Codable, Sendable, Equatable, Identifiable {
    let printerId: UUID
    let printerName: String
    let status: FilamentCoverageStatus
    let toolheads: [ToolheadFilamentCoverage]
    let activeJobId: UUID?
    let activeJobName: String?
    let activeJobProgress: Double?
    let earliestPredictedRunoutAt: Date?
    let assignedQueuedJobCount: Int
    let evaluatedAtUtc: Date

    /// `Identifiable` conformance uses the stable printer id, matching
    /// the app's navigation contract (`AppDestination.printerDetail(id:)`).
    var id: UUID { printerId }
}

/// Fleet-wide response for the batch endpoint.
struct FleetFilamentCoverage: Codable, Sendable, Equatable {
    let printers: [PrinterFilamentCoverage]
    let evaluatedAtUtc: Date
}
