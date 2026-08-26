using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Tasks;
using FluentAssertions;
using Xunit;

namespace Farm.Infrastructure.Tests.Infrastructure;

public class EfUserTaskRepositoryTests : IDisposable
{
    private readonly AppDbContext _context;
    private readonly EfUserTaskRepository _repository;

    public EfUserTaskRepositoryTests()
    {
        _context = AppDbTestHelpers.CreateSqliteInMemoryDb();
        _repository = new EfUserTaskRepository(_context);
    }

    public void Dispose()
    {
        _context.Dispose();
        GC.SuppressFinalize(this);
    }

    #region GetByIdAsync Tests

    [Fact]
    public async Task GetByIdAsync_WithExistingTask_ReturnsTask()
    {
        // Arrange
        var task = CreateUserTask("Test Task", UserTaskType.ProfileImport);
        _ = _context.UserTasks.Add(task);
        _ = await _context.SaveChangesAsync();

        // Act
        UserTask? result = await _repository.GetByIdAsync(task.Id);

        // Assert
        result.Should().NotBeNull();
        result!.Id.Should().Be(task.Id);
        result.Title.Should().Be("Test Task");
    }

    [Fact]
    public async Task GetByIdAsync_WithNonExistingTask_ReturnsNull()
    {
        // Act
        UserTask? result = await _repository.GetByIdAsync(Guid.NewGuid());

        // Assert
        result.Should().BeNull();
    }

    #endregion

    #region GetPendingTasksAsync Tests

    [Fact]
    public async Task GetPendingTasksAsync_WithPendingTasks_ReturnsPendingOnly()
    {
        // Arrange
        var pendingTask1 = CreateUserTask("Pending 1", UserTaskType.ProfileImport, UserTaskStatus.Pending);
        var pendingTask2 = CreateUserTask("Pending 2", UserTaskType.MaintenanceDue, UserTaskStatus.Pending);
        var completedTask = CreateUserTask("Completed", UserTaskType.ProfileImport, UserTaskStatus.Completed);
        var dismissedTask = CreateUserTask("Dismissed", UserTaskType.ProfileImport, UserTaskStatus.Dismissed);

        _context.UserTasks.AddRange(pendingTask1, pendingTask2, completedTask, dismissedTask);
        _ = await _context.SaveChangesAsync();

        // Act
        IReadOnlyList<UserTask> result = await _repository.GetPendingTasksAsync();

        // Assert
        result.Should().HaveCount(2);
        result.Should().OnlyContain(t => t.Status == UserTaskStatus.Pending);
    }

    [Fact]
    public async Task GetPendingTasksAsync_WithTaskTypeFilter_ReturnsFilteredTasks()
    {
        // Arrange
        var profileTask = CreateUserTask("Profile Import", UserTaskType.ProfileImport, UserTaskStatus.Pending);
        var maintenanceTask = CreateUserTask("Maintenance", UserTaskType.MaintenanceDue, UserTaskStatus.Pending);

        _context.UserTasks.AddRange(profileTask, maintenanceTask);
        _ = await _context.SaveChangesAsync();

        // Act
        IReadOnlyList<UserTask> result = await _repository.GetPendingTasksAsync(UserTaskType.ProfileImport);

        // Assert
        result.Should().HaveCount(1);
        result[0].TaskType.Should().Be(UserTaskType.ProfileImport);
    }

    [Fact]
    public async Task GetPendingTasksAsync_OrdersByPriorityDescending()
    {
        // Arrange
        var lowPriorityTask = CreateUserTask("Low", UserTaskType.ProfileImport, UserTaskStatus.Pending, UserTaskPriority.Low);
        var highPriorityTask = CreateUserTask("High", UserTaskType.ProfileImport, UserTaskStatus.Pending, UserTaskPriority.High);
        var normalPriorityTask = CreateUserTask("Normal", UserTaskType.ProfileImport, UserTaskStatus.Pending, UserTaskPriority.Normal);

        _context.UserTasks.AddRange(lowPriorityTask, highPriorityTask, normalPriorityTask);
        _ = await _context.SaveChangesAsync();

        // Act
        IReadOnlyList<UserTask> result = await _repository.GetPendingTasksAsync();

        // Assert
        result.Should().HaveCount(3);
        result[0].Priority.Should().Be(UserTaskPriority.High);
        result[1].Priority.Should().Be(UserTaskPriority.Normal);
        result[2].Priority.Should().Be(UserTaskPriority.Low);
    }

