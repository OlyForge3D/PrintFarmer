using Farm.Infrastructure.Domain.Enums;
using Farm.Infrastructure.Services.Queue.Dispatch;

namespace Farm.Infrastructure.Domain;

/// <summary>
/// Audit trail entry for dispatch operations.
/// Records every scoring, dispatch, rejection, or failure for traceability.
/// </summary>
public class DispatchLog
{
    public Guid Id { get; set; }

    /// <summary>The print job being dispatched.</summary>
    public Guid PrintJobId { get; set; }

    public PrintJob? PrintJob { get; set; }

    /// <summary>The printer evaluated or dispatched to.</summary>
    public Guid PrinterId { get; set; }

    public Printer? Printer { get; set; }

    /// <summary>What happened: Suggested, Dispatched, Rejected, or Failed.</summary>
    public DispatchAction Action { get; set; }

    /// <summary>
    /// How the dispatch was initiated: Manual, Suggested, or Auto.
    /// Maps to <see cref="DispatchMode"/> enum on PrintJob.
    /// </summary>
    public DispatchMode DispatchMode { get; set; } = DispatchMode.Manual;

    /// <summary>The printer's total weighted score at the time of this action.</summary>
    public double? Score { get; set; }

    /// <summary>JSON-serialized score breakdown for historical reference.</summary>
    public string? ScoreBreakdown { get; set; }

    /// <summary>
    /// Full scoring breakdown stored as JSON for audit purposes.
    /// Contains per-factor scores, weights, and elimination reasons.
    /// </summary>
    public string? ScoringDetails { get; set; }

    /// <summary>Human-readable reason for the action (e.g., elimination reason, user choice).</summary>
    public string? Reason { get; set; }

    /// <summary>When the dispatch was initiated.</summary>
    public DateTimeOffset? DispatchedAt { get; set; }

    /// <summary>
    /// User who triggered the dispatch. Null for auto-dispatch operations.
    /// </summary>
    public string? DispatchedByUserId { get; set; }

    /// <summary>
    /// Outcome of the dispatch operation.
    /// </summary>
    public DispatchStatus Status { get; set; } = DispatchStatus.Pending;

    /// <summary>
    /// Error details when Status is Failed.
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>UTC timestamp of when this action occurred.</summary>
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>When this record was created.</summary>
    public DateTimeOffset CreatedDate { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>When this record was last updated.</summary>
    public DateTimeOffset UpdatedDate { get; set; } = DateTimeOffset.UtcNow;
}
