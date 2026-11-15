import Foundation

// MARK: - Spool Scan Error

enum SpoolScanError: Error, LocalizedError, Sendable {
    case permissionDenied
    case notSupported
    case invalidPayload(String)
    case spoolNotFound(Int)
    case networkError(Error)
    case tagNotNDEFSupported
    case tagRequiresISO15693
    case tagReadOnly
    case tagCapacityExceeded(Int)
    case tagWrittenButLinkFailed(String)
    case cancelled

    var errorDescription: String? {
        switch self {
        case .permissionDenied:
            return "Permission denied. Check Settings."
        case .notSupported:
            return "This scanning method is not supported on your device."
        case .invalidPayload(let detail):
            return "Invalid scan data: \(detail)"
        case .spoolNotFound(let id):
            return "Spool #\(id) not found."
        case .networkError(let err):
            return "Network error: \(err.localizedDescription)"
        case .tagNotNDEFSupported:
            return "This NFC tag does not support NDEF."
        case .tagRequiresISO15693:
            return "OpenPrintTag writing requires an NFC-V (ISO 15693) tag."
        case .tagReadOnly:
            return "This NFC tag is read-only."
        case .tagCapacityExceeded(let capacity):
            return "This NFC tag does not have enough capacity (\(capacity) bytes available)."
        case .tagWrittenButLinkFailed(let detail):
            return "The tag was written, but PrintFarmer could not link its UID in Spoolman. \(detail)"
        case .cancelled:
            return "Scan cancelled."
        }
    }
}

// MARK: - Spool Scan Result

enum SpoolScanResult: Sendable {
    case spoolId(Int)
    case printerId(UUID)
    case newSpoolData(ScannedSpoolData)
    case nfcTag(ScannedNFCTag)
    case cancelled
    case error(SpoolScanError)
}

enum NFCTagTechnology: String, Sendable {
    case iso14443
    case iso15693
}

enum LegacyNFCTagContent: Sendable {
    case spoolId(Int)
    case printerId(UUID)
    case newSpoolData(ScannedSpoolData)
}

struct ScannedNFCTag: Identifiable, Sendable {
    let uid: String
    let technology: NFCTagTechnology
    let openPrintTagPayload: Data?
    let legacyContent: LegacyNFCTagContent?

    var id: String { uid }
}

// MARK: - Scanned Spool Data

struct ScannedSpoolData: Sendable {
    let material: String?
    let colorHex: String?
    let vendor: String?
    let weight: Double?
    let diameter: Double?
    let temperature: Int?
    let spoolmanId: Int?
    let filamentId: Int?

    init(
        material: String?,
        colorHex: String?,
        vendor: String?,
        weight: Double?,
        diameter: Double?,
        temperature: Int?,
        spoolmanId: Int?,
        filamentId: Int? = nil
    ) {
        self.material = material
        self.colorHex = colorHex
        self.vendor = vendor
        self.weight = weight
        self.diameter = diameter
        self.temperature = temperature
        self.spoolmanId = spoolmanId
        self.filamentId = filamentId
    }
}

// MARK: - Spool Scanner Protocol

/// Contract for spool scanning (QR code or NFC).
/// ViewModels depend on this protocol; concrete services implement device-specific scanning.
protocol SpoolScannerProtocol: AnyObject, Sendable {
    var isAvailable: Bool { get }
    func scan() async -> SpoolScanResult
}
