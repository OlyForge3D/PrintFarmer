using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Dtos.PrintQueue;
using Farm.Infrastructure.Repositories.Printers;
using Farm.Infrastructure.Repositories.Queue;
using Farm.Infrastructure.Services.Interfaces;
using Farm.Infrastructure.Services.Printers;
using Farm.Infrastructure.Services.Queue;
using Farm.Infrastructure.Services.Queue.Dispatch;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Farm.Web.Api.Controllers;

/// <summary>
/// Manages the print job queue and printer assignment
/// </summary>
[ApiController]
[Route("api/job-queue")]
[Tags("Print Job Queue")]
[Authorize]
public class JobQueueController(
    IJobQueueService queueService,
    IPrintJobManagementService printJobManagementService,
    IPrintJobCompletionService printJobCompletionService,
    IJobDispatchService jobDispatchService,
    IBatchDispatchService batchDispatchService,
    IPrinterStatusCacheReader printerStatusCache,
    ILogger<JobQueueController> logger) : ControllerBase
{
    /// <summary>
    /// Get queue overview with optional compatibility filtering.
    /// Filters printers by model, nozzle diameter, and/or material type.
    /// All filtering is case-insensitive. Nozzle matching uses ±0.01mm tolerance.
    /// </summary>
    /// <param name="model">Optional printer model name or slicer alias (e.g., "COREONEL", "Prusa MK4")</param>
    /// <param name="nozzle">Optional required nozzle diameter in mm (e.g., 0.4)</param>
    /// <param name="material">Optional required material type (e.g., "PLA", "PCTG")</param>
    [HttpGet]
    [AllowAnonymous]
    [ProducesResponseType(typeof(IEnumerable<QueueOverviewDto>), 200)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<IEnumerable<QueueOverviewDto>>> GetQueueAsync(
        [FromQuery] string? model = null,
        [FromQuery] decimal? nozzle = null,
        [FromQuery] string? material = null)
    {
        try
        {
            IReadOnlyList<QueueOverviewDto> dtos = await queueService.GetQueueOverviewAsync(model, nozzle, material, CancellationToken.None);
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
    /// <param name="request">The print job request containing G-code file ID and optional settings.</param>
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
            JobQueuePrintJobDto? added = await queueService.AddJobToQueueAsync(request, CancellationToken.None);
            if (added == null)
            {
                return NotFound($"G-code file with ID {request.GcodeFileId} not found or no available printer");
            }

            // Return 201 Created with location header
            string location = $"/api/job-queue/{added.Id}";
            return Created(location, added);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error queueing print job for file {RequestGcodeFileId}", request.GcodeFileId);
            return Problem("An error occurred while queueing the job", statusCode: 500);
        }
    }

    /// <summary>
    /// Get a specific job
    /// </summary>
    /// <param name="id">The unique identifier of the job to retrieve.</param>
    [HttpGet("{id:guid}")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(JobQueuePrintJobDto), 200)]
    [ProducesResponseType(404)]
    [ProducesResponseType(500)]
    public async Task<ActionResult<JobQueuePrintJobDto>> GetJobAsync(Guid id)
    {
        try
        {
            JobQueuePrintJobDto? dto = await queueService.GetJobAsync(id, CancellationToken.None);
            return dto == null ? NotFound($"Print job with ID {id} not found") : Ok(dto);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error retrieving print job {Id}", id);
            return Problem("An error occurred while retrieving the job", statusCode: 500);
        }
    }

    /// <summary>
    /// Update job status, priority, or assignment
    /// </summary>
    /// <param name="id">The unique identifier of the job to update.</param>
    /// <param name="request">The update request containing new status, priority, or assignment.</param>
    [HttpPut("{id:guid}")]
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
            JobQueuePrintJobDto? updated = await queueService.UpdateJobAsync(id, request, CancellationToken.None);
            if (updated == null)
            {
                // Service returns null for not found or invalid assignment; translate to proper HTTP
                return NotFound($"Print job with ID {id} not found or invalid printer assignment");
            }

            return Ok(updated);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error updating print job {Id}", id);
            return Problem("An error occurred while updating the job", statusCode: 500);
        }
    }

    /// <summary>
    /// Dispatch a queued/assigned job to its printer to start printing.
    /// The job must have an assigned printer and be in Queued or Assigned status.
    /// </summary>
    /// <param name="id">The unique identifier of the job to dispatch.</param>
    /// <returns>The updated job with Starting/Printing status.</returns>
    [HttpPost("{id:guid}/dispatch")]
    [ProducesResponseType(typeof(QueuedPrintJobDto), 200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(404)]
    [ProducesResponseType(500)]
    public async Task<IActionResult> DispatchJobAsync(Guid id)
    {
        try
        {
            string? userId = User.Identity?.Name ?? "anonymous";
            QueuedPrintJobDto result = await printJobManagementService.DispatchJobAsync(id.ToString(), userId, CancellationToken.None);
            return Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning("Cannot dispatch job {Id}: {Message}", id, ex.Message);
            return BadRequest(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error dispatching print job {Id}", id);
            return Problem("An error occurred while dispatching the job", statusCode: 500);
        }
    }

    /// <summary>
    /// Cancel a job, stopping the print if currently printing.
    /// Works for jobs in Queued, Assigned, Starting, Printing, or Paused status.
    /// If the job is currently printing on a printer, this will send a cancel command to stop the print.
    /// </summary>
    /// <param name="id">The unique identifier of the job to cancel.</param>
    [HttpPost("{id:guid}/cancel")]
    [ProducesResponseType(204)]
    [ProducesResponseType(400)]
    [ProducesResponseType(404)]
    [ProducesResponseType(500)]
    public async Task<IActionResult> CancelJobAsync(Guid id)
    {
        try
        {
            string? userId = User.Identity?.Name ?? "anonymous";
            await printJobManagementService.CancelJobAsync(id.ToString(), userId, CancellationToken.None);
            return NoContent();
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning("Cannot cancel job {Id}: {Message}", id, ex.Message);
            return BadRequest(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error cancelling print job {Id}", id);
            return Problem("An error occurred while cancelling the job", statusCode: 500);
        }
    }

    /// <summary>
    /// Abort the current print attempt but keep the job in the queue.
    /// Sends cancel to the printer hardware and returns the job to Queued status.
    /// Only works when the job is actively printing (Printing, Starting, or Paused status).
    /// </summary>
    /// <param name="id">The unique identifier of the job whose current print to abort.</param>
    [HttpPost("{id:guid}/abort-print")]
    [ProducesResponseType(204)]
    [ProducesResponseType(400)]
    [ProducesResponseType(404)]
    [ProducesResponseType(500)]
    public async Task<IActionResult> AbortPrintAsync(Guid id)
    {
        try
        {
            string? userId = User.Identity?.Name ?? "anonymous";
            await printJobManagementService.AbortPrintAsync(id.ToString(), userId, CancellationToken.None);
            return NoContent();
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning("Cannot abort print for job {Id}: {Message}", id, ex.Message);
            return BadRequest(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error aborting print for job {Id}", id);
            return Problem("An error occurred while aborting the print", statusCode: 500);
        }
    }

    /// <summary>
    /// Delete a job from the queue
    /// </summary>
    /// <param name="id">The unique identifier of the job to delete.</param>
    [HttpDelete("{id:guid}")]
    [ProducesResponseType(204)]
    [ProducesResponseType(400)]
    [ProducesResponseType(404)]
    [ProducesResponseType(500)]
    public async Task<IActionResult> DeleteJobAsync(Guid id)
    {
        try
        {
            bool ok = await queueService.RemoveJobAsync(id, CancellationToken.None);
            return !ok ? BadRequest("Cannot delete the job (not found or currently printing)") : NoContent();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error deleting print job {Id}", id);
            return Problem("An error occurred while deleting the job", statusCode: 500);
        }
    }

    /// <summary>
    /// Synchronize orphaned jobs that are stuck in "Printing" status but the printer is now idle.
    /// This can happen if the API was restarted/redeployed while a print was in progress.
    /// Checks the current printer state from the status cache and marks jobs as completed/failed accordingly.
    /// </summary>
    /// <returns>The number of jobs that were synchronized.</returns>
    [HttpPost("sync-orphaned")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(object), 200)]
    [ProducesResponseType(500)]
    public async Task<IActionResult> SyncOrphanedJobsAsync()
    {
        try
        {
            logger.LogInformation("[JobQueueController] Manual sync of orphaned jobs requested");

            // Create a lookup function that gets printer state from cache
            string? LookupPrinterState(Guid printerId)
            {
                PrinterStatusDto? status = printerStatusCache.GetStatus(printerId);
                return status?.State;
            }

            int syncedCount = await printJobCompletionService.SyncOrphanedPrintingJobsAsync(
                LookupPrinterState,
                CancellationToken.None);

            return Ok(new { syncedCount, message = $"Synchronized {syncedCount} orphaned job(s)" });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error synchronizing orphaned jobs");
            return Problem("An error occurred while synchronizing orphaned jobs", statusCode: 500);
        }
    }

    /// <summary>
    /// Find and score candidate printers for a job using multi-factor analysis.
    /// Returns all printers ranked by compatibility, with eliminated printers at the end.
    /// </summary>
    /// <param name="id">The print job ID to find candidates for.</param>
    [HttpGet("{id:guid}/candidates")]
    [ProducesResponseType(typeof(List<DispatchCandidateDto>), 200)]
    [ProducesResponseType(404)]
    [ProducesResponseType(500)]
    public async Task<IActionResult> GetCandidatesAsync(Guid id)
    {
        try
        {
            List<DispatchCandidateDto> candidates = await jobDispatchService.FindCandidatesAsync(id, CancellationToken.None);
            return Ok(candidates);
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning("Cannot find candidates for job {Id}: {Message}", id, ex.Message);
            return NotFound(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error finding candidates for job {Id}", id);
            return Problem("An error occurred while finding candidates", statusCode: 500);
        }
    }

    /// <summary>
    /// Dispatch a job to a specific printer selected from scored candidates.
    /// Assigns the job, records the dispatch score, and triggers print start.
    /// </summary>
    /// <param name="id">The print job ID to dispatch.</param>
    /// <param name="request">The dispatch request containing the target printer ID.</param>
    [HttpPost("{id:guid}/dispatch-to")]
    [ProducesResponseType(typeof(QueuedPrintJobDto), 200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(404)]
    [ProducesResponseType(500)]
    public async Task<IActionResult> DispatchToAsync(Guid id, [FromBody] DispatchJobDto request)
    {
        if (request is null)
        {
            return BadRequest("Request body is required");
        }

        try
        {
            string userId = User.Identity?.Name ?? "anonymous";
            QueuedPrintJobDto result = await jobDispatchService.DispatchJobAsync(id, request.PrinterId, userId, CancellationToken.None);
            return Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning("Cannot dispatch job {Id}: {Message}", id, ex.Message);
            return BadRequest(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error dispatching job {Id} to printer {PrinterId}", id, request.PrinterId);
            return Problem("An error occurred while dispatching the job", statusCode: 500);
        }
    }

    /// <summary>
    /// Batch-dispatch multiple queued jobs to their best-fit printers.
    /// Uses the configured load-balancing strategy (or an override per request).
    /// </summary>
    /// <param name="request">Batch dispatch parameters.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpPost("batch-dispatch")]
    [ProducesResponseType(typeof(BatchDispatchResult), 200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(500)]
    public async Task<IActionResult> BatchDispatchAsync([FromBody] BatchDispatchRequest request, CancellationToken ct)
    {
        if (request is null)
        {
            return BadRequest("Request body is required");
        }

        if (!request.DispatchAll && (request.JobIds is null || request.JobIds.Count == 0))
        {
            return BadRequest("Either set DispatchAll to true or provide at least one job ID.");
        }

        try
        {
            string userId = User.Identity?.Name ?? "anonymous";
            BatchDispatchResult result = await batchDispatchService.BatchDispatchAsync(request, userId, ct);
            return Ok(result);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error during batch dispatch");
            return Problem("An error occurred during batch dispatch", statusCode: 500);
        }
    }
}
