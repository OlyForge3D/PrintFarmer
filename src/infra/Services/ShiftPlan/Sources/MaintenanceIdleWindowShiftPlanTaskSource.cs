using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Maintenance;
using Farm.Infrastructure.Services.Mutations;
using Farm.Infrastructure.Services.OperatorFeatures;
using Farm.Infrastructure.Settings;
using Microsoft.Extensions.Logging;

namespace Farm.Infrastructure.Services.ShiftPlan.Sources;

/// <summary>
/// Materializes active <see cref="MaintenanceAlert"/> rows into shift-plan
/// tasks anchored to the corresponding printer's next idle window. Alerts on
/// a printer that has an eligible unassigned dispatchable job are skipped so
/// the compiler never fights the dispatcher.
/// </summary>
public sealed class MaintenanceIdleWindowShiftPlanTaskSource : IShiftPlanTaskSource
{
    private readonly IMaintenanceAlertRepository _alerts;
    private readonly IIdleWindowService _idleWindows;
    private readonly ISettingsService _settings;
    private readonly IOperatorFeatureGate _featureGate;
    private readonly ILogger<MaintenanceIdleWindowShiftPlanTaskSource> _logger;
    private readonly IMutationWatermarkReader? _watermarkReader;

    public MaintenanceIdleWindowShiftPlanTaskSource(
        IMaintenanceAlertRepository alerts,
        IIdleWindowService idleWindows,
        ISettingsService settings,
        IOperatorFeatureGate featureGate,
        ILogger<MaintenanceIdleWindowShiftPlanTaskSource> logger,
        IMutationWatermarkReader? watermarkReader = null)
    {
        _alerts = alerts;
        _idleWindows = idleWindows;
        _settings = settings;
        _featureGate = featureGate;
        _logger = logger;
        _watermarkReader = watermarkReader;
    }

    public string SourceName => "maintenance-idle-window";

    /// <inheritdoc/>
    public IReadOnlyCollection<UserTaskSourceKind> OwnedKinds { get; } =
        [UserTaskSourceKind.Maintenance];

