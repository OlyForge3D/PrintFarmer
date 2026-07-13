using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Maintenance;
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

    public MaintenanceIdleWindowShiftPlanTaskSource(
        IMaintenanceAlertRepository alerts,
        IIdleWindowService idleWindows,
        ISettingsService settings,
        IOperatorFeatureGate featureGate,
        ILogger<MaintenanceIdleWindowShiftPlanTaskSource> logger)
    {
        _alerts = alerts;
        _idleWindows = idleWindows;
        _settings = settings;
        _featureGate = featureGate;
        _logger = logger;
    }

    public string SourceName => "maintenance-idle-window";

    /// <inheritdoc/>
    public IReadOnlyCollection<UserTaskSourceKind> OwnedKinds { get; } =
        [UserTaskSourceKind.Maintenance];

    public async Task<IReadOnlyList<ShiftPlanTaskSpec>> ProduceAsync(CancellationToken ct)
    {
        ShiftPlanSettings settings;
        try
        {
            settings = _settings.Get<ShiftPlanSettings>() ?? new ShiftPlanSettings();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "ShiftPlanSettings unavailable; using defaults");
            settings = new ShiftPlanSettings();
        }

        TimeSpan minWindow = TimeSpan.FromMinutes(Math.Max(1, settings.MinIdleWindowMinutes));
        TimeSpan lead = TimeSpan.FromMinutes(Math.Max(0, settings.MaintenanceLeadMinutes));

        // Fix A: fail closed. Let a repository failure propagate — the compiler's
        // per-source isolation (ShiftPlanCompiler.CompileAsync) catches it, increments
        // its failure counter, and crucially does NOT add Maintenance to the successful
        // kinds, so open maintenance tasks are preserved instead of mass auto-completed.
        // Swallowing the exception here would masquerade a repo outage as "no active
        // alerts" and auto-complete every open maintenance task (IShiftPlanTaskSource
        // contract, "fail closed").
        List<MaintenanceAlert> active = await _alerts.GetAllActiveAlertsAsync(ct).ConfigureAwait(false);

        if (active.Count == 0)
        {
            return Array.Empty<ShiftPlanTaskSpec>();
        }

        // Finding H5 (issue #711): when the multi-slot fallback feature is off,
        // per-toolhead maintenance must not leak into the shift plan. Drop any
        // alert scoped to a specific toolhead so only printer-wide maintenance is
        // projected. Printer-wide alerts (ToolheadId == null) always flow through.
        bool perToolEnabled = _featureGate.IsEnabled(OperatorFeature.MultiSlotFallback);
        if (!perToolEnabled)
        {
            active = active.Where(a => !a.ToolheadId.HasValue).ToList();
            if (active.Count == 0)
            {
                return Array.Empty<ShiftPlanTaskSpec>();
            }
        }

        IdleWindowResult idleResult = await _idleWindows
            .GetIdleWindowsWithIndeterminateAsync(minWindow, ct)
            .ConfigureAwait(false);

        // Fix R4-1: fail closed when dispatch eligibility is indeterminate for a
        // printer that has an active maintenance alert. A scorer outage makes
        // IdleWindowService exclude that printer from the window set; if we returned
        // successfully with the printer merely absent, the compiler would treat
        // Maintenance as a successful (but now spec-less) source and auto-complete the
        // still-active maintenance task — then recreate a duplicate once scoring
        // recovers (task flapping, lost InProgress state). Throwing routes through the
        // compiler's per-source isolation, which preserves existing maintenance tasks
        // for this pass instead of sweeping them into auto-complete.
        if (idleResult.IndeterminatePrinterIds.Count > 0
            && active.Any(a => idleResult.IndeterminatePrinterIds.Contains(a.PrinterId)))
        {
            throw new InvalidOperationException(
                "Dispatch eligibility indeterminate for maintenance-alerted printer; failing closed to preserve tasks.");
        }

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

        return specs;
    }
}
