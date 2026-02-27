using System.ComponentModel.DataAnnotations;

namespace Farm.Infrastructure.Domain;

/// <summary>
/// A global maintenance task in the catalog.
/// Tasks are standalone reusable definitions referenced by plans via PlanTask join.
/// Scope rules (nullable bools) control which printer models a task applies to.
/// null = don't care, true = required, false = excluded.
/// </summary>
public class MaintenanceTask
{
    public Guid Id { get; set; }

    /// <summary>
    /// Name of the task (e.g., "Replace HEPA Filter", "Lubricate Z Lead Screws")
    /// </summary>
    [MaxLength(200)]
    public string TaskName { get; set; } = string.Empty;

    /// <summary>
    /// Detailed description of what this task involves
    /// </summary>
    [MaxLength(1000)]
    public string? Description { get; set; }

    /// <summary>
    /// Category for grouping (e.g., "Filtration", "Motion System", "Hotend", "Bed")
    /// </summary>
    [MaxLength(100)]
    public string Category { get; set; } = string.Empty;

    /// <summary>
    /// Maintenance interval in print hours (null for calendar-based only)
    /// </summary>
    public double? IntervalHours { get; set; }

    /// <summary>
    /// Maintenance interval in calendar days (null for hour-based only)
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
    /// Whether this task is currently active in the catalog
    /// </summary>
    public bool IsActive { get; set; } = true;

    /// <summary>
    /// Whether this is a system-seeded default task
    /// </summary>
    public bool IsDefault { get; set; }

    // ── Scope Rules ──────────────────────────────────────────────
    // null = don't care, true = printer MUST have this, false = printer must NOT have this
    public bool? RequiresEnclosure { get; set; }

    public bool? RequiresCarbonFilter { get; set; }

    public bool? RequiresHepaFilter { get; set; }

    public bool? RequiresBowdenTube { get; set; }

    public bool? RequiresPtfeLiner { get; set; }

    public bool? RequiresLinearRails { get; set; }

    public bool? RequiresLeadScrews { get; set; }

    public bool? RequiresToolchanger { get; set; }

    public bool? RequiresFilamentCutter { get; set; }

    public bool? RequiresHeatedChamber { get; set; }

    public bool? RequiresHeatedBed { get; set; }

    public bool? RequiresMultiMaterial { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Components/parts required for this task (with quantities)
    /// </summary>
    public ICollection<MaintenanceTaskComponent> TaskComponents { get; set; } = new List<MaintenanceTaskComponent>();

    /// <summary>
    /// Plans that include this task
    /// </summary>
    public ICollection<PlanTask> PlanTasks { get; set; } = new List<PlanTask>();
}
