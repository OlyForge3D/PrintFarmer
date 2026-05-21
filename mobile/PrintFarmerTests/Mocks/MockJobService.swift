import Foundation
@testable import PrintFarmer

final class MockJobService: JobServiceProtocol, @unchecked Sendable {
    var queueOverviewsToReturn: [QueueOverview] = []
    var queuedJobResponsesToReturn: [QueuedPrintJobResponse] = []
    var jobToReturn: PrintJob?
    var errorToThrow: Error?

    // Call tracking
    var listJobsCalled = false
    var listAllJobsCalled = false
    var getJobCalledWith: UUID?
    var createCalledWith: CreatePrintJobRequest?
    var updateCalledWith: (id: UUID, request: UpdatePrintJobRequest)?
    var deleteCalledWith: UUID?
    var cancelCalledWith: UUID?
    var dispatchCalledWith: UUID?
    var abortCalledWith: UUID?
    var pauseCalledWith: UUID?
    var resumeCalledWith: UUID?

    func list() async throws -> [QueueOverview] {
        listJobsCalled = true
        if let error = errorToThrow { throw error }
        return queueOverviewsToReturn
    }

    func listAllJobs() async throws -> [QueuedPrintJobResponse] {
        listAllJobsCalled = true
        if let error = errorToThrow { throw error }
        return queuedJobResponsesToReturn
    }

    func get(id: UUID) async throws -> PrintJob {
        getJobCalledWith = id
        if let error = errorToThrow { throw error }
        guard let job = jobToReturn else { throw NetworkError.notFound }
        return job
    }

    func create(_ request: CreatePrintJobRequest) async throws -> PrintJob {
        createCalledWith = request
        if let error = errorToThrow { throw error }
        guard let job = jobToReturn else { throw NetworkError.notFound }
        return job
    }

    func update(id: UUID, _ request: UpdatePrintJobRequest) async throws -> PrintJob {
        updateCalledWith = (id, request)
        if let error = errorToThrow { throw error }
        guard let job = jobToReturn else { throw NetworkError.notFound }
        return job
    }

    func delete(id: UUID) async throws {
        deleteCalledWith = id
        if let error = errorToThrow { throw error }
    }

    func cancel(id: UUID) async throws {
        cancelCalledWith = id
        if let error = errorToThrow { throw error }
    }

    func dispatch(id: UUID) async throws {
        dispatchCalledWith = id
        if let error = errorToThrow { throw error }
    }

    func abort(id: UUID) async throws {
        abortCalledWith = id
        if let error = errorToThrow { throw error }
    }

    func pause(id: UUID) async throws {
        pauseCalledWith = id
        if let error = errorToThrow { throw error }
    }

    func resume(id: UUID) async throws {
        resumeCalledWith = id
        if let error = errorToThrow { throw error }
    }

    func reset() {
        queueOverviewsToReturn = []
        queuedJobResponsesToReturn = []
        jobToReturn = nil
        errorToThrow = nil
        listJobsCalled = false
        listAllJobsCalled = false
        getJobCalledWith = nil
        createCalledWith = nil
        updateCalledWith = nil
        deleteCalledWith = nil
        cancelCalledWith = nil
        dispatchCalledWith = nil
        abortCalledWith = nil
        pauseCalledWith = nil
        resumeCalledWith = nil
    }
}
