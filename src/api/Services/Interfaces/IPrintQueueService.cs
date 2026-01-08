using Farm.Api.DTOs;

namespace Farm.Api.Services.Interfaces;

/// <summary>
/// Service for managing print queue operations
/// </summary>
public interface IPrintQueueService
{
    // ============= QUERY OPERATIONS =============
    
    /// <summary>
    /// Get all queued and printing jobs with file metadata
    /// </summary>
    Task<List<QueuedPrintJobWithFileMetaDto>> GetAllQueuedJobsAsync(
        string? filterStatus = null,
        string? filterModel = null,
        string? filterMaterial = null,
        int limit = 100,
        int offset = 0,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Get print jobs for a specific printer
    /// </summary>
    Task<List<QueuedPrintJobDto>> GetPrinterQueueAsync(
        string printerId,
        int limit = 50,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Get aggregated queue statistics
    /// </summary>
    Task<QueueStatsDto> GetQueueStatsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Get printer model statistics with queue counts
    /// </summary>
    Task<List<QueuePrinterModelStatsDto>> GetModelStatsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Get print job history (Phase 2)
    /// </summary>
    Task<QueueHistoryPageDto> GetQueueHistoryAsync(
        int limit = 50,
        int offset = 0,
        string sortBy = "completedAt",
        CancellationToken cancellationToken = default
    );

    // ============= COMMAND OPERATIONS =============
    
    /// <summary>
    /// Enqueue a print job
    /// </summary>
    Task<QueuedPrintJobDto> EnqueueJobAsync(
        EnqueueQueueJobRequest request,
        string userId,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Update print job (status, priority, printer assignment)
    /// </summary>
    Task<QueuedPrintJobDto> UpdateJobAsync(
        string jobId,
        UpdateQueueJobRequest request,
        string userId,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Update job priority (for reordering queue)
    /// </summary>
    Task<QueuedPrintJobDto> UpdateJobPriorityAsync(
        string jobId,
        int newPriority,
        string userId,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Pause a printing job
    /// </summary>
    Task<QueuedPrintJobDto> PauseJobAsync(
        string jobId,
        string userId,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Resume a paused job
    /// </summary>
    Task<QueuedPrintJobDto> ResumeJobAsync(
        string jobId,
        string userId,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Cancel a job (remove from queue or stop printing)
    /// </summary>
    Task CancelJobAsync(
        string jobId,
        string userId,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Cancel multiple jobs at once
    /// </summary>
    Task<QueueBulkOperationResultDto> BulkCancelJobsAsync(
        List<string> jobIds,
        string userId,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Reorder multiple jobs in queue
    /// </summary>
    Task<QueueBulkOperationResultDto> BulkReorderJobsAsync(
        List<QueueJobReorderMove> moves,
        string userId,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Rerun a completed job (add it back to queue)
    /// </summary>
    Task<QueuedPrintJobDto> RerunJobAsync(
        string jobId,
        string userId,
        CancellationToken cancellationToken = default
    );

    // ============= HISTORY OPERATIONS (Phase 2) =============
    
    /// <summary>
    /// Seed print job history from printer history
    /// </summary>
    Task SeedHistoryFromPrintersAsync(
        List<string>? printerIds = null,
        int daysBack = 30,
        CancellationToken cancellationToken = default
    );
}
