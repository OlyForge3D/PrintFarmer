namespace Farm.Slicer.Module.Services;

/// <summary>
/// Event data for SliceJob lifecycle notifications.
/// </summary>
public class SliceJobEvent
{
    /// <summary>Type of event: JobQueued, JobStarted, JobProgress, JobCompleted, JobFailed, JobCancelled.</summary>
    public string EventType { get; set; } = string.Empty;

    /// <summary>Gets or sets the job identifier.</summary>
    public Guid JobId { get; set; }

    /// <summary>Gets or sets the user who submitted the job.</summary>
    public Guid UserId { get; set; }

    /// <summary>Gets or sets the optional target printer identifier.</summary>
    public Guid? PrinterId { get; set; }

    /// <summary>Gets or sets the current job status.</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>Gets or sets the progress percentage (0-100) for JobProgress events.</summary>
    public int ProgressPercent { get; set; }

    /// <summary>Gets or sets the progress message.</summary>
    public string? ProgressMessage { get; set; }

    /// <summary>Gets or sets when the job was queued.</summary>
    public DateTime QueuedAt { get; set; }

    /// <summary>Gets or sets when processing started.</summary>
    public DateTime? StartedAt { get; set; }

    /// <summary>Gets or sets when the job completed.</summary>
    public DateTime? CompletedAt { get; set; }

    /// <summary>Gets or sets the URL to result file for JobCompleted events.</summary>
    public string? ResultFileUrl { get; set; }

    /// <summary>Gets or sets the error message for JobFailed events.</summary>
    public string? ErrorMessage { get; set; }

    /// <summary>Gets or sets the estimated print time in seconds.</summary>
    public int? EstimatedPrintTimeSeconds { get; set; }

    /// <summary>Gets or sets the estimated filament usage in grams.</summary>
    public decimal? FilamentUsedGrams { get; set; }

    /// <summary>Gets or sets the worker identifier that processed this job.</summary>
    public Guid? WorkerId { get; set; }

    /// <summary>Gets or sets the job priority (0=Low, 1=Normal, 2=High, 3=Critical).</summary>
    public int Priority { get; set; }

    /// <summary>Gets or sets when this event was generated.</summary>
    public DateTime Timestamp { get; set; }
}
