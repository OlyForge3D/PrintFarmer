namespace Farm.Web.Api.DTOs.Retries;

/// <summary>
/// Represents a single job retry attempt record.
/// </summary>
public class JobRetryDto
{
    /// <summary>
    /// Unique identifier for this retry record.
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// The original job that failed.
    /// </summary>
    public Guid OriginalJobId { get; set; }

    /// <summary>
    /// The new job created to retry the work.
    /// </summary>
    public Guid RetryJobId { get; set; }

    /// <summary>
    /// Attempt number (1 = first retry, 2 = second retry, etc.).
    /// </summary>
    public int AttemptNumber { get; set; }

    /// <summary>
    /// The error category that triggered the retry (e.g., "Recoverable").
    /// </summary>
    public string ErrorCategory { get; set; } = string.Empty;

    /// <summary>
    /// Description of why the original job failed.
    /// </summary>
    public string FailureReason { get; set; } = string.Empty;

    /// <summary>
    /// Current status of the retry (e.g., "Pending", "Executing", "Completed", "Failed").
    /// </summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>
    /// When this retry is scheduled to execute.
    /// </summary>
    public DateTime ScheduledRetryTime { get; set; }

    /// <summary>
    /// When the retry actually started executing (null if not yet executed).
    /// </summary>
    public DateTime? ActualRetryTime { get; set; }

    /// <summary>
    /// Optional notes about this retry attempt.
    /// </summary>
    public string? Notes { get; set; }

    /// <summary>
    /// When this retry record was created.
    /// </summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// When this retry record was last updated.
    /// </summary>
    public DateTime UpdatedAt { get; set; }
}
