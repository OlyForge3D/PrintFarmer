namespace Farm.Infrastructure;

/// <summary>
/// Event types returned for a printer session timeline.
/// </summary>
public enum PrinterSessionTimelineEventType
{
    Queued = 0,
    Dispatched = 1,
    SessionStarted = 2,
    StateTransition = 3,
    FailureDetected = 4,
    SessionEnded = 5,
}

/// <summary>
/// Represents a single event within a printer session.
/// </summary>
public sealed class PrinterSessionTimelineEventDto
{
    /// <summary>
    /// Gets or sets the event type.
    /// </summary>
    public PrinterSessionTimelineEventType Type { get; set; }

    /// <summary>
    /// Gets or sets when the event occurred.
    /// </summary>
    public DateTime OccurredAt { get; set; }

    /// <summary>
    /// Gets or sets the short operator-facing summary.
    /// </summary>
    public string Summary { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the previous state for state-transition events.
    /// </summary>
    public string? FromState { get; set; }

    /// <summary>
    /// Gets or sets the new state for state-transition events.
    /// </summary>
    public string? ToState { get; set; }

    /// <summary>
    /// Gets or sets the duration spent in the previous state.
    /// </summary>
    public int? DurationSeconds { get; set; }

    /// <summary>
    /// Gets or sets the ML confidence for failure-detection events.
    /// </summary>
    public decimal? Confidence { get; set; }

    /// <summary>
    /// Gets or sets whether the print was auto-paused.
    /// </summary>
    public bool? AutoPaused { get; set; }

    /// <summary>
    /// Gets or sets the snapshot URL associated with the event.
    /// </summary>
    public string? SnapshotUrl { get; set; }

    /// <summary>
    /// Gets or sets optional notes for the event.
    /// </summary>
    public string? Notes { get; set; }
}

/// <summary>
/// Represents a single print session for a printer.
/// </summary>
public sealed class PrinterSessionTimelineSessionDto
{
    /// <summary>
    /// Gets or sets the print job identifier.
    /// </summary>
    public Guid JobId { get; set; }

    /// <summary>
    /// Gets or sets the display name for the job.
    /// </summary>
    public string JobName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the file name when it can be resolved.
    /// </summary>
    public string? FileName { get; set; }

    /// <summary>
    /// Gets or sets the current or terminal job status.
    /// </summary>
    public PrintJobStatus Status { get; set; }

    /// <summary>
    /// Gets or sets when the job entered the queue.
    /// </summary>
    public DateTime QueuedAt { get; set; }

    /// <summary>
    /// Gets or sets when the job was dispatched.
    /// </summary>
    public DateTime? DispatchedAt { get; set; }

    /// <summary>
    /// Gets or sets when the session started printing.
    /// </summary>
    public DateTime? StartedAt { get; set; }

    /// <summary>
    /// Gets or sets when the session ended.
    /// </summary>
    public DateTime? EndedAt { get; set; }

    /// <summary>
    /// Gets or sets the actual duration of the session in seconds.
    /// </summary>
    public int? DurationSeconds { get; set; }

    /// <summary>
    /// Gets or sets the failure reason when the job failed or was cancelled.
    /// </summary>
    public string? FailureReason { get; set; }

    /// <summary>
    /// Gets or sets whether the session contains a persisted failure-detection incident.
    /// </summary>
    public bool HasFailureIncident { get; set; }

    /// <summary>
    /// Gets or sets the count of failure-detection incidents linked to the session.
    /// </summary>
    public int FailureIncidentCount { get; set; }

    /// <summary>
    /// Gets or sets the ordered timeline events for this session.
    /// </summary>
    public List<PrinterSessionTimelineEventDto> Events { get; set; } = [];
}

/// <summary>
/// Represents the recent session timeline for a single printer.
/// </summary>
public sealed class PrinterSessionTimelineDto
{
    /// <summary>
    /// Gets or sets the printer identifier.
    /// </summary>
    public Guid PrinterId { get; set; }

    /// <summary>
    /// Gets or sets the printer display name.
    /// </summary>
    public string PrinterName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the number of sessions returned.
    /// </summary>
    public int ReturnedSessionCount { get; set; }

    /// <summary>
    /// Gets or sets the recent sessions for the printer.
    /// </summary>
    public List<PrinterSessionTimelineSessionDto> Sessions { get; set; } = [];
}
