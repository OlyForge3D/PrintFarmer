using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Farm.Infrastructure.Services;

/// <summary>
/// Service for managing job scheduling with timezone support
/// Phase 4.1: Job Scheduling
/// </summary>
public class JobSchedulingService(AppDbContext context, ILogger<JobSchedulingService> logger)
{
    private readonly AppDbContext _context = context ?? throw new ArgumentNullException(nameof(context));
    private readonly ILogger<JobSchedulingService> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

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
        CancellationToken cancellationToken = default)
    {
        // Validate job exists
        PrintJob job = await _context.PrintJobs
            .Include(j => j.Schedule)
            .FirstOrDefaultAsync(j => j.Id == jobId, cancellationToken) ?? throw new InvalidOperationException($"Print job '{jobId}' not found");

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
                ScheduledAt = DateTime.UtcNow
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
        CancellationToken cancellationToken = default)
    {
        JobSchedule schedule = await _context.JobSchedules
            .FirstOrDefaultAsync(js => js.PrintJobId == jobId, cancellationToken) ?? throw new InvalidOperationException($"Job '{jobId}' is not scheduled");

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
    public async Task CancelSchedulingAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        JobSchedule schedule = await _context.JobSchedules
            .FirstOrDefaultAsync(js => js.PrintJobId == jobId, cancellationToken) ?? throw new InvalidOperationException($"Job '{jobId}' is not scheduled");

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
    public async Task PauseSchedulingAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        JobSchedule schedule = await _context.JobSchedules
            .FirstOrDefaultAsync(js => js.PrintJobId == jobId, cancellationToken) ?? throw new InvalidOperationException($"Job '{jobId}' is not scheduled");

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
    public async Task ResumeSchedulingAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        JobSchedule schedule = await _context.JobSchedules
            .FirstOrDefaultAsync(js => js.PrintJobId == jobId, cancellationToken) ?? throw new InvalidOperationException($"Job '{jobId}' is not scheduled");

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

                // Update job to trigger printing
                if (schedule.PrintJob != null)
                {
                    schedule.PrintJob.Status = PrintJobStatus.Printing;
                    schedule.PrintJob.ActualStartTime = now;
                }

                _context.JobExecutions.Add(execution);
                await _context.SaveChangesAsync(cancellationToken);

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
