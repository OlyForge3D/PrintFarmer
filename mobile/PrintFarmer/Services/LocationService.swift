import Foundation

// MARK: - Location Service

actor LocationService: LocationServiceProtocol {
    private let apiClient: APIClient

    init(apiClient: APIClient) {
        self.apiClient = apiClient
    }

    func list() async throws -> [Location] {
        try await apiClient.get("/api/locations")
    }

    func get(id: UUID) async throws -> Location {
        try await apiClient.get("/api/locations/\(id)")
    }

    func create(_ request: CreateLocationRequest) async throws -> Location {
        try await apiClient.post("/api/locations", body: request)
    }

    func update(id: UUID, _ request: UpdateLocationRequest) async throws -> Location {
        try await apiClient.put("/api/locations/\(id)", body: request)
    }

    func delete(id: UUID) async throws {
        try await apiClient.delete("/api/locations/\(id)")
    }
}
