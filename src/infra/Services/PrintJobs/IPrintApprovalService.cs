using System;
using System.Threading.Tasks;

namespace Farm.Infrastructure.Services.PrintJobs;

/// <summary>
/// Service for managing print job approvals in controlled print workflows.
/// </summary>
public interface IPrintApprovalService
{
    /// <summary>Creates a pending approval request for a print job.</summary>
    /// <returns>The approval ID.</returns>
    Task<Guid> CreatePendingApprovalAsync(Guid printJobId, Guid? printerId, string? requestedBy);

    /// <summary>Approves a pending print job request.</summary>
    /// <returns>True if approved; false if approval not found or already processed.</returns>
    Task<bool> ApproveAsync(Guid approvalId, string? approvedBy);
}
