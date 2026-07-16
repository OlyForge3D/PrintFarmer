import Foundation

// MARK: - Printed-Parts Inventory DTOs
//
// Mirrors the backend contract merged in PR #741 (issue #714, F9):
//   - Route: GET  /api/parts-inventory                       -> [PartInventoryResponse]
//   - Route: GET  /api/parts-inventory/{sku}                 -> PartInventoryResponse
//   - Route: GET  /api/parts-inventory/by-barcode/{sku}      -> PartInventoryResponse
//   - Route: POST /api/parts-inventory/{sku}/adjust          -> PartAdjustmentResponse
//   - Route: GET  /api/parts-inventory/reorder               -> [ReorderCandidateResponse]
//   - Route: GET  /api/parts-inventory/mappings?sku=         -> [PartOutputMappingResponse]
//   - Route: GET  /api/bins                                  -> [BinResponse]
//   - Route: GET  /api/bins/by-barcode/{code}                -> BinResponse
//   - Route: POST /api/bins/register                         -> BinResponse (200 or 201)
//   - Route: POST /api/job-queue/{id}/harvest                -> HarvestJobResponse
// Property names are camelCase; enum wire values follow each type's own
// converter (see notes below) rather than a single global policy.
// See src/infra/Dtos/PartsInventory/PartsInventoryDtos.cs and
// src/api/Infrastructure/PartsInventory/PartsInventoryProblemDetails.cs for
// the authoritative shapes.

/// A printed-part SKU tracked in inventory. Distinct from the existing
/// maintenance/replacement-parts domain (`MaintenanceComponentController`) —
/// this is stock *produced* by prints, not consumed to service printers.
struct PartInventoryResponse: Codable, Sendable, Equatable, Identifiable {
    let id: UUID
    let sku: String
    let name: String
    let description: String?
    let modelFileRef: String?
    let defaultBinId: UUID?
    let defaultBinCode: String?
    let defaultBinName: String?
    let onHand: Int
    let reorderPoint: Int
    let needsReorder: Bool
    let isActive: Bool
    let createdAt: Date
    let updatedAt: Date
}

/// A printed-part storage bin. `code` doubles as the scannable barcode.
struct BinResponse: Codable, Sendable, Equatable, Identifiable {
    let id: UUID
    let code: String
    let name: String
    let location: String?
    let notes: String?
    let isActive: Bool
    let createdAt: Date
    let updatedAt: Date
}

/// Reason a printed-part stock adjustment was recorded. Wire values are
/// kebab-case strings from a dedicated converter (NOT the app's global
/// `JsonStringEnumConverter`) — see `PartAdjustmentReasonConverter` in
/// `PartInventoryAdjustment.cs`.
enum PartAdjustmentReason: String, Codable, Sendable, Equatable {
    /// Positive delta from a plate being harvested off a printer.
    case harvest
    /// Negative delta when a printed part failed QC and was scrapped.
    case qcReject = "qc-reject"
    /// Manual correction (miscount, adjustment, cycle count).
    case manual
}

/// A single immutable ledger entry for a SKU's stock.
struct PartAdjustmentResponse: Codable, Sendable, Equatable, Identifiable {
    let id: UUID
    let partInventoryId: UUID
    let sku: String
    let binId: UUID?
    let binCode: String?
    let delta: Int
    let resultingBalance: Int
    let reason: PartAdjustmentReason
    let printJobId: UUID?
    let operationKey: String?
    let notes: String?
    let userId: String?
    let createdAt: Date
}

/// A job-output → SKU mapping (either a G-code file or a print-project file
/// maps to a printed-part SKU with a per-output quantity).
struct PartOutputMappingResponse: Codable, Sendable, Equatable, Identifiable {
    let id: UUID
    let partInventoryId: UUID
    let sku: String
    let gcodeFileId: UUID?
    let printProjectFileId: UUID?
    let quantity: Int
    let createdAt: Date
    let updatedAt: Date
}

/// Source used to resolve a final harvested output. Wire values are the
/// exact C# enum member names (PascalCase) — this enum has no dedicated
/// converter, so it goes through the app's global `JsonStringEnumConverter`
/// with no naming policy applied (see `PartHarvestOutputOrigin` in
/// `PartOutputSnapshots.cs`).
enum PartHarvestOutputOrigin: String, Codable, Sendable, Equatable {
    case explicitOutputs = "ExplicitOutputs"
    case jobSnapshot = "JobSnapshot"
    case projectMapping = "ProjectMapping"
    case gcodeMapping = "GcodeMapping"
    /// Forward-compatibility bucket for origins the client does not
    /// recognise.
    case unknown

