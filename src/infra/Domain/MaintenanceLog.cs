using System.ComponentModel.DataAnnotations;

namespace Farm.Infrastructure.Domain;

/// <summary>
/// Records a completed maintenance activity on a printer.
/// Provides audit trail and history for maintenance operations.
/// </summary>
public class MaintenanceLog
{
    public Guid Id { get; set; }

    /// <summary>
    /// Printer this maintenance was performed on
    /// </summary>
    public Guid PrinterId { get; set; }

    /// <summary>
    /// Navigation property to printer
    /// </summary>
    public Printer Printer { get; set; } = null!;

    /// <summary>
    /// Optional: Deployment (printer × plan) this maintenance was performed for (null for unscheduled maintenance)
    /// </summary>
    public Guid? PrinterMaintenanceScheduleId { get; set; }

    /// <summary>
    /// Navigation property to printer maintenance schedule deployment
    /// </summary>
    public PrinterMaintenanceSchedule? PrinterMaintenanceSchedule { get; set; }

    /// <summary>
    /// Optional: Alert this maintenance resolved (null if not performed in response to alert)
    /// </summary>
    public Guid? ResolvedAlertId { get; set; }

    /// <summary>
    /// Navigation property to resolved alert
    /// </summary>
    public MaintenanceAlert? ResolvedAlert { get; set; }

    /// <summary>
    /// Optional: The maintenance task (from the global catalog) that was performed
    /// </summary>
    public Guid? MaintenanceTaskId { get; set; }

    /// <summary>
    /// Navigation property to maintenance task
    /// </summary>
    public MaintenanceTask? MaintenanceTask { get; set; }

    /// <summary>
    /// Name/title of the maintenance performed
    /// </summary>
    [MaxLength(128)]
    public string TaskName { get; set; } = string.Empty;

    /// <summary>
    /// Detailed notes about what was done
    /// </summary>
    [MaxLength(2000)]
    public string? Notes { get; set; }

    /// <summary>
    /// Component that was maintained (e.g., "Hotend", "Bed", "Belts")
    /// </summary>
    [MaxLength(64)]
    public string? Component { get; set; }

    /// <summary>
    /// User who performed the maintenance (username or ID)
    /// </summary>
    [MaxLength(128)]
    public string? PerformedBy { get; set; }

    /// <summary>
    /// When the maintenance was performed
    /// </summary>
    public DateTime PerformedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Duration of the maintenance in minutes
    /// </summary>
    public int? DurationMinutes { get; set; }

    /// <summary>
    /// Parts replaced during maintenance (comma-separated or JSON)
    /// </summary>
    [MaxLength(512)]
    public string? PartsReplaced { get; set; }

    /// <summary>
    /// Cost of maintenance (parts + labor)
    /// </summary>
    public decimal? Cost { get; set; }

    /// <summary>
    /// Printer hours at time of maintenance (for tracking intervals)
    /// </summary>
    public double? PrinterHoursAtMaintenance { get; set; }

    /// <summary>
    /// When this record was created
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
