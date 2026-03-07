namespace Farm.Infrastructure.Services.Queue.Dispatch;

/// <summary>
/// A scored printer candidate returned by the dispatch scoring engine.
/// </summary>
public class DispatchCandidateDto
{
    /// <summary>Unique identifier of the candidate printer.</summary>
    public Guid PrinterId { get; set; }

    /// <summary>Display name of the candidate printer.</summary>
    public string PrinterName { get; set; } = string.Empty;

    /// <summary>Weighted average score (0–100). Zero if eliminated.</summary>
    public double Score { get; set; }

    /// <summary>Per-factor score breakdown for transparency.</summary>
    public Dictionary<string, FactorScoreDto> ScoreBreakdown { get; set; } = [];

    /// <summary>Whether this printer was eliminated by a hard requirement.</summary>
    public bool Eliminated { get; set; }

    /// <summary>Human-readable reasons for elimination.</summary>
    public List<string> EliminationReasons { get; set; } = [];
}

/// <summary>
/// Individual scoring factor detail for API responses.
/// </summary>
public class FactorScoreDto
{
    /// <summary>Human-readable factor name.</summary>
    public string FactorName { get; set; } = string.Empty;

    /// <summary>Raw score (0–100).</summary>
    public double Score { get; set; }

    /// <summary>Weight used in calculation.</summary>
    public double Weight { get; set; }

    /// <summary>Score × Weight.</summary>
    public double WeightedScore { get; set; }
}

/// <summary>
/// Request body for dispatching a job to a specific printer.
/// </summary>
public class DispatchJobDto
{
    /// <summary>The printer to dispatch the job to.</summary>
    public Guid PrinterId { get; set; }
}

/// <summary>
/// Auto-dispatch settings exposed via API.
/// </summary>
public class DispatchSettingsDto
{
    public bool AutoDispatchEnabled { get; set; }

    public AutoDispatchMode AutoDispatchMode { get; set; }

    public int IdleThresholdSeconds { get; set; }

    public double MinimumScoreThreshold { get; set; }

    public int MaxConcurrentDispatches { get; set; }

    public LoadBalancingStrategy LoadBalancingStrategy { get; set; }

    public DateTime UpdatedAt { get; set; }
}

/// <summary>
/// Request body for updating auto-dispatch settings.
/// </summary>
public class UpdateDispatchSettingsDto
{
    public bool AutoDispatchEnabled { get; set; }

    public AutoDispatchMode AutoDispatchMode { get; set; }

    public int IdleThresholdSeconds { get; set; }

    public double MinimumScoreThreshold { get; set; }

    public int MaxConcurrentDispatches { get; set; }

    public LoadBalancingStrategy LoadBalancingStrategy { get; set; }
}

/// <summary>
/// SignalR event payload when a job is auto-dispatched.
/// </summary>
public class JobAutoDispatchedEvent
{
    public Guid JobId { get; set; }

    public string JobName { get; set; } = string.Empty;

    public Guid PrinterId { get; set; }

    public string PrinterName { get; set; } = string.Empty;

    public double Score { get; set; }

    public AutoDispatchMode Mode { get; set; }
}

/// <summary>
/// SignalR event payload when auto-dispatch suggests a job (Suggest mode).
/// </summary>
public class DispatchSuggestionEvent
{
    public Guid JobId { get; set; }

    public string JobName { get; set; } = string.Empty;

    public Guid PrinterId { get; set; }

    public string PrinterName { get; set; } = string.Empty;

    public double Score { get; set; }
}

/// <summary>
/// SignalR event payload when an auto-dispatch attempt fails.
/// </summary>
public class DispatchFailedEvent
{
    public Guid? JobId { get; set; }

    public Guid PrinterId { get; set; }

    public string PrinterName { get; set; } = string.Empty;

    public string Reason { get; set; } = string.Empty;
}

// --- Phase 3: Batch Dispatch & Load Balancing ---

/// <summary>
/// Request body for batch-dispatching multiple queued jobs at once.
/// </summary>
public class BatchDispatchRequest
{
    /// <summary>
    /// Specific job IDs to dispatch. If empty or null, dispatches all unassigned queued jobs.
    /// </summary>
    public List<Guid>? JobIds { get; set; }

    /// <summary>
    /// If true, dispatch all unassigned queued jobs (ignores JobIds).
    /// </summary>
    public bool DispatchAll { get; set; }

    /// <summary>
    /// Optional override for load-balancing strategy.
    /// If null, uses the system-wide DispatchSettings.LoadBalancingStrategy.
    /// </summary>
    public LoadBalancingStrategy? Strategy { get; set; }
}

/// <summary>
/// Result of a batch dispatch operation.
/// </summary>
public class BatchDispatchResult
{
    /// <summary>Number of jobs successfully dispatched.</summary>
    public int DispatchedCount { get; set; }