    init(from decoder: Decoder) throws {
        let raw = try decoder.singleValueContainer().decode(String.self)
        self = PartHarvestOutputOrigin(rawValue: raw) ?? .unknown
    }
}

/// A single persisted final output from a successful or replayed harvest.
struct HarvestOutputResponse: Codable, Sendable, Equatable, Identifiable {
    let sequence: Int
    let partInventoryId: UUID
    let partSku: String
    let quantity: Int
    let expectedBinId: UUID?
    let expectedBinCode: String?
    let actualBinId: UUID
    let actualBinCode: String
    let origin: PartHarvestOutputOrigin
    let sourceFileId: UUID?
    let sourceMappingId: UUID?
    let overrideApplied: Bool
    let overrideReason: String?
    let createdAt: Date

    var id: Int { sequence }
}

/// Response body for a successful (or idempotently replayed)
/// `POST /api/job-queue/{id}/harvest`.
struct HarvestJobResponse: Codable, Sendable, Equatable {
    let printJobId: UUID
    let harvestedAt: Date
    let binId: UUID?
    let binCode: String?
    let alreadyHarvested: Bool
    let adjustments: [PartAdjustmentResponse]
    let outputs: [HarvestOutputResponse]
}

/// Reorder-evaluation entry: a SKU whose on-hand is below its reorder point.
struct ReorderCandidateResponse: Codable, Sendable, Equatable, Identifiable {
    let partInventoryId: UUID
    let sku: String
    let name: String
    let onHand: Int
    let reorderPoint: Int
    let deficit: Int

    var id: UUID { partInventoryId }
}

// MARK: - Typed 409 Conflict (wrongBin / partMappingRequired)

/// A single mismatch row in a `wrongBin` conflict: the SKU whose scanned
/// destination bin did not match its expected bin.
struct WrongBinMismatch: Codable, Sendable, Equatable {
    let partSku: String
    let expectedBinCode: String?
    let scannedBinCode: String
}

/// Typed 409 ProblemDetails body for the printed-parts harvest endpoints.
/// Only one of `mismatches` (code `wrongBin`) or the mapping-guidance fields
/// (code `partMappingRequired`) is populated per instance — see
/// `PartsInventoryProblemDetails.cs` for the exact extension keys.
struct PartsInventoryConflict: Codable, Sendable, Equatable {
    static let wrongBinCode = "wrongBin"
    static let partMappingRequiredCode = "partMappingRequired"

    let code: String
    let title: String?
    let detail: String?
    /// Populated when `code == wrongBinCode`.
    let mismatches: [WrongBinMismatch]?
    /// Populated when `code == partMappingRequiredCode`.
    let jobId: UUID?
    let projectFileId: UUID?
    let gcodeFileId: UUID?
    let guidance: String?

    var isWrongBin: Bool { code == Self.wrongBinCode }
    var isPartMappingRequired: Bool { code == Self.partMappingRequiredCode }
}

// MARK: - Requests

/// Per-SKU output override sent when confirming a harvest manually.
struct HarvestOutputRequestItem: Codable, Sendable, Equatable {
    let sku: String
    let quantity: Int
}

/// Per-SKU destination bin assignment for a multi-output harvest.
struct HarvestOutputBinRequest: Codable, Sendable, Equatable {
    let partSku: String
    let binCode: String
}

/// Request body for `POST /api/job-queue/{id}/harvest`.
struct HarvestJobRequest: Codable, Sendable, Equatable {
    var binCode: String?
    var quantityOverride: Int?
    var outputs: [HarvestOutputRequestItem]?
    var operationKey: String?
    var outputBins: [HarvestOutputBinRequest]?
    var allowWrongBin: Bool = false
    var overrideReason: String?
}

/// Request body for `POST /api/parts-inventory/{sku}/adjust`.
struct AdjustPartInventoryRequest: Codable, Sendable, Equatable {
    var delta: Int
    var reason: PartAdjustmentReason
    var jobId: UUID?
    var binCode: String?
    var notes: String?
    var operationKey: String?
}

/// Request body for `POST /api/bins/register`.
struct RegisterBinBarcodeRequest: Codable, Sendable, Equatable {
    var code: String
    var name: String?
    var location: String?
}
