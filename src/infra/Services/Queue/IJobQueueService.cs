using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure;

namespace Farm.Infrastructure.Services.Queue;

/// <summary>
/// Service for managing print job queues and job operations.
/// </summary>
public interface IJobQueueService
{
    /// <summary>
    /// Gets queue overview for all available printers, filtered by compatibility criteria.
    /// All filtering happens server-side for consistency with auto-assign logic.
    /// </summary>
    /// <param name="requiredModel">Optional printer model name or alias (e.g., "COREONEL", "Prusa MK4")</param>
    /// <param name="requiredNozzle">Optional required nozzle diameter in mm (exact match ±0.01mm)</param>
    /// <param name="requiredMaterial">Optional required material type (case-insensitive)</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>List of compatible printers with their queue status</returns>
    Task<IReadOnlyList<QueueOverviewDto>> GetQueueOverviewAsync(string? requiredModel, decimal? requiredNozzle, string? requiredMaterial, CancellationToken ct);

    /// <summary>Gets all jobs in a printer's queue.</summary>
    Task<IReadOnlyList<JobQueuePrintJobDto>> GetPrinterQueueAsync(Guid printerId, CancellationToken ct);

    /// <summary>
    /// Adds a job to a printer's queue.
    /// When <paramref name="userId"/> is provided, printer group ACL is enforced.
    /// When null, the caller is trusted (API-key / system callers).
    /// </summary>
    /// <exception cref="QueueGroupAccessDeniedException">Thrown when the user lacks Submit access to the target printer group.</exception>
    Task<JobQueuePrintJobDto?> AddJobToQueueAsync(QueuePrintJobDto request, Guid? userId, CancellationToken ct);

    /// <summary>Gets a print job by ID.</summary>
    Task<JobQueuePrintJobDto?> GetJobAsync(Guid id, CancellationToken ct);

    /// <summary>Removes a job from the queue.</summary>
    /// <param name="id">Job identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns><see langword="true"/> when the job was removed.</returns>
    Task<bool> RemoveJobAsync(Guid id, CancellationToken ct);

    /// <summary>
    /// Removes a job from the queue, enforcing a caller-supplied <c>If-Match</c> revision.
    /// </summary>
    /// <param name="id">Job identifier.</param>
    /// <param name="ifMatchJobRowVersion">
    /// Base-64 job ETag. Pass <see langword="null"/> only for trusted internal callers.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns><see langword="true"/> when the job was removed.</returns>
    Task<bool> RemoveJobAsync(Guid id, string? ifMatchJobRowVersion, CancellationToken ct);

    /// <summary>Removes a public caller's authorized job with an ETag precondition.</summary>
    Task<bool> RemoveJobAsync(
        Guid id,
        string? ifMatchJobRowVersion,
        string actorSubject,
        CancellationToken ct) =>
        RemoveJobAsync(id, ifMatchJobRowVersion, ct);

    /// <summary>Updates a job's priority in the queue.</summary>
    Task<JobQueuePrintJobDto?> UpdateJobPriorityAsync(Guid id, UpdateJobPriorityDto request, CancellationToken ct);

    /// <summary>Updates an authorized public caller's job priority.</summary>
    Task<JobQueuePrintJobDto?> UpdateJobPriorityAsync(
        Guid id,
        UpdateJobPriorityDto request,
        string actorSubject,
        CancellationToken ct) =>
        UpdateJobPriorityAsync(id, request, ct);

    /// <summary>Updates a job's status.</summary>
    Task<JobQueuePrintJobDto?> UpdateJobAsync(Guid id, UpdatePrintJobStatusDto request, CancellationToken ct);

    /// <summary>Updates an authorized public caller's queue job.</summary>
    Task<JobQueuePrintJobDto?> UpdateJobAsync(
        Guid id,
        UpdatePrintJobStatusDto request,
        string actorSubject,
        CancellationToken ct) =>
        UpdateJobAsync(id, request, ct);
}
