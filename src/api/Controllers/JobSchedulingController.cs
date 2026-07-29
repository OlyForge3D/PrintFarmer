using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Security;
using Farm.Infrastructure.Services;
using Farm.Infrastructure.Services.Queue;
using Farm.Web.Api.Infrastructure.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Farm.Web.Api.Controllers;

/// <summary>
/// API controller for managing job scheduling
/// Phase 4.1: Job Scheduling
/// </summary>
[ApiController]
[Route("api/job-scheduling")]
[Authorize]
public class JobSchedulingController(JobSchedulingService schedulingService, ILogger<JobSchedulingController> logger) : ControllerBase
{
    private readonly JobSchedulingService _schedulingService = schedulingService ?? throw new ArgumentNullException(nameof(schedulingService));
    private readonly ILogger<JobSchedulingController> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <summary>
    /// Schedule a print job for a specific date and time
    /// </summary>
    /// <param name="jobId">The unique identifier of the job to schedule.</param>
    /// <param name="request">The scheduling request containing start time and recurrence options.</param>
    /// <param name="cancellationToken">Cancellation token for the async operation.</param>
    [HttpPost("{jobId:guid}/schedule")]
    [RequirePermission(PrintFarmerPermissions.Queue.Write)]
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
            ScheduledJobDto result = await _schedulingService.ScheduleJobAsync(
                jobId,
                request.ScheduledLocalTime,
                request.TimeZone,
                request.RecurrencePattern,
                request.RecurrenceInterval,
                request.RecurrenceEndLocalTime,
                QueueActorIdentity.Resolve(User),
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
        catch (UnauthorizedAccessException)
        {
            return NotFound();
        }
    }

    /// <summary>
    /// Reschedule an existing scheduled job
    /// </summary>
    /// <param name="jobId">The unique identifier of the job to reschedule.</param>
    /// <param name="request">The reschedule request containing the new scheduled time.</param>
    /// <param name="cancellationToken">Cancellation token for the async operation.</param>
    [HttpPut("{jobId:guid}/reschedule")]
    [RequirePermission(PrintFarmerPermissions.Queue.Write)]
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
            ScheduledJobDto result = await _schedulingService.RescheduleJobAsync(
                jobId,
                request.ScheduledLocalTime,
                request.TimeZone,
                request.RecurrencePattern,
                request.RecurrenceInterval,
                request.RecurrenceEndLocalTime,
                QueueActorIdentity.Resolve(User),
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
        catch (UnauthorizedAccessException)
        {
            return NotFound();
        }
    }

    /// <summary>
    /// Cancel scheduling for a job
    /// </summary>
    /// <param name="jobId">The unique identifier of the job to cancel scheduling for.</param>
    /// <param name="cancellationToken">Cancellation token for the async operation.</param>
    [HttpDelete("{jobId:guid}/schedule")]
    [RequirePermission(PrintFarmerPermissions.Queue.Cancel)]
    [ProducesResponseType(204)]
    [ProducesResponseType(404)]
    public async Task<IActionResult> CancelSchedulingAsync(Guid jobId, CancellationToken cancellationToken)
    {
        try
        {
            await _schedulingService.CancelSchedulingAsync(
                jobId,
                QueueActorIdentity.Resolve(User),
                cancellationToken);
            return NoContent();
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning("Job scheduling not found: {ExceptionMessage}", ex.Message);
            return NotFound(new { error = ex.Message });
        }
        catch (UnauthorizedAccessException)
        {
            return NotFound();
        }
    }

    /// <summary>
    /// Get all scheduled jobs
    /// </summary>
    /// <param name="dateFrom">Optional start date to filter scheduled jobs.</param>
    /// <param name="dateTo">Optional end date to filter scheduled jobs.</param>
    /// <param name="cancellationToken">Cancellation token for the async operation.</param>
    [HttpGet("scheduled")]
    [RequirePermission(PrintFarmerPermissions.Queue.Read)]
    [ProducesResponseType(typeof(IEnumerable<ScheduledJobDto>), 200)]
    public async Task<ActionResult<IEnumerable<ScheduledJobDto>>> GetScheduledJobsAsync(
        [FromQuery] DateTime? dateFrom,
        [FromQuery] DateTime? dateTo,
        CancellationToken cancellationToken)
    {
        IEnumerable<ScheduledJobDto> result = await _schedulingService.GetScheduledJobsAsync(
            QueueActorIdentity.Resolve(User),
            dateFrom,
            dateTo,
            cancellationToken);
        return Ok(result);
    }

