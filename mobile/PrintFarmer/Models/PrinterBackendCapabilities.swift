import Foundation

/// Explicit server evidence for commands, NOT a hardware-presence inventory.
/// False means unavailable or unknown; it does not prove that a heater/axis
/// is physically absent. A backend name or a generic control flag is not proof.
struct PrinterBackendCapabilities: Codable, Equatable, Sendable {
    let supportsMovement: Bool
    let supportsTemperatureControl: Bool
    let supportsBedTemperature: Bool
    let supportsFanControl: Bool
    let supportsHoming: Bool
    let supportedAxes: [String]
    var supportsAbsoluteMovement: Bool = false
    var supportsDisableMotors: Bool = false
    var supportsExtrusion: Bool = false
    var supportsZOffset: Bool = false
    var supportsZOffsetFirmwareSave: Bool = false
    var supportsHomingXY: Bool = false
    var supportsHomingZ: Bool = false
    var supportsFilamentLoad: Bool = false
    var supportsFilamentUnload: Bool = false
    var supportsFilamentChange: Bool = false
    var verifiedSafety: PrinterVerifiedSafetyDto?
    var supportsRelativeMovement: Bool { supportsMovement }
    var supportsHotendTemperature: Bool { supportsTemperatureControl }

    func supportsHome(axes: [String]) -> Bool {
        switch Set(axes.map { $0.uppercased() }) {
        case ["X", "Y", "Z"]: supportsHoming
        case ["X", "Y"]: supportsHomingXY
        case ["Z"]: supportsHomingZ
        default: false
        }
    }

    /// Keep the old entry point for callers, but never enable actuation from it.
    static func fallback(for backend: PrinterBackend) -> PrinterBackendCapabilities {
        PrinterBackendCapabilities(
            supportsMovement: false, supportsTemperatureControl: false,
            supportsBedTemperature: false, supportsFanControl: false,
            supportsHoming: false, supportedAxes: []
        )
    }
}

extension PrinterBackendCapabilities {
    init(wire: PrinterBackendCapabilitiesWireDto) {
        self.init(
            supportsMovement: wire.supportsRelativeMovement == true,
            supportsTemperatureControl: wire.supportsHotendTemperature == true,
            supportsBedTemperature: wire.supportsBedTemperature == true,
            supportsFanControl: false,
            supportsHoming: wire.supportsHoming == true,
            supportedAxes: (wire.supportedAxes ?? []).map { $0.uppercased() }.filter { ["X", "Y", "Z"].contains($0) }
        )
        supportsAbsoluteMovement = wire.supportsAbsoluteMovement == true
        supportsDisableMotors = wire.supportsDisableMotors == true
        supportsExtrusion = wire.supportsExtrusion == true
        supportsZOffset = wire.supportsZOffset == true
        supportsZOffsetFirmwareSave = wire.supportsZOffsetFirmwareSave == true
        supportsHomingXY = wire.supportsHomingXY == true
        supportsHomingZ = wire.supportsHomingZ == true
        supportsFilamentLoad = wire.supportsFilamentLoad == true
        supportsFilamentUnload = wire.supportsFilamentUnload == true
        supportsFilamentChange = wire.supportsFilamentChange == true
        verifiedSafety = wire.verifiedSafety
    }
}

// MARK: - Wire DTO

/// Mirrors backend `PrinterBackendCapabilitiesDto`. Decoded from
/// `/api/printers/{id}/backend-capabilities`. Optional operation flags allow
/// old servers to decode without optimistically enabling physical commands.
struct PrinterBackendCapabilitiesWireDto: Codable, Sendable {
    let printerId: UUID
    let printerName: String?
    let backend: PrinterBackend?
    let supportsMovement: Bool?
    let supportsTemperatureControl: Bool?
    let supportsCamera: Bool?
    let supportsFileDownload: Bool?
    let supportsFileList: Bool?
    let supportsFileUpload: Bool?
    let supportsStartPrint: Bool?
    let supportsControlOperations: Bool?
    let supportsFileMetadata: Bool?
    let supportsPrinterInformation: Bool?
    let supportsHistory: Bool?
    let supportsFilamentControl: Bool?
    let supportsRelativeMovement: Bool?
    let supportsAbsoluteMovement: Bool?
    let supportsDisableMotors: Bool?
    let supportsExtrusion: Bool?
    let supportsZOffset: Bool?
    let supportsZOffsetFirmwareSave: Bool?
    let supportsHoming: Bool?
    let supportsHomingXY: Bool?
    let supportsHomingZ: Bool?
    let supportsHotendTemperature: Bool?
    let supportsBedTemperature: Bool?
    let supportsFilamentLoad: Bool?
    let supportsFilamentUnload: Bool?
    let supportsFilamentChange: Bool?
    let supportedAxes: [String]?
    var verifiedSafety: PrinterVerifiedSafetyDto?
}

// Mirrors src/infra/Models/PrinterSafetyContracts.cs, including string enums.
// Unknown enum values fail closed; missing versioned envelopes remain optional.
enum VerifiedSafetySupport: String, Codable, Sendable {
    case supported = "Supported", unsupported = "Unsupported", unknown = "Unknown"
    init(from decoder: Decoder) throws {
        self = Self(rawValue: try decoder.singleValueContainer().decode(String.self)) ?? .unknown
    }
}

