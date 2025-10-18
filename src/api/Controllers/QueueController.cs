using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Telemetry;
using Farm.Web.Shared;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Farm.Web.Api.Controllers;

/// <summary>
/// Controller for managing print job queues and job assignment
/// </summary>
[ApiController]
[Route("api/queue")]
[Tags("Job Queue Management")]

public class QueueController(IUnifiedLoggingService logger, Farm.Web.Api.Services.Queue.IQueueService queueService) : ControllerBase
{
    private readonly IUnifiedLoggingService _logger = logger;
    private readonly Farm.Web.Api.Services.Queue.IQueueService _queueService = queueService;

    /// <summary>
    /// Get all printer queues with current jobs
    /// </summary>
    /// <returns>List of printer queues with job counts and status</returns>
    [HttpGet("overview")]
    [ProducesResponseType(typeof(IEnumerable<QueueOverviewDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetQueueOverviewAsync()
    {
        try
        {
            var overview = await _queueService.GetQueueOverviewAsync(CancellationToken.None);
            return Ok(overview);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get queue overview");
            return StatusCode(StatusCodes.Status500InternalServerError, "Failed to get queue overview");
        }
    }

    /// <summary>
    /// Get jobs in a specific printer's queue
    /// </summary>
    /// <param name="printerId">Printer ID</param>
    /// <returns>List of jobs in the queue</returns>
    [HttpGet("printer/{printerId:guid}")]
    [ProducesResponseType(typeof(IEnumerable<JobQueuePrintJobDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetPrinterQueueAsync(Guid printerId)
    {
        try
        {
            var jobs = await _queueService.GetPrinterQueueAsync(printerId, CancellationToken.None);
            return Ok(jobs);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Failed to get printer queue for printer {printerId}");
            return StatusCode(StatusCodes.Status500InternalServerError, "Failed to get printer queue");
        }
    }

    /// <summary>
    /// Add a job to a printer queue or auto-assign to best available printer
    /// </summary>
    /// <param name="request">Job queue request</param>
    /// <returns>Created job information</returns>
    [HttpPost("jobs")]
    [ProducesResponseType(typeof(JobQueuePrintJobDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> AddJobToQueueAsync([FromBody] QueuePrintJobDto request)
    {
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            // Verify G-code file exists
            var dto = await _queueService.AddJobToQueueAsync(request, CancellationToken.None);
            if (dto == null)
            {
                return BadRequest("Failed to add job to queue");
            }
            _logger.LogInformation($"Job added to queue: {dto.Id} for printer {dto.AssignedPrinterId}");
            return Created($"/api/queue/jobs/{dto.Id}", dto);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to add job to queue");
            return StatusCode(StatusCodes.Status500InternalServerError, "Failed to add job to queue");
        }
    }

    /// <summary>
    /// Get a specific job
    /// </summary>
    /// <param name="id">Job ID</param>
    /// <returns>Job information</returns>
    [HttpGet("jobs/{id:guid}")]
    [ProducesResponseType(typeof(JobQueuePrintJobDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetJobAsync(Guid id)
    {
        var dto = await _queueService.GetJobAsync(id, CancellationToken.None);
        if (dto == null)
        {
            return NotFound();
        }

        return Ok(dto);
    }

    /// <summary>
    /// Remove a job from the queue
    /// </summary>
    /// <param name="id">Job ID</param>
    /// <returns>No content if successful</returns>
    [HttpDelete("jobs/{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> RemoveJobFromQueueAsync(Guid id)
    {
        var ok = await _queueService.RemoveJobAsync(id, CancellationToken.None);
        if (!ok)
        {
            return NotFound();
        }

        _logger.LogInformation($"Job removed from queue: {id}");
        return NoContent();
    }

    /// <summary>
    /// Update job priority
    /// </summary>
    /// <param name="id">Job ID</param>
    /// <param name="request">Priority update request</param>
    /// <returns>Updated job information</returns>
    [HttpPatch("jobs/{id:guid}/priority")]
    [ProducesResponseType(typeof(JobQueuePrintJobDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateJobPriorityAsync(Guid id, [FromBody] UpdateJobPriorityDto request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var dto = await _queueService.UpdateJobPriorityAsync(id, request, CancellationToken.None);
        if (dto == null)
        {
            return NotFound();
        }

        _logger.LogInformation($"Job priority updated: {id} to {request.Priority}");
        return Ok(dto);
    }
}
