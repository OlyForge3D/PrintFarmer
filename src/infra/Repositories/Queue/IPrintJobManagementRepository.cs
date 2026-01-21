using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure.Repositories.Queue;

/// <summary>
/// Repository for print job management operations including CRUD, filtering,
/// analytics, and history queries.
/// </summary>
public interface IPrintJobManagementRepository
{
    // ============= BASIC CRUD OPERATIONS =============

    /// <summary>
    /// Get a print job by ID with all related entities (GcodeFile, AssignedPrinter, Model).
    /// </summary>
    Task<PrintJob?> GetByIdWithRelationsAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Add a new print job to the database.
    /// </summary>
    Task<PrintJob> AddAsync(PrintJob job, CancellationToken ct = default);

    /// <summary>
    /// Update an existing print job.
    /// </summary>
    Task<PrintJob> UpdateAsync(PrintJob job, CancellationToken ct = default);

    /// <summary>
    /// Delete a print job by ID.
    /// </summary>
    Task<bool> DeleteAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Save pending changes to the database.
    /// </summary>
    Task SaveChangesAsync(CancellationToken ct = default);

    // ============= FILTERED QUERIES =============

    /// <summary>
    /// Get all queued jobs with optional filtering and pagination.
    /// </summary>
    /// <param name="filterStatus">Optional status filter.</param>
    /// <param name="filterModel">Optional printer model name filter.</param>
    /// <param name="filterMaterial">Optional material type filter.</param>
    /// <param name="limit">Maximum results to return.</param>
    /// <param name="offset">Number of results to skip.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<List<PrintJob>> GetFilteredJobsAsync(
        PrintJobStatus? filterStatus = null,
        string? filterModel = null,
        string? filterMaterial = null,
        int limit = 100,
        int offset = 0,
        CancellationToken ct = default);

    /// <summary>
    /// Get jobs for a specific printer.
    /// </summary>
    Task<List<PrintJob>> GetJobsByPrinterAsync(Guid printerId, int limit = 50, CancellationToken ct = default);

    /// <summary>
    /// Get jobs by status.
    /// </summary>
    Task<List<PrintJob>> GetJobsByStatusAsync(PrintJobStatus status, CancellationToken ct = default);

    /// <summary>
    /// Get jobs by multiple statuses.
    /// </summary>
    Task<List<PrintJob>> GetJobsByStatusesAsync(IEnumerable<PrintJobStatus> statuses, CancellationToken ct = default);

    // ============= STATISTICS & ANALYTICS =============

    /// <summary>
    /// Get queue statistics (counts by status).
    /// </summary>
    Task<(int queued, int printing, int paused, int completed, int failed)> GetQueueStatsAsync(CancellationToken ct = default);

    /// <summary>
    /// Get statistics grouped by printer model.
    /// </summary>
    Task<List<PrinterModelQueueStats>> GetModelStatsAsync(CancellationToken ct = default);

    /// <summary>
    /// Get completed jobs for history with pagination.
    /// </summary>
    Task<(List<PrintJob> jobs, int totalCount)> GetHistoryAsync(
        int limit = 50,
        int offset = 0,
        string sortBy = "completedAt",
        CancellationToken ct = default);

    // ============= TIMELINE & HISTORY =============

    /// <summary>
    /// Get timeline events for visualization.
    /// </summary>
    Task<List<PrintJob>> GetTimelineJobsAsync(
        DateTime? dateFrom = null,
        DateTime? dateTo = null,
        Guid? printerId = null,
        PrintJobStatus? filterStatus = null,
        int limit = 100,
        CancellationToken ct = default);

    /// <summary>
    /// Get a job with its state history loaded.
    /// </summary>
    Task<PrintJob?> GetJobWithStateHistoryAsync(Guid jobId, CancellationToken ct = default);

    /// <summary>
    /// Get duration analytics data for completed jobs.
    /// </summary>
    Task<List<PrintJob>> GetCompletedJobsForAnalyticsAsync(
        Guid? printerId = null,
        DateTime? dateFrom = null,
        DateTime? dateTo = null,
        CancellationToken ct = default);

    // ============= RELATED ENTITIES =============

    /// <summary>
    /// Get a GCode file by ID.
    /// </summary>
    Task<GcodeFile?> GetGcodeFileAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Get a printer by ID with model information.
    /// </summary>
    Task<Printer?> GetPrinterWithModelAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Get all available printers for job assignment.
    /// </summary>
    Task<List<Printer>> GetAvailablePrintersAsync(CancellationToken ct = default);

    /// <summary>
    /// Get the next queue position for a printer.
    /// </summary>
    Task<int> GetNextQueuePositionAsync(Guid printerId, CancellationToken ct = default);

    // ============= BULK OPERATIONS =============

    /// <summary>
    /// Get multiple jobs by their IDs.
    /// </summary>
    Task<List<PrintJob>> GetByIdsAsync(IEnumerable<Guid> ids, CancellationToken ct = default);

    /// <summary>
    /// Update multiple jobs in a single transaction.
    /// </summary>
    Task UpdateManyAsync(IEnumerable<PrintJob> jobs, CancellationToken ct = default);
}

/// <summary>
/// Statistics for a printer model's queue.
/// </summary>
public record PrinterModelQueueStats(
    string ModelName,
    int TotalQueued,
    int CurrentlyPrinting,
    DateTime? OldestQueuedAtUtc);
