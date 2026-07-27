using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Dtos.PrintQueue;
using Farm.Infrastructure.Services.Interfaces;
using Farm.Infrastructure.Services.Queue;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Farm.Infrastructure.Services;

/// <summary>
/// Service for managing job scheduling with timezone support
/// Phase 4.1: Job Scheduling
/// </summary>
public class JobSchedulingService(
    AppDbContext context,
    ILogger<JobSchedulingService> logger,
    IPrintJobManagementService? printJobManagement = null,
    IQueueResourceAuthorizationService? resourceAuthorization = null)
{
    private readonly AppDbContext _context = context ?? throw new ArgumentNullException(nameof(context));
    private readonly ILogger<JobSchedulingService> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly IPrintJobManagementService? _printJobManagement = printJobManagement;
    private readonly IQueueResourceAuthorizationService? _resourceAuthorization = resourceAuthorization;

    /// <summary>
    /// Schedule a print job for a specific date and time in a given timezone
    /// </summary>
    /// <param name="jobId">The unique identifier of the print job to schedule.</param>
    /// <param name="scheduledStartTime">The desired start time for the job.</param>
    /// <param name="timeZone">The timezone for the scheduled time (default: UTC).</param>
    /// <param name="recurrencePattern">Optional recurrence pattern for repeating jobs.</param>
    /// <param name="recurrenceEndDate">Optional end date for recurring jobs.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    public async Task<ScheduledJobDto> ScheduleJobAsync(
        Guid jobId,
        DateTime scheduledStartTime,
        string timeZone = "UTC",
        string? recurrencePattern = null,
        DateTime? recurrenceEndDate = null,
        CancellationToken cancellationToken = default) =>
        await ScheduleJobAsync(
            jobId,
            scheduledStartTime,
            timeZone,
            recurrencePattern,
            recurrenceEndDate,
            QueueActorIdentity.Scheduler,
            cancellationToken);

    public async Task<ScheduledJobDto> ScheduleJobAsync(
        Guid jobId,
        DateTime scheduledStartTime,
        string timeZone,
        string? recurrencePattern,
        DateTime? recurrenceEndDate,
        string actorSubject,
        CancellationToken cancellationToken = default)
    {
        // Validate job exists
        PrintJob job = await _context.PrintJobs
            .Include(j => j.Schedule)
            .FirstOrDefaultAsync(j => j.Id == jobId, cancellationToken) ?? throw new InvalidOperationException($"Print job '{jobId}' not found");
        await EnsureActorMayScheduleAsync(job, actorSubject, cancellationToken);

        // Validate timezone
        if (!TryGetTimeZoneInfo(timeZone, out TimeZoneInfo? tzInfo))
        {
            throw new ArgumentException($"Invalid timezone: {timeZone}");
        }

        // Convert input time from user timezone to UTC
        DateTime utcTime = ConvertToUtc(scheduledStartTime, tzInfo);

        // If job already has a schedule, update it
        if (job.Schedule != null)
        {
            job.Schedule.ScheduledStartTime = utcTime;
            job.Schedule.TimeZone = timeZone;
            job.Schedule.RecurrencePattern = recurrencePattern;
            job.Schedule.RecurrenceEndDate = recurrenceEndDate;
            job.Schedule.IsActive = true;
            job.Schedule.IsPaused = false;
            job.Schedule.UpdatedAt = DateTime.UtcNow;
            job.Schedule.InitiatingActorSubject = actorSubject;
        }
        else
        {
            // Create new schedule
            var schedule = new JobSchedule
            {
                PrintJobId = jobId,
                ScheduledStartTime = utcTime,
                TimeZone = timeZone,
                RecurrencePattern = recurrencePattern,
                RecurrenceEndDate = recurrenceEndDate,
                IsActive = true,
                IsPaused = false,
                ScheduledAt = DateTime.UtcNow,
                InitiatingActorSubject = actorSubject,
            };
            job.Schedule = schedule;
            _context.JobSchedules.Add(schedule);
        }

        await _context.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("[JobScheduling] Scheduled job '{JobId}' for {ScheduledTime} (timezone: {TimeZone})", jobId, utcTime, timeZone);

        return await GetScheduledJobAsync(jobId, cancellationToken)
            ?? throw new InvalidOperationException("Failed to retrieve scheduled job");
    }

    /// <summary>
    /// Reschedule an existing scheduled job to a different time
    /// </summary>
    /// <param name="jobId">The unique identifier of the job to reschedule.</param>
    /// <param name="newScheduledTime">The new scheduled start time.</param>
    /// <param name="timeZone">The timezone for the new scheduled time (default: UTC).</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    public async Task<ScheduledJobDto> RescheduleJobAsync(
        Guid jobId,
        DateTime newScheduledTime,
        string timeZone = "UTC",
        CancellationToken cancellationToken = default) =>
        await RescheduleJobAsync(
            jobId,
            newScheduledTime,
            timeZone,
            QueueActorIdentity.Scheduler,
            cancellationToken);

    public async Task<ScheduledJobDto> RescheduleJobAsync(
        Guid jobId,
        DateTime newScheduledTime,
        string timeZone,
        string actorSubject,
        CancellationToken cancellationToken = default)
    {
        JobSchedule schedule = await _context.JobSchedules
            .Include(candidate => candidate.PrintJob)
            .FirstOrDefaultAsync(js => js.PrintJobId == jobId, cancellationToken) ?? throw new InvalidOperationException($"Job '{jobId}' is not scheduled");
        await EnsureActorMayScheduleAsync(schedule.PrintJob, actorSubject, cancellationToken);

        // Validate timezone
        if (!TryGetTimeZoneInfo(timeZone, out TimeZoneInfo? tzInfo))
        {
            throw new ArgumentException($"Invalid timezone: {timeZone}");
        }

        // Convert to UTC
        DateTime utcTime = ConvertToUtc(newScheduledTime, tzInfo);

        schedule.ScheduledStartTime = utcTime;
        schedule.TimeZone = timeZone;
        schedule.UpdatedAt = DateTime.UtcNow;
        schedule.InitiatingActorSubject = actorSubject;

        await _context.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("[JobScheduling] Rescheduled job '{JobId}' to {ScheduledTime}", jobId, utcTime);

        return await GetScheduledJobAsync(jobId, cancellationToken)
            ?? throw new InvalidOperationException("Failed to retrieve scheduled job");
    }

    /// <summary>
    /// Cancel scheduling for a job (deactivates but keeps history)
    /// </summary>
    /// <param name="jobId">The unique identifier of the job to cancel scheduling for.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    public Task CancelSchedulingAsync(
        Guid jobId,
        CancellationToken cancellationToken = default) =>
        CancelSchedulingAsync(jobId, QueueActorIdentity.Scheduler, cancellationToken);

    public async Task CancelSchedulingAsync(
        Guid jobId,
        string actorSubject,
        CancellationToken cancellationToken = default)
    {
        JobSchedule schedule = await _context.JobSchedules
            .Include(candidate => candidate.PrintJob)
            .FirstOrDefaultAsync(js => js.PrintJobId == jobId, cancellationToken) ?? throw new InvalidOperationException($"Job '{jobId}' is not scheduled");
        await EnsureActorMayScheduleAsync(schedule.PrintJob, actorSubject, cancellationToken);

        schedule.IsActive = false;
        schedule.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("[JobScheduling] Cancelled scheduling for job '{JobId}'", jobId);
    }

    /// <summary>
    /// Pause a scheduled job (can be resumed later)
    /// </summary>
    /// <param name="jobId">The unique identifier of the job to pause.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    public Task PauseSchedulingAsync(
        Guid jobId,
        CancellationToken cancellationToken = default) =>
        PauseSchedulingAsync(jobId, QueueActorIdentity.Scheduler, cancellationToken);

    public async Task PauseSchedulingAsync(
        Guid jobId,
        string actorSubject,
        CancellationToken cancellationToken = default)
    {
        JobSchedule schedule = await _context.JobSchedules
            .Include(candidate => candidate.PrintJob)
            .FirstOrDefaultAsync(js => js.PrintJobId == jobId, cancellationToken) ?? throw new InvalidOperationException($"Job '{jobId}' is not scheduled");
        await EnsureActorMayScheduleAsync(schedule.PrintJob, actorSubject, cancellationToken);

        schedule.IsPaused = true;
        schedule.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("[JobScheduling] Paused scheduling for job '{JobId}'", jobId);
    }

    /// <summary>
    /// Resume a paused scheduled job
    /// </summary>
    /// <param name="jobId">The unique identifier of the paused job to resume.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    public Task ResumeSchedulingAsync(
        Guid jobId,
        CancellationToken cancellationToken = default) =>
        ResumeSchedulingAsync(jobId, QueueActorIdentity.Scheduler, cancellationToken);

    public async Task ResumeSchedulingAsync(
        Guid jobId,
        string actorSubject,
        CancellationToken cancellationToken = default)
    {
        JobSchedule schedule = await _context.JobSchedules
            .Include(candidate => candidate.PrintJob)
            .FirstOrDefaultAsync(js => js.PrintJobId == jobId, cancellationToken) ?? throw new InvalidOperationException($"Job '{jobId}' is not scheduled");
        await EnsureActorMayScheduleAsync(schedule.PrintJob, actorSubject, cancellationToken);

        schedule.IsPaused = false;
        schedule.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("[JobScheduling] Resumed scheduling for job '{JobId}'", jobId);
    }

    /// <summary>
    /// Get all scheduled jobs (active and not paused)
    /// </summary>
    /// <param name="dateFrom">Optional filter for jobs scheduled on or after this date.</param>
    /// <param name="dateTo">Optional filter for jobs scheduled on or before this date.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    public async Task<IEnumerable<ScheduledJobDto>> GetScheduledJobsAsync(
        DateTime? dateFrom = null,
        DateTime? dateTo = null,
        CancellationToken cancellationToken = default)
    {
        IQueryable<JobSchedule> query = _context.JobSchedules
            .Where(js => js.IsActive && !js.IsPaused)
            .Include(js => js.PrintJob)
            .ThenInclude(j => j.AssignedPrinter)
            .AsQueryable();

        if (dateFrom.HasValue)
        {
            query = query.Where(js => js.ScheduledStartTime >= dateFrom.Value);
        }

        if (dateTo.HasValue)
        {
            query = query.Where(js => js.ScheduledStartTime <= dateTo.Value);
        }

        List<JobSchedule> schedules = await query
            .OrderBy(js => js.ScheduledStartTime)
            .ToListAsync(cancellationToken);

        return schedules.Select(js => new ScheduledJobDto
        {
            JobId = js.PrintJobId,
            PrinterName = js.PrintJob?.AssignedPrinter?.Name ?? "Unassigned",
            JobName = js.PrintJob?.Name ?? "Unknown",
            ScheduledStartTime = js.ScheduledStartTime,
            ScheduledStartTimeInTimeZone = ConvertFromUtc(js.ScheduledStartTime, js.TimeZone),
            TimeZone = js.TimeZone,
            RecurrencePattern = js.RecurrencePattern,
            IsActive = js.IsActive,
            IsPaused = js.IsPaused
        });
    }

    /// <summary>
    /// Get a specific scheduled job
    /// </summary>
    /// <param name="jobId">The unique identifier of the scheduled job.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    public async Task<ScheduledJobDto?> GetScheduledJobAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        JobSchedule? schedule = await _context.JobSchedules
            .Include(js => js.PrintJob)
            .ThenInclude(j => j.AssignedPrinter)
            .FirstOrDefaultAsync(js => js.PrintJobId == jobId, cancellationToken);

        return schedule == null
            ? null
            : new ScheduledJobDto
            {
                JobId = schedule.PrintJobId,
                PrinterName = schedule.PrintJob?.AssignedPrinter?.Name ?? "Unassigned",
                JobName = schedule.PrintJob?.Name ?? "Unknown",
                ScheduledStartTime = schedule.ScheduledStartTime,
                ScheduledStartTimeInTimeZone = ConvertFromUtc(schedule.ScheduledStartTime, schedule.TimeZone),
                TimeZone = schedule.TimeZone,
                RecurrencePattern = schedule.RecurrencePattern,
                IsActive = schedule.IsActive,
                IsPaused = schedule.IsPaused
            };
    }

    /// <summary>
    /// Get execution history for a scheduled job
    /// </summary>
    /// <param name="jobId">The unique identifier of the job to get history for.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    public async Task<IEnumerable<JobExecutionDto>> GetExecutionHistoryAsync(
        Guid jobId,
        CancellationToken cancellationToken = default)
    {
        List<JobExecution> executions = await _context.JobExecutions
            .Where(je => je.JobSchedule.PrintJobId == jobId)
            .OrderByDescending(je => je.ScheduledExecutionTime)
            .ToListAsync(cancellationToken);

        return executions.Select(je => new JobExecutionDto
        {
            Id = je.Id,
            ScheduledExecutionTime = je.ScheduledExecutionTime,
            ActualStartTime = je.ActualStartTime,
            Status = je.Status,
            Message = je.Message,
            DurationSeconds = je.ActualStartTime.HasValue
                ? (int)(DateTime.UtcNow - je.ActualStartTime.Value).TotalSeconds
                : null
        });
    }

    /// <summary>
    /// Trigger scheduled jobs that are due to run
    /// Called by background service periodically
    /// </summary>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    public async Task TriggerScheduledJobsAsync(CancellationToken cancellationToken = default)
    {
        DateTime now = DateTime.UtcNow;

        // Get all active, non-paused schedules that are due
        List<JobSchedule> dueSchedules = await _context.JobSchedules
            .Where(js => js.IsActive && !js.IsPaused && js.ScheduledStartTime <= now)
            .Include(js => js.PrintJob)
            .OrderByDescending(js => js.PrintJob.Priority)
            .ThenBy(js => js.ScheduledStartTime)
            .ThenBy(js => js.PrintJob.QueuePosition)
            .ThenBy(js => js.PrintJob.QueuedAt)
            .ThenBy(js => js.Id)
            .ToListAsync(cancellationToken);

        foreach (JobSchedule? schedule in dueSchedules)
        {
            try
            {
                // Create execution record
                var execution = new JobExecution
                {
                    JobScheduleId = schedule.Id,
                    ScheduledExecutionTime = schedule.ScheduledStartTime,
                    Status = "Running",
                    ActualStartTime = now
                };

                _context.JobExecutions.Add(execution);
                await _context.SaveChangesAsync(cancellationToken);

                // =============================================================
                // The scheduler NEVER sets Status = Printing directly (issue #900,
                // defect 5). Printing is reachable only through the shared dispatch
                // claim + adapter orchestration, which enforces bed-clear
                // acknowledgement, telemetry freshness, filament, capability and
                // compatibility gates. The scheduler simply invokes that path.
                // =============================================================
                await TriggerScheduledDispatchAsync(schedule, execution, cancellationToken);

                _logger.LogInformation(
                    "[JobScheduling] Triggered scheduled job '{JobId}' at {ExecutionTime}", schedule.PrintJobId, now);

                // If no recurrence, mark as inactive
                if (string.IsNullOrEmpty(schedule.RecurrencePattern))
                {
                    schedule.IsActive = false;
                    await _context.SaveChangesAsync(cancellationToken);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError("[JobScheduling] Failed to trigger job '{JobId}': {ExceptionMessage}", schedule.PrintJobId, ex.Message);
            }
        }
    }

    /// <summary>
    /// Routes a due schedule through the shared dispatch orchestration.
    /// The execution record is updated with the typed outcome so an operator can see why a
    /// scheduled start was refused instead of finding a job silently stuck in Printing.
    /// </summary>
    private async Task TriggerScheduledDispatchAsync(
        JobSchedule schedule,
        JobExecution execution,
        CancellationToken cancellationToken)
    {
        if (schedule.PrintJob is null)
        {
            execution.Status = "Failed";
            execution.Message = "Scheduled job no longer exists.";
            await _context.SaveChangesAsync(cancellationToken);
            return;
        }

        if (_printJobManagement is null)
        {
            // No orchestrator wired (unit-test/host-less configuration): refuse to
            // fabricate a Printing state. Leave the job queued for the auto-dispatcher.
            execution.Status = "Skipped";
            execution.Message = "Dispatch orchestration is not available in this host.";
            await _context.SaveChangesAsync(cancellationToken);

            _logger.LogWarning(
                "[JobScheduling] Dispatch orchestration unavailable; job {JobId} left queued for auto-dispatch",
                schedule.PrintJobId);
            return;
        }

        try
        {
            QueuedPrintJobDto result = await _printJobManagement.DispatchJobAsync(
                schedule.PrintJobId.ToString(),
                schedule.InitiatingActorSubject,
                ifMatchJobRowVersion: null,
                cancellationToken);

            DispatchAttemptResultDto? dispatch = result.DispatchResult;
            if (dispatch?.Outcome == DispatchAttemptOutcome.Accepted)
            {
                execution.Status = "Completed";
                execution.Message = "The backend confirmed the scheduled start.";
            }
            else if (dispatch?.Outcome == DispatchAttemptOutcome.Unknown)
            {
                execution.Status = "Unknown";
                execution.Message =
                    dispatch.ErrorDetail ?? "The backend start requires reconciliation.";
            }
            else
            {
                execution.Status = "Failed";
                execution.Message =
                    dispatch?.ErrorDetail ?? "The scheduled start was not accepted.";
            }
        }
        catch (Exception ex)
        {
            execution.Status = "Failed";
            execution.Message = ex.Message[..Math.Min(ex.Message.Length, 500)];

            _logger.LogWarning(
                "[JobScheduling] Scheduled dispatch refused for job {JobId}: {Reason}",
                schedule.PrintJobId, ex.Message);
        }

        await _context.SaveChangesAsync(cancellationToken);
    }

    private async Task EnsureActorMayScheduleAsync(
        PrintJob job,
        string actorSubject,
        CancellationToken ct)
    {
        if (_resourceAuthorization is null)
        {
            return;
        }

        bool canAccessJob = await _resourceAuthorization.CanActorAccessJobAsync(
            actorSubject,
            job.Id,
            PrinterGroupAccessLevel.Submit,
            ct);
        bool canAccessPrinter = job.AssignedPrinterId.HasValue &&
            await _resourceAuthorization.CanActorAccessPrinterAsync(
                actorSubject,
                job.AssignedPrinterId.Value,
                PrinterGroupAccessLevel.Submit,
                ct);
        bool canAccessProject = !job.CalibrationProjectId.HasValue ||
            await _resourceAuthorization.CanActorAccessProjectAsync(
                actorSubject,
                job.CalibrationProjectId.Value,
                ct);
        if (!canAccessJob || !canAccessPrinter || !canAccessProject)
        {
            throw new UnauthorizedAccessException("The scheduled queue resource was not found.");
        }
    }

    /// <summary>
    /// Get all available timezones
    /// </summary>
    public IEnumerable<TimeZoneDto> GetAvailableTimeZones()
    {
        return TimeZoneInfo.GetSystemTimeZones()
            .Select(tz => new TimeZoneDto
            {
                Id = tz.Id,
                DisplayName = tz.DisplayName,
                Offset = tz.BaseUtcOffset.ToString(@"hh\:mm")
            })
            .OrderBy(tz => tz.Offset)
            .ThenBy(tz => tz.DisplayName);
    }

    /// <summary>
    /// Convert time from user timezone to UTC
    /// </summary>
    /// <param name="userTime">The time in the user's timezone.</param>
    /// <param name="timeZone">The timezone information for the user's time.</param>
    public DateTime ConvertToUtc(DateTime userTime, TimeZoneInfo timeZone)
    {
        return TimeZoneInfo.ConvertTimeToUtc(userTime, timeZone);
    }

    /// <summary>
    /// Convert time from UTC to user timezone
    /// </summary>
    /// <param name="utcTime">The UTC time to convert.</param>
    /// <param name="timeZoneId">The target timezone identifier.</param>
    public DateTime ConvertFromUtc(DateTime utcTime, string timeZoneId)
    {
        if (!TryGetTimeZoneInfo(timeZoneId, out TimeZoneInfo? tzInfo))
        {
            return utcTime; // Fall back to UTC
        }

        return TimeZoneInfo.ConvertTimeFromUtc(utcTime, tzInfo);
    }

    /// <summary>
    /// Try to get timezone info from ID, handles both Windows and IANA identifiers
    /// </summary>
    /// <param name="timeZoneId">The timezone identifier (Windows or IANA format).</param>
    /// <param name="timeZoneInfo">The resulting timezone info if found.</param>
    private bool TryGetTimeZoneInfo(string timeZoneId, out TimeZoneInfo timeZoneInfo)
    {
        try
        {
            // Try direct lookup first (Windows and IANA IDs on modern .NET)
            timeZoneInfo = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
            return true;
        }
        catch
        {
            // Fall back to UTC if not found
            timeZoneInfo = TimeZoneInfo.Utc;
            return false;
        }
    }
}

/// <summary>
/// DTO for scheduled job information
/// </summary>
public class ScheduledJobDto
{
    public Guid JobId { get; set; }

    public string JobName { get; set; } = string.Empty;

    public string PrinterName { get; set; } = string.Empty;

    public DateTime ScheduledStartTime { get; set; } // UTC

    public DateTime ScheduledStartTimeInTimeZone { get; set; } // In user's timezone

    public string TimeZone { get; set; } = "UTC";

    public string? RecurrencePattern { get; set; }

    public bool IsActive { get; set; }

    public bool IsPaused { get; set; }
}

/// <summary>
/// DTO for job execution record
/// </summary>
public class JobExecutionDto
{
    public Guid Id { get; set; }

    public DateTime ScheduledExecutionTime { get; set; }

    public DateTime? ActualStartTime { get; set; }

    public string Status { get; set; } = string.Empty;

    public string? Message { get; set; }

    public int? DurationSeconds { get; set; }
}

/// <summary>
/// DTO for timezone information
/// </summary>
public class TimeZoneDto
{
    public string Id { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string Offset { get; set; } = string.Empty;
}
