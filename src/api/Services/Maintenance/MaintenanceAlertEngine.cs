using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Maintenance;
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
    IHubContext<MaintenanceHub> hubContext,
    IOptionsMonitor<MaintenanceAlertSettings> settingsMonitor,
    ILogger<MaintenanceAlertEngine> logger) : IMaintenanceAlertService
{
    private readonly IPrinterStatisticsRepository _statsRepo = statsRepo ?? throw new ArgumentNullException(nameof(statsRepo));
    private readonly IMaintenanceScheduleRepository _scheduleRepo = scheduleRepo ?? throw new ArgumentNullException(nameof(scheduleRepo));
    private readonly IMaintenanceAlertRepository _alertRepo = alertRepo ?? throw new ArgumentNullException(nameof(alertRepo));
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

        int alertsGenerated = 0;

        // Evaluate each schedule
        foreach (MaintenanceSchedule schedule in schedules)
        {
            bool shouldAlert = await ShouldGenerateAlertAsync(stats, schedule, settings, cancellationToken);

            if (shouldAlert)
            {
                // Check if alert already exists
                bool hasActiveAlert = await _alertRepo.HasActiveAlertAsync(
                    printerId,
                    schedule.Id,
                    cancellationToken);

                if (!hasActiveAlert)
                {
                    await GenerateAlertAsync(stats, schedule, cancellationToken);
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

    private async Task<bool> ShouldGenerateAlertAsync(
        PrinterStatistics stats,
        MaintenanceSchedule schedule,
        MaintenanceAlertSettings settings,
        CancellationToken cancellationToken)
    {
        // Check hour-based interval
        if (schedule.IntervalHours.HasValue)
        {
            double thresholdHours = schedule.IntervalHours.Value * (settings.ThresholdPercentage / 100.0);

            if (stats.TotalPrintHours >= thresholdHours)
            {
                _logger.LogDebug(
                    "Schedule '{TaskName}' triggered: {Hours}h >= {Threshold}h (interval: {Interval}h)",
                    schedule.TaskName,
                    stats.TotalPrintHours,
                    thresholdHours,
                    schedule.IntervalHours.Value);
                return true;
            }
        }

        // Check day-based interval
        if (schedule.IntervalDays.HasValue)
        {
            // For day-based, we need to check against the last maintenance log
            // For Phase 3, we'll use schedule creation date as baseline
            // In Phase 4, we'll integrate with MaintenanceLog
            DateTime baselineDate = schedule.CreatedAt;
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
        CancellationToken cancellationToken)
    {
        MaintenanceAlertSettings settings = _settingsMonitor.CurrentValue;

        // Create alert
        MaintenanceAlert alert = new()
        {
            Id = Guid.NewGuid(),
            PrinterId = stats.PrinterId,
            MaintenanceScheduleId = schedule.Id,
            Title = $"Maintenance Due: {schedule.TaskName}",
            Message = BuildAlertMessage(stats, schedule),
            Severity = schedule.Priority,
            Status = MaintenanceAlertStatus.Active,
            PrinterHoursAtTrigger = stats.TotalPrintHours,
            HoursSinceLastMaintenance = schedule.IntervalHours.HasValue ? stats.TotalPrintHours : null,
            DaysSinceLastMaintenance = schedule.IntervalDays.HasValue
                ? (DateTime.UtcNow - schedule.CreatedAt).Days
                : null
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

    private static string BuildAlertMessage(PrinterStatistics stats, MaintenanceSchedule schedule)
    {
        if (schedule.IntervalHours.HasValue)
        {
            return $"{schedule.TaskName} is due for printer. " +
                   $"Current hours: {stats.TotalPrintHours:F1}h, " +
                   $"Interval: {schedule.IntervalHours:F1}h. " +
                   $"{schedule.Description ?? "Please perform scheduled maintenance."}";
        }

        if (schedule.IntervalDays.HasValue)
        {
            int daysSince = (DateTime.UtcNow - schedule.CreatedAt).Days;
            return $"{schedule.TaskName} is due for printer. " +
                   $"Days since last maintenance: {daysSince}, " +
                   $"Interval: {schedule.IntervalDays} days. " +
                   $"{schedule.Description ?? "Please perform scheduled maintenance."}";
        }

        return $"{schedule.TaskName} is due. {schedule.Description ?? "Please perform scheduled maintenance."}";
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
