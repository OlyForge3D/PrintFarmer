using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure.Services.Queue;

/// <summary>
/// The single shared queue-ordering selector used by every readiness, skip, scoring,
/// batch and ready-head query (issue #900, defect 12).
///
/// Semantics: higher <see cref="PrintJob.Priority"/> runs first
/// (<c>Urgent(3) → High(2) → Normal(1) → Low(0)</c>), then FIFO by queued timestamp,
/// then job id as a total-order tiebreak so results are deterministic across providers
/// and processes.
/// </summary>
public static class QueueOrdering
{
    /// <summary>
    /// Orders jobs by descending priority, then queued time, then id.
    /// Every ready-head / auto-dispatch / batch query MUST use this selector so operators
    /// never see one ordering in the UI and a different one during dispatch.
    /// </summary>
    /// <param name="jobs">Source query or sequence.</param>
    /// <returns>Deterministically ordered query.</returns>
    public static IOrderedQueryable<PrintJob> OrderByPriorityDescending(this IQueryable<PrintJob> jobs)
    {
        ArgumentNullException.ThrowIfNull(jobs);

        return jobs
            .OrderByDescending(j => j.Priority)
            .ThenBy(j => j.QueuedAt)
            .ThenBy(j => j.Id);
    }

    /// <summary>
    /// In-memory counterpart of <see cref="OrderByPriorityDescending(IQueryable{PrintJob})"/>.
    /// </summary>
    /// <param name="jobs">Source sequence.</param>
    /// <returns>Deterministically ordered sequence.</returns>
    public static IOrderedEnumerable<PrintJob> OrderByPriorityDescending(this IEnumerable<PrintJob> jobs)
    {
        ArgumentNullException.ThrowIfNull(jobs);

        return jobs
            .OrderByDescending(j => j.Priority)
            .ThenBy(j => j.QueuedAt)
            .ThenBy(j => j.Id);
    }

    /// <summary>
    /// Validates that a raw integer priority maps to a defined <see cref="PrintJobPriority"/>.
    /// Undefined priorities are rejected on create and on every mutation path.
    /// </summary>
    /// <param name="priority">Raw priority value supplied by a caller.</param>
    /// <returns><see langword="true"/> when the value is a defined priority.</returns>
    public static bool IsDefinedPriority(int priority) =>
        Enum.IsDefined(typeof(PrintJobPriority), (PrintJobPriority)priority);

    /// <summary>Human-readable message used when rejecting an undefined priority.</summary>
    /// <param name="priority">The rejected value.</param>
    /// <returns>Validation message.</returns>
    public static string UndefinedPriorityMessage(int priority) =>
        $"Priority value {priority} is not a valid PrintJobPriority. " +
        "Use Low (0), Normal (1), High (2), or Urgent (3).";
}
