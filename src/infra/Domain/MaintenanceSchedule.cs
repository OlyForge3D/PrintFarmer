using System.ComponentModel.DataAnnotations;

namespace Farm.Infrastructure.Domain;

/// <summary>
/// Defines a maintenance task and its interval/threshold for a specific printer.
/// Can be model-specific (seeded for all printers of a model) or printer-specific (custom per printer).
/// </summary>
public class MaintenanceSchedule
{
    public Guid Id { get; set; }

    /// <summary>
    /// Optional: Printer this schedule applies to (null for model-wide defaults)
    /// </summary>
    public Guid? PrinterId { get; set; }

    /// <summary>
    /// Navigation property to printer (null for model-wide defaults)
    /// </summary>
    public Printer? Printer { get; set; }

    /// <summary>
    /// Optional: Printer model this schedule applies to (for model-wide defaults)
    /// </summary>
    public Guid? PrinterModelId { get; set; }

    /// <summary>
    /// Navigation property to printer model (for model-wide defaults)
    /// </summary>
    public PrinterModel? PrinterModel { get; set; }

    /// <summary>
    /// Name of the maintenance task (e.g., "Hotend Replacement", "Belt Tension Check", "Bed Level")
    /// </summary>
    [MaxLength(128)]
    public string TaskName { get; set; } = string.Empty;

    /// <summary>
    /// Detailed description of what this maintenance involves
    /// </summary>
    [MaxLength(512)]
    public string? Description { get; set; }

    /// <summary>
    /// Component being maintained (e.g., "Hotend", "Bed", "Belts", "Bearings", "Fans")
    /// </summary>
    [MaxLength(64)]
    public string? Component { get; set; }

    /// <summary>
    /// Maintenance interval in print hours (null for calendar-based maintenance)
    /// </summary>
    public double? IntervalHours { get; set; }

    /// <summary>
    /// Maintenance interval in calendar days (null for hour-based maintenance)
    /// </summary>
    public int? IntervalDays { get; set; }

    /// <summary>
    /// Optional: Manufacturer this schedule applies to (for manufacturer-wide defaults)
    /// </summary>
    public Guid? ManufacturerId { get; set; }

    /// <summary>
    /// Estimated duration of this maintenance task in minutes (for reporting/analytics)
    /// </summary>
    public int? EstimatedDurationMinutes { get; set; }

    /// <summary>
    /// Priority level for this maintenance task (1=Low, 2=Medium, 3=High, 4=Critical)
    /// </summary>
    public int Priority { get; set; } = 2;

    /// <summary>
    /// Whether this schedule is currently active
    /// </summary>
    public bool IsActive { get; set; } = true;

    /// <summary>
    /// Whether this is a default schedule (seeded for all printers of this model)
    /// </summary>
    public bool IsDefault { get; set; } = false;

    /// <summary>
    /// When this schedule was created
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// When this schedule was last updated
    /// </summary>
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
