using System.ComponentModel.DataAnnotations;

namespace Farm.Infrastructure.Domain;

/// <summary>
/// A printed-product SKU tracked on the shop floor.
/// This is DISTINCT from <see cref="MaintenanceComponent"/>, which represents
/// replacement parts used to service printers.
/// Printed-part stock changes are recorded exclusively via
/// <see cref="PartInventoryAdjustment"/> entries; <see cref="OnHand"/> is a
/// denormalized aggregate maintained inside the same transaction as each
/// adjustment.
/// </summary>
public class PartInventory
{
    public Guid Id { get; set; }

    /// <summary>Concurrency token to serialize competing adjust/harvest writers.</summary>
    [Timestamp]
    public byte[]? RowVersion { get; set; }

    /// <summary>
    /// Operator-owned unique identifier for the printed part (e.g. "PF-BRKT-01").
    /// Used as the canonical URL segment for the parts-inventory API.
    /// </summary>
    [Required]
    [MaxLength(64)]
    public string Sku { get; set; } = string.Empty;

    [Required]
    [MaxLength(200)]
    public string Name { get; set; } = string.Empty;

    [MaxLength(2000)]
    public string? Description { get; set; }

    /// <summary>
    /// Optional pointer to the source model (path, URL, or gcode/project file id).
    /// Purely informational; harvest mapping goes through <see cref="PartOutputMapping"/>.
    /// </summary>
    [MaxLength(500)]
    public string? ModelFileRef { get; set; }

    /// <summary>Default bin used when a harvest omits an explicit bin.</summary>
    public Guid? DefaultBinId { get; set; }

    public Bin? DefaultBin { get; set; }

    /// <summary>
    /// Denormalized on-hand count. Always equals the signed sum of adjustment deltas
    /// for this SKU; both are written inside the same transaction.
    /// </summary>
    public int OnHand { get; set; }

    /// <summary>
    /// Minimum on-hand threshold. When <see cref="OnHand"/> drops below this value,
    /// the reorder evaluation surfaces the SKU for the F8 shift compiler.
    /// </summary>
    public int ReorderPoint { get; set; }

    /// <summary>Soft-delete flag; inactive SKUs are hidden from operator lists.</summary>
    public bool IsActive { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<PartInventoryAdjustment> Adjustments { get; set; } = new List<PartInventoryAdjustment>();

    public ICollection<PartOutputMapping> Mappings { get; set; } = new List<PartOutputMapping>();
}
