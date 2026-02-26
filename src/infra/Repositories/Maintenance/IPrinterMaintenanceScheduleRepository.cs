using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure.Repositories.Maintenance;

/// <summary>
/// Repository for printer maintenance schedule (plan deployment) operations.
/// </summary>
public interface IPrinterMaintenanceScheduleRepository
{
    /// <summary>
    /// Gets all schedule deployments, optionally filtered by printer or plan.
    /// </summary>
    Task<List<PrinterMaintenanceSchedule>> GetAllAsync(Guid? printerId = null, Guid? planId = null, bool? activeOnly = null, CancellationToken ct = default);

    /// <summary>
    /// Gets active deployments for a printer with deep-loaded PlanTasks and MaintenanceTasks.
    /// Used by the alert engine to evaluate maintenance intervals.
    /// </summary>
    Task<List<PrinterMaintenanceSchedule>> GetActiveWithTasksAsync(Guid printerId, CancellationToken ct = default);

    /// <summary>
    /// Gets a schedule deployment by ID, including plan and printer navigation.
    /// </summary>
    Task<PrinterMaintenanceSchedule?> GetByIdAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Checks if a plan is already deployed to a printer.
    /// </summary>
    Task<bool> ExistsAsync(Guid planId, Guid printerId, CancellationToken ct = default);

    /// <summary>
    /// Adds a new schedule deployment.
    /// </summary>
    Task AddAsync(PrinterMaintenanceSchedule schedule, CancellationToken ct = default);

    /// <summary>
    /// Updates an existing schedule deployment.
    /// </summary>
    Task UpdateAsync(PrinterMaintenanceSchedule schedule, CancellationToken ct = default);

    /// <summary>
    /// Deletes a schedule deployment.
    /// </summary>
    Task DeleteAsync(PrinterMaintenanceSchedule schedule, CancellationToken ct = default);

    /// <summary>
    /// Persists changes to the database.
    /// </summary>
    Task SaveChangesAsync(CancellationToken ct = default);
}
