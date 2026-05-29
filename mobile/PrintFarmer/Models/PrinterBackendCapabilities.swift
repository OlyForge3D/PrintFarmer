import Foundation

/// Capabilities exposed by a printer's backend implementation.
///
/// Surfaces the subset of operations the controls UI needs to gate (movement,
/// temperature control, fan, homing, supported axes) in a single shape that's
/// independent of the wire DTO. Booleans here are AND of:
///   - what the backend (`PrinterBackend`) is capable of in PrintFarmer's plugin
///     model, and
///   - what the live `/api/printers/{id}/backend-capabilities` endpoint reports
///     for fields that overlap.
///
/// The wire DTO (`PrinterBackendCapabilitiesDto`) currently exposes
/// `supportsMovement` and `supportsTemperatureControl`; the remaining fields
/// (`supportsBedTemperature`, `supportsFanControl`, `supportsHoming`,
/// `supportedAxes`) are derived from a static lookup keyed by `printer.backend`.
struct PrinterBackendCapabilities: Codable, Equatable, Sendable {
    let supportsMovement: Bool
    let supportsTemperatureControl: Bool
    let supportsBedTemperature: Bool
    let supportsFanControl: Bool
    let supportsHoming: Bool
    let supportedAxes: [String]

    /// Static fallback table keyed by `PrinterBackend`. Used when the backend
    /// endpoint is unavailable, returns 404, or doesn't surface a given field.
    static func fallback(for backend: PrinterBackend) -> PrinterBackendCapabilities {
        switch backend {
        case .moonraker, .prusaLink, .octoPrint:
            return PrinterBackendCapabilities(
                supportsMovement: true,
                supportsTemperatureControl: true,
                supportsBedTemperature: true,
                supportsFanControl: true,
                supportsHoming: true,
                supportedAxes: ["X", "Y", "Z"]
            )
        case .flashForge:
            return PrinterBackendCapabilities(
                supportsMovement: true,
                supportsTemperatureControl: true,
                supportsBedTemperature: false,
                supportsFanControl: false,
                supportsHoming: true,
                supportedAxes: ["X", "Y", "Z"]
            )
        case .sdcp:
            return PrinterBackendCapabilities(
                supportsMovement: false,
                supportsTemperatureControl: false,
                supportsBedTemperature: false,
                supportsFanControl: false,
                supportsHoming: false,
                supportedAxes: []
            )
        case .unknown:
            return PrinterBackendCapabilities(
                supportsMovement: false,
                supportsTemperatureControl: false,
                supportsBedTemperature: false,
                supportsFanControl: false,
                supportsHoming: false,
                supportedAxes: []
            )
        }
    }
}

// MARK: - Wire DTO

/// Mirrors backend `PrinterBackendCapabilitiesDto`. Decoded from
/// `/api/printers/{id}/backend-capabilities`. Only the two overlapping fields
/// (`supportsMovement`, `supportsTemperatureControl`) are consumed; other
/// boolean flags are decoded for forward compatibility but ignored here.
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
}
