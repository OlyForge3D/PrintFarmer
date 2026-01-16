using System;
using System.Linq;
using Microsoft.AspNetCore.Mvc;
using System.Threading.Tasks;
using Farm.Web.Api.Services.PrintJobs;

namespace Farm.Web.Api.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class PrintApprovalsController : ControllerBase
    {
        private readonly IPrintApprovalService _approvalService;
        private readonly Farm.Web.Api.Data.Repositories.IPrintApprovalRepository? _repo;

        public PrintApprovalsController(IPrintApprovalService approvalService, Farm.Web.Api.Data.Repositories.IPrintApprovalRepository? repo = null)
        {
            _approvalService = approvalService;
            _repo = repo;
        }

        [HttpGet]
        public async Task<IActionResult> GetPendingAsync()
        {
            if (_repo == null)
            {
                return Ok(Array.Empty<object>());
            }

            var list = await _repo.ListPendingAsync();
            var dto = list.Select(x => new { approvalId = x.Id, printJobId = x.PrintJobId, printerId = x.PrinterId, requestedBy = x.RequestedBy, createdAt = x.CreatedAt });
            return Ok(dto);
        }

        [HttpPost("{id:guid}/approve")]
        public async Task<IActionResult> ApproveAsync([FromRoute] Guid id)
        {
            var ok = await _approvalService.ApproveAsync(id, User?.Identity?.Name);
            if (!ok) return NotFound();
            return NoContent();
        }

        [HttpPost("{id:guid}/reject")]
        public async Task<IActionResult> RejectAsync([FromRoute] Guid id)
        {
            if (_repo == null) return NotFound();

            var approval = await _repo.GetAsync(id);
            if (approval is null) return NotFound();

            await _repo.RemoveAsync(approval);
            return NoContent();
        }
    }
}
