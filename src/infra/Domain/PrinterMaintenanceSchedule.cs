namespace Farm.Infrastructure.Domain;

/// <summary>
/// Represents a maintenance plan deployed to a specific printer.
/// This is the operational entity: the alert engine evaluates these
/// to determine when maintenance tasks are due.
/// </summary>
public class PrinterMaintenanceSchedule
{
    public Guid Id { get; set; }

    /// <summary>
    /// The plan that was deployed
    /// </summary>
    public Guid MaintenancePlanId { get; set; }

    /// <summary>
    /// Navigation property to the plan
    /// </summary>
    public MaintenancePlan MaintenancePlan { get; set; } = null!;

    /// <summary>
    /// The printer this plan is deployed to
    /// </summary>
    public Guid PrinterId { get; set; }

    /// <summary>
    /// Navigation property to the printer
    /// </summary>
    public Printer Printer { get; set; } = null!;

    /// <summary>
    /// Whether this deployment is currently active
    /// </summary>
    public bool IsActive { get; set; } = true;

    /// <summary>
    /// Optional physical toolhead scope. When null, the schedule is printer-wide (legacy
    /// behavior). When set, the schedule tracks a specific physical toolhead so per-tool
    /// nozzle/hotend intervals can accrue independently (issue #711, F6). Service layer
    /// enforces <see cref="ToolheadType.Physical"/> — MMU gates are not eligible.
    /// </summary>
    public Guid? ToolheadId { get; set; }

    /// <summary>
    /// Navigation property to the scoped toolhead. Null when the schedule is printer-wide.
    /// </summary>
    public Toolhead? Toolhead { get; set; }

    /// <summary>
    /// When the plan was deployed to this printer
    /// </summary>
    public DateTime DeployedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Optional notes about this deployment
    /// </summary>
    public string? Notes { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
