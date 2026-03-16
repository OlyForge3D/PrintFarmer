namespace Farm.Infrastructure;

/// <summary>
/// DTO for failure detection events broadcast via SignalR.
/// Represents a detected print failure with confidence score and metadata.
/// </summary>
public sealed class FailureDetectionDto
{
    /// <summary>
    /// ID of the printer where the failure was detected.
    /// </summary>
    public Guid PrinterId { get; set; }

    /// <summary>
    /// Name of the printer.
    /// </summary>
    public string PrinterName { get; set; } = string.Empty;

    /// <summary>
    /// ID of the print job that was running when the failure was detected.
    /// Null if no job was tracked.
    /// </summary>
    public Guid? JobId { get; set; }

    /// <summary>
    /// Confidence score from the ML model (0.0 to 1.0).
    /// </summary>
    public decimal Confidence { get; set; }

    /// <summary>
    /// Timestamp when the failure was detected.
    /// </summary>
    public DateTime DetectedAt { get; set; }

    /// <summary>
    /// Whether the job was automatically paused after detection.
    /// </summary>
    public bool AutoPaused { get; set; }
}
