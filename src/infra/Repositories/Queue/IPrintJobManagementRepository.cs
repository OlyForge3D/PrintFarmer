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
    /// Get a print job by ID (simple lookup, no related entities).
    /// </summary>
    Task<PrintJob?> GetByIdAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Get a print job by ID with all related entities (GcodeFile, AssignedPrinter, Model).
    /// </summary>
    Task<PrintJob?> GetByIdWithRelationsAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Get a print job by ID with GcodeFile relation loaded.
    /// </summary>
    Task<PrintJob?> GetByIdWithGcodeFileAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Add a new print job to the database.
    /// </summary>
    Task<PrintJob> AddAsync(PrintJob job, CancellationToken ct = default);

    /// <summary>
    /// Add a print job to the change tracker without saving.
    /// Call SaveChangesAsync() separately to persist.
    /// </summary>
    void Add(PrintJob job);

    /// <summary>
    /// Remove a print job from the change tracker without saving.
    /// Call SaveChangesAsync() separately to persist.
    /// </summary>
    void Remove(PrintJob job);

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
    /// <param name="deadlineStartUtc">Optional inclusive minimum deadline timestamp (UTC).</param>
    /// <param name="deadlineEndUtc">Optional inclusive maximum deadline timestamp (UTC).</param>
    /// <param name="sortBy">Sort mode for queued jobs (for example: priority, deadline, deadline_desc).</param>
    /// <param name="limit">Maximum results to return.</param>
    /// <param name="offset">Number of results to skip.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<List<PrintJob>> GetFilteredJobsAsync(
        PrintJobStatus? filterStatus = null,
        string? filterModel = null,
        string? filterMaterial = null,
        DateTime? deadlineStartUtc = null,
        DateTime? deadlineEndUtc = null,
        string sortBy = "priority",
        int limit = 100,
        int offset = 0,
        DateTime? queuedFrom = null,
        DateTime? queuedTo = null,
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
    /// Get average wait time in minutes for recently completed jobs.
    /// Wait time = ActualStartTime - QueuedAt for completed jobs within the lookback period.
    /// </summary>
    Task<double> GetAverageWaitTimeMinutesAsync(Guid? printerModelId = null, int lookbackDays = 30, CancellationToken ct = default);

    /// <summary>
    /// Get statistics grouped by printer model.
    /// </summary>
    Task<List<PrinterModelQueueStats>> GetModelStatsAsync(CancellationToken ct = default);

    /// <summary>
    /// Get completed jobs for history with pagination and filtering.
    /// </summary>
    /// <param name="limit">Maximum number of jobs to return.</param>
    /// <param name="offset">Number of jobs to skip for pagination.</param>
    /// <param name="sortBy">Field to sort by (completedAt, duration, name, status).</param>
    /// <param name="statuses">Optional list of statuses to filter by (completed, failed, cancelled).</param>
    /// <param name="dateStart">Optional start date filter (inclusive).</param>
    /// <param name="dateEnd">Optional end date filter (inclusive).</param>
    /// <param name="deadlineStartUtc">Optional inclusive minimum deadline timestamp (UTC).</param>
    /// <param name="deadlineEndUtc">Optional inclusive maximum deadline timestamp (UTC).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Tuple of paginated jobs, total count, and statistics for the full filtered set.</returns>
    Task<(List<PrintJob> jobs, int totalCount, int completedCount, int failedCount, int cancelledCount, long totalPrintTimeSeconds)> GetHistoryAsync(
        int limit = 50,
        int offset = 0,
        string sortBy = "completedAt",
        List<string>? statuses = null,
        DateTime? dateStart = null,
        DateTime? dateEnd = null,
        DateTime? deadlineStartUtc = null,
        DateTime? deadlineEndUtc = null,
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

    // ============= HISTORY SEEDING OPERATIONS =============

    /// <summary>
    /// Get all enabled printers (for history seeding).
    /// </summary>
    Task<List<Printer>> GetEnabledPrintersAsync(CancellationToken ct = default);

    /// <summary>
    /// Get external job IDs for a specific printer (for duplicate detection during history seeding).
    /// </summary>
    Task<HashSet<string>> GetExternalJobIdsForPrinterAsync(Guid printerId, CancellationToken ct = default);

    /// <summary>
    /// Get a job by external job ID and source printer ID (for history seeding updates).
    /// </summary>
    Task<PrintJob?> GetByExternalIdAsync(Guid printerId, string externalJobId, CancellationToken ct = default);

    /// <summary>
    /// Finds an existing PrintFarmer-managed job that likely corresponds to a printer history entry.
    /// Used to prevent duplicate history rows when a print is started via PrintFarmer and later
    /// appears in the printer-provided history sync.
    /// </summary>
    Task<PrintJob?> FindExistingJobForHistoryMatchAsync(
        Guid printerId,
        string filename,
        DateTime startTimeUtc,
        DateTime? endTimeUtc,
        CancellationToken ct = default);

    /// <summary>
    /// Find a GCode file by filename (for history seeding).
    /// </summary>
    Task<GcodeFile?> FindGcodeFileByFilenameAsync(string filename, CancellationToken ct = default);

    /// <summary>
    /// Update a printer's LastHistorySeedUtc timestamp (for incremental history seeding).
    /// </summary>
    Task UpdatePrinterLastHistorySeedAsync(Guid printerId, DateTime lastSeedUtc, CancellationToken ct = default);

    /// <summary>
    /// Get the maximum queue position across all queued/printing jobs.
    /// </summary>
    Task<int> GetMaxQueuePositionAsync(CancellationToken ct = default);

    /// <summary>
    /// Get toolheads configured for a printer (for slicer estimate snapshotting).
    /// </summary>
    Task<List<Toolhead>> GetToolheadsForPrinterAsync(Guid printerId, CancellationToken ct = default);

    /// <summary>
    /// Add a toolhead usage record without saving (caller must call SaveChangesAsync).
    /// </summary>
    Task AddToolheadUsageAsync(PrintJobToolheadUsage usage, CancellationToken ct = default);
}

/// <summary>
/// Statistics for a printer model's queue.
/// </summary>
public record PrinterModelQueueStats(
    string ModelName,
    int TotalQueued,
    int CurrentlyPrinting,
    DateTime? OldestQueuedAtUtc);
