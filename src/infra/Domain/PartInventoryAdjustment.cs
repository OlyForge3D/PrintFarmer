using System.ComponentModel.DataAnnotations;

namespace Farm.Infrastructure.Domain;

/// <summary>
/// Reason a printed-part stock adjustment was recorded.
/// Serialized as string in API and DB to keep enum evolution safe.
/// </summary>
public enum PartAdjustmentReason
{
    /// <summary>Positive delta from a plate being harvested off a printer.</summary>
    Harvest = 0,

    /// <summary>Negative delta when a printed part failed QC and was scrapped.</summary>
    QcReject = 1,

    /// <summary>Manual correction (miscount, adjustment, cycle count).</summary>
    Manual = 2,

    /// <summary>Initial seed count for a newly registered SKU.</summary>
    InitialStock = 3,

    /// <summary>Positive delta representing outbound shipment or use elsewhere.</summary>
    Consumption = 4,
}

/// <summary>
/// Immutable ledger entry describing a single change to printed-part stock.
/// Adjustments are never mutated after creation; corrections are recorded as
/// additional adjustments. <see cref="PartInventory.OnHand"/> is the running
/// sum of every non-void adjustment for the SKU.
/// </summary>
public class PartInventoryAdjustment
{
    public Guid Id { get; set; }

    public Guid PartInventoryId { get; set; }

    public PartInventory? PartInventory { get; set; }

    /// <summary>Optional target/source bin for the adjustment.</summary>
    public Guid? BinId { get; set; }

    public Bin? Bin { get; set; }

    /// <summary>
    /// Signed change to on-hand stock. Positive = added stock,
    /// negative = removed stock. Zero deltas are rejected by the service layer.
    /// </summary>
    public int Delta { get; set; }

    public PartAdjustmentReason Reason { get; set; }

    /// <summary>Optional link back to the print job that produced this delta.</summary>
    public Guid? PrintJobId { get; set; }

    public PrintJob? PrintJob { get; set; }

    /// <summary>
    /// Idempotency key. When two adjustments share the same key (per SKU)
    /// only the first is applied — used by harvest and by client retries.
    /// </summary>
    [MaxLength(128)]
    public string? OperationKey { get; set; }

    [MaxLength(1000)]
    public string? Notes { get; set; }

    [MaxLength(450)]
    public string? UserId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
