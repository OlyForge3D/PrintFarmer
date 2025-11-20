using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Printers;
using Farm.Infrastructure.Telemetry;
using Farm.Web.Api.Repositories.Queue;
using Farm.Web.Shared;
using Microsoft.AspNetCore.Mvc;

namespace Farm.Web.Api.Controllers;

/// <summary>
/// Manages the print job queue and printer assignment
/// </summary>
[ApiController]
[Route("api/job-queue")]
[Tags("Print Job Queue")]
public class JobQueueController(Farm.Web.Api.Services.Queue.IJobQueueService queueService, IUnifiedLoggingService logger) : ControllerBase
{
    /// <summary>
    /// Get all jobs in the queue
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(IEnumerable<JobQueuePrintJobDto>), 200)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<IEnumerable<JobQueuePrintJobDto>>> GetQueueAsync()
    {
        try
        {
            var dtos = await queueService.GetQueueOverviewAsync(CancellationToken.None);
            return Ok(dtos);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error retrieving print job queue");
            return Problem("An error occurred while retrieving the queue", statusCode: 500);
        }
    }

    /// <summary>
    /// Add a new job to the queue
    /// </summary>
    [HttpPost]
    [ProducesResponseType(typeof(JobQueuePrintJobDto), 201)]
    [ProducesResponseType(400)]
    [ProducesResponseType(404)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<JobQueuePrintJobDto>> QueueJobAsync([FromBody] QueuePrintJobDto request)
    {
        if (request is null)
        {
            return BadRequest("Request body is required");
        }
        try
        {
            var added = await queueService.AddJobToQueueAsync(request, CancellationToken.None);
            if (added == null)
            {
                return NotFound($"G-code file with ID {request.GcodeFileId} not found or no available printer");
            }

            return CreatedAtAction(nameof(GetJobAsync), new { id = added.Id }, added);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, $"Error queueing print job for file {request.GcodeFileId}");
            return Problem("An error occurred while queueing the job", statusCode: 500);
        }
    }

    /// <summary>
    /// Get a specific job
    /// </summary>
    [HttpGet("{id}")]
    [ProducesResponseType(typeof(JobQueuePrintJobDto), 200)]
    [ProducesResponseType(404)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<JobQueuePrintJobDto>> GetJobAsync(Guid id)
    {
        try
        {
            var dto = await queueService.GetJobAsync(id, CancellationToken.None);
            if (dto == null)
            {
                return NotFound($"Print job with ID {id} not found");
            }

            return Ok(dto);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, $"Error retrieving print job {id}");
            return Problem("An error occurred while retrieving the job", statusCode: 500);
        }
    }

    /// <summary>
    /// Update job status, priority, or assignment
    /// </summary>
    [HttpPut("{id}")]
    [ProducesResponseType(typeof(JobQueuePrintJobDto), 200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(404)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<JobQueuePrintJobDto>> UpdateJobAsync(Guid id, [FromBody] UpdatePrintJobStatusDto request)
    {
        if (request is null)
        {
            return BadRequest("Request body is required");
        }
        try
        {
            var updated = await queueService.UpdateJobAsync(id, request, CancellationToken.None);
            if (updated == null)
            {
                // Service returns null for not found or invalid assignment; translate to proper HTTP
                return NotFound($"Print job with ID {id} not found or invalid printer assignment");
            }

            return Ok(updated);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, $"Error updating print job {id}");
            return Problem("An error occurred while updating the job", statusCode: 500);
        }
    }

    /// <summary>
    /// Delete a job from the queue
    /// </summary>
    [HttpDelete("{id}")]
    [ProducesResponseType(204)]
    [ProducesResponseType(400)]
    [ProducesResponseType(404)]
    [ProducesResponseType(500)]
    public async Task<IActionResult> DeleteJobAsync(Guid id)
    {
        try
        {
            var ok = await queueService.RemoveJobAsync(id, CancellationToken.None);
            if (!ok)
            {
                return BadRequest("Cannot delete the job (not found or currently printing)");
            }

            return NoContent();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, $"Error deleting print job {id}");
            return Problem("An error occurred while deleting the job", statusCode: 500);
        }
    }
}
