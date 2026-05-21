import Foundation

// MARK: - Spool Service Protocol

/// Contract for Spoolman spool operations. Lambert implements the concrete service;
/// Ripley's ViewModels depend only on this protocol.
protocol SpoolServiceProtocol: Sendable {
    func listSpools(limit: Int, offset: Int, search: String?, material: String?, vendor: String?) async throws -> SpoolmanPagedResult<SpoolmanSpool>
    func createSpool(_ request: SpoolmanSpoolRequest) async throws -> SpoolmanSpool
    func updateSpool(id: Int, _ request: SpoolmanSpoolRequest) async throws -> SpoolmanSpool
    func deleteSpool(id: Int) async throws
    func listFilaments() async throws -> [SpoolmanFilament]
    func listVendors() async throws -> [SpoolmanVendor]
    func listMaterials() async throws -> [SpoolmanMaterial]
    func listAvailableMaterials() async throws -> [String]
}

// Convenience overloads
extension SpoolServiceProtocol {
    func listSpools(limit: Int = 50, offset: Int = 0) async throws -> SpoolmanPagedResult<SpoolmanSpool> {
        try await listSpools(limit: limit, offset: offset, search: nil, material: nil, vendor: nil)
    }
}
