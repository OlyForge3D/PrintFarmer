namespace Farm.Infrastructure.Services.Queue.Dispatch;

/// <summary>
/// Orchestrates batch dispatch operations: dispatching multiple jobs at once
/// with configurable load-balancing strategies.
/// </summary>
public interface IBatchDispatchService
{
    /// <summary>
    /// Dispatches multiple queued jobs to their best-fit printers in a single operation.
    /// </summary>
    /// <param name="request">Batch dispatch parameters (job IDs, strategy).</param>
    /// <param name="userId">The user initiating the batch dispatch.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Aggregate result with per-job outcomes.</returns>
    Task<BatchDispatchResult> BatchDispatchAsync(BatchDispatchRequest request, string userId, CancellationToken ct = default);

    /// <summary>
    /// Returns current queue status: pending jobs, printer depths, and dispatch stats.
    /// </summary>
    Task<DispatchQueueStatusDto> GetQueueStatusAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns paginated dispatch history log entries.
    /// </summary>
    /// <param name="page">1-based page number.</param>
    /// <param name="pageSize">Items per page (max 100).</param>
    /// <param name="dateFrom">Optional inclusive minimum dispatch timestamp (UTC).</param>
    /// <param name="dateTo">Optional inclusive maximum dispatch timestamp (UTC).</param>
    /// <param name="ct">Cancellation token.</param>
    Task<(List<DispatchHistoryDto> Items, int TotalCount)> GetDispatchHistoryAsync(int page, int pageSize, DateTime? dateFrom = null, DateTime? dateTo = null, CancellationToken ct = default);
}
