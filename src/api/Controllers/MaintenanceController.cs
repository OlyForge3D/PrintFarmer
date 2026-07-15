using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Maintenance;
using Farm.Infrastructure.Services.Maintenance;
using Farm.Infrastructure.Services.OperatorFeatures;
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
    IToolheadStatisticsRepository toolheadStatisticsRepository,
    IMaintenanceAlertService alertService,
    IPrintersService printersService,
    IOperatorFeatureGate operatorFeatureGate,
    IHubContext<MaintenanceHub> maintenanceHub,
    IWebhookService webhookService,
    IMaintenanceAlertResolutionService alertResolutionService)
    : ControllerBase
{
    private readonly ILogger<MaintenanceController> _logger = logger;
    private readonly IMaintenanceAlertRepository _alertRepository = alertRepository;
    private readonly IMaintenanceLogRepository _logRepository = logRepository;
    private readonly IPrinterMaintenanceScheduleRepository _deploymentRepository = deploymentRepository;
    private readonly IPrinterStatisticsRepository _statisticsRepository = statisticsRepository;
    private readonly IToolheadStatisticsRepository _toolheadStatisticsRepository = toolheadStatisticsRepository;
    private readonly IMaintenanceAlertService _alertService = alertService;
    private readonly IPrintersService _printersService = printersService;
    private readonly IOperatorFeatureGate _operatorFeatureGate = operatorFeatureGate;
    private readonly IHubContext<MaintenanceHub> _maintenanceHub = maintenanceHub;
    private readonly IWebhookService _webhookService = webhookService;
    private readonly IMaintenanceAlertResolutionService _alertResolutionService = alertResolutionService;

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
            if (!await _operatorFeatureGate.IsEnabledAsync(OperatorFeature.MultiSlotFallback, ct).ConfigureAwait(false))
            {
                alerts = alerts.Where(a => !a.ToolheadId.HasValue).ToList();
            }

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
            if (alert == null
                || (alert.ToolheadId.HasValue
                    && !await _operatorFeatureGate.IsEnabledAsync(OperatorFeature.MultiSlotFallback, ct).ConfigureAwait(false)))
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
            if (!await _operatorFeatureGate.IsEnabledAsync(OperatorFeature.MultiSlotFallback, ct).ConfigureAwait(false))
            {
                alerts = alerts.Where(a => !a.ToolheadId.HasValue).ToList();
            }

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
    [ProducesResponseType(400)]
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
        catch (PerToolMaintenanceDisabledException ex)
        {
            return BadRequest(ex.Message);
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
    [ProducesResponseType(400)]
    [ProducesResponseType(409)]
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

            // Per-tool maintenance gate (issue #711, round-5 FIX 2). A toolhead-scoped alert must
            // not be resolved into a per-tool maintenance log while MultiSlotFallback is disabled,
            // mirroring CreateMaintenanceLogAsync. Reject rather than silently strip the scope so
            // the resolution log never misrepresents which head was serviced.
            if (alert.ToolheadId.HasValue
                && !await _operatorFeatureGate.IsEnabledAsync(OperatorFeature.MultiSlotFallback, ct).ConfigureAwait(false))
            {
                return BadRequest("Per-tool maintenance is disabled.");
            }

            // Capture current printer hours for accurate hour-based maintenance baselines.
            PrinterStatistics? stats = await _statisticsRepository.GetByPrinterIdAsync(alert.PrinterId, ct);

            // For per-toolhead-scoped alerts, also capture the toolhead's cumulative hours so
            // the next accrual measures from this point (issue #711, FIX B).
            double? toolheadHoursAtMaintenance = alert.ToolheadId.HasValue
                ? await _toolheadStatisticsRepository.GetCumulativeHoursAsync(alert.ToolheadId.Value, ct)
                : null;

            // Create maintenance log
            var maintenanceLog = new MaintenanceLog
            {
                Id = Guid.NewGuid(),
                PrinterId = alert.PrinterId,
                PrinterMaintenanceScheduleId = alert.PrinterMaintenanceScheduleId,
                MaintenanceTaskId = alert.MaintenanceTaskId,
                ToolheadId = alert.ToolheadId,
                TaskName = alert.Title ?? "Scheduled Maintenance",
                PerformedAt = DateTime.UtcNow,
                PerformedBy = request.PerformedBy,
                Notes = request.Notes,
                DurationMinutes = request.DurationMinutes,
                Cost = request.Cost,
                PartsReplaced = request.PartsReplaced,
                PrinterHoursAtMaintenance = stats?.TotalPrintHours,
                ToolheadHoursAtMaintenance = toolheadHoursAtMaintenance
            };

            // Atomically resolve the alert and persist its completion log in a single transaction so
            // a per-tool gate that flips after the pre-check above cannot leave an orphaned log with
            // an unresolved alert (issue #711, round-7 Finding 5). The service re-checks the gate,
            // stages the log, mutates the alert, and commits — or rolls back on any failure.
            MaintenanceAlertResolutionResult? resolution = await _alertResolutionService.ResolveWithLogAsync(
                id,
                maintenanceLog,
                request.PerformedBy,
                ct);
            if (resolution == null)
            {
                return NotFound($"Alert with ID {id} not found");
            }

            alert = resolution.Alert;
            return Ok(new ResolveAlertResponse(
                alert,
                resolution.Log,
                resolution.Created));
        }
        catch (PerToolMaintenanceDisabledException ex)
        {
            return BadRequest(ex.Message);
        }
        catch (MaintenanceAlertNotResolvableException ex)
        {
            return Conflict(ex.Message);
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
    [ProducesResponseType(400)]
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
        catch (PerToolMaintenanceDisabledException ex)
        {
            return BadRequest(ex.Message);
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
            bool includeToolheadScope = await _operatorFeatureGate.IsEnabledAsync(OperatorFeature.MultiSlotFallback, ct).ConfigureAwait(false);
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
                .Where(l => includeToolheadScope || !l.ToolheadId.HasValue)
                .ToLookup(l => l.PrinterId);

            // Load V3 deployments with deep PlanTasks → Tasks in a single batch query
            List<PrinterMaintenanceSchedule> allDeployments = await _deploymentRepository
                .GetActiveWithTasksAsync(printerIds, ct);
            if (!includeToolheadScope)
            {
                allDeployments = allDeployments.Where(d => !d.ToolheadId.HasValue).ToList();
            }

            ILookup<Guid, PrinterMaintenanceSchedule> deploymentsByPrinter = allDeployments
                .ToLookup(d => d.PrinterId);

            // Per-toolhead cumulative hours (toolhead ID → hours) so per-tool schedules project
            // their remaining time against their own toolhead, not the printer-wide counter
            // (issue #711, FIX B).
            IReadOnlyDictionary<Guid, double> toolheadHours = await _toolheadStatisticsRepository
                .GetCumulativeHoursByPrintersAsync(printerIds, ct);

            foreach (Printer printer in printers)
            {
                statsByPrinter.TryGetValue(printer.Id, out PrinterStatistics? stats);
                List<PrinterMaintenanceSchedule> deployments = deploymentsByPrinter[printer.Id].ToList();
                if (deployments.Count == 0)
                {
                    continue;
                }

                // Group last log by (task ID, toolhead scope) so per-toolhead logs do not
                // contaminate printer-wide baselines and vice versa (issue #711, F6).
                Dictionary<(Guid TaskId, Guid? ToolheadId), MaintenanceLog> lastLogByTaskId = logsByPrinter[printer.Id]
                    .Where(l => l.MaintenanceTaskId.HasValue)
                    .GroupBy(l => (l.MaintenanceTaskId!.Value, l.ToolheadId))
                    .ToDictionary(
                        g => g.Key,
                        g => g.Aggregate((latest, current) => current.PerformedAt > latest.PerformedAt ? current : latest));

                // Track (task, toolhead scope) pairs already processed so per-toolhead
                // schedules surface as independent upcoming rows without duplicating a task
                // that appears in multiple plans for the same scope.
                HashSet<(Guid TaskId, Guid? ToolheadId)> processedTasks = [];

                foreach (PrinterMaintenanceSchedule deployment in deployments)
                {
                    if (deployment.MaintenancePlan?.PlanTasks == null)
                    {
                        continue;
                    }

                    foreach (PlanTask planTask in deployment.MaintenancePlan.PlanTasks)
                    {
                        MaintenanceTask task = planTask.MaintenanceTask;
                        if (task == null || !task.IsActive || !processedTasks.Add((task.Id, deployment.ToolheadId)))
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

                        lastLogByTaskId.TryGetValue((task.Id, deployment.ToolheadId), out MaintenanceLog? lastLog);

                        // Compute day-based due date (real calendar date)
                        DateTime baselineDate = lastLog?.PerformedAt ?? deployment.DeployedAt;
                        DateTime? dueDate = effectiveDays.HasValue
                            ? baselineDate.AddDays(effectiveDays.Value)
                            : null;
                        int? daysUntilDue = dueDate.HasValue
                            ? (int)(dueDate.Value.Date - now.Date).TotalDays
                            : null;

                        // Compute hour-based remaining time (no synthetic date). Per-tool
                        // schedules accrue against their own toolhead's cumulative hours
                        // (issue #711, FIX B); printer-wide schedules use TotalPrintHours.
                        double? hoursUntilDue = null;
                        if (effectiveHours.HasValue)
                        {
                            bool perToolScope = deployment.ToolheadId.HasValue
                                && toolheadHours.ContainsKey(deployment.ToolheadId.Value);

                            if (stats == null && !effectiveDays.HasValue && !perToolScope)
                            {
                                continue;
                            }

                            double? hoursSinceLast = null;
                            if (perToolScope)
                            {
                                double currentToolheadHours = toolheadHours[deployment.ToolheadId!.Value];
                                double toolheadBaseline = lastLog?.ToolheadHoursAtMaintenance ?? 0;
                                hoursSinceLast = Math.Max(0, currentToolheadHours - toolheadBaseline);
                            }
                            else if (stats != null)
                            {
                                hoursSinceLast = lastLog?.PrinterHoursAtMaintenance is double baselineHours
                                    ? Math.Max(0, stats.TotalPrintHours - baselineHours)
                                    : stats.TotalPrintHours;
                            }

                            if (hoursSinceLast.HasValue)
                            {
                                hoursUntilDue = effectiveHours.Value - hoursSinceLast.Value;
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

                        string taskId = deployment.ToolheadId.HasValue
                            ? $"{printer.Id}-{task.Id}-{deployment.ToolheadId}"
                            : $"{printer.Id}-{task.Id}";

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
                            lastLog?.PerformedAt,
                            deployment.ToolheadId));
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
            if (!await _operatorFeatureGate.IsEnabledAsync(OperatorFeature.MultiSlotFallback, ct).ConfigureAwait(false))
            {
                logs = logs.Where(l => !l.ToolheadId.HasValue).ToList();
            }

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

            // Resolve the authoritative toolhead scope (issue #711, FIX C). When a deployment
            // (schedule) is referenced, its ToolheadId is authoritative: load it, verify it
            // belongs to the same printer, and reject a client-supplied ToolheadId that
            // contradicts the deployment's scope. This prevents a log that claims one toolhead
            // while pointing at a schedule scoped to another.
            Guid? effectiveToolheadId = request.ToolheadId;
            if (request.DeploymentId.HasValue)
            {
                PrinterMaintenanceSchedule? deployment = await _deploymentRepository.GetByIdAsync(request.DeploymentId.Value, ct);
                if (deployment is null)
                {
                    return BadRequest($"Deployment {request.DeploymentId} was not found.");
                }

                if (deployment.PrinterId != request.PrinterId)
                {
                    return BadRequest($"Deployment {request.DeploymentId} belongs to printer {deployment.PrinterId}, not {request.PrinterId}.");
                }

                if (request.ToolheadId.HasValue && request.ToolheadId != deployment.ToolheadId)
                {
                    return BadRequest(
                        $"Toolhead {request.ToolheadId} contradicts deployment {request.DeploymentId} " +
                        $"(scope: {deployment.ToolheadId?.ToString() ?? "printer-wide"}).");
                }

                // Deployment scope wins so the log is always consistent with its schedule.
                effectiveToolheadId = deployment.ToolheadId;
            }

            if (effectiveToolheadId.HasValue
                && !await _operatorFeatureGate.IsEnabledAsync(OperatorFeature.MultiSlotFallback, ct).ConfigureAwait(false))
            {
                return BadRequest("Per-tool maintenance is disabled.");
            }

            // Validate the (resolved) per-toolhead scope (issue #711, F6). Null = printer-wide
            // log. When set, the toolhead must be a physical dock on the target printer;
            // MMU/AMS gates are not eligible for maintenance scope.
            if (effectiveToolheadId.HasValue)
            {
                Printer? printer = await _printersService.FindByIdWithIncludesAsync(request.PrinterId, ct);
                Toolhead? toolhead = printer?.Toolheads.FirstOrDefault(t => t.Id == effectiveToolheadId.Value);
                if (toolhead is null)
                {
                    return BadRequest($"Toolhead {effectiveToolheadId} does not belong to printer {request.PrinterId}.");
                }

                if (toolhead.ToolheadType != ToolheadType.Physical)
                {
                    return BadRequest($"Toolhead {effectiveToolheadId} is not a physical toolhead and is not eligible for maintenance scope.");
                }
            }

            // For per-toolhead-scoped logs, capture the toolhead's cumulative hours so the next
            // accrual measures from this point (issue #711, FIX B).
            double? toolheadHoursAtMaintenance = effectiveToolheadId.HasValue
                ? await _toolheadStatisticsRepository.GetCumulativeHoursAsync(effectiveToolheadId.Value, ct)
                : null;

            var log = new MaintenanceLog
            {
                Id = Guid.NewGuid(),
                PrinterId = request.PrinterId,
                PrinterMaintenanceScheduleId = request.DeploymentId,
                MaintenanceTaskId = request.TaskId,
                ToolheadId = effectiveToolheadId,
                TaskName = request.TaskName ?? "Manual Maintenance",
                Component = request.ComponentName,
                PerformedAt = request.PerformedAt ?? DateTime.UtcNow,
                PerformedBy = request.PerformedBy,
                Notes = request.Notes,
                DurationMinutes = request.DurationMinutes,
                Cost = request.Cost,
                PartsReplaced = request.PartsReplaced,
                PrinterHoursAtMaintenance = stats?.TotalPrintHours,
                ToolheadHoursAtMaintenance = toolheadHoursAtMaintenance
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

            return CreatedAtAction("GetPrinterMaintenanceLogs", new { printerId = createdLog.PrinterId }, createdLog);
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
            bool includeToolheadScope = await _operatorFeatureGate.IsEnabledAsync(OperatorFeature.MultiSlotFallback, ct).ConfigureAwait(false);

            // Get all statistics with printer info
            var allStats = await _statisticsRepository.GetAllAsync(ct);

            // Get all printers with includes for manufacturer/model names
            var allPrinters = await _printersService.GetAllWithIncludesAsync(ct);

            // Get recent logs to determine last performed dates (last 2 years)
            var twoYearsAgo = DateTime.UtcNow.AddYears(-2);
            var logs = await _logRepository.GetAllAsync(twoYearsAgo, null, ct);
            if (!includeToolheadScope)
            {
                logs = logs.Where(l => !l.ToolheadId.HasValue).ToList();
            }

            var logsByPrinter = logs.GroupBy(l => l.PrinterId).ToDictionary(g => g.Key, g => g.ToList());

            // Batch-load all deployments in one query instead of per-printer
            var allPrinterIds = allPrinters.Select(p => p.Id).ToList();
            var allDeployments = await _deploymentRepository.GetActiveWithTasksAsync(allPrinterIds, ct);
            if (!includeToolheadScope)
            {
                allDeployments = allDeployments.Where(d => !d.ToolheadId.HasValue).ToList();
            }

            var deploymentsByPrinter = allDeployments.ToLookup(d => d.PrinterId);

            // Per-toolhead cumulative hours so per-tool schedules project against their own
            // toolhead's hours, not the printer-wide counter (issue #711, round-5 FIX 3).
            IReadOnlyDictionary<Guid, double> toolheadHours =
                await _toolheadStatisticsRepository.GetCumulativeHoursByPrintersAsync(allPrinterIds, ct);

            var result = new List<FleetPrinterStatisticsDto>();

            foreach (var printer in allPrinters)
            {
                var stats = allStats.FirstOrDefault(s => s.PrinterId == printer.Id);

                var deployments = deploymentsByPrinter[printer.Id].ToList();
                var printerLogs = logsByPrinter.GetValueOrDefault(printer.Id, []);

                // Calculate days until next maintenance
                int? daysUntilNextMaintenance = null;
                string? nextMaintenanceTask = null;

                // Last log per (task, toolhead scope) so per-toolhead logs do not contaminate
                // printer-wide baselines and vice versa (issue #711, round-5 FIX 3).
                Dictionary<(Guid TaskId, Guid? ToolheadId), MaintenanceLog> lastLogByTaskAndScope = printerLogs
                    .Where(l => l.MaintenanceTaskId.HasValue)
                    .GroupBy(l => (l.MaintenanceTaskId!.Value, l.ToolheadId))
                    .ToDictionary(
                        g => g.Key,
                        g => g.Aggregate((latest, current) => current.PerformedAt > latest.PerformedAt ? current : latest));

                // Track (task, toolhead scope) pairs already evaluated so a per-tool schedule on
                // each head is projected independently instead of collapsing to a single task
                // (issue #711, round-5 FIX 3). The aggregate below keeps the most-urgent result.
                HashSet<(Guid TaskId, Guid? ToolheadId)> processedTasks = [];

                foreach (var deployment in deployments)
                {
                    if (deployment.MaintenancePlan?.PlanTasks == null)
                    {
                        continue;
                    }

                    foreach (var planTask in deployment.MaintenancePlan.PlanTasks)
                    {
                        var task = planTask.MaintenanceTask;
                        if (task == null || !task.IsActive || !processedTasks.Add((task.Id, deployment.ToolheadId)))
                        {
                            continue;
                        }

                        // Effective intervals: PlanTask overrides take precedence
                        double? effectiveHours = planTask.IntervalHoursOverride ?? task.IntervalHours;
                        int? effectiveDays = planTask.IntervalDaysOverride ?? task.IntervalDays;

                        // Last log for this exact (task, toolhead scope) pair.
                        lastLogByTaskAndScope.TryGetValue((task.Id, deployment.ToolheadId), out MaintenanceLog? lastLog);

                        DateTime lastPerformed = lastLog?.PerformedAt ?? deployment.DeployedAt;
                        DateTime nextDue;

                        if (effectiveHours.HasValue)
                        {
                            // Per-tool schedules accrue against their own toolhead's cumulative
                            // hours; printer-wide schedules use TotalPrintHours. Each measures from
                            // its own captured baseline (issue #711, round-5 FIX 3).
                            double hoursSinceLastMaintenance;
                            if (deployment.ToolheadId.HasValue
                                && toolheadHours.TryGetValue(deployment.ToolheadId.Value, out double currentToolheadHours))
                            {
                                double toolheadBaseline = lastLog?.ToolheadHoursAtMaintenance ?? 0;
                                hoursSinceLastMaintenance = Math.Max(0, currentToolheadHours - toolheadBaseline);
                            }
                            else
                            {
                                double totalHours = stats?.TotalPrintHours ?? 0;
                                hoursSinceLastMaintenance = lastLog?.PrinterHoursAtMaintenance is double baselineHours
                                    ? Math.Max(0, totalHours - baselineHours)
                                    : totalHours;
                            }

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
    /// Existing printers without accrued statistics return an empty, non-persisted snapshot.
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
                Printer? printer = await _printersService.FindByIdAsync(printerId, ct);
                if (printer is null)
                {
                    return NotFound($"Printer not found: {printerId}");
                }

                return Ok(new PrinterStatistics
                {
                    Id = printerId,
                    PrinterId = printerId,
                    TotalPrintHours = 0,
                    TotalJobsCompleted = 0,
                    TotalJobsFailed = 0,
                    TotalFilamentUsedGrams = 0,
                    TotalFilamentUsedMeters = 0,

                    // This non-persisted snapshot represents an existing printer with no accrued statistics yet.
                    // MinValue dates serialize as "0001-01-01T00:00:00"; the frontend treats min/epoch dates as never-synced and renders an em dash.
                    LastSyncTime = default,
                    CreatedAt = default,
                    UpdatedAt = default
                });
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

            bool includeToolheadScope = await _operatorFeatureGate.IsEnabledAsync(OperatorFeature.MultiSlotFallback, ct).ConfigureAwait(false);
            var trends = await _logRepository.GetTrendsAsync(start, end, includeToolheadScope, ct);

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
            bool includeToolheadScope = await _operatorFeatureGate.IsEnabledAsync(OperatorFeature.MultiSlotFallback, ct).ConfigureAwait(false);
            var lifespans = await _logRepository.GetComponentLifespanAsync(includeToolheadScope, ct);

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
            bool includeToolheadScope = await _operatorFeatureGate.IsEnabledAsync(OperatorFeature.MultiSlotFallback, ct).ConfigureAwait(false);
            var costs = await _logRepository.GetCostAnalysisAsync(months, includeToolheadScope, ct);

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
            bool includeToolheadScope = await _operatorFeatureGate.IsEnabledAsync(OperatorFeature.MultiSlotFallback, ct).ConfigureAwait(false);
            var uptimes = await _logRepository.GetPrinterUptimeAsync(includeToolheadScope, ct);

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
    MaintenanceLog? MaintenanceLog,
    bool Created = true);

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
    string? PartsReplaced,
    Guid? ToolheadId = null);

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
