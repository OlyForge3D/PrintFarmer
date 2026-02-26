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
    /// Gets all maintenance logs for a specific printer and task combination.
    /// </summary>
    /// <param name="printerId">The printer ID.</param>
    /// <param name="taskId">The maintenance task ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of maintenance logs for the printer and task.</returns>
    Task<List<MaintenanceLog>> GetByPrinterAndTaskAsync(Guid printerId, Guid taskId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the most recent maintenance log for a specific printer and task combination.
    /// </summary>
    /// <param name="printerId">The printer ID.</param>
    /// <param name="taskId">The maintenance task ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The most recent maintenance log, or null if none found.</returns>
    Task<MaintenanceLog?> GetLastMaintenanceAsync(Guid printerId, Guid taskId, CancellationToken cancellationToken = default);

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

    #region Analytics

    /// <summary>
    /// Gets maintenance trends data grouped by date.
    /// </summary>
    /// <param name="startDate">Start date for the trend period.</param>
    /// <param name="endDate">End date for the trend period.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of maintenance trend entries.</returns>
    Task<List<MaintenanceTrendEntry>> GetTrendsAsync(DateTime startDate, DateTime endDate, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets component lifespan statistics based on maintenance history.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of component lifespan statistics.</returns>
    Task<List<ComponentLifespanEntry>> GetComponentLifespanAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets maintenance cost analysis grouped by month.
    /// </summary>
    /// <param name="months">Number of months to analyze.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of monthly cost entries.</returns>
    Task<List<MaintenanceCostEntry>> GetCostAnalysisAsync(int months = 12, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets printer uptime statistics based on maintenance downtime.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of printer uptime entries.</returns>
    Task<List<PrinterUptimeEntry>> GetPrinterUptimeAsync(CancellationToken cancellationToken = default);

    #endregion
}

#region Analytics DTOs

/// <summary>
/// Represents a single maintenance trend data point.
/// </summary>
public record MaintenanceTrendEntry(
    DateTime Date,
    string PrinterName,
    string? Component,
    string Action,
    decimal Cost);

/// <summary>
/// Represents component lifespan statistics.
/// </summary>
public record ComponentLifespanEntry(
    string Component,
    double AvgLifespanHours,
    int Replacements);

/// <summary>
/// Represents monthly maintenance cost.
/// </summary>
public record MaintenanceCostEntry(
    string Month,
    decimal TotalCost);

/// <summary>
/// Represents printer uptime statistics.
/// </summary>
public record PrinterUptimeEntry(
    string PrinterName,
    Guid PrinterId,
    double UptimePercent,
    int MaintenanceCount,
    int TotalDowntimeMinutes);

#endregion