    public async Task<ShiftPlanSourceResult> ProduceAsync(CancellationToken ct)
    {
        long? rootOrigin = await OriginWatermark
            .CaptureAsync(_watermarkReader, _logger, "maintenance shift-plan inputs", ct)
            .ConfigureAwait(false);
        SettingsSnapshot<ShiftPlanSettings> settingsSnapshot;
        try
        {
            settingsSnapshot = _settings.GetSnapshot<ShiftPlanSettings>()
                ?? new SettingsSnapshot<ShiftPlanSettings>(
                    _settings.Get<ShiftPlanSettings>(),
                    OriginWatermark: null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "ShiftPlanSettings unavailable; using defaults");
            settingsSnapshot = new SettingsSnapshot<ShiftPlanSettings>(
                new ShiftPlanSettings(),
                OriginWatermark: null);
        }

        ShiftPlanSettings settings = settingsSnapshot.Value;
        TimeSpan minWindow = TimeSpan.FromMinutes(Math.Max(1, settings.MinIdleWindowMinutes));
        TimeSpan lead = TimeSpan.FromMinutes(Math.Max(0, settings.MaintenanceLeadMinutes));

        // Let repository failures propagate so the compiler records the source failure
        // and receives no authoritative absence evidence for Maintenance.
        List<MaintenanceAlert> active = await _alerts.GetAllActiveAlertsAsync(ct).ConfigureAwait(false);

        if (active.Count == 0)
        {
            return BuildResult(
                [],
                new HashSet<string>(StringComparer.Ordinal),
                rootOrigin,
                settingsSnapshot.OriginWatermark);
        }

        // Finding H5 (issue #711): when the multi-slot fallback feature is off,
        // per-toolhead maintenance must not leak into the shift plan. Drop any
        // alert scoped to a specific toolhead so only printer-wide maintenance is
        // projected. Printer-wide alerts (ToolheadId == null) always flow through.
        bool perToolEnabled = await _featureGate
            .IsEnabledStrictAsync(OperatorFeature.MultiSlotFallback, ct)
            .ConfigureAwait(false);
        if (!perToolEnabled)
        {
            active = active.Where(a => !a.ToolheadId.HasValue).ToList();
        }

        bool perToolEnabledAfterFilter = await _featureGate
            .IsEnabledStrictAsync(OperatorFeature.MultiSlotFallback, ct)
            .ConfigureAwait(false);
        if (perToolEnabledAfterFilter != perToolEnabled)
        {
            throw new InvalidOperationException(
                "Multi-slot fallback feature changed during maintenance observation.");
        }

        if (active.Count == 0)
        {
            return BuildResult(
                [],
                new HashSet<string>(StringComparer.Ordinal),
                rootOrigin,
                settingsSnapshot.OriginWatermark);
        }

        IdleWindowResult idleResult = await _idleWindows
            .GetIdleWindowsWithIndeterminateAsync(minWindow, ct)
            .ConfigureAwait(false);

        HashSet<string> preservedSourceIds =
        [
            .. active
                .Where(alert => idleResult.IndeterminatePrinterIds.Contains(alert.PrinterId))
                .Select(alert => $"maintenancealert:{alert.Id}"),
        ];

        Dictionary<Guid, IdleWindow> byPrinter = idleResult.Windows.ToDictionary(w => w.PrinterId);

        List<ShiftPlanTaskSpec> specs = new(active.Count);
        foreach (MaintenanceAlert alert in active)
        {
            ct.ThrowIfCancellationRequested();

            if (!byPrinter.TryGetValue(alert.PrinterId, out IdleWindow window))
            {
                // No idle window on this printer right now — skip. When the queue
                // drains, a subsequent compile will pick it up.
                continue;
            }

            if (window.IsDispatchEligibleNow)
            {
                // Dispatcher would place a job here; do not compete for the slot.
                continue;
            }

            // Effective window is bounded by the maintenance lead-time buffer so
            // operators are not scheduled to start work in the last few minutes
            // before the next print begins.
            DateTime windowStart = window.StartUtc;
            DateTime windowEnd = window.EndUtc == DateTime.MaxValue
                ? DateTime.MaxValue
                : window.EndUtc - lead;
            if (windowEnd != DateTime.MaxValue && windowEnd - windowStart < minWindow)
            {
                continue;
            }

            UserTaskPriority priority = alert.Severity switch
            {
                >= 3 => UserTaskPriority.High,
                2 => UserTaskPriority.Normal,
                _ => UserTaskPriority.Low,
            };

            specs.Add(new ShiftPlanTaskSpec(
                TaskType: UserTaskType.MaintenanceInIdleWindow,
                SourceKind: UserTaskSourceKind.Maintenance,
                SourceId: $"maintenancealert:{alert.Id}",
                Title: alert.Title,
                Description: alert.Message,
                Priority: priority,
                AnchorKind: UserTaskAnchorKind.Window,
                AnchorAtUtc: null,
                WindowStartUtc: windowStart,
                WindowEndUtc: windowEnd == DateTime.MaxValue ? null : windowEnd,
                EntityType: "Printer",
                EntityId: alert.PrinterId,
                DueAt: null));
        }

        return BuildResult(
            specs,
            preservedSourceIds,
            rootOrigin,
            settingsSnapshot.OriginWatermark,
            idleResult.OriginWatermark);
    }

    private static ShiftPlanSourceResult BuildResult(
        IReadOnlyList<ShiftPlanTaskSpec> specs,
        IReadOnlySet<string> preservedSourceIds,
        params long?[] origins)
    {
        long? originWatermark = OriginWatermark.Combine(origins);
        bool isComplete = originWatermark is not null;
        return new ShiftPlanSourceResult(specs, originWatermark)
        {
            Authority = new ShiftPlanSourceAuthority(
            [
                new ShiftPlanKindAuthority(
                    UserTaskSourceKind.Maintenance,
                    isComplete,
                    preservedSourceIds,
                    isComplete ? [] : ["maintenance-origin-unproven"]),
            ]),
        };
    }
}
