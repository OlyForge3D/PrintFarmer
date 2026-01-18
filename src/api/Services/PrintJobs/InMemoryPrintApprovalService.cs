using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;

namespace Farm.Web.Api.Services.PrintJobs
{
    public class InMemoryPrintApprovalService : IPrintApprovalService
    {
        private readonly ConcurrentDictionary<Guid, (Guid PrintJobId, Guid? PrinterId, string? RequestedBy)> _pending = new();

        public Task<Guid> CreatePendingApprovalAsync(Guid printJobId, Guid? printerId, string? requestedBy)
        {
            var id = Guid.NewGuid();
            _pending[id] = (printJobId, printerId, requestedBy);
            return Task.FromResult(id);
        }

        public Task<bool> ApproveAsync(Guid approvalId, string? approvedBy)
        {
            if (!_pending.TryRemove(approvalId, out var entry))
            {
                return Task.FromResult(false);
            }

            // TODO: enqueue the print job to the actual queue; this is just a scaffold
            return Task.FromResult(true);
        }
    }
}
