import Foundation
@testable import PrintFarmer

final class MockPrinterService: PrinterServiceProtocol, @unchecked Sendable {
    var printersToReturn: [Printer] = []
    var printerToReturn: Printer?
    var statusToReturn: PrinterStatusDetail?
    var cameraUrlsToReturn: [PrinterCameraUrls] = []
    var cameraUrlToReturn: PrinterCameraUrl?
    var currentJobToReturn: PrintJobStatusInfo?
    var commandResultToReturn = CommandResult(success: true, message: nil)
    var snapshotDataToReturn = Data()
    var queueOverviewToReturn: [QueueOverview] = []
    var spoolsToReturn: [SpoolmanSpool] = []
    var errorToThrow: Error?

    // Call tracking
    var listPrintersCalled = false
    var listIncludeDisabledArg: Bool?
    var getPrinterCalledWith: UUID?
    var getStatusCalledWith: UUID?
    var listCameraUrlsCalled = false
    var getCameraUrlCalledWith: UUID?
    var getSnapshotCalledWith: UUID?
    var getSnapshotCallCount = 0
    var getCurrentJobCalledWith: UUID?
    var pauseCalledWith: UUID?
    var resumeCalledWith: UUID?
    var cancelCalledWith: UUID?
    var stopCalledWith: UUID?
    var emergencyStopCalledWith: UUID?
    var maintenanceCalledWith: (id: UUID, inMaintenance: Bool)?
    var queueOverviewCalled = false
    var setActiveSpoolCalledWith: (printerId: UUID, spoolId: Int?)?
    var listAvailableSpoolsCalledWith: UUID?
    var loadFilamentCalledWith: UUID?
    var unloadFilamentCalledWith: UUID?
    var changeFilamentCalledWith: UUID?
    var setTemperaturesCalledWith: (printerId: UUID, hotend: Double?, bed: Double?)?
    var homeCalledWith: (printerId: UUID, axes: [String])?
    var homeXYCalledWith: UUID?
    var homeZCalledWith: UUID?
    var moveCalledWith: (printerId: UUID, axis: String, distanceMm: Double, feedrateMmMin: Int)?

    func list(includeDisabled: Bool = false) async throws -> [Printer] {
        listPrintersCalled = true
        listIncludeDisabledArg = includeDisabled
        if let error = errorToThrow { throw error }
        return printersToReturn
    }

    func get(id: UUID) async throws -> Printer {
        getPrinterCalledWith = id
        if let error = errorToThrow { throw error }
        guard let printer = printerToReturn else { throw NetworkError.notFound }
        return printer
    }

    func getStatus(id: UUID) async throws -> PrinterStatusDetail {
        getStatusCalledWith = id
        if let error = errorToThrow { throw error }
        guard let status = statusToReturn else { throw NetworkError.notFound }
        return status
    }

    func listCameraUrls() async throws -> [PrinterCameraUrls] {
        listCameraUrlsCalled = true
        if let error = errorToThrow { throw error }
        return cameraUrlsToReturn
    }

    func getCameraUrl(id: UUID) async throws -> PrinterCameraUrl {
        getCameraUrlCalledWith = id
        if let error = errorToThrow { throw error }
        guard let cameraUrl = cameraUrlToReturn else { throw NetworkError.notFound }
        return cameraUrl
    }

    func getSnapshot(id: UUID) async throws -> Data {
        getSnapshotCalledWith = id
        getSnapshotCallCount += 1
        if let error = errorToThrow { throw error }
        return snapshotDataToReturn
    }

    func getCurrentJob(id: UUID) async throws -> PrintJobStatusInfo? {
        getCurrentJobCalledWith = id
        if let error = errorToThrow { throw error }
        return currentJobToReturn
    }

    func pause(id: UUID) async throws -> CommandResult {
        pauseCalledWith = id
        if let error = errorToThrow { throw error }
        return commandResultToReturn
    }

    func resume(id: UUID) async throws -> CommandResult {
        resumeCalledWith = id
        if let error = errorToThrow { throw error }
        return commandResultToReturn
    }

    func cancel(id: UUID) async throws -> CommandResult {
        cancelCalledWith = id
        if let error = errorToThrow { throw error }
        return commandResultToReturn
    }

    func stop(id: UUID) async throws -> CommandResult {
        stopCalledWith = id
        if let error = errorToThrow { throw error }
        return commandResultToReturn
    }

