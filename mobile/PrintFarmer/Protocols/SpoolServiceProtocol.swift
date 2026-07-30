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
    /// a backend change, which is out of scope here. Instead this pages
    /// through `listSpools` at the largest page size the backend allows
    /// (`limit` is clamped to 500 server-side), advancing `offset` by the
    /// number of items actually returned, until either the ID is found
    /// (short-circuits immediately — no further pages fetched) or every
    /// reported item (per the response's `totalCount`) has been checked.
    /// An empty page, or a page that fails to advance `offset` past
    /// `totalCount`, terminates the loop rather than looping forever, so a
    /// malformed/inconsistent server response can never hang this check.
    /// Any thrown error (network/server) propagates to the caller
    /// unchanged so it can be distinguished from a genuine "not found".
    func spoolExists(id: Int) async throws -> Bool {
        let pageSize = 500
        var offset = 0
        while true {
            let page = try await listSpools(limit: pageSize, offset: offset, search: nil, material: nil, vendor: nil)
            if page.items.contains(where: { $0.id == id }) {
                return true
            }
            let nextOffset = offset + page.items.count
            guard !page.items.isEmpty, nextOffset > offset, nextOffset < page.totalCount else {
                return false
            }
            offset = nextOffset
        }
    }
}
