using System.ComponentModel.DataAnnotations;

namespace Farm.Infrastructure.Domain;

/// <summary>
/// An individual maintenance task within a plan.
/// Defines what needs to be done and how often.
/// Example: "Check belt tension" every 500 print hours.
/// </summary>
public class MaintenanceTask
{
    public Guid Id { get; set; }

    /// <summary>
    /// The plan this task belongs to
    /// </summary>
    public Guid MaintenancePlanId { get; set; }

    /// <summary>
    /// Navigation property to the parent plan
    /// </summary>
    public MaintenancePlan MaintenancePlan { get; set; } = null!;

    /// <summary>
    /// Name of the task (e.g., "Belt Tension Check", "Replace Bearings")
    /// </summary>
    [MaxLength(200)]
    public string TaskName { get; set; } = string.Empty;

    /// <summary>
    /// Detailed description of what this task involves
    /// </summary>
    [MaxLength(1000)]
    public string? Description { get; set; }

    /// <summary>
    /// Maintenance interval in print hours (null for calendar-based)
    /// </summary>
    public double? IntervalHours { get; set; }

    /// <summary>
    /// Maintenance interval in calendar days (null for hour-based)
    /// </summary>
    public int? IntervalDays { get; set; }

    /// <summary>
    /// Estimated duration of this task in minutes
    /// </summary>
    public int? EstimatedDurationMinutes { get; set; }

    /// <summary>
    /// Priority level (1=Low, 2=Medium, 3=High, 4=Critical)
    /// </summary>
    public int Priority { get; set; } = 2;

    /// <summary>
    /// Whether this task is currently active
    /// </summary>
    public bool IsActive { get; set; } = true;

    /// <summary>
    /// Display order within the plan
    /// </summary>
    public int SortOrder { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Components required for this task (with quantities)
    /// </summary>
    public ICollection<MaintenanceTaskComponent> TaskComponents { get; set; } = new List<MaintenanceTaskComponent>();
}
