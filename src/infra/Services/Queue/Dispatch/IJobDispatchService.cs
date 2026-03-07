using Farm.Infrastructure.Dtos.PrintQueue;

namespace Farm.Infrastructure.Services.Queue.Dispatch;

/// <summary>
/// Orchestrates dispatch operations: finding candidates and assigning jobs to printers.
/// </summary>
public interface IJobDispatchService
{
    /// <summary>
    /// Finds and scores candidate printers for the specified job.
    /// </summary>
    /// <param name="jobId">The print job to find candidates for.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Scored candidates as DTOs ready for API response.</returns>
    Task<List<DispatchCandidateDto>> FindCandidatesAsync(Guid jobId, CancellationToken ct = default);

    /// <summary>
    /// Assigns a job to the specified printer and triggers print start.
    /// Records the dispatch in the audit log with the printer's score.
    /// </summary>
    /// <param name="jobId">The print job to dispatch.</param>
    /// <param name="printerId">The target printer.</param>
    /// <param name="userId">The user initiating the dispatch.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The updated job DTO.</returns>
    Task<QueuedPrintJobDto> DispatchJobAsync(Guid jobId, Guid printerId, string userId, CancellationToken ct = default);
}
