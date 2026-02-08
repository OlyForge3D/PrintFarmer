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
/// DTO representing a user task for API responses.
/// </summary>
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
    string? MetadataJson);

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
}
