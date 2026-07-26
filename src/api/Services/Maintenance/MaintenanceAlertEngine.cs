using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Dtos.Attention;
using Farm.Infrastructure.Repositories.Maintenance;
using Farm.Infrastructure.Services.Attention;
using Farm.Infrastructure.Services.Maintenance;
using Farm.Infrastructure.Services.OperatorFeatures;
using Farm.Infrastructure.Settings;
using Farm.Web.Api.Hubs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Farm.Web.Api.Services.Maintenance;

/// <summary>
/// Service that evaluates V3 maintenance deployments (PrinterMaintenanceSchedule → Plan → PlanTask → Task)
/// against printer statistics and generates alerts when maintenance is due.
/// </summary>
public class MaintenanceAlertEngine(
    IPrinterStatisticsRepository statsRepo,
    IPrinterMaintenanceScheduleRepository deploymentRepo,
    IMaintenanceAlertRepository alertRepo,
    IMaintenanceLogRepository logRepo,
    IHubContext<MaintenanceHub> hubContext,
    IOptionsMonitor<MaintenanceAlertSettings> settingsMonitor,
    ILogger<MaintenanceAlertEngine> logger,
    IAttentionBroadcaster? attentionBroadcaster = null,
    IToolheadStatisticsRepository? toolheadStatsRepo = null,
    IOperatorFeatureGate? operatorFeatureGate = null) : IMaintenanceAlertService
{
    private readonly IPrinterStatisticsRepository _statsRepo = statsRepo ?? throw new ArgumentNullException(nameof(statsRepo));
    private readonly IPrinterMaintenanceScheduleRepository _deploymentRepo = deploymentRepo ?? throw new ArgumentNullException(nameof(deploymentRepo));
    private readonly IMaintenanceAlertRepository _alertRepo = alertRepo ?? throw new ArgumentNullException(nameof(alertRepo));
    private readonly IMaintenanceLogRepository _logRepo = logRepo ?? throw new ArgumentNullException(nameof(logRepo));
    private readonly IHubContext<MaintenanceHub> _hubContext = hubContext ?? throw new ArgumentNullException(nameof(hubContext));
    private readonly IOptionsMonitor<MaintenanceAlertSettings> _settingsMonitor = settingsMonitor ?? throw new ArgumentNullException(nameof(settingsMonitor));
    private readonly ILogger<MaintenanceAlertEngine> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    // Attention feed invalidation (issue #707). Optional to preserve existing test constructors.
    private readonly IAttentionBroadcaster? _attentionBroadcaster = attentionBroadcaster;

    // Per-toolhead cumulative hours for per-tool schedule accrual (issue #711, FIX B).
    // Optional to preserve existing test constructors; when null, per-tool schedules fall
    // back to printer-wide hours (previous behavior).
    private readonly IToolheadStatisticsRepository? _toolheadStatsRepo = toolheadStatsRepo;

    // Per-tool maintenance feature gate (issue #711, round-5 FIX 2). Optional to preserve
    // existing test constructors; when null the gate is treated as enabled (previous behavior),
    // matching DispatchScorer. When wired and disabled, toolhead-scoped deployments do not
    // generate new per-tool alerts; printer-wide deployments continue normally.
    private readonly IOperatorFeatureGate? _operatorFeatureGate = operatorFeatureGate;

    public async Task<int> EvaluatePrinterMaintenanceAsync(
        Guid printerId,
        CancellationToken cancellationToken = default)
    {
        MaintenanceAlertSettings settings = _settingsMonitor.CurrentValue;

        _logger.LogDebug("Evaluating maintenance for printer {PrinterId}", printerId);

        // Get printer statistics
        PrinterStatistics? stats = await _statsRepo.GetByPrinterIdAsync(printerId, cancellationToken);
        if (stats == null)
        {
            _logger.LogDebug("No statistics found for printer {PrinterId}, skipping evaluation", printerId);
            return 0;
        }

        // Get active V3 deployments with deep-loaded PlanTasks → MaintenanceTasks
        List<PrinterMaintenanceSchedule> deployments = await _deploymentRepo.GetActiveWithTasksAsync(
            printerId,
            cancellationToken);

        if (deployments.Count == 0)
        {
            _logger.LogDebug("No active deployments found for printer {PrinterId}", printerId);
            return 0;
        }

        // Load maintenance logs once so we can compute baselines efficiently.
        // Group by (MaintenanceTaskId, ToolheadId) so per-toolhead-scoped schedules accrue
        // their intervals independently from printer-wide schedules and from each other
        // (issue #711, F6). A printer-wide deployment (null toolhead) only consumes logs that
        // are themselves printer-wide.
        List<MaintenanceLog> logs = await _logRepo.GetByPrinterIdAsync(printerId, cancellationToken);
        Dictionary<(Guid TaskId, Guid? ToolheadId), MaintenanceLog> lastLogByTaskAndToolhead = logs
            .Where(l => l.MaintenanceTaskId.HasValue)
            .GroupBy(l => (l.MaintenanceTaskId!.Value, l.ToolheadId))
            .ToDictionary(
                g => g.Key,
                g => g.Aggregate((latest, current) => current.PerformedAt > latest.PerformedAt ? current : latest));

        // Per-toolhead cumulative hours so per-tool schedules accrue against their own
        // toolhead, not the printer-wide counter (issue #711, FIX B). Empty when the optional
        // repository is not wired (test constructors) → per-tool schedules fall back to
        // printer-wide hours.
        IReadOnlyDictionary<Guid, double> toolheadHours = _toolheadStatsRepo is not null
            ? await _toolheadStatsRepo.GetCumulativeHoursByPrinterAsync(printerId, cancellationToken)
            : EmptyToolheadHours;

        int alertsGenerated = 0;
        List<MaintenanceAlert> createdAlerts = new();

        // When the per-tool maintenance feature is disabled, toolhead-scoped deployments must not
        // generate new per-tool alerts (issue #711, round-5 FIX 2). Printer-wide deployments
        // (null toolhead) are unaffected. A null gate (test constructors) is treated as enabled.
        bool perToolMaintenanceEnabled = _operatorFeatureGate is null
            || await _operatorFeatureGate.IsEnabledAsync(OperatorFeature.MultiSlotFallback, cancellationToken).ConfigureAwait(false);

        // Evaluate each deployment → plan → tasks
        foreach (PrinterMaintenanceSchedule deployment in deployments)
        {
            if (deployment.MaintenancePlan?.PlanTasks == null)
            {
                continue;
            }

            if (deployment.ToolheadId.HasValue && !perToolMaintenanceEnabled)
            {
                _logger.LogDebug(
                    "Skipping per-tool deployment {DeploymentId} on printer {PrinterId}: MultiSlotFallback disabled",
                    deployment.Id,
                    printerId);
                continue;
            }

            foreach (PlanTask planTask in deployment.MaintenancePlan.PlanTasks)
            {
                MaintenanceTask task = planTask.MaintenanceTask;
                if (task == null || !task.IsActive)
                {
                    continue;
                }

                // Effective intervals: PlanTask overrides take precedence over base task
                double? effectiveHours = planTask.IntervalHoursOverride ?? task.IntervalHours;
                int? effectiveDays = planTask.IntervalDaysOverride ?? task.IntervalDays;

                if (!effectiveHours.HasValue && !effectiveDays.HasValue)
                {
                    continue;
                }

                lastLogByTaskAndToolhead.TryGetValue((task.Id, deployment.ToolheadId), out MaintenanceLog? lastLog);
                bool shouldAlert = ShouldGenerateAlert(stats, task.TaskName, effectiveHours, effectiveDays, lastLog, deployment.DeployedAt, settings, deployment.ToolheadId, toolheadHours);

                if (shouldAlert)
                {
                    // Check if alert already exists (dedup by printer + task + deployment)
                    bool hasActiveAlert = await _alertRepo.HasActiveAlertAsync(
                        printerId,
                        task.Id,
                        deployment.Id,
                        cancellationToken);

                    if (!hasActiveAlert)
                    {
                        MaintenanceAlert created = await GenerateAlertAsync(stats, deployment, task, effectiveHours, effectiveDays, lastLog, toolheadHours, cancellationToken);
                        createdAlerts.Add(created);
                        alertsGenerated++;
                    }
                    else
                    {
                        _logger.LogDebug(
                            "Active alert already exists for printer {PrinterId} and task {TaskId}",
                            printerId,
                            task.Id);
                    }
                }
            }
        }

        if (alertsGenerated > 0)
        {
            await _alertRepo.SaveChangesAsync(cancellationToken);
            _logger.LogInformation(
                "Generated {Count} maintenance alerts for printer {PrinterId}",
                alertsGenerated,
                printerId);

            // Broadcast only after the commit succeeds (issue #707, review R3). The legacy
            // MaintenanceHub notification honours EnableSignalRNotifications, but the attention
            // feed invalidation is independent of that toggle.
            foreach (MaintenanceAlert created in createdAlerts)
            {
                await BroadcastAlertCreatedAsync(created);
            }
        }

        return alertsGenerated;
    }

    private bool ShouldGenerateAlert(
        PrinterStatistics stats,
        string taskName,
        double? intervalHours,
        int? intervalDays,
        MaintenanceLog? lastLog,
        DateTime deployedAt,
        MaintenanceAlertSettings settings,
        Guid? toolheadId,
        IReadOnlyDictionary<Guid, double> toolheadHours)
    {
        // Check hour-based interval
        if (intervalHours.HasValue)
        {
            double thresholdHours = intervalHours.Value * (settings.ThresholdPercentage / 100.0);

            double hoursSinceLast = ComputeHoursSinceLastMaintenance(stats, lastLog, toolheadId, toolheadHours);

            if (hoursSinceLast >= thresholdHours)
            {
                _logger.LogDebug(
                    "Task '{TaskName}' triggered: {Hours}h >= {Threshold}h (interval: {Interval}h)",
                    taskName,
                    hoursSinceLast,
                    thresholdHours,
                    intervalHours.Value);
                return true;
            }
        }

        // Check day-based interval
        if (intervalDays.HasValue)
        {
            DateTime baselineDate = lastLog?.PerformedAt ?? deployedAt;
            int daysSinceBaseline = (DateTime.UtcNow - baselineDate).Days;
            int thresholdDays = (int)(intervalDays.Value * (settings.ThresholdPercentage / 100.0));

            if (daysSinceBaseline >= thresholdDays)
            {
                _logger.LogDebug(
                    "Task '{TaskName}' triggered: {Days} days >= {Threshold} days (interval: {Interval} days)",
                    taskName,
                    daysSinceBaseline,
                    thresholdDays,
                    intervalDays.Value);
                return true;
            }
        }

        return false;
    }

    private async Task<MaintenanceAlert> GenerateAlertAsync(
        PrinterStatistics stats,
        PrinterMaintenanceSchedule deployment,
        MaintenanceTask task,
        double? effectiveHours,
        int? effectiveDays,
        MaintenanceLog? lastLog,
        IReadOnlyDictionary<Guid, double> toolheadHours,
        CancellationToken cancellationToken)
    {
        double? hoursSinceLast = effectiveHours.HasValue
            ? ComputeHoursSinceLastMaintenance(stats, lastLog, deployment.ToolheadId, toolheadHours)
            : null;

        int? daysSinceLast = effectiveDays.HasValue
            ? (DateTime.UtcNow - (lastLog?.PerformedAt ?? deployment.DeployedAt)).Days
            : null;

        // Create alert referencing both the deployment and the specific task. The alert
        // inherits the deployment's optional toolhead scope so per-tool alerts stay
        // independent and resolution logs can preserve that scope (issue #711, F6).
        MaintenanceAlert alert = new()
        {
            Id = Guid.NewGuid(),
            PrinterId = stats.PrinterId,
            PrinterMaintenanceScheduleId = deployment.Id,
            MaintenanceTaskId = task.Id,
            ToolheadId = deployment.ToolheadId,
            Title = $"Maintenance Due: {task.TaskName}",
            Message = BuildAlertMessage(stats, task.TaskName, task.Description, effectiveHours, effectiveDays, hoursSinceLast, daysSinceLast),
            Severity = task.Priority,
            Status = MaintenanceAlertStatus.Active,
            PrinterHoursAtTrigger = stats.TotalPrintHours,
            HoursSinceLastMaintenance = hoursSinceLast,
            DaysSinceLastMaintenance = daysSinceLast
        };

        await _alertRepo.AddAsync(alert, cancellationToken);

        _logger.LogInformation(
            "Created maintenance alert for printer {PrinterId}: {Title}",
            stats.PrinterId,
            alert.Title);

        // Broadcasts are deferred until AFTER the batch SaveChangesAsync succeeds (issue
        // #707, review R3) so the attention feed is never invalidated for an alert that was
        // never committed.
        return alert;
    }

    private static string BuildAlertMessage(
        PrinterStatistics stats,
        string taskName,
        string? description,
        double? intervalHours,
        int? intervalDays,
        double? hoursSinceLastMaintenance,
        int? daysSinceLastMaintenance)
    {
        string fallbackMessage = description ?? "Please perform scheduled maintenance.";

        if (intervalHours.HasValue && !intervalDays.HasValue)
        {
            return $"{taskName} is due for printer. " +
                   $"Hours since last maintenance: {hoursSinceLastMaintenance ?? stats.TotalPrintHours:F1}h, " +
                   $"Interval: {intervalHours:F1}h. " +
                   fallbackMessage;
        }

        if (intervalDays.HasValue && !intervalHours.HasValue)
        {
            return $"{taskName} is due for printer. " +
                   $"Days since last maintenance: {daysSinceLastMaintenance ?? 0}, " +
                   $"Interval: {intervalDays} days. " +
                   fallbackMessage;
        }

        if (intervalHours.HasValue && intervalDays.HasValue)
        {
            return $"{taskName} is due for printer. " +
                   $"Hours since last maintenance: {hoursSinceLastMaintenance ?? stats.TotalPrintHours:F1}h (interval: {intervalHours:F1}h), " +
                   $"Days since last maintenance: {daysSinceLastMaintenance ?? 0} (interval: {intervalDays} days). " +
                   fallbackMessage;
        }

        return $"{taskName} is due. {fallbackMessage}";
    }

    private static readonly IReadOnlyDictionary<Guid, double> EmptyToolheadHours = new Dictionary<Guid, double>();

    private static double ComputeHoursSinceLastMaintenance(
        PrinterStatistics stats,
        MaintenanceLog? lastLog,
        Guid? toolheadId,
        IReadOnlyDictionary<Guid, double> toolheadHours)
    {
        // Per-toolhead scope (issue #711, FIX B): when the schedule targets a specific toolhead
        // and per-toolhead hours are available, accrue against that toolhead's cumulative hours
        // using the log's captured per-toolhead baseline. Per-tool tracking starts at 0 at
        // migration time, so a schedule with no prior per-tool log measures from when tracking
        // began (baseline 0).
        if (toolheadId.HasValue && toolheadHours.TryGetValue(toolheadId.Value, out double currentToolheadHours))
        {
            double toolheadBaseline = lastLog?.ToolheadHoursAtMaintenance ?? 0;
            return Math.Max(0, currentToolheadHours - toolheadBaseline);
        }

        // Printer-wide scope (or no per-toolhead data): preferred baseline is the printer's
        // total hours at the last maintenance log. If historical logs don't contain printer
        // hours yet, fall back to total hours (maintains previous behavior until new logs
        // populate PrinterHoursAtMaintenance).
        if (lastLog?.PrinterHoursAtMaintenance is double baselineHours)
        {
            return Math.Max(0, stats.TotalPrintHours - baselineHours);
        }

        return stats.TotalPrintHours;
    }

    private async Task BroadcastAlertCreatedAsync(MaintenanceAlert alert)
    {
        MaintenanceAlertSettings settings = _settingsMonitor.CurrentValue;

        // SignalR notification remains gated on the operator toggle.
        if (settings.EnableSignalRNotifications)
        {
            try
            {
                await _hubContext.Clients.Group(Farm.Infrastructure.Security.AuthorizedHubGroups.Farm).SendAsync(
                    MaintenanceHubEvents.AlertCreated,
                    new
                    {
                        id = alert.Id,
                        printerId = alert.PrinterId,
                        title = alert.Title,
                        message = alert.Message,
                        severity = alert.Severity,
                        createdAt = alert.CreatedAt
                    });

                _logger.LogDebug("Broadcast maintenance alert {AlertId} via SignalR", alert.Id);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to broadcast maintenance alert {AlertId} via SignalR", alert.Id);
            }
        }

        // Invalidate the unified attention feed (issue #707). This is INDEPENDENT of the
        // legacy MaintenanceAlertSettings.EnableSignalRNotifications toggle (review R3) — the
        // attention feed must reflect committed maintenance state regardless of that setting.
        if (_attentionBroadcaster is not null)
        {
            await _attentionBroadcaster.NotifyChangedAsync(new AttentionChangedPayload(
                AttentionIdPrefixes.Build(AttentionIdPrefixes.Maintenance, alert.Id),
                AttentionChangeKind.Created,
                alert.CreatedAt));
        }
    }

    public async Task AcknowledgeAlertAsync(
        Guid alertId,
        string acknowledgedBy,
        CancellationToken cancellationToken = default)
    {
        MaintenanceAlert? alert = await _alertRepo.GetByIdAsync(alertId, cancellationToken);
        if (alert == null)
        {
            _logger.LogWarning("Alert {AlertId} not found for acknowledgment", alertId);
            return;
        }

        EnsureAlertMutationEnabled(alert);

        alert.Status = MaintenanceAlertStatus.Acknowledged;
        alert.AcknowledgedAt = DateTime.UtcNow;
        alert.AcknowledgedBy = acknowledgedBy;

        await _alertRepo.UpdateAsync(alert, cancellationToken);
        await _alertRepo.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Alert {AlertId} acknowledged by {User}",
            alertId,
            acknowledgedBy);

        // Broadcast status change
        await BroadcastAlertStatusChangedAsync(alert);
    }

    public async Task ResolveAlertAsync(
        Guid alertId,
        string resolvedBy,
        CancellationToken cancellationToken = default)
    {
        MaintenanceAlert? alert = await _alertRepo.GetByIdAsync(alertId, cancellationToken);
        if (alert == null)
        {
            _logger.LogWarning("Alert {AlertId} not found for resolution", alertId);
            return;
        }

        EnsureAlertMutationEnabled(alert);

        alert.Status = MaintenanceAlertStatus.Resolved;
        alert.ResolvedAt = DateTime.UtcNow;
        alert.ResolvedBy = resolvedBy;

        await _alertRepo.UpdateAsync(alert, cancellationToken);
        await _alertRepo.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Alert {AlertId} resolved by {User}",
            alertId,
            resolvedBy);

        // Broadcast status change
        await BroadcastAlertStatusChangedAsync(alert);
    }

    public async Task DismissAlertAsync(
        Guid alertId,
        string dismissedBy,
        string? dismissReason = null,
        CancellationToken cancellationToken = default)
    {
        MaintenanceAlert? alert = await _alertRepo.GetByIdAsync(alertId, cancellationToken);
        if (alert == null)
        {
            _logger.LogWarning("Alert {AlertId} not found for dismissal", alertId);
            return;
        }

        EnsureAlertMutationEnabled(alert);

        alert.Status = MaintenanceAlertStatus.Dismissed;
        alert.DismissedAt = DateTime.UtcNow;
        alert.DismissedBy = dismissedBy;
        alert.DismissalReason = dismissReason;

        await _alertRepo.UpdateAsync(alert, cancellationToken);
        await _alertRepo.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Alert {AlertId} dismissed by {User}: {Reason}",
            alertId,
            dismissedBy,
            dismissReason ?? "No reason provided");

        // Broadcast status change
        await BroadcastAlertStatusChangedAsync(alert);
    }

    private void EnsureAlertMutationEnabled(MaintenanceAlert alert)
    {
        bool perToolMaintenanceEnabled =
            _operatorFeatureGate?.IsEnabled(OperatorFeature.MultiSlotFallback) ?? true;
        if (alert.ToolheadId.HasValue && !perToolMaintenanceEnabled)
        {
            throw new PerToolMaintenanceDisabledException();
        }
    }

    private async Task BroadcastAlertStatusChangedAsync(MaintenanceAlert alert)
    {
        MaintenanceAlertSettings settings = _settingsMonitor.CurrentValue;

        // SignalR notification remains gated on the operator toggle.
        if (settings.EnableSignalRNotifications)
        {
            try
            {
                await _hubContext.Clients.Group(Farm.Infrastructure.Security.AuthorizedHubGroups.Farm).SendAsync(
                    MaintenanceHubEvents.AlertStatusChanged,
                    new
                    {
                        id = alert.Id,
                        printerId = alert.PrinterId,
                        status = alert.Status.ToString(),
                        acknowledgedAt = alert.AcknowledgedAt,
                        acknowledgedBy = alert.AcknowledgedBy,
                        resolvedAt = alert.ResolvedAt,
                        resolvedBy = alert.ResolvedBy,
                        dismissedAt = alert.DismissedAt,
                        dismissedBy = alert.DismissedBy
                    });

                _logger.LogDebug("Broadcast alert status change for {AlertId} via SignalR", alert.Id);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to broadcast alert status change for {AlertId} via SignalR", alert.Id);
            }
        }

        // Invalidate the unified attention feed (issue #707). INDEPENDENT of the legacy
        // EnableSignalRNotifications toggle (review R3); fires after the committed status
        // mutation in Acknowledge/Resolve/Dismiss.
        if (_attentionBroadcaster is not null)
        {
            // Resolved/Dismissed retire the item; Acknowledged (and anything else) updates it.
            AttentionChangeKind changeKind = alert.Status is MaintenanceAlertStatus.Resolved
                or MaintenanceAlertStatus.Dismissed
                ? AttentionChangeKind.Resolved
                : AttentionChangeKind.Updated;
            DateTime occurredAt = alert.ResolvedAt
                ?? alert.DismissedAt
                ?? alert.AcknowledgedAt
                ?? DateTime.UtcNow;
            await _attentionBroadcaster.NotifyChangedAsync(new AttentionChangedPayload(
                AttentionIdPrefixes.Build(AttentionIdPrefixes.Maintenance, alert.Id),
                changeKind,
                occurredAt));
        }
    }

    public async Task<List<MaintenanceAlert>> GetAllActiveAlertsAsync(
        CancellationToken cancellationToken = default)
    {
        return await _alertRepo.GetAllActiveAlertsAsync(cancellationToken);
    }

    public async Task<List<MaintenanceAlert>> GetActivePrinterAlertsAsync(
        Guid printerId,
        CancellationToken cancellationToken = default)
    {
        return await _alertRepo.GetActivePrinterAlertsAsync(printerId, cancellationToken);
    }
}
