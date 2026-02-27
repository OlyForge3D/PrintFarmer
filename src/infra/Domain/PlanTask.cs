namespace Farm.Infrastructure.Domain;

/// <summary>
/// Join entity linking a MaintenancePlan to a MaintenanceTask (many-to-many).
/// Allows optional interval overrides so a plan can adjust task intervals per context.
/// </summary>
public class PlanTask
{
    public Guid Id { get; set; }

    /// <summary>
    /// The plan that includes this task
    /// </summary>
    public Guid MaintenancePlanId { get; set; }

    /// <summary>
    /// Navigation property to the plan
    /// </summary>
    public MaintenancePlan MaintenancePlan { get; set; } = null!;

    /// <summary>
    /// The task included in this plan
    /// </summary>
    public Guid MaintenanceTaskId { get; set; }

    /// <summary>
    /// Navigation property to the task
    /// </summary>
    public MaintenanceTask MaintenanceTask { get; set; } = null!;

    /// <summary>
    /// Display order within the plan
    /// </summary>
    public int SortOrder { get; set; }

    /// <summary>
    /// Optional: Override the task's default IntervalHours for this plan
    /// </summary>
    public double? IntervalHoursOverride { get; set; }

    /// <summary>
    /// Optional: Override the task's default IntervalDays for this plan
    /// </summary>
    public int? IntervalDaysOverride { get; set; }
}
