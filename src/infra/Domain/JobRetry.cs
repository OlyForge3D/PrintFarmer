using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Diagnostics.CodeAnalysis;
using Farm.Infrastructure;
using Farm.Infrastructure.Annotations;

namespace Farm.Infrastructure.Domain;

/// <summary>
/// Retry history for failed print jobs
/// Tracks original failure, retry attempts, and outcomes
/// </summary>
public class JobRetry
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Original job ID that failed
    /// </summary>
    public Guid OriginalJobId { get; set; }

    /// <summary>
    /// Navigation property to original print job
    /// </summary>
    public virtual PrintJob? OriginalJob { get; set; }

    /// <summary>
    /// New job ID created for this retry attempt
    /// </summary>
    public Guid RetryJobId { get; set; }

    /// <summary>
    /// Navigation property to retry print job
    /// </summary>
    public virtual PrintJob? RetryJob { get; set; }

    /// <summary>
    /// Attempt number (1 = first retry, 2 = second retry, etc.)
    /// </summary>
    public int AttemptNumber { get; set; }

    /// <summary>
    /// Category of the original failure
    /// </summary>
    public ErrorCategory ErrorCategory { get; set; }

    /// <summary>
    /// Detailed failure reason from the printer/system
    /// </summary>
    public string FailureReason { get; set; } = string.Empty;

    /// <summary>
    /// When the retry was scheduled to begin
    /// </summary>
    public DateTime ScheduledRetryTime { get; set; }

    /// <summary>
    /// When the retry actually started
    /// </summary>
    public DateTime? ActualRetryTime { get; set; }

    /// <summary>
    /// Status: Pending, Running, Succeeded, Failed
    /// </summary>
    public string Status { get; set; } = "Pending"; // Pending, Running, Succeeded, Failed

    /// <summary>
    /// Additional notes about the retry attempt
    /// </summary>
    public string? Notes { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
