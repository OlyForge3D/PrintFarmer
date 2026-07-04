import Foundation
@testable import PrintFarmer

/// Configurable mock for SpoolScannerProtocol.
/// Allows tests to set the scan result and availability, and tracks calls.
final class MockScannerService: SpoolScannerProtocol, BarcodeScannerProtocol, @unchecked Sendable {

    var scanResultToReturn: SpoolScanResult = .cancelled
    var barcodeScanResultToReturn: BarcodeScanResult = .cancelled
    var mockIsAvailable: Bool = true
    var barcodeScanDelayNanoseconds: UInt64 = 0
    var scanCallCount = 0
    var barcodeScanCallCount = 0

    var isAvailable: Bool { mockIsAvailable }

    func scan() async -> SpoolScanResult {
        scanCallCount += 1
        return scanResultToReturn
    }

    func scanBarcode() async -> BarcodeScanResult {
        barcodeScanCallCount += 1
        if barcodeScanDelayNanoseconds > 0 {
            try? await Task.sleep(nanoseconds: barcodeScanDelayNanoseconds)
        }
        return barcodeScanResultToReturn
    }

    func reset() {
        scanResultToReturn = .cancelled
        barcodeScanResultToReturn = .cancelled
        mockIsAvailable = true
        barcodeScanDelayNanoseconds = 0
        scanCallCount = 0
        barcodeScanCallCount = 0
    }
}
