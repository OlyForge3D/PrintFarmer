import Foundation

// MARK: - Location Service Protocol

/// Contract for location operations.
protocol LocationServiceProtocol: Sendable {
    func list() async throws -> [Location]
    func get(id: UUID) async throws -> Location
    func create(_ request: CreateLocationRequest) async throws -> Location
    func update(id: UUID, _ request: UpdateLocationRequest) async throws -> Location
    func delete(id: UUID) async throws
}
