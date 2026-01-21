using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure.Repositories.Maintenance;

/// <summary>
/// Repository for maintenance schedules.
/// Provides queries for both model-wide defaults and printer-specific schedules.
/// </summary>
public interface IMaintenanceScheduleRepository
{
    /// <summary>
    /// Gets all active schedules for a specific printer.
    /// Includes both printer-specific schedules and model-wide defaults.
    /// </summary>
    /// <param name="printerId">The printer ID</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>List of active maintenance schedules</returns>
    Task<List<MaintenanceSchedule>> GetActivePrinterSchedulesAsync(
        Guid printerId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all active schedules for a printer model (model-wide defaults).
    /// </summary>
    /// <param name="printerModelId">The printer model ID</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>List of active model-wide maintenance schedules</returns>
    Task<List<MaintenanceSchedule>> GetActiveModelSchedulesAsync(
        Guid printerModelId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a specific schedule by ID.
    /// </summary>
    /// <param name="id">The schedule ID</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The maintenance schedule or null if not found</returns>
    Task<MaintenanceSchedule?> GetByIdAsync(
        Guid id,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all schedules (for admin/reporting).
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>All maintenance schedules</returns>
    Task<List<MaintenanceSchedule>> GetAllAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds a new maintenance schedule.
    /// </summary>
    /// <param name="schedule">The schedule to add</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task AddAsync(MaintenanceSchedule schedule, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates an existing maintenance schedule.
    /// </summary>
    /// <param name="schedule">The schedule to update</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task UpdateAsync(MaintenanceSchedule schedule, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a maintenance schedule.
    /// </summary>
    /// <param name="id">The schedule ID to delete</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Saves all pending changes.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    Task SaveChangesAsync(CancellationToken cancellationToken = default);
}
