using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Tasks;
using FluentAssertions;
using Xunit;

namespace Farm.Web.Api.Tests.Infrastructure;

public class EfUserTaskRepositoryTests : IDisposable
{
    private readonly AppDbContext _context;
    private readonly EfUserTaskRepository _repository;

    public EfUserTaskRepositoryTests()
    {
        _context = TestInfrastructure.TestHelpers.CreateSqliteInMemoryDb();
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

    #region Helper Methods

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
