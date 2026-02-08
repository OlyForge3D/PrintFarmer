using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Tasks;
using Farm.Infrastructure.Services.Tasks;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Services;

public class UserTaskServiceTests
{
    private readonly Mock<IUserTaskRepository> _repositoryMock;
    private readonly Mock<ILogger<UserTaskService>> _loggerMock;
    private readonly Mock<ITaskBroadcaster> _broadcasterMock;
    private readonly UserTaskService _service;

    public UserTaskServiceTests()
    {
        _repositoryMock = new Mock<IUserTaskRepository>();
        _loggerMock = new Mock<ILogger<UserTaskService>>();
        _broadcasterMock = new Mock<ITaskBroadcaster>();

        _service = new UserTaskService(
            _repositoryMock.Object,
            _loggerMock.Object,
            _broadcasterMock.Object);
    }

    #region GetPendingTasksAsync Tests

    [Fact]
    public async Task GetPendingTasksAsync_WithNoTasks_ReturnsEmptyList()
    {
        // Arrange
        _repositoryMock.Setup(r => r.GetPendingTasksAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<UserTask>());

        // Act
        IReadOnlyList<UserTaskDto> result = await _service.GetPendingTasksAsync();

        // Assert
        Assert.NotNull(result);
        Assert.Empty(result);
    }

    [Fact]
    public async Task GetPendingTasksAsync_WithTasks_ReturnsDtos()
    {
        // Arrange
        var task1 = CreateUserTask("Task 1", UserTaskType.ProfileImport);
        var task2 = CreateUserTask("Task 2", UserTaskType.MaintenanceDue);
        _repositoryMock.Setup(r => r.GetPendingTasksAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { task1, task2 });

        // Act
        IReadOnlyList<UserTaskDto> result = await _service.GetPendingTasksAsync();

        // Assert
        Assert.NotNull(result);
        Assert.Equal(2, result.Count);
        Assert.Equal(task1.Title, result[0].Title);
        Assert.Equal(task2.Title, result[1].Title);
    }

    #endregion

    #region GetByIdAsync Tests

