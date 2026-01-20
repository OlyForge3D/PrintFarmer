using System;
using System.Linq;
using System.Threading.Tasks;
using Farm.Infrastructure.Domain;
using Farm.Web.Api.Services.PrintJobs;
using Microsoft.AspNetCore.Mvc;

namespace Farm.Web.Api.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class PrintApprovalsController(IPrintApprovalService approvalService, Farm.Web.Api.Data.Repositories.IPrintApprovalRepository? repo = null) : ControllerBase
    {
        private readonly IPrintApprovalService _approvalService = approvalService;
        private readonly Farm.Web.Api.Data.Repositories.IPrintApprovalRepository? _repo = repo;

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

        [HttpPost("{id:guid}/approve")]
        public async Task<IActionResult> ApproveAsync([FromRoute] Guid id)
        {
            bool ok = await _approvalService.ApproveAsync(id, User?.Identity?.Name);
            return !ok ? NotFound() : NoContent();
        }

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
}
