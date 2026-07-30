using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Maintenance;
using Farm.Infrastructure.Repositories.Printers;
using Farm.Infrastructure.Services.OperatorFeatures;
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
    IPrintersRepository printersRepository,
    IOperatorFeatureGate featureGate)
    : ControllerBase
{
    private readonly ILogger<MaintenanceScheduleDeploymentController> _logger = logger;
    private readonly IPrinterMaintenanceScheduleRepository _scheduleRepository = scheduleRepository;
    private readonly IMaintenancePlanRepository _planRepository = planRepository;
    private readonly IPrintersRepository _printersRepository = printersRepository;
    private readonly IOperatorFeatureGate _featureGate = featureGate;

    private static PrinterMaintenanceScheduleResponse ToResponse(PrinterMaintenanceSchedule s) => new(
        s.Id,
        s.MaintenancePlanId,
        s.MaintenancePlan?.Name ?? string.Empty,
        s.PrinterId,
        s.Printer?.Name,
        s.ToolheadId,
        s.Toolhead?.Name,
        s.IsActive,
        s.DeployedAt,
        s.Notes,
        s.CreatedAt,
        s.UpdatedAt);

    private static bool UsesPrintHourIntervals(MaintenancePlan plan) =>
        plan.PlanTasks.Any(planTask =>
            (planTask.IntervalHoursOverride ?? planTask.MaintenanceTask.IntervalHours).HasValue);

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
        if (!await _featureGate.IsEnabledAsync(OperatorFeature.MultiSlotFallback, ct).ConfigureAwait(false))
        {
            schedules = schedules.Where(s => !s.ToolheadId.HasValue).ToList();
        }

        return Ok(schedules.Select(ToResponse).ToList());
    }

    /// <summary>
    /// Gets a schedule deployment by ID.
    /// </summary>
    [HttpGet("{id:guid}")]
    public async Task<ActionResult<PrinterMaintenanceScheduleResponse>> GetByIdAsync(Guid id, CancellationToken ct)
    {
        PrinterMaintenanceSchedule? schedule = await _scheduleRepository.GetByIdAsync(id, ct);
        if (schedule == null
            || (schedule.ToolheadId.HasValue
                && !await _featureGate.IsEnabledAsync(OperatorFeature.MultiSlotFallback, ct).ConfigureAwait(false)))
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
        if (request.ToolheadId.HasValue
            && !await _featureGate.IsEnabledAsync(OperatorFeature.MultiSlotFallback, ct).ConfigureAwait(false))
        {
            return BadRequest(new { message = "Per-tool maintenance is disabled." });
        }

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

        // Validate optional per-toolhead scope (issue #711, F6). Null preserves legacy
        // printer-wide semantics. When set, the toolhead must belong to the target printer
        // and be a physical dock — MMU/AMS gates are not eligible for maintenance scope.
        if (request.ToolheadId.HasValue)
        {
            Printer? printerWithToolheads = await _printersRepository.FindByIdWithToolheadsAsync(request.PrinterId, ct);
            Toolhead? toolhead = printerWithToolheads?.Toolheads
                .FirstOrDefault(t => t.Id == request.ToolheadId.Value);

            if (toolhead is null)
            {
                return BadRequest(new { message = $"Toolhead {request.ToolheadId} does not belong to printer {request.PrinterId}." });
            }

            if (toolhead.ToolheadType != ToolheadType.Physical)
            {
                return BadRequest(new { message = $"Toolhead {request.ToolheadId} is not a physical toolhead and is not eligible for maintenance scope." });
            }

            if (!printerWithToolheads!.SupportsPerToolAttribution
                && UsesPrintHourIntervals(plan))
            {
                return BadRequest(new
                {
                    message = "Hour-based per-tool maintenance requires a printer that supports per-tool attribution. Use a calendar-based plan or printer-wide scope."
                });
            }
        }

        // Check for duplicate deployment within the same toolhead scope (null = printer-wide).
        bool exists = await _scheduleRepository.ExistsAsync(request.MaintenancePlanId, request.PrinterId, request.ToolheadId, ct);
        if (exists)
        {
            return Conflict(new { message = "This plan is already deployed to this printer for the requested scope." });
        }

        var schedule = new PrinterMaintenanceSchedule
        {
            Id = Guid.NewGuid(),
            MaintenancePlanId = request.MaintenancePlanId,
            PrinterId = request.PrinterId,
            ToolheadId = request.ToolheadId,
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
            // TOCTOU race: another request deployed the same plan/scope concurrently. The
            // unique indexes (composite for toolhead-scoped, filtered for printer-wide) reject
            // the duplicate at the database level.
            return Conflict(new { message = "This plan is already deployed to this printer for the requested scope." });
        }

        _logger.LogInformation(
            "Deployed plan {PlanId} to printer {PrinterId} (toolhead {ToolheadId}) as schedule {ScheduleId}",
            request.MaintenancePlanId,
            request.PrinterId,
            request.ToolheadId,
            schedule.Id);

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

        if (schedule.ToolheadId.HasValue
            && !await _featureGate.IsEnabledAsync(OperatorFeature.MultiSlotFallback, ct).ConfigureAwait(false))
        {
            return BadRequest(new { message = "Per-tool maintenance is disabled." });
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

        if (schedule.ToolheadId.HasValue
            && !await _featureGate.IsEnabledAsync(OperatorFeature.MultiSlotFallback, ct).ConfigureAwait(false))
        {
            return BadRequest(new { message = "Per-tool maintenance is disabled." });
        }

        await _scheduleRepository.DeleteAsync(schedule, ct);
        _logger.LogInformation("Undeployed schedule {ScheduleId} (plan {PlanId} from printer {PrinterId})", schedule.Id, schedule.MaintenancePlanId, schedule.PrinterId);

        return NoContent();
    }
}