    [Fact]
    public async Task GetByIdAsync_WithExistingTask_ReturnsDto()
    {
        // Arrange
        var taskId = Guid.NewGuid();
        var task = CreateUserTask("Test Task", UserTaskType.ProfileImport, taskId);
        _repositoryMock.Setup(r => r.GetByIdAsync(taskId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(task);

        // Act
        UserTaskDto? result = await _service.GetByIdAsync(taskId);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(taskId, result.Id);
        Assert.Equal("Test Task", result.Title);
    }

    [Fact]
    public async Task GetByIdAsync_WithNonExistingTask_ReturnsNull()
    {
        // Arrange
        var taskId = Guid.NewGuid();
        _repositoryMock.Setup(r => r.GetByIdAsync(taskId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((UserTask?)null);

        // Act
        UserTaskDto? result = await _service.GetByIdAsync(taskId);

        // Assert
        Assert.Null(result);
    }

    #endregion

    #region GetPendingCountAsync Tests

    [Fact]
    public async Task GetPendingCountAsync_ReturnsCount()
    {
        // Arrange
        _repositoryMock.Setup(r => r.GetPendingCountAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(5);

        // Act
        int result = await _service.GetPendingCountAsync();

        // Assert
        Assert.Equal(5, result);
    }

    #endregion

    #region CreateOrUpdateProfileImportTaskAsync Tests

    [Fact]
    public async Task CreateOrUpdateProfileImportTaskAsync_WithNoExistingTask_CreatesNewTask()
    {
        // Arrange
        var dto = new CreateProfileImportTaskDto(
            PrinterModelId: Guid.NewGuid(),
            PrinterModelName: "MK4S",
            ManufacturerName: "Prusa",
            PrinterId: Guid.NewGuid());

        _repositoryMock.Setup(r => r.GetByEntityAsync(
            UserTaskType.ProfileImport,
            "PrinterModel",
            dto.PrinterModelId,
            It.IsAny<CancellationToken>()))
            .ReturnsAsync((UserTask?)null);

        _repositoryMock.Setup(r => r.GetPendingCountAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        // Act
        UserTaskDto result = await _service.CreateOrUpdateProfileImportTaskAsync(dto);

        // Assert
        Assert.NotNull(result);
        Assert.Equal("Import slicer profiles for Prusa MK4S", result.Title);
        Assert.Equal(UserTaskType.ProfileImport, result.TaskType);
        Assert.Equal(UserTaskStatus.Pending, result.Status);
        Assert.Equal(UserTaskPriority.High, result.Priority);
        Assert.Equal(1, result.RelatedEntityCount);

        _repositoryMock.Verify(r => r.AddAsync(It.IsAny<UserTask>(), It.IsAny<CancellationToken>()), Times.Once);
        _broadcasterMock.Verify(b => b.BroadcastTaskCreatedAsync(It.IsAny<UserTaskDto>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CreateOrUpdateProfileImportTaskAsync_WithExistingTask_AddsPrinterToTask()
    {
        // Arrange
        var printerModelId = Guid.NewGuid();
        var existingPrinterId = Guid.NewGuid();
        var newPrinterId = Guid.NewGuid();

        var existingTask = CreateUserTask("Import slicer profiles for Prusa MK4S", UserTaskType.ProfileImport);
        existingTask.EntityType = "PrinterModel";
        existingTask.EntityId = printerModelId;
        existingTask.RelatedEntityIdsJson = $"[\"{existingPrinterId}\"]";

        var dto = new CreateProfileImportTaskDto(
            PrinterModelId: printerModelId,
            PrinterModelName: "MK4S",
            ManufacturerName: "Prusa",
            PrinterId: newPrinterId);

        _repositoryMock.Setup(r => r.GetByEntityAsync(
            UserTaskType.ProfileImport,
            "PrinterModel",
            printerModelId,
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(existingTask);

        _repositoryMock.Setup(r => r.GetPendingCountAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        // Act
        UserTaskDto result = await _service.CreateOrUpdateProfileImportTaskAsync(dto);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(2, result.RelatedEntityCount);
        Assert.Contains("2 printers waiting", existingTask.Description);

        _repositoryMock.Verify(r => r.UpdateAsync(existingTask, It.IsAny<CancellationToken>()), Times.Once);
        _broadcasterMock.Verify(b => b.BroadcastTaskUpdatedAsync(It.IsAny<UserTaskDto>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CreateOrUpdateProfileImportTaskAsync_WithExistingPrinter_DoesNotDuplicate()
    {
        // Arrange
        var printerModelId = Guid.NewGuid();
        var existingPrinterId = Guid.NewGuid();

        var existingTask = CreateUserTask("Import slicer profiles for Prusa MK4S", UserTaskType.ProfileImport);
        existingTask.EntityType = "PrinterModel";
        existingTask.EntityId = printerModelId;
        existingTask.RelatedEntityIdsJson = $"[\"{existingPrinterId}\"]";

        var dto = new CreateProfileImportTaskDto(
            PrinterModelId: printerModelId,
            PrinterModelName: "MK4S",
            ManufacturerName: "Prusa",
            PrinterId: existingPrinterId); // Same printer

        _repositoryMock.Setup(r => r.GetByEntityAsync(
            UserTaskType.ProfileImport,
            "PrinterModel",
            printerModelId,
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(existingTask);

        // Act
        UserTaskDto result = await _service.CreateOrUpdateProfileImportTaskAsync(dto);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(1, result.RelatedEntityCount); // Still 1, not duplicated

        _repositoryMock.Verify(r => r.UpdateAsync(It.IsAny<UserTask>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    #endregion

    #region CompleteTaskAsync Tests

    [Fact]
    public async Task CompleteTaskAsync_WithExistingTask_ReturnsTrue()
    {
        // Arrange
        var taskId = Guid.NewGuid();
        var task = CreateUserTask("Test Task", UserTaskType.ProfileImport, taskId);
        _repositoryMock.Setup(r => r.GetByIdAsync(taskId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(task);
        _repositoryMock.Setup(r => r.GetPendingCountAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);

        // Act
        bool result = await _service.CompleteTaskAsync(taskId);

        // Assert
        Assert.True(result);
        Assert.Equal(UserTaskStatus.Completed, task.Status);
        Assert.NotNull(task.CompletedAt);

        _repositoryMock.Verify(r => r.UpdateAsync(task, It.IsAny<CancellationToken>()), Times.Once);
        _broadcasterMock.Verify(b => b.BroadcastTaskUpdatedAsync(It.IsAny<UserTaskDto>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CompleteTaskAsync_WithNonExistingTask_ReturnsFalse()
    {
        // Arrange
        var taskId = Guid.NewGuid();
        _repositoryMock.Setup(r => r.GetByIdAsync(taskId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((UserTask?)null);

        // Act
        bool result = await _service.CompleteTaskAsync(taskId);

        // Assert
        Assert.False(result);
        _repositoryMock.Verify(r => r.UpdateAsync(It.IsAny<UserTask>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    #endregion

    #region DismissTaskAsync Tests

    [Fact]
    public async Task DismissTaskAsync_WithExistingTask_ReturnsTrue()
    {
        // Arrange
        var taskId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var task = CreateUserTask("Test Task", UserTaskType.ProfileImport, taskId);
        _repositoryMock.Setup(r => r.GetByIdAsync(taskId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(task);
        _repositoryMock.Setup(r => r.GetPendingCountAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);

        // Act
        bool result = await _service.DismissTaskAsync(taskId, userId);

        // Assert
        Assert.True(result);
        Assert.Equal(UserTaskStatus.Dismissed, task.Status);
        Assert.NotNull(task.DismissedAt);
        Assert.Equal(userId, task.DismissedByUserId);

        _repositoryMock.Verify(r => r.UpdateAsync(task, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DismissTaskAsync_WithNonExistingTask_ReturnsFalse()
    {
        // Arrange
        var taskId = Guid.NewGuid();
        _repositoryMock.Setup(r => r.GetByIdAsync(taskId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((UserTask?)null);

        // Act
        bool result = await _service.DismissTaskAsync(taskId);

        // Assert
        Assert.False(result);
    }

    #endregion

    #region SkipTaskAsync Tests

    [Fact]
    public async Task SkipTaskAsync_WithExistingTask_ReturnsTrue()
    {
        // Arrange
        var taskId = Guid.NewGuid();
        var task = CreateUserTask("Test Task", UserTaskType.ProfileImport, taskId);
        _repositoryMock.Setup(r => r.GetByIdAsync(taskId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(task);
        _repositoryMock.Setup(r => r.GetPendingCountAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        // Act
        bool result = await _service.SkipTaskAsync(taskId);

        // Assert
        Assert.True(result);
        Assert.Equal(UserTaskStatus.Skipped, task.Status);

        _repositoryMock.Verify(r => r.UpdateAsync(task, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SkipTaskAsync_WithNonExistingTask_ReturnsFalse()
    {
        // Arrange
        var taskId = Guid.NewGuid();
        _repositoryMock.Setup(r => r.GetByIdAsync(taskId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((UserTask?)null);

        // Act
        bool result = await _service.SkipTaskAsync(taskId);

        // Assert
        Assert.False(result);
    }

    #endregion

    #region HasPendingProfileImportTaskAsync Tests

    [Fact]
    public async Task HasPendingProfileImportTaskAsync_WithExistingTask_ReturnsTrue()
    {
        // Arrange
        var printerModelId = Guid.NewGuid();
        var task = CreateUserTask("Import profiles", UserTaskType.ProfileImport);
        _repositoryMock.Setup(r => r.GetByEntityAsync(
            UserTaskType.ProfileImport,
            "PrinterModel",
            printerModelId,
            It.IsAny<CancellationToken>()))
            .ReturnsAsync(task);

        // Act
        bool result = await _service.HasPendingProfileImportTaskAsync(printerModelId);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public async Task HasPendingProfileImportTaskAsync_WithNoTask_ReturnsFalse()
    {
        // Arrange
        var printerModelId = Guid.NewGuid();
        _repositoryMock.Setup(r => r.GetByEntityAsync(
            UserTaskType.ProfileImport,
            "PrinterModel",
            printerModelId,
            It.IsAny<CancellationToken>()))
            .ReturnsAsync((UserTask?)null);

        // Act
        bool result = await _service.HasPendingProfileImportTaskAsync(printerModelId);

        // Assert
        Assert.False(result);
    }

    #endregion

    #region Service Without Broadcaster Tests

    [Fact]
    public async Task CreateTask_WithNoBroadcaster_StillWorks()
    {
        // Arrange - service without broadcaster
        var serviceWithoutBroadcaster = new UserTaskService(
            _repositoryMock.Object,
            _loggerMock.Object,
            null); // No broadcaster

        var dto = new CreateProfileImportTaskDto(
            PrinterModelId: Guid.NewGuid(),
            PrinterModelName: "MK4S",
            ManufacturerName: "Prusa",
            PrinterId: Guid.NewGuid());

        _repositoryMock.Setup(r => r.GetByEntityAsync(
            UserTaskType.ProfileImport,
            "PrinterModel",
            dto.PrinterModelId,
            It.IsAny<CancellationToken>()))
            .ReturnsAsync((UserTask?)null);

        // Act
        UserTaskDto result = await serviceWithoutBroadcaster.CreateOrUpdateProfileImportTaskAsync(dto);

        // Assert
        Assert.NotNull(result);
        _repositoryMock.Verify(r => r.AddAsync(It.IsAny<UserTask>(), It.IsAny<CancellationToken>()), Times.Once);
        // Broadcaster should not be called since it's null
        _broadcasterMock.Verify(b => b.BroadcastTaskCreatedAsync(It.IsAny<UserTaskDto>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    #endregion

    #region Helper Methods

    private static UserTask CreateUserTask(string title, UserTaskType taskType, Guid? id = null)
    {
        return new UserTask
        {
            Id = id ?? Guid.NewGuid(),
            Title = title,
            TaskType = taskType,
            Status = UserTaskStatus.Pending,
            Priority = UserTaskPriority.Normal,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
    }

    #endregion
}
