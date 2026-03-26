namespace Farm.Infrastructure.Domain;

/// <summary>
/// Persisted record of a failure-detection incident for a printer.
/// </summary>
public sealed class FailureDetectionIncident
{
    /// <summary>
    /// Gets or sets the incident identifier.
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// Gets or sets the printer identifier.
    /// </summary>
    public Guid PrinterId { get; set; }

    /// <summary>
    /// Gets or sets the printer that raised the incident.
    /// </summary>
    public Printer? Printer { get; set; }

    /// <summary>
    /// Gets or sets the active job identifier when known.
    /// </summary>
    public Guid? JobId { get; set; }

    /// <summary>
    /// Gets or sets the active job display name when known.
    /// </summary>
    public string? JobName { get; set; }

    /// <summary>
    /// Gets or sets the active file name when known.
    /// </summary>
    public string? FileName { get; set; }

    /// <summary>
    /// Gets or sets the model confidence that triggered the incident.
    /// </summary>
    public decimal Confidence { get; set; }

    /// <summary>
    /// Gets or sets when the incident was detected.
    /// </summary>
    public DateTime DetectedAt { get; set; }

    /// <summary>
    /// Gets or sets the snapshot URL used for the analysis when known.
    /// </summary>
    public string? SnapshotUrl { get; set; }

    /// <summary>
    /// Gets or sets whether the active job was auto-paused.
    /// </summary>
    public bool AutoPaused { get; set; }
}
