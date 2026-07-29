import Foundation

// MARK: - Toolhead + PrinterDetails wire models (issue #711, F6)
//
// Mirrors the additive shape exposed by the backend in PR #752:
//   - `Farm.Infrastructure.ToolheadDto.cumulativePrintHours`
//     (always emitted, numeric-including-zero when per-tool attribution is
//     active, explicit `null` otherwise)
//   - `Farm.Infrastructure.PrinterDetailsDto.supportsPerToolAttribution`
//     (always emitted; `true` only when the operator feature is on AND the
//     printer's persisted domain flag is set)
//   - `PrinterDetailsDto.fallbackGroups` — server returns `[]` when the
//     multi-slot-fallback operator feature is disabled.
//
// The shared `APIClient` decoder uses the default key strategy, so Swift
// property names match wire JSON exactly (camelCase). Both DTOs contain
// many fields Hudson's UI does not yet need; this Swift projection decodes
// only the F6-relevant subset. Codable ignores any extra keys, so backend
// evolution outside these fields does not break decoding.

/// Classifies how a toolhead maps to printer hardware, mirroring the
/// backend `Farm.Infrastructure.Domain.ToolheadType` enum which is wire-
/// serialized as a string via `JsonStringEnumConverter`.
enum ToolheadType: String, Codable, Sendable {
    /// Discrete physical toolhead with its own hotend/extruder
    /// (Prusa XL dock, Snapmaker J1 dual head).
    case physical = "Physical"

    /// Virtual gate on a multi-material unit feeding a shared hotend
    /// (Prusa MMU3 gate, Bambu AMS slot).
    case mmuGate = "MmuGate"

    /// Wire-defensive fallback for enum drift.
    case unknown = "Unknown"

    init(from decoder: Decoder) throws {
        let container = try decoder.singleValueContainer()
        let raw = (try? container.decode(String.self)) ?? ""
        self = Self(rawValue: raw) ?? .unknown
    }
}

/// Nozzle material type — mirrors `Farm.Infrastructure.Domain.NozzleType`
/// serialized as string via `JsonStringEnumConverter`. Unknown wire values
/// fall through to `.unknown` rather than failing the decode so a new
/// backend nozzle case never breaks the printer details response.
enum NozzleType: String, Codable, Sendable {
    case brass = "Brass"
    case hardenedSteel = "HardenedSteel"
    case stainlessSteel = "StainlessSteel"
    case tungstenCarbide = "TungstenCarbide"
    case abrasive = "Abrasive"
    case unknown = "Unknown"

    init(from decoder: Decoder) throws {
        let container = try decoder.singleValueContainer()
        let raw = (try? container.decode(String.self)) ?? ""
        self = Self(rawValue: raw) ?? .unknown
    }
}

/// F6-focused projection of `Farm.Infrastructure.ToolheadDto`.
///
/// - `cumulativePrintHours` uses the tri-state distinction the backend
///   guarantees on the wire: numeric (including `0`) means "supported and
///   accrued", explicit `null` means "attribution not applicable on this
///   printer" (feature disabled or printer domain flag off). UI callers
///   must use the presence of a numeric value — never `0.0` alone — to
///   decide whether to render the odometer.
struct Toolhead: Codable, Identifiable, Sendable, Equatable {
    let id: UUID
    let name: String?
    let index: Int
    let isPrimary: Bool
    let toolheadType: ToolheadType
    let nozzleDiameter: Double?
    let nozzleType: NozzleType?
    let supportedMaterials: [String]?
    let currentSpoolId: Int?
    let currentMaterial: String?
    let currentFilamentColor: String?

    /// Cumulative print-hours accrued to this specific toolhead. Numeric
    /// (including `0`) when the printer supports per-tool attribution AND
    /// the operator feature is enabled; explicit `null` otherwise.
    let cumulativePrintHours: Double?

