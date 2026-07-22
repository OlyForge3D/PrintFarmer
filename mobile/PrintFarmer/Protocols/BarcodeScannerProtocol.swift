import Foundation

// MARK: - Barcode Scan Result

enum BarcodeScanResult: Sendable {
    case barcode(String)
    case cancelled
    case error(SpoolScanError)
}

// MARK: - Barcode Scanner Protocol

protocol BarcodeScannerProtocol: AnyObject, Sendable {
    var isAvailable: Bool { get }
    func scanBarcode() async -> BarcodeScanResult
}
