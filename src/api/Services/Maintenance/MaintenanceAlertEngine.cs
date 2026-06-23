using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Maintenance;
using Farm.Infrastructure.Services.Maintenance;
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
    ILogger<MaintenanceAlertEngine> logger) : IMaintenanceAlertService
{
    private readonly IPrinterStatisticsRepository _statsRepo = statsRepo ?? throw new ArgumentNullException(nameof(statsRepo));
    private readonly IPrinterMaintenanceScheduleRepository _deploymentRepo = deploymentRepo ?? throw new ArgumentNullException(nameof(deploymentRepo));
    private readonly IMaintenanceAlertRepository _alertRepo = alertRepo ?? throw new ArgumentNullException(nameof(alertRepo));
    private readonly IMaintenanceLogRepository _logRepo = logRepo ?? throw new ArgumentNullException(nameof(logRepo));
    private readonly IHubContext<MaintenanceHub> _hubContext = hubContext ?? throw new ArgumentNullException(nameof(hubContext));
    private readonly IOptionsMonitor<MaintenanceAlertSettings> _settingsMonitor = settingsMonitor ?? throw new ArgumentNullException(nameof(settingsMonitor));
    private readonly ILogger<MaintenanceAlertEngine> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

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
        // Group by MaintenanceTaskId for V3 task-level dedup.
        List<MaintenanceLog> logs = await _logRepo.GetByPrinterIdAsync(printerId, cancellationToken);
        Dictionary<Guid, MaintenanceLog> lastLogByTaskId = logs
            .Where(l => l.MaintenanceTaskId.HasValue)
            .GroupBy(l => l.MaintenanceTaskId!.Value)
            .ToDictionary(
                g => g.Key,
                g => g.Aggregate((latest, current) => current.PerformedAt > latest.PerformedAt ? current : latest));

        int alertsGenerated = 0;

        // Evaluate each deployment → plan → tasks
        foreach (PrinterMaintenanceSchedule deployment in deployments)
        {
            if (deployment.MaintenancePlan?.PlanTasks == null)
            {
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

                lastLogByTaskId.TryGetValue(task.Id, out MaintenanceLog? lastLog);
                bool shouldAlert = ShouldGenerateAlert(stats, task.TaskName, effectiveHours, effectiveDays, lastLog, deployment.DeployedAt, settings);

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
                        await GenerateAlertAsync(stats, deployment, task, effectiveHours, effectiveDays, lastLog, cancellationToken);
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
        MaintenanceAlertSettings settings)
    {
        // Check hour-based interval
        if (intervalHours.HasValue)
        {
            double thresholdHours = intervalHours.Value * (settings.ThresholdPercentage / 100.0);

            double hoursSinceLast = ComputeHoursSinceLastMaintenance(stats, lastLog);

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

    private async Task GenerateAlertAsync(
        PrinterStatistics stats,
        PrinterMaintenanceSchedule deployment,
        MaintenanceTask task,
        double? effectiveHours,
        int? effectiveDays,
        MaintenanceLog? lastLog,
        CancellationToken cancellationToken)
    {
        MaintenanceAlertSettings settings = _settingsMonitor.CurrentValue;

        double? hoursSinceLast = effectiveHours.HasValue
            ? ComputeHoursSinceLastMaintenance(stats, lastLog)
            : null;

        int? daysSinceLast = effectiveDays.HasValue
            ? (DateTime.UtcNow - (lastLog?.PerformedAt ?? deployment.DeployedAt)).Days
            : null;

        // Create alert referencing both the deployment and the specific task
        MaintenanceAlert alert = new()
        {
            Id = Guid.NewGuid(),
            PrinterId = stats.PrinterId,
            PrinterMaintenanceScheduleId = deployment.Id,
            MaintenanceTaskId = task.Id,
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

        // Send SignalR notification if enabled
        if (settings.EnableSignalRNotifications)
        {
            await BroadcastAlertCreatedAsync(alert);
        }
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

    private static double ComputeHoursSinceLastMaintenance(PrinterStatistics stats, MaintenanceLog? lastLog)
    {
        // Preferred baseline is the printer's total hours at the last maintenance log.
        // If historical logs don't contain printer hours yet, fall back to total hours
        // (maintains previous behavior until new logs populate PrinterHoursAtMaintenance).
        if (lastLog?.PrinterHoursAtMaintenance is double baselineHours)
        {
            return Math.Max(0, stats.TotalPrintHours - baselineHours);
        }

        return stats.TotalPrintHours;
    }

    private async Task BroadcastAlertCreatedAsync(MaintenanceAlert alert)
    {
        try
        {
            await _hubContext.Clients.All.SendAsync(
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

    private async Task BroadcastAlertStatusChangedAsync(MaintenanceAlert alert)
    {
        MaintenanceAlertSettings settings = _settingsMonitor.CurrentValue;
        if (!settings.EnableSignalRNotifications)
        {
            return;
        }

        try
        {
            await _hubContext.Clients.All.SendAsync(
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
