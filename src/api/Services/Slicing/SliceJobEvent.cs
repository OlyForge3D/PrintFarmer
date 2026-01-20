namespace Farm.Web.Api.Services.Slicing;

/// <summary>
/// Event data for SliceJob lifecycle notifications
/// </summary>
public class SliceJobEvent
{
    /// <summary>
    /// Type of event: JobQueued, JobStarted, JobProgress, JobCompleted, JobFailed, JobCancelled
    /// </summary>
    public string EventType { get; set; } = string.Empty;

    /// <summary>
    /// Job ID
    /// </summary>
    public Guid JobId { get; set; }

    /// <summary>
    /// User who submitted the job
    /// </summary>
    public Guid UserId { get; set; }

    /// <summary>
    /// Target printer ID (optional)
    /// </summary>
    public Guid? PrinterId { get; set; }

    /// <summary>
    /// Current job status
    /// </summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>
    /// Progress percentage (0-100) for JobProgress events
    /// </summary>
    public int ProgressPercent { get; set; }

    /// <summary>
    /// Progress message for JobProgress events
    /// </summary>
    public string? ProgressMessage { get; set; }

    /// <summary>
    /// When the job was queued
    /// </summary>
    public DateTime QueuedAt { get; set; }

    /// <summary>
    /// When processing started (if applicable)
    /// </summary>
    public DateTime? StartedAt { get; set; }

    /// <summary>
    /// When the job completed (if applicable)
    /// </summary>
    public DateTime? CompletedAt { get; set; }

    /// <summary>
    /// URL to result file for JobCompleted events
    /// </summary>
    public string? ResultFileUrl { get; set; }

    /// <summary>
    /// Error message for JobFailed events
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// Estimated print time in seconds (for JobCompleted)
    /// </summary>
    public int? EstimatedPrintTimeSeconds { get; set; }

    /// <summary>
    /// Estimated filament usage in grams (for JobCompleted)
    /// </summary>
    public decimal? FilamentUsedGrams { get; set; }

    /// <summary>
    /// Worker ID that processed this job (if applicable)
    /// </summary>
    public Guid? WorkerId { get; set; }

    /// <summary>
    /// Job priority (0=Low, 1=Normal, 2=High, 3=Critical)
    /// </summary>
    public int Priority { get; set; }

    /// <summary>
    /// When this event was generated
    /// </summary>
    public DateTime Timestamp { get; set; }
}
