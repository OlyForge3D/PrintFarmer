using System.ComponentModel.DataAnnotations;

namespace Farm.Infrastructure.Domain;

/// <summary>
/// A maintenance plan groups related maintenance tasks for a printer, model, manufacturer, or motion type.
/// Example: "Prusa Mini Maintenance Plan" with tasks like belt tension, bearing replacement, etc.
/// </summary>
public class MaintenancePlan
{
    public Guid Id { get; set; }

    /// <summary>
    /// Human-readable name for the plan (e.g., "Prusa Mini Preventive Maintenance")
    /// </summary>
    [MaxLength(200)]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Detailed description of what this plan covers
    /// </summary>
    [MaxLength(1000)]
    public string? Description { get; set; }

    /// <summary>
    /// Optional: Specific printer this plan applies to
    /// </summary>
    public Guid? PrinterId { get; set; }

    /// <summary>
    /// Navigation property to printer
    /// </summary>
    public Printer? Printer { get; set; }

    /// <summary>
    /// Optional: Printer model this plan applies to (for model-wide plans)
    /// </summary>
    public Guid? PrinterModelId { get; set; }

    /// <summary>
    /// Navigation property to printer model
    /// </summary>
    public PrinterModel? PrinterModel { get; set; }

    /// <summary>
    /// Optional: Manufacturer this plan applies to
    /// </summary>
    public Guid? ManufacturerId { get; set; }

    /// <summary>
    /// Navigation property to manufacturer
    /// </summary>
    public Manufacturer? Manufacturer { get; set; }

    /// <summary>
    /// Optional: Motion type this plan applies to (Cartesian=0, CoreXY=1, etc.)
    /// </summary>
    public int? MotionType { get; set; }

    /// <summary>
    /// Whether this plan is currently active
    /// </summary>
    public bool IsActive { get; set; } = true;

    /// <summary>
    /// Whether this is a system-seeded default plan
    /// </summary>
    public bool IsDefault { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Tasks belonging to this plan
    /// </summary>
    public ICollection<MaintenanceTask> Tasks { get; set; } = new List<MaintenanceTask>();
}
