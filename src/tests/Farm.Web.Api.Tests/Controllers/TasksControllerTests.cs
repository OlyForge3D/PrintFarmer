using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Tasks;
using Farm.Web.Api.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Controllers;

public class TasksControllerTests
{
    private readonly Mock<IUserTaskService> _taskServiceMock;
    private readonly TasksController _controller;

    public TasksControllerTests()
    {
        _taskServiceMock = new Mock<IUserTaskService>();
        _controller = new TasksController(_taskServiceMock.Object);
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext()
        };
    }

    #region GetPendingTasksAsync Tests

    [Fact]
    public async Task GetPendingTasksAsync_WithTasks_ReturnsOkWithTasks()
    {
        // Arrange
        var tasks = new List<UserTaskDto>
        {
            CreateUserTaskDto("Task 1", UserTaskType.ProfileImport),
            CreateUserTaskDto("Task 2", UserTaskType.MaintenanceDue)
        } as IReadOnlyList<UserTaskDto>;

        _taskServiceMock
            .Setup(s => s.GetPendingTasksAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(tasks);

        // Act
        ActionResult<IReadOnlyList<UserTaskDto>> result = await _controller.GetPendingTasksAsync(CancellationToken.None);

        // Assert
        OkObjectResult okResult = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal(tasks, okResult.Value);
        _taskServiceMock.Verify(s => s.GetPendingTasksAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetPendingTasksAsync_WithNoTasks_ReturnsOkWithEmptyList()
    {
        // Arrange
        IReadOnlyList<UserTaskDto> emptyTasks = new List<UserTaskDto>();
        _taskServiceMock
            .Setup(s => s.GetPendingTasksAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(emptyTasks);

        // Act
        ActionResult<IReadOnlyList<UserTaskDto>> result = await _controller.GetPendingTasksAsync(CancellationToken.None);

        // Assert
        OkObjectResult okResult = Assert.IsType<OkObjectResult>(result.Result);
        IReadOnlyList<UserTaskDto> returnedTasks = Assert.IsAssignableFrom<IReadOnlyList<UserTaskDto>>(okResult.Value);
        Assert.Empty(returnedTasks);
    }

    #endregion

    #region GetByIdAsync Tests

    [Fact]
    public async Task GetByIdAsync_WithExistingTask_ReturnsOkWithTask()
    {
        // Arrange
        var taskId = Guid.NewGuid();
        var task = CreateUserTaskDto("Test Task", UserTaskType.ProfileImport, taskId);

        _taskServiceMock
            .Setup(s => s.GetByIdAsync(taskId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(task);

        // Act
        ActionResult<UserTaskDto> result = await _controller.GetByIdAsync(taskId, CancellationToken.None);

        // Assert
        OkObjectResult okResult = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal(task, okResult.Value);
    }

    [Fact]
    public async Task GetByIdAsync_WithNonExistingTask_ReturnsNotFound()
    {
        // Arrange
        var taskId = Guid.NewGuid();
        _taskServiceMock
            .Setup(s => s.GetByIdAsync(taskId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((UserTaskDto?)null);

        // Act
        ActionResult<UserTaskDto> result = await _controller.GetByIdAsync(taskId, CancellationToken.None);

        // Assert
        Assert.IsType<NotFoundResult>(result.Result);
    }

    #endregion

    #region GetPendingCountAsync Tests

    [Fact]
    public async Task GetPendingCountAsync_ReturnsOkWithCount()
    {
        // Arrange
        _taskServiceMock
            .Setup(s => s.GetPendingCountAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(5);

        // Act
        ActionResult<PendingTaskCountDto> result = await _controller.GetPendingCountAsync(CancellationToken.None);

        // Assert
        OkObjectResult okResult = Assert.IsType<OkObjectResult>(result.Result);
        PendingTaskCountDto countDto = Assert.IsType<PendingTaskCountDto>(okResult.Value);
        Assert.Equal(5, countDto.Count);
    }

    [Fact]
    public async Task GetPendingCountAsync_WithNoTasks_ReturnsZero()
    {
        // Arrange
        _taskServiceMock
            .Setup(s => s.GetPendingCountAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);

        // Act
        ActionResult<PendingTaskCountDto> result = await _controller.GetPendingCountAsync(CancellationToken.None);

        // Assert
        OkObjectResult okResult = Assert.IsType<OkObjectResult>(result.Result);
        PendingTaskCountDto countDto = Assert.IsType<PendingTaskCountDto>(okResult.Value);
        Assert.Equal(0, countDto.Count);
    }

    #endregion

    #region CompleteAsync Tests

    [Fact]
    public async Task CompleteAsync_WithExistingTask_ReturnsNoContent()
    {
        // Arrange
        var taskId = Guid.NewGuid();
        _taskServiceMock
            .Setup(s => s.CompleteTaskAsync(taskId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        // Act
        IActionResult result = await _controller.CompleteAsync(taskId, CancellationToken.None);

        // Assert
        Assert.IsType<NoContentResult>(result);
        _taskServiceMock.Verify(s => s.CompleteTaskAsync(taskId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CompleteAsync_WithNonExistingTask_ReturnsNotFound()
    {
        // Arrange
        var taskId = Guid.NewGuid();
        _taskServiceMock
            .Setup(s => s.CompleteTaskAsync(taskId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        // Act
        IActionResult result = await _controller.CompleteAsync(taskId, CancellationToken.None);

        // Assert
        Assert.IsType<NotFoundResult>(result);
    }

    #endregion

    #region DismissAsync Tests

    [Fact]
    public async Task DismissAsync_WithExistingTask_ReturnsNoContent()
    {
        // Arrange
        var taskId = Guid.NewGuid();
        _taskServiceMock
            .Setup(s => s.DismissTaskAsync(taskId, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        // Act
        IActionResult result = await _controller.DismissAsync(taskId, CancellationToken.None);

        // Assert
        Assert.IsType<NoContentResult>(result);
        _taskServiceMock.Verify(s => s.DismissTaskAsync(taskId, null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DismissAsync_WithNonExistingTask_ReturnsNotFound()
    {
        // Arrange
        var taskId = Guid.NewGuid();
        _taskServiceMock
            .Setup(s => s.DismissTaskAsync(taskId, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        // Act
        IActionResult result = await _controller.DismissAsync(taskId, CancellationToken.None);

        // Assert
        Assert.IsType<NotFoundResult>(result);
    }

    #endregion

    #region SkipAsync Tests

    [Fact]
    public async Task SkipAsync_WithExistingTask_ReturnsNoContent()
    {
        // Arrange
        var taskId = Guid.NewGuid();
        _taskServiceMock
            .Setup(s => s.SkipTaskAsync(taskId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        // Act
        IActionResult result = await _controller.SkipAsync(taskId, CancellationToken.None);

        // Assert
        Assert.IsType<NoContentResult>(result);
        _taskServiceMock.Verify(s => s.SkipTaskAsync(taskId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SkipAsync_WithNonExistingTask_ReturnsNotFound()
    {
        // Arrange
        var taskId = Guid.NewGuid();
        _taskServiceMock
            .Setup(s => s.SkipTaskAsync(taskId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        // Act
        IActionResult result = await _controller.SkipAsync(taskId, CancellationToken.None);

        // Assert
        Assert.IsType<NotFoundResult>(result);
    }

    #endregion

    #region Helper Methods

    private static UserTaskDto CreateUserTaskDto(string title, UserTaskType taskType, Guid? id = null)
    {
        return new UserTaskDto(
            Id: id ?? Guid.NewGuid(),
            TaskType: taskType,
            EntityType: "PrinterModel",
            EntityId: Guid.NewGuid(),
            Title: title,
            Description: "Test description",
            Status: UserTaskStatus.Pending,
            Priority: UserTaskPriority.Normal,
            CreatedAt: DateTime.UtcNow,
            DueAt: null,
            CompletedAt: null,
            RelatedEntityCount: 1,
            MetadataJson: null);
    }

    #endregion
}
