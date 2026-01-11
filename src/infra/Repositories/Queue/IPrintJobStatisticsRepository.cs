using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure.Repositories.Queue;

/// <summary>
/// Repository for print job statistics and prediction data.
/// Provides specialized queries for completion time prediction and analytics.
/// </summary>
public interface IPrintJobStatisticsRepository
{
    /// <summary>
    /// Records new statistics for a completed job
    /// </summary>
    Task AddAsync(PrintJobStatistics statistics, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates existing job statistics
    /// </summary>
    Task UpdateAsync(PrintJobStatistics statistics, CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves statistics for a specific job by its ID
    /// </summary>
    Task<PrintJobStatistics?> GetByJobIdAsync(Guid jobId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all statistics for jobs matching a specific printer model and material
    /// Used for finding similar historical jobs for predictions
    /// </summary>
    /// <param name="modelId">Printer model ID (null for any model)</param>
    /// <param name="material">Material type (null for any material)</param>
    /// <param name="successfulOnly">Only include successful jobs</param>
    /// <param name="limit">Maximum number of records to return</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task<List<PrintJobStatistics>> GetByModelAndMaterialAsync(
        Guid? modelId,
        string? material,
        bool successfulOnly = true,
        int limit = 100,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all successful completed jobs within a date range
    /// Used for historical analysis and analytics
    /// </summary>
    /// <param name="fromDate">Start date for filtering (UTC, optional)</param>
    /// <param name="toDate">End date for filtering (UTC, optional)</param>
    /// <param name="limit">Maximum number of records</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task<List<PrintJobStatistics>> GetSuccessfulJobsAsync(
        DateTime? fromDate = null,
        DateTime? toDate = null,
        int limit = 1000,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets statistics for a specific printer model
    /// Used for model-specific analytics
    /// </summary>
    Task<List<PrintJobStatistics>> GetByPrinterModelAsync(
        Guid modelId,
        bool successfulOnly = true,
        DateTime? fromDate = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets statistics grouped by material type
    /// Used for material analysis and reporting
    /// </summary>
    Task<List<PrintJobStatistics>> GetByMaterialAsync(
        string material,
        bool successfulOnly = true,
        DateTime? fromDate = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all statistics records (careful with large datasets)
    /// </summary>
    Task<List<PrintJobStatistics>> GetAllAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the count of statistics records matching criteria
    /// </summary>
    Task<int> CountAsync(
        Guid? modelId = null,
        string? material = null,
        bool? successOnly = null,
        DateTime? fromDate = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Persists all pending changes to the database
    /// </summary>
    Task SaveChangesAsync(CancellationToken cancellationToken = default);
}
