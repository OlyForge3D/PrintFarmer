using System.ComponentModel.DataAnnotations;

namespace Farm.Infrastructure.Domain;

/// <summary>
/// A physical component/part used in maintenance (global inventory).
/// Examples: LM8UU Linear Bearing, GT2 Belt (1m), PEI Sheet, Noctua 4020 Fan.
/// </summary>
public class MaintenanceComponent
{
    public Guid Id { get; set; }

    /// <summary>
    /// Component name (e.g., "LM8UU Linear Bearing")
    /// </summary>
    [MaxLength(200)]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Manufacturer SKU or part number
    /// </summary>
    [MaxLength(100)]
    public string? Sku { get; set; }

    /// <summary>
    /// Detailed description
    /// </summary>
    [MaxLength(1000)]
    public string? Description { get; set; }

    /// <summary>
    /// Category grouping (e.g., "Bearings", "Belts", "Electronics", "Hotend", "Fans")
    /// </summary>
    [MaxLength(100)]
    public string Category { get; set; } = string.Empty;

    /// <summary>
    /// Cost per unit in the user's currency
    /// </summary>
    public decimal? UnitCost { get; set; }

    /// <summary>
    /// Supplier or vendor name
    /// </summary>
    [MaxLength(200)]
    public string? Supplier { get; set; }

    /// <summary>
    /// URL to purchase or datasheet
    /// </summary>
    [MaxLength(500)]
    public string? Url { get; set; }

    /// <summary>
    /// Current stock quantity on hand
    /// </summary>
    public int InStock { get; set; }

    /// <summary>
    /// Minimum stock level (reorder threshold)
    /// </summary>
    public int MinimumStock { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Tasks that use this component
    /// </summary>
    public ICollection<MaintenanceTaskComponent> TaskComponents { get; set; } = new List<MaintenanceTaskComponent>();
}
