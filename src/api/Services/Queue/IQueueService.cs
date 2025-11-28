using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure;

namespace Farm.Web.Api.Services.Queue
{
    public interface IQueueService
    {
        Task<IReadOnlyList<QueueOverviewDto>> GetQueueOverviewAsync(CancellationToken ct);
        Task<IReadOnlyList<JobQueuePrintJobDto>> GetPrinterQueueAsync(Guid printerId, CancellationToken ct);
        Task<JobQueuePrintJobDto?> AddJobToQueueAsync(QueuePrintJobDto request, CancellationToken ct);
        Task<JobQueuePrintJobDto?> GetJobAsync(Guid id, CancellationToken ct);
        Task<bool> RemoveJobAsync(Guid id, CancellationToken ct);
        Task<JobQueuePrintJobDto?> UpdateJobPriorityAsync(Guid id, UpdateJobPriorityDto request, CancellationToken ct);
        Task<JobQueuePrintJobDto?> UpdateJobAsync(Guid id, UpdatePrintJobStatusDto request, CancellationToken ct);
    }
}
