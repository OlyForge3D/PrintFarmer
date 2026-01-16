using System;
using System.Threading.Tasks;

namespace Farm.Web.Api.Services.PrintJobs
{
    public interface IPrintApprovalService
    {
        Task<Guid> CreatePendingApprovalAsync(Guid printJobId, Guid? printerId, string? requestedBy);
        Task<bool> ApproveAsync(Guid approvalId, string? approvedBy);
    }
}
