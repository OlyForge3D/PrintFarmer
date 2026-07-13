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
    public async Task<IReadOnlyList<UserTaskDto>> GetPendingTasksAsync(bool isAdmin, CancellationToken ct = default)
    {
        // Fix 8/B: non-admin callers must not receive maintenance-sourced tasks
        // (whose title/description carry alert content) from the flat list either.
        IReadOnlyList<UserTask> tasks = await _taskRepository.GetPendingTasksAsync(null, includeMaintenance: isAdmin, ct);
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
    public async Task<int> GetPendingCountAsync(bool isAdmin, CancellationToken ct = default)
    {
        // Fix 8/B: the count returned to a non-admin must exclude maintenance tasks
        // so it agrees with the filtered list.
        return await _taskRepository.GetPendingCountAsync(null, includeMaintenance: isAdmin, ct);
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

                // Update description with new count
                int count = relatedPrinterIds.Count;
                existingTask.Description = $"{count} printer{(count == 1 ? string.Empty : "s")} waiting for slicer profiles";

                // Fix R4-2: the task was loaded via GetByEntityAsync (no-tracking), so a
                // blind full-entity UpdateAsync marks EVERY column modified and would
                // clobber a concurrent user Complete/Skip/Dismiss back to the stale
                // Pending status (the same lost-update bug R3-5 fixed for the
                // complete/skip/dismiss paths). Write only the columns this import path
                // actually changes — never Status — so a concurrent terminal user action
                // wins the race. UpdateFieldsAsync also stamps UpdatedAt. A row that
                // already went terminal is filtered out by GetByEntityAsync on the next
                // call, where the new-task branch handles it.
                await _taskRepository.UpdateFieldsAsync(
                    existingTask,
                    [nameof(UserTask.RelatedEntityIdsJson), nameof(UserTask.Description)],
                    ct);
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

        // Fix R3-5: task was loaded no-tracking, so only write the columns this
        // action intends to change instead of a blind full-entity Update() that
        // would clobber any field a concurrent writer (e.g. the shift-plan
        // compiler) changed on the same row in the meantime.
        await _taskRepository.UpdateFieldsAsync(task, [nameof(UserTask.Status), nameof(UserTask.CompletedAt)], ct);
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

        // Fix R3-5: see CompleteTaskAsync — write only the named columns.
        await _taskRepository.UpdateFieldsAsync(
            task,
            [nameof(UserTask.Status), nameof(UserTask.DismissedAt), nameof(UserTask.DismissedByUserId)],
            ct);
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

        // Fix R3-5: see CompleteTaskAsync — write only the named columns.
        await _taskRepository.UpdateFieldsAsync(task, [nameof(UserTask.Status)], ct);
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

    /// <inheritdoc />
    public async Task<ShiftPlanDto> GetShiftPlanAsync(CancellationToken ct = default)
    {
        return await GetShiftPlanAsync(isAdmin: false, ct);
    }

    /// <inheritdoc cref="IUserTaskService.GetShiftPlanAsync(bool, CancellationToken)"/>
    /// <param name="isAdmin">
    /// When <c>true</c>, maintenance-sourced tasks are included. Non-admin
    /// callers must pass <c>false</c> so sensitive alert details are excluded.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<ShiftPlanDto> GetShiftPlanAsync(bool isAdmin, CancellationToken ct = default)
    {
        IReadOnlyList<UserTask> pending = await _taskRepository.GetPendingTasksAsync(null, ct);

        // Fix 8: exclude maintenance tasks for non-admin callers.
        IEnumerable<UserTask> visible = isAdmin
            ? pending
            : pending.Where(t => t.SourceKind != UserTaskSourceKind.Maintenance);

        // Fix 5: Now → Timeline (At+Window interleaved by boundary) → AnytimeToday.
        // Legacy tasks with Unspecified land in AnytimeToday.
        static UserTaskAnchorKind BucketOf(UserTaskAnchorKind kind) => kind switch
        {
            UserTaskAnchorKind.Now => UserTaskAnchorKind.Now,
            UserTaskAnchorKind.At or UserTaskAnchorKind.Window => UserTaskAnchorKind.Timeline,
            _ => UserTaskAnchorKind.AnytimeToday,
        };

        // Primary boundary for interleaved ordering. Fix H: key by the task's own
        // AnchorKind so a Window task that also happens to carry AnchorAtUtc still
        // sorts by its window-start boundary, not the point anchor. Now floats to the
        // top, Anytime sinks to the bottom.
        static DateTime PrimaryBoundary(UserTask t)
        {
            UserTaskAnchorKind b = BucketOf(t.AnchorKind);
            return b switch
            {
                UserTaskAnchorKind.Now => DateTime.MinValue,
                UserTaskAnchorKind.Timeline => t.AnchorKind switch
                {
                    UserTaskAnchorKind.At => t.AnchorAtUtc ?? t.WindowStartUtc ?? t.DueAt ?? t.CreatedAt,
                    UserTaskAnchorKind.Window => t.WindowStartUtc ?? t.AnchorAtUtc ?? t.DueAt ?? t.CreatedAt,
                    _ => t.AnchorAtUtc ?? t.WindowStartUtc ?? DateTime.MaxValue,
                },
                _ => DateTime.MaxValue,
            };
        }

        List<UserTask> ordered = [.. visible
            .OrderBy(t => PrimaryBoundary(t))
            .ThenByDescending(t => t.Priority)
            .ThenBy(t => t.CreatedAt)
            .ThenBy(t => t.Id)];

        List<ShiftPlanGroupDto> groups = new(3);
        foreach (UserTaskAnchorKind bucket in new[]
        {
            UserTaskAnchorKind.Now,
            UserTaskAnchorKind.Timeline,
            UserTaskAnchorKind.AnytimeToday,
        })
        {
            List<UserTaskDto> tasksInGroup = ordered
                .Where(t => BucketOf(t.AnchorKind) == bucket)
                .Select(MapToDto)
                .ToList();

            if (tasksInGroup.Count > 0)
            {
                groups.Add(new ShiftPlanGroupDto(bucket, tasksInGroup));
            }
        }

        return new ShiftPlanDto(groups, DateTime.UtcNow);
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
            task.MetadataJson,
            task.AnchorKind,
            task.AnchorAtUtc,
            task.WindowStartUtc,
            task.WindowEndUtc,
            task.SourceKind,
            task.SourceId);
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

            // Fix R3-4: broadcast the non-maintenance-filtered count so it always
            // agrees with the non-admin REST count (GET /api/tasks/count). The prior
            // maintenance-inclusive count over-reported to every connected client,
            // most of whom cannot see maintenance tasks at all.
            int pendingCount = await _taskRepository.GetPendingCountAsync(null, includeMaintenance: false, ct);
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

            // Fix R3-4: see BroadcastTaskCreatedAsync.
            int pendingCount = await _taskRepository.GetPendingCountAsync(null, includeMaintenance: false, ct);
            await _broadcaster.BroadcastPendingTaskCountAsync(pendingCount, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[UserTaskService] Failed to broadcast task updated event");
        }
    }
}
