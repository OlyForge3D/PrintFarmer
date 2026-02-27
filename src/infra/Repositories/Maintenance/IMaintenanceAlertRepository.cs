using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure.Repositories.Maintenance;

/// <summary>
/// Repository for maintenance alerts.
/// </summary>
public interface IMaintenanceAlertRepository
{
    /// <summary>
    /// Gets all active alerts for a specific printer.
    /// </summary>
    /// <param name="printerId">The printer ID</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>List of active maintenance alerts</returns>
    Task<List<MaintenanceAlert>> GetActivePrinterAlertsAsync(
        Guid printerId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all alerts for a specific printer (all statuses).
    /// </summary>
    /// <param name="printerId">The printer ID</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>List of maintenance alerts</returns>
    Task<List<MaintenanceAlert>> GetAllPrinterAlertsAsync(
        Guid printerId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all active alerts across all printers.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>List of active maintenance alerts</returns>
    Task<List<MaintenanceAlert>> GetAllActiveAlertsAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets an alert by ID.
    /// </summary>
    /// <param name="id">The alert ID</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The maintenance alert or null if not found</returns>
    Task<MaintenanceAlert?> GetByIdAsync(
        Guid id,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks if an active alert already exists for a printer, task, and deployment.
    /// Prevents duplicate alerts from being created.
    /// </summary>
    /// <param name="printerId">The printer ID</param>
    /// <param name="taskId">The maintenance task ID</param>
    /// <param name="deploymentId">The deployment (printer × plan) ID</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if an active alert exists</returns>
    Task<bool> HasActiveAlertAsync(
        Guid printerId,
        Guid taskId,
        Guid deploymentId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds a new maintenance alert.
    /// </summary>
    /// <param name="alert">The alert to add</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task AddAsync(MaintenanceAlert alert, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates an existing maintenance alert.
    /// </summary>
    /// <param name="alert">The alert to update</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task UpdateAsync(MaintenanceAlert alert, CancellationToken cancellationToken = default);

    /// <summary>
    /// Saves all pending changes.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    Task SaveChangesAsync(CancellationToken cancellationToken = default);
}
