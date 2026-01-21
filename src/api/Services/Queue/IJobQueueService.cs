using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure;

namespace Farm.Web.Api.Services.Queue
{
    public interface IJobQueueService
    {
        /// <summary>
        /// Gets queue overview for all available printers, optionally filtered by model compatibility.
        /// </summary>
        /// <param name="requiredModel">Optional printer model name or alias to filter by (e.g., "COREONEL", "Prusa MK4")</param>
        /// <param name="ct">Cancellation token</param>
        /// <returns>List of compatible printers with their queue status</returns>
        Task<IReadOnlyList<QueueOverviewDto>> GetQueueOverviewAsync(string? requiredModel, CancellationToken ct);

        Task<IReadOnlyList<JobQueuePrintJobDto>> GetPrinterQueueAsync(Guid printerId, CancellationToken ct);

        Task<JobQueuePrintJobDto?> AddJobToQueueAsync(QueuePrintJobDto request, CancellationToken ct);

        Task<JobQueuePrintJobDto?> GetJobAsync(Guid id, CancellationToken ct);

        Task<bool> RemoveJobAsync(Guid id, CancellationToken ct);

        Task<JobQueuePrintJobDto?> UpdateJobPriorityAsync(Guid id, UpdateJobPriorityDto request, CancellationToken ct);

        Task<JobQueuePrintJobDto?> UpdateJobAsync(Guid id, UpdatePrintJobStatusDto request, CancellationToken ct);
    }
}