enum VerifiedSafetyFactState: String, Codable, Sendable {
    case verified = "Verified", unknown = "Unknown"
    init(from decoder: Decoder) throws {
        self = Self(rawValue: try decoder.singleValueContainer().decode(String.self)) ?? .unknown
    }
}

enum VerifiedSafetyDiscoveryState: String, Codable, Sendable {
    case verified = "Verified", partial = "Partial", unavailable = "Unavailable"
    init(from decoder: Decoder) throws {
        self = Self(rawValue: try decoder.singleValueContainer().decode(String.self)) ?? .unavailable
    }
}

struct SafetyVector3Dto: Codable, Equatable, Sendable {
    var x: Double
    var y: Double
    var z: Double
    var isFinite: Bool { x.isFinite && y.isFinite && z.isFinite }
}

struct SafetyTravelEnvelopeDto: Codable, Equatable, Sendable {
    var minimum: SafetyVector3Dto
    var maximum: SafetyVector3Dto
    var isValid: Bool {
        minimum.isFinite && maximum.isFinite &&
        minimum.x <= maximum.x && minimum.y <= maximum.y && minimum.z <= maximum.z
    }
    func contains(_ point: SafetyVector3Dto) -> Bool {
        isValid && point.isFinite &&
        (minimum.x...maximum.x).contains(point.x) &&
        (minimum.y...maximum.y).contains(point.y) &&
        (minimum.z...maximum.z).contains(point.z)
    }
}

struct VerifiedSafetyDiscoveryDto: Codable, Equatable, Sendable {
    var state: VerifiedSafetyDiscoveryState
    var observedAtUtc: Date?
    var sourceRevision: String?
}

struct VerifiedSafetyOperationCapabilityDto: Codable, Equatable, Sendable {
    var support: VerifiedSafetySupport
    var source: String?
    var observedAtUtc: Date?
}

struct VerifiedSafetyOperationsDto: Codable, Equatable, Sendable {
    var absoluteMovement: VerifiedSafetyOperationCapabilityDto
    var firmwareZOffsetSave: VerifiedSafetyOperationCapabilityDto
    var filamentLoad: VerifiedSafetyOperationCapabilityDto
    var filamentUnload: VerifiedSafetyOperationCapabilityDto
    var filamentChange: VerifiedSafetyOperationCapabilityDto
}

struct VerifiedSafetyFact<Value: Codable & Equatable & Sendable>: Codable, Equatable, Sendable {
    var state: VerifiedSafetyFactState
    var value: Value?
    var source: String?
    var observedAtUtc: Date?
}

typealias VerifiedSafetyScalarFactDto = VerifiedSafetyFact<Double>
typealias VerifiedSafetyVectorFactDto = VerifiedSafetyFact<SafetyVector3Dto>
typealias VerifiedSafetyEnvelopeFactDto = VerifiedSafetyFact<SafetyTravelEnvelopeDto>

struct VerifiedSafetyExtrusionDto: Codable, Equatable, Sendable {
    var minimumSafeMeasuredHotendTemperatureC: VerifiedSafetyScalarFactDto
}

struct VerifiedSafetyPositioningDto: Codable, Equatable, Sendable {
    var coordinateOriginMm: VerifiedSafetyVectorFactDto
    var travelEnvelopeMm: VerifiedSafetyEnvelopeFactDto
    var minimumClearanceZMm: VerifiedSafetyScalarFactDto
}

struct PrinterVerifiedSafetyDto: Codable, Equatable, Sendable {
    var contractVersion: Int
    var discovery: VerifiedSafetyDiscoveryDto
    var operations: VerifiedSafetyOperationsDto
    var extrusion: VerifiedSafetyExtrusionDto
    var positioning: VerifiedSafetyPositioningDto
}

struct SafetyTelemetryFact<Value: Codable & Equatable & Sendable>: Codable, Equatable, Sendable {
    var value: Value?
    var observedAtUtc: Date?
    var staleAfterSeconds: Int
    var source: String?

    func isFresh(at now: Date) -> Bool {
        guard value != nil, let observedAtUtc, staleAfterSeconds > 0,
              let source, !source.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else { return false }
        return observedAtUtc <= now && now.timeIntervalSince(observedAtUtc) <= Double(staleAfterSeconds)
    }
}

typealias SafetyScalarTelemetryFactDto = SafetyTelemetryFact<Double>
typealias SafetyAxesTelemetryFactDto = SafetyTelemetryFact<[String]>
typealias SafetyVectorTelemetryFactDto = SafetyTelemetryFact<SafetyVector3Dto>

struct PrinterSafetyTelemetryDto: Codable, Equatable, Sendable {
    var measuredHotendTemperatureC: SafetyScalarTelemetryFactDto
    var targetHotendTemperatureC: SafetyScalarTelemetryFactDto
    var homedAxes: SafetyAxesTelemetryFactDto
    var coordinateOriginOffsetMm: SafetyVectorTelemetryFactDto
}
