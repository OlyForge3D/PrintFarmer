using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Maintenance;
using Farm.Web.Api.Controllers.Requests;
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

    // ───────────────────────── Plans ─────────────────────────

    /// <summary>
    /// Gets all maintenance plans. Optionally filter by active status.
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<List<MaintenancePlan>>> GetAllAsync(
        [FromQuery] bool? activeOnly,
        CancellationToken ct)
    {
        List<MaintenancePlan> plans = await _planRepository.GetAllAsync(activeOnly, ct);
        return Ok(plans);
    }

    /// <summary>
    /// Gets a maintenance plan by ID, including its tasks and their components.
    /// </summary>
    [HttpGet("{id:guid}")]
    public async Task<ActionResult<MaintenancePlan>> GetByIdAsync(Guid id, CancellationToken ct)
    {
        MaintenancePlan? plan = await _planRepository.GetByIdAsync(id, ct);
        if (plan == null)
        {
            return NotFound();
        }

        return Ok(plan);
    }

    /// <summary>
    /// Gets all plans applicable to a specific printer.
    /// </summary>
    [HttpGet("for-printer/{printerId:guid}")]
    public async Task<ActionResult<List<MaintenancePlan>>> GetForPrinterAsync(Guid printerId, CancellationToken ct)
    {
        List<MaintenancePlan> plans = await _planRepository.GetPlansForPrinterAsync(printerId, ct);
        return Ok(plans);
    }

    /// <summary>
    /// Creates a new maintenance plan.
    /// </summary>
    [HttpPost]
    public async Task<ActionResult<MaintenancePlan>> CreateAsync(
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

        return Created($"/api/maintenance/plans/{plan.Id}", plan);
    }

    /// <summary>
    /// Updates an existing maintenance plan.
    /// </summary>
    [HttpPut("{id:guid}")]
    public async Task<ActionResult<MaintenancePlan>> UpdateAsync(
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

        return Ok(plan);
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
    /// Gets all tasks for a plan.
    /// </summary>
    [HttpGet("{planId:guid}/tasks")]
    public async Task<ActionResult<List<MaintenanceTask>>> GetTasksAsync(Guid planId, CancellationToken ct)
    {
        MaintenancePlan? plan = await _planRepository.GetByIdAsync(planId, ct);
        if (plan == null)
        {
            return NotFound();
        }

        List<MaintenanceTask> tasks = await _taskRepository.GetByPlanIdAsync(planId, ct);
        return Ok(tasks);
    }

    /// <summary>
    /// Gets a single task by ID.
    /// </summary>
    [HttpGet("{planId:guid}/tasks/{taskId:guid}")]
    public async Task<ActionResult<MaintenanceTask>> GetTaskAsync(Guid planId, Guid taskId, CancellationToken ct)
    {
        MaintenanceTask? task = await _taskRepository.GetByIdAsync(taskId, ct);
        if (task == null || task.MaintenancePlanId != planId)
        {
            return NotFound();
        }

        return Ok(task);
    }

    /// <summary>
    /// Creates a new task in a plan.
    /// </summary>
    [HttpPost("{planId:guid}/tasks")]
    public async Task<ActionResult<MaintenanceTask>> CreateTaskAsync(
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
            MaintenancePlanId = planId,
            TaskName = request.TaskName,
            Description = request.Description,
            IntervalHours = request.IntervalHours,
            IntervalDays = request.IntervalDays,
            EstimatedDurationMinutes = request.EstimatedDurationMinutes,
            Priority = request.Priority,
            IsActive = request.IsActive,
            SortOrder = request.SortOrder,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        await _taskRepository.AddAsync(task, ct);
        _logger.LogInformation("Created task {TaskId} '{TaskName}' in plan {PlanId}", task.Id, task.TaskName, planId);

        return Created($"/api/maintenance/plans/{planId}/tasks/{task.Id}", task);
    }

    /// <summary>
    /// Updates a task.
    /// </summary>
    [HttpPut("{planId:guid}/tasks/{taskId:guid}")]
    public async Task<ActionResult<MaintenanceTask>> UpdateTaskAsync(
        Guid planId,
        Guid taskId,
        [FromBody] UpdateMaintenanceTaskRequest request,
        CancellationToken ct)
    {
        MaintenanceTask? task = await _taskRepository.GetByIdAsync(taskId, ct);
        if (task == null || task.MaintenancePlanId != planId)
        {
            return NotFound();
        }

        task.TaskName = request.TaskName;
        task.Description = request.Description;
        task.IntervalHours = request.IntervalHours;
        task.IntervalDays = request.IntervalDays;
        task.EstimatedDurationMinutes = request.EstimatedDurationMinutes;
        task.Priority = request.Priority;
        task.IsActive = request.IsActive;
        task.SortOrder = request.SortOrder;
        task.UpdatedAt = DateTime.UtcNow;

        await _taskRepository.UpdateAsync(task, ct);
        _logger.LogInformation("Updated task {TaskId} '{TaskName}'", task.Id, task.TaskName);

        return Ok(task);
    }

    /// <summary>
    /// Deletes a task (cascades to component associations).
    /// </summary>
    [HttpDelete("{planId:guid}/tasks/{taskId:guid}")]
    public async Task<IActionResult> DeleteTaskAsync(Guid planId, Guid taskId, CancellationToken ct)
    {
        MaintenanceTask? task = await _taskRepository.GetByIdAsync(taskId, ct);
        if (task == null || task.MaintenancePlanId != planId)
        {
            return NotFound();
        }

        await _taskRepository.DeleteAsync(task, ct);
        _logger.LogInformation("Deleted task {TaskId} from plan {PlanId}", taskId, planId);

        return NoContent();
    }

    // ───────────────────── Task Components ───────────────────

    /// <summary>
    /// Gets component associations for a task.
    /// </summary>
    [HttpGet("{planId:guid}/tasks/{taskId:guid}/components")]
    public async Task<ActionResult<List<MaintenanceTaskComponent>>> GetTaskComponentsAsync(
        Guid planId,
        Guid taskId,
        CancellationToken ct)
    {
        MaintenanceTask? task = await _taskRepository.GetByIdAsync(taskId, ct);
        if (task == null || task.MaintenancePlanId != planId)
        {
            return NotFound();
        }

        List<MaintenanceTaskComponent> components = await _taskRepository.GetTaskComponentsAsync(taskId, ct);
        return Ok(components);
    }

    /// <summary>
    /// Adds a component to a task.
    /// </summary>
    [HttpPost("{planId:guid}/tasks/{taskId:guid}/components")]
    public async Task<ActionResult<MaintenanceTaskComponent>> AddTaskComponentAsync(
        Guid planId,
        Guid taskId,
        [FromBody] AddTaskComponentRequest request,
        CancellationToken ct)
    {
        MaintenanceTask? task = await _taskRepository.GetByIdAsync(taskId, ct);
        if (task == null || task.MaintenancePlanId != planId)
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

        return Created($"/api/maintenance/plans/{planId}/tasks/{taskId}/components", taskComponent);
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
        if (task == null || task.MaintenancePlanId != planId)
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
