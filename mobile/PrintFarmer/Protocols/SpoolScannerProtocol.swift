import Foundation

// MARK: - Spool Scan Error

enum SpoolScanError: Error, LocalizedError, Sendable {
    case permissionDenied
    case notSupported
    case invalidPayload(String)
    case spoolNotFound(Int)
    case networkError(Error)
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
        case .cancelled:
            return "Scan cancelled."
        }
    }
}

// MARK: - Spool Scan Result

enum SpoolScanResult: Sendable {
    case spoolId(Int)
    case newSpoolData(ScannedSpoolData)
    case cancelled
    case error(SpoolScanError)
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
}

// MARK: - Spool Scanner Protocol

/// Contract for spool scanning (QR code or NFC).
/// ViewModels depend on this protocol; concrete services implement device-specific scanning.
protocol SpoolScannerProtocol: AnyObject, Sendable {
    var isAvailable: Bool { get }
    func scan() async -> SpoolScanResult
}
