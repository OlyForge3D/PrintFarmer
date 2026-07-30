import Foundation

// MARK: - Parts Inventory Service Protocol
//
// Wraps the printed-parts inventory / bins / harvest API merged in PR #741
// (issue #714, F9). See `PartsInventoryModels.swift` for wire types.
//
// This is distinct from `SpoolServiceProtocol` (filament spool inventory)
// and unrelated to `MaintenanceComponentService` (replacement parts used to
// service printers) — printed parts are stock *produced* by prints.

protocol PartsInventoryServiceProtocol: Sendable {
    /// Lists printed-part SKUs.
    ///
    /// A gated 404 with `code == "featureDisabled"` (#725) surfaces as
    /// `NetworkError.featureDisabled` so the caller can fall back to hiding
    /// the printed-parts UI instead of parsing localized error text.
    func listParts(includeInactive: Bool) async throws -> [PartInventoryResponse]

    /// Resolves a printed-part SKU by its scanned barcode. Throws
    /// `NetworkError.notFound` when no SKU matches, or `.featureDisabled`
    /// when the feature gate is off.
    func resolvePartByBarcode(_ sku: String) async throws -> PartInventoryResponse

    /// Lists printed-part storage bins.
    func listBins(includeInactive: Bool) async throws -> [BinResponse]

    /// Resolves a bin by its scanned barcode. Throws `NetworkError.notFound`
    /// when no bin matches that code.
    func resolveBinByBarcode(_ code: String) async throws -> BinResponse

    /// Registers a bin from a scanned barcode. Returns the existing bin if
    /// one already has this code, otherwise creates and returns a new one.
    /// Unlike SKU/bin CRUD, this route does not require `farm_admin`.
    func registerBin(code: String, name: String?, location: String?) async throws -> BinResponse

    /// Applies a signed adjustment to a SKU's stock (harvest, QC reject, or
    /// manual correction). Idempotent via `operationKey`.
    func adjustPart(sku: String, request: AdjustPartInventoryRequest) async throws -> PartAdjustmentResponse

    /// Lists SKUs whose on-hand is at or below their reorder point.
    func reorderCandidates() async throws -> [ReorderCandidateResponse]

    /// Lists job-output → SKU mappings, optionally filtered to a single SKU.
    /// Used client-side to prefill a harvest's expected quantity/SKU before
    /// the server resolves the authoritative outputs.
    func mappings(sku: String?) async throws -> [PartOutputMappingResponse]

    /// Harvests a completed print job into printed-part stock. Idempotent:
    /// replaying against an already-harvested job returns the original
    /// result (`alreadyHarvested == true`) without applying deltas twice.
    ///
    /// A `wrongBin` or `partMappingRequired` conflict surfaces as
    /// `NetworkError.partsInventoryConflict(_:)` with the exact adjudicated
    /// detail — callers must not synthesise their own conflict messaging.
    func harvestJob(jobId: UUID, request: HarvestJobRequest) async throws -> HarvestJobResponse
}

extension PartsInventoryServiceProtocol {
    func listParts() async throws -> [PartInventoryResponse] {
        try await listParts(includeInactive: false)
    }

    func listBins() async throws -> [BinResponse] {
        try await listBins(includeInactive: false)
    }

    func registerBin(code: String) async throws -> BinResponse {
        try await registerBin(code: code, name: nil, location: nil)
    }

    func mappings() async throws -> [PartOutputMappingResponse] {
        try await mappings(sku: nil)
    }
}
