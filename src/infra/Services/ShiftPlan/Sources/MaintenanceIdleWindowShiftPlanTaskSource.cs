using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Maintenance;
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
    private readonly ILogger<MaintenanceIdleWindowShiftPlanTaskSource> _logger;

    public MaintenanceIdleWindowShiftPlanTaskSource(
        IMaintenanceAlertRepository alerts,
        IIdleWindowService idleWindows,
        ISettingsService settings,
        ILogger<MaintenanceIdleWindowShiftPlanTaskSource> logger)
    {
        _alerts = alerts;
        _idleWindows = idleWindows;
        _settings = settings;
        _logger = logger;
    }

    public string SourceName => "maintenance-idle-window";

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

        List<MaintenanceAlert> active;
        try
        {
            active = await _alerts.GetAllActiveAlertsAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Maintenance alert repository failed in shift-plan compile");
            return Array.Empty<ShiftPlanTaskSpec>();
        }

        if (active.Count == 0)
        {
            return Array.Empty<ShiftPlanTaskSpec>();
        }

        IReadOnlyList<IdleWindow> windows = await _idleWindows
            .GetIdleWindowsAsync(minWindow, ct)
            .ConfigureAwait(false);

        Dictionary<Guid, IdleWindow> byPrinter = windows.ToDictionary(w => w.PrinterId);

        List<ShiftPlanTaskSpec> specs = new(active.Count);
        foreach (MaintenanceAlert alert in active)
        {
            ct.ThrowIfCancellationRequested();

            if (!byPrinter.TryGetValue(alert.PrinterId, out IdleWindow? window))
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
