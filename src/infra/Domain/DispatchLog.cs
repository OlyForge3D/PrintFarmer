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

    /// <summary>The printer's total weighted score at the time of this action.</summary>
    public double? Score { get; set; }

    /// <summary>JSON-serialized score breakdown for historical reference.</summary>
    public string? ScoreBreakdown { get; set; }

    /// <summary>Human-readable reason for the action (e.g., elimination reason, user choice).</summary>
    public string? Reason { get; set; }

    /// <summary>UTC timestamp of when this action occurred.</summary>
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