    init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        id = try c.decode(UUID.self, forKey: .id)
        name = try c.decodeIfPresent(String.self, forKey: .name)
        index = try c.decodeIfPresent(Int.self, forKey: .index) ?? 0
        isPrimary = try c.decodeIfPresent(Bool.self, forKey: .isPrimary) ?? false
        toolheadType = try c.decodeIfPresent(ToolheadType.self, forKey: .toolheadType) ?? .physical
        nozzleDiameter = try c.decodeIfPresent(Double.self, forKey: .nozzleDiameter)
        nozzleType = try c.decodeIfPresent(NozzleType.self, forKey: .nozzleType)
        supportedMaterials = try c.decodeIfPresent([String].self, forKey: .supportedMaterials)
        currentSpoolId = try c.decodeIfPresent(Int.self, forKey: .currentSpoolId)
        currentMaterial = try c.decodeIfPresent(String.self, forKey: .currentMaterial)
        currentFilamentColor = try c.decodeIfPresent(String.self, forKey: .currentFilamentColor)
        cumulativePrintHours = try c.decodeIfPresent(Double.self, forKey: .cumulativePrintHours)
    }

    init(
        id: UUID,
        name: String?,
        index: Int,
        isPrimary: Bool,
        toolheadType: ToolheadType = .physical,
        nozzleDiameter: Double? = nil,
        nozzleType: NozzleType? = nil,
        supportedMaterials: [String]? = nil,
        currentSpoolId: Int? = nil,
        currentMaterial: String? = nil,
        currentFilamentColor: String? = nil,
        cumulativePrintHours: Double? = nil
    ) {
        self.id = id
        self.name = name
        self.index = index
        self.isPrimary = isPrimary
        self.toolheadType = toolheadType
        self.nozzleDiameter = nozzleDiameter
        self.nozzleType = nozzleType
        self.supportedMaterials = supportedMaterials
        self.currentSpoolId = currentSpoolId
        self.currentMaterial = currentMaterial
        self.currentFilamentColor = currentFilamentColor
        self.cumulativePrintHours = cumulativePrintHours
    }

    private enum CodingKeys: String, CodingKey {
        case id, name, index, isPrimary, toolheadType
        case nozzleDiameter, nozzleType, supportedMaterials
        case currentSpoolId, currentMaterial, currentFilamentColor
        case cumulativePrintHours
    }
}

/// F6-focused projection of `Farm.Infrastructure.PrinterDetailsDto` returned
/// by `GET /api/printers/{id}/details`. Only the fields F6 needs are
/// declared; the backend DTO carries many more (credentials, camera URLs,
/// scheduler defaults, etc.) that Codable silently ignores.
///
/// - `fallbackGroups` is `[]` when the multi-slot-fallback operator feature
///   is disabled server-side — treat empty and `nil` identically.
/// - `supportsPerToolAttribution` is `true` only when the operator feature
///   is on AND the printer supports per-tool attribution; use it to decide
///   whether to render per-tool odometers.
struct PrinterDetails: Codable, Identifiable, Sendable, Equatable {
    let id: UUID
    let name: String
    let backend: PrinterBackend
    let hasMmu: Bool?
    let manufacturerName: String?
    let modelName: String?
    let toolheads: [Toolhead]
    let fallbackGroups: [FilamentFallbackGroup]
    let supportsPerToolAttribution: Bool
    /// Base-64 printer revision used as the public ETag. The toolhead spool-bind
    /// endpoint (`PUT /toolheads/{index}/spool`) is If-Match protected, so a
    /// replay reuses this value from the same details read it validates the
    /// toolhead against. Optional/absent on older servers.
    let rowVersion: String?

    init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        id = try c.decode(UUID.self, forKey: .id)
        name = try c.decodeIfPresent(String.self, forKey: .name) ?? ""
        backend = try c.decodeIfPresent(PrinterBackend.self, forKey: .backend) ?? .unknown
        hasMmu = try c.decodeIfPresent(Bool.self, forKey: .hasMmu)
        manufacturerName = try c.decodeIfPresent(String.self, forKey: .manufacturerName)
        modelName = try c.decodeIfPresent(String.self, forKey: .modelName)
        let heads = try c.decodeIfPresent([Toolhead].self, forKey: .toolheads) ?? []
        toolheads = heads.sorted { $0.index < $1.index }
        fallbackGroups = try c.decodeIfPresent([FilamentFallbackGroup].self, forKey: .fallbackGroups) ?? []
        supportsPerToolAttribution = try c.decodeIfPresent(Bool.self, forKey: .supportsPerToolAttribution) ?? false
        rowVersion = try c.decodeIfPresent(String.self, forKey: .rowVersion)
    }

    init(
        id: UUID,
        name: String,
        backend: PrinterBackend,
        hasMmu: Bool? = nil,
        manufacturerName: String? = nil,
        modelName: String? = nil,
        toolheads: [Toolhead] = [],
        fallbackGroups: [FilamentFallbackGroup] = [],
        supportsPerToolAttribution: Bool = false,
        rowVersion: String? = nil
    ) {
        self.id = id
        self.name = name
        self.backend = backend
        self.hasMmu = hasMmu
        self.manufacturerName = manufacturerName
        self.modelName = modelName
        self.toolheads = toolheads.sorted { $0.index < $1.index }
        self.fallbackGroups = fallbackGroups
        self.supportsPerToolAttribution = supportsPerToolAttribution
        self.rowVersion = rowVersion
    }

    private enum CodingKeys: String, CodingKey {
        case id, name, backend, hasMmu, manufacturerName, modelName
        case toolheads, fallbackGroups, supportsPerToolAttribution, rowVersion
    }
}
