import Foundation

// MARK: - Demo Location Service

final class DemoLocationService: LocationServiceProtocol, @unchecked Sendable {

    func list() async throws -> [Location] {
        DemoData.locations
    }

    func get(id: UUID) async throws -> Location {
        guard let loc = DemoData.locations.first(where: { $0.id == id }) else {
            throw ServiceError.notImplemented("Location not found in demo data")
        }
        return loc
    }

    func create(_ request: CreateLocationRequest) async throws -> Location {
        throw ServiceError.notImplemented("create location — read-only in demo mode")
    }

    func update(id: UUID, _ request: UpdateLocationRequest) async throws -> Location {
        throw ServiceError.notImplemented("update location — read-only in demo mode")
    }

    func delete(id: UUID) async throws {}
}
