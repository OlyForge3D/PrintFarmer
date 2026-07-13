using System.ComponentModel.DataAnnotations;

namespace Farm.Infrastructure.Domain;

/// <summary>Mapping source captured when a job is first dispatched.</summary>
public enum PartOutputMappingSourceKind
{
    ProjectFile = 0,
    GcodeFile = 1,
}

/// <summary>Source used to resolve a final harvested output.</summary>
public enum PartHarvestOutputOrigin
{
    ExplicitOutputs = 0,
    JobSnapshot = 1,
    ProjectMapping = 2,
    GcodeMapping = 3,
}

/// <summary>
/// Immutable printed-output definition captured at the first successful dispatch.
/// Mapping edits after dispatch cannot change these rows.
/// </summary>
public sealed class PrintJobPartOutputSnapshot
{
    public Guid Id { get; set; }

    public Guid PrintJobId { get; set; }

    public PrintJob? PrintJob { get; set; }

    public Guid PartInventoryId { get; set; }

    public PartInventory? PartInventory { get; set; }

    [Required]
    [MaxLength(64)]
    public string Sku { get; set; } = string.Empty;

    public int QuantityPerPrint { get; set; }

    public Guid? ExpectedBinId { get; set; }

    public Bin? ExpectedBin { get; set; }

    [MaxLength(128)]
    public string? ExpectedBinCode { get; set; }

    public PartOutputMappingSourceKind SourceKind { get; set; }

    public Guid SourceFileId { get; set; }

    public Guid SourceMappingId { get; set; }

    public int Sequence { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Immutable final output row committed with a harvest. Replays read these rows
/// rather than mutable mappings or request data.
/// </summary>
public sealed class PartHarvestOutputSnapshot
{
    public Guid Id { get; set; }

    public Guid PrintJobId { get; set; }

    public PrintJob? PrintJob { get; set; }

    public Guid PartInventoryId { get; set; }

    public PartInventory? PartInventory { get; set; }

    public Guid PartInventoryAdjustmentId { get; set; }

    public PartInventoryAdjustment? PartInventoryAdjustment { get; set; }

    public Guid? JobOutputSnapshotId { get; set; }

    [Required]
    [MaxLength(64)]
    public string Sku { get; set; } = string.Empty;

    public int Quantity { get; set; }

    public Guid? ExpectedBinId { get; set; }

    public Bin? ExpectedBin { get; set; }

    [MaxLength(128)]
    public string? ExpectedBinCode { get; set; }

    public Guid ActualBinId { get; set; }

    public Bin? ActualBin { get; set; }

    [Required]
    [MaxLength(128)]
    public string ActualBinCode { get; set; } = string.Empty;

    public PartHarvestOutputOrigin Origin { get; set; }

    public Guid? SourceFileId { get; set; }

    public Guid? SourceMappingId { get; set; }

    public bool OverrideApplied { get; set; }

    [MaxLength(1000)]
    public string? OverrideReason { get; set; }

    public int Sequence { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
