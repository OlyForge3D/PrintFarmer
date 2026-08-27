using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Idempotency;
using Farm.Infrastructure.Services.OperatorFeatures;
using Farm.Infrastructure.Services.Tasks;
using Farm.Web.Api.Infrastructure.Idempotency;
using Farm.Web.Api.Infrastructure.OperatorFeatures;
using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Farm.Web.Api.Controllers;

/// <summary>
/// Controller for managing user tasks.
/// </summary>
[ApiController]
[Route("api/tasks")]
[Authorize]
public class TasksController(
    IUserTaskService taskService,
    IOperatorFeatureGate featureGate,
    IValidator<CreateManualTaskDto> createManualTaskValidator) : ControllerBase
{
    private readonly IUserTaskService _taskService = taskService ?? throw new ArgumentNullException(nameof(taskService));
    private readonly IOperatorFeatureGate _featureGate = featureGate ?? throw new ArgumentNullException(nameof(featureGate));
    private readonly IValidator<CreateManualTaskDto> _createManualTaskValidator = createManualTaskValidator ?? throw new ArgumentNullException(nameof(createManualTaskValidator));

    // Fix 7: returns a not-found result when the shift-plan feature is disabled.
    private NotFoundObjectResult? ShiftPlanDisabledResult()
        => _featureGate.IsEnabled(OperatorFeature.ShiftPlan)
            ? null
            : OperatorFeatureProblemDetails.NotFound(_featureGate, OperatorFeature.ShiftPlan);

    // Fix 8: true when the caller holds the farm_admin role.
    private bool IsAdmin => User.IsInRole("farm_admin");

    /// <summary>
    /// Gets all pending tasks, or the shift-plan grouped view when <c>view=shift</c>.
    /// </summary>
    /// <remarks>
    /// The default (no <c>view</c> parameter) preserves the existing flat list contract.
    /// <c>view=shift</c> returns a <see cref="ShiftPlanDto"/> with anchor-grouped/ordered
    /// tasks per issue #713 (Now → Timeline → AnytimeToday). Requires the shift-plan
    /// feature to be enabled; returns 404 with code=featureDisabled otherwise.
    /// Maintenance-sourced tasks are included only for <c>farm_admin</c> callers.
    /// Unknown <c>view</c> values fall back to the flat list.
    /// </remarks>
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<UserTaskDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ShiftPlanDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetPendingTasksAsync([FromQuery] string? view, CancellationToken ct)
    {
        if (string.Equals(view, "shift", StringComparison.OrdinalIgnoreCase))
        {
            // Fix 7: gate the shift-plan view behind the feature flag.
            if (ShiftPlanDisabledResult() is NotFoundObjectResult disabled)
            {
                return disabled;
            }

            // Fix 8: pass admin status so maintenance tasks are included/excluded.
            ShiftPlanDto plan = await _taskService.GetShiftPlanAsync(IsAdmin, ct);
            return Ok(plan);
        }

        IReadOnlyList<UserTaskDto> tasks = await _taskService.GetPendingTasksAsync(IsAdmin, ct);
        return Ok(tasks);
    }

    /// <summary>
    /// Creates a new manual task.
    /// </summary>
    [HttpPost]
    [ProducesResponseType(typeof(UserTaskDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<UserTaskDto>> CreateManualTaskAsync([FromBody] CreateManualTaskDto dto, CancellationToken ct)
    {
        FluentValidation.Results.ValidationResult validationResult = await _createManualTaskValidator.ValidateAsync(dto, ct);
        if (!validationResult.IsValid)
        {
            return BadRequest(validationResult.Errors);
        }

        UserTaskDto task = await _taskService.CreateManualTaskAsync(dto, ct);
        return CreatedAtAction("GetById", new { id = task.Id }, task);
    }

    /// <summary>
    /// Gets a task by ID.
    /// </summary>
    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(UserTaskDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<UserTaskDto>> GetByIdAsync(Guid id, CancellationToken ct)
    {
        UserTaskDto? task = await _taskService.GetByIdAsync(id, ct);
        if (task == null)
        {
            return NotFound();
        }

        // Fix 8: maintenance tasks are invisible to non-admin callers.
        if (task.SourceKind == UserTaskSourceKind.Maintenance && !IsAdmin)
        {
            return NotFound();
        }

        return Ok(task);
    }

    /// <summary>
    /// Gets the count of pending tasks.
    /// </summary>
    [HttpGet("count")]
    [ProducesResponseType(typeof(PendingTaskCountDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<PendingTaskCountDto>> GetPendingCountAsync(CancellationToken ct)
    {
        // Fix 8/B: non-admin callers get a count that excludes maintenance tasks so
        // it matches the list they can actually see.
        int count = await _taskService.GetPendingCountAsync(IsAdmin, ct);
        return Ok(new PendingTaskCountDto(count));
    }

    /// <summary>
    /// Marks a task as complete.
    /// </summary>
    [HttpPost("{id:guid}/complete")]
    [Idempotent(IdempotencyRouteKeys.TaskComplete)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> CompleteAsync(Guid id, CancellationToken ct)
    {
        // Fix 8: block non-admin mutations on maintenance tasks.
        if (await IsMaintenanceTaskForNonAdminAsync(id, ct) is IActionResult guard)
        {
            return guard;
        }

        bool success = await _taskService.CompleteTaskAsync(id, ct);
        if (!success)
        {
            return NotFound();
        }

        return NoContent();
    }

    /// <summary>
    /// Dismisses a task.
    /// </summary>
    [HttpPost("{id:guid}/dismiss")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DismissAsync(Guid id, CancellationToken ct)
    {
        // Fix 8: block non-admin mutations on maintenance tasks.
        if (await IsMaintenanceTaskForNonAdminAsync(id, ct) is IActionResult guard)
        {
            return guard;
        }

        string? userIdStr = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        Guid? userId = Guid.TryParse(userIdStr, out Guid parsed) ? parsed : null;
        bool success = await _taskService.DismissTaskAsync(id, userId, ct);
        if (!success)
        {
            return NotFound();
        }

        return NoContent();
    }

    /// <summary>
    /// Skips a task (for later).
    /// </summary>
    [HttpPost("{id:guid}/skip")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> SkipAsync(Guid id, CancellationToken ct)
    {
        // Fix 8: block non-admin mutations on maintenance tasks.
        if (await IsMaintenanceTaskForNonAdminAsync(id, ct) is IActionResult guard)
        {
            return guard;
        }

        bool success = await _taskService.SkipTaskAsync(id, ct);
        if (!success)
        {
            return NotFound();
        }

        return NoContent();
    }

    /// <summary>
    /// Returns a <see cref="ForbidResult"/> when <paramref name="id"/> is a
    /// maintenance task and the caller is not an admin; <c>null</c> otherwise.
    /// </summary>
    private async Task<IActionResult?> IsMaintenanceTaskForNonAdminAsync(Guid id, CancellationToken ct)
    {
        if (IsAdmin)
        {
            return null;
        }

        UserTaskDto? task = await _taskService.GetByIdAsync(id, ct);
        if (task?.SourceKind == UserTaskSourceKind.Maintenance)
        {
            return Forbid();
        }

        return null;
    }
}

/// <summary>
/// Response DTO for pending task count.
/// </summary>
public record PendingTaskCountDto(int Count);
