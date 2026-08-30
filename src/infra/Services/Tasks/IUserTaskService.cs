using System.Text.Json.Serialization;
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
/// <remarks>
/// <see cref="AnchorKind"/> and <see cref="SourceKind"/> carry property-level
/// <c>[JsonConverter]</c> attributes (issue #2246) because a property-level
/// attribute is the only thing that outranks the global
/// <c>JsonStringEnumConverter</c> registered in <c>ControllerStartup</c>/
/// <c>SignalRStartup</c>; the type-level attributes on the enums themselves are
/// otherwise dead code for real MVC/SignalR output. Keep both properties'
/// canonical lowercase camelCase tokens working across HTTP and SignalR.
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
    [property: JsonConverter(typeof(UserTaskAnchorKindJsonConverter))] UserTaskAnchorKind AnchorKind,
    DateTime? AnchorAtUtc,
    DateTime? WindowStartUtc,
    DateTime? WindowEndUtc,
    [property: JsonConverter(typeof(UserTaskSourceKindJsonConverter))] UserTaskSourceKind SourceKind,
    string? SourceId);

/// <summary>
/// A group of tasks sharing the same anchor bucket in the shift-plan view.
/// </summary>
/// <param name="AnchorKind">
/// Anchor bucket. The value <see cref="UserTaskAnchorKind.Timeline"/> identifies the
/// merged At+Window group; individual tasks in that group retain their own
/// <see cref="UserTaskAnchorKind"/> (At or Window).
/// Other values are <see cref="UserTaskAnchorKind.Now"/> and
/// <see cref="UserTaskAnchorKind.AnytimeToday"/>.
/// </param>
/// <param name="Tasks">Tasks in this group, deterministically ordered per contract.</param>
/// <remarks>
/// <see cref="AnchorKind"/> carries a property-level <c>[JsonConverter]</c> attribute
/// (issue #2246) for the same reason as <see cref="UserTaskDto.AnchorKind"/> — see that
/// property's remarks.
/// </remarks>
public sealed record ShiftPlanGroupDto(
    [property: JsonConverter(typeof(UserTaskAnchorKindJsonConverter))] UserTaskAnchorKind AnchorKind,
    IReadOnlyList<UserTaskDto> Tasks);

/// <summary>
/// Response payload for <c>GET /api/tasks?view=shift</c> (issue #713).
/// </summary>
/// <param name="Groups">
/// Anchor-grouped, deterministically ordered task groups. Group order is
/// <c>Now</c> → <c>Timeline</c> (At + Window interleaved by earliest boundary asc) →
/// <c>AnytimeToday</c> (which also carries legacy <c>Unspecified</c> tasks so no task disappears).
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
    /// Gets all pending tasks. When <paramref name="isAdmin"/> is <c>false</c>,
    /// maintenance-sourced tasks are excluded so their alert content is not
    /// surfaced to non-admin callers (issue #713 Fix 8).
    /// </summary>
    Task<IReadOnlyList<UserTaskDto>> GetPendingTasksAsync(bool isAdmin, CancellationToken ct = default);

    /// <summary>
    /// Gets a task by its ID.
    /// </summary>
    Task<UserTaskDto?> GetByIdAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Gets the count of pending tasks.
    /// </summary>
    Task<int> GetPendingCountAsync(CancellationToken ct = default);

    /// <summary>
    /// Gets the count of pending tasks. When <paramref name="isAdmin"/> is
    /// <c>false</c>, maintenance-sourced tasks are excluded so the count agrees
    /// with the filtered list a non-admin can see (issue #713 Fix 8).
    /// </summary>
    Task<int> GetPendingCountAsync(bool isAdmin, CancellationToken ct = default);

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
    /// Group order is Now → Timeline (At+Window interleaved by earliest boundary asc) →
    /// AnytimeToday (which absorbs any legacy <c>Unspecified</c> tasks). Within the
    /// Timeline group, At tasks use <see cref="UserTask.AnchorAtUtc"/> and Window tasks
    /// use <see cref="UserTask.WindowStartUtc"/> as the ordering boundary. Within a group,
    /// ordering is boundary asc, priority desc, created asc, id asc.
    /// Maintenance-sourced tasks are excluded from results unless the caller passes
    /// <c>isAdmin: true</c>.
    /// </remarks>
    Task<ShiftPlanDto> GetShiftPlanAsync(CancellationToken ct = default);

    /// <inheritdoc cref="GetShiftPlanAsync(CancellationToken)"/>
    /// <param name="isAdmin"><c>true</c> to include maintenance-sourced tasks.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<ShiftPlanDto> GetShiftPlanAsync(bool isAdmin, CancellationToken ct = default);
}
