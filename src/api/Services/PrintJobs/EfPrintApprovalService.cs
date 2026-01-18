using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Farm.Infrastructure.Domain;
using Farm.Web.Api.Data.Repositories;
using Farm.Web.Api.Services.PrintJobQueue;

namespace Farm.Web.Api.Services.PrintJobs
{
    public class EfPrintApprovalService(IPrintApprovalRepository repo, IPrintJobQueueService queueService) : IPrintApprovalService
    {
        private readonly IPrintApprovalRepository _repo = repo ?? throw new ArgumentNullException(nameof(repo));
        private readonly IPrintJobQueueService _queueService = queueService ?? throw new ArgumentNullException(nameof(queueService));

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
            var approval = await _repo.GetAsync(approvalId);
            if (approval is null)
            {
                return false;
            }

            var req = new EnqueuePrintJobRequest(approval.PrintJobId, approval.PrinterId, priority: null, requiredNozzleDiameter: null, requiredMaterialType: null);
            var enqueued = await _queueService.EnqueueAsync(req);
            if (enqueued is not null)
            {
                await _repo.RemoveAsync(approval);
                return true;
            }

            return false;
        }
    }
}
