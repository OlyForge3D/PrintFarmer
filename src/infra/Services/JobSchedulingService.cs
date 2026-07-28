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
    IQueueResourceAuthorizationService? resourceAuthorization = null,
    IQueuePositionAllocator? positionAllocator = null)
{
    private readonly AppDbContext _context = context ?? throw new ArgumentNullException(nameof(context));
    private readonly ILogger<JobSchedulingService> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly IPrintJobManagementService? _printJobManagement = printJobManagement;
    private readonly IQueueResourceAuthorizationService? _resourceAuthorization = resourceAuthorization;
    private readonly IQueuePositionAllocator _positionAllocator =
        positionAllocator ?? new QueuePositionAllocator(context);

    /// <summary>
    /// Schedule a print job for a specific date and time in a given timezone
    /// </summary>
    /// <param name="jobId">The unique identifier of the print job to schedule.</param>
    /// <param name="scheduledLocalTime">Offset-free wall time in the selected timezone.</param>
    /// <param name="timeZone">The timezone for the scheduled time (default: UTC).</param>
    /// <param name="recurrencePattern">Optional recurrence pattern for repeating jobs.</param>
    /// <param name="recurrenceInterval">Number of recurrence units between executions.</param>
    /// <param name="recurrenceEndLocalTime">Optional wall-time end for recurring jobs.</param>
    /// <param name="actorSubject">Authenticated initiating user subject.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    public async Task<ScheduledJobDto> ScheduleJobAsync(
        Guid jobId,
        DateTime scheduledLocalTime,
        string timeZone,
        string? recurrencePattern,
        int recurrenceInterval,
        DateTime? recurrenceEndLocalTime,
        string actorSubject,
        CancellationToken cancellationToken = default)
    {
        // Validate job exists
        PrintJob job = await _context.PrintJobs
            .Include(j => j.Schedule)
            .FirstOrDefaultAsync(j => j.Id == jobId, cancellationToken) ?? throw new InvalidOperationException($"Print job '{jobId}' not found");
        await EnsureActorMayScheduleAsync(job, actorSubject, cancellationToken);

        TimeZoneInfo timeZoneInfo = GetTimeZoneInfo(timeZone);
        DateTime utcTime = ConvertReviewedWallTimeToUtc(
            scheduledLocalTime,
            timeZoneInfo,
            nameof(scheduledLocalTime));
        string? normalizedRecurrence = NormalizeRecurrence(recurrencePattern);
        EnsureRecurrenceSupported(job, normalizedRecurrence);
        int normalizedInterval = ValidateRecurrenceInterval(
            normalizedRecurrence,
            recurrenceInterval);
        DateTime? recurrenceEndUtc = recurrenceEndLocalTime.HasValue
            ? ConvertReviewedWallTimeToUtc(
                recurrenceEndLocalTime.Value,
                timeZoneInfo,
                nameof(recurrenceEndLocalTime))
            : null;
        if (recurrenceEndUtc.HasValue && recurrenceEndUtc.Value < utcTime)
        {
            throw new ArgumentException(
                "Recurrence end must not be earlier than the first scheduled execution.",
                nameof(recurrenceEndLocalTime));
        }

        JobSchedule? existingSchedule = job.Schedule ??
            await _context.JobSchedules
                .Include(schedule => schedule.PrintJob)
                .FirstOrDefaultAsync(
                    schedule => schedule.RootPrintJobId == jobId,
                    cancellationToken);

        // If job already has a schedule, update it
        if (existingSchedule is not null)
        {
            existingSchedule.RootPrintJobId = StableJobId(existingSchedule);
            existingSchedule.ScheduledStartTime = utcTime;
            existingSchedule.TimeZone = timeZone;
            existingSchedule.RecurrencePattern = normalizedRecurrence;
            existingSchedule.RecurrenceInterval = normalizedInterval;
            existingSchedule.RecurrenceEndDate = recurrenceEndUtc;
            existingSchedule.IsActive = true;
            existingSchedule.IsPaused = false;
            existingSchedule.UpdatedAt = DateTime.UtcNow;
            existingSchedule.InitiatingActorSubject = actorSubject;
            existingSchedule.RequiresOperatorReauthorization = false;
        }
        else
        {
            // Create new schedule
            var schedule = new JobSchedule
            {
                PrintJobId = jobId,
                RootPrintJobId = jobId,
                ScheduledStartTime = utcTime,
                TimeZone = timeZone,
                RecurrencePattern = normalizedRecurrence,
                RecurrenceInterval = normalizedInterval,
                RecurrenceEndDate = recurrenceEndUtc,
                IsActive = true,
                IsPaused = false,
                ScheduledAt = DateTime.UtcNow,
                InitiatingActorSubject = actorSubject,
                RequiresOperatorReauthorization = false,
            };
            job.Schedule = schedule;
            _context.JobSchedules.Add(schedule);
        }

        await _context.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("[JobScheduling] Scheduled job '{JobId}' for {ScheduledTime} (timezone: {TimeZone})", jobId, utcTime, timeZone);

        return await GetScheduledJobAsync(jobId, actorSubject, cancellationToken)
            ?? throw new InvalidOperationException("Failed to retrieve scheduled job");
    }

    /// <summary>
    /// Reschedule an existing scheduled job to a different time
    /// </summary>
    /// <param name="jobId">The unique identifier of the job to reschedule.</param>
    /// <param name="scheduledLocalTime">New offset-free wall time.</param>
    /// <param name="timeZone">The timezone for the new scheduled time (default: UTC).</param>
    /// <param name="recurrencePattern">Optional recurrence pattern.</param>
    /// <param name="recurrenceInterval">Number of recurrence units between executions.</param>
    /// <param name="recurrenceEndLocalTime">Optional wall-time recurrence end.</param>
    /// <param name="actorSubject">Authenticated initiating user subject.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    public async Task<ScheduledJobDto> RescheduleJobAsync(
        Guid jobId,
        DateTime scheduledLocalTime,
        string timeZone,
        string? recurrencePattern,
        int recurrenceInterval,
        DateTime? recurrenceEndLocalTime,
        string actorSubject,
        CancellationToken cancellationToken = default)
    {
        JobSchedule schedule = await _context.JobSchedules
            .Include(candidate => candidate.PrintJob)
            .FirstOrDefaultAsync(
                candidate =>
                    candidate.PrintJobId == jobId ||
                    candidate.RootPrintJobId == jobId,
                cancellationToken) ?? throw new InvalidOperationException($"Job '{jobId}' is not scheduled");
        await EnsureActorMayScheduleAsync(schedule.PrintJob, actorSubject, cancellationToken);

        TimeZoneInfo timeZoneInfo = GetTimeZoneInfo(timeZone);
        DateTime utcTime = ConvertReviewedWallTimeToUtc(
            scheduledLocalTime,
            timeZoneInfo,
            nameof(scheduledLocalTime));
        string? normalizedRecurrence = NormalizeRecurrence(recurrencePattern);
        EnsureRecurrenceSupported(schedule.PrintJob, normalizedRecurrence);
        int normalizedInterval = ValidateRecurrenceInterval(
            normalizedRecurrence,
            recurrenceInterval);
        DateTime? recurrenceEndUtc = recurrenceEndLocalTime.HasValue
            ? ConvertReviewedWallTimeToUtc(
                recurrenceEndLocalTime.Value,
                timeZoneInfo,
                nameof(recurrenceEndLocalTime))
            : null;
        if (recurrenceEndUtc.HasValue && recurrenceEndUtc.Value < utcTime)
        {
            throw new ArgumentException(
                "Recurrence end must not be earlier than the scheduled execution.",
                nameof(recurrenceEndLocalTime));
        }

        schedule.ScheduledStartTime = utcTime;
        schedule.TimeZone = timeZone;
        schedule.RecurrencePattern = normalizedRecurrence;
        schedule.RecurrenceInterval = normalizedInterval;
        schedule.RecurrenceEndDate = recurrenceEndUtc;
        schedule.IsActive = true;
        schedule.IsPaused = false;
        schedule.UpdatedAt = DateTime.UtcNow;
        schedule.InitiatingActorSubject = actorSubject;
        schedule.RequiresOperatorReauthorization = false;

        await _context.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("[JobScheduling] Rescheduled job '{JobId}' to {ScheduledTime}", jobId, utcTime);

        return await GetScheduledJobAsync(jobId, actorSubject, cancellationToken)
            ?? throw new InvalidOperationException("Failed to retrieve scheduled job");
    }

    /// <summary>
    /// Cancel scheduling for a job (deactivates but keeps history)
    /// </summary>
    /// <param name="jobId">The unique identifier of the job to cancel scheduling for.</param>
    /// <param name="actorSubject">Authenticated actor requesting cancellation.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    public async Task CancelSchedulingAsync(
        Guid jobId,
        string actorSubject,
        CancellationToken cancellationToken = default)
    {
        JobSchedule schedule = await _context.JobSchedules
            .Include(candidate => candidate.PrintJob)
            .FirstOrDefaultAsync(
                candidate =>
                    candidate.PrintJobId == jobId ||
                    candidate.RootPrintJobId == jobId,
                cancellationToken) ?? throw new InvalidOperationException($"Job '{jobId}' is not scheduled");
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
    /// <param name="actorSubject">Authenticated actor requesting the pause.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    public async Task PauseSchedulingAsync(
        Guid jobId,
        string actorSubject,
        CancellationToken cancellationToken = default)
    {
        JobSchedule schedule = await _context.JobSchedules
            .Include(candidate => candidate.PrintJob)
            .FirstOrDefaultAsync(
                candidate =>
                    candidate.PrintJobId == jobId ||
                    candidate.RootPrintJobId == jobId,
                cancellationToken) ?? throw new InvalidOperationException($"Job '{jobId}' is not scheduled");
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
    /// <param name="actorSubject">Authenticated actor requesting the resume.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    public async Task ResumeSchedulingAsync(
        Guid jobId,
        string actorSubject,
        CancellationToken cancellationToken = default)
    {
        JobSchedule schedule = await _context.JobSchedules
            .Include(candidate => candidate.PrintJob)
            .FirstOrDefaultAsync(
                candidate =>
                    candidate.PrintJobId == jobId ||
                    candidate.RootPrintJobId == jobId,
                cancellationToken) ?? throw new InvalidOperationException($"Job '{jobId}' is not scheduled");
        await EnsureActorMayScheduleAsync(schedule.PrintJob, actorSubject, cancellationToken);

        schedule.IsPaused = false;
        schedule.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("[JobScheduling] Resumed scheduling for job '{JobId}'", jobId);
    }

    /// <summary>
    /// Get all scheduled jobs (active and not paused)
    /// </summary>
    /// <param name="actorSubject">Authenticated actor whose visible schedules are returned.</param>
    /// <param name="dateFrom">Optional filter for jobs scheduled on or after this date.</param>
    /// <param name="dateTo">Optional filter for jobs scheduled on or before this date.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    public async Task<IEnumerable<ScheduledJobDto>> GetScheduledJobsAsync(
        string actorSubject,
        DateTime? dateFrom = null,
        DateTime? dateTo = null,
        CancellationToken cancellationToken = default)
    {
        IQueryable<JobSchedule> query = _context.JobSchedules
            .Where(js =>
                (js.IsActive && !js.IsPaused) ||
                js.RequiresOperatorReauthorization)
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

        if (_resourceAuthorization is null)
        {
            return [];
        }

        IReadOnlySet<Guid> authorizedJobIds =
            await _resourceAuthorization.FilterActorAccessibleJobIdsAsync(
                actorSubject,
                schedules.Select(schedule => schedule.PrintJobId).ToArray(),
                PrinterGroupAccessLevel.View,
                cancellationToken);
        return schedules
            .Where(schedule => authorizedJobIds.Contains(schedule.PrintJobId))
            .Select(ToDto)
            .ToList();
    }

    /// <summary>
    /// Get a specific scheduled job
    /// </summary>
    /// <param name="jobId">The unique identifier of the scheduled job.</param>
    /// <param name="actorSubject">Authenticated actor requesting the schedule.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    public async Task<ScheduledJobDto?> GetScheduledJobAsync(
        Guid jobId,
        string actorSubject,
        CancellationToken cancellationToken = default)
    {
        JobSchedule? schedule = await _context.JobSchedules
            .Include(js => js.PrintJob)
            .ThenInclude(j => j.AssignedPrinter)
            .FirstOrDefaultAsync(
                schedule =>
                    schedule.PrintJobId == jobId ||
                    schedule.RootPrintJobId == jobId,
                cancellationToken);

        return schedule is not null &&
            await ActorMayAccessJobAsync(
                schedule.PrintJob,
                actorSubject,
                PrinterGroupAccessLevel.View,
                cancellationToken)
                ? ToDto(schedule)
                : null;
    }

    /// <summary>
    /// Get execution history for a scheduled job
    /// </summary>
    /// <param name="jobId">The unique identifier of the job to get history for.</param>
    /// <param name="actorSubject">Authenticated actor requesting execution history.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    public async Task<IReadOnlyList<JobExecutionDto>?> GetExecutionHistoryAsync(
        Guid jobId,
        string actorSubject,
        CancellationToken cancellationToken = default)
    {
        JobSchedule? schedule = await _context.JobSchedules
            .AsNoTracking()
            .Include(candidate => candidate.PrintJob)
            .FirstOrDefaultAsync(
                candidate =>
                    candidate.PrintJobId == jobId ||
                    candidate.RootPrintJobId == jobId,
                cancellationToken);
        if (schedule is null ||
            !await ActorMayAccessJobAsync(
                schedule.PrintJob,
                actorSubject,
                PrinterGroupAccessLevel.View,
                cancellationToken))
        {
            return null;
        }

        List<JobExecution> executions = await _context.JobExecutions
            .Where(execution => execution.JobScheduleId == schedule.Id)
            .OrderByDescending(je => je.ScheduledExecutionTime)
            .ToListAsync(cancellationToken);

        return executions.Select(je => new JobExecutionDto
        {
            Id = je.Id,
            ScheduledExecutionTime = je.ScheduledExecutionTime,
            ActualStartTime = je.ActualStartTime,
            Status = je.Status,
            Message = je.Message,
            OccurrenceJobId = je.OccurrencePrintJobId,
            DispatchAttemptId = je.DispatchAttemptId,
            DurationSeconds = je.ActualStartTime.HasValue
                ? (int)(DateTime.UtcNow - je.ActualStartTime.Value).TotalSeconds
                : null
        }).ToList();
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
            .ThenInclude(job => job.ToolheadUsages)
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
                if (schedule.PrintJob is null ||
                    schedule.RequiresOperatorReauthorization ||
                    string.IsNullOrWhiteSpace(schedule.InitiatingActorSubject) ||
                    !await ActorMayAccessJobAsync(
                        schedule.PrintJob,
                        schedule.InitiatingActorSubject,
                        PrinterGroupAccessLevel.Submit,
                        cancellationToken))
                {
                    await MarkRequiresOperatorReauthorizationAsync(
                        schedule,
                        now,
                        cancellationToken);
                    continue;
                }

                if (await ResolveUnknownOccurrenceAsync(schedule, cancellationToken))
                {
                    continue;
                }

                // Create execution record
                var execution = new JobExecution
                {
                    JobScheduleId = schedule.Id,
                    OccurrencePrintJobId = schedule.PrintJobId,
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
                ScheduledOccurrenceDisposition disposition =
                    await TriggerScheduledDispatchAsync(
                        schedule,
                        execution,
                        cancellationToken);

                _logger.LogInformation(
                    "[JobScheduling] Scheduled occurrence for job '{JobId}' completed with {Disposition}",
                    schedule.PrintJobId,
                    disposition);

                if (disposition == ScheduledOccurrenceDisposition.Accepted)
                {
                    await ConsumeAcceptedOccurrenceAsync(
                        schedule,
                        execution,
                        cancellationToken);
                }

                await _context.SaveChangesAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "[JobScheduling] Failed to trigger scheduled occurrence for job '{JobId}'",
                    schedule.PrintJobId);
            }
        }
    }

    /// <summary>
    /// Routes a due schedule through the shared dispatch orchestration.
    /// The execution record is updated with the typed outcome so an operator can see why a
    /// scheduled start was refused instead of finding a job silently stuck in Printing.
    /// </summary>
    private async Task<ScheduledOccurrenceDisposition> TriggerScheduledDispatchAsync(
        JobSchedule schedule,
        JobExecution execution,
        CancellationToken cancellationToken)
    {
        if (schedule.PrintJob is null)
        {
            execution.Status = "Failed";
            execution.Message = "Scheduled job no longer exists.";
            await _context.SaveChangesAsync(cancellationToken);
            return ScheduledOccurrenceDisposition.RetryableFailure;
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
            return ScheduledOccurrenceDisposition.RetryableFailure;
        }

        try
        {
            QueuedPrintJobDto result = await _printJobManagement.DispatchJobAsync(
                schedule.PrintJobId.ToString(),
                schedule.InitiatingActorSubject!,
                ifMatchJobRowVersion: null,
                cancellationToken);

            DispatchAttemptResultDto? dispatch = result.DispatchResult;
            execution.DispatchAttemptId = dispatch?.AttemptId;
            if (dispatch?.Outcome == DispatchAttemptOutcome.Accepted)
            {
                execution.Status = "Completed";
                execution.Message = "The backend confirmed the scheduled start.";
                await _context.SaveChangesAsync(cancellationToken);
                return ScheduledOccurrenceDisposition.Accepted;
            }
            else if (dispatch?.Outcome == DispatchAttemptOutcome.Unknown)
            {
                execution.Status = "Unknown";
                execution.Message = "The backend start requires reconciliation.";
                await _context.SaveChangesAsync(cancellationToken);
                return ScheduledOccurrenceDisposition.AwaitingReconciliation;
            }
            else
            {
                execution.Status = dispatch?.Outcome switch
                {
                    DispatchAttemptOutcome.Rejected => "Rejected",
                    DispatchAttemptOutcome.FailedBeforeStart => "FailedBeforeStart",
                    _ => "FailedBeforeStart",
                };
                execution.Message = "The scheduled start was not accepted.";
                await _context.SaveChangesAsync(cancellationToken);
                return ScheduledOccurrenceDisposition.RetryableFailure;
            }
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                exception,
                "[JobScheduling] Scheduled dispatch client failed for job {JobId}; resolving durable outcome",
                schedule.PrintJobId);
            QueueDispatchAttempt? durableAttempt = await _context.QueueDispatchAttempts
                .AsNoTracking()
                .Where(attempt => attempt.PrintJobId == schedule.PrintJobId)
                .OrderByDescending(attempt => attempt.ClaimedAtUtc)
                .FirstOrDefaultAsync(CancellationToken.None);
            execution.DispatchAttemptId = durableAttempt?.Id;
            if (durableAttempt?.Outcome == DispatchAttemptOutcome.Accepted ||
                durableAttempt?.BackendCallPhase is DispatchBackendCallPhase.Accepted or
                    DispatchBackendCallPhase.PostAccept)
            {
                execution.Status = "Completed";
                execution.Message =
                    "The durable dispatch attempt confirms backend acceptance.";
                await _context.SaveChangesAsync(CancellationToken.None);
                return ScheduledOccurrenceDisposition.Accepted;
            }

            if ((durableAttempt?.Outcome is DispatchAttemptOutcome.Unknown or
                    DispatchAttemptOutcome.InProgress) &&
                durableAttempt.BackendCallPhase is
                    DispatchBackendCallPhase.BackendCall or
                    DispatchBackendCallPhase.AwaitingReconciliation)
            {
                execution.Status = "Unknown";
                execution.Message =
                    "The durable dispatch attempt requires reconciliation.";
                await _context.SaveChangesAsync(CancellationToken.None);
                return ScheduledOccurrenceDisposition.AwaitingReconciliation;
            }

            execution.Status = "FailedBeforeStart";
            execution.Message = "The scheduled start failed before backend acceptance.";
            await _context.SaveChangesAsync(cancellationToken);
            return ScheduledOccurrenceDisposition.RetryableFailure;
        }
    }

    private async Task EnsureActorMayScheduleAsync(
        PrintJob job,
        string actorSubject,
        CancellationToken ct)
    {
        if (!await ActorMayAccessJobAsync(
            job,
            actorSubject,
            PrinterGroupAccessLevel.Submit,
            ct))
        {
            throw new UnauthorizedAccessException("The scheduled queue resource was not found.");
        }
    }

    private async Task<bool> ActorMayAccessJobAsync(
        PrintJob job,
        string actorSubject,
        PrinterGroupAccessLevel minimumAccess,
        CancellationToken ct)
    {
        if (_resourceAuthorization is null ||
            !Guid.TryParse(actorSubject, out _))
        {
            return false;
        }

        bool canAccessJob = await _resourceAuthorization.CanActorAccessJobAsync(
            actorSubject,
            job.Id,
            minimumAccess,
            ct);
        bool canAccessPrinter = job.AssignedPrinterId.HasValue &&
            await _resourceAuthorization.CanActorAccessPrinterAsync(
                actorSubject,
                job.AssignedPrinterId.Value,
                minimumAccess,
                ct);
        bool canAccessProject = !job.CalibrationProjectId.HasValue ||
            await _resourceAuthorization.CanActorAccessProjectAsync(
                actorSubject,
                job.CalibrationProjectId.Value,
                ct);
        return canAccessJob && canAccessPrinter && canAccessProject;
    }

    private async Task MarkRequiresOperatorReauthorizationAsync(
        JobSchedule schedule,
        DateTime now,
        CancellationToken ct)
    {
        schedule.IsActive = false;
        schedule.IsPaused = true;
        schedule.RequiresOperatorReauthorization = true;
        schedule.UpdatedAt = now;
        _context.JobExecutions.Add(new JobExecution
        {
            JobScheduleId = schedule.Id,
            ScheduledExecutionTime = schedule.ScheduledStartTime,
            ActualStartTime = now,
            Status = "ReauthorizationRequired",
            Message =
                "The originating actor is missing or no longer has access to the job, printer, and project.",
        });
        await _context.SaveChangesAsync(ct);
        _logger.LogWarning(
            "[JobScheduling] Disabled schedule {ScheduleId} for job {JobId}; operator reauthorization is required",
            schedule.Id,
            schedule.PrintJobId);
    }

    private async Task<bool> ResolveUnknownOccurrenceAsync(
        JobSchedule schedule,
        CancellationToken ct)
    {
        JobExecution? unresolved = await _context.JobExecutions
            .Where(execution =>
                execution.JobScheduleId == schedule.Id &&
                execution.ScheduledExecutionTime == schedule.ScheduledStartTime &&
                execution.Status == "Unknown")
            .OrderByDescending(execution => execution.CreatedAt)
            .FirstOrDefaultAsync(ct);
        if (unresolved is null)
        {
            return false;
        }

        if (unresolved.DispatchAttemptId is not Guid attemptId)
        {
            return true;
        }

        QueueDispatchAttempt? attempt = await _context.QueueDispatchAttempts
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == attemptId, ct);
        if (attempt is null ||
            attempt.Outcome is DispatchAttemptOutcome.Unknown or
                DispatchAttemptOutcome.InProgress)
        {
            return true;
        }

        unresolved.UpdatedAt = DateTime.UtcNow;
        if (attempt.Outcome == DispatchAttemptOutcome.Accepted)
        {
            unresolved.Status = "Completed";
            unresolved.Message = "Reconciliation confirmed backend acceptance.";
            await ConsumeAcceptedOccurrenceAsync(schedule, unresolved, ct);
            await _context.SaveChangesAsync(ct);
            return true;
        }

        unresolved.Status = attempt.Outcome == DispatchAttemptOutcome.Rejected
            ? "Rejected"
            : "FailedBeforeStart";
        unresolved.Message = "Reconciliation confirmed the occurrence was not accepted.";
        await _context.SaveChangesAsync(ct);
        return false;
    }

    private async Task ConsumeAcceptedOccurrenceAsync(
        JobSchedule schedule,
        JobExecution execution,
        CancellationToken ct)
    {
        schedule.RootPrintJobId = StableJobId(schedule);
        if (!AdvanceSchedule(schedule))
        {
            return;
        }

        if (schedule.PrintJob.JobKind == JobKind.FilamentCalibration)
        {
            schedule.IsActive = false;
            schedule.IsPaused = true;
            execution.Message =
                "Backend acceptance was confirmed; recurring calibration requires a new reviewed job.";
            return;
        }

        PrintJob previousOccurrence = schedule.PrintJob;
        PrintJob nextOccurrence = await CloneRecurringOccurrenceAsync(
            previousOccurrence,
            ct);
        previousOccurrence.Schedule = null;
        schedule.PrintJobId = nextOccurrence.Id;
        schedule.PrintJob = nextOccurrence;
        nextOccurrence.Schedule = schedule;
        _context.PrintJobs.Add(nextOccurrence);
    }

    private bool AdvanceSchedule(JobSchedule schedule)
    {
        if (string.IsNullOrWhiteSpace(schedule.RecurrencePattern))
        {
            schedule.IsActive = false;
            schedule.UpdatedAt = DateTime.UtcNow;
            return false;
        }

        TimeZoneInfo timeZone = GetTimeZoneInfo(schedule.TimeZone);
        DateTime local = DateTime.SpecifyKind(
            TimeZoneInfo.ConvertTimeFromUtc(
                DateTime.SpecifyKind(schedule.ScheduledStartTime, DateTimeKind.Utc),
                timeZone),
            DateTimeKind.Unspecified);
        int interval = Math.Max(1, schedule.RecurrenceInterval);
        DateTime nextLocal = schedule.RecurrencePattern switch
        {
            "Daily" => local.AddDays(interval),
            "Weekly" => local.AddDays(7 * interval),
            "Monthly" => local.AddMonths(interval),
            _ => throw new InvalidOperationException(
                $"Unsupported recurrence pattern '{schedule.RecurrencePattern}'."),
        };
        DateTime nextUtc = ConvertRecurringWallTimeToUtc(nextLocal, timeZone);
        if (schedule.RecurrenceEndDate.HasValue &&
            nextUtc > schedule.RecurrenceEndDate.Value)
        {
            schedule.IsActive = false;
            schedule.UpdatedAt = DateTime.UtcNow;
            return false;
        }

        schedule.ScheduledStartTime = nextUtc;
        schedule.UpdatedAt = DateTime.UtcNow;
        return true;
    }

    private async Task<PrintJob> CloneRecurringOccurrenceAsync(
        PrintJob source,
        CancellationToken ct)
    {
        DateTime now = DateTime.UtcNow;
        var occurrence = new PrintJob
        {
            Id = Guid.NewGuid(),
            Name = source.Name,
            GcodeFileId = source.GcodeFileId,
            AssignedPrinterId = source.AssignedPrinterId,
            Status = source.AssignedPrinterId.HasValue
                ? PrintJobStatus.Assigned
                : PrintJobStatus.Queued,
            Priority = source.Priority,
            QueuePosition = await _positionAllocator.AllocateAsync(
                source.AssignedPrinterId,
                ct),
            RequiredNozzleDiameter = source.RequiredNozzleDiameter,
            RequiredMaterialType = source.RequiredMaterialType,
            RequiredCapabilities = source.RequiredCapabilities?.ToArray(),
            EstimatedPrintTime = source.EstimatedPrintTime,
            EstimatedFilamentUsage = source.EstimatedFilamentUsage,
            PreferredPrinterIds = source.PreferredPrinterIds?.ToArray(),
            ExcludedPrinterIds = source.ExcludedPrinterIds?.ToArray(),
            Notes = source.Notes,
            DeadlineAtUtc = source.DeadlineAtUtc.HasValue
                ? now + (source.DeadlineAtUtc.Value - source.QueuedAt)
                : null,
            Copies = source.Copies,
            ProjectFileId = source.ProjectFileId,
            ProjectId = source.ProjectId,
            ProjectName = source.ProjectName,
            SpoolmanFilamentId = source.SpoolmanFilamentId,
            SpoolmanSpoolId = source.SpoolmanSpoolId,
            FilamentName = source.FilamentName,
            FilamentVendor = source.FilamentVendor,
            FilamentColor = source.FilamentColor,
            PlateIndex = source.PlateIndex,
            PlateName = source.PlateName,
            JobKind = JobKind.Standard,
            CreatorSubject = source.CreatorSubject,
            CreatedAt = now,
            UpdatedAt = now,
            QueuedAt = now,
        };
        foreach (PrintJobToolheadUsage usage in source.ToolheadUsages)
        {
            occurrence.ToolheadUsages.Add(new PrintJobToolheadUsage
            {
                Id = Guid.NewGuid(),
                PrintJobId = occurrence.Id,
                ToolheadIndex = usage.ToolheadIndex,
                SpoolmanSpoolId = usage.SpoolmanSpoolId,
                SlicerEstimateGrams = usage.SlicerEstimateGrams,
                FilamentName = usage.FilamentName,
                FilamentColor = usage.FilamentColor,
            });
        }

        return occurrence;
    }

    private ScheduledJobDto ToDto(JobSchedule schedule)
    {
        DateTime utc = DateTime.SpecifyKind(
            schedule.ScheduledStartTime,
            DateTimeKind.Utc);
        return new ScheduledJobDto
        {
            Id = schedule.Id,
            JobId = StableJobId(schedule),
            PrinterId = schedule.PrintJob.AssignedPrinterId,
            PrinterName = schedule.PrintJob.AssignedPrinter?.Name ?? "Unassigned",
            JobName = schedule.PrintJob.Name ?? "Unknown",
            ScheduledStartTimeUtc = utc,
            ScheduledLocalTime = DateTime.SpecifyKind(
                ConvertFromUtc(utc, schedule.TimeZone),
                DateTimeKind.Unspecified),
            TimeZone = schedule.TimeZone,
            RecurrencePattern = schedule.RecurrencePattern,
            RecurrenceInterval = schedule.RecurrenceInterval,
            RecurrenceEndTimeUtc = schedule.RecurrenceEndDate.HasValue
                ? DateTime.SpecifyKind(
                    schedule.RecurrenceEndDate.Value,
                    DateTimeKind.Utc)
                : null,
            IsActive = schedule.IsActive,
            IsPaused = schedule.IsPaused,
            RequiresOperatorReauthorization =
                schedule.RequiresOperatorReauthorization,
        };
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
        return ConvertReviewedWallTimeToUtc(userTime, timeZone, nameof(userTime));
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

    private static TimeZoneInfo GetTimeZoneInfo(string timeZoneId)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        }
        catch (TimeZoneNotFoundException exception)
        {
            throw new ArgumentException(
                $"Invalid timezone: {timeZoneId}",
                nameof(timeZoneId),
                exception);
        }
        catch (InvalidTimeZoneException exception)
        {
            throw new ArgumentException(
                $"Invalid timezone: {timeZoneId}",
                nameof(timeZoneId),
                exception);
        }
    }

    private static DateTime ConvertReviewedWallTimeToUtc(
        DateTime localTime,
        TimeZoneInfo timeZone,
        string parameterName)
    {
        if (localTime.Kind != DateTimeKind.Unspecified)
        {
            throw new ArgumentException(
                "Scheduled wall time must omit an offset and UTC designator; pair it with timeZone.",
                parameterName);
        }

        if (timeZone.IsInvalidTime(localTime))
        {
            throw new ArgumentException(
                "Scheduled wall time does not exist because of a daylight-saving transition.",
                parameterName);
        }

        if (timeZone.IsAmbiguousTime(localTime))
        {
            throw new ArgumentException(
                "Scheduled wall time is ambiguous because of a daylight-saving transition.",
                parameterName);
        }

        return TimeZoneInfo.ConvertTimeToUtc(localTime, timeZone);
    }

    private static DateTime ConvertRecurringWallTimeToUtc(
        DateTime localTime,
        TimeZoneInfo timeZone)
    {
        DateTime candidate = DateTime.SpecifyKind(localTime, DateTimeKind.Unspecified);
        for (int minute = 0; minute <= 180 && timeZone.IsInvalidTime(candidate); minute++)
        {
            candidate = candidate.AddMinutes(1);
        }

        if (timeZone.IsInvalidTime(candidate))
        {
            throw new InvalidOperationException(
                "Could not resolve the next recurring execution across the daylight-saving gap.");
        }

        if (timeZone.IsAmbiguousTime(candidate))
        {
            TimeSpan selectedOffset = timeZone
                .GetAmbiguousTimeOffsets(candidate)
                .Max();
            return new DateTimeOffset(candidate, selectedOffset).UtcDateTime;
        }

        return TimeZoneInfo.ConvertTimeToUtc(candidate, timeZone);
    }

    private static string? NormalizeRecurrence(string? recurrencePattern)
    {
        if (string.IsNullOrWhiteSpace(recurrencePattern) ||
            string.Equals(recurrencePattern, "Once", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return recurrencePattern.Trim().ToLowerInvariant() switch
        {
            "daily" => "Daily",
            "weekly" => "Weekly",
            "monthly" => "Monthly",
            _ => throw new ArgumentException(
                "Recurrence pattern must be Once, Daily, Weekly, or Monthly.",
                nameof(recurrencePattern)),
        };
    }

    private static int ValidateRecurrenceInterval(
        string? recurrencePattern,
        int recurrenceInterval)
    {
        if (recurrencePattern is null)
        {
            return 1;
        }

        return recurrenceInterval is >= 1 and <= 365
            ? recurrenceInterval
            : throw new ArgumentOutOfRangeException(
                nameof(recurrenceInterval),
                "Recurrence interval must be between 1 and 365.");
    }

    private static Guid StableJobId(JobSchedule schedule) =>
        schedule.RootPrintJobId == Guid.Empty
            ? schedule.PrintJobId
            : schedule.RootPrintJobId;

    private static void EnsureRecurrenceSupported(
        PrintJob job,
        string? recurrencePattern)
    {
        if (recurrencePattern is not null &&
            job.JobKind == JobKind.FilamentCalibration)
        {
            throw new ArgumentException(
                "Calibration jobs are immutable one-shot jobs and cannot recur.",
                nameof(recurrencePattern));
        }
    }

    private enum ScheduledOccurrenceDisposition
    {
        Accepted,
        RetryableFailure,
        AwaitingReconciliation,
    }
}

/// <summary>
/// DTO for scheduled job information
/// </summary>
public class ScheduledJobDto
{
    public Guid Id { get; set; }

    public Guid JobId { get; set; }

    public string JobName { get; set; } = string.Empty;

    public string PrinterName { get; set; } = string.Empty;

    public Guid? PrinterId { get; set; }

    public DateTime ScheduledStartTimeUtc { get; set; }

    public DateTime ScheduledLocalTime { get; set; }

    public string TimeZone { get; set; } = "UTC";

    public string? RecurrencePattern { get; set; }

    public int RecurrenceInterval { get; set; } = 1;

    public DateTime? RecurrenceEndTimeUtc { get; set; }

    public bool IsActive { get; set; }

    public bool IsPaused { get; set; }

    public bool RequiresOperatorReauthorization { get; set; }

    public string Status => RequiresOperatorReauthorization
        ? "reauthorizationRequired"
        : !IsActive
            ? "completed"
            : IsPaused
                ? "paused"
                : "active";
}

/// <summary>
/// DTO for job execution record
/// </summary>
public class JobExecutionDto
{
    public Guid Id { get; set; }

    public Guid? OccurrenceJobId { get; set; }

    public Guid? DispatchAttemptId { get; set; }

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
