using System.Security.Claims;
using System.Text.Json;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.OperatorFeatures;
using Farm.Infrastructure.Services.Tasks;
using Farm.Modules.Observability.Controllers;
using Farm.Web.Api.Controllers;
using FluentValidation;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Controllers;

public class TasksControllerTests
{
    private readonly Mock<IUserTaskService> _taskServiceMock;
    private readonly Mock<IValidator<CreateManualTaskDto>> _validatorMock;
    private readonly Mock<IOperatorFeatureGate> _featureGateMock;
    private readonly TasksController _controller;

    public TasksControllerTests()
    {
        _taskServiceMock = new Mock<IUserTaskService>();
        _validatorMock = new Mock<IValidator<CreateManualTaskDto>>();
        _featureGateMock = new Mock<IOperatorFeatureGate>();

        // Default: shift-plan feature is enabled.
        _featureGateMock.Setup(g => g.IsEnabled(OperatorFeature.ShiftPlan)).Returns(true);
        _featureGateMock.Setup(g => g.IsEnabledAsync(OperatorFeature.ShiftPlan, It.IsAny<CancellationToken>())).ReturnsAsync(true);

        _controller = new TasksController(
            _taskServiceMock.Object,
            _featureGateMock.Object,
            _validatorMock.Object);
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
            .Setup(s => s.GetPendingTasksAsync(It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(tasks);

        // Act
        IActionResult result = await _controller.GetPendingTasksAsync(view: null, CancellationToken.None);

        // Assert
        OkObjectResult okResult = Assert.IsType<OkObjectResult>(result);
        Assert.Equal(tasks, okResult.Value);
        _taskServiceMock.Verify(s => s.GetPendingTasksAsync(It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetPendingTasksAsync_WithNoTasks_ReturnsOkWithEmptyList()
    {
        // Arrange
        IReadOnlyList<UserTaskDto> emptyTasks = new List<UserTaskDto>();
        _taskServiceMock
            .Setup(s => s.GetPendingTasksAsync(It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(emptyTasks);

        // Act
        IActionResult result = await _controller.GetPendingTasksAsync(view: null, CancellationToken.None);

        // Assert
        OkObjectResult okResult = Assert.IsType<OkObjectResult>(result);
        IReadOnlyList<UserTaskDto> returnedTasks = Assert.IsAssignableFrom<IReadOnlyList<UserTaskDto>>(okResult.Value);
        Assert.Empty(returnedTasks);
    }

    /// <summary>Fix B: the default flat list forwards non-admin status so the service
    /// excludes maintenance-sourced tasks for ordinary callers.</summary>
    [Fact]
    public async Task GetPendingTasksAsync_DefaultList_NonAdmin_RequestsWithoutMaintenance()
    {
        IReadOnlyList<UserTaskDto> tasks = new List<UserTaskDto> { CreateUserTaskDto("Task", UserTaskType.ProfileImport) };
        _taskServiceMock
            .Setup(s => s.GetPendingTasksAsync(false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(tasks);

        // Default HttpContext user has no farm_admin role.
        IActionResult result = await _controller.GetPendingTasksAsync(view: null, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        _taskServiceMock.Verify(s => s.GetPendingTasksAsync(false, It.IsAny<CancellationToken>()), Times.Once);
        _taskServiceMock.Verify(s => s.GetPendingTasksAsync(true, It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>Fix B: an admin caller receives the unfiltered flat list (maintenance included).</summary>
    [Fact]
    public async Task GetPendingTasksAsync_DefaultList_Admin_RequestsWithMaintenance()
    {
        SetAdminUser();
        IReadOnlyList<UserTaskDto> tasks = new List<UserTaskDto>
        {
            CreateUserTaskDto("Maint", UserTaskType.MaintenanceDue, sourceKind: UserTaskSourceKind.Maintenance)
        };
        _taskServiceMock
            .Setup(s => s.GetPendingTasksAsync(true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(tasks);

        IActionResult result = await _controller.GetPendingTasksAsync(view: null, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        _taskServiceMock.Verify(s => s.GetPendingTasksAsync(true, It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>Fix 7: when shift-plan feature is disabled, view=shift returns 404.</summary>
    [Fact]
    public async Task GetPendingTasksAsync_ViewShift_FeatureDisabled_ReturnsNotFound()
    {
        _featureGateMock.Setup(g => g.IsEnabled(OperatorFeature.ShiftPlan)).Returns(false);
        _featureGateMock.Setup(g => g.IsEnabledAsync(OperatorFeature.ShiftPlan, It.IsAny<CancellationToken>())).ReturnsAsync(false);
        // OperatorFeatureProblemDetails.NotFound calls gate.GetFlagName internally.
        _featureGateMock.Setup(g => g.GetFlagName(OperatorFeature.ShiftPlan))
            .Returns("shiftPlanEnabled");

        IActionResult result = await _controller.GetPendingTasksAsync(view: "shift", CancellationToken.None);

        Assert.IsType<NotFoundObjectResult>(result);
    }

    /// <summary>Fix 7: when shift-plan feature is enabled, view=shift delegates to service.</summary>
    [Fact]
    public async Task GetPendingTasksAsync_ViewShift_FeatureEnabled_ReturnsShiftPlan()
    {
        ShiftPlanDto plan = new([], DateTime.UtcNow);
        _taskServiceMock
            .Setup(s => s.GetShiftPlanAsync(It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(plan);

        IActionResult result = await _controller.GetPendingTasksAsync(view: "shift", CancellationToken.None);

        OkObjectResult ok = Assert.IsType<OkObjectResult>(result);
        Assert.Same(plan, ok.Value);
    }

    /// <summary>
    /// Fix 6: enum fields in ShiftPlanDto must serialize as camelCase-named strings, not integers.
    /// NOTE: this uses a hand-built <see cref="JsonSerializerOptions"/> with NO *options-level* enum
    /// converter registered — no global <see cref="System.Text.Json.Serialization.JsonStringEnumConverter"/>
    /// and no explicit property-level converter added to <c>Converters</c> here. Attributes are
    /// intrinsic to the member/type, not the options bag, so the property-level
    /// <c>[JsonConverter]</c> attribute on <see cref="ShiftPlanGroupDto.AnchorKind"/> still applies
    /// in this test and is what actually produces <c>"now"</c>. This test therefore only proves
    /// property-NAME casing, not enum-VALUE global-converter-vs-property-attribute precedence — it
    /// would keep passing even if that property-level attribute were removed, because the
    /// type-level <c>[JsonConverter]</c> attribute on <see cref="Farm.Infrastructure.Domain.UserTaskAnchorKind"/>
    /// would then take over (issue #2246 finding; only removing BOTH attributes would make this
    /// assert an integer). For the real global-converter-precedence proof across every
    /// anchor/source token via this app's actual DI-registered MVC and SignalR
    /// <see cref="JsonSerializerOptions"/>, see
    /// <c>Farm.Web.Api.Tests.Contracts.UserTaskDtoWireContractTests</c>.
    /// </summary>
    [Fact]
    public async Task GetPendingTasksAsync_ViewShift_SerializesAnchorKindAsCamelCaseString()
    {
        ShiftPlanGroupDto group = new(
            UserTaskAnchorKind.Now,
            new[] { CreateUserTaskDto("t", UserTaskType.Custom) });
        ShiftPlanDto plan = new(new[] { group }, DateTime.UtcNow);

        _taskServiceMock
            .Setup(s => s.GetShiftPlanAsync(It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(plan);

        IActionResult result = await _controller.GetPendingTasksAsync(view: "shift", CancellationToken.None);
        OkObjectResult ok = Assert.IsType<OkObjectResult>(result);

        // Serialize with camelCase property-naming only (no options-level enum converter added).
        // The property-level [JsonConverter] attribute on AnchorKind still applies here (attributes
        // are intrinsic to the member, not the options bag) and is what produces "now" below. This
        // proves the anchorKind PROPERTY name is camelCase, not global-converter-vs-property-attribute
        // precedence — see UserTaskDtoWireContractTests for that proof against real DI options.
        string json = JsonSerializer.Serialize(ok.Value, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        });

        // Fix 6: must be "now" (camelCase), not "Now" (PascalCase), not 0 (integer).
        Assert.Contains("\"anchorKind\":\"now\"", json);
        Assert.DoesNotContain("\"anchorKind\":\"Now\"", json);
        Assert.DoesNotContain("\"anchorKind\":0", json);
    }

    #endregion

    #region CreateManualTaskAsync Tests

    [Fact]
    public async Task CreateManualTaskAsync_WithValidDto_ReturnsCreatedWithLocationHeader()
    {
        // Arrange
        var taskId = Guid.NewGuid();
        var dto = new CreateManualTaskDto("Test Task", "Test description", UserTaskPriority.Normal);
        var createdTask = CreateUserTaskDto("Test Task", UserTaskType.ProfileImport, taskId);

        _validatorMock
            .Setup(v => v.ValidateAsync(dto, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FluentValidation.Results.ValidationResult());

        _taskServiceMock
            .Setup(s => s.CreateManualTaskAsync(dto, It.IsAny<CancellationToken>()))
            .ReturnsAsync(createdTask);

        // Act
        ActionResult<UserTaskDto> result = await _controller.CreateManualTaskAsync(dto, CancellationToken.None);

        // Assert
        CreatedAtActionResult createdResult = Assert.IsType<CreatedAtActionResult>(result.Result);
        Assert.Equal(StatusCodes.Status201Created, createdResult.StatusCode);
        Assert.Equal("GetById", createdResult.ActionName);
        Assert.Equal(taskId, createdResult.RouteValues!["id"]);
        Assert.Equal(createdTask, createdResult.Value);
    }

    [Fact]
    public async Task CreateManualTaskAsync_WithInvalidDto_ReturnsBadRequest()
    {
        // Arrange
        var dto = new CreateManualTaskDto("", "", UserTaskPriority.Normal);
        var validationResult = new FluentValidation.Results.ValidationResult(
            [new FluentValidation.Results.ValidationFailure("Title", "Title is required")]);

        _validatorMock
            .Setup(v => v.ValidateAsync(dto, It.IsAny<CancellationToken>()))
            .ReturnsAsync(validationResult);

        // Act
        ActionResult<UserTaskDto> result = await _controller.CreateManualTaskAsync(dto, CancellationToken.None);

        // Assert
        Assert.IsType<BadRequestObjectResult>(result.Result);
        _taskServiceMock.Verify(s => s.CreateManualTaskAsync(It.IsAny<CreateManualTaskDto>(), It.IsAny<CancellationToken>()), Times.Never);
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

    /// <summary>Fix 8: non-admin looking up a maintenance task by id gets 404 (not visible).</summary>
    [Fact]
    public async Task GetByIdAsync_MaintenanceTask_NonAdmin_ReturnsNotFound()
    {
        Guid taskId = Guid.NewGuid();
        UserTaskDto maintenanceTask = CreateUserTaskDto("maint", UserTaskType.MaintenanceDue, taskId,
            sourceKind: UserTaskSourceKind.Maintenance);
        _taskServiceMock
            .Setup(s => s.GetByIdAsync(taskId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(maintenanceTask);
        // HttpContext has no farm_admin claim → IsAdmin = false.

        ActionResult<UserTaskDto> result = await _controller.GetByIdAsync(taskId, CancellationToken.None);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    /// <summary>Fix 8: admin can access a maintenance task by id.</summary>
    [Fact]
    public async Task GetByIdAsync_MaintenanceTask_Admin_ReturnsOk()
    {
        Guid taskId = Guid.NewGuid();
        UserTaskDto maintenanceTask = CreateUserTaskDto("maint", UserTaskType.MaintenanceDue, taskId,
            sourceKind: UserTaskSourceKind.Maintenance);
        _taskServiceMock
            .Setup(s => s.GetByIdAsync(taskId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(maintenanceTask);
        SetAdminUser();

        ActionResult<UserTaskDto> result = await _controller.GetByIdAsync(taskId, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result.Result);
    }

    #endregion

    #region GetPendingCountAsync Tests

    [Fact]
    public async Task GetPendingCountAsync_ReturnsOkWithCount()
    {
        // Arrange
        _taskServiceMock
            .Setup(s => s.GetPendingCountAsync(It.IsAny<bool>(), It.IsAny<CancellationToken>()))
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
            .Setup(s => s.GetPendingCountAsync(It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);

        // Act
        ActionResult<PendingTaskCountDto> result = await _controller.GetPendingCountAsync(CancellationToken.None);

        // Assert
        OkObjectResult okResult = Assert.IsType<OkObjectResult>(result.Result);
        PendingTaskCountDto countDto = Assert.IsType<PendingTaskCountDto>(okResult.Value);
        Assert.Equal(0, countDto.Count);
    }

    /// <summary>Fix B: non-admin count forwards includeMaintenance=false so it matches the visible list.</summary>
    [Fact]
    public async Task GetPendingCountAsync_NonAdmin_ExcludesMaintenance()
    {
        _taskServiceMock
            .Setup(s => s.GetPendingCountAsync(false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(3);

        ActionResult<PendingTaskCountDto> result = await _controller.GetPendingCountAsync(CancellationToken.None);

        OkObjectResult okResult = Assert.IsType<OkObjectResult>(result.Result);
        PendingTaskCountDto countDto = Assert.IsType<PendingTaskCountDto>(okResult.Value);
        Assert.Equal(3, countDto.Count);
        _taskServiceMock.Verify(s => s.GetPendingCountAsync(false, It.IsAny<CancellationToken>()), Times.Once);
        _taskServiceMock.Verify(s => s.GetPendingCountAsync(true, It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>Fix B: admin count includes maintenance tasks.</summary>
    [Fact]
    public async Task GetPendingCountAsync_Admin_IncludesMaintenance()
    {
        SetAdminUser();
        _taskServiceMock
            .Setup(s => s.GetPendingCountAsync(true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(7);

        ActionResult<PendingTaskCountDto> result = await _controller.GetPendingCountAsync(CancellationToken.None);

        OkObjectResult okResult = Assert.IsType<OkObjectResult>(result.Result);
        PendingTaskCountDto countDto = Assert.IsType<PendingTaskCountDto>(okResult.Value);
        Assert.Equal(7, countDto.Count);
        _taskServiceMock.Verify(s => s.GetPendingCountAsync(true, It.IsAny<CancellationToken>()), Times.Once);
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

    /// <summary>Fix 8: non-admin attempting to complete a maintenance task gets 403.</summary>
    [Fact]
    public async Task CompleteAsync_MaintenanceTask_NonAdmin_ReturnsForbid()
    {
        Guid taskId = Guid.NewGuid();
        UserTaskDto maintenanceTask = CreateUserTaskDto("maint", UserTaskType.MaintenanceDue, taskId,
            sourceKind: UserTaskSourceKind.Maintenance);
        _taskServiceMock
            .Setup(s => s.GetByIdAsync(taskId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(maintenanceTask);
        // No farm_admin claim.

        IActionResult result = await _controller.CompleteAsync(taskId, CancellationToken.None);

        Assert.IsType<ForbidResult>(result);
        _taskServiceMock.Verify(s => s.CompleteTaskAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>Fix 8: admin can complete a maintenance task.</summary>
    [Fact]
    public async Task CompleteAsync_MaintenanceTask_Admin_ReturnsNoContent()
    {
        Guid taskId = Guid.NewGuid();
        _taskServiceMock
            .Setup(s => s.CompleteTaskAsync(taskId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        SetAdminUser();

        IActionResult result = await _controller.CompleteAsync(taskId, CancellationToken.None);

        Assert.IsType<NoContentResult>(result);
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

    private void SetAdminUser()
    {
        ClaimsIdentity identity = new(
            [new Claim(ClaimTypes.Role, "farm_admin")],
            authenticationType: "test");
        _controller.ControllerContext.HttpContext.User = new ClaimsPrincipal(identity);
    }

    private static UserTaskDto CreateUserTaskDto(
        string title,
        UserTaskType taskType,
        Guid? id = null,
        UserTaskSourceKind sourceKind = UserTaskSourceKind.Unspecified)
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
            MetadataJson: null,
            AnchorKind: UserTaskAnchorKind.Unspecified,
            AnchorAtUtc: null,
            WindowStartUtc: null,
            WindowEndUtc: null,
            SourceKind: sourceKind,
            SourceId: null);
    }

    #endregion
}

