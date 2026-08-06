import Foundation
@testable import PrintFarmer

final class MockAutoDispatchService: AutoDispatchServiceProtocol, @unchecked Sendable {
    var globalStatusToReturn: AutoDispatchGlobalStatus?
    var statusToReturn: AutoDispatchStatus?
    var readyResultToReturn: AutoDispatchReadyResult?
    var errorToThrow: Error?

    // Call tracking
    var getAllStatusCalled = false
    var getStatusCalledWith: UUID?
    var markReadyCalledWith: UUID?
    var confirmFilamentOverrideCalled = false
    var skipCalledWith: UUID?
    var cancelCalledWith: UUID?
    var preClearCalledWith: UUID?
    var setEnabledCalledWith: (printerId: UUID, request: SetAutoDispatchEnabledRequest)?

    func getAllStatus() async throws -> AutoDispatchGlobalStatus {
        getAllStatusCalled = true
        if let error = errorToThrow { throw error }
        return globalStatusToReturn ?? AutoDispatchGlobalStatus(globalEnabled: true, printers: [])
    }

    func getStatus(printerId: UUID) async throws -> AutoDispatchStatus {
        getStatusCalledWith = printerId
        if let error = errorToThrow { throw error }
        return statusToReturn!
    }

    func markReady(status: AutoDispatchStatus) async throws -> AutoDispatchReadyResult {
        markReadyCalledWith = status.printerId
        if let error = errorToThrow { throw error }
        return readyResultToReturn!
    }

    func confirmFilamentOverride(
        challenge: AutoDispatchReadyResult
    ) async throws -> AutoDispatchReadyResult {
        confirmFilamentOverrideCalled = true
        if let error = errorToThrow { throw error }
        return readyResultToReturn ?? challenge
    }

    func skip(status: AutoDispatchStatus) async throws -> AutoDispatchStatus {
        skipCalledWith = status.printerId
        if let error = errorToThrow { throw error }
        return statusToReturn!
    }

    func cancel(status: AutoDispatchStatus) async throws -> AutoDispatchStatus {
        cancelCalledWith = status.printerId
        if let error = errorToThrow { throw error }
        return statusToReturn!
    }

    func preClear(status: AutoDispatchStatus) async throws -> AutoDispatchStatus {
        preClearCalledWith = status.printerId
        if let error = errorToThrow { throw error }
        return statusToReturn!
    }

    func setEnabled(
        status: AutoDispatchStatus,
        request: SetAutoDispatchEnabledRequest
    ) async throws -> AutoDispatchStatus {
        setEnabledCalledWith = (status.printerId, request)
        if let error = errorToThrow { throw error }
        return statusToReturn!
    }
}
