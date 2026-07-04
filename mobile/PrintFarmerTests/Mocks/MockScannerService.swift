import Foundation
@testable import PrintFarmer

/// Configurable mock for SpoolScannerProtocol.
/// Allows tests to set the scan result and availability, and tracks calls.
final class MockScannerService: SpoolScannerProtocol, BarcodeScannerProtocol, @unchecked Sendable {

    var scanResultToReturn: SpoolScanResult = .cancelled
    var barcodeScanResultToReturn: BarcodeScanResult = .cancelled
    var mockIsAvailable: Bool = true
    var scanCallCount = 0
    var barcodeScanCallCount = 0

    var isAvailable: Bool { mockIsAvailable }

    func scan() async -> SpoolScanResult {
        scanCallCount += 1
        return scanResultToReturn
    }

    func scanBarcode() async -> BarcodeScanResult {
        barcodeScanCallCount += 1
        return barcodeScanResultToReturn
    }

    func reset() {
        scanResultToReturn = .cancelled
        barcodeScanResultToReturn = .cancelled
        mockIsAvailable = true
        scanCallCount = 0
        barcodeScanCallCount = 0
    }
}
