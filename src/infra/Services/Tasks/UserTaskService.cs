using System.Text.Json;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Tasks;
using Microsoft.Extensions.Logging;

namespace Farm.Infrastructure.Services.Tasks;

/// <summary>
/// Service for managing user tasks.
/// </summary>
public class UserTaskService(
    IUserTaskRepository taskRepository,
    ILogger<UserTaskService> logger,
    ITaskBroadcaster? broadcaster = null) : IUserTaskService
{
    private readonly IUserTaskRepository _taskRepository = taskRepository ?? throw new ArgumentNullException(nameof(taskRepository));
    private readonly ILogger<UserTaskService> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly ITaskBroadcaster? _broadcaster = broadcaster;

    /// <inheritdoc />
    public async Task<IReadOnlyList<UserTaskDto>> GetPendingTasksAsync(CancellationToken ct = default)
    {
        IReadOnlyList<UserTask> tasks = await _taskRepository.GetPendingTasksAsync(null, ct);
        return tasks.Select(MapToDto).ToList();
    }

    /// <inheritdoc />
    public async Task<UserTaskDto?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        UserTask? task = await _taskRepository.GetByIdAsync(id, ct);
        return task != null ? MapToDto(task) : null;
    }

    /// <inheritdoc />
    public async Task<int> GetPendingCountAsync(CancellationToken ct = default)
    {
        return await _taskRepository.GetPendingCountAsync(null, ct);
    }

    /// <inheritdoc />
    public async Task<UserTaskDto> CreateOrUpdateProfileImportTaskAsync(CreateProfileImportTaskDto dto, CancellationToken ct = default)
    {
        // Check if a task already exists for this printer model
        UserTask? existingTask = await _taskRepository.GetByEntityAsync(
            UserTaskType.ProfileImport,
            "PrinterModel",
            dto.PrinterModelId,
            ct);

        if (existingTask != null)
        {
            // Add printer to the related entities list if not already there
            List<Guid> relatedPrinterIds = ParseRelatedEntityIds(existingTask.RelatedEntityIdsJson);
            if (!relatedPrinterIds.Contains(dto.PrinterId))
            {
                relatedPrinterIds.Add(dto.PrinterId);
                existingTask.RelatedEntityIdsJson = JsonSerializer.Serialize(relatedPrinterIds);
                existingTask.UpdatedAt = DateTime.UtcNow;

                // Update description with new count
                int count = relatedPrinterIds.Count;
                existingTask.Description = $"{count} printer{(count == 1 ? string.Empty : "s")} waiting for slicer profiles";

                await _taskRepository.UpdateAsync(existingTask, ct);
                _logger.LogInformation("[UserTaskService] Updated profile import task for {PrinterModelName}, now {PrinterCount} printers waiting", dto.PrinterModelName, count);

                UserTaskDto updatedDto = MapToDto(existingTask);
                await BroadcastTaskUpdatedAsync(updatedDto, ct);
            }

            return MapToDto(existingTask);
        }

        // Create new task
        UserTask newTask = new()
        {
            Id = Guid.NewGuid(),
            TaskType = UserTaskType.ProfileImport,
            EntityType = "PrinterModel",
            EntityId = dto.PrinterModelId,
            Title = $"Import slicer profiles for {dto.ManufacturerName} {dto.PrinterModelName}",
            Description = "1 printer waiting for slicer profiles",
            Status = UserTaskStatus.Pending,
            Priority = UserTaskPriority.High,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            MetadataJson = JsonSerializer.Serialize(new
            {
                printerModelId = dto.PrinterModelId,
                manufacturerName = dto.ManufacturerName,
                printerModelName = dto.PrinterModelName
            }),
            RelatedEntityIdsJson = JsonSerializer.Serialize(new List<Guid> { dto.PrinterId })
        };

        await _taskRepository.AddAsync(newTask, ct);
        _logger.LogInformation("[UserTaskService] Created profile import task for {ManufacturerName} {PrinterModelName}", dto.ManufacturerName, dto.PrinterModelName);

        UserTaskDto createdDto = MapToDto(newTask);
        await BroadcastTaskCreatedAsync(createdDto, ct);

        return createdDto;
    }

    /// <inheritdoc />
    public async Task<bool> CompleteTaskAsync(Guid taskId, CancellationToken ct = default)
    {
        UserTask? task = await _taskRepository.GetByIdAsync(taskId, ct);
        if (task == null)
        {
            return false;
        }

        task.Status = UserTaskStatus.Completed;
        task.CompletedAt = DateTime.UtcNow;
        task.UpdatedAt = DateTime.UtcNow;

        await _taskRepository.UpdateAsync(task, ct);
        _logger.LogInformation("[UserTaskService] Completed task: {TaskTitle}", task.Title);

        await BroadcastTaskUpdatedAsync(MapToDto(task), ct);
        return true;
    }

    /// <inheritdoc />
    public async Task<bool> DismissTaskAsync(Guid taskId, Guid? userId = null, CancellationToken ct = default)
    {
        UserTask? task = await _taskRepository.GetByIdAsync(taskId, ct);
        if (task == null)
        {
            return false;
        }

        task.Status = UserTaskStatus.Dismissed;
        task.DismissedAt = DateTime.UtcNow;
        task.DismissedByUserId = userId;
        task.UpdatedAt = DateTime.UtcNow;

        await _taskRepository.UpdateAsync(task, ct);
        _logger.LogInformation("[UserTaskService] Dismissed task: {TaskTitle}", task.Title);

        await BroadcastTaskUpdatedAsync(MapToDto(task), ct);
        return true;
    }

    /// <inheritdoc />
    public async Task<bool> SkipTaskAsync(Guid taskId, CancellationToken ct = default)
    {
        UserTask? task = await _taskRepository.GetByIdAsync(taskId, ct);
        if (task == null)
        {
            return false;
        }

        task.Status = UserTaskStatus.Skipped;
        task.UpdatedAt = DateTime.UtcNow;

        await _taskRepository.UpdateAsync(task, ct);
        _logger.LogInformation("[UserTaskService] Skipped task: {TaskTitle}", task.Title);

        await BroadcastTaskUpdatedAsync(MapToDto(task), ct);
        return true;
    }

    /// <inheritdoc />
    public async Task<bool> HasPendingProfileImportTaskAsync(Guid printerModelId, CancellationToken ct = default)
    {
        UserTask? existingTask = await _taskRepository.GetByEntityAsync(
            UserTaskType.ProfileImport,
            "PrinterModel",
            printerModelId,
            ct);

        return existingTask != null;
    }

    /// <inheritdoc />
    public async Task<UserTaskDto> CreateManualTaskAsync(CreateManualTaskDto dto, CancellationToken ct = default)
    {
        UserTask newTask = new()
        {
            Id = Guid.NewGuid(),
            TaskType = UserTaskType.Custom,
            EntityType = "Manual",
            EntityId = Guid.Empty,
            Title = dto.Title,
            Description = dto.Description,
            Status = UserTaskStatus.Pending,
            Priority = dto.Priority,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        await _taskRepository.AddAsync(newTask, ct);
        _logger.LogInformation("[UserTaskService] Created manual task: {TaskTitle}", newTask.Title);

        UserTaskDto createdDto = MapToDto(newTask);
        await BroadcastTaskCreatedAsync(createdDto, ct);

        return createdDto;
    }

    private static UserTaskDto MapToDto(UserTask task)
    {
        int relatedCount = ParseRelatedEntityIds(task.RelatedEntityIdsJson).Count;

        return new UserTaskDto(
            task.Id,
            task.TaskType,
            task.EntityType,
            task.EntityId,
            task.Title,
            task.Description,
            task.Status,
            task.Priority,
            task.CreatedAt,
            task.DueAt,
            task.CompletedAt,
            relatedCount,
            task.MetadataJson);
    }

    private static List<Guid> ParseRelatedEntityIds(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<Guid>>(json) ?? [];
        }
        catch
        {
            return [];
        }
    }

    private async Task BroadcastTaskCreatedAsync(UserTaskDto task, CancellationToken ct)
    {
        if (_broadcaster == null)
        {
            return;
        }

        try
        {
            await _broadcaster.BroadcastTaskCreatedAsync(task, ct);
            int pendingCount = await _taskRepository.GetPendingCountAsync(null, ct);
            await _broadcaster.BroadcastPendingTaskCountAsync(pendingCount, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[UserTaskService] Failed to broadcast task created event");
        }
    }

    private async Task BroadcastTaskUpdatedAsync(UserTaskDto task, CancellationToken ct)
    {
        if (_broadcaster == null)
        {
            return;
        }

        try
        {
            await _broadcaster.BroadcastTaskUpdatedAsync(task, ct);
            int pendingCount = await _taskRepository.GetPendingCountAsync(null, ct);
            await _broadcaster.BroadcastPendingTaskCountAsync(pendingCount, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[UserTaskService] Failed to broadcast task updated event");
        }
    }
}
