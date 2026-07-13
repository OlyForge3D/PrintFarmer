using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Tasks;
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
public class TasksController(IUserTaskService taskService, IValidator<CreateManualTaskDto> createManualTaskValidator) : ControllerBase
{
    private readonly IUserTaskService _taskService = taskService ?? throw new ArgumentNullException(nameof(taskService));
    private readonly IValidator<CreateManualTaskDto> _createManualTaskValidator = createManualTaskValidator ?? throw new ArgumentNullException(nameof(createManualTaskValidator));

    /// <summary>
    /// Gets all pending tasks, or the shift-plan grouped view when <c>view=shift</c>.
    /// </summary>
    /// <remarks>
    /// The default (no <c>view</c> parameter) preserves the existing flat list contract.
    /// <c>view=shift</c> returns a <see cref="ShiftPlanDto"/> with anchor-grouped/ordered
    /// tasks per issue #713. Unknown <c>view</c> values fall back to the flat list.
    /// </remarks>
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<UserTaskDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ShiftPlanDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetPendingTasksAsync([FromQuery] string? view, CancellationToken ct)
    {
        if (string.Equals(view, "shift", StringComparison.OrdinalIgnoreCase))
        {
            ShiftPlanDto plan = await _taskService.GetShiftPlanAsync(ct);
            return Ok(plan);
        }

        IReadOnlyList<UserTaskDto> tasks = await _taskService.GetPendingTasksAsync(ct);
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

        return Ok(task);
    }

    /// <summary>
    /// Gets the count of pending tasks.
    /// </summary>
    [HttpGet("count")]
    [ProducesResponseType(typeof(PendingTaskCountDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<PendingTaskCountDto>> GetPendingCountAsync(CancellationToken ct)
    {
        int count = await _taskService.GetPendingCountAsync(ct);
        return Ok(new PendingTaskCountDto(count));
    }

    /// <summary>
    /// Marks a task as complete.
    /// </summary>
    [HttpPost("{id:guid}/complete")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> CompleteAsync(Guid id, CancellationToken ct)
    {
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
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DismissAsync(Guid id, CancellationToken ct)
    {
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
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> SkipAsync(Guid id, CancellationToken ct)
    {
        bool success = await _taskService.SkipTaskAsync(id, ct);
        if (!success)
        {
            return NotFound();
        }

        return NoContent();
    }
}

/// <summary>
/// Response DTO for pending task count.
/// </summary>
public record PendingTaskCountDto(int Count);