    #endregion

    #region GetByStatusAsync Tests

    [Fact]
    public async Task GetByStatusAsync_WithStatus_ReturnsMatchingTasks()
    {
        // Arrange
        var pendingTask = CreateUserTask("Pending", UserTaskType.ProfileImport, UserTaskStatus.Pending);
        var completedTask = CreateUserTask("Completed", UserTaskType.ProfileImport, UserTaskStatus.Completed);

        _context.UserTasks.AddRange(pendingTask, completedTask);
        _ = await _context.SaveChangesAsync();

        // Act
        IReadOnlyList<UserTask> result = await _repository.GetByStatusAsync(new[] { UserTaskStatus.Completed });

        // Assert
        result.Should().HaveCount(1);
        result[0].Status.Should().Be(UserTaskStatus.Completed);
    }

    [Fact]
    public async Task GetByStatusAsync_WithMultipleStatuses_ReturnsAllMatching()
    {
        // Arrange
        var pendingTask = CreateUserTask("Pending", UserTaskType.ProfileImport, UserTaskStatus.Pending);
        var completedTask = CreateUserTask("Completed", UserTaskType.ProfileImport, UserTaskStatus.Completed);
        var dismissedTask = CreateUserTask("Dismissed", UserTaskType.ProfileImport, UserTaskStatus.Dismissed);

        _context.UserTasks.AddRange(pendingTask, completedTask, dismissedTask);
        _ = await _context.SaveChangesAsync();

        // Act
        IReadOnlyList<UserTask> result = await _repository.GetByStatusAsync(
            new[] { UserTaskStatus.Completed, UserTaskStatus.Dismissed });

        // Assert
        result.Should().HaveCount(2);
        result.Should().OnlyContain(t => t.Status == UserTaskStatus.Completed || t.Status == UserTaskStatus.Dismissed);
    }

    #endregion

    #region GetByEntityAsync Tests

    [Fact]
    public async Task GetByEntityAsync_WithMatchingEntity_ReturnsTask()
    {
        // Arrange
        var entityId = Guid.NewGuid();
        var task = CreateUserTask("Task", UserTaskType.ProfileImport, UserTaskStatus.Pending);
        task.EntityType = "PrinterModel";
        task.EntityId = entityId;

        _ = _context.UserTasks.Add(task);
        _ = await _context.SaveChangesAsync();

        // Act
        UserTask? result = await _repository.GetByEntityAsync(
            UserTaskType.ProfileImport,
            "PrinterModel",
            entityId);

        // Assert
        result.Should().NotBeNull();
        result!.EntityId.Should().Be(entityId);
    }

