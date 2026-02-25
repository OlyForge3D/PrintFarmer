namespace Farm.Infrastructure.Domain;

/// <summary>
/// Junction table linking a maintenance task to a component with quantity.
/// Example: "Replace Bearings" task requires 4x LM8UU bearings.
/// </summary>
public class MaintenanceTaskComponent
{
    public Guid Id { get; set; }

    /// <summary>
    /// The task that requires this component
    /// </summary>
    public Guid MaintenanceTaskId { get; set; }

    /// <summary>
    /// Navigation property to the task
    /// </summary>
    public MaintenanceTask MaintenanceTask { get; set; } = null!;

    /// <summary>
    /// The component required
    /// </summary>
    public Guid MaintenanceComponentId { get; set; }

    /// <summary>
    /// Navigation property to the component
    /// </summary>
    public MaintenanceComponent MaintenanceComponent { get; set; } = null!;

    /// <summary>
    /// Quantity of this component needed for the task
    /// </summary>
    public int Quantity { get; set; } = 1;

    /// <summary>
    /// Optional notes (e.g., "use PTFE-coated variant for CoreXY")
    /// </summary>
    public string? Notes { get; set; }
}
