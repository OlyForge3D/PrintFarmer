using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Dtos.Attention;
using Farm.Infrastructure.Services.Attention;
using Farm.Infrastructure.Settings;
using Microsoft.Extensions.Logging;

namespace Farm.Infrastructure.Services.ShiftPlan.Sources;

/// <summary>
/// Bridges the existing <see cref="IAttentionSource"/> seam into shift-plan
/// task specs. Attention items that map to actionable operator work
/// (Failure, Harvest, Runout) become anchor-typed specs; Offline is not
/// materialized as a task (it's a printer-state signal, not a shift task),
/// and Maintenance items are handled by
/// <see cref="MaintenanceIdleWindowShiftPlanTaskSource"/> which pairs alerts
/// with idle windows instead of anchoring to "now".
/// </summary>
public sealed class AttentionShiftPlanTaskSource : IShiftPlanTaskSource
{
    private readonly IEnumerable<IAttentionSource> _attentionSources;
    private readonly ISettingsService _settings;
    private readonly ILogger<AttentionShiftPlanTaskSource> _logger;

    public AttentionShiftPlanTaskSource(
        IEnumerable<IAttentionSource> attentionSources,
        ISettingsService settings,
        ILogger<AttentionShiftPlanTaskSource> logger)
    {
        _attentionSources = attentionSources;
        _settings = settings;
        _logger = logger;
    }

    public string SourceName => "attention";

    /// <inheritdoc/>
    public IReadOnlyCollection<UserTaskSourceKind> OwnedKinds { get; } =
    [
        UserTaskSourceKind.FailureIncident,
        UserTaskSourceKind.Harvest,
        UserTaskSourceKind.FilamentCoverage,
    ];

    public async Task<IReadOnlyList<ShiftPlanTaskSpec>> ProduceAsync(CancellationToken ct)
    {
        List<ShiftPlanTaskSpec> results = new();
        SpoolCoverageSettings spool;
        try
        {
            spool = _settings.Get<SpoolCoverageSettings>() ?? new SpoolCoverageSettings();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "SpoolCoverageSettings unavailable; using defaults");
            spool = new SpoolCoverageSettings();
        }

        int runoutLeadMinutes = Math.Max(0, spool.RunoutWarningLeadMinutes);

        foreach (IAttentionSource src in _attentionSources)
        {
            ct.ThrowIfCancellationRequested();

            // Fix 4: do NOT catch per-inner-source exceptions — let them propagate
            // so the compiler can suppress auto-complete for this source's OwnedKinds.
            IReadOnlyList<AttentionItemDto> items = await src.GetItemsAsync(ct).ConfigureAwait(false);

            foreach (AttentionItemDto item in items)
            {
                ShiftPlanTaskSpec? spec = MapItem(item, runoutLeadMinutes);
                if (spec is not null)
                {
                    results.Add(spec);
                }
            }
        }

        return results;
    }

    private static ShiftPlanTaskSpec? MapItem(AttentionItemDto item, int runoutLeadMinutes)
    {
        UserTaskPriority priority = item.Severity switch
        {
            AttentionSeverity.Critical => UserTaskPriority.High,
            AttentionSeverity.Warning => UserTaskPriority.Normal,
            _ => UserTaskPriority.Low,
        };

        return item.Kind switch
        {
            AttentionKind.Failure => new ShiftPlanTaskSpec(
                TaskType: UserTaskType.FailureClear,
                SourceKind: UserTaskSourceKind.FailureIncident,
                SourceId: item.Id,
                Title: item.Title,
                Description: item.Detail,
                Priority: priority,
                AnchorKind: UserTaskAnchorKind.Now,
                AnchorAtUtc: item.OccurredAt,
                WindowStartUtc: null,
                WindowEndUtc: null,
                EntityType: "Printer",
                EntityId: item.PrinterId,
                DueAt: null),

            AttentionKind.Harvest => new ShiftPlanTaskSpec(
                TaskType: UserTaskType.HarvestReady,
                SourceKind: UserTaskSourceKind.Harvest,
                SourceId: item.Id,
                Title: item.Title,
                Description: item.Detail,
                Priority: priority,
                AnchorKind: UserTaskAnchorKind.Now,
                AnchorAtUtc: item.OccurredAt,
                WindowStartUtc: null,
                WindowEndUtc: null,
                EntityType: "Printer",
                EntityId: item.PrinterId,
                DueAt: null),

            AttentionKind.Runout => BuildRunoutSpec(item, priority, runoutLeadMinutes),

            _ => null, // Maintenance handled elsewhere; Offline is not a task.
        };
    }

    private static ShiftPlanTaskSpec BuildRunoutSpec(
        AttentionItemDto item, UserTaskPriority priority, int leadMinutes)
    {
        DateTime? anchor = null;
        UserTaskAnchorKind kind = UserTaskAnchorKind.Now;
        DateTime? due = item.DeadlineAt;

        if (item.DeadlineAt is DateTime deadline)
        {
            DateTime candidate = deadline.AddMinutes(-leadMinutes);
            if (candidate > DateTime.UtcNow)
            {
                anchor = candidate;
                kind = UserTaskAnchorKind.At;
            }
        }

        return new ShiftPlanTaskSpec(
            TaskType: UserTaskType.FilamentRunout,
            SourceKind: UserTaskSourceKind.FilamentCoverage,
            SourceId: item.Id,
            Title: item.Title,
            Description: item.Detail,
            Priority: priority,
            AnchorKind: kind,
            AnchorAtUtc: anchor,
            WindowStartUtc: null,
            WindowEndUtc: null,
            EntityType: "Printer",
            EntityId: item.PrinterId,
            DueAt: due);
    }
}