    /// <summary>
    /// Get scheduling information for a specific job
    /// </summary>
    /// <param name="jobId">The unique identifier of the job.</param>
    /// <param name="cancellationToken">Cancellation token for the async operation.</param>
    [HttpGet("{jobId:guid}")]
    [RequirePermission(PrintFarmerPermissions.Queue.Read)]
    [ProducesResponseType(typeof(ScheduledJobDto), 200)]
    [ProducesResponseType(404)]
    public async Task<ActionResult<ScheduledJobDto>> GetScheduledJobAsync(Guid jobId, CancellationToken cancellationToken)
    {
        ScheduledJobDto? result = await _schedulingService.GetScheduledJobAsync(
            jobId,
            QueueActorIdentity.Resolve(User),
            cancellationToken);
        return result == null ? NotFound(new { error = $"No scheduling found for job '{jobId}'" }) : Ok(result);
    }

    /// <summary>
    /// Get execution history for a scheduled job
    /// </summary>
    /// <param name="jobId">The unique identifier of the job.</param>
    /// <param name="cancellationToken">Cancellation token for the async operation.</param>
    [HttpGet("{jobId:guid}/executions")]
    [RequirePermission(PrintFarmerPermissions.Queue.Read)]
    [ProducesResponseType(typeof(IEnumerable<JobExecutionDto>), 200)]
    [ProducesResponseType(404)]
    public async Task<ActionResult<IEnumerable<JobExecutionDto>>> GetExecutionHistoryAsync(
        Guid jobId,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<JobExecutionDto>? result =
            await _schedulingService.GetExecutionHistoryAsync(
                jobId,
                QueueActorIdentity.Resolve(User),
                cancellationToken);
        return result is null ? NotFound() : Ok(result);
    }

    /// <summary>
    /// Get all available timezones
    /// </summary>
    [HttpGet("timezones")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(IEnumerable<TimeZoneDto>), 200)]
    public ActionResult<IEnumerable<TimeZoneDto>> GetAvailableTimeZones()
    {
        IEnumerable<TimeZoneDto> result = _schedulingService.GetAvailableTimeZones();
        return Ok(result);
    }

    /// <summary>
    /// Pause a scheduled job
    /// </summary>
    /// <param name="jobId">The unique identifier of the job to pause.</param>
    /// <param name="cancellationToken">Cancellation token for the async operation.</param>
    [HttpPost("{jobId:guid}/pause")]
    [RequirePermission(PrintFarmerPermissions.Queue.Write)]
    [ProducesResponseType(204)]
    [ProducesResponseType(404)]
    public async Task<IActionResult> PauseSchedulingAsync(Guid jobId, CancellationToken cancellationToken)
    {
        try
        {
            await _schedulingService.PauseSchedulingAsync(
                jobId,
                QueueActorIdentity.Resolve(User),
                cancellationToken);
            return NoContent();
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning("Job scheduling not found: {ExceptionMessage}", ex.Message);
            return NotFound(new { error = ex.Message });
        }
        catch (UnauthorizedAccessException)
        {
            return NotFound();
        }
    }

    /// <summary>
    /// Resume a paused scheduled job
    /// </summary>
    /// <param name="jobId">The unique identifier of the job to resume.</param>
    /// <param name="cancellationToken">Cancellation token for the async operation.</param>
    [HttpPost("{jobId:guid}/resume")]
    [RequirePermission(PrintFarmerPermissions.Queue.Start)]
    [ProducesResponseType(204)]
    [ProducesResponseType(404)]
    public async Task<IActionResult> ResumeSchedulingAsync(Guid jobId, CancellationToken cancellationToken)
    {
        try
        {
            await _schedulingService.ResumeSchedulingAsync(
                jobId,
                QueueActorIdentity.Resolve(User),
                cancellationToken);
            return NoContent();
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning("Job scheduling not found: {ExceptionMessage}", ex.Message);
            return NotFound(new { error = ex.Message });
        }
        catch (UnauthorizedAccessException)
        {
            return NotFound();
        }
    }
}

/// <summary>
/// Request to schedule a job
/// </summary>
public class ScheduleJobRequest
{
    public required DateTime ScheduledLocalTime { get; set; }

    public required string TimeZone { get; set; }

    public string? RecurrencePattern { get; set; }

    public int RecurrenceInterval { get; set; } = 1;

    public DateTime? RecurrenceEndLocalTime { get; set; }
}

/// <summary>
/// Request to reschedule a job
/// </summary>
public class RescheduleJobRequest
{
    public required DateTime ScheduledLocalTime { get; set; }

    public required string TimeZone { get; set; }

    public string? RecurrencePattern { get; set; }

    public int RecurrenceInterval { get; set; } = 1;

    public DateTime? RecurrenceEndLocalTime { get; set; }
}
