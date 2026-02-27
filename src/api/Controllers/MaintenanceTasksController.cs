using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Dtos.Maintenance;
using Farm.Infrastructure.Repositories.Maintenance;
using Farm.Infrastructure.Services.Maintenance;
using Farm.Web.Api.Controllers.Requests;
using Farm.Web.Api.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Farm.Web.Api.Controllers;

/// <summary>
/// Standalone API controller for the global maintenance task catalog.
/// Provides direct CRUD access to tasks without requiring a plan context.
/// </summary>
[ApiController]
[Route("api/maintenance/tasks")]
[Authorize(Roles = "farm_admin")]
public class MaintenanceTasksController(
    ILogger<MaintenanceTasksController> logger,
    IMaintenanceTaskRepository taskRepository,
    IMaintenanceImportExportService importExportService)
    : ControllerBase
{
    private readonly ILogger<MaintenanceTasksController> _logger = logger;
    private readonly IMaintenanceTaskRepository _taskRepository = taskRepository;
    private readonly IMaintenanceImportExportService _importExportService = importExportService;

    // ───────────────────────── Tasks ─────────────────────────

    /// <summary>
    /// Gets all tasks in the global catalog. Optionally filter by category or active status.
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<List<MaintenanceTaskResponse>>> GetAllAsync(
        [FromQuery] string? category,
        [FromQuery] bool? activeOnly,
        CancellationToken ct)
    {
        List<MaintenanceTask> tasks = await _taskRepository.GetAllAsync(category, activeOnly, ct);
        return Ok(tasks.Select(MaintenanceResponseMapper.ToTaskResponse).ToList());
    }

    /// <summary>
    /// Gets a task by ID, including its component associations.
    /// </summary>
    [HttpGet("{id:guid}")]
    public async Task<ActionResult<MaintenanceTaskResponse>> GetByIdAsync(Guid id, CancellationToken ct)
    {
        MaintenanceTask? task = await _taskRepository.GetByIdAsync(id, ct);
        if (task == null)
        {
            return NotFound();
        }

        return Ok(MaintenanceResponseMapper.ToTaskResponse(task));
    }

    /// <summary>
    /// Gets all distinct task categories.
    /// </summary>
    [HttpGet("categories")]
    public async Task<ActionResult<List<string>>> GetCategoriesAsync(CancellationToken ct)
    {
        List<string> categories = await _taskRepository.GetCategoriesAsync(ct);
        return Ok(categories);
    }

    /// <summary>
    /// Creates a new task in the global catalog.
    /// </summary>
    [HttpPost]
    public async Task<ActionResult<MaintenanceTaskResponse>> CreateAsync(
        [FromBody] CreateMaintenanceTaskRequest request,
        CancellationToken ct)
    {
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
        _logger.LogInformation("Created maintenance task {TaskId} '{TaskName}'", task.Id, task.TaskName);

        return Created($"/api/maintenance/tasks/{task.Id}", MaintenanceResponseMapper.ToTaskResponse(task));
    }

    /// <summary>
    /// Updates an existing task in the global catalog.
    /// </summary>
    [HttpPut("{id:guid}")]
    public async Task<ActionResult<MaintenanceTaskResponse>> UpdateAsync(
        Guid id,
        [FromBody] UpdateMaintenanceTaskRequest request,
        CancellationToken ct)
    {
        MaintenanceTask? task = await _taskRepository.GetByIdAsync(id, ct);
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
        _logger.LogInformation("Updated maintenance task {TaskId} '{TaskName}'", task.Id, task.TaskName);

        return Ok(MaintenanceResponseMapper.ToTaskResponse(task));
    }

    /// <summary>
    /// Deletes a task from the global catalog. Fails with 409 if the task is referenced by any plan.
    /// </summary>
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> DeleteAsync(Guid id, CancellationToken ct)
    {
        MaintenanceTask? task = await _taskRepository.GetByIdAsync(id, ct);
        if (task == null)
        {
            return NotFound();
        }

        if (task.PlanTasks.Count > 0)
        {
            return Conflict(new { message = "Cannot delete task because it is referenced by one or more maintenance plans." });
        }

        await _taskRepository.DeleteAsync(task, ct);
        _logger.LogInformation("Deleted maintenance task {TaskId} '{TaskName}'", task.Id, task.TaskName);

        return NoContent();
    }

    // ───────────────────── Task Components ───────────────────

    /// <summary>
    /// Gets component associations for a task.
    /// </summary>
    [HttpGet("{taskId:guid}/components")]
    public async Task<ActionResult<List<MaintenanceTaskComponentResponse>>> GetTaskComponentsAsync(
        Guid taskId,
        CancellationToken ct)
    {
        MaintenanceTask? task = await _taskRepository.GetByIdAsync(taskId, ct);
        if (task == null)
        {
            return NotFound();
        }

        List<MaintenanceTaskComponent> components = await _taskRepository.GetTaskComponentsAsync(taskId, ct);
        return Ok(components.Select(MaintenanceResponseMapper.ToTaskComponentResponse).ToList());
    }

    /// <summary>
    /// Adds a component to a task.
    /// </summary>
    [HttpPost("{taskId:guid}/components")]
    public async Task<ActionResult<MaintenanceTaskComponentResponse>> AddTaskComponentAsync(
        Guid taskId,
        [FromBody] AddTaskComponentRequest request,
        CancellationToken ct)
    {
        MaintenanceTask? task = await _taskRepository.GetByIdAsync(taskId, ct);
        if (task == null)
        {
            return NotFound();
        }

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

        return Created($"/api/maintenance/tasks/{taskId}/components", MaintenanceResponseMapper.ToTaskComponentResponse(taskComponent));
    }

    /// <summary>
    /// Removes a component from a task.
    /// </summary>
    [HttpDelete("{taskId:guid}/components/{componentId:guid}")]
    public async Task<IActionResult> RemoveTaskComponentAsync(
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

    // ───────────────────── Import / Export ────────────────────

    /// <summary>
    /// Exports all tasks as a JSON file.
    /// </summary>
    [HttpGet("export")]
    public async Task<ActionResult<MaintenanceExportEnvelope>> ExportAsync(CancellationToken ct)
    {
        MaintenanceExportEnvelope envelope = await _importExportService.ExportTasksAsync(ct);
        return Ok(envelope);
    }

    /// <summary>
    /// Imports tasks from a JSON file. Name-based matching: existing tasks are updated, new tasks are created.
    /// </summary>
    [HttpPost("import")]
    public async Task<ActionResult<MaintenanceImportResult>> ImportAsync(
        [FromBody] MaintenanceExportEnvelope envelope,
        CancellationToken ct)
    {
        if (envelope.Tasks is not { Count: > 0 })
        {
            return BadRequest(new { message = "No tasks found in import data." });
        }

        MaintenanceImportResult result = await _importExportService.ImportTasksAsync(envelope.Tasks, ct);
        _logger.LogInformation("Task import: {Created} created, {Updated} updated, {Errors} errors",
            result.CreatedCount, result.UpdatedCount, result.ErrorCount);

        return Ok(result);
    }
}
