using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Dtos.Attention;
using Farm.Infrastructure.Repositories.Attention;
using Farm.Infrastructure.Services.Maintenance;
using Farm.Infrastructure.Services.Printers;
using Farm.Infrastructure.Services.Queue;
using Microsoft.Extensions.Logging;

namespace Farm.Infrastructure.Services.Attention;

/// <summary>
/// Default <see cref="IAttentionService"/> implementation.
/// </summary>
/// <remarks>
/// <para>
/// Composition rules:
/// </para>
/// <list type="bullet">
///   <item><description>Every registered <see cref="IAttentionSource"/> is invoked and its items merged.</description></item>
///   <item><description>Per-source failures are logged and swallowed so a single misbehaving source cannot blank the feed.</description></item>
///   <item><description>Items are de-duplicated by <see cref="AttentionItemDto.Id"/> (last writer wins).</description></item>
///   <item><description>Maintenance items are filtered <b>before</b> composition/pagination for callers who lack the <c>farm_admin</c> role, so non-admins never see or page over maintenance ids or details.</description></item>
///   <item><description>Sort order is severity DESC, then nearest deadline first (nulls last), then oldest <c>OccurredAt</c> first.</description></item>
///   <item><description>Per-user snoozes with expiry in the future suppress matching items, unless the item's <c>OccurredAt</c> is strictly newer than the snooze's <see cref="AttentionSnooze.AttentionItemAnchorAtUtc"/> anchor <b>and</b> the item opts into fresh-occurrence bypass (<see cref="AttentionItemDto.AllowFreshOccurrenceBypass"/>).</description></item>
/// </list>
/// </remarks>
public sealed class AttentionService(
    IEnumerable<IAttentionSource> sources,
    IAttentionSnoozeRepository snoozes,
    IPrintersService printersService,
    IMaintenanceAlertService maintenanceAlerts,
    IQueueDataService queueData,
    ILogger<AttentionService> logger,
    TimeProvider? timeProvider = null) : IAttentionService
{
    /// <summary>Default page size when the caller does not specify one.</summary>
    public const int DefaultPageSize = 50;

    /// <summary>Maximum page size the API will honour.</summary>
    public const int MaxPageSize = 200;

    /// <summary>Role required to see or act on maintenance attention items.</summary>
    public const string MaintenanceRoleName = "farm_admin";

    private readonly IReadOnlyList<IAttentionSource> _sources = (sources ?? throw new ArgumentNullException(nameof(sources))).ToList();
    private readonly IAttentionSnoozeRepository _snoozes = snoozes ?? throw new ArgumentNullException(nameof(snoozes));
    private readonly IPrintersService _printers = printersService ?? throw new ArgumentNullException(nameof(printersService));
    private readonly IMaintenanceAlertService _maintenanceAlerts = maintenanceAlerts ?? throw new ArgumentNullException(nameof(maintenanceAlerts));
    private readonly IQueueDataService _queueData = queueData ?? throw new ArgumentNullException(nameof(queueData));
    private readonly ILogger<AttentionService> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    /// <inheritdoc />
    public async Task<AttentionFeedDto> GetFeedAsync(
        Guid userId,
        bool isFarmAdmin,
        int page = 1,
        int pageSize = DefaultPageSize,
        CancellationToken cancellationToken = default)
    {
        int effectivePage = Math.Max(page, 1);
        int effectivePageSize = pageSize <= 0 ? DefaultPageSize : Math.Min(pageSize, MaxPageSize);

        DateTime now = _clock.GetUtcNow().UtcDateTime;

        // Composition — collect from every source; per-source failures do not blank the feed.
        List<AttentionItemDto> merged = new();
        foreach (IAttentionSource source in _sources)
        {
            try
            {
                IReadOnlyList<AttentionItemDto> items = await source.GetItemsAsync(cancellationToken);
                merged.AddRange(items);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[AttentionService] Source '{Source}' failed; skipping", source.SourceName);
            }
        }

        // Role-based filter: non-admin operators must not see maintenance items, ids, or
        // detail. Filter BEFORE dedupe/pagination/totals so pagination totals match what
        // the caller is actually authorized to see.
        if (!isFarmAdmin)
        {
            merged.RemoveAll(i => i.Kind == AttentionKind.Maintenance);
        }

        // De-duplicate by computed id (last-writer wins).
        Dictionary<string, AttentionItemDto> byId = new(StringComparer.Ordinal);
        foreach (AttentionItemDto item in merged)
        {
            byId[item.Id] = item;
        }

        // Load per-user snoozes and apply fresh-occurrence bypass using OccurredAt anchor.
        IReadOnlyList<AttentionSnooze> active = await _snoozes.GetActiveForUserAsync(userId, now, cancellationToken);
        Dictionary<string, AttentionSnooze> snoozeById = new(StringComparer.Ordinal);
        foreach (AttentionSnooze snooze in active)
        {
            snoozeById[snooze.AttentionItemId] = snooze;
        }

        List<AttentionItemDto> visible = new(byId.Count);
        foreach (AttentionItemDto item in byId.Values)
        {
            if (!snoozeById.TryGetValue(item.Id, out AttentionSnooze? snooze))
            {
                visible.Add(item);
                continue;
            }

            // Fresh-occurrence bypass: newer OccurredAt than the anchor supersedes the
            // snooze, but only for sources that opt in (stable OccurredAt). Sources with
            // moving timestamps (for example continuous Offline) MUST opt out.
            if (item.AllowFreshOccurrenceBypass
                && snooze.AttentionItemAnchorAtUtc is DateTime anchor
                && item.OccurredAt > anchor)
            {
                visible.Add(item);
            }
        }

        // Severity primary, then nearest deadline, then oldest first.
        List<AttentionItemDto> sorted = visible
            .OrderByDescending(i => (int)i.Severity)
            .ThenBy(i => i.DeadlineAt ?? DateTime.MaxValue)
            .ThenBy(i => i.OccurredAt)
            .ToList();

        int totalCount = sorted.Count;
        int totalPages = totalCount == 0 ? 0 : (int)Math.Ceiling(totalCount / (double)effectivePageSize);

        List<AttentionItemDto> pageItems = sorted
            .Skip((effectivePage - 1) * effectivePageSize)
            .Take(effectivePageSize)
            .ToList();

        // Healthy printers = enabled printers with no visible items (page-independent).
        HashSet<Guid> printersWithItems = new(visible.Select(i => i.PrinterId));
        List<Printer> allPrinters = await _printers.GetAllAsync(cancellationToken);
        List<Guid> healthy = allPrinters
            .Where(p => p.IsEnabled && !printersWithItems.Contains(p.Id))
            .Select(p => p.Id)
            .ToList();

        return new AttentionFeedDto(pageItems, totalCount, effectivePage, effectivePageSize, totalPages, healthy);
    }

    /// <inheritdoc />
    public async Task<AttentionItemDto?> FindItemAsync(Guid userId, string attentionItemId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(attentionItemId);

        // Walk the sources directly (bypass pagination and snooze suppression) so the
        // caller can operate on an item regardless of the current page slice.
        AttentionItemDto? match = await FindItemIgnoringSnoozeAsync(attentionItemId, cancellationToken);
        if (match is null)
        {
            return null;
        }

        DateTime now = _clock.GetUtcNow().UtcDateTime;
        IReadOnlyList<AttentionSnooze> active = await _snoozes.GetActiveForUserAsync(userId, now, cancellationToken);
        AttentionSnooze? snooze = active.FirstOrDefault(s => string.Equals(s.AttentionItemId, attentionItemId, StringComparison.Ordinal));
        if (snooze is null)
        {
            return match;
        }

        if (!match.AllowFreshOccurrenceBypass)
        {
            return null;
        }

        return snooze.AttentionItemAnchorAtUtc is DateTime anchor && match.OccurredAt > anchor
            ? match
            : null;
    }

    /// <inheritdoc />
    public async Task<SnoozeResult> SnoozeAsync(
        Guid userId,
        string attentionItemId,
        DateTime snoozedUntilUtc,
        DateTime? attentionItemAnchorAtUtc,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(attentionItemId))
        {
            return new SnoozeResult(Success: false, Reason: "Attention item id is required.", Snooze: null);
        }

        DateTime now = _clock.GetUtcNow().UtcDateTime;
        if (snoozedUntilUtc <= now)
        {
            return new SnoozeResult(Success: false, Reason: "Snooze deadline must be in the future.", Snooze: null);
        }

        // If the caller did not supply an anchor, try to derive it from the current source snapshot.
        DateTime? effectiveAnchor = attentionItemAnchorAtUtc;
        if (effectiveAnchor is null)
        {
            AttentionItemDto? currentItem = await FindItemIgnoringSnoozeAsync(attentionItemId, cancellationToken);
            effectiveAnchor = currentItem?.OccurredAt;
        }

        AttentionSnooze snooze = await _snoozes.UpsertAsync(userId, attentionItemId, snoozedUntilUtc, now, effectiveAnchor, cancellationToken);
        return new SnoozeResult(Success: true, Reason: null, Snooze: snooze);
    }

    /// <inheritdoc />
    public async Task<SnoozeResult> ClearSnoozeAsync(Guid userId, string attentionItemId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(attentionItemId))
        {
            return new SnoozeResult(Success: false, Reason: "Attention item id is required.", Snooze: null);
        }

        bool removed = await _snoozes.RemoveAsync(userId, attentionItemId, cancellationToken);
        return new SnoozeResult(Success: removed, Reason: removed ? null : "No active snooze for this item.", Snooze: null);
    }

    /// <inheritdoc />
    public async Task<AttentionActionResult> ExecuteActionAsync(
        Guid userId,
        string userName,
        bool isFarmAdmin,
        string attentionItemId,
        AttentionActionKind actionKind,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(attentionItemId))
        {
            return new AttentionActionResult(AttentionActionOutcome.NotFound, "Attention item id is required.");
        }

        // Locate the item in the CURRENT feed (bypassing user snoozes so callers can still act on an item they snoozed).
        AttentionItemDto? item = await FindItemIgnoringSnoozeAsync(attentionItemId, cancellationToken);
        if (item is null)
        {
            return new AttentionActionResult(AttentionActionOutcome.NotFound, "Attention item was not found.");
        }

        // Role gate: non-admins cannot even discover or address maintenance items.
        if (item.Kind == AttentionKind.Maintenance && !isFarmAdmin)
        {
            return new AttentionActionResult(AttentionActionOutcome.NotFound, "Attention item was not found.");
        }

        // Validate the action is offered by the item. Sources are the source of truth for
        // "advertised" actions — any action we can execute today must be present here, and
        // any action here must correspond to a real downstream mutation (no no-op 200s).
        if (!item.Actions.Any(a => a.Kind == actionKind))
        {
            return new AttentionActionResult(AttentionActionOutcome.InvalidAction, $"Action '{actionKind}' is not available for this item.");
        }

        // Dispatch. Snooze is handled by the dedicated endpoint; if a client posts here, redirect them.
        if (actionKind == AttentionActionKind.Snooze)
        {
            return new AttentionActionResult(AttentionActionOutcome.InvalidAction, "Use POST /api/attention/{id}/snooze for snoozes.");
        }

        return item.Kind switch
        {
            AttentionKind.Failure => await DispatchFailureAsync(item, actionKind, cancellationToken),
            AttentionKind.Maintenance => await DispatchMaintenanceAsync(item, actionKind, userName, cancellationToken),
            AttentionKind.Offline => new AttentionActionResult(AttentionActionOutcome.InvalidAction, "Offline items expose snooze only."),
            AttentionKind.Harvest => new AttentionActionResult(AttentionActionOutcome.NotImplemented, "Harvest execution lands with F9/#714."),
            AttentionKind.Runout => new AttentionActionResult(AttentionActionOutcome.NotImplemented, "Runout execution lands with F4/#709."),
            _ => new AttentionActionResult(AttentionActionOutcome.Failed, "Unknown attention kind."),
        };
    }

    private async Task<AttentionItemDto?> FindItemIgnoringSnoozeAsync(string attentionItemId, CancellationToken cancellationToken)
    {
        foreach (IAttentionSource source in _sources)
        {
            try
            {
                IReadOnlyList<AttentionItemDto> items = await source.GetItemsAsync(cancellationToken);
                AttentionItemDto? match = items.FirstOrDefault(i => string.Equals(i.Id, attentionItemId, StringComparison.Ordinal));
                if (match is not null)
                {
                    return match;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[AttentionService] Source '{Source}' failed during action lookup", source.SourceName);
            }
        }

        return null;
    }

    private async Task<AttentionActionResult> DispatchFailureAsync(AttentionItemDto item, AttentionActionKind actionKind, CancellationToken ct)
    {
        // Stale-incident + job-identity safety: never issue Pause/Resume/Cancel unless
        // the printer's currently-attached job matches the incident's JobId. Name matches
        // are unsafe — resliced or renamed jobs collide.
        if (item.JobId is not Guid incidentJobId)
        {
            return new AttentionActionResult(
                AttentionActionOutcome.Conflict,
                "Incident is missing a job id; cannot verify the print is still active.");
        }

        PrintJob? job = await _queueData.GetPrintJobByIdAsync(incidentJobId, ct);
        if (job is null || job.AssignedPrinterId != item.PrinterId)
        {
            return new AttentionActionResult(
                AttentionActionOutcome.NotFound,
                "The incident's print job is no longer on this printer.");
        }

        // The incident may have auto-paused the job, so accept Paused in addition to the
        // in-flight states. Anything else (Completed/Cancelled/Failed/etc.) means a newer
        // operator action has already resolved the plate — refuse to mutate.
        bool jobIsActive = job.Status is PrintJobStatus.Starting
            or PrintJobStatus.Printing
            or PrintJobStatus.Paused;
        if (!jobIsActive)
        {
            return new AttentionActionResult(
                AttentionActionOutcome.Conflict,
                $"Print job is no longer active (status: {job.Status}); refusing to mutate printer.");
        }

        try
        {
            bool ok = actionKind switch
            {
                AttentionActionKind.Pause => await _printers.PauseAsync(item.PrinterId, ct),
                AttentionActionKind.Resume => await _printers.ResumeAsync(item.PrinterId, ct),
                AttentionActionKind.Cancel => await _printers.CancelPrintAsync(item.PrinterId, ct),
                _ => false,
            };
            return ok
                ? new AttentionActionResult(AttentionActionOutcome.Ok, null)
                : new AttentionActionResult(AttentionActionOutcome.Failed, "Downstream refused the command.");
        }
        catch (PrinterBackendBusyException ex)
        {
            return new AttentionActionResult(AttentionActionOutcome.Conflict, ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[AttentionService] Failure dispatch for '{Action}' on printer {PrinterId} failed", actionKind, item.PrinterId);
            return new AttentionActionResult(AttentionActionOutcome.Failed, ex.Message);
        }
    }

    private async Task<AttentionActionResult> DispatchMaintenanceAsync(AttentionItemDto item, AttentionActionKind actionKind, string userName, CancellationToken ct)
    {
        // The alert id is embedded in item.Id as "maintenance:{alertId}".
        if (!TryParseSuffixGuid(item.Id, AttentionIdPrefixes.Maintenance, out Guid alertId))
        {
            return new AttentionActionResult(AttentionActionOutcome.Failed, "Malformed maintenance attention id.");
        }

        try
        {
            switch (actionKind)
            {
                case AttentionActionKind.Acknowledge:
                    await _maintenanceAlerts.AcknowledgeAlertAsync(alertId, userName, ct);
                    return new AttentionActionResult(AttentionActionOutcome.Ok, null);
                case AttentionActionKind.Resolve:
                    await _maintenanceAlerts.ResolveAlertAsync(alertId, userName, ct);
                    return new AttentionActionResult(AttentionActionOutcome.Ok, null);
                case AttentionActionKind.Dismiss:
                    await _maintenanceAlerts.DismissAlertAsync(alertId, userName, dismissReason: null, ct);
                    return new AttentionActionResult(AttentionActionOutcome.Ok, null);
                default:
                    return new AttentionActionResult(AttentionActionOutcome.InvalidAction, $"Action '{actionKind}' is not valid for maintenance items.");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[AttentionService] Maintenance dispatch for '{Action}' on alert {AlertId} failed", actionKind, alertId);
            return new AttentionActionResult(AttentionActionOutcome.Failed, ex.Message);
        }
    }

    private static bool TryParseSuffixGuid(string attentionId, string expectedPrefix, out Guid value)
    {
        value = Guid.Empty;
        if (string.IsNullOrEmpty(attentionId))
        {
            return false;
        }

        int colon = attentionId.IndexOf(':', StringComparison.Ordinal);
        if (colon <= 0)
        {
            return false;
        }

        ReadOnlySpan<char> prefix = attentionId.AsSpan(0, colon);
        if (!prefix.SequenceEqual(expectedPrefix.AsSpan()))
        {
            return false;
        }

        ReadOnlySpan<char> suffix = attentionId.AsSpan(colon + 1);
        return Guid.TryParse(suffix, out value);
    }
}
