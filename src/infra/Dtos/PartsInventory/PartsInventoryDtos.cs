using System.ComponentModel.DataAnnotations;
using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure.Dtos.PartsInventory;

/// <summary>Response DTO for a printed-part SKU.</summary>
public record PartInventoryResponse(
    Guid Id,
    string Sku,
    string Name,
    string? Description,
    string? ModelFileRef,
    Guid? DefaultBinId,
    string? DefaultBinCode,
    string? DefaultBinName,
    int OnHand,
    int ReorderPoint,
    bool NeedsReorder,
    bool IsActive,
    DateTime CreatedAt,
    DateTime UpdatedAt);

/// <summary>Response DTO for a printed-part storage bin.</summary>
public record BinResponse(
    Guid Id,
    string Code,
    string Name,
    string? Location,
    string? Notes,
    bool IsActive,
    DateTime CreatedAt,
    DateTime UpdatedAt);

/// <summary>Response DTO for a single ledger entry.</summary>
public record PartAdjustmentResponse(
    Guid Id,
    Guid PartInventoryId,
    string Sku,
    Guid? BinId,
    string? BinCode,
    int Delta,
    PartAdjustmentReason Reason,
    Guid? PrintJobId,
    string? OperationKey,
    string? Notes,
    string? UserId,
    DateTime CreatedAt);

/// <summary>Response DTO for a job-output → SKU mapping.</summary>
public record PartOutputMappingResponse(
    Guid Id,
    Guid PartInventoryId,
    string Sku,
    Guid? GcodeFileId,
    Guid? PrintProjectFileId,
    int Quantity,
    DateTime CreatedAt,
    DateTime UpdatedAt);

/// <summary>Response DTO for the harvest action on a completed print job.</summary>
public record HarvestJobResponse(
    Guid PrintJobId,
    DateTime HarvestedAt,
    Guid? BinId,
    string? BinCode,
    bool AlreadyHarvested,
    IReadOnlyList<PartAdjustmentResponse> Adjustments);

/// <summary>Item in a caller-supplied harvest override / mapping fallback.</summary>
public record HarvestOutputRequestItem(
    [Required, MinLength(1), MaxLength(64)] string Sku,
    [Range(1, 10000)] int Quantity);

/// <summary>Request body for harvesting a completed job into printed-part stock.</summary>
public record HarvestJobRequest(
    string? BinCode = null,
    int? QuantityOverride = null,
    IReadOnlyList<HarvestOutputRequestItem>? Outputs = null,
    string? OperationKey = null);

/// <summary>Request body for adjusting a printed-part SKU stock level.</summary>
public record AdjustPartInventoryRequest(
    [Range(-10000, 10000)] int Delta,
    PartAdjustmentReason Reason,
    Guid? JobId = null,
    string? BinCode = null,
    string? Notes = null,
    string? OperationKey = null);

/// <summary>Request body for creating a printed-part SKU.</summary>
public record CreatePartInventoryRequest(
    [Required, MinLength(1), MaxLength(64)] string Sku,
    [Required, MinLength(1), MaxLength(200)] string Name,
    [MaxLength(2000)] string? Description = null,
    [MaxLength(500)] string? ModelFileRef = null,
    string? DefaultBinCode = null,
    [Range(0, int.MaxValue)] int InitialOnHand = 0,
    [Range(0, int.MaxValue)] int ReorderPoint = 0);

/// <summary>Request body for updating a printed-part SKU's mutable metadata.</summary>
public record UpdatePartInventoryRequest(
    [Required, MinLength(1), MaxLength(200)] string Name,
    [MaxLength(2000)] string? Description = null,
    [MaxLength(500)] string? ModelFileRef = null,
    string? DefaultBinCode = null,
    [Range(0, int.MaxValue)] int ReorderPoint = 0,
    bool IsActive = true);

/// <summary>Request body for creating a bin. <c>Code</c> doubles as the barcode.</summary>
public record CreateBinRequest(
    [Required, MinLength(1), MaxLength(128)] string Code,
    [Required, MinLength(1), MaxLength(200)] string Name,
    [MaxLength(200)] string? Location = null,
    [MaxLength(1000)] string? Notes = null);

/// <summary>Request body for updating a bin.</summary>
public record UpdateBinRequest(
    [Required, MinLength(1), MaxLength(200)] string Name,
    [MaxLength(200)] string? Location = null,
    [MaxLength(1000)] string? Notes = null,
    bool IsActive = true);

/// <summary>
/// Request body for the bin registration endpoint that reuses the shared
/// barcode infrastructure. If a bin with the supplied code exists it is
/// returned; otherwise a new bin is created with the supplied name/location.
/// </summary>
public record RegisterBinBarcodeRequest(
    [Required, MinLength(1), MaxLength(128)] string Code,
    [MaxLength(200)] string? Name = null,
    [MaxLength(200)] string? Location = null);

/// <summary>Request body for creating a job-output → SKU mapping.</summary>
public record CreatePartOutputMappingRequest(
    [Required, MinLength(1), MaxLength(64)] string Sku,
    Guid? GcodeFileId = null,
    Guid? PrintProjectFileId = null,
    [Range(1, 10000)] int Quantity = 1);

/// <summary>Reorder-evaluation entry consumed by the F8 shift compiler.</summary>
public record ReorderCandidateResponse(
    Guid PartInventoryId,
    string Sku,
    string Name,
    int OnHand,
    int ReorderPoint,
    int Deficit);
