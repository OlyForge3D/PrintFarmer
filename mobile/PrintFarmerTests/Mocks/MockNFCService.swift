import Foundation
@testable import PrintFarmer

// MARK: - Mock NFC Service

final class MockNFCService: SpoolScannerProtocol, @unchecked Sendable {
    var resultToReturn: SpoolScanResult = .cancelled
    var available = true
    var writeTagError: Error?

    private(set) var scanCallCount = 0
    private(set) var writeTagCallCount = 0
    private(set) var lastWrittenSpool: SpoolmanSpool?

    var isAvailable: Bool { available }

    func scan() async -> SpoolScanResult {
        scanCallCount += 1
        return resultToReturn
    }

    func writeTag(spool: SpoolmanSpool) async throws {
        writeTagCallCount += 1
        lastWrittenSpool = spool
        if let error = writeTagError {
            throw error
        }
    }

    func reset() {
        resultToReturn = .cancelled
        available = true
        writeTagError = nil
        scanCallCount = 0
        writeTagCallCount = 0
        lastWrittenSpool = nil
    }
}
