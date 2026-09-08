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
    var supportsRelativeMovement: Bool { supportsMovement }
    var supportsHotendTemperature: Bool { supportsTemperatureControl }

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
}
