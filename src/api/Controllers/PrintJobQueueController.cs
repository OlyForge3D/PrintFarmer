using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Telemetry;
using Farm.Web.Api.Services.PrintJobQueue;
using Microsoft.AspNetCore.Mvc;
using PrintJobDto = Farm.Web.Api.Services.PrintJobQueue.PrintJobDto;

namespace Farm.Web.Api.Controllers;

[ApiController]
[Route("api/print-job-queue")]
[Tags("Print Job Queue (New)")]
public class PrintJobQueueController(IPrintJobQueueService service, IUnifiedLoggingService logger) : ControllerBase
{
    private readonly IPrintJobQueueService _service = service;
    private readonly IUnifiedLoggingService _logger = logger;

    [HttpGet]
    public async Task<ActionResult<IEnumerable<PrintJobDto>>> GetAllAsync(CancellationToken cancellationToken)
    {
        try
        {
            var all = await _service.GetAllAsync(cancellationToken).ConfigureAwait(false);
            return Ok(all);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching print job queue");
            return Problem("Error fetching print job queue", statusCode: 500);
        }
    }

    [HttpPost]
    public async Task<ActionResult<PrintJobDto>> EnqueueAsync([FromBody] EnqueuePrintJobRequest req, CancellationToken cancellationToken)
    {
        if (req == null)
        {
            return BadRequest("Request body required");
        }

        try
        {
            var added = await _service.EnqueueAsync(req, cancellationToken).ConfigureAwait(false);
            if (added == null)
            {
                return BadRequest("Could not enqueue job");
            }

            return CreatedAtAction(nameof(GetByIdAsync), new { id = added.Id }, added);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error enqueueing print job");
            return Problem("Error enqueueing print job", statusCode: 500);
        }
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<PrintJobDto>> GetByIdAsync(Guid id, CancellationToken cancellationToken)
    {
        var job = await _service.GetAsync(id, cancellationToken).ConfigureAwait(false);
        if (job == null)
        {
            return NotFound();
        }

        return Ok(job);
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        var ok = await _service.RemoveAsync(id, cancellationToken).ConfigureAwait(false);
        if (!ok)
        {
            return NotFound();
        }

        return NoContent();
    }
}

// Service interface and DTOs live in Farm.Web.Api.Services.PrintJobQueue.IPrintJobQueueService
