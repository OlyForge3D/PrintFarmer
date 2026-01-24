using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure;

namespace Farm.Web.Api.Services.Queue
{
    /// <summary>
    /// Service for managing print job queues and job operations.
    /// </summary>
    public interface IJobQueueService
    {
        /// <summary>
        /// Gets queue overview for all available printers, optionally filtered by model compatibility.
        /// </summary>
        /// <param name="requiredModel">Optional printer model name or alias to filter by (e.g., "COREONEL", "Prusa MK4")</param>
        /// <param name="ct">Cancellation token</param>
        /// <returns>List of compatible printers with their queue status</returns>
        Task<IReadOnlyList<QueueOverviewDto>> GetQueueOverviewAsync(string? requiredModel, CancellationToken ct);

        /// <summary>Gets all jobs in a printer's queue.</summary>
        Task<IReadOnlyList<JobQueuePrintJobDto>> GetPrinterQueueAsync(Guid printerId, CancellationToken ct);

        /// <summary>Adds a job to a printer's queue.</summary>
        Task<JobQueuePrintJobDto?> AddJobToQueueAsync(QueuePrintJobDto request, CancellationToken ct);

        /// <summary>Gets a print job by ID.</summary>
        Task<JobQueuePrintJobDto?> GetJobAsync(Guid id, CancellationToken ct);

        /// <summary>Removes a job from the queue.</summary>
        Task<bool> RemoveJobAsync(Guid id, CancellationToken ct);

        /// <summary>Updates a job's priority in the queue.</summary>
        Task<JobQueuePrintJobDto?> UpdateJobPriorityAsync(Guid id, UpdateJobPriorityDto request, CancellationToken ct);

        /// <summary>Updates a job's status.</summary>
        Task<JobQueuePrintJobDto?> UpdateJobAsync(Guid id, UpdatePrintJobStatusDto request, CancellationToken ct);
    }
}
