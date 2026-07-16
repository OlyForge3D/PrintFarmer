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
    func createFilament(_ request: SpoolmanFilamentRequest) async throws -> SpoolmanFilament
    func listVendors() async throws -> [SpoolmanVendor]
    func listMaterials() async throws -> [SpoolmanMaterial]
    func listAvailableMaterials() async throws -> [String]
}

// Convenience overloads
extension SpoolServiceProtocol {
    func listSpools(limit: Int = 50, offset: Int = 0) async throws -> SpoolmanPagedResult<SpoolmanSpool> {
        try await listSpools(limit: limit, offset: offset, search: nil, material: nil, vendor: nil)
    }

    /// Active-server existence check for a specific spool ID (#714 Item
    /// C: scan-station spool routing must confirm the ID actually exists
    /// on the currently-connected server before navigating to
    /// `.spoolDetail`, rather than trusting a scanned/parsed ID blindly).
    ///
    /// There is no dedicated single-spool GET endpoint server-side (only
    /// paginated list, create, patch, delete) — introducing one would be
    /// a backend change, which is out of scope here. Instead this fetches
    /// the largest single page the backend allows (`limit` is clamped to
    /// 500 server-side) and checks for an exact ID match, which covers
    /// the realistic size of a single print farm's spool library without
    /// any backend change. Any thrown error (network/server) propagates
    /// to the caller unchanged so it can be distinguished from a genuine
    /// "not found".
    func spoolExists(id: Int) async throws -> Bool {
        let page = try await listSpools(limit: 500, offset: 0, search: nil, material: nil, vendor: nil)
        return page.items.contains { $0.id == id }
    }
}