    [Fact]
    public async Task GetByEntityAsync_WithNoMatch_ReturnsNull()
    {
        // Arrange
        var entityId = Guid.NewGuid();
        var task = CreateUserTask("Task", UserTaskType.ProfileImport, UserTaskStatus.Pending);
        task.EntityType = "PrinterModel";
        task.EntityId = entityId;

        _ = _context.UserTasks.Add(task);
        _ = await _context.SaveChangesAsync();

        // Act - Different entity ID
        UserTask? result = await _repository.GetByEntityAsync(
            UserTaskType.ProfileImport,
            "PrinterModel",
            Guid.NewGuid());

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public async Task GetByEntityAsync_IgnoresCompletedAndDismissedTasks()
    {
        // Arrange
        var entityId = Guid.NewGuid();
        var completedTask = CreateUserTask("Completed", UserTaskType.ProfileImport, UserTaskStatus.Completed);
        completedTask.EntityType = "PrinterModel";
        completedTask.EntityId = entityId;

        _ = _context.UserTasks.Add(completedTask);
        _ = await _context.SaveChangesAsync();

        // Act
        UserTask? result = await _repository.GetByEntityAsync(
            UserTaskType.ProfileImport,
            "PrinterModel",
            entityId);

        // Assert
        result.Should().BeNull();
    }

    #endregion

    #region GetPendingCountAsync Tests

    [Fact]
    public async Task GetPendingCountAsync_ReturnsCorrectCount()
    {
        // Arrange
        var pending1 = CreateUserTask("Pending 1", UserTaskType.ProfileImport, UserTaskStatus.Pending);
        var pending2 = CreateUserTask("Pending 2", UserTaskType.MaintenanceDue, UserTaskStatus.Pending);
        var completed = CreateUserTask("Completed", UserTaskType.ProfileImport, UserTaskStatus.Completed);

        _context.UserTasks.AddRange(pending1, pending2, completed);
        _ = await _context.SaveChangesAsync();

        // Act
        int result = await _repository.GetPendingCountAsync();

        // Assert
        result.Should().Be(2);
    }

    [Fact]
    public async Task GetPendingCountAsync_WithTaskTypeFilter_ReturnsFilteredCount()
    {
        // Arrange
        var profile1 = CreateUserTask("Profile 1", UserTaskType.ProfileImport, UserTaskStatus.Pending);
        var profile2 = CreateUserTask("Profile 2", UserTaskType.ProfileImport, UserTaskStatus.Pending);
        var maintenance = CreateUserTask("Maintenance", UserTaskType.MaintenanceDue, UserTaskStatus.Pending);

        _context.UserTasks.AddRange(profile1, profile2, maintenance);
        _ = await _context.SaveChangesAsync();

        // Act
        int result = await _repository.GetPendingCountAsync(UserTaskType.ProfileImport);

        // Assert
        result.Should().Be(2);
    }

    #endregion

    #region AddAsync Tests

    [Fact]
    public async Task AddAsync_AddsTaskToDatabase()
    {
        // Arrange
        var task = CreateUserTask("New Task", UserTaskType.ProfileImport);

        // Act
        await _repository.AddAsync(task);

        // Assert
        UserTask? savedTask = await _context.UserTasks.FindAsync(task.Id);
        savedTask.Should().NotBeNull();
        savedTask!.Title.Should().Be("New Task");
    }

    #endregion

    #region UpdateAsync Tests

    [Fact]
    public async Task UpdateAsync_UpdatesTaskInDatabase()
    {
        // Arrange
        var task = CreateUserTask("Original Title", UserTaskType.ProfileImport);
        _ = _context.UserTasks.Add(task);
        _ = await _context.SaveChangesAsync();

        // Act
        task.Title = "Updated Title";
        task.Status = UserTaskStatus.Completed;
        await _repository.UpdateAsync(task);

        // Assert
        _context.ChangeTracker.Clear();
        UserTask? updatedTask = await _context.UserTasks.FindAsync(task.Id);
        updatedTask.Should().NotBeNull();
        updatedTask!.Title.Should().Be("Updated Title");
        updatedTask.Status.Should().Be(UserTaskStatus.Completed);
    }

    #endregion

    #region DeleteAsync Tests

    [Fact]
    public async Task DeleteAsync_RemovesTaskFromDatabase()
    {
        // Arrange
        var task = CreateUserTask("To Delete", UserTaskType.ProfileImport);
        _ = _context.UserTasks.Add(task);
        _ = await _context.SaveChangesAsync();

        // Act
        await _repository.DeleteAsync(task);

        // Assert
        UserTask? deletedTask = await _context.UserTasks.FindAsync(task.Id);
        deletedTask.Should().BeNull();
    }

    #endregion

    #region Fix B: includeMaintenance filtering

    /// <summary>Fix B: non-admin list requests exclude maintenance-sourced tasks.</summary>
    [Fact]
    public async Task GetPendingTasksAsync_IncludeMaintenanceFalse_ExcludesMaintenanceTasks()
    {
        var normal = CreateUserTask("Normal", UserTaskType.ProfileImport, UserTaskStatus.Pending);
        var maintenance = CreateUserTask("Maint", UserTaskType.MaintenanceDue, UserTaskStatus.Pending);
        maintenance.SourceKind = UserTaskSourceKind.Maintenance;
        maintenance.SourceId = "maintenancealert:1";

        _context.UserTasks.AddRange(normal, maintenance);
        _ = await _context.SaveChangesAsync();

        IReadOnlyList<UserTask> included = await _repository.GetPendingTasksAsync(null, includeMaintenance: true);
        IReadOnlyList<UserTask> excluded = await _repository.GetPendingTasksAsync(null, includeMaintenance: false);

        included.Should().HaveCount(2);
        _ = excluded.Should().ContainSingle().Which.SourceKind.Should().NotBe(UserTaskSourceKind.Maintenance);
    }

    /// <summary>Fix B: non-admin count excludes maintenance-sourced tasks so it matches the visible list.</summary>
    [Fact]
    public async Task GetPendingCountAsync_IncludeMaintenanceFalse_ExcludesMaintenanceTasks()
    {
        var normal = CreateUserTask("Normal", UserTaskType.ProfileImport, UserTaskStatus.Pending);
        var maintenance = CreateUserTask("Maint", UserTaskType.MaintenanceDue, UserTaskStatus.Pending);
        maintenance.SourceKind = UserTaskSourceKind.Maintenance;
        maintenance.SourceId = "maintenancealert:1";

        _context.UserTasks.AddRange(normal, maintenance);
        _ = await _context.SaveChangesAsync();

        int included = await _repository.GetPendingCountAsync(null, includeMaintenance: true);
        int excluded = await _repository.GetPendingCountAsync(null, includeMaintenance: false);

        included.Should().Be(2);
        excluded.Should().Be(1);
    }

    #endregion

    #region Fix F: GetSuppressedSourceKeysAsync

    /// <summary>
    /// Fix F: only recently Skipped/Dismissed compiler tasks suppress re-creation.
    /// Completed tasks (recurrence is legitimate) and stale rows are excluded.
    /// </summary>
    [Fact]
    public async Task GetSuppressedSourceKeysAsync_ReturnsRecentSkippedAndDismissed_ExcludesCompletedAndStale()
    {
        DateTime now = DateTime.UtcNow;

        var skipped = CreateUserTask("Skipped", UserTaskType.MaintenanceDue, UserTaskStatus.Skipped);
        skipped.SourceKind = UserTaskSourceKind.Maintenance;
        skipped.SourceId = "maintenancealert:skip";
        skipped.UpdatedAt = now;
        skipped.LastMutationSequence = 40;

        // A second, newer terminal row for the SAME key: the projection must collapse to one
        // entry carrying the GREATEST mutation version (issue #823 replay detection).
        var skippedAgain = CreateUserTask("SkippedAgain", UserTaskType.MaintenanceDue, UserTaskStatus.Skipped);
        skippedAgain.SourceKind = UserTaskSourceKind.Maintenance;
        skippedAgain.SourceId = "maintenancealert:skip";
        skippedAgain.UpdatedAt = now;
        skippedAgain.LastMutationSequence = 42;

        var dismissed = CreateUserTask("Dismissed", UserTaskType.FailureClear, UserTaskStatus.Dismissed);
        dismissed.SourceKind = UserTaskSourceKind.FailureIncident;
        dismissed.SourceId = "failure:dismiss";
        dismissed.UpdatedAt = now;
        dismissed.LastMutationSequence = 7;

        var completed = CreateUserTask("Completed", UserTaskType.MaintenanceDue, UserTaskStatus.Completed);
        completed.SourceKind = UserTaskSourceKind.Maintenance;
        completed.SourceId = "maintenancealert:done";
        completed.UpdatedAt = now;

        var stale = CreateUserTask("Stale", UserTaskType.MaintenanceDue, UserTaskStatus.Skipped);
        stale.SourceKind = UserTaskSourceKind.Maintenance;
        stale.SourceId = "maintenancealert:stale";
        stale.UpdatedAt = now.AddHours(-2);

        _context.UserTasks.AddRange(skipped, skippedAgain, dismissed, completed, stale);
        _ = await _context.SaveChangesAsync();

        IReadOnlyCollection<(UserTaskSourceKind SourceKind, string SourceId, long Version)> result =
            await _repository.GetSuppressedSourceKeysAsync(now.AddHours(-1));

        HashSet<(UserTaskSourceKind, string)> keys = result.Select(r => (r.SourceKind, r.SourceId)).ToHashSet();
        result.Should().HaveCount(2);
        keys.Should().Contain((UserTaskSourceKind.Maintenance, "maintenancealert:skip"));
        keys.Should().Contain((UserTaskSourceKind.FailureIncident, "failure:dismiss"));
        keys.Should().NotContain((UserTaskSourceKind.Maintenance, "maintenancealert:done"));
        keys.Should().NotContain((UserTaskSourceKind.Maintenance, "maintenancealert:stale"));

        // The greatest mutation version per key is surfaced so overlap replay can be detected.
        result.Single(r => r.SourceId == "maintenancealert:skip").Version.Should().Be(42);
        result.Single(r => r.SourceId == "failure:dismiss").Version.Should().Be(7);
    }

    #endregion

    #region GetOpenSuppressedByKeysAsync Tests

    /// <summary>
    /// Fix R6-2: bootstrap ignores terminal suppression rows outside the 30-day
    /// maximum age so stale historical episodes cannot be rehydrated forever.
    /// </summary>
    [Fact]
    public async Task GetOpenSuppressedByKeysAsync_RowOlderThanMaximumAge_IsExcluded()
    {
        DateTime now = DateTime.UtcNow;
        UserTask recent = CreateUserTask("Recent", UserTaskType.MaintenanceDue, UserTaskStatus.Skipped);
        recent.SourceKind = UserTaskSourceKind.Maintenance;
        recent.SourceId = "maintenancealert:recent";
        recent.UpdatedAt = now.AddDays(-29);

        UserTask stale = CreateUserTask("Stale", UserTaskType.MaintenanceDue, UserTaskStatus.Dismissed);
        stale.SourceKind = UserTaskSourceKind.Maintenance;
        stale.SourceId = "maintenancealert:stale";
        stale.UpdatedAt = now.AddDays(-31);

        _context.UserTasks.AddRange(recent, stale);
        _ = await _context.SaveChangesAsync();

        IReadOnlyCollection<(UserTaskSourceKind SourceKind, string SourceId, long Version)> result =
            await _repository.GetOpenSuppressedByKeysAsync(
                [
                    (UserTaskSourceKind.Maintenance, "maintenancealert:recent"),
                    (UserTaskSourceKind.Maintenance, "maintenancealert:stale"),
                ],
                maxAgeUtc: now.AddDays(-30));

        result.Should().ContainSingle();
        result.Select(r => (r.SourceKind, r.SourceId)).Should()
            .Contain((UserTaskSourceKind.Maintenance, "maintenancealert:recent"));
    }

    /// <summary>
    /// Fix R6-4: source-kind and source-id predicates must remain paired instead of
    /// retrieving the Cartesian combinations formed by independent IN filters.
    /// </summary>
    [Fact]
    public async Task GetOpenSuppressedByKeysAsync_CrossedSourcePairs_ReturnsOnlyExactPairs()
    {
        DateTime now = DateTime.UtcNow;
        UserTask first = CreateSuppressedTask(UserTaskSourceKind.Maintenance, "maintenancealert:one", now);
        UserTask second = CreateSuppressedTask(UserTaskSourceKind.FailureIncident, "failure:two", now);
        UserTask crossedFirst = CreateSuppressedTask(UserTaskSourceKind.Maintenance, "failure:two", now);
        UserTask crossedSecond = CreateSuppressedTask(UserTaskSourceKind.FailureIncident, "maintenancealert:one", now);
        _context.UserTasks.AddRange(first, second, crossedFirst, crossedSecond);
        _ = await _context.SaveChangesAsync();

        IReadOnlyCollection<(UserTaskSourceKind SourceKind, string SourceId, long Version)> result =
            await _repository.GetOpenSuppressedByKeysAsync(
                [
                    (UserTaskSourceKind.Maintenance, "maintenancealert:one"),
                    (UserTaskSourceKind.FailureIncident, "failure:two"),
                ],
                maxAgeUtc: now.AddDays(-1));

        HashSet<(UserTaskSourceKind, string)> keys = result.Select(r => (r.SourceKind, r.SourceId)).ToHashSet();
        result.Should().HaveCount(2);
        keys.Should().Contain((UserTaskSourceKind.Maintenance, "maintenancealert:one"));
        keys.Should().Contain((UserTaskSourceKind.FailureIncident, "failure:two"));
        keys.Should().NotContain((UserTaskSourceKind.Maintenance, "failure:two"));
        keys.Should().NotContain((UserTaskSourceKind.FailureIncident, "maintenancealert:one"));
    }

    #endregion

    #region Helper Methods

    private static UserTask CreateSuppressedTask(UserTaskSourceKind sourceKind, string sourceId, DateTime updatedAt)
    {
        UserTask task = CreateUserTask("Suppressed", UserTaskType.MaintenanceDue, UserTaskStatus.Skipped);
        task.SourceKind = sourceKind;
        task.SourceId = sourceId;
        task.UpdatedAt = updatedAt;
        return task;
    }

    private static UserTask CreateUserTask(
        string title,
        UserTaskType taskType,
        UserTaskStatus status = UserTaskStatus.Pending,
        UserTaskPriority priority = UserTaskPriority.Normal)
    {
        return new UserTask
        {
            Id = Guid.NewGuid(),
            Title = title,
            TaskType = taskType,
            Status = status,
            Priority = priority,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
    }

    #endregion
}
