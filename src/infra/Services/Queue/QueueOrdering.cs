using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure.Services.Queue;

/// <summary>
/// Shared ordering selectors for cross-scope and single-scope queue consumers.
///
/// Semantics: higher <see cref="PrintJob.Priority"/> runs first
/// (<c>Urgent(3) → High(2) → Normal(1) → Low(0)</c>). Cross-scope consumers use FIFO
/// by queued timestamp because queue positions are only comparable within a scope.
/// Single-scope consumers use queue position, then queued timestamp and job id.
/// </summary>
public static class QueueOrdering
{
    /// <summary>
    /// Orders jobs across scopes by descending priority, then queued time, then id.
    /// Queue positions must not be compared across scopes.
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
    /// Orders jobs in one queue scope by priority, queue position, queued time and id.
    /// Callers must filter to one assigned printer or to the unassigned scope first.
    /// </summary>
    public static IOrderedQueryable<PrintJob> OrderWithinScope(this IQueryable<PrintJob> jobs)
    {
        ArgumentNullException.ThrowIfNull(jobs);

        return jobs
            .OrderByDescending(j => j.Priority)
            .ThenBy(j => j.QueuePosition)
            .ThenBy(j => j.QueuedAt)
            .ThenBy(j => j.Id);
    }

    /// <summary>In-memory counterpart of <see cref="OrderWithinScope(IQueryable{PrintJob})"/>.</summary>
    public static IOrderedEnumerable<PrintJob> OrderWithinScope(this IEnumerable<PrintJob> jobs)
    {
        ArgumentNullException.ThrowIfNull(jobs);

        return jobs
            .OrderByDescending(j => j.Priority)
            .ThenBy(j => j.QueuePosition)
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
