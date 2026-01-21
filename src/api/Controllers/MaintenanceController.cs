using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Maintenance;
using Farm.Infrastructure.Services.Printers;
using Farm.Infrastructure.Telemetry;
using Farm.Web.Api.Hubs;
using Farm.Web.Api.Services.Maintenance;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;

namespace Farm.Web.Api.Controllers;

/// <summary>
/// API controller for printer maintenance management.
/// Provides endpoints for alerts, maintenance logging, schedules, statistics, and maintenance mode.
/// </summary>
[ApiController]
[Route("api/maintenance")]
public class MaintenanceController(
    IUnifiedLoggingService logger,
    IMaintenanceAlertRepository alertRepository,
    IMaintenanceLogRepository logRepository,
    IMaintenanceScheduleRepository scheduleRepository,
    IPrinterStatisticsRepository statisticsRepository,
    IMaintenanceAlertService alertService,
    IPrintersService printersService,
    IHubContext<MaintenanceHub> maintenanceHub)
    : ControllerBase
{
    private readonly IUnifiedLoggingService _logger = logger;
    private readonly IMaintenanceAlertRepository _alertRepository = alertRepository;
    private readonly IMaintenanceLogRepository _logRepository = logRepository;
    private readonly IMaintenanceScheduleRepository _scheduleRepository = scheduleRepository;
    private readonly IPrinterStatisticsRepository _statisticsRepository = statisticsRepository;
    private readonly IMaintenanceAlertService _alertService = alertService;
    private readonly IPrintersService _printersService = printersService;
    private readonly IHubContext<MaintenanceHub> _maintenanceHub = maintenanceHub;

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
            _logger.LogError(ex, $"[MaintenanceController] Error getting alert {id}");
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
            _logger.LogError(ex, $"[MaintenanceController] Error getting alerts for printer {printerId}");
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
            _logger.LogError(ex, $"[MaintenanceController] Error acknowledging alert {id}");
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

            // Create maintenance log
            var maintenanceLog = new MaintenanceLog
            {
                Id = Guid.NewGuid(),
                PrinterId = alert.PrinterId,
                MaintenanceScheduleId = alert.MaintenanceScheduleId,
                TaskName = alert.Title ?? "Scheduled Maintenance",
                PerformedAt = DateTime.UtcNow,
                PerformedBy = request.PerformedBy,
                Notes = request.Notes,
                DurationMinutes = request.DurationMinutes,
                Cost = request.Cost,
                PartsReplaced = request.PartsReplaced
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
                scheduleId = createdLog.MaintenanceScheduleId,
                performedAt = createdLog.PerformedAt,
                performedBy = createdLog.PerformedBy
            }, ct);

            return Ok(new ResolveAlertResponse(alert, createdLog));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"[MaintenanceController] Error resolving alert {id}");
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
            _logger.LogError(ex, $"[MaintenanceController] Error dismissing alert {id}");
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
            _logger.LogError(ex, $"[MaintenanceController] Error getting logs for printer {printerId}");
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
            var log = new MaintenanceLog
            {
                Id = Guid.NewGuid(),
                PrinterId = request.PrinterId,
                MaintenanceScheduleId = request.ScheduleId,
                TaskName = request.TaskName ?? "Manual Maintenance",
                Component = request.ComponentName,
                PerformedAt = request.PerformedAt ?? DateTime.UtcNow,
                PerformedBy = request.PerformedBy,
                Notes = request.Notes,
                DurationMinutes = request.DurationMinutes,
                Cost = request.Cost,
                PartsReplaced = request.PartsReplaced
            };

            MaintenanceLog createdLog = await _logRepository.AddAsync(log, ct);

            // Broadcast maintenance completed
            await _maintenanceHub.Clients.All.SendAsync("maintenancecompleted", new
            {
                logId = createdLog.Id,
                printerId = createdLog.PrinterId,
                scheduleId = createdLog.MaintenanceScheduleId,
                performedAt = createdLog.PerformedAt,
                performedBy = createdLog.PerformedBy
            }, ct);

            return CreatedAtAction(nameof(GetPrinterMaintenanceLogsAsync), new { printerId = createdLog.PrinterId }, createdLog);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[MaintenanceController] Error creating maintenance log");
            return StatusCode(500, $"Internal Server Error: {ex.Message}");
        }
    }

    #endregion

    #region Maintenance Schedules

    /// <summary>
    /// Gets all maintenance schedules.
    /// </summary>
    [HttpGet("schedules")]
    [ProducesResponseType(typeof(IEnumerable<MaintenanceSchedule>), 200)]
    public async Task<ActionResult<IEnumerable<MaintenanceSchedule>>> GetAllSchedulesAsync(CancellationToken ct)
    {
        try
        {
            List<MaintenanceSchedule> schedules = await _scheduleRepository.GetAllAsync(ct);
            return Ok(schedules);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[MaintenanceController] Error getting all schedules");
            return StatusCode(500, $"Internal Server Error: {ex.Message}");
        }
    }

    /// <summary>
    /// Gets maintenance schedules for a specific printer (includes both printer-specific and model-wide).
    /// </summary>
    [HttpGet("printers/{printerId:guid}/schedules")]
    [ProducesResponseType(typeof(IEnumerable<MaintenanceSchedule>), 200)]
    public async Task<ActionResult<IEnumerable<MaintenanceSchedule>>> GetPrinterSchedulesAsync(Guid printerId, CancellationToken ct)
    {
        try
        {
            List<MaintenanceSchedule> schedules = await _scheduleRepository.GetActivePrinterSchedulesAsync(printerId, ct);
            return Ok(schedules);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"[MaintenanceController] Error getting schedules for printer {printerId}");
            return StatusCode(500, $"Internal Server Error: {ex.Message}");
        }
    }

    /// <summary>
    /// Creates a new maintenance schedule.
    /// </summary>
    [HttpPost("schedules")]
    [ProducesResponseType(typeof(MaintenanceSchedule), 201)]
    public async Task<ActionResult<MaintenanceSchedule>> CreateScheduleAsync([FromBody] CreateMaintenanceScheduleRequest request, CancellationToken ct)
    {
        try
        {
            var schedule = new MaintenanceSchedule
            {
                Id = Guid.NewGuid(),
                TaskName = request.TaskName,
                Description = request.Description,
                IntervalHours = request.IntervalHours,
                IntervalDays = request.IntervalDays,
                Component = request.ComponentName,
                PrinterModelId = request.PrinterModelId,
                PrinterId = request.PrinterId,
                IsActive = request.IsActive ?? true
            };

            await _scheduleRepository.AddAsync(schedule, ct);

            return CreatedAtAction(nameof(GetAllSchedulesAsync), new { id = schedule.Id }, schedule);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[MaintenanceController] Error creating schedule");
            return StatusCode(500, $"Internal Server Error: {ex.Message}");
        }
    }

    /// <summary>
    /// Updates an existing maintenance schedule.
    /// </summary>
    [HttpPut("schedules/{id:guid}")]
    [ProducesResponseType(typeof(MaintenanceSchedule), 200)]
    [ProducesResponseType(404)]
    public async Task<ActionResult<MaintenanceSchedule>> UpdateScheduleAsync(
        Guid id,
        [FromBody] UpdateMaintenanceScheduleRequest request,
        CancellationToken ct)
    {
        try
        {
            MaintenanceSchedule? schedule = await _scheduleRepository.GetByIdAsync(id, ct);
            if (schedule == null)
            {
                return NotFound($"Schedule with ID {id} not found");
            }

            schedule.TaskName = request.TaskName ?? schedule.TaskName;
            schedule.Description = request.Description ?? schedule.Description;
            schedule.IntervalHours = request.IntervalHours ?? schedule.IntervalHours;
            schedule.IntervalDays = request.IntervalDays ?? schedule.IntervalDays;
            schedule.Component = request.ComponentName ?? schedule.Component;
            schedule.IsActive = request.IsActive ?? schedule.IsActive;

            await _scheduleRepository.UpdateAsync(schedule, ct);

            return Ok(schedule);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"[MaintenanceController] Error updating schedule {id}");
            return StatusCode(500, $"Internal Server Error: {ex.Message}");
        }
    }

    /// <summary>
    /// Deletes a maintenance schedule.
    /// </summary>
    [HttpDelete("schedules/{id:guid}")]
    [ProducesResponseType(204)]
    [ProducesResponseType(404)]
    public async Task<ActionResult> DeleteScheduleAsync(Guid id, CancellationToken ct)
    {
        try
        {
            MaintenanceSchedule? schedule = await _scheduleRepository.GetByIdAsync(id, ct);
            if (schedule == null)
            {
                return NotFound($"Schedule with ID {id} not found");
            }

            await _scheduleRepository.DeleteAsync(id, ct);

            return NoContent();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"[MaintenanceController] Error deleting schedule {id}");
            return StatusCode(500, $"Internal Server Error: {ex.Message}");
        }
    }

    #endregion

    #region Printer Statistics

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
            _logger.LogError(ex, $"[MaintenanceController] Error getting statistics for printer {printerId}");
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

            _logger.LogInformation($"[MaintenanceController] Printer {printerId} maintenance mode set to {request.InMaintenance}");

            return NoContent();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"[MaintenanceController] Error updating maintenance mode for printer {printerId}");
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
    Guid? ScheduleId,
    string? TaskName,
    string? ComponentName,
    DateTime? PerformedAt,
    string PerformedBy,
    string? Notes,
    int? DurationMinutes,
    decimal? Cost,
    string? PartsReplaced);

public record CreateMaintenanceScheduleRequest(
    string TaskName,
    string? Description,
    double? IntervalHours,
    int? IntervalDays,
    string? ComponentName,
    Guid? PrinterModelId,
    Guid? PrinterId,
    bool? IsActive);

public record UpdateMaintenanceScheduleRequest(
    string? TaskName,
    string? Description,
    double? IntervalHours,
    int? IntervalDays,
    string? ComponentName,
    bool? IsActive);

public record UpdateMaintenanceModeRequest(bool InMaintenance);

#endregion
