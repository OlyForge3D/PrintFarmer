import Foundation

// MARK: - Spool Service

actor SpoolService: SpoolServiceProtocol {
    private let apiClient: APIClient

    init(apiClient: APIClient) {
        self.apiClient = apiClient
    }

    func listSpools(
        limit: Int = 50, offset: Int = 0,
        search: String? = nil, material: String? = nil, vendor: String? = nil
    ) async throws -> SpoolmanPagedResult<SpoolmanSpool> {
        var params: [String] = []
        params.append("limit=\(limit)")
        params.append("offset=\(offset)")
        if let search { params.append("search=\(search)") }
        if let material { params.append("material=\(material)") }
        if let vendor { params.append("vendor=\(vendor)") }
        let query = params.isEmpty ? "" : "?\(params.joined(separator: "&"))"
        return try await apiClient.get("/api/spoolman/spools\(query)")
    }

    func createSpool(_ request: SpoolmanSpoolRequest) async throws -> SpoolmanSpool {
        try await apiClient.post("/api/spoolman/spools", body: request)
    }

    func updateSpool(id: Int, _ request: SpoolmanSpoolRequest) async throws -> SpoolmanSpool {
        try await apiClient.patch("/api/spoolman/spools/\(id)", body: request)
    }

    func deleteSpool(id: Int) async throws {
        try await apiClient.delete("/api/spoolman/spools/\(id)")
    }

    func listFilaments() async throws -> [SpoolmanFilament] {
        try await apiClient.get("/api/spoolman/filaments")
    }

    func listVendors() async throws -> [SpoolmanVendor] {
        try await apiClient.get("/api/spoolman/vendors")
    }

    func listMaterials() async throws -> [SpoolmanMaterial] {
        try await apiClient.get("/api/spoolman/materials")
    }

    func listAvailableMaterials() async throws -> [String] {
        try await apiClient.get("/api/spoolman/materials/available")
    }
}
