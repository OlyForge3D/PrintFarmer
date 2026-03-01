using Farm.Infrastructure.Dtos.PrintQueue;

namespace Farm.Infrastructure.Services.Interfaces;

/// <summary>
/// Service for managing print jobs including CRUD operations, queue management,
/// analytics, timeline visualization, and history tracking.
/// </summary>
public interface IPrintJobManagementService
{
    // ============= QUERY OPERATIONS =============

    /// <summary>
    /// Get all queued and printing jobs with file metadata
    /// </summary>
    /// <param name="filterStatus">Optional status filter for jobs.</param>
    /// <param name="filterModel">Optional model filter for jobs.</param>
    /// <param name="filterMaterial">Optional material filter for jobs.</param>
    /// <param name="limit">Maximum number of jobs to return.</param>
    /// <param name="offset">Number of jobs to skip for pagination.</param>
    /// <param name="cancellationToken">Cancellation token to cancel the operation.</param>
    Task<List<QueuedPrintJobWithFileMetaDto>> GetAllQueuedJobsAsync(
        string? filterStatus = null,
        string? filterModel = null,
        string? filterMaterial = null,
        int limit = 100,
        int offset = 0,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Get print jobs for a specific printer
    /// </summary>
    /// <param name="printerId">The unique identifier of the printer.</param>
    /// <param name="limit">Maximum number of jobs to return.</param>
    /// <param name="cancellationToken">Cancellation token to cancel the operation.</param>
    Task<List<QueuedPrintJobDto>> GetPrinterQueueAsync(
        string printerId,
        int limit = 50,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Get aggregated queue statistics
    /// </summary>
    /// <param name="cancellationToken">Cancellation token to cancel the operation.</param>
    Task<QueueStatsDto> GetQueueStatsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Get printer model statistics with queue counts
    /// </summary>
    /// <param name="cancellationToken">Cancellation token to cancel the operation.</param>
    Task<List<QueuePrinterModelStatsDto>> GetModelStatsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Get print job history (Phase 2)
    /// </summary>
    /// <param name="limit">Maximum number of history records to return.</param>
    /// <param name="offset">Number of records to skip for pagination.</param>
    /// <param name="sortBy">Field to sort results by.</param>
    /// <param name="statuses">Optional list of statuses to filter by (completed, failed, cancelled).</param>
    /// <param name="dateStart">Optional start date filter (inclusive).</param>
    /// <param name="dateEnd">Optional end date filter (inclusive).</param>
    /// <param name="cancellationToken">Cancellation token to cancel the operation.</param>
    Task<QueueHistoryPageDto> GetQueueHistoryAsync(
        int limit = 50,
        int offset = 0,
        string sortBy = "completedAt",
        List<string>? statuses = null,
        DateTime? dateStart = null,
        DateTime? dateEnd = null,
        CancellationToken cancellationToken = default);

    // ============= COMMAND OPERATIONS =============

    /// <summary>
    /// Enqueue a print job
    /// </summary>
    /// <param name="request">The request containing job details to enqueue.</param>
    /// <param name="userId">The unique identifier of the user.</param>
    /// <param name="cancellationToken">Cancellation token to cancel the operation.</param>
    Task<QueuedPrintJobDto> EnqueueJobAsync(
        EnqueueQueueJobRequest request,
        string userId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Update print job (status, priority, printer assignment)
    /// </summary>
    /// <param name="jobId">The unique identifier of the print job.</param>
    /// <param name="request">The request containing update details.</param>
    /// <param name="userId">The unique identifier of the user.</param>
    /// <param name="cancellationToken">Cancellation token to cancel the operation.</param>
    Task<QueuedPrintJobDto> UpdateJobAsync(
        string jobId,
        UpdateQueueJobRequest request,
        string userId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Update job priority (for reordering queue)
    /// </summary>
    /// <param name="jobId">The unique identifier of the print job.</param>
    /// <param name="newPriority">The new priority value for the job.</param>
    /// <param name="userId">The unique identifier of the user.</param>
    /// <param name="cancellationToken">Cancellation token to cancel the operation.</param>
    Task<QueuedPrintJobDto> UpdateJobPriorityAsync(
        string jobId,
        int newPriority,
        string userId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Pause a printing job
    /// </summary>
    /// <param name="jobId">The unique identifier of the print job.</param>
    /// <param name="userId">The unique identifier of the user.</param>
    /// <param name="cancellationToken">Cancellation token to cancel the operation.</param>
    Task<QueuedPrintJobDto> PauseJobAsync(
        string jobId,
        string userId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Resume a paused job
    /// </summary>
    /// <param name="jobId">The unique identifier of the print job.</param>
    /// <param name="userId">The unique identifier of the user.</param>
    /// <param name="cancellationToken">Cancellation token to cancel the operation.</param>
    Task<QueuedPrintJobDto> ResumeJobAsync(
        string jobId,
        string userId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Dispatch a queued/assigned job to its printer to start printing
    /// </summary>
    /// <param name="jobId">The unique identifier of the print job.</param>
    /// <param name="userId">The unique identifier of the user.</param>
    /// <param name="cancellationToken">Cancellation token to cancel the operation.</param>
    /// <returns>Updated job with Starting/Printing status.</returns>
    Task<QueuedPrintJobDto> DispatchJobAsync(
        string jobId,
        string userId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Cancel a job (remove from queue or stop printing)
    /// </summary>
    /// <param name="jobId">The unique identifier of the print job.</param>
    /// <param name="userId">The unique identifier of the user.</param>
    /// <param name="cancellationToken">Cancellation token to cancel the operation.</param>
    Task CancelJobAsync(
        string jobId,
        string userId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Abort the current print attempt but keep the job in the queue.
    /// </summary>
    Task AbortPrintAsync(
        string jobId,
        string userId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Cancel multiple jobs at once
    /// </summary>
    /// <param name="jobIds">List of job identifiers to cancel.</param>
    /// <param name="userId">The unique identifier of the user.</param>
    /// <param name="cancellationToken">Cancellation token to cancel the operation.</param>
    Task<QueueBulkOperationResultDto> BulkCancelJobsAsync(
        List<string> jobIds,
        string userId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reorder multiple jobs in queue
    /// </summary>
    /// <param name="moves">List of reorder moves to apply.</param>
    /// <param name="userId">The unique identifier of the user.</param>
    /// <param name="cancellationToken">Cancellation token to cancel the operation.</param>
    Task<QueueBulkOperationResultDto> BulkReorderJobsAsync(
        List<QueueJobReorderMove> moves,
        string userId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Rerun a completed job (add it back to queue)
    /// </summary>
    /// <param name="jobId">The unique identifier of the print job.</param>
    /// <param name="userId">The unique identifier of the user.</param>
    /// <param name="cancellationToken">Cancellation token to cancel the operation.</param>
    Task<QueuedPrintJobDto> RerunJobAsync(
        string jobId,
        string userId,
        CancellationToken cancellationToken = default);

    // ============= JOB DETAILS OPERATIONS (Phase 3) =============

    /// <summary>
    /// Get detailed information about a specific job
    /// </summary>
    /// <param name="jobId">The unique identifier of the print job.</param>
    /// <param name="cancellationToken">Cancellation token to cancel the operation.</param>
    Task<QueuedPrintJobDto?> GetJobByIdAsync(
        string jobId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Update job details (name, priority, notes, tags, material, nozzle)
    /// </summary>
    /// <param name="jobId">The unique identifier of the print job.</param>
    /// <param name="updates">The request containing updated job details.</param>
    /// <param name="cancellationToken">Cancellation token to cancel the operation.</param>
    Task<QueuedPrintJobDto?> UpdateJobDetailsAsync(
        string jobId,
        UpdateJobDetailsRequest updates,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Update job notes
    /// </summary>
    /// <param name="jobId">The unique identifier of the print job.</param>
    /// <param name="notes">The notes to set for the job.</param>
    /// <param name="cancellationToken">Cancellation token to cancel the operation.</param>
    Task<bool> UpdateJobNotesAsync(
        string jobId,
        string? notes,
        CancellationToken cancellationToken = default);

    // ============= TIMELINE & ANALYTICS OPERATIONS (Phase 3C) =============

    /// <summary>
    /// Get timeline events for visualization with optional filtering
    /// </summary>
    /// <param name="dateFrom">Optional start date for filtering events.</param>
    /// <param name="dateTo">Optional end date for filtering events.</param>
    /// <param name="printerId">Optional printer identifier to filter by.</param>
    /// <param name="filterStatus">Optional status filter for events.</param>
    /// <param name="limit">Maximum number of events to return.</param>
    /// <param name="cancellationToken">Cancellation token to cancel the operation.</param>
    Task<IEnumerable<TimelineEventDto>> GetTimelineAsync(
        DateTime? dateFrom = null,
        DateTime? dateTo = null,
        string? printerId = null,
        string? filterStatus = null,
        int limit = 100,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Get complete state history for a specific job
    /// </summary>
    /// <param name="jobId">The unique identifier of the print job.</param>
    /// <param name="cancellationToken">Cancellation token to cancel the operation.</param>
    Task<JobStateHistoryDto> GetJobStateHistoryAsync(
        string jobId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Get duration analytics comparing estimated vs actual durations
    /// </summary>
    /// <param name="printerId">Optional printer identifier to filter by.</param>
    /// <param name="dateFrom">Optional start date for filtering analytics.</param>
    /// <param name="dateTo">Optional end date for filtering analytics.</param>
    /// <param name="cancellationToken">Cancellation token to cancel the operation.</param>
    Task<DurationAnalyticsDto> GetDurationAnalyticsAsync(
        string? printerId = null,
        DateTime? dateFrom = null,
        DateTime? dateTo = null,
        CancellationToken cancellationToken = default);

    // ============= HISTORY OPERATIONS (Phase 2) =============

    /// <summary>
    /// Seed print job history from printer history APIs.
    /// Fetches all available history (up to 10,000 jobs per printer) and uses
    /// (ExternalJobId, SourcePrinterId) composite key to prevent duplicates.
    /// Existing jobs are updated, new jobs are inserted (AddOrUpdate semantics).
    /// </summary>
    /// <param name="printerIds">Optional list of printer identifiers to seed from. If null, seeds from all enabled printers.</param>
    /// <param name="cancellationToken">Cancellation token to cancel the operation.</param>
    Task SeedHistoryFromPrintersAsync(
        List<string>? printerIds = null,
        CancellationToken cancellationToken = default);
}
