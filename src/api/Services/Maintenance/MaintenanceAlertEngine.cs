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
/// Service that evaluates maintenance schedules against printer statistics
/// and generates alerts when maintenance is due.
/// </summary>
public class MaintenanceAlertEngine(
    IPrinterStatisticsRepository statsRepo,
    IMaintenanceScheduleRepository scheduleRepo,
    IMaintenanceAlertRepository alertRepo,
    IMaintenanceLogRepository logRepo,
    IHubContext<MaintenanceHub> hubContext,
    IOptionsMonitor<MaintenanceAlertSettings> settingsMonitor,
    ILogger<MaintenanceAlertEngine> logger) : IMaintenanceAlertService
{
    private readonly IPrinterStatisticsRepository _statsRepo = statsRepo ?? throw new ArgumentNullException(nameof(statsRepo));
    private readonly IMaintenanceScheduleRepository _scheduleRepo = scheduleRepo ?? throw new ArgumentNullException(nameof(scheduleRepo));
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

        // Get active schedules for this printer
        List<MaintenanceSchedule> schedules = await _scheduleRepo.GetActivePrinterSchedulesAsync(
            printerId,
            cancellationToken);

        if (schedules.Count == 0)
        {
            _logger.LogDebug("No active schedules found for printer {PrinterId}", printerId);
            return 0;
        }

        // Load maintenance logs once so we can compute baselines efficiently.
        List<MaintenanceLog> logs = await _logRepo.GetByPrinterIdAsync(printerId, cancellationToken);
        Dictionary<Guid, MaintenanceLog> lastLogByScheduleId = logs
            .Where(l => l.MaintenanceScheduleId.HasValue)
            .GroupBy(l => l.MaintenanceScheduleId!.Value)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(x => x.PerformedAt).First());

        int alertsGenerated = 0;

        // Evaluate each schedule
        foreach (MaintenanceSchedule schedule in schedules)
        {
            lastLogByScheduleId.TryGetValue(schedule.Id, out MaintenanceLog? lastLog);
            bool shouldAlert = ShouldGenerateAlert(stats, schedule, lastLog, settings);

            if (shouldAlert)
            {
                // Check if alert already exists
                bool hasActiveAlert = await _alertRepo.HasActiveAlertAsync(
                    printerId,
                    schedule.Id,
                    cancellationToken);

                if (!hasActiveAlert)
                {
                    await GenerateAlertAsync(stats, schedule, lastLog, cancellationToken);
                    alertsGenerated++;
                }
                else
                {
                    _logger.LogDebug(
                        "Active alert already exists for printer {PrinterId} and schedule {ScheduleId}",
                        printerId,
                        schedule.Id);
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
        MaintenanceSchedule schedule,
        MaintenanceLog? lastLog,
        MaintenanceAlertSettings settings)
    {
        // Check hour-based interval
        if (schedule.IntervalHours.HasValue)
        {
            double thresholdHours = schedule.IntervalHours.Value * (settings.ThresholdPercentage / 100.0);

            double hoursSinceLast = ComputeHoursSinceLastMaintenance(stats, lastLog);

            if (hoursSinceLast >= thresholdHours)
            {
                _logger.LogDebug(
                    "Schedule '{TaskName}' triggered: {Hours}h >= {Threshold}h (interval: {Interval}h)",
                    schedule.TaskName,
                    hoursSinceLast,
                    thresholdHours,
                    schedule.IntervalHours.Value);
                return true;
            }
        }

        // Check day-based interval
        if (schedule.IntervalDays.HasValue)
        {
            DateTime baselineDate = lastLog?.PerformedAt ?? schedule.CreatedAt;
            int daysSinceBaseline = (DateTime.UtcNow - baselineDate).Days;
            int thresholdDays = (int)(schedule.IntervalDays.Value * (settings.ThresholdPercentage / 100.0));

            if (daysSinceBaseline >= thresholdDays)
            {
                _logger.LogDebug(
                    "Schedule '{TaskName}' triggered: {Days} days >= {Threshold} days (interval: {Interval} days)",
                    schedule.TaskName,
                    daysSinceBaseline,
                    thresholdDays,
                    schedule.IntervalDays.Value);
                return true;
            }
        }

        return false;
    }

    private async Task GenerateAlertAsync(
        PrinterStatistics stats,
        MaintenanceSchedule schedule,
        MaintenanceLog? lastLog,
        CancellationToken cancellationToken)
    {
        MaintenanceAlertSettings settings = _settingsMonitor.CurrentValue;

        double? hoursSinceLast = schedule.IntervalHours.HasValue
            ? ComputeHoursSinceLastMaintenance(stats, lastLog)
            : null;

        int? daysSinceLast = schedule.IntervalDays.HasValue
            ? (DateTime.UtcNow - (lastLog?.PerformedAt ?? schedule.CreatedAt)).Days
            : null;

        // Create alert
        MaintenanceAlert alert = new()
        {
            Id = Guid.NewGuid(),
            PrinterId = stats.PrinterId,
            MaintenanceScheduleId = schedule.Id,
            Title = $"Maintenance Due: {schedule.TaskName}",
            Message = BuildAlertMessage(stats, schedule, hoursSinceLast, daysSinceLast),
            Severity = schedule.Priority,
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
        MaintenanceSchedule schedule,
        double? hoursSinceLastMaintenance,
        int? daysSinceLastMaintenance)
    {
        if (schedule.IntervalHours.HasValue && !schedule.IntervalDays.HasValue)
        {
            return $"{schedule.TaskName} is due for printer. " +
                   $"Hours since last maintenance: {hoursSinceLastMaintenance ?? stats.TotalPrintHours:F1}h, " +
                   $"Interval: {schedule.IntervalHours:F1}h. " +
                   $"{schedule.Description ?? "Please perform scheduled maintenance."}";
        }

        if (schedule.IntervalDays.HasValue && !schedule.IntervalHours.HasValue)
        {
            return $"{schedule.TaskName} is due for printer. " +
                   $"Days since last maintenance: {daysSinceLastMaintenance ?? 0}, " +
                   $"Interval: {schedule.IntervalDays} days. " +
                   $"{schedule.Description ?? "Please perform scheduled maintenance."}";
        }

        if (schedule.IntervalHours.HasValue && schedule.IntervalDays.HasValue)
        {
            return $"{schedule.TaskName} is due for printer. " +
                   $"Hours since last maintenance: {hoursSinceLastMaintenance ?? stats.TotalPrintHours:F1}h (interval: {schedule.IntervalHours:F1}h), " +
                   $"Days since last maintenance: {daysSinceLastMaintenance ?? 0} (interval: {schedule.IntervalDays} days). " +
                   $"{schedule.Description ?? "Please perform scheduled maintenance."}";
        }

        return $"{schedule.TaskName} is due. {schedule.Description ?? "Please perform scheduled maintenance."}";
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
