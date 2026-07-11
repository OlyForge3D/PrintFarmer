using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Dtos.Attention;
using Farm.Infrastructure.Services.FailureDetection;
using Farm.Infrastructure.Services.Queue;

namespace Farm.Infrastructure.Services.Attention.Sources;

/// <summary>
/// Attention source that surfaces recent, high-signal print-failure incidents.
/// </summary>
/// <remarks>
/// <para>
/// Auto-paused incidents are treated as <see cref="AttentionSeverity.Critical"/> because
/// the printer is stopped and requires operator triage; non-auto-paused incidents are
/// downgraded to <see cref="AttentionSeverity.Warning"/>.
/// </para>
/// <para>
/// A stale incident window keeps the feed to actionable items only; older incidents
/// remain queryable via the failure-detection history endpoint.
/// </para>
/// <para>
/// Composition suppresses incidents that can no longer be truthfully acted on (issue #707,
/// review R2): incidents already resolved by a successful action, incidents whose job has
/// gone missing, moved to a different printer, or is no longer active (superseded). This
/// guarantees a card disappears on refetch after its advertised action succeeds rather than
/// lingering for the full stale window.
/// </para>
/// </remarks>
public sealed class FailureAttentionSource(
    IFailureDetectionIncidentHistoryService history,
    IQueueDataService queueData,
    TimeProvider? timeProvider = null) : IAttentionSource
{
    /// <summary>Only surface incidents newer than this window.</summary>
    public static readonly TimeSpan StaleWindow = TimeSpan.FromHours(24);

    /// <summary>Cap on incidents fetched per composition pass.</summary>
    private const int MaxIncidents = 50;

    private readonly IFailureDetectionIncidentHistoryService _history =
        history ?? throw new ArgumentNullException(nameof(history));

    private readonly IQueueDataService _queueData =
        queueData ?? throw new ArgumentNullException(nameof(queueData));

    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    /// <inheritdoc />
    public string SourceName => "failure";

    /// <inheritdoc />
    public async Task<IReadOnlyList<AttentionItemDto>> GetItemsAsync(CancellationToken cancellationToken)
    {
        List<FailureDetectionDto> incidents =
            await _history.GetRecentAsync(printerId: null, take: MaxIncidents, ct: cancellationToken);

        DateTime cutoff = _clock.GetUtcNow().UtcDateTime - StaleWindow;
        List<AttentionItemDto> items = new(incidents.Count);

        foreach (FailureDetectionDto incident in incidents)
        {
            if (incident.DetectedAt < cutoff)
            {
                continue;
            }

            // A successful attention action already resolved this incident; suppress the card
            // even if the underlying job is still active (issue #707, R2).
            if (incident.ResolvedAtUtc is not null)
            {
                continue;
            }

            if (incident.Id is not Guid incidentId)
            {
                continue;
            }

            // Stale-incident safety: an incident without a JobId cannot be safely acted on
            // (we can't verify the printer's active job still matches). Suppress it from the
            // feed rather than surface an unactionable card.
            if (incident.JobId is not Guid incidentJobId)
            {
                continue;
            }

            // Job-identity suppression: only surface incidents whose referenced job still
            // exists, is still on this printer, and is still active. A missing, moved, or
            // superseded job means the card is no longer actionable and must not linger.
            PrintJob? job = await _queueData.GetPrintJobByIdAsync(incidentJobId, cancellationToken);
            if (job is null || job.AssignedPrinterId != incident.PrinterId || !IsActionable(job.Status))
            {
                continue;
            }

            int confidencePercent = (int)Math.Round(incident.Confidence * 100m);
            string title = incident.AutoPaused ? "Print failure — auto-paused" : "Possible print failure";
            string detail = incident.AutoPaused
                ? $"AI detected a failure ({confidencePercent}% confidence) on {incident.PrinterName}. Action: verify the print and resume or cancel."
                : $"AI flagged a possible failure ({confidencePercent}% confidence) on {incident.PrinterName}. Action: check the camera and pause if the print has failed.";

            // Only advertise actions that mutate real state end-to-end. Dismiss is intentionally
            // omitted because the server has no per-user failure-dismissal store; Snooze is the
            // supported client-side suppression.
            List<AttentionActionDto> actions = new(3);
            if (incident.AutoPaused)
            {
                actions.Add(new AttentionActionDto(AttentionActionKind.Resume, "Resume", RequiresConfirmation: true));
                actions.Add(new AttentionActionDto(AttentionActionKind.Cancel, "Cancel", RequiresConfirmation: true));
            }
            else
            {
                actions.Add(new AttentionActionDto(AttentionActionKind.Pause, "Pause", RequiresConfirmation: false));
                actions.Add(new AttentionActionDto(AttentionActionKind.Cancel, "Cancel", RequiresConfirmation: true));
            }

            actions.Add(new AttentionActionDto(AttentionActionKind.Snooze, "Snooze", RequiresConfirmation: false));

            items.Add(new AttentionItemDto(
                Id: AttentionIdPrefixes.Build(AttentionIdPrefixes.Failure, incidentId),
                Kind: AttentionKind.Failure,
                Severity: incident.AutoPaused ? AttentionSeverity.Critical : AttentionSeverity.Warning,
                PrinterId: incident.PrinterId,
                PrinterName: incident.PrinterName,
                Title: title,
                Detail: detail,
                OccurredAt: DateTime.SpecifyKind(incident.DetectedAt, DateTimeKind.Utc),
                Actions: actions,
                JobId: incidentJobId));
        }

        return items;
    }

    private static bool IsActionable(PrintJobStatus status)
        => status is PrintJobStatus.Starting or PrintJobStatus.Printing or PrintJobStatus.Paused;
}
