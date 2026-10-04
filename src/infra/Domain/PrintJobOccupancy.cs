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
}
