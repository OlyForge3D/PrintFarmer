using System;
using System.Threading.Tasks;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Microsoft.Extensions.Logging;

namespace Farm.Web.Api.Services.PrintJobs
{
    public class PrintApprovalService : IPrintApprovalService
    {
        private readonly AppDbContext _dbContext;
        private readonly ILogger<PrintApprovalService> _logger;

        public PrintApprovalService(
            AppDbContext dbContext,
            ILogger<PrintApprovalService> logger)
        {
            _dbContext = dbContext;
            _logger = logger;
        }

        public async Task<Guid> CreatePendingApprovalAsync(Guid printJobId, Guid? printerId, string? requestedBy)
        {
            var approval = new PrintApproval
            {
                Id = Guid.NewGuid(),
                PrintJobId = printJobId,
                PrinterId = printerId,
                RequestedBy = requestedBy,
                CreatedAt = DateTimeOffset.UtcNow
            };

            _dbContext.Set<PrintApproval>().Add(approval);
            await _dbContext.SaveChangesAsync();

            _logger.LogInformation(
                "Created pending approval {ApprovalId} for print job {PrintJobId}",
                approval.Id,
                printJobId);

            return approval.Id;
        }

        public async Task<bool> ApproveAsync(Guid approvalId, string? approvedBy)
        {
            var approval = await _dbContext.Set<PrintApproval>().FindAsync(approvalId);

            if (approval == null)
            {
                _logger.LogWarning("Approval {ApprovalId} not found", approvalId);
                return false;
            }

            // Remove the approval (approved jobs don't need the approval record anymore)
            _dbContext.Set<PrintApproval>().Remove(approval);
            await _dbContext.SaveChangesAsync();

            _logger.LogInformation(
                "Approved print job {PrintJobId} (approval {ApprovalId}) by {ApprovedBy}",
                approval.PrintJobId,
                approvalId,
                approvedBy ?? "system");

            return true;
        }
    }
}