    func emergencyStop(id: UUID) async throws -> CommandResult {
        emergencyStopCalledWith = id
        if let error = errorToThrow { throw error }
        return commandResultToReturn
    }

    func setMaintenanceMode(id: UUID, inMaintenance: Bool) async throws -> Printer {
        maintenanceCalledWith = (id, inMaintenance)
        if let error = errorToThrow { throw error }
        guard let printer = printerToReturn else { throw NetworkError.notFound }
        return printer
    }

    func getQueueOverview(model: String?, nozzle: Double?, material: String?) async throws -> [QueueOverview] {
        queueOverviewCalled = true
        if let error = errorToThrow { throw error }
        return queueOverviewToReturn
    }

    func setActiveSpool(printerId: UUID, spoolId: Int?) async throws -> CommandResult {
        setActiveSpoolCalledWith = (printerId, spoolId)
        if let error = errorToThrow { throw error }
        return commandResultToReturn
    }

    func listAvailableSpools(printerId: UUID) async throws -> [SpoolmanSpool] {
        listAvailableSpoolsCalledWith = printerId
        if let error = errorToThrow { throw error }
        return spoolsToReturn
    }

    func loadFilament(printerId: UUID) async throws -> CommandResult {
        loadFilamentCalledWith = printerId
        if let error = errorToThrow { throw error }
        return commandResultToReturn
    }

    func unloadFilament(printerId: UUID) async throws -> CommandResult {
        unloadFilamentCalledWith = printerId
        if let error = errorToThrow { throw error }
        return commandResultToReturn
    }

    func changeFilament(printerId: UUID) async throws -> CommandResult {
        changeFilamentCalledWith = printerId
        if let error = errorToThrow { throw error }
        return commandResultToReturn
    }

    var capabilitiesToReturn: PrinterBackendCapabilities?
    var getBackendCapabilitiesCalledWith: UUID?

    func getBackendCapabilities(printerId: UUID) async throws -> PrinterBackendCapabilities {
        getBackendCapabilitiesCalledWith = printerId
        if let error = errorToThrow { throw error }
        return capabilitiesToReturn ?? PrinterBackendCapabilities.fallback(for: .moonraker)
    }

    var beforeSetTemperatures: (@Sendable () async -> Void)?

    func setTemperatures(printerId: UUID, hotend: Double?, bed: Double?) async throws {
        if let hook = beforeSetTemperatures { await hook() }
        setTemperaturesCalledWith = (printerId, hotend, bed)
        if let error = errorToThrow { throw error }
    }

    func home(printerId: UUID, axes: [String]) async throws {
        homeCalledWith = (printerId, axes)
        if let error = errorToThrow { throw error }
    }

    func homeXY(printerId: UUID) async throws {
        homeXYCalledWith = printerId
        if let error = errorToThrow { throw error }
    }

    func homeZ(printerId: UUID) async throws {
        homeZCalledWith = printerId
        if let error = errorToThrow { throw error }
    }

    func move(printerId: UUID, axis: String, distanceMm: Double, feedrateMmMin: Int) async throws {
        moveCalledWith = (printerId, axis, distanceMm, feedrateMmMin)
        if let error = errorToThrow { throw error }
    }

    func reset() {
        printersToReturn = []
        printerToReturn = nil
        statusToReturn = nil
        cameraUrlsToReturn = []
        cameraUrlToReturn = nil
        currentJobToReturn = nil
        commandResultToReturn = CommandResult(success: true, message: nil)
        errorToThrow = nil
        listPrintersCalled = false
        listIncludeDisabledArg = nil
        getPrinterCalledWith = nil
        getStatusCalledWith = nil
        listCameraUrlsCalled = false
        getCameraUrlCalledWith = nil
        getSnapshotCalledWith = nil
        getSnapshotCallCount = 0
        getCurrentJobCalledWith = nil
        pauseCalledWith = nil
        resumeCalledWith = nil
        cancelCalledWith = nil
        stopCalledWith = nil
        emergencyStopCalledWith = nil
        maintenanceCalledWith = nil
        queueOverviewCalled = false
        setActiveSpoolCalledWith = nil
        listAvailableSpoolsCalledWith = nil
        loadFilamentCalledWith = nil
        unloadFilamentCalledWith = nil
        changeFilamentCalledWith = nil
        setTemperaturesCalledWith = nil
        homeCalledWith = nil
        homeXYCalledWith = nil
        homeZCalledWith = nil
        moveCalledWith = nil
        spoolsToReturn = []
    }
}
