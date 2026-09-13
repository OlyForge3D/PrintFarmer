import Foundation

// MARK: - Durable physical control contract

// Unlike display-only enums, unrecognized motion values must fail decoding.
enum PrinterControlOperationKind: String, Codable, CaseIterable, Sendable {
    case homeAll = "HomeAll"
    case homeXY = "HomeXY"
    case homeZ = "HomeZ"
    case jog = "Jog"
    case moveTo = "MoveTo"
}

enum PrinterControlOperationState: String, Codable, Sendable {
    case queued = "Queued"
    case running = "Running"
    case succeeded = "Succeeded"
    case failed = "Failed"
    case unknown = "Unknown"
    case recovering = "Recovering"
    case recovered = "Recovered"

    var isTerminal: Bool {
        self == .succeeded || self == .failed || self == .recovered
    }
}

enum PrinterControlCompletionEvidence: String, Codable, Sendable {
    case none = "None"
    case notSent = "NotSent"
    case backendRejected = "BackendRejected"
    case motionQueueDrained = "MotionQueueDrained"
    case operatorVerifiedRecovery = "OperatorVerifiedRecovery"
}

enum PrinterControlSenderIsolation: String, Codable, Sendable {
    case notRequested = "NotRequested"
    case pending = "Pending"
    case confirmed = "Confirmed"
    case externalVerificationRequired = "ExternalVerificationRequired"
}

struct PrinterControlOperationRequest: Codable, Equatable, Sendable {
    let kind: PrinterControlOperationKind
    let x: Double?
    let y: Double?
    let z: Double?
    let f: Double?

    init(kind: PrinterControlOperationKind, x: Double? = nil, y: Double? = nil,
         z: Double? = nil, f: Double? = nil) {
        self.kind = kind
        self.x = x
        self.y = y
        self.z = z
        self.f = f
    }
}

struct PrinterControlOperationFailure: Codable, Equatable, Sendable {
    let code: String
    let message: String
}

struct PrinterControlOperation: Codable, Equatable, Sendable {
    let operationId: UUID
    let printerId: UUID
    let kind: PrinterControlOperationKind
    var x: Double?
    var y: Double?
    var z: Double?
    var f: Double?
    let state: PrinterControlOperationState
    let rowVersion: String
    let createdAtUtc: Date
    let updatedAtUtc: Date
    var startedAtUtc: Date?
    var completedAtUtc: Date?
    let barrierHeld: Bool
    let requiresRecovery: Bool
    let completionEvidence: PrinterControlCompletionEvidence
    var failure: PrinterControlOperationFailure?
    let senderIsolation: PrinterControlSenderIsolation

    /// Only an authoritative read may release a caller's pending barrier.
    /// Acceptance, transport errors and telemetry provide no such evidence.
    var isSafelyComplete: Bool {
        guard !barrierHeld, !requiresRecovery else { return false }
        switch state {
        case .succeeded: return completionEvidence == .motionQueueDrained
        case .failed: return completionEvidence == .notSent || completionEvidence == .backendRejected
        case .recovered: return completionEvidence == .operatorVerifiedRecovery
        default: return false
        }
    }

    func validate(printerId: UUID, operationId: UUID? = nil) throws {
        guard self.printerId == printerId,
              operationId == nil || self.operationId == operationId,
              !rowVersion.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty,
              updatedAtUtc >= createdAtUtc,
              barrierHeld || isSafelyComplete else {
            throw PrinterControlOperationError.invalidResponse
        }
    }
}

struct PrinterPhysicalControl: Codable, Equatable, Sendable {
    let supportedOperations: [PrinterControlOperationKind]
    let barrierHeld: Bool
    var operationId: UUID?
    var state: PrinterControlOperationState?
    let requiresRecovery: Bool

    init(supportedOperations: [PrinterControlOperationKind], barrierHeld: Bool,
         operationId: UUID? = nil, state: PrinterControlOperationState? = nil,
         requiresRecovery: Bool) {
        self.supportedOperations = supportedOperations
        self.barrierHeld = barrierHeld
        self.operationId = operationId
        self.state = state
        self.requiresRecovery = requiresRecovery
    }

    private enum CodingKeys: String, CodingKey {
        case supportedOperations, barrierHeld, operationId, state, requiresRecovery
    }

    init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        self.init(
            supportedOperations: try c.decode([PrinterControlOperationKind].self, forKey: .supportedOperations),
            barrierHeld: try c.decode(Bool.self, forKey: .barrierHeld),
            operationId: try c.decodeIfPresent(UUID.self, forKey: .operationId),
            state: try c.decodeIfPresent(PrinterControlOperationState.self, forKey: .state),
            requiresRecovery: try c.decode(Bool.self, forKey: .requiresRecovery)
        )
        let hasConsistentOwner = (operationId == nil) == (state == nil)
        guard barrierHeld ? hasConsistentOwner : isExplicitlyUnlocked else {
            throw DecodingError.dataCorrupted(.init(
                codingPath: decoder.codingPath,
                debugDescription: "Inconsistent physical-control barrier evidence"
            ))
        }
    }

    var isExplicitlyUnlocked: Bool {
        !barrierHeld && !requiresRecovery && state == nil && operationId == nil
    }
}

struct PrinterCurrentControlOperation: Codable, Equatable, Sendable {
    let physicalControl: PrinterPhysicalControl
    /// Nil may coexist with a held barrier owned by an unrelated physical
    /// command. Only the explicit projection can establish an unlocked state.
    let operation: PrinterControlOperation?

    init(physicalControl: PrinterPhysicalControl, operation: PrinterControlOperation?) {
        self.physicalControl = physicalControl
        self.operation = operation
    }

    private enum CodingKeys: String, CodingKey {
        case physicalControl, operation
    }

    init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        physicalControl = try c.decode(PrinterPhysicalControl.self, forKey: .physicalControl)
        guard c.contains(.operation) else {
            throw DecodingError.keyNotFound(CodingKeys.operation, .init(
                codingPath: decoder.codingPath,
                debugDescription: "Current-operation evidence requires an explicit operation or null"
            ))
        }
        operation = try c.decodeIfPresent(PrinterControlOperation.self, forKey: .operation)
    }

    func validate(printerId: UUID) throws {
        if let operation {
            try operation.validate(printerId: printerId)
            guard operation.barrierHeld,
                  physicalControl.operationId == operation.operationId,
                  physicalControl.state == operation.state,
                  physicalControl.barrierHeld == operation.barrierHeld,
                  physicalControl.requiresRecovery == operation.requiresRecovery else {
                throw PrinterControlOperationError.invalidResponse
            }
        } else if physicalControl.operationId != nil || physicalControl.state != nil
                    || (!physicalControl.barrierHeld && !physicalControl.isExplicitlyUnlocked) {
            throw PrinterControlOperationError.invalidResponse
        }
    }
}

struct PrinterControlOperationInvalidation: Codable, Equatable, Sendable {
    let printerId: UUID
    let operationId: UUID
    let rowVersion: String
}

enum PrinterControlOperationError: LocalizedError, Sendable {
    case updateRequired
    case invalidResponse
    case problem(statusCode: Int, code: String?, message: String?)

    var errorDescription: String? {
        switch self {
        case .updateRequired:
            return "Update the PrintFarmer server to use durable motion controls. No legacy command was sent."
        case .invalidResponse:
            return "The server did not provide valid motion-operation evidence. Controls remain locked; refresh the operation."
        case .problem(_, let code, let message):
            return message ?? code ?? "The server rejected the motion-operation request."
        }
    }
}

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
