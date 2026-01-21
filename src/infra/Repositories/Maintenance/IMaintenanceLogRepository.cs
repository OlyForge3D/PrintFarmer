using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure.Repositories.Maintenance;

/// <summary>
/// Repository interface for managing MaintenanceLog entities.
/// Provides CRUD operations and querying for maintenance activity records.
/// </summary>
public interface IMaintenanceLogRepository
{
    /// <summary>
    /// Gets a maintenance log entry by ID.
    /// </summary>
    /// <param name="id">The maintenance log ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The maintenance log if found, null otherwise.</returns>
    Task<MaintenanceLog?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all maintenance logs for a specific printer, ordered by date descending.
    /// </summary>
    /// <param name="printerId">The printer ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of maintenance logs for the printer.</returns>
    Task<List<MaintenanceLog>> GetByPrinterIdAsync(Guid printerId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all maintenance logs for a specific printer and schedule combination.
    /// </summary>
    /// <param name="printerId">The printer ID.</param>
    /// <param name="scheduleId">The maintenance schedule ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of maintenance logs for the printer and schedule.</returns>
    Task<List<MaintenanceLog>> GetByPrinterAndScheduleAsync(Guid printerId, Guid scheduleId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the most recent maintenance log for a specific printer and schedule combination.
    /// </summary>
    /// <param name="printerId">The printer ID.</param>
    /// <param name="scheduleId">The maintenance schedule ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The most recent maintenance log, or null if none found.</returns>
    Task<MaintenanceLog?> GetLastMaintenanceAsync(Guid printerId, Guid scheduleId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all maintenance logs, optionally filtered by date range.
    /// </summary>
    /// <param name="startDate">Optional start date filter.</param>
    /// <param name="endDate">Optional end date filter.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of all maintenance logs matching the criteria.</returns>
    Task<List<MaintenanceLog>> GetAllAsync(DateTime? startDate = null, DateTime? endDate = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds a new maintenance log entry.
    /// </summary>
    /// <param name="log">The maintenance log to add.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The added maintenance log with generated ID.</returns>
    Task<MaintenanceLog> AddAsync(MaintenanceLog log, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates an existing maintenance log entry.
    /// </summary>
    /// <param name="log">The maintenance log to update.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The updated maintenance log.</returns>
    Task<MaintenanceLog> UpdateAsync(MaintenanceLog log, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a maintenance log entry.
    /// </summary>
    /// <param name="id">The maintenance log ID to delete.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if deleted, false if not found.</returns>
    Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}
