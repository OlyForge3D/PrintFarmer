using System;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.PrintJobs;
using Farm.Infrastructure.Services.PrintJobs;
using Farm.Infrastructure.Services.Queue;

namespace Farm.Infrastructure.Services.PrintJobs;

public class PrintApprovalService(IPrintApprovalRepository repo, IJobQueueService queueService) : IPrintApprovalService
{
    private readonly IPrintApprovalRepository _repo = repo ?? throw new ArgumentNullException(nameof(repo));
    private readonly IJobQueueService _queueService = queueService ?? throw new ArgumentNullException(nameof(queueService));

    public async Task<Guid> CreatePendingApprovalAsync(Guid printJobId, Guid? printerId, string? requestedBy)
    {
        var pa = new PrintApproval
        {
            Id = Guid.NewGuid(),
            PrintJobId = printJobId,
            PrinterId = printerId,
            RequestedBy = requestedBy,
            CreatedAt = DateTimeOffset.UtcNow
        };

        await _repo.AddAsync(pa);
        return pa.Id;
    }

    public async Task<bool> ApproveAsync(Guid approvalId, string? approvedBy)
    {
        PrintApproval? approval = await _repo.GetAsync(approvalId);
        if (approval is null)
        {
            return false;
        }

        var req = new QueuePrintJobDto
        {
            GcodeFileId = approval.PrintJobId,
            AssignedPrinterId = approval.PrinterId,
            Priority = PrintJobPriority.Normal,
            RequiredNozzleDiameter = null,
            RequiredMaterialType = null
        };
        JobQueuePrintJobDto? enqueued = await _queueService.AddJobToQueueAsync(req, null, CancellationToken.None);
        if (enqueued is not null)
        {
            await _repo.RemoveAsync(approval);
            return true;
        }

        return false;
    }
}
