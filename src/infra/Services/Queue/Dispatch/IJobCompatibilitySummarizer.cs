using Farm.Infrastructure.Dtos.PrintQueue;

namespace Farm.Infrastructure.Services.Queue.Dispatch;

/// <summary>
/// Summarizes which printers can run unassigned queued jobs, using exactly the dispatch
/// scorer's hard-requirement eligibility rules (issue #3280).
/// </summary>
public interface IJobCompatibilitySummarizer
{
    /// <summary>
    /// Builds a compatibility summary for each requested job that exists and is an unassigned
    /// <c>Queued</c> job. Other job ids are omitted from the result.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, PrinterCompatibilitySummaryDto>> SummarizeCompatibilityAsync(
        IReadOnlyCollection<Guid> jobIds,
        CancellationToken ct = default);
}
