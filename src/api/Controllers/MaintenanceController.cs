using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Maintenance;
using Farm.Infrastructure.Services.Maintenance;
using Farm.Infrastructure.Services.Printers;
using Farm.Infrastructure.Services.Webhooks;
using Farm.Web.Api.Controllers.Responses;
using Farm.Web.Api.Hubs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace Farm.Web.Api.Controllers;

/// <summary>
/// API controller for printer maintenance management.
/// Provides endpoints for alerts, maintenance logging, schedules, statistics, and maintenance mode.
/// </summary>
[ApiController]
[Route("api/maintenance")]
[Authorize(Roles = "farm_admin")]
public class MaintenanceController(
    ILogger<MaintenanceController> logger,
    IMaintenanceAlertRepository alertRepository,
    IMaintenanceLogRepository logRepository,
    IPrinterMaintenanceScheduleRepository deploymentRepository,
    IPrinterStatisticsRepository statisticsRepository,
    IMaintenanceAlertService alertService,
    IPrintersService printersService,
    IHubContext<MaintenanceHub> maintenanceHub,
    IWebhookService webhookService)
    : ControllerBase
{
    private readonly ILogger<MaintenanceController> _logger = logger;
    private readonly IMaintenanceAlertRepository _alertRepository = alertRepository;
    private readonly IMaintenanceLogRepository _logRepository = logRepository;
    private readonly IPrinterMaintenanceScheduleRepository _deploymentRepository = deploymentRepository;
    private readonly IPrinterStatisticsRepository _statisticsRepository = statisticsRepository;
    private readonly IMaintenanceAlertService _alertService = alertService;
    private readonly IPrintersService _printersService = printersService;
    private readonly IHubContext<MaintenanceHub> _maintenanceHub = maintenanceHub;
    private readonly IWebhookService _webhookService = webhookService;

    #region Maintenance Alerts

    /// <summary>
    /// Gets all active maintenance alerts across all printers.
    /// </summary>
    [HttpGet("alerts")]
    [ProducesResponseType(typeof(IEnumerable<MaintenanceAlert>), 200)]
    public async Task<ActionResult<IEnumerable<MaintenanceAlert>>> GetAllAlertsAsync(CancellationToken ct)
    {
        try
        {
            List<MaintenanceAlert> alerts = await _alertRepository.GetAllActiveAlertsAsync(ct);
            return Ok(alerts);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[MaintenanceController] Error getting all alerts");
            return StatusCode(500, $"Internal Server Error: {ex.Message}");
        }
    }

    /// <summary>
    /// Gets a specific maintenance alert by ID.
    /// </summary>
    [HttpGet("alerts/{id:guid}")]
    [ProducesResponseType(typeof(MaintenanceAlert), 200)]
    [ProducesResponseType(404)]
    public async Task<ActionResult<MaintenanceAlert>> GetAlertByIdAsync(Guid id, CancellationToken ct)
    {
        try
        {
            MaintenanceAlert? alert = await _alertRepository.GetByIdAsync(id, ct);
            if (alert == null)
            {
                return NotFound($"Alert with ID {id} not found");
            }

            return Ok(alert);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[MaintenanceController] Error getting alert {Id}", id);
            return StatusCode(500, $"Internal Server Error: {ex.Message}");
        }
    }

    /// <summary>
    /// Gets all maintenance alerts for a specific printer.
    /// </summary>
    [HttpGet("printers/{printerId:guid}/alerts")]
    [ProducesResponseType(typeof(IEnumerable<MaintenanceAlert>), 200)]
    public async Task<ActionResult<IEnumerable<MaintenanceAlert>>> GetPrinterAlertsAsync(Guid printerId, CancellationToken ct)
    {
        try
        {
            List<MaintenanceAlert> alerts = await _alertRepository.GetActivePrinterAlertsAsync(printerId, ct);
            return Ok(alerts);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[MaintenanceController] Error getting alerts for printer {PrinterId}", printerId);
            return StatusCode(500, $"Internal Server Error: {ex.Message}");
        }
    }

    /// <summary>
    /// Acknowledges a maintenance alert (user has seen it).
    /// </summary>
    [HttpPost("alerts/{id:guid}/acknowledge")]
    [ProducesResponseType(typeof(MaintenanceAlert), 200)]
    [ProducesResponseType(404)]
    public async Task<ActionResult<MaintenanceAlert>> AcknowledgeAlertAsync(
        Guid id,
        [FromBody] AcknowledgeAlertRequest request,
        CancellationToken ct)
    {
        try
        {
            // Get the alert first
            MaintenanceAlert? alert = await _alertRepository.GetByIdAsync(id, ct);
            if (alert == null)
            {
                return NotFound($"Alert with ID {id} not found");
            }

            // Acknowledge it
            await _alertService.AcknowledgeAlertAsync(id, request.AcknowledgedBy, ct);

            // Reload to get updated state
            alert = await _alertRepository.GetByIdAsync(id, ct);

            // Broadcast status change
            await _maintenanceHub.Clients.All.SendAsync("alertstatuschanged", new
            {
                id = alert!.Id,
                printerId = alert.PrinterId,
                status = alert.Status.ToString(),
                acknowledgedAt = alert.AcknowledgedAt,
                acknowledgedBy = alert.AcknowledgedBy
            }, ct);

            return Ok(alert);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[MaintenanceController] Error acknowledging alert {Id}", id);
            return StatusCode(500, $"Internal Server Error: {ex.Message}");
        }
    }

    /// <summary>
    /// Resolves a maintenance alert by logging the completed maintenance.
    /// </summary>
    [HttpPost("alerts/{id:guid}/resolve")]
    [ProducesResponseType(typeof(ResolveAlertResponse), 200)]
    [ProducesResponseType(404)]
    public async Task<ActionResult<ResolveAlertResponse>> ResolveAlertAsync(
        Guid id,
        [FromBody] ResolveAlertRequest request,
        CancellationToken ct)
    {
        try
        {
            // Get the alert
            MaintenanceAlert? alert = await _alertRepository.GetByIdAsync(id, ct);
            if (alert == null)
            {
                return NotFound($"Alert with ID {id} not found");
            }

            // Capture current printer hours for accurate hour-based maintenance baselines.
            PrinterStatistics? stats = await _statisticsRepository.GetByPrinterIdAsync(alert.PrinterId, ct);

            // Create maintenance log
            var maintenanceLog = new MaintenanceLog
            {
                Id = Guid.NewGuid(),
                PrinterId = alert.PrinterId,
                PrinterMaintenanceScheduleId = alert.PrinterMaintenanceScheduleId,
                MaintenanceTaskId = alert.MaintenanceTaskId,
                TaskName = alert.Title ?? "Scheduled Maintenance",
                PerformedAt = DateTime.UtcNow,
                PerformedBy = request.PerformedBy,
                Notes = request.Notes,
                DurationMinutes = request.DurationMinutes,
                Cost = request.Cost,
                PartsReplaced = request.PartsReplaced,
                PrinterHoursAtMaintenance = stats?.TotalPrintHours
            };

            MaintenanceLog createdLog = await _logRepository.AddAsync(maintenanceLog, ct);

            // Resolve the alert
            await _alertService.ResolveAlertAsync(id, request.PerformedBy, ct);

            // Reload to get updated state
            alert = await _alertRepository.GetByIdAsync(id, ct);

            // Broadcast status change
            await _maintenanceHub.Clients.All.SendAsync("alertstatuschanged", new
            {
                id = alert!.Id,
                printerId = alert.PrinterId,
                status = alert.Status.ToString(),
                resolvedAt = alert.ResolvedAt,
                resolvedBy = alert.ResolvedBy
            }, ct);

            // Broadcast maintenance completed
            await _maintenanceHub.Clients.All.SendAsync("maintenancecompleted", new
            {
                logId = createdLog.Id,
                printerId = createdLog.PrinterId,
                deploymentId = createdLog.PrinterMaintenanceScheduleId,
                performedAt = createdLog.PerformedAt,
                performedBy = createdLog.PerformedBy
            }, ct);

            _webhookService.Enqueue("maintenance.completed", new
            {
                logId = createdLog.Id,
                printerId = createdLog.PrinterId,
                performedAt = createdLog.PerformedAt,
                performedBy = createdLog.PerformedBy
            });

            return Ok(new ResolveAlertResponse(alert, createdLog));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[MaintenanceController] Error resolving alert {Id}", id);
            return StatusCode(500, $"Internal Server Error: {ex.Message}");
        }
    }

    /// <summary>
    /// Dismisses a maintenance alert (user chooses to ignore).
    /// </summary>
    [HttpPost("alerts/{id:guid}/dismiss")]
    [ProducesResponseType(typeof(MaintenanceAlert), 200)]
    [ProducesResponseType(404)]
    public async Task<ActionResult<MaintenanceAlert>> DismissAlertAsync(
        Guid id,
        [FromBody] DismissAlertRequest request,
        CancellationToken ct)
    {
        try
        {
            // Get the alert first
            MaintenanceAlert? alert = await _alertRepository.GetByIdAsync(id, ct);
            if (alert == null)
            {
                return NotFound($"Alert with ID {id} not found");
            }

            // Dismiss it
            await _alertService.DismissAlertAsync(id, request.DismissedBy, request.Reason, ct);

            // Reload to get updated state
            alert = await _alertRepository.GetByIdAsync(id, ct);

            // Broadcast status change
            await _maintenanceHub.Clients.All.SendAsync("alertstatuschanged", new
            {
                id = alert!.Id,
                printerId = alert.PrinterId,
                status = alert.Status.ToString(),
                dismissedAt = alert.DismissedAt,
                dismissedBy = alert.DismissedBy,
                dismissalReason = alert.DismissalReason
            }, ct);

            return Ok(alert);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[MaintenanceController] Error dismissing alert {Id}", id);
            return StatusCode(500, $"Internal Server Error: {ex.Message}");
        }
    }

    #endregion

    #region Upcoming Maintenance

    /// <summary>
    /// Gets upcoming maintenance tasks across the fleet, computed server-side.
    /// Day-based tasks include real due dates; hour-based tasks include remaining hours and no synthetic dates.
    /// </summary>
    [HttpGet("upcoming")]
    [ProducesResponseType(typeof(IEnumerable<UpcomingMaintenanceTaskDto>), 200)]
    public async Task<ActionResult<IEnumerable<UpcomingMaintenanceTaskDto>>> GetUpcomingMaintenanceAsync(
        [FromQuery] int lookaheadDays = 30,
        [FromQuery] bool includeOverdue = true,
        [FromQuery] Guid? printerId = null,
        CancellationToken ct = default)
    {
        try
        {
            DateTime now = DateTime.UtcNow;

            List<Printer> printers;
            if (printerId.HasValue)
            {
                Printer? printer = await _printersService.FindByIdAsync(printerId.Value, ct);
                if (printer == null)
                {
                    return NotFound($"Printer with ID {printerId} not found");
                }

                printers = [printer];
            }
            else
            {
                printers = await _printersService.GetAllAsync(ct);
            }

            List<UpcomingMaintenanceTaskDto> tasks = [];

            // Batch-load all data in 3 queries instead of N per printer
            List<Guid> printerIds = printers.Select(p => p.Id).ToList();
            List<PrinterStatistics> allStats = await _statisticsRepository.GetAllAsync(ct);
            Dictionary<Guid, PrinterStatistics> statsByPrinter = allStats
                .Where(s => printerIds.Contains(s.PrinterId))
                .ToDictionary(s => s.PrinterId);
            List<MaintenanceLog> allLogs = await _logRepository.GetByPrinterIdsAsync(printerIds, ct);
            ILookup<Guid, MaintenanceLog> logsByPrinter = allLogs
                .ToLookup(l => l.PrinterId);

            // Load V3 deployments with deep PlanTasks → Tasks in a single batch query
            List<PrinterMaintenanceSchedule> allDeployments = await _deploymentRepository
                .GetActiveWithTasksAsync(printerIds, ct);

            ILookup<Guid, PrinterMaintenanceSchedule> deploymentsByPrinter = allDeployments
                .ToLookup(d => d.PrinterId);

            foreach (Printer printer in printers)
            {
                statsByPrinter.TryGetValue(printer.Id, out PrinterStatistics? stats);
                List<PrinterMaintenanceSchedule> deployments = deploymentsByPrinter[printer.Id].ToList();
                if (deployments.Count == 0)
                {
                    continue;
                }

                // Group last log by task ID for efficient lookup
                Dictionary<Guid, MaintenanceLog> lastLogByTaskId = logsByPrinter[printer.Id]
                    .Where(l => l.MaintenanceTaskId.HasValue)
                    .GroupBy(l => l.MaintenanceTaskId!.Value)
                    .ToDictionary(g => g.Key, g => g.OrderByDescending(x => x.PerformedAt).First());

                // Track tasks already processed (avoid duplicates if same task in multiple plans)
                HashSet<Guid> processedTasks = [];

                foreach (PrinterMaintenanceSchedule deployment in deployments)
                {
                    if (deployment.MaintenancePlan?.PlanTasks == null)
                    {
                        continue;
                    }

                    foreach (PlanTask planTask in deployment.MaintenancePlan.PlanTasks)
                    {
                        MaintenanceTask task = planTask.MaintenanceTask;
                        if (task == null || !task.IsActive || !processedTasks.Add(task.Id))
                        {
                            continue;
                        }

                        // Effective intervals: PlanTask overrides take precedence
                        double? effectiveHours = planTask.IntervalHoursOverride ?? task.IntervalHours;
                        int? effectiveDays = planTask.IntervalDaysOverride ?? task.IntervalDays;

                        if (!effectiveHours.HasValue && !effectiveDays.HasValue)
                        {
                            continue;
                        }

                        lastLogByTaskId.TryGetValue(task.Id, out MaintenanceLog? lastLog);

                        // Compute day-based due date (real calendar date)
                        DateTime baselineDate = lastLog?.PerformedAt ?? deployment.DeployedAt;
                        DateTime? dueDate = effectiveDays.HasValue
                            ? baselineDate.AddDays(effectiveDays.Value)
                            : null;
                        int? daysUntilDue = dueDate.HasValue
                            ? (int)(dueDate.Value.Date - now.Date).TotalDays
                            : null;

                        // Compute hour-based remaining time (no synthetic date)
                        double? hoursUntilDue = null;
                        if (effectiveHours.HasValue)
                        {
                            if (stats == null && !effectiveDays.HasValue)
                            {
                                continue;
                            }

                            if (stats != null)
                            {
                                double hoursSinceLast = lastLog?.PrinterHoursAtMaintenance is double baselineHours
                                    ? Math.Max(0, stats.TotalPrintHours - baselineHours)
                                    : stats.TotalPrintHours;
                                hoursUntilDue = effectiveHours.Value - hoursSinceLast;
                            }
                        }

                        // Choose how to present either/or: whichever is due sooner.
                        string intervalType;
                        double intervalValue;
                        DateTime? effectiveDueDate;
                        int? effectiveDaysUntilDue;
                        double? effectiveHoursUntilDue;

                        if (effectiveDays.HasValue && !effectiveHours.HasValue)
                        {
                            intervalType = "days";
                            intervalValue = effectiveDays.Value;
                            effectiveDueDate = dueDate;
                            effectiveDaysUntilDue = daysUntilDue;
                            effectiveHoursUntilDue = null;
                        }
                        else if (effectiveHours.HasValue && !effectiveDays.HasValue)
                        {
                            intervalType = "hours";
                            intervalValue = effectiveHours.Value;
                            effectiveDueDate = null;
                            effectiveDaysUntilDue = null;
                            effectiveHoursUntilDue = hoursUntilDue;
                        }
                        else
                        {
                            // Both set
                            if (hoursUntilDue.HasValue && daysUntilDue.HasValue)
                            {
                                double daysAsHours = daysUntilDue.Value * 24.0;
                                bool hoursComesFirst = hoursUntilDue.Value <= daysAsHours;

                                intervalType = hoursComesFirst ? "hours" : "days";
                                intervalValue = hoursComesFirst ? effectiveHours!.Value : effectiveDays!.Value;
                                effectiveDueDate = hoursComesFirst ? null : dueDate;
                                effectiveDaysUntilDue = hoursComesFirst ? null : daysUntilDue;
                                effectiveHoursUntilDue = hoursComesFirst ? hoursUntilDue : null;
                            }
                            else
                            {
                                intervalType = "days";
                                intervalValue = effectiveDays!.Value;
                                effectiveDueDate = dueDate;
                                effectiveDaysUntilDue = daysUntilDue;
                                effectiveHoursUntilDue = null;
                            }
                        }

                        bool isOverdue = intervalType == "days"
                            ? (effectiveDaysUntilDue ?? 0) < 0
                            : (effectiveHoursUntilDue ?? double.PositiveInfinity) <= 0;

                        bool isDueToday = intervalType == "days" && effectiveDueDate.HasValue && effectiveDueDate.Value.Date == now.Date;

                        if (!includeOverdue && isOverdue)
                        {
                            continue;
                        }

                        if (intervalType == "days" && effectiveDaysUntilDue.HasValue && effectiveDaysUntilDue.Value > lookaheadDays)
                        {
                            continue;
                        }

                        if (intervalType == "hours" && effectiveHoursUntilDue.HasValue && effectiveHoursUntilDue.Value > lookaheadDays * 24.0)
                        {
                            continue;
                        }

                        string taskId = $"{printer.Id}-{task.Id}";

                        tasks.Add(new UpcomingMaintenanceTaskDto(
                            taskId,
                            task.Id,
                            printer.Id,
                            printer.Name ?? "Unknown Printer",
                            task.TaskName,
                            task.Category,
                            task.Description,
                            task.Priority,
                            intervalType,
                            intervalValue,
                            effectiveDueDate,
                            effectiveDaysUntilDue,
                            effectiveHoursUntilDue,
                            isOverdue,
                            isDueToday,
                            lastLog?.PerformedAt));
                    }
                }
            }

            // Sort: overdue first, then by whichever remaining value we have.
            tasks.Sort((a, b) =>
            {
                if (a.IsOverdue && !b.IsOverdue)
                {
                    return -1;
                }

                if (!a.IsOverdue && b.IsOverdue)
                {
                    return 1;
                }

                if (a.DueDate.HasValue && b.DueDate.HasValue)
                {
                    return a.DueDate.Value.CompareTo(b.DueDate.Value);
                }

                if (a.HoursUntilDue.HasValue && b.HoursUntilDue.HasValue)
                {
                    return a.HoursUntilDue.Value.CompareTo(b.HoursUntilDue.Value);
                }

                // Prefer real dates over hour-only when mixed.
                if (a.DueDate.HasValue && !b.DueDate.HasValue)
                {
                    return -1;
                }

                if (!a.DueDate.HasValue && b.DueDate.HasValue)
                {
                    return 1;
                }

                return string.CompareOrdinal(a.TaskName, b.TaskName);
            });

            return Ok(tasks);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[MaintenanceController] Error getting upcoming maintenance");
            return StatusCode(500, $"Internal Server Error: {ex.Message}");
        }
    }

    #endregion

    #region Maintenance Logs

    /// <summary>
    /// Gets maintenance history for a specific printer.
    /// </summary>
    [HttpGet("printers/{printerId:guid}/logs")]
    [ProducesResponseType(typeof(IEnumerable<MaintenanceLog>), 200)]
    public async Task<ActionResult<IEnumerable<MaintenanceLog>>> GetPrinterMaintenanceLogsAsync(Guid printerId, CancellationToken ct)
    {
        try
        {
            List<MaintenanceLog> logs = await _logRepository.GetByPrinterIdAsync(printerId, ct);
            return Ok(logs);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[MaintenanceController] Error getting logs for printer {PrinterId}", printerId);
            return StatusCode(500, $"Internal Server Error: {ex.Message}");
        }
    }

    /// <summary>
    /// Creates a new maintenance log entry (manual logging without alert).
    /// </summary>
    [HttpPost("logs")]
    [ProducesResponseType(typeof(MaintenanceLog), 201)]
    public async Task<ActionResult<MaintenanceLog>> CreateMaintenanceLogAsync([FromBody] CreateMaintenanceLogRequest request, CancellationToken ct)
    {
        try
        {
            PrinterStatistics? stats = await _statisticsRepository.GetByPrinterIdAsync(request.PrinterId, ct);

            var log = new MaintenanceLog
            {
                Id = Guid.NewGuid(),
                PrinterId = request.PrinterId,
                PrinterMaintenanceScheduleId = request.DeploymentId,
                MaintenanceTaskId = request.TaskId,
                TaskName = request.TaskName ?? "Manual Maintenance",
                Component = request.ComponentName,
                PerformedAt = request.PerformedAt ?? DateTime.UtcNow,
                PerformedBy = request.PerformedBy,
                Notes = request.Notes,
                DurationMinutes = request.DurationMinutes,
                Cost = request.Cost,
                PartsReplaced = request.PartsReplaced,
                PrinterHoursAtMaintenance = stats?.TotalPrintHours
            };

            MaintenanceLog createdLog = await _logRepository.AddAsync(log, ct);

            // Broadcast maintenance completed
            await _maintenanceHub.Clients.All.SendAsync("maintenancecompleted", new
            {
                logId = createdLog.Id,
                printerId = createdLog.PrinterId,
                taskId = createdLog.MaintenanceTaskId,
                performedAt = createdLog.PerformedAt,
                performedBy = createdLog.PerformedBy
            }, ct);

            _webhookService.Enqueue("maintenance.completed", new
            {
                logId = createdLog.Id,
                printerId = createdLog.PrinterId,
                taskId = createdLog.MaintenanceTaskId,
                performedAt = createdLog.PerformedAt,
                performedBy = createdLog.PerformedBy
            });

            return CreatedAtAction(nameof(GetPrinterMaintenanceLogsAsync), new { printerId = createdLog.PrinterId }, createdLog);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[MaintenanceController] Error creating maintenance log");
            return StatusCode(500, $"Internal Server Error: {ex.Message}");
        }
    }

    #endregion

    // NOTE: Old flat MaintenanceSchedule CRUD endpoints removed (V3 migration).
    // Schedule management is now handled by MaintenanceScheduleDeploymentController
    // using the hierarchical Task → Plan → PrinterMaintenanceSchedule model.
    #region Printer Statistics

    /// <summary>
    /// Gets cumulative statistics for all printers with upcoming maintenance info.
    /// </summary>
    [HttpGet("statistics/fleet")]
    [ProducesResponseType(typeof(List<FleetPrinterStatisticsDto>), 200)]
    public async Task<ActionResult<List<FleetPrinterStatisticsDto>>> GetFleetStatisticsAsync(CancellationToken ct)
    {
        try
        {
            // Get all statistics with printer info
            var allStats = await _statisticsRepository.GetAllAsync(ct);

            // Get all printers with includes for manufacturer/model names
            var allPrinters = await _printersService.GetAllWithIncludesAsync(ct);

            // Get recent logs to determine last performed dates (last 2 years)
            var twoYearsAgo = DateTime.UtcNow.AddYears(-2);
            var logs = await _logRepository.GetAllAsync(twoYearsAgo, null, ct);
            var logsByPrinter = logs.GroupBy(l => l.PrinterId).ToDictionary(g => g.Key, g => g.ToList());

            // Batch-load all deployments in one query instead of per-printer
            var allPrinterIds = allPrinters.Select(p => p.Id).ToList();
            var allDeployments = await _deploymentRepository.GetActiveWithTasksAsync(allPrinterIds, ct);
            var deploymentsByPrinter = allDeployments.ToLookup(d => d.PrinterId);

            var result = new List<FleetPrinterStatisticsDto>();

            foreach (var printer in allPrinters)
            {
                var stats = allStats.FirstOrDefault(s => s.PrinterId == printer.Id);

                var deployments = deploymentsByPrinter[printer.Id].ToList();
                var printerLogs = logsByPrinter.GetValueOrDefault(printer.Id, []);

                // Calculate days until next maintenance
                int? daysUntilNextMaintenance = null;
                string? nextMaintenanceTask = null;

                // Track tasks already evaluated (avoid duplicates from multiple plans)
                HashSet<Guid> processedTasks = [];

                foreach (var deployment in deployments)
                {
                    if (deployment.MaintenancePlan?.PlanTasks == null)
                    {
                        continue;
                    }

                    foreach (var planTask in deployment.MaintenancePlan.PlanTasks)
                    {
                        var task = planTask.MaintenanceTask;
                        if (task == null || !task.IsActive || !processedTasks.Add(task.Id))
                        {
                            continue;
                        }

                        // Effective intervals: PlanTask overrides take precedence
                        double? effectiveHours = planTask.IntervalHoursOverride ?? task.IntervalHours;
                        int? effectiveDays = planTask.IntervalDaysOverride ?? task.IntervalDays;

                        // Find the last log for this task
                        var lastLog = printerLogs
                            .Where(l => l.MaintenanceTaskId == task.Id)
                            .OrderByDescending(l => l.PerformedAt)
                            .FirstOrDefault();

                        DateTime lastPerformed = lastLog?.PerformedAt ?? deployment.DeployedAt;
                        DateTime nextDue;

                        if (effectiveHours.HasValue)
                        {
                            double hoursSinceLastMaintenance = stats?.TotalPrintHours ?? 0;
                            double hoursRemaining = effectiveHours.Value - hoursSinceLastMaintenance;
                            nextDue = DateTime.UtcNow.AddDays(hoursRemaining / 8.0);
                        }
                        else if (effectiveDays.HasValue)
                        {
                            nextDue = lastPerformed.AddDays(effectiveDays.Value);
                        }
                        else
                        {
                            continue;
                        }

                        int daysTillDue = (int)(nextDue - DateTime.UtcNow).TotalDays;

                        if (daysUntilNextMaintenance == null || daysTillDue < daysUntilNextMaintenance)
                        {
                            daysUntilNextMaintenance = daysTillDue;
                            nextMaintenanceTask = task.TaskName;
                        }
                    }
                }

                result.Add(new FleetPrinterStatisticsDto
                {
                    PrinterId = printer.Id,
                    PrinterName = printer.Name,
                    ManufacturerName = printer.Manufacturer?.Name,
                    ModelName = printer.Model?.Name,
                    IsOnline = printer.IsAvailable, // Use IsAvailable as proxy for online status
                    InMaintenance = printer.InMaintenance,
                    TotalPrintHours = stats?.TotalPrintHours ?? 0,
                    TotalJobsCompleted = stats?.TotalJobsCompleted ?? 0,
                    TotalJobsFailed = stats?.TotalJobsFailed ?? 0,
                    TotalFilamentUsedGrams = stats?.TotalFilamentUsedGrams ?? 0,
                    TotalFilamentUsedMeters = stats?.TotalFilamentUsedMeters ?? 0,
                    LastSyncTime = stats?.LastSyncTime,
                    DaysUntilNextMaintenance = daysUntilNextMaintenance,
                    NextMaintenanceTask = nextMaintenanceTask
                });
            }

            return Ok(result.OrderBy(r => r.DaysUntilNextMaintenance ?? int.MaxValue).ToList());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[MaintenanceController] Error getting fleet statistics");
            return StatusCode(500, $"Internal Server Error: {ex.Message}");
        }
    }

    /// <summary>
    /// Gets cumulative statistics for a specific printer.
    /// </summary>
    [HttpGet("printers/{printerId:guid}/statistics")]
    [ProducesResponseType(typeof(PrinterStatistics), 200)]
    [ProducesResponseType(404)]
    public async Task<ActionResult<PrinterStatistics>> GetPrinterStatisticsAsync(Guid printerId, CancellationToken ct)
    {
        try
        {
            PrinterStatistics? stats = await _statisticsRepository.GetByPrinterIdAsync(printerId, ct);
            if (stats == null)
            {
                return NotFound($"Statistics not found for printer {printerId}");
            }

            return Ok(stats);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[MaintenanceController] Error getting statistics for printer {PrinterId}", printerId);
            return StatusCode(500, $"Internal Server Error: {ex.Message}");
        }
    }

    #endregion

    #region Maintenance Mode

    /// <summary>
    /// Updates the maintenance mode status for a printer.
    /// When in maintenance mode, the printer should not receive new print jobs.
    /// </summary>
    [HttpPut("printers/{printerId:guid}/mode")]
    [ProducesResponseType(204)]
    [ProducesResponseType(404)]
    public async Task<ActionResult> UpdateMaintenanceModeAsync(
        Guid printerId,
        [FromBody] UpdateMaintenanceModeRequest request,
        CancellationToken ct)
    {
        try
        {
            Printer? printer = await _printersService.FindByIdAsync(printerId, ct);
            if (printer == null)
            {
                return NotFound($"Printer with ID {printerId} not found");
            }

            printer.InMaintenance = request.InMaintenance;

            // The IPrintersService doesn't have UpdateAsync, so we rely on EF Core change tracking
            // No explicit call needed - changes are saved automatically
            _logger.LogInformation("[MaintenanceController] Printer {PrinterId} maintenance mode set to {RequestInMaintenance}", printerId, request.InMaintenance);

            return NoContent();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[MaintenanceController] Error updating maintenance mode for printer {PrinterId}", printerId);
            return StatusCode(500, $"Internal Server Error: {ex.Message}");
        }
    }

    #endregion

    #region Analytics

    /// <summary>
    /// Gets maintenance trends within a date range.
    /// </summary>
    [HttpGet("analytics/trends")]
    [ProducesResponseType(typeof(List<MaintenanceTrendResponse>), 200)]
    public async Task<ActionResult<List<MaintenanceTrendResponse>>> GetTrendsAsync(
        [FromQuery] DateTime? startDate,
        [FromQuery] DateTime? endDate,
        CancellationToken ct)
    {
        try
        {
            DateTime start = startDate ?? DateTime.UtcNow.AddMonths(-6);
            DateTime end = endDate ?? DateTime.UtcNow;

            var trends = await _logRepository.GetTrendsAsync(start, end, ct);

            var response = trends.Select(t => new MaintenanceTrendResponse(
                t.Date,
                t.PrinterName,
                t.Component,
                t.Action,
                t.Cost)).ToList();

            return Ok(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[MaintenanceController] Error getting maintenance trends");
            return StatusCode(500, $"Internal Server Error: {ex.Message}");
        }
    }

    /// <summary>
    /// Gets component lifespan analytics (average hours between replacements).
    /// </summary>
    [HttpGet("analytics/component-lifespan")]
    [ProducesResponseType(typeof(List<ComponentLifespanResponse>), 200)]
    public async Task<ActionResult<List<ComponentLifespanResponse>>> GetComponentLifespanAsync(CancellationToken ct)
    {
        try
        {
            var lifespans = await _logRepository.GetComponentLifespanAsync(ct);

            var response = lifespans.Select(l => new ComponentLifespanResponse(
                l.Component,
                l.AvgLifespanHours,
                l.Replacements)).ToList();

            return Ok(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[MaintenanceController] Error getting component lifespan data");
            return StatusCode(500, $"Internal Server Error: {ex.Message}");
        }
    }

    /// <summary>
    /// Gets monthly maintenance cost analysis.
    /// </summary>
    [HttpGet("analytics/cost")]
    [ProducesResponseType(typeof(List<MaintenanceCostResponse>), 200)]
    public async Task<ActionResult<List<MaintenanceCostResponse>>> GetCostAnalysisAsync(
        [FromQuery] int months = 12,
        CancellationToken ct = default)
    {
        try
        {
            var costs = await _logRepository.GetCostAnalysisAsync(months, ct);

            var response = costs.Select(c => new MaintenanceCostResponse(
                c.Month,
                c.TotalCost)).ToList();

            return Ok(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[MaintenanceController] Error getting cost analysis data");
            return StatusCode(500, $"Internal Server Error: {ex.Message}");
        }
    }

    /// <summary>
    /// Gets printer uptime percentages and maintenance metrics.
    /// </summary>
    [HttpGet("analytics/uptime")]
    [ProducesResponseType(typeof(List<PrinterUptimeResponse>), 200)]
    public async Task<ActionResult<List<PrinterUptimeResponse>>> GetPrinterUptimeAsync(CancellationToken ct)
    {
        try
        {
            var uptimes = await _logRepository.GetPrinterUptimeAsync(ct);

            var response = uptimes.Select(u => new PrinterUptimeResponse(
                u.PrinterName,
                u.PrinterId,
                u.UptimePercent,
                u.MaintenanceCount,
                u.TotalDowntimeMinutes)).ToList();

            return Ok(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[MaintenanceController] Error getting printer uptime data");
            return StatusCode(500, $"Internal Server Error: {ex.Message}");
        }
    }

    #endregion
}

#region Request/Response DTOs

public record AcknowledgeAlertRequest(string AcknowledgedBy);

public record DismissAlertRequest(string DismissedBy, string? Reason);

public record ResolveAlertRequest(
    string PerformedBy,
    string? Notes,
    int? DurationMinutes,
    decimal? Cost,
    string? PartsReplaced);

public record ResolveAlertResponse(
    MaintenanceAlert Alert,
    MaintenanceLog MaintenanceLog);

public record CreateMaintenanceLogRequest(
    Guid PrinterId,
    Guid? DeploymentId,
    Guid? TaskId,
    string? TaskName,
    string? ComponentName,
    DateTime? PerformedAt,
    string PerformedBy,
    string? Notes,
    int? DurationMinutes,
    decimal? Cost,
    string? PartsReplaced);

public record UpdateMaintenanceModeRequest(bool InMaintenance);

#endregion

#region Fleet Statistics DTOs

/// <summary>
/// DTO for fleet-wide printer statistics including maintenance projections.
/// </summary>
public record FleetPrinterStatisticsDto
{
    public Guid PrinterId { get; init; }

    public string PrinterName { get; init; } = string.Empty;

    public string? ManufacturerName { get; init; }

    public string? ModelName { get; init; }

    public bool IsOnline { get; init; }

    public bool InMaintenance { get; init; }

    public double TotalPrintHours { get; init; }

    public int TotalJobsCompleted { get; init; }

    public int TotalJobsFailed { get; init; }

    public double TotalFilamentUsedGrams { get; init; }

    public double TotalFilamentUsedMeters { get; init; }

    public DateTime? LastSyncTime { get; init; }

    /// <summary>Days until next maintenance task is due (negative = overdue)</summary>
    public int? DaysUntilNextMaintenance { get; init; }

    /// <summary>Name of the next maintenance task due</summary>
    public string? NextMaintenanceTask { get; init; }
}

#endregion

#region Analytics DTOs

public record MaintenanceTrendResponse(
    DateTime Date,
    string PrinterName,
    string? Component,
    string Action,
    decimal Cost);

public record ComponentLifespanResponse(
    string Component,
    double AvgLifespanHours,
    int Replacements);

public record MaintenanceCostResponse(
    string Month,
    decimal TotalCost);

public record PrinterUptimeResponse(
    string PrinterName,
    Guid PrinterId,
    double UptimePercent,
    int MaintenanceCount,
    int TotalDowntimeMinutes);

#endregion
