using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure.Services.Tasks;

/// <summary>
/// DTO for creating a profile import task.
/// </summary>
public record CreateProfileImportTaskDto(
    Guid PrinterModelId,
    string PrinterModelName,
    string ManufacturerName,
    Guid PrinterId);

/// <summary>
/// DTO for creating a manual user task.
/// </summary>
public record CreateManualTaskDto(
    string Title,
    string? Description,
    UserTaskPriority Priority);

/// <summary>
/// DTO representing a user task for API responses.
/// </summary>
/// <remarks>
/// The <see cref="AnchorKind"/> / <see cref="SourceKind"/> fields were added by
/// issue #713 for the shift-plan compiler. Legacy tasks materialize with
/// <c>Unspecified</c> for both — this is compatible with existing clients that
/// simply ignore the new fields.
/// </remarks>
public record UserTaskDto(
    Guid Id,
    UserTaskType TaskType,
    string EntityType,
    Guid EntityId,
    string Title,
    string? Description,
    UserTaskStatus Status,
    UserTaskPriority Priority,
    DateTime CreatedAt,
    DateTime? DueAt,
    DateTime? CompletedAt,
    int RelatedEntityCount,
    string? MetadataJson,
    UserTaskAnchorKind AnchorKind,
    DateTime? AnchorAtUtc,
    DateTime? WindowStartUtc,
    DateTime? WindowEndUtc,
    UserTaskSourceKind SourceKind,
    string? SourceId);

/// <summary>
/// A group of tasks sharing the same anchor bucket in the shift-plan view.
/// </summary>
/// <param name="AnchorKind">Anchor bucket (now, at, window, anytimeToday).</param>
/// <param name="Tasks">Tasks in this group, deterministically ordered per contract.</param>
public sealed record ShiftPlanGroupDto(
    UserTaskAnchorKind AnchorKind,
    IReadOnlyList<UserTaskDto> Tasks);

/// <summary>
/// Response payload for <c>GET /api/tasks?view=shift</c> (issue #713).
/// </summary>
/// <param name="Groups">
/// Anchor-grouped, deterministically ordered task groups. Group order is
/// <c>Now</c> → <c>At</c> (by anchor time asc) / <c>Window</c> (by window start asc,
/// interleaved with At by earliest boundary) → <c>AnytimeToday</c> (which also
/// carries legacy <c>Unspecified</c> tasks so no task disappears).
/// </param>
/// <param name="GeneratedAt">UTC snapshot time; useful for client freshness.</param>
public sealed record ShiftPlanDto(
    IReadOnlyList<ShiftPlanGroupDto> Groups,
    DateTime GeneratedAt);

/// <summary>
/// Service interface for managing user tasks.
/// </summary>
public interface IUserTaskService
{
    /// <summary>
    /// Gets all pending tasks.
    /// </summary>
    Task<IReadOnlyList<UserTaskDto>> GetPendingTasksAsync(CancellationToken ct = default);

    /// <summary>
    /// Gets a task by its ID.
    /// </summary>
    Task<UserTaskDto?> GetByIdAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Gets the count of pending tasks.
    /// </summary>
    Task<int> GetPendingCountAsync(CancellationToken ct = default);

    /// <summary>
    /// Creates or updates a profile import task for a printer model.
    /// If a task already exists for the model, adds the printer to the related entities.
    /// </summary>
    Task<UserTaskDto> CreateOrUpdateProfileImportTaskAsync(CreateProfileImportTaskDto dto, CancellationToken ct = default);

    /// <summary>
    /// Marks a task as completed.
    /// </summary>
    Task<bool> CompleteTaskAsync(Guid taskId, CancellationToken ct = default);

    /// <summary>
    /// Marks a task as dismissed.
    /// </summary>
    Task<bool> DismissTaskAsync(Guid taskId, Guid? userId = null, CancellationToken ct = default);

    /// <summary>
    /// Marks a task as skipped (won't be shown again).
    /// </summary>
    Task<bool> SkipTaskAsync(Guid taskId, CancellationToken ct = default);

    /// <summary>
    /// Checks if a profile import task exists for a printer model.
    /// </summary>
    Task<bool> HasPendingProfileImportTaskAsync(Guid printerModelId, CancellationToken ct = default);

    /// <summary>
    /// Creates a manual user task.
    /// </summary>
    Task<UserTaskDto> CreateManualTaskAsync(CreateManualTaskDto dto, CancellationToken ct = default);

    /// <summary>
    /// Returns the current shift-plan view: pending tasks grouped by anchor and
    /// deterministically ordered for operator consumption (issue #713).
    /// </summary>
    /// <remarks>
    /// Group order is Now → At/Window (interleaved by earliest boundary asc) →
    /// AnytimeToday (which absorbs any legacy <c>Unspecified</c> tasks so no
    /// task disappears from the operator view). Within a group, ordering is
    /// anchor/window start asc, then priority desc, then created asc, then id asc.
    /// </remarks>
    Task<ShiftPlanDto> GetShiftPlanAsync(CancellationToken ct = default);
}
