import Foundation

// MARK: - Job Service Protocol

protocol JobServiceProtocol: Sendable {
    func list() async throws -> [QueueOverview]
    func listAllJobs() async throws -> [QueuedPrintJobResponse]
    func get(id: UUID) async throws -> PrintJob
    func create(_ request: CreatePrintJobRequest) async throws -> PrintJob
    func update(id: UUID, _ request: UpdatePrintJobRequest) async throws -> PrintJob
    func delete(id: UUID) async throws
    func dispatch(id: UUID) async throws
    func cancel(id: UUID) async throws
    func abort(id: UUID) async throws
    func pause(id: UUID) async throws
    func resume(id: UUID) async throws
}
