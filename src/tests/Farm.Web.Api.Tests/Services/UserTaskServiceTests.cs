using System.Text.Json;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Tasks;
using Farm.Infrastructure.Services.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
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

        UserTask? addedTask = null;
        _repositoryMock.Setup(r => r.AddAsync(It.IsAny<UserTask>(), It.IsAny<CancellationToken>()))
            .Callback<UserTask, CancellationToken>((task, _) => addedTask = task)
            .Returns(Task.CompletedTask);
        _repositoryMock.Setup(r => r.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Returns((Guid id, CancellationToken _) => Task.FromResult<UserTask?>(addedTask?.Id == id ? addedTask : null));

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
        _repositoryMock.Setup(r => r.TryUpdateFieldsIfOpenAsync(
                existingTask,
                It.IsAny<IReadOnlyCollection<string>>(),
                It.IsAny<DateTime?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _repositoryMock.Setup(r => r.GetByIdAsync(existingTask.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(existingTask);

        // Act
        UserTaskDto result = await _service.CreateOrUpdateProfileImportTaskAsync(dto);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(2, result.RelatedEntityCount);
        Assert.Contains("2 printers waiting", existingTask.Description);

        // Fix R4-2: the import path must patch only the columns it changes via
        // UpdateFieldsAsync — never a full-row UpdateAsync — so a concurrent user
        // Complete/Skip/Dismiss is not clobbered back to Pending. Status must NOT be
        // among the written columns.
        _repositoryMock.Verify(
            r => r.TryUpdateFieldsIfOpenAsync(
                existingTask,
                It.Is<IReadOnlyCollection<string>>(props =>
                    props.Contains(nameof(UserTask.RelatedEntityIdsJson))
                    && props.Contains(nameof(UserTask.Description))
                    && !props.Contains(nameof(UserTask.Status))),
                existingTask.UpdatedAt,
                It.IsAny<CancellationToken>()),
            Times.Once);
        _repositoryMock.Verify(r => r.UpdateFieldsAsync(It.IsAny<UserTask>(), It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()), Times.Never);
        _repositoryMock.Verify(r => r.UpdateAsync(It.IsAny<UserTask>(), It.IsAny<CancellationToken>()), Times.Never);
        _broadcasterMock.Verify(
            b => b.BroadcastTaskUpdatedAsync(
                It.Is<UserTaskDto>(task => task.Status == UserTaskStatus.Pending && task.RelatedEntityCount == 2),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task CreateOrUpdateProfileImportTaskAsync_ExistingTaskBecameTerminal_CreatesNewTaskAndDoesNotBroadcastStaleUpdate()
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

        _repositoryMock.Setup(r => r.TryUpdateFieldsIfOpenAsync(
                existingTask,
                It.IsAny<IReadOnlyCollection<string>>(),
                It.IsAny<DateTime?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        _repositoryMock.Setup(r => r.GetPendingCountAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        UserTask? added = null;
        _repositoryMock.Setup(r => r.AddAsync(It.IsAny<UserTask>(), It.IsAny<CancellationToken>()))
            .Callback<UserTask, CancellationToken>((task, _) => added = task)
            .Returns(Task.CompletedTask);
        _repositoryMock.Setup(r => r.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Returns((Guid id, CancellationToken _) => Task.FromResult<UserTask?>(added?.Id == id ? added : null));

        // Act
        UserTaskDto result = await _service.CreateOrUpdateProfileImportTaskAsync(dto);

        // Assert
        Assert.NotNull(added);
        Assert.NotEqual(existingTask.Id, result.Id);
        Assert.Equal(UserTaskStatus.Pending, result.Status);
        Assert.Equal(1, result.RelatedEntityCount);

        _repositoryMock.Verify(r => r.AddAsync(It.IsAny<UserTask>(), It.IsAny<CancellationToken>()), Times.Once);
        _broadcasterMock.Verify(b => b.BroadcastTaskUpdatedAsync(It.IsAny<UserTaskDto>(), It.IsAny<CancellationToken>()), Times.Never);
        _broadcasterMock.Verify(
            b => b.BroadcastTaskCreatedAsync(
                It.Is<UserTaskDto>(task => task.Id == added.Id && task.Status == UserTaskStatus.Pending),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /// <summary>
    /// Fix R6-3: two workers that both observed a terminal transition can race to
    /// recover the profile-import task. The unique index allows one insert; the loser
    /// refreshes that open task and emits an update rather than another created event.
    /// </summary>
    [Fact]
    public async Task CreateOrUpdateProfileImportTaskAsync_ConcurrentRecoveryInserts_CreatesOneOpenTaskAndOneCreatedBroadcast()
    {
        Guid printerModelId = Guid.NewGuid();
        Guid existingPrinterId = Guid.NewGuid();
        Guid importedPrinterId = Guid.NewGuid();
        Guid concurrentPrinterId = Guid.NewGuid();
        UserTask terminalTask = CreateUserTask("Import slicer profiles for Prusa MK4S", UserTaskType.ProfileImport);
        terminalTask.EntityType = "PrinterModel";
        terminalTask.EntityId = printerModelId;
        terminalTask.RelatedEntityIdsJson = $"[\"{existingPrinterId}\"]";

        CreateProfileImportTaskDto dto = new(
            PrinterModelId: printerModelId,
            PrinterModelName: "MK4S",
            ManufacturerName: "Prusa",
            PrinterId: importedPrinterId);
        CreateProfileImportTaskDto concurrentDto = new(
            PrinterModelId: printerModelId,
            PrinterModelName: "MK4S",
            ManufacturerName: "Prusa",
            PrinterId: concurrentPrinterId);
        List<UserTask> openTasks = [];
        object sync = new();

        UserTask CreateTerminalRead() => new()
        {
            Id = terminalTask.Id,
            TaskType = terminalTask.TaskType,
            EntityType = terminalTask.EntityType,
            EntityId = terminalTask.EntityId,
            Title = terminalTask.Title,
            Description = terminalTask.Description,
            Status = terminalTask.Status,
            Priority = terminalTask.Priority,
            CreatedAt = terminalTask.CreatedAt,
            UpdatedAt = terminalTask.UpdatedAt,
            RelatedEntityIdsJson = terminalTask.RelatedEntityIdsJson,
            SourceKind = terminalTask.SourceKind,
            SourceId = terminalTask.SourceId,
        };

        Task<UserTask?> GetTaskForRaceAsync()
        {
            lock (sync)
            {
                return Task.FromResult<UserTask?>(openTasks.Count == 0 ? CreateTerminalRead() : openTasks.SingleOrDefault());
            }
        }

        _repositoryMock.Setup(r => r.GetByEntityAsync(
                UserTaskType.ProfileImport,
                "PrinterModel",
                printerModelId,
                It.IsAny<CancellationToken>()))
            .Returns((UserTaskType _, string _, Guid _, CancellationToken _) => GetTaskForRaceAsync());
        _repositoryMock.Setup(r => r.TryUpdateFieldsIfOpenAsync(
                It.IsAny<UserTask>(),
                It.IsAny<IReadOnlyCollection<string>>(),
                It.IsAny<DateTime?>(),
                It.IsAny<CancellationToken>()))
            .Returns((UserTask task, IReadOnlyCollection<string> _, DateTime? _, CancellationToken _) =>
                Task.FromResult(task.Id != terminalTask.Id));
        _repositoryMock.Setup(r => r.AddAsync(It.IsAny<UserTask>(), It.IsAny<CancellationToken>()))
            .Returns((UserTask task, CancellationToken _) =>
            {
                lock (sync)
                {
                    if (openTasks.Count > 0)
                    {
                        return Task.FromException(new DbUpdateException(
                            "insert failed",
                            new SqliteException("UNIQUE constraint failed: UserTasks.TaskType, UserTasks.EntityType, UserTasks.EntityId", 19, 2067)));
                    }

                    openTasks.Add(task);
                    return Task.CompletedTask;
                }
            });
        _repositoryMock.Setup(r => r.DetachTrackedAsync(It.IsAny<IEnumerable<UserTask>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _repositoryMock.Setup(r => r.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Returns((Guid id, CancellationToken _) =>
            {
                lock (sync)
                {
                    return Task.FromResult<UserTask?>(openTasks.SingleOrDefault(task => task.Id == id));
                }
            });
        _repositoryMock.Setup(r => r.GetPendingCountAsync(null, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);
        _broadcasterMock.Setup(b => b.BroadcastTaskCreatedAsync(It.IsAny<UserTaskDto>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _broadcasterMock.Setup(b => b.BroadcastTaskUpdatedAsync(It.IsAny<UserTaskDto>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        UserTaskService concurrentService = new(
            _repositoryMock.Object,
            _loggerMock.Object,
            _broadcasterMock.Object);

        UserTaskDto[] result = await Task.WhenAll(
            _service.CreateOrUpdateProfileImportTaskAsync(dto),
            concurrentService.CreateOrUpdateProfileImportTaskAsync(concurrentDto));

        lock (sync)
        {
            Assert.Single(openTasks);
            Assert.All(result, task => Assert.Equal(openTasks[0].Id, task.Id));
        }

        Assert.Equal(2, result.Length);
        _broadcasterMock.Verify(
            b => b.BroadcastTaskCreatedAsync(It.IsAny<UserTaskDto>(), It.IsAny<CancellationToken>()),
            Times.Once);
        _broadcasterMock.Verify(
            b => b.BroadcastTaskUpdatedAsync(It.IsAny<UserTaskDto>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task CreateOrUpdateProfileImportTaskAsync_ConcurrentFirstCreates_LeavesOneOpenTaskWithBothPrinters()
    {
        Guid printerModelId = Guid.NewGuid();
        Guid firstPrinterId = Guid.NewGuid();
        Guid secondPrinterId = Guid.NewGuid();
        ProfileImportRepositoryState state = new();
        Mock<IUserTaskRepository> repository = CreateProfileImportRepository(state);
        List<UserTaskDto> createdBroadcasts = [];
        List<UserTaskDto> updatedBroadcasts = [];
        Mock<ITaskBroadcaster> broadcaster = CreateRecordingBroadcaster(createdBroadcasts, updatedBroadcasts);
        UserTaskService firstService = new(repository.Object, _loggerMock.Object, broadcaster.Object);
        UserTaskService secondService = new(repository.Object, _loggerMock.Object, broadcaster.Object);

        UserTaskDto[] results = await Task.WhenAll(
            firstService.CreateOrUpdateProfileImportTaskAsync(CreateProfileImportDto(printerModelId, firstPrinterId)),
            secondService.CreateOrUpdateProfileImportTaskAsync(CreateProfileImportDto(printerModelId, secondPrinterId)));

        UserTask finalTask = state.GetOpenTask(printerModelId);
        List<Guid> relatedPrinterIds = JsonSerializer.Deserialize<List<Guid>>(finalTask.RelatedEntityIdsJson!)!;

        Assert.Equal(2, results.Length);
        Assert.Equal(2, relatedPrinterIds.Count);
        Assert.Contains(firstPrinterId, relatedPrinterIds);
        Assert.Contains(secondPrinterId, relatedPrinterIds);
        Assert.Single(createdBroadcasts);
        Assert.Single(updatedBroadcasts);
    }

    [Fact]
    public async Task CreateOrUpdateProfileImportTaskAsync_ConcurrentMerges_RepeatedContentionRetainsEveryPrinterId()
    {
        for (int iteration = 0; iteration < 50; iteration++)
        {
            Guid printerModelId = Guid.NewGuid();
            Guid existingPrinterId = Guid.NewGuid();
            Guid firstImportedPrinterId = Guid.NewGuid();
            Guid secondImportedPrinterId = Guid.NewGuid();
            ProfileImportRepositoryState state = new();
            state.Seed(CreateProfileImportTaskForTest(printerModelId, existingPrinterId));
            Mock<IUserTaskRepository> repository = CreateProfileImportRepository(state);
            UserTaskService firstService = new(repository.Object, _loggerMock.Object);
            UserTaskService secondService = new(repository.Object, _loggerMock.Object);

            _ = await Task.WhenAll(
                firstService.CreateOrUpdateProfileImportTaskAsync(CreateProfileImportDto(printerModelId, firstImportedPrinterId)),
                secondService.CreateOrUpdateProfileImportTaskAsync(CreateProfileImportDto(printerModelId, secondImportedPrinterId)));

            UserTask finalTask = state.GetOpenTask(printerModelId);
            List<Guid> relatedPrinterIds = JsonSerializer.Deserialize<List<Guid>>(finalTask.RelatedEntityIdsJson!)!;

            Assert.Equal(3, relatedPrinterIds.Count);
            Assert.Contains(existingPrinterId, relatedPrinterIds);
            Assert.Contains(firstImportedPrinterId, relatedPrinterIds);
            Assert.Contains(secondImportedPrinterId, relatedPrinterIds);
        }
    }

    [Fact]
    public async Task CreateOrUpdateProfileImportTaskAsync_ConcurrentFirstCreate_LastBroadcastContainsMergedPrinterCount()
    {
        Guid printerModelId = Guid.NewGuid();
        Guid firstPrinterId = Guid.NewGuid();
        Guid secondPrinterId = Guid.NewGuid();
        ProfileImportRepositoryState state = new();
        Mock<IUserTaskRepository> repository = CreateProfileImportRepository(state);
        List<UserTaskDto> createdBroadcasts = [];
        List<UserTaskDto> updatedBroadcasts = [];
        List<UserTaskDto> orderedBroadcasts = [];
        Mock<ITaskBroadcaster> broadcaster = CreateRecordingBroadcaster(createdBroadcasts, updatedBroadcasts, orderedBroadcasts);
        UserTaskService firstService = new(repository.Object, _loggerMock.Object, broadcaster.Object);
        UserTaskService secondService = new(repository.Object, _loggerMock.Object, broadcaster.Object);

        _ = await Task.WhenAll(
            firstService.CreateOrUpdateProfileImportTaskAsync(CreateProfileImportDto(printerModelId, firstPrinterId)),
            secondService.CreateOrUpdateProfileImportTaskAsync(CreateProfileImportDto(printerModelId, secondPrinterId)));

        Assert.NotEmpty(orderedBroadcasts);
        UserTaskDto lastBroadcast = orderedBroadcasts[^1];
        Assert.Equal(2, lastBroadcast.RelatedEntityCount);
        Assert.Equal(2, JsonSerializer.Deserialize<List<Guid>>(state.GetOpenTask(printerModelId).RelatedEntityIdsJson!)!.Count);
    }

    [Fact]
    public async Task CreateOrUpdateProfileImportTaskAsync_ConcurrentMerges_SerializesPerTask_LastBroadcastReflectsLatestState_InverseInterleaving()
    {
        Guid printerModelId = Guid.NewGuid();
        Guid firstPrinterId = Guid.NewGuid();
        Guid secondPrinterId = Guid.NewGuid();
        InverseInterleavingProfileImportRepositoryState state = new();
        Mock<IUserTaskRepository> repository = CreateInverseInterleavingProfileImportRepository(state);
        List<UserTaskDto> createdBroadcasts = [];
        List<UserTaskDto> updatedBroadcasts = [];
        List<UserTaskDto> orderedBroadcasts = [];
        TaskCompletionSource<bool> firstCreatedBroadcastEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<bool> allowFirstCreatedBroadcast = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<bool> updatedBroadcastCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Mock<ITaskBroadcaster> broadcaster = CreateBlockingCreatedBroadcastBroadcaster(
            createdBroadcasts,
            updatedBroadcasts,
            orderedBroadcasts,
            firstCreatedBroadcastEntered,
            allowFirstCreatedBroadcast,
            updatedBroadcastCompleted);
        UserTaskService firstService = new(repository.Object, _loggerMock.Object, broadcaster.Object);
        UserTaskService secondService = new(repository.Object, _loggerMock.Object, broadcaster.Object);

        Task<UserTaskDto> firstOperation = firstService.CreateOrUpdateProfileImportTaskAsync(
            CreateProfileImportDto(printerModelId, firstPrinterId));
        await state.FirstAddAttempted.WaitAsync(TimeSpan.FromSeconds(5));

        Task<UserTaskDto> secondOperation = secondService.CreateOrUpdateProfileImportTaskAsync(
            CreateProfileImportDto(printerModelId, secondPrinterId));
        bool secondAddArmedBeforeInsert = await WaitForSignalAsync(state.SecondAddAttempted, TimeSpan.FromSeconds(1));

        state.AllowFirstInsert();
        await firstCreatedBroadcastEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        state.AllowConflictingInsert();

        if (secondAddArmedBeforeInsert)
        {
            await updatedBroadcastCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }

        _ = allowFirstCreatedBroadcast.TrySetResult(true);

        UserTaskDto[] results = await Task.WhenAll(firstOperation, secondOperation);
        UserTask finalTask = state.GetOpenTask(printerModelId);
        List<Guid> relatedPrinterIds = JsonSerializer.Deserialize<List<Guid>>(finalTask.RelatedEntityIdsJson!)!;

        Assert.Equal(2, results.Length);
        Assert.All(results, result => Assert.Equal(finalTask.Id, result.Id));
        Assert.Single(createdBroadcasts);
        Assert.Single(updatedBroadcasts);
        Assert.Equal(2, orderedBroadcasts.Count);
        Assert.Equal(1, orderedBroadcasts[0].RelatedEntityCount);
        Assert.Equal(2, orderedBroadcasts[1].RelatedEntityCount);
        Assert.Equal(2, relatedPrinterIds.Count);
        Assert.Contains(firstPrinterId, relatedPrinterIds);
        Assert.Contains(secondPrinterId, relatedPrinterIds);
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

        _repositoryMock.Verify(
            r => r.UpdateFieldsAsync(
                task,
                It.Is<IReadOnlyCollection<string>>(p => p.Contains(nameof(UserTask.Status)) && p.Contains(nameof(UserTask.CompletedAt))),
                It.IsAny<CancellationToken>()),
            Times.Once);
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

        _repositoryMock.Verify(
            r => r.UpdateFieldsAsync(
                task,
                It.Is<IReadOnlyCollection<string>>(p => p.Contains(nameof(UserTask.Status))
                    && p.Contains(nameof(UserTask.DismissedAt))
                    && p.Contains(nameof(UserTask.DismissedByUserId))),
                It.IsAny<CancellationToken>()),
            Times.Once);
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

        _repositoryMock.Verify(
            r => r.UpdateFieldsAsync(
                task,
                It.Is<IReadOnlyCollection<string>>(p => p.Contains(nameof(UserTask.Status))),
                It.IsAny<CancellationToken>()),
            Times.Once);
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

    private static CreateProfileImportTaskDto CreateProfileImportDto(Guid printerModelId, Guid printerId) =>
        new(
            PrinterModelId: printerModelId,
            PrinterModelName: "MK4S",
            ManufacturerName: "Prusa",
            PrinterId: printerId);

    private static UserTask CreateProfileImportTaskForTest(Guid printerModelId, Guid printerId)
    {
        UserTask task = CreateUserTask("Import slicer profiles for Prusa MK4S", UserTaskType.ProfileImport);
        task.EntityType = "PrinterModel";
        task.EntityId = printerModelId;
        task.RelatedEntityIdsJson = JsonSerializer.Serialize(new[] { printerId });
        task.Description = "1 printer waiting for slicer profiles";
        return task;
    }

    private static Mock<IUserTaskRepository> CreateProfileImportRepository(ProfileImportRepositoryState state)
    {
        Mock<IUserTaskRepository> repository = new();
        repository.Setup(r => r.GetByEntityAsync(
                UserTaskType.ProfileImport,
                "PrinterModel",
                It.IsAny<Guid>(),
                It.IsAny<CancellationToken>()))
            .Returns((UserTaskType _, string _, Guid entityId, CancellationToken ct) => state.GetOpenTaskAsync(entityId, ct));
        repository.Setup(r => r.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Returns((Guid id, CancellationToken ct) => state.GetByIdAsync(id, ct));
        repository.Setup(r => r.AddAsync(It.IsAny<UserTask>(), It.IsAny<CancellationToken>()))
            .Returns((UserTask task, CancellationToken ct) => state.AddAsync(task, ct));
        repository.Setup(r => r.TryUpdateFieldsIfOpenAsync(
                It.IsAny<UserTask>(),
                It.IsAny<IReadOnlyCollection<string>>(),
                It.IsAny<DateTime?>(),
                It.IsAny<CancellationToken>()))
            .Returns((UserTask task, IReadOnlyCollection<string> _, DateTime? expectedUpdatedAt, CancellationToken ct) =>
                state.TryUpdateFieldsIfOpenAsync(task, expectedUpdatedAt, ct));
        repository.Setup(r => r.DetachTrackedAsync(It.IsAny<IEnumerable<UserTask>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        repository.Setup(r => r.GetPendingCountAsync(null, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);
        return repository;
    }

    private static Mock<IUserTaskRepository> CreateInverseInterleavingProfileImportRepository(
        InverseInterleavingProfileImportRepositoryState state)
    {
        Mock<IUserTaskRepository> repository = new();
        repository.Setup(r => r.GetByEntityAsync(
                UserTaskType.ProfileImport,
                "PrinterModel",
                It.IsAny<Guid>(),
                It.IsAny<CancellationToken>()))
            .Returns((UserTaskType _, string _, Guid entityId, CancellationToken ct) => state.GetOpenTaskAsync(entityId, ct));
        repository.Setup(r => r.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Returns((Guid id, CancellationToken ct) => state.GetByIdAsync(id, ct));
        repository.Setup(r => r.AddAsync(It.IsAny<UserTask>(), It.IsAny<CancellationToken>()))
            .Returns((UserTask task, CancellationToken ct) => state.AddAsync(task, ct));
        repository.Setup(r => r.TryUpdateFieldsIfOpenAsync(
                It.IsAny<UserTask>(),
                It.IsAny<IReadOnlyCollection<string>>(),
                It.IsAny<DateTime?>(),
                It.IsAny<CancellationToken>()))
            .Returns((UserTask task, IReadOnlyCollection<string> _, DateTime? expectedUpdatedAt, CancellationToken ct) =>
                state.TryUpdateFieldsIfOpenAsync(task, expectedUpdatedAt, ct));
        repository.Setup(r => r.DetachTrackedAsync(It.IsAny<IEnumerable<UserTask>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        repository.Setup(r => r.GetPendingCountAsync(null, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);
        return repository;
    }

    private static Mock<ITaskBroadcaster> CreateRecordingBroadcaster(
        List<UserTaskDto> createdBroadcasts,
        List<UserTaskDto> updatedBroadcasts,
        List<UserTaskDto>? orderedBroadcasts = null)
    {
        Mock<ITaskBroadcaster> broadcaster = new();
        broadcaster.Setup(b => b.BroadcastTaskCreatedAsync(It.IsAny<UserTaskDto>(), It.IsAny<CancellationToken>()))
            .Callback<UserTaskDto, CancellationToken>((task, _) =>
            {
                lock (createdBroadcasts)
                {
                    createdBroadcasts.Add(task);
                }

                if (orderedBroadcasts is not null)
                {
                    lock (orderedBroadcasts)
                    {
                        orderedBroadcasts.Add(task);
                    }
                }
            })
            .Returns(Task.CompletedTask);
        broadcaster.Setup(b => b.BroadcastTaskUpdatedAsync(It.IsAny<UserTaskDto>(), It.IsAny<CancellationToken>()))
            .Callback<UserTaskDto, CancellationToken>((task, _) =>
            {
                lock (updatedBroadcasts)
                {
                    updatedBroadcasts.Add(task);
                }

                if (orderedBroadcasts is not null)
                {
                    lock (orderedBroadcasts)
                    {
                        orderedBroadcasts.Add(task);
                    }
                }
            })
            .Returns(Task.CompletedTask);
        broadcaster.Setup(b => b.BroadcastPendingTaskCountAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        return broadcaster;
    }

    private static Mock<ITaskBroadcaster> CreateBlockingCreatedBroadcastBroadcaster(
        List<UserTaskDto> createdBroadcasts,
        List<UserTaskDto> updatedBroadcasts,
        List<UserTaskDto> orderedBroadcasts,
        TaskCompletionSource<bool> firstCreatedBroadcastEntered,
        TaskCompletionSource<bool> allowFirstCreatedBroadcast,
        TaskCompletionSource<bool> updatedBroadcastCompleted)
    {
        Mock<ITaskBroadcaster> broadcaster = new();
        int createdBroadcastCount = 0;
        broadcaster.Setup(b => b.BroadcastTaskCreatedAsync(It.IsAny<UserTaskDto>(), It.IsAny<CancellationToken>()))
            .Returns(async (UserTaskDto task, CancellationToken ct) =>
            {
                if (Interlocked.Increment(ref createdBroadcastCount) == 1)
                {
                    _ = firstCreatedBroadcastEntered.TrySetResult(true);
                    await allowFirstCreatedBroadcast.Task.WaitAsync(ct);
                }

                lock (createdBroadcasts)
                {
                    createdBroadcasts.Add(task);
                }

                lock (orderedBroadcasts)
                {
                    orderedBroadcasts.Add(task);
                }
            });
        broadcaster.Setup(b => b.BroadcastTaskUpdatedAsync(It.IsAny<UserTaskDto>(), It.IsAny<CancellationToken>()))
            .Returns((UserTaskDto task, CancellationToken ct) =>
            {
                lock (updatedBroadcasts)
                {
                    updatedBroadcasts.Add(task);
                }

                lock (orderedBroadcasts)
                {
                    orderedBroadcasts.Add(task);
                }

                _ = updatedBroadcastCompleted.TrySetResult(true);
                return Task.CompletedTask;
            });
        broadcaster.Setup(b => b.BroadcastPendingTaskCountAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        return broadcaster;
    }

    private static async Task<bool> WaitForSignalAsync(Task signal, TimeSpan timeout)
    {
#pragma warning disable VSTHRD003 // signal is a caller-supplied Task (typically a TaskCompletionSource) this helper waits on with a timeout; not a foreign/UI-thread task in the deadlock sense the rule targets.
        Task completedTask = await Task.WhenAny(signal, Task.Delay(timeout));
#pragma warning restore VSTHRD003
        return completedTask == signal;
    }

    private sealed class ProfileImportRepositoryState
    {
        private readonly object _sync = new();
        private readonly Dictionary<Guid, UserTask> _tasks = [];

        public void Seed(UserTask task)
        {
            lock (_sync)
            {
                _tasks[task.Id] = Clone(task);
            }
        }

        public Task<UserTask?> GetOpenTaskAsync(Guid printerModelId, CancellationToken _)
        {
            lock (_sync)
            {
                UserTask? task = _tasks.Values.SingleOrDefault(task =>
                    task.EntityId == printerModelId
                    && task.TaskType == UserTaskType.ProfileImport
                    && task.EntityType == "PrinterModel"
                    && task.Status is UserTaskStatus.Pending or UserTaskStatus.InProgress);
                return Task.FromResult(task is null ? null : Clone(task));
            }
        }

        public Task<UserTask?> GetByIdAsync(Guid id, CancellationToken _)
        {
            lock (_sync)
            {
                return Task.FromResult(_tasks.TryGetValue(id, out UserTask? task) ? Clone(task) : null);
            }
        }

        public Task AddAsync(UserTask task, CancellationToken _)
        {
            lock (_sync)
            {
                bool hasOpenTask = _tasks.Values.Any(existing =>
                    existing.EntityId == task.EntityId
                    && existing.TaskType == UserTaskType.ProfileImport
                    && existing.EntityType == "PrinterModel"
                    && existing.Status is UserTaskStatus.Pending or UserTaskStatus.InProgress);
                if (hasOpenTask)
                {
                    throw new DbUpdateException(
                        "insert failed",
                        new SqliteException(
                            "UNIQUE constraint failed: UserTasks.TaskType, UserTasks.EntityType, UserTasks.EntityId",
                            19,
                            2067));
                }

                _tasks[task.Id] = Clone(task);
            }

            return Task.CompletedTask;
        }

        public Task<bool> TryUpdateFieldsIfOpenAsync(UserTask task, DateTime? expectedUpdatedAt, CancellationToken _)
        {
            lock (_sync)
            {
                if (!_tasks.TryGetValue(task.Id, out UserTask? persisted)
                    || persisted.Status is not (UserTaskStatus.Pending or UserTaskStatus.InProgress)
                    || (expectedUpdatedAt.HasValue && persisted.UpdatedAt != expectedUpdatedAt.Value))
                {
                    return Task.FromResult(false);
                }
                persisted.RelatedEntityIdsJson = task.RelatedEntityIdsJson;
                persisted.Description = task.Description;
                persisted.UpdatedAt = persisted.UpdatedAt.AddTicks(1);
                persisted.UpdatedAt = persisted.UpdatedAt.AddTicks(1);
                return Task.FromResult(true);
            }
        }

        public UserTask GetOpenTask(Guid printerModelId)
        {
            lock (_sync)
            {
                UserTask task = _tasks.Values.Single(task =>
                    task.EntityId == printerModelId
                    && task.TaskType == UserTaskType.ProfileImport
                    && task.EntityType == "PrinterModel"
                    && task.Status is UserTaskStatus.Pending or UserTaskStatus.InProgress);
                return Clone(task);
            }
        }

        private static UserTask Clone(UserTask task) => new()
        {
            Id = task.Id,
            TaskType = task.TaskType,
            EntityType = task.EntityType,
            EntityId = task.EntityId,
            Title = task.Title,
            Description = task.Description,
            Status = task.Status,
            Priority = task.Priority,
            CreatedAt = task.CreatedAt,
            UpdatedAt = task.UpdatedAt,
            MetadataJson = task.MetadataJson,
            RelatedEntityIdsJson = task.RelatedEntityIdsJson,
            SourceKind = task.SourceKind,
            SourceId = task.SourceId,
        };
    }

    private sealed class InverseInterleavingProfileImportRepositoryState
    {
        private readonly object _sync = new();
        private readonly Dictionary<Guid, UserTask> _tasks = [];
        private readonly TaskCompletionSource<bool> _firstAddAttempted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _allowFirstInsert =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _firstInsertCompleted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _secondAddAttempted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _allowConflictingInsert =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _addAttemptCount;

        public Task FirstAddAttempted => _firstAddAttempted.Task;

        public Task SecondAddAttempted => _secondAddAttempted.Task;

        public void AllowFirstInsert()
        {
            _ = _allowFirstInsert.TrySetResult(true);
        }

        public void AllowConflictingInsert()
        {
            _ = _allowConflictingInsert.TrySetResult(true);
        }

        public Task<UserTask?> GetOpenTaskAsync(Guid printerModelId, CancellationToken _)
        {
            lock (_sync)
            {
                UserTask? task = _tasks.Values.SingleOrDefault(task =>
                    task.EntityId == printerModelId
                    && task.TaskType == UserTaskType.ProfileImport
                    && task.EntityType == "PrinterModel"
                    && task.Status is UserTaskStatus.Pending or UserTaskStatus.InProgress);
                return Task.FromResult(task is null ? null : Clone(task));
            }
        }

        public Task<UserTask?> GetByIdAsync(Guid id, CancellationToken _)
        {
            lock (_sync)
            {
                return Task.FromResult(_tasks.TryGetValue(id, out UserTask? task) ? Clone(task) : null);
            }
        }

        public async Task AddAsync(UserTask task, CancellationToken ct)
        {
            int attemptNumber = Interlocked.Increment(ref _addAttemptCount);
            if (attemptNumber == 1)
            {
                _ = _firstAddAttempted.TrySetResult(true);
                await _allowFirstInsert.Task.WaitAsync(ct);

                lock (_sync)
                {
                    _tasks[task.Id] = Clone(task);
                }

                _ = _firstInsertCompleted.TrySetResult(true);
                return;
            }

            if (attemptNumber == 2)
            {
                _ = _secondAddAttempted.TrySetResult(true);
                await _firstInsertCompleted.Task.WaitAsync(ct);
                await _allowConflictingInsert.Task.WaitAsync(ct);
                throw CreateUniqueConstraintException();
            }

            lock (_sync)
            {
                bool hasOpenTask = _tasks.Values.Any(existing =>
                    existing.EntityId == task.EntityId
                    && existing.TaskType == UserTaskType.ProfileImport
                    && existing.EntityType == "PrinterModel"
                    && existing.Status is UserTaskStatus.Pending or UserTaskStatus.InProgress);
                if (hasOpenTask)
                {
                    throw CreateUniqueConstraintException();
                }

                _tasks[task.Id] = Clone(task);
            }
        }

        public Task<bool> TryUpdateFieldsIfOpenAsync(UserTask task, DateTime? expectedUpdatedAt, CancellationToken _)
        {
            lock (_sync)
            {
                if (!_tasks.TryGetValue(task.Id, out UserTask? persisted)
                    || persisted.Status is not (UserTaskStatus.Pending or UserTaskStatus.InProgress)
                    || (expectedUpdatedAt.HasValue && persisted.UpdatedAt != expectedUpdatedAt.Value))
                {
                    return Task.FromResult(false);
                }

                persisted.RelatedEntityIdsJson = task.RelatedEntityIdsJson;
                persisted.Description = task.Description;
                persisted.UpdatedAt = persisted.UpdatedAt.AddTicks(1);
                return Task.FromResult(true);
            }
        }

        public UserTask GetOpenTask(Guid printerModelId)
        {
            lock (_sync)
            {
                UserTask task = _tasks.Values.Single(task =>
                    task.EntityId == printerModelId
                    && task.TaskType == UserTaskType.ProfileImport
                    && task.EntityType == "PrinterModel"
                    && task.Status is UserTaskStatus.Pending or UserTaskStatus.InProgress);
                return Clone(task);
            }
        }

        private static DbUpdateException CreateUniqueConstraintException() =>
            new(
                "insert failed",
                new SqliteException(
                    "UNIQUE constraint failed: UserTasks.TaskType, UserTasks.EntityType, UserTasks.EntityId",
                    19,
                    2067));

        private static UserTask Clone(UserTask task) => new()
        {
            Id = task.Id,
            TaskType = task.TaskType,
            EntityType = task.EntityType,
            EntityId = task.EntityId,
            Title = task.Title,
            Description = task.Description,
            Status = task.Status,
            Priority = task.Priority,
            CreatedAt = task.CreatedAt,
            UpdatedAt = task.UpdatedAt,
            MetadataJson = task.MetadataJson,
            RelatedEntityIdsJson = task.RelatedEntityIdsJson,
            SourceKind = task.SourceKind,
            SourceId = task.SourceId,
        };
    }

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
