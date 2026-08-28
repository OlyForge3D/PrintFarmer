using System;
using System.Linq;
using System.Threading.Tasks;
using Farm.Infrastructure.Authorization;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.PrintJobs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Farm.Modules.PrintQueue.Controllers;

[ApiController]
[Route("api/print-approvals")]
[Authorize]

// S6960: Sonar suggests splitting this controller into 2 smaller ones. Its endpoints are
// cohesive approve/reject/list operations over the same print-approval workflow and share the
// same authorization/DI surface; splitting would add controller-count/routing overhead without
// improving readability or testability. Deliberately not refactored — tracked as a design
// decision, not a defect, per issue #2094.
#pragma warning disable S6960
public class PrintApprovalsController(IPrintApprovalService approvalService, Farm.Infrastructure.Repositories.PrintJobs.IPrintApprovalRepository? repo = null) : ControllerBase
{
    private readonly IPrintApprovalService _approvalService = approvalService;
    private readonly Farm.Infrastructure.Repositories.PrintJobs.IPrintApprovalRepository? _repo = repo;

    [HttpGet]
    public async Task<IActionResult> GetPendingAsync()
    {
        if (_repo == null)
        {
            return Ok(Array.Empty<object>());
        }

        IEnumerable<PrintApproval> list = await _repo.ListPendingAsync();
        var dto = list.Select(x => new { approvalId = x.Id, printJobId = x.PrintJobId, printerId = x.PrinterId, requestedBy = x.RequestedBy, createdAt = x.CreatedAt });
        return Ok(dto);
    }

    [RequirePermission("job_queue", "admin")]
    [HttpPost("{id:guid}/approve")]
    public async Task<IActionResult> ApproveAsync([FromRoute] Guid id)
    {
        bool ok = await _approvalService.ApproveAsync(id, User?.Identity?.Name);
        return !ok ? NotFound() : NoContent();
    }

    [RequirePermission("job_queue", "admin")]
    [HttpPost("{id:guid}/reject")]
    public async Task<IActionResult> RejectAsync([FromRoute] Guid id)
    {
        if (_repo == null)
        {
            return NotFound();
        }

        PrintApproval? approval = await _repo.GetAsync(id);
        if (approval is null)
        {
            return NotFound();
        }

        await _repo.RemoveAsync(approval);
        return NoContent();
    }
}
#pragma warning restore S6960
