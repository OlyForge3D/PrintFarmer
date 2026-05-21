import Foundation
@testable import PrintFarmer

/// Configurable mock for SpoolScannerProtocol.
/// Allows tests to set the scan result and availability, and tracks calls.
final class MockScannerService: SpoolScannerProtocol, @unchecked Sendable {

    var scanResultToReturn: SpoolScanResult = .cancelled
    var mockIsAvailable: Bool = true
    var scanCallCount = 0

    var isAvailable: Bool { mockIsAvailable }

    func scan() async -> SpoolScanResult {
        scanCallCount += 1
        return scanResultToReturn
    }

    func reset() {
        scanResultToReturn = .cancelled
        mockIsAvailable = true
        scanCallCount = 0
    }
}
