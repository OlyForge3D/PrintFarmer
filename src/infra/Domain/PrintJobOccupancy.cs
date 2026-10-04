using Farm.Infrastructure;

namespace Farm.Infrastructure.Domain;

/// <summary>
/// Defines which print-job states physically reserve a printer.
/// </summary>
internal static class PrintJobOccupancy
{
    internal static IReadOnlyList<PrintJobStatus> Statuses { get; } =
    [
        PrintJobStatus.Starting,
        PrintJobStatus.Printing,
        PrintJobStatus.Paused,
    ];

    /// <summary>Returns whether the status represents a job that still occupies the printer.</summary>
    internal static bool OccupiesPrinter(this PrintJobStatus status) =>
        Statuses.Contains(status);

    /// <summary>Filters a print-job query to jobs that still occupy their assigned printer.</summary>
    internal static IQueryable<PrintJob> WhereOccupiesPrinter(this IQueryable<PrintJob> jobs) =>
        jobs.Where(job => Statuses.Contains(job.Status));

    /// <summary>
    /// Excludes jobs released by the indeterminate-claim escape hatch (issue #2859). Such a job
    /// may already be printing physically, so no automatic selector may pick it until an
    /// operator deliberately clears the block.
    /// </summary>
    internal static IQueryable<PrintJob> WhereNotOperatorRecoveryBlocked(this IQueryable<PrintJob> jobs) =>
        jobs.Where(job => job.BlockedReasonCode == null
            || job.BlockedReasonCode != JobBlockedReasonCode.OperatorRecoveryRequired);
}
