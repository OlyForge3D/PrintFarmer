using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Maintenance;
using Farm.Web.Api.Controllers.Requests;
using Farm.Web.Api.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Farm.Web.Api.Controllers;

/// <summary>
/// API controller for hierarchical maintenance plans and their tasks.
/// Plans group recurring maintenance tasks that can be scoped to a printer, model, manufacturer, or motion type.
/// </summary>
[ApiController]
[Route("api/maintenance/plans")]
[Authorize(Roles = "farm_admin")]
public class MaintenancePlanController(
    ILogger<MaintenancePlanController> logger,
    IMaintenancePlanRepository planRepository,
    IMaintenanceTaskRepository taskRepository)
    : ControllerBase
{
    private readonly ILogger<MaintenancePlanController> _logger = logger;
    private readonly IMaintenancePlanRepository _planRepository = planRepository;
    private readonly IMaintenanceTaskRepository _taskRepository = taskRepository;

    // ───────────────────── Mapping Helpers ─────────────────────
    private static MaintenancePlanResponse ToResponse(MaintenancePlan plan) => new(
        plan.Id,
        plan.Name,
        plan.Description,
        plan.PrinterId,
        plan.Printer?.Name,
        plan.PrinterModelId,
        plan.PrinterModel?.Name,
        plan.ManufacturerId,
        plan.Manufacturer?.Name,
        plan.MotionType,
        plan.IsActive,
        plan.IsDefault,
        plan.CreatedAt,
        plan.UpdatedAt,
        plan.PlanTasks.Select(ToPlanTaskResponse).ToList());

    private static PlanTaskResponse ToPlanTaskResponse(PlanTask pt) => new(
        pt.Id,
        pt.MaintenancePlanId,
        pt.MaintenanceTaskId,
        pt.SortOrder,
        pt.IntervalHoursOverride,
        pt.IntervalDaysOverride,
        ToTaskResponse(pt.MaintenanceTask));

    private static MaintenanceTaskResponse ToTaskResponse(MaintenanceTask task) => new(
        task.Id,
        task.TaskName,
        task.Description,
        task.Category,
        task.IntervalHours,
        task.IntervalDays,
        task.EstimatedDurationMinutes,
        task.Priority,
        task.IsActive,
        task.IsDefault,
        task.RequiresEnclosure,
        task.RequiresCarbonFilter,
        task.RequiresHepaFilter,
        task.RequiresBowdenTube,
        task.RequiresPtfeLiner,
        task.RequiresLinearRails,
        task.RequiresLeadScrews,
        task.RequiresToolchanger,
        task.RequiresFilamentCutter,
        task.RequiresHeatedChamber,
        task.RequiresHeatedBed,
        task.RequiresMultiMaterial,
        task.CreatedAt,
        task.UpdatedAt,
        task.TaskComponents.Select(ToTaskComponentResponse).ToList());

    private static MaintenanceTaskComponentResponse ToTaskComponentResponse(MaintenanceTaskComponent tc) => new(
        tc.Id,
        tc.MaintenanceTaskId,
        tc.MaintenanceComponentId,
        tc.MaintenanceComponent?.Name,
        tc.Quantity,
        tc.Notes);

    // ───────────────────────── Plans ─────────────────────────

    /// <summary>
    /// Gets all maintenance plans. Optionally filter by active status.
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<List<MaintenancePlanResponse>>> GetAllAsync(
        [FromQuery] bool? activeOnly,
        CancellationToken ct)
    {
        List<MaintenancePlan> plans = await _planRepository.GetAllAsync(activeOnly, ct);
        return Ok(plans.Select(ToResponse).ToList());
    }

    /// <summary>
    /// Gets a maintenance plan by ID, including its tasks and their components.
    /// </summary>
    [HttpGet("{id:guid}")]
    public async Task<ActionResult<MaintenancePlanResponse>> GetByIdAsync(Guid id, CancellationToken ct)
    {
        MaintenancePlan? plan = await _planRepository.GetByIdAsync(id, ct);
        if (plan == null)
        {
            return NotFound();
        }

        return Ok(ToResponse(plan));
    }

    /// <summary>
    /// Gets all plans applicable to a specific printer.
    /// </summary>
    [HttpGet("for-printer/{printerId:guid}")]
    public async Task<ActionResult<List<MaintenancePlanResponse>>> GetForPrinterAsync(Guid printerId, CancellationToken ct)
    {
        List<MaintenancePlan> plans = await _planRepository.GetPlansForPrinterAsync(printerId, ct);
        return Ok(plans.Select(ToResponse).ToList());
    }

    /// <summary>
    /// Creates a new maintenance plan.
    /// </summary>
    [HttpPost]
    public async Task<ActionResult<MaintenancePlanResponse>> CreateAsync(
        [FromBody] CreateMaintenancePlanRequest request,
        CancellationToken ct)
    {
        var plan = new MaintenancePlan
        {
            Id = Guid.NewGuid(),
            Name = request.Name,
            Description = request.Description,
            PrinterId = request.PrinterId,
            PrinterModelId = request.PrinterModelId,
            ManufacturerId = request.ManufacturerId,
            MotionType = request.MotionType,
            IsActive = request.IsActive,
            IsDefault = request.IsDefault,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        await _planRepository.AddAsync(plan, ct);
        _logger.LogInformation("Created maintenance plan {PlanId} '{PlanName}'", plan.Id, plan.Name);

        return Created($"/api/maintenance/plans/{plan.Id}", ToResponse(plan));
    }

    /// <summary>
    /// Updates an existing maintenance plan.
    /// </summary>
    [HttpPut("{id:guid}")]
    public async Task<ActionResult<MaintenancePlanResponse>> UpdateAsync(
        Guid id,
        [FromBody] UpdateMaintenancePlanRequest request,
        CancellationToken ct)
    {
        MaintenancePlan? plan = await _planRepository.GetByIdAsync(id, ct);
        if (plan == null)
        {
            return NotFound();
        }

        plan.Name = request.Name;
        plan.Description = request.Description;
        plan.PrinterId = request.PrinterId;
        plan.PrinterModelId = request.PrinterModelId;
        plan.ManufacturerId = request.ManufacturerId;
        plan.MotionType = request.MotionType;
        plan.IsActive = request.IsActive;
        plan.IsDefault = request.IsDefault;
        plan.UpdatedAt = DateTime.UtcNow;

        await _planRepository.UpdateAsync(plan, ct);
        _logger.LogInformation("Updated maintenance plan {PlanId} '{PlanName}'", plan.Id, plan.Name);

        return Ok(ToResponse(plan));
    }

    /// <summary>
    /// Deletes a maintenance plan and all its tasks (cascade).
    /// </summary>
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> DeleteAsync(Guid id, CancellationToken ct)
    {
        MaintenancePlan? plan = await _planRepository.GetByIdAsync(id, ct);
        if (plan == null)
        {
            return NotFound();
        }

        await _planRepository.DeleteAsync(plan, ct);
        _logger.LogInformation("Deleted maintenance plan {PlanId} '{PlanName}'", plan.Id, plan.Name);

        return NoContent();
    }

    // ───────────────────────── Tasks ─────────────────────────

    /// <summary>
    /// Gets all tasks linked to a plan (via PlanTask join).
    /// </summary>
    [HttpGet("{planId:guid}/tasks")]
    public async Task<ActionResult<List<PlanTaskResponse>>> GetTasksAsync(Guid planId, CancellationToken ct)
    {
        MaintenancePlan? plan = await _planRepository.GetByIdAsync(planId, ct);
        if (plan == null)
        {
            return NotFound();
        }

        return Ok(plan.PlanTasks.OrderBy(pt => pt.SortOrder).Select(ToPlanTaskResponse).ToList());
    }

    /// <summary>
    /// Gets a single task by ID (global catalog lookup).
    /// </summary>
    [HttpGet("{planId:guid}/tasks/{taskId:guid}")]
    public async Task<ActionResult<MaintenanceTaskResponse>> GetTaskAsync(Guid planId, Guid taskId, CancellationToken ct)
    {
        MaintenancePlan? plan = await _planRepository.GetByIdAsync(planId, ct);
        if (plan == null)
        {
            return NotFound();
        }

        PlanTask? planTask = plan.PlanTasks.FirstOrDefault(pt => pt.MaintenanceTaskId == taskId);
        if (planTask == null)
        {
            return NotFound();
        }

        return Ok(ToTaskResponse(planTask.MaintenanceTask));
    }

    /// <summary>
    /// Creates a new task in the global catalog and links it to the plan.
    /// </summary>
    [HttpPost("{planId:guid}/tasks")]
    public async Task<ActionResult<MaintenanceTaskResponse>> CreateTaskAsync(
        Guid planId,
        [FromBody] CreateMaintenanceTaskRequest request,
        CancellationToken ct)
    {
        MaintenancePlan? plan = await _planRepository.GetByIdAsync(planId, ct);
        if (plan == null)
        {
            return NotFound();
        }

        var task = new MaintenanceTask
        {
            Id = Guid.NewGuid(),
            TaskName = request.TaskName,
            Description = request.Description,
            Category = request.Category,
            IntervalHours = request.IntervalHours,
            IntervalDays = request.IntervalDays,
            EstimatedDurationMinutes = request.EstimatedDurationMinutes,
            Priority = request.Priority,
            IsActive = request.IsActive,
            IsDefault = request.IsDefault,
            RequiresEnclosure = request.RequiresEnclosure,
            RequiresCarbonFilter = request.RequiresCarbonFilter,
            RequiresHepaFilter = request.RequiresHepaFilter,
            RequiresBowdenTube = request.RequiresBowdenTube,
            RequiresPtfeLiner = request.RequiresPtfeLiner,
            RequiresLinearRails = request.RequiresLinearRails,
            RequiresLeadScrews = request.RequiresLeadScrews,
            RequiresToolchanger = request.RequiresToolchanger,
            RequiresFilamentCutter = request.RequiresFilamentCutter,
            RequiresHeatedChamber = request.RequiresHeatedChamber,
            RequiresHeatedBed = request.RequiresHeatedBed,
            RequiresMultiMaterial = request.RequiresMultiMaterial,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        await _taskRepository.AddAsync(task, ct);

        // Link task to plan via PlanTask join
        int maxSort = plan.PlanTasks.Count > 0 ? plan.PlanTasks.Max(pt => pt.SortOrder) : 0;
        plan.PlanTasks.Add(new PlanTask
        {
            Id = Guid.NewGuid(),
            MaintenancePlanId = planId,
            MaintenanceTaskId = task.Id,
            SortOrder = maxSort + 1
        });
        await _planRepository.SaveChangesAsync(ct);

        _logger.LogInformation("Created task {TaskId} '{TaskName}' and linked to plan {PlanId}", task.Id, task.TaskName, planId);

        return Created($"/api/maintenance/plans/{planId}/tasks/{task.Id}", ToTaskResponse(task));
    }

    /// <summary>
    /// Updates a task in the global catalog.
    /// </summary>
    [HttpPut("{planId:guid}/tasks/{taskId:guid}")]
    public async Task<ActionResult<MaintenanceTaskResponse>> UpdateTaskAsync(
        Guid planId,
        Guid taskId,
        [FromBody] UpdateMaintenanceTaskRequest request,
        CancellationToken ct)
    {
        MaintenanceTask? task = await _taskRepository.GetByIdAsync(taskId, ct);
        if (task == null)
        {
            return NotFound();
        }

        task.TaskName = request.TaskName;
        task.Description = request.Description;
        task.Category = request.Category;
        task.IntervalHours = request.IntervalHours;
        task.IntervalDays = request.IntervalDays;
        task.EstimatedDurationMinutes = request.EstimatedDurationMinutes;
        task.Priority = request.Priority;
        task.IsActive = request.IsActive;
        task.IsDefault = request.IsDefault;
        task.RequiresEnclosure = request.RequiresEnclosure;
        task.RequiresCarbonFilter = request.RequiresCarbonFilter;
        task.RequiresHepaFilter = request.RequiresHepaFilter;
        task.RequiresBowdenTube = request.RequiresBowdenTube;
        task.RequiresPtfeLiner = request.RequiresPtfeLiner;
        task.RequiresLinearRails = request.RequiresLinearRails;
        task.RequiresLeadScrews = request.RequiresLeadScrews;
        task.RequiresToolchanger = request.RequiresToolchanger;
        task.RequiresFilamentCutter = request.RequiresFilamentCutter;
        task.RequiresHeatedChamber = request.RequiresHeatedChamber;
        task.RequiresHeatedBed = request.RequiresHeatedBed;
        task.RequiresMultiMaterial = request.RequiresMultiMaterial;
        task.UpdatedAt = DateTime.UtcNow;

        await _taskRepository.UpdateAsync(task, ct);
        _logger.LogInformation("Updated task {TaskId} '{TaskName}'", task.Id, task.TaskName);

        return Ok(ToTaskResponse(task));
    }

    /// <summary>
    /// Removes a task from a plan (deletes PlanTask link). Does not delete the global task.
    /// </summary>
    [HttpDelete("{planId:guid}/tasks/{taskId:guid}")]
    public async Task<IActionResult> DeleteTaskAsync(Guid planId, Guid taskId, CancellationToken ct)
    {
        MaintenancePlan? plan = await _planRepository.GetByIdAsync(planId, ct);
        if (plan == null)
        {
            return NotFound();
        }

        PlanTask? planTask = plan.PlanTasks.FirstOrDefault(pt => pt.MaintenanceTaskId == taskId);
        if (planTask == null)
        {
            return NotFound();
        }

        plan.PlanTasks.Remove(planTask);
        await _planRepository.SaveChangesAsync(ct);
        _logger.LogInformation("Removed task {TaskId} from plan {PlanId}", taskId, planId);

        return NoContent();
    }

    // ───────────────────── Task Components ───────────────────

    /// <summary>
    /// Gets component associations for a task.
    /// </summary>
    [HttpGet("{planId:guid}/tasks/{taskId:guid}/components")]
    public async Task<ActionResult<List<MaintenanceTaskComponentResponse>>> GetTaskComponentsAsync(
        Guid planId,
        Guid taskId,
        CancellationToken ct)
    {
        MaintenanceTask? task = await _taskRepository.GetByIdAsync(taskId, ct);
        if (task == null)
        {
            return NotFound();
        }

        List<MaintenanceTaskComponent> components = await _taskRepository.GetTaskComponentsAsync(taskId, ct);
        return Ok(components.Select(ToTaskComponentResponse).ToList());
    }

    /// <summary>
    /// Adds a component to a task.
    /// </summary>
    [HttpPost("{planId:guid}/tasks/{taskId:guid}/components")]
    public async Task<ActionResult<MaintenanceTaskComponentResponse>> AddTaskComponentAsync(
        Guid planId,
        Guid taskId,
        [FromBody] AddTaskComponentRequest request,
        CancellationToken ct)
    {
        MaintenanceTask? task = await _taskRepository.GetByIdAsync(taskId, ct);
        if (task == null)
        {
            return NotFound();
        }

        // Check for duplicate
        MaintenanceTaskComponent? existing = await _taskRepository.FindTaskComponentAsync(taskId, request.ComponentId, ct);
        if (existing != null)
        {
            return Conflict(new { message = "Component is already associated with this task." });
        }

        var taskComponent = new MaintenanceTaskComponent
        {
            Id = Guid.NewGuid(),
            MaintenanceTaskId = taskId,
            MaintenanceComponentId = request.ComponentId,
            Quantity = request.Quantity,
            Notes = request.Notes
        };

        await _taskRepository.AddComponentAsync(taskComponent, ct);
        _logger.LogInformation("Added component {ComponentId} to task {TaskId}", request.ComponentId, taskId);

        return Created($"/api/maintenance/plans/{planId}/tasks/{taskId}/components", ToTaskComponentResponse(taskComponent));
    }

    /// <summary>
    /// Removes a component from a task.
    /// </summary>
    [HttpDelete("{planId:guid}/tasks/{taskId:guid}/components/{componentId:guid}")]
    public async Task<IActionResult> RemoveTaskComponentAsync(
        Guid planId,
        Guid taskId,
        Guid componentId,
        CancellationToken ct)
    {
        MaintenanceTask? task = await _taskRepository.GetByIdAsync(taskId, ct);
        if (task == null)
        {
            return NotFound();
        }

        MaintenanceTaskComponent? taskComponent = await _taskRepository.FindTaskComponentAsync(taskId, componentId, ct);
        if (taskComponent == null)
        {
            return NotFound();
        }

        await _taskRepository.RemoveComponentAsync(taskComponent, ct);
        _logger.LogInformation("Removed component {ComponentId} from task {TaskId}", componentId, taskId);

        return NoContent();
    }
}
