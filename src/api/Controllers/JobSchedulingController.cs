using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Farm.Web.Api.Controllers;

/// <summary>
/// API controller for managing job scheduling
/// Phase 4.1: Job Scheduling
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize]
public class JobSchedulingController(JobSchedulingService schedulingService, ILogger<JobSchedulingController> logger) : ControllerBase
{
    private readonly JobSchedulingService _schedulingService = schedulingService ?? throw new ArgumentNullException(nameof(schedulingService));
    private readonly ILogger<JobSchedulingController> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <summary>
    /// Schedule a print job for a specific date and time
    /// </summary>
    [HttpPost("{jobId:guid}/schedule")]
    [ProducesResponseType(typeof(ScheduledJobDto), 200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(404)]
    public async Task<ActionResult<ScheduledJobDto>> ScheduleJobAsync(
        Guid jobId,
        [FromBody] ScheduleJobRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await _schedulingService.ScheduleJobAsync(
                jobId,
                request.ScheduledStartTime,
                request.TimeZone ?? "UTC",
                request.RecurrencePattern,
                request.RecurrenceEndDate,
                cancellationToken);

            return Ok(result);
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning("Invalid scheduling request: {ExceptionMessage}", ex.Message);
            return BadRequest(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning("Job not found: {ExceptionMessage}", ex.Message);
            return NotFound(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Reschedule an existing scheduled job
    /// </summary>
    [HttpPut("{jobId:guid}/reschedule")]
    [ProducesResponseType(typeof(ScheduledJobDto), 200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(404)]
    public async Task<ActionResult<ScheduledJobDto>> RescheduleJobAsync(
        Guid jobId,
        [FromBody] RescheduleJobRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await _schedulingService.RescheduleJobAsync(
                jobId,
                request.NewScheduledTime,
                request.TimeZone ?? "UTC",
                cancellationToken);

            return Ok(result);
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning("Invalid rescheduling request: {ExceptionMessage}", ex.Message);
            return BadRequest(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning("Job scheduling not found: {ExceptionMessage}", ex.Message);
            return NotFound(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Cancel scheduling for a job
    /// </summary>
    [HttpDelete("{jobId:guid}/schedule")]
    [ProducesResponseType(204)]
    [ProducesResponseType(404)]
    public async Task<IActionResult> CancelSchedulingAsync(Guid jobId, CancellationToken cancellationToken)
    {
        try
        {
            await _schedulingService.CancelSchedulingAsync(jobId, cancellationToken);
            return NoContent();
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning("Job scheduling not found: {ExceptionMessage}", ex.Message);
            return NotFound(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Get all scheduled jobs
    /// </summary>
    [HttpGet("scheduled")]
    [ProducesResponseType(typeof(IEnumerable<ScheduledJobDto>), 200)]
    public async Task<ActionResult<IEnumerable<ScheduledJobDto>>> GetScheduledJobsAsync(
        [FromQuery] DateTime? dateFrom,
        [FromQuery] DateTime? dateTo,
        CancellationToken cancellationToken)
    {
        var result = await _schedulingService.GetScheduledJobsAsync(dateFrom, dateTo, cancellationToken);
        return Ok(result);
    }

    /// <summary>
    /// Get scheduling information for a specific job
    /// </summary>
    [HttpGet("{jobId:guid}")]
    [ProducesResponseType(typeof(ScheduledJobDto), 200)]
    [ProducesResponseType(404)]
    public async Task<ActionResult<ScheduledJobDto>> GetScheduledJobAsync(Guid jobId, CancellationToken cancellationToken)
    {
        var result = await _schedulingService.GetScheduledJobAsync(jobId, cancellationToken);
        if (result == null)
        {
            return NotFound(new { error = $"No scheduling found for job '{jobId}'" });
        }

        return Ok(result);
    }

    /// <summary>
    /// Get execution history for a scheduled job
    /// </summary>
    [HttpGet("{jobId:guid}/executions")]
    [ProducesResponseType(typeof(IEnumerable<JobExecutionDto>), 200)]
    public async Task<ActionResult<IEnumerable<JobExecutionDto>>> GetExecutionHistoryAsync(
        Guid jobId,
        CancellationToken cancellationToken)
    {
        var result = await _schedulingService.GetExecutionHistoryAsync(jobId, cancellationToken);
        return Ok(result);
    }

    /// <summary>
    /// Get all available timezones
    /// </summary>
    [HttpGet("timezones")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(IEnumerable<TimeZoneDto>), 200)]
    public ActionResult<IEnumerable<TimeZoneDto>> GetAvailableTimeZones()
    {
        var result = _schedulingService.GetAvailableTimeZones();
        return Ok(result);
    }

    /// <summary>
    /// Pause a scheduled job
    /// </summary>
    [HttpPost("{jobId:guid}/pause")]
    [ProducesResponseType(204)]
    [ProducesResponseType(404)]
    public async Task<IActionResult> PauseSchedulingAsync(Guid jobId, CancellationToken cancellationToken)
    {
        try
        {
            await _schedulingService.PauseSchedulingAsync(jobId, cancellationToken);
            return NoContent();
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning("Job scheduling not found: {ExceptionMessage}", ex.Message);
            return NotFound(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Resume a paused scheduled job
    /// </summary>
    [HttpPost("{jobId:guid}/resume")]
    [ProducesResponseType(204)]
    [ProducesResponseType(404)]
    public async Task<IActionResult> ResumeSchedulingAsync(Guid jobId, CancellationToken cancellationToken)
    {
        try
        {
            await _schedulingService.ResumeSchedulingAsync(jobId, cancellationToken);
            return NoContent();
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning("Job scheduling not found: {ExceptionMessage}", ex.Message);
            return NotFound(new { error = ex.Message });
        }
    }
}

/// <summary>
/// Request to schedule a job
/// </summary>
public class ScheduleJobRequest
{
    public DateTime ScheduledStartTime { get; set; }
    public string? TimeZone { get; set; }
    public string? RecurrencePattern { get; set; }
    public DateTime? RecurrenceEndDate { get; set; }
}

/// <summary>
/// Request to reschedule a job
/// </summary>
public class RescheduleJobRequest
{
    public DateTime NewScheduledTime { get; set; }
    public string? TimeZone { get; set; }
}
