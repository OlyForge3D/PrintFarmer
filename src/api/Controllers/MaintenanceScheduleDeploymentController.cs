using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Maintenance;
using Farm.Infrastructure.Repositories.Printers;
using Farm.Web.Api.Controllers.Requests;
using Farm.Web.Api.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Farm.Web.Api.Controllers;

/// <summary>
/// API controller for deploying maintenance plans to printers.
/// A schedule represents an active deployment of a plan on a specific printer.
/// </summary>
[ApiController]
[Route("api/maintenance/schedules")]
[Authorize(Roles = "farm_admin")]
public class MaintenanceScheduleDeploymentController(
    ILogger<MaintenanceScheduleDeploymentController> logger,
    IPrinterMaintenanceScheduleRepository scheduleRepository,
    IMaintenancePlanRepository planRepository,
    IPrintersRepository printersRepository)
    : ControllerBase
{
    private readonly ILogger<MaintenanceScheduleDeploymentController> _logger = logger;
    private readonly IPrinterMaintenanceScheduleRepository _scheduleRepository = scheduleRepository;
    private readonly IMaintenancePlanRepository _planRepository = planRepository;
    private readonly IPrintersRepository _printersRepository = printersRepository;

    private static PrinterMaintenanceScheduleResponse ToResponse(PrinterMaintenanceSchedule s) => new(
        s.Id,
        s.MaintenancePlanId,
        s.MaintenancePlan?.Name ?? string.Empty,
        s.PrinterId,
        s.Printer?.Name,
        s.IsActive,
        s.DeployedAt,
        s.Notes,
        s.CreatedAt,
        s.UpdatedAt);

    /// <summary>
    /// Gets all schedule deployments. Optionally filter by printer, plan, or active status.
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<List<PrinterMaintenanceScheduleResponse>>> GetAllAsync(
        [FromQuery] Guid? printerId,
        [FromQuery] Guid? planId,
        [FromQuery] bool? activeOnly,
        CancellationToken ct)
    {
        List<PrinterMaintenanceSchedule> schedules = await _scheduleRepository.GetAllAsync(printerId, planId, activeOnly, ct);
        return Ok(schedules.Select(ToResponse).ToList());
    }

    /// <summary>
    /// Gets a schedule deployment by ID.
    /// </summary>
    [HttpGet("{id:guid}")]
    public async Task<ActionResult<PrinterMaintenanceScheduleResponse>> GetByIdAsync(Guid id, CancellationToken ct)
    {
        PrinterMaintenanceSchedule? schedule = await _scheduleRepository.GetByIdAsync(id, ct);
        if (schedule == null)
        {
            return NotFound();
        }

        return Ok(ToResponse(schedule));
    }

    /// <summary>
    /// Deploys a maintenance plan to a printer. Returns 409 if already deployed.
    /// </summary>
    [HttpPost]
    public async Task<ActionResult<PrinterMaintenanceScheduleResponse>> DeployAsync(
        [FromBody] DeployMaintenancePlanRequest request,
        CancellationToken ct)
    {
        // Verify the plan exists
        MaintenancePlan? plan = await _planRepository.GetByIdAsync(request.MaintenancePlanId, ct);
        if (plan == null)
        {
            return NotFound(new { message = "Maintenance plan not found." });
        }

        // Verify the printer exists
        bool printerExists = await _printersRepository.ExistsAsync(request.PrinterId, ct);
        if (!printerExists)
        {
            return NotFound(new { message = "Printer not found." });
        }

        // Check for duplicate deployment
        bool exists = await _scheduleRepository.ExistsAsync(request.MaintenancePlanId, request.PrinterId, ct);
        if (exists)
        {
            return Conflict(new { message = "This plan is already deployed to this printer." });
        }

        var schedule = new PrinterMaintenanceSchedule
        {
            Id = Guid.NewGuid(),
            MaintenancePlanId = request.MaintenancePlanId,
            PrinterId = request.PrinterId,
            IsActive = true,
            DeployedAt = DateTime.UtcNow,
            Notes = request.Notes,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        try
        {
            await _scheduleRepository.AddAsync(schedule, ct);
        }
        catch (DbUpdateException)
        {
            // TOCTOU race: another request deployed the same plan concurrently
            return Conflict(new { message = "This plan is already deployed to this printer." });
        }

        _logger.LogInformation("Deployed plan {PlanId} to printer {PrinterId} as schedule {ScheduleId}", request.MaintenancePlanId, request.PrinterId, schedule.Id);

        // Reload with navigation properties
        PrinterMaintenanceSchedule? created = await _scheduleRepository.GetByIdAsync(schedule.Id, ct);
        return Created($"/api/maintenance/schedules/{schedule.Id}", ToResponse(created!));
    }

    /// <summary>
    /// Updates a schedule deployment (activate/deactivate, change notes).
    /// </summary>
    [HttpPut("{id:guid}")]
    public async Task<ActionResult<PrinterMaintenanceScheduleResponse>> UpdateAsync(
        Guid id,
        [FromBody] UpdateScheduleDeploymentRequest request,
        CancellationToken ct)
    {
        PrinterMaintenanceSchedule? schedule = await _scheduleRepository.GetByIdAsync(id, ct);
        if (schedule == null)
        {
            return NotFound();
        }

        schedule.IsActive = request.IsActive;
        schedule.Notes = request.Notes;
        schedule.UpdatedAt = DateTime.UtcNow;

        await _scheduleRepository.UpdateAsync(schedule, ct);
        _logger.LogInformation("Updated schedule {ScheduleId} IsActive={IsActive}", schedule.Id, schedule.IsActive);

        return Ok(ToResponse(schedule));
    }

    /// <summary>
    /// Removes a schedule deployment (undeploys a plan from a printer).
    /// </summary>
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> DeleteAsync(Guid id, CancellationToken ct)
    {
        PrinterMaintenanceSchedule? schedule = await _scheduleRepository.GetByIdAsync(id, ct);
        if (schedule == null)
        {
            return NotFound();
        }

        await _scheduleRepository.DeleteAsync(schedule, ct);
        _logger.LogInformation("Undeployed schedule {ScheduleId} (plan {PlanId} from printer {PrinterId})", schedule.Id, schedule.MaintenancePlanId, schedule.PrinterId);

        return NoContent();
    }
}
