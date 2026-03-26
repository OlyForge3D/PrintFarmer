using System.IO;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;

namespace Farm.Infrastructure.Services.Printers;

/// <summary>
/// Retrieves operator-focused print session timelines for a single printer.
/// </summary>
public interface IPrinterSessionTimelineService
{
    /// <summary>
    /// Returns recent print sessions for a printer, enriched with failure incidents.
    /// </summary>
    Task<PrinterSessionTimelineDto> GetRecentAsync(Guid printerId, int take, CancellationToken ct = default);
}

/// <summary>
/// Builds recent printer session timelines from persisted jobs, state transitions, and failure incidents.
/// </summary>
public sealed class PrinterSessionTimelineService(AppDbContext dbContext) : IPrinterSessionTimelineService
{
    /// <summary>
    /// Default number of sessions to return.
    /// </summary>
    public const int DefaultTake = 10;

    /// <summary>
    /// Maximum number of sessions to return.
    /// </summary>
    public const int MaxTake = 50;

    private readonly AppDbContext _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));

    /// <summary>
    /// Returns recent print sessions for a printer, enriched with failure incidents.
    /// </summary>
    public async Task<PrinterSessionTimelineDto> GetRecentAsync(Guid printerId, int take, CancellationToken ct = default)
    {
        Printer? printer = await _dbContext.Printers
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == printerId, ct);

        if (printer is null)
        {
            throw new KeyNotFoundException($"Printer {printerId} was not found.");
        }

        int normalizedTake = NormalizeTake(take);
        List<PrintJob> jobs = await _dbContext.PrintJobs
            .AsNoTracking()
            .Include(job => job.GcodeFile)
            .Include(job => job.StateHistory)
            .Where(job => job.AssignedPrinterId == printerId)
            .OrderByDescending(job => job.ActualEndTime ?? job.ActualStartTime ?? job.DispatchedAt ?? job.QueuedAt)
            .Take(normalizedTake)
            .ToListAsync(ct);

        Dictionary<Guid, List<FailureDetectionIncident>> incidentsByJobId = await GetIncidentsByJobAsync(printerId, jobs, ct);

        List<PrinterSessionTimelineSessionDto> sessions = jobs
            .Select(job => CreateSession(job, incidentsByJobId.TryGetValue(job.Id, out List<FailureDetectionIncident>? incidents) ? incidents : []))
            .OrderByDescending(session => session.Events.Count == 0 ? session.QueuedAt : session.Events[^1].OccurredAt)
            .ToList();

        return new PrinterSessionTimelineDto
        {
            PrinterId = printer.Id,
            PrinterName = printer.Name,
            ReturnedSessionCount = sessions.Count,
            Sessions = sessions,
        };
    }

    /// <summary>
    /// Finds incidents for the requested jobs and attaches orphaned incidents by session window.
    /// </summary>
    private async Task<Dictionary<Guid, List<FailureDetectionIncident>>> GetIncidentsByJobAsync(
        Guid printerId,
        List<PrintJob> jobs,
        CancellationToken ct)
    {
        Dictionary<Guid, List<FailureDetectionIncident>> incidentsByJobId = jobs.ToDictionary(job => job.Id, _ => new List<FailureDetectionIncident>());

        if (jobs.Count == 0)
        {
            return incidentsByJobId;
        }

        DateTime now = DateTime.UtcNow;
        DateTime earliestWindowStart = jobs.Min(GetSessionWindowStart);
        DateTime latestWindowEnd = jobs.Max(job => GetSessionWindowEnd(job, now));
        HashSet<Guid> jobIds = jobs.Select(job => job.Id).ToHashSet();

        List<FailureDetectionIncident> incidents = await _dbContext.FailureDetectionIncidents
            .AsNoTracking()
            .Where(incident =>
                incident.PrinterId == printerId &&
                incident.DetectedAt >= earliestWindowStart &&
                incident.DetectedAt <= latestWindowEnd)
            .OrderBy(incident => incident.DetectedAt)
            .ToListAsync(ct);

        foreach (FailureDetectionIncident incident in incidents)
        {
            if (incident.JobId.HasValue && jobIds.Contains(incident.JobId.Value))
            {
                incidentsByJobId[incident.JobId.Value].Add(incident);
                continue;
            }

            PrintJob? matchedJob = jobs
                .Where(job => incident.DetectedAt >= GetSessionWindowStart(job) && incident.DetectedAt <= GetSessionWindowEnd(job, now))
                .OrderByDescending(GetSessionWindowStart)
                .FirstOrDefault();

            if (matchedJob is not null)
            {
                incidentsByJobId[matchedJob.Id].Add(incident);
            }
        }

        return incidentsByJobId;
    }

    /// <summary>
    /// Creates the session DTO for a single print job.
    /// </summary>
    private static PrinterSessionTimelineSessionDto CreateSession(PrintJob job, List<FailureDetectionIncident> incidents)
    {
        List<PrinterSessionTimelineEventDto> events = BuildEvents(job, incidents);

        return new PrinterSessionTimelineSessionDto
        {
            JobId = job.Id,
            JobName = ResolveJobName(job),
            FileName = ResolveFileName(job, incidents),
            Status = job.Status,
            QueuedAt = job.QueuedAt,
            DispatchedAt = job.DispatchedAt,
            StartedAt = job.ActualStartTime,
            EndedAt = job.ActualEndTime,
            DurationSeconds = job.ActualPrintTime.HasValue ? (int)job.ActualPrintTime.Value.TotalSeconds : null,
            FailureReason = job.FailureReason,
            HasFailureIncident = incidents.Count != 0,
            FailureIncidentCount = incidents.Count,
            Events = events,
        };
    }

    /// <summary>
    /// Builds the ordered event list for a print session.
    /// </summary>
    private static List<PrinterSessionTimelineEventDto> BuildEvents(PrintJob job, List<FailureDetectionIncident> incidents)
    {
        List<PrinterSessionTimelineEventDto> events =
        [
            new PrinterSessionTimelineEventDto
            {
                Type = PrinterSessionTimelineEventType.Queued,
                OccurredAt = job.QueuedAt,
                Summary = "Job queued",
            }
        ];

        if (job.DispatchedAt.HasValue)
        {
            string dispatchedSummary = job.DispatchScore.HasValue
                ? $"Job dispatched (score {job.DispatchScore.Value:F2})"
                : "Job dispatched";

            events.Add(new PrinterSessionTimelineEventDto
            {
                Type = PrinterSessionTimelineEventType.Dispatched,
                OccurredAt = job.DispatchedAt.Value,
                Summary = dispatchedSummary,
            });
        }

        foreach (JobStateHistory transition in job.StateHistory.OrderBy(transition => transition.TransitionedAtUtc))
        {
            events.Add(new PrinterSessionTimelineEventDto
            {
                Type = PrinterSessionTimelineEventType.StateTransition,
                OccurredAt = transition.TransitionedAtUtc,
                Summary = $"{transition.FromState} → {transition.ToState}",
                FromState = transition.FromState,
                ToState = transition.ToState,
                DurationSeconds = transition.DurationInState.HasValue ? (int)transition.DurationInState.Value.TotalSeconds : null,
                Notes = transition.Notes,
            });
        }

        if (job.ActualStartTime.HasValue && !HasTransitionToState(job.StateHistory, PrintJobStatus.Printing.ToString(), job.ActualStartTime.Value))
        {
            events.Add(new PrinterSessionTimelineEventDto
            {
                Type = PrinterSessionTimelineEventType.SessionStarted,
                OccurredAt = job.ActualStartTime.Value,
                Summary = "Print started",
                ToState = PrintJobStatus.Printing.ToString(),
            });
        }

        foreach (FailureDetectionIncident incident in incidents.OrderBy(incident => incident.DetectedAt))
        {
            string incidentSummary = incident.AutoPaused
                ? $"Failure detected ({incident.Confidence:F3}); print auto-paused"
                : $"Failure detected ({incident.Confidence:F3})";

            events.Add(new PrinterSessionTimelineEventDto
            {
                Type = PrinterSessionTimelineEventType.FailureDetected,
                OccurredAt = incident.DetectedAt,
                Summary = incidentSummary,
                Confidence = incident.Confidence,
                AutoPaused = incident.AutoPaused,
                SnapshotUrl = incident.SnapshotUrl,
                Notes = incident.JobName,
            });
        }

        if (job.ActualEndTime.HasValue && !HasTransitionToState(job.StateHistory, job.Status.ToString(), job.ActualEndTime.Value))
        {
            string terminalSummary = job.Status switch
            {
                PrintJobStatus.Completed => "Print completed",
                PrintJobStatus.Failed => "Print failed",
                PrintJobStatus.Cancelled => "Print cancelled",
                _ => $"Session ended ({job.Status})",
            };

            events.Add(new PrinterSessionTimelineEventDto
            {
                Type = PrinterSessionTimelineEventType.SessionEnded,
                OccurredAt = job.ActualEndTime.Value,
                Summary = terminalSummary,
                ToState = job.Status.ToString(),
                Notes = job.FailureReason,
            });
        }

        return events
            .OrderBy(@event => @event.OccurredAt)
            .ThenBy(@event => GetEventSortPriority(@event.Type))
            .ToList();
    }

    /// <summary>
    /// Resolves the operator-facing job name.
    /// </summary>
    private static string ResolveJobName(PrintJob job)
        => string.IsNullOrWhiteSpace(job.GcodeFile?.Name) ? job.Name : job.GcodeFile.Name;

    /// <summary>
    /// Resolves the operator-facing file name.
    /// </summary>
    private static string? ResolveFileName(PrintJob job, List<FailureDetectionIncident> incidents)
    {
        string? incidentFileName = incidents
            .Select(incident => incident.FileName)
            .FirstOrDefault(fileName => !string.IsNullOrWhiteSpace(fileName));

        if (!string.IsNullOrWhiteSpace(incidentFileName))
        {
            return incidentFileName;
        }

        string? gcodeName = job.GcodeFile?.Name;
        if (!string.IsNullOrWhiteSpace(gcodeName))
        {
            return Path.GetFileName(gcodeName);
        }

        return string.IsNullOrWhiteSpace(job.Name) ? null : Path.GetFileName(job.Name);
    }

    /// <summary>
    /// Returns the window start used for linking orphaned incidents.
    /// </summary>
    private static DateTime GetSessionWindowStart(PrintJob job)
        => job.ActualStartTime ?? job.DispatchedAt ?? job.QueuedAt;

    /// <summary>
    /// Returns the window end used for linking orphaned incidents.
    /// </summary>
    private static DateTime GetSessionWindowEnd(PrintJob job, DateTime now)
    {
        if (job.ActualEndTime.HasValue)
        {
            return job.ActualEndTime.Value;
        }

        DateTime? latestTransitionAt = job.StateHistory.Count == 0
            ? null
            : job.StateHistory.Max(transition => transition.TransitionedAtUtc);

        if (IsActiveStatus(job.Status))
        {
            return now;
        }

        return latestTransitionAt ?? job.UpdatedAt;
    }

    /// <summary>
    /// Checks whether the job already has a matching transition near the supplied timestamp.
    /// </summary>
    private static bool HasTransitionToState(IEnumerable<JobStateHistory> transitions, string toState, DateTime timestamp)
        => transitions.Any(transition =>
            string.Equals(transition.ToState, toState, StringComparison.OrdinalIgnoreCase) &&
            Math.Abs((transition.TransitionedAtUtc - timestamp).TotalSeconds) < 1);

    /// <summary>
    /// Returns the stable event sort priority for equal timestamps.
    /// </summary>
    private static int GetEventSortPriority(PrinterSessionTimelineEventType eventType)
        => eventType switch
        {
            PrinterSessionTimelineEventType.Queued => 0,
            PrinterSessionTimelineEventType.Dispatched => 1,
            PrinterSessionTimelineEventType.SessionStarted => 2,
            PrinterSessionTimelineEventType.StateTransition => 3,
            PrinterSessionTimelineEventType.FailureDetected => 4,
            PrinterSessionTimelineEventType.SessionEnded => 5,
            _ => 99,
        };

    /// <summary>
    /// Identifies active job states that should remain open-ended.
    /// </summary>
    private static bool IsActiveStatus(PrintJobStatus status)
        => status is PrintJobStatus.Assigned or PrintJobStatus.Starting or PrintJobStatus.Printing or PrintJobStatus.Paused;

    /// <summary>
    /// Normalizes the take value into the supported bounds.
    /// </summary>
    private static int NormalizeTake(int take)
    {
        if (take <= 0)
        {
            return DefaultTake;
        }

        return Math.Min(take, MaxTake);
    }
}