    /// <summary>Number of jobs that could not be dispatched.</summary>
    public int FailedCount { get; set; }

    /// <summary>Number of jobs that were skipped (no eligible printers).</summary>
    public int SkippedCount { get; set; }

    /// <summary>Total jobs evaluated.</summary>
    public int TotalCount { get; set; }

    /// <summary>Per-job dispatch results.</summary>
    public List<BatchDispatchItemResult> Results { get; set; } = [];
}

/// <summary>
/// Individual dispatch result for a single job in a batch operation.
/// </summary>
public class BatchDispatchItemResult
{
    public Guid JobId { get; set; }

    public string JobName { get; set; } = string.Empty;

    /// <summary>Dispatched, Skipped, or Failed.</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>Printer assigned (null if skipped/failed).</summary>
    public Guid? PrinterId { get; set; }

    /// <summary>Printer name (null if skipped/failed).</summary>
    public string? PrinterName { get; set; }

    /// <summary>Score the assigned printer received (null if skipped/failed).</summary>
    public double? Score { get; set; }

    /// <summary>Reason for skip or failure (null if dispatched).</summary>
    public string? Reason { get; set; }
}

/// <summary>
/// Queue status overview for the dispatch dashboard.
/// </summary>
public class DispatchQueueStatusDto
{
    /// <summary>Number of unassigned queued jobs waiting for dispatch.</summary>
    public int PendingUnassignedJobs { get; set; }

    /// <summary>Total jobs currently in the queue (all statuses).</summary>
    public int TotalQueuedJobs { get; set; }

    /// <summary>Number of printers currently idle.</summary>
    public int IdlePrinters { get; set; }

    /// <summary>Number of printers currently printing.</summary>
    public int BusyPrinters { get; set; }

    /// <summary>Per-printer queue depth breakdown.</summary>
    public List<PrinterQueueDepthDto> PrinterQueueDepths { get; set; } = [];

    /// <summary>Overall dispatch statistics.</summary>
    public DispatchStatsDto Stats { get; set; } = new();
}

/// <summary>
/// Queue depth for a single printer.
/// </summary>
public class PrinterQueueDepthDto
{
    public Guid PrinterId { get; set; }

    public string PrinterName { get; set; } = string.Empty;

    /// <summary>Number of jobs assigned to this printer (queued + printing).</summary>
    public int QueueDepth { get; set; }

    /// <summary>Whether the printer is currently printing.</summary>
    public bool IsPrinting { get; set; }

    /// <summary>Whether the printer is enabled and available.</summary>
    public bool IsAvailable { get; set; }
}

/// <summary>
/// Aggregate dispatch statistics.
/// </summary>
public class DispatchStatsDto
{
    /// <summary>Total dispatches in the last 24 hours.</summary>
    public int DispatchesLast24Hours { get; set; }

    /// <summary>Average dispatch score in the last 24 hours.</summary>
    public double AverageScoreLast24Hours { get; set; }

    /// <summary>Number of auto-dispatches in the last 24 hours.</summary>
    public int AutoDispatchesLast24Hours { get; set; }

    /// <summary>Number of failed dispatches in the last 24 hours.</summary>
    public int FailedDispatchesLast24Hours { get; set; }
}

/// <summary>
/// A single dispatch history entry.
/// </summary>
public class DispatchHistoryDto
{
    public Guid Id { get; set; }

    public Guid PrintJobId { get; set; }

    public string? JobName { get; set; }

    public Guid PrinterId { get; set; }

    public string? PrinterName { get; set; }

    public DispatchAction Action { get; set; }

    public double? Score { get; set; }

    public string? Reason { get; set; }

    public DateTime CreatedAtUtc { get; set; }
}

/// <summary>
/// SignalR event payload when a batch dispatch operation starts.
/// </summary>
public class BatchDispatchStartedEvent
{
    /// <summary>Unique identifier for this batch operation.</summary>
    public Guid BatchId { get; set; }

    /// <summary>Number of jobs being dispatched.</summary>
    public int JobCount { get; set; }

    /// <summary>Load-balancing strategy used.</summary>
    public LoadBalancingStrategy Strategy { get; set; }
}

/// <summary>
/// SignalR event payload when a batch dispatch operation completes.
/// </summary>
public class BatchDispatchCompletedEvent
{
    /// <summary>Unique identifier matching the started event.</summary>
    public Guid BatchId { get; set; }

    /// <summary>Number of jobs successfully dispatched.</summary>
    public int DispatchedCount { get; set; }

    /// <summary>Number of jobs that failed dispatch.</summary>
    public int FailedCount { get; set; }

    /// <summary>Number of jobs skipped (no eligible printers).</summary>
    public int SkippedCount { get; set; }
}
