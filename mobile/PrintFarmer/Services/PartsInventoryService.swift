import Foundation

// MARK: - Parts Inventory Service
//
// Thin repository over the printed-parts inventory / bins / harvest API
// merged in PR #741 (issue #714, F9). See `PartsInventoryServiceProtocol`
// for behavioral contracts and `PartsInventoryModels.swift` for wire types.

actor PartsInventoryService: PartsInventoryServiceProtocol {
    private let apiClient: APIClient

    init(apiClient: APIClient) {
        self.apiClient = apiClient
    }

    // MARK: - Parts

    func listParts(includeInactive: Bool = false) async throws -> [PartInventoryResponse] {
        let query = Self.encodeQuery([URLQueryItem(name: "includeInactive", value: String(includeInactive))])
        return try await apiClient.get("/api/parts-inventory\(query)")
    }

    func resolvePartByBarcode(_ sku: String) async throws -> PartInventoryResponse {
        let path = "/api/parts-inventory/by-barcode/\(Self.encodePathSegment(sku))"
        return try await apiClient.get(path)
    }

    func adjustPart(sku: String, request: AdjustPartInventoryRequest) async throws -> PartAdjustmentResponse {
        let path = "/api/parts-inventory/\(Self.encodePathSegment(sku))/adjust"
        return try await apiClient.post(path, body: request)
    }

    func reorderCandidates() async throws -> [ReorderCandidateResponse] {
        try await apiClient.get("/api/parts-inventory/reorder")
    }

    func mappings(sku: String? = nil) async throws -> [PartOutputMappingResponse] {
        var items: [URLQueryItem] = []
        if let sku, !sku.isEmpty {
            items.append(URLQueryItem(name: "sku", value: sku))
        }
        let query = Self.encodeQuery(items)
        return try await apiClient.get("/api/parts-inventory/mappings\(query)")
    }

    // MARK: - Bins

    func listBins(includeInactive: Bool = false) async throws -> [BinResponse] {
        let query = Self.encodeQuery([URLQueryItem(name: "includeInactive", value: String(includeInactive))])
        return try await apiClient.get("/api/bins\(query)")
    }

    func resolveBinByBarcode(_ code: String) async throws -> BinResponse {
        let path = "/api/bins/by-barcode/\(Self.encodePathSegment(code))"
        return try await apiClient.get(path)
    }

    func registerBin(code: String, name: String? = nil, location: String? = nil) async throws -> BinResponse {
        let request = RegisterBinBarcodeRequest(code: code, name: name, location: location)
        return try await apiClient.post("/api/bins/register", body: request)
    }

    // MARK: - Harvest

    func harvestJob(jobId: UUID, request: HarvestJobRequest) async throws -> HarvestJobResponse {
        let path = "/api/job-queue/\(jobId.uuidString)/harvest"
        return try await apiClient.post(path, body: request)
    }

    // MARK: - Path & query encoding

    /// Percent-encodes a SKU or bin code for use as a single path segment.
    /// Scanned barcodes may contain `/`, `:`, or other reserved characters
    /// that would otherwise be interpreted as path separators by the router.
    private static func encodePathSegment(_ segment: String) -> String {
        segment.addingPercentEncoding(withAllowedCharacters: .urlPathSegmentAllowedForPartsInventory)
            ?? segment
    }

    private static func encodeQuery(_ items: [URLQueryItem]) -> String {
        guard !items.isEmpty else { return "" }
        var components = URLComponents()
        components.queryItems = items
        return "?\(components.percentEncodedQuery ?? "")"
    }
}

// MARK: - Path-segment character set

private extension CharacterSet {
    /// RFC 3986 `pchar` minus the sub-delims that would confuse routing
    /// (`:`, `/`, `?`, `#`, `[`, `]`, `@`). Named distinctly from
    /// `AttentionService`'s private copy so both files can define their own
    /// scoped extension without a redeclaration conflict.
    static let urlPathSegmentAllowedForPartsInventory: CharacterSet = {
        var set = CharacterSet.urlPathAllowed
        set.remove(charactersIn: ":/?#[]@")
        return set
    }()
}
