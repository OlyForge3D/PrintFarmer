import Foundation
@testable import PrintFarmer

// MARK: - Mock QR Spool Scanner Service

final class MockQRSpoolScannerService: SpoolScannerProtocol, @unchecked Sendable {
    var resultToReturn: SpoolScanResult = .cancelled
    var available = true

    private(set) var scanCallCount = 0

    var isAvailable: Bool { available }

    func scan() async -> SpoolScanResult {
        scanCallCount += 1
        return resultToReturn
    }

    func reset() {
        resultToReturn = .cancelled
        available = true
        scanCallCount = 0
    }
}
