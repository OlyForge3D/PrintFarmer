using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Interfaces;
using Farm.Infrastructure.Services.OperatorFeatures;
using Farm.Infrastructure.Services.Printers;
using Farm.Infrastructure.Services.Queue;
using Farm.Infrastructure.Services.Queue.Dispatch;
using Farm.Infrastructure.Services.SignalR;
using Farm.Infrastructure.Telemetry;
using Farm.Web.Api.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Controllers;

public class JobQueueControllerTests
{
    private readonly Mock<IJobQueueService> _queueServiceMock;
    private readonly Mock<IPrintJobManagementService> _printJobManagementServiceMock;
    private readonly Mock<ILogger<JobQueueController>> _loggerMock;
    private readonly Mock<IPrintJobCompletionService> _printJobCompletionServiceMock;
    private readonly Mock<IJobDispatchService> _jobDispatchServiceMock;
    private readonly Mock<IBatchDispatchService> _batchDispatchServiceMock;
    private readonly Mock<IBedClearAcknowledgementService> _bedClearAcknowledgementServiceMock;
    private readonly Mock<IPrinterStatusCacheReader> _printerStatusCacheMock;
    private readonly Mock<IPrintFarmerTelemetryService> _telemetryServiceMock;
    private readonly Mock<Farm.Infrastructure.Services.PartsInventory.IPartHarvestService> _partHarvestServiceMock;
    private readonly Mock<IOperatorFeatureGate> _operatorFeatureGateMock;
    private readonly JobQueueController _controller;

    public JobQueueControllerTests()
    {
        _queueServiceMock = new Mock<IJobQueueService>();
        _printJobManagementServiceMock = new Mock<IPrintJobManagementService>();
        _loggerMock = new Mock<ILogger<JobQueueController>>();
        _printJobCompletionServiceMock = new Mock<IPrintJobCompletionService>();
        _jobDispatchServiceMock = new Mock<IJobDispatchService>();
        _batchDispatchServiceMock = new Mock<IBatchDispatchService>();
        _bedClearAcknowledgementServiceMock = new Mock<IBedClearAcknowledgementService>();
        _printerStatusCacheMock = new Mock<IPrinterStatusCacheReader>();
        _telemetryServiceMock = new Mock<IPrintFarmerTelemetryService>();
        _partHarvestServiceMock = new Mock<Farm.Infrastructure.Services.PartsInventory.IPartHarvestService>();
        _operatorFeatureGateMock = new Mock<IOperatorFeatureGate>();
        _operatorFeatureGateMock
            .Setup(gate => gate.IsEnabled(OperatorFeature.PrintedPartsInventory))
            .Returns(true);
        _operatorFeatureGateMock
            .Setup(gate => gate.IsEnabledAsync(OperatorFeature.PrintedPartsInventory, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _controller = new JobQueueController(
            _queueServiceMock.Object,
            _printJobManagementServiceMock.Object,
            _printJobCompletionServiceMock.Object,
            _jobDispatchServiceMock.Object,
            _batchDispatchServiceMock.Object,
            _bedClearAcknowledgementServiceMock.Object,
            _printerStatusCacheMock.Object,
            _telemetryServiceMock.Object,
            _partHarvestServiceMock.Object,
            _operatorFeatureGateMock.Object,
            _loggerMock.Object);

        // Set up authenticated user with valid GUID claim for ACL enforcement
        var userId = Guid.NewGuid();
        var claims = new[] { new Claim(ClaimTypes.NameIdentifier, userId.ToString()) };
        var identity = new ClaimsIdentity(claims, "TestAuth");
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) }
        };
    }

    [Fact]
    public void Constructor_WithValidDependencies_CreatesInstance()
    {
        // Arrange & Act
        var controller = new JobQueueController(
            _queueServiceMock.Object,
            _printJobManagementServiceMock.Object,
            _printJobCompletionServiceMock.Object,
            _jobDispatchServiceMock.Object,
            _batchDispatchServiceMock.Object,
            _bedClearAcknowledgementServiceMock.Object,
            _printerStatusCacheMock.Object,
            _telemetryServiceMock.Object,
            _partHarvestServiceMock.Object,
            _operatorFeatureGateMock.Object,
            _loggerMock.Object);

        // Assert
        Assert.NotNull(controller);
    }

    [Fact]
    public async Task GetQueueAsync_WithValidQueue_ReturnsOk()
    {
        // Arrange
        var queueOverview = new List<QueueOverviewDto>
        {
            new QueueOverviewDto
            {
                PrinterId = Guid.NewGuid(),
                PrinterName = "Printer1",
                PrinterModel = "Model1",
                IsAvailable = true,
                QueuedJobsCount = 2
            }
        };

        _queueServiceMock
            .Setup(s => s.GetQueueOverviewAsync(It.IsAny<string?>(), It.IsAny<decimal?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(queueOverview);

        // Act
        ActionResult<IEnumerable<QueueOverviewDto>> result = await _controller.GetQueueAsync();

        // Assert
        ActionResult<IEnumerable<QueueOverviewDto>> okResult = Assert.IsType<ActionResult<IEnumerable<QueueOverviewDto>>>(result);
        OkObjectResult okValue = Assert.IsType<OkObjectResult>(okResult.Result);
        Assert.Equal(queueOverview, okValue.Value);
    }

    [Fact]
    public async Task GetQueueAsync_WithException_ReturnsProblem()
    {
        // Arrange
        _queueServiceMock
            .Setup(s => s.GetQueueOverviewAsync(It.IsAny<string?>(), It.IsAny<decimal?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Test exception"));

        // Act
        ActionResult<IEnumerable<QueueOverviewDto>> result = await _controller.GetQueueAsync();

        // Assert
        ActionResult<IEnumerable<QueueOverviewDto>> actionResult = Assert.IsType<ActionResult<IEnumerable<QueueOverviewDto>>>(result);
        ObjectResult problemResult = Assert.IsType<ObjectResult>(actionResult.Result);
        Assert.Equal(500, problemResult.StatusCode);
    }

    [Fact]
    public async Task GetChangesAsync_CrossResourceEvent_IsNotReturned()
    {
        DbContextOptions<AppDbContext> options =
            new DbContextOptionsBuilder<AppDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;
        await using var db = new AppDbContext(options);
        Guid allowedJobId = Guid.NewGuid();
        Guid deniedJobId = Guid.NewGuid();
        QueueDispatchOutbox command = CreateOutboxEvent(allowedJobId, 1);
        command.EventType = BedClearAcknowledgementService.BackendStartCommandEventType;
        command.PayloadJson = """{"actorSubject":"private","acknowledgementKey":"secret"}""";
        db.QueueDispatchOutbox.AddRange(
            command,
            CreateOutboxEvent(allowedJobId, 2),
            CreateOutboxEvent(deniedJobId, 3));
        await db.SaveChangesAsync();

        Mock<IQueueResourceAuthorizationService> authorization = new();
        authorization
            .Setup(service => service.FilterAccessiblePrinterIdsAsync(
                It.IsAny<ClaimsPrincipal>(),
                It.IsAny<IReadOnlyCollection<Guid>>(),
                PrinterGroupAccessLevel.View,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HashSet<Guid>());
        authorization
            .Setup(service => service.FilterActorAccessibleJobIdsAsync(
                It.IsAny<string>(),
                It.Is<IReadOnlyCollection<Guid>>(ids => ids.Contains(allowedJobId) && ids.Contains(deniedJobId)),
                PrinterGroupAccessLevel.View,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HashSet<Guid> { allowedJobId });
        JobQueueController controller = CreateController(db, authorization.Object);

        IActionResult result = await controller.GetChangesAsync(
            afterSequence: 0,
            limit: 100,
            CancellationToken.None);

        OkObjectResult ok = Assert.IsType<OkObjectResult>(result);
        object value = Assert.IsAssignableFrom<object>(ok.Value);
        object? eventValue = value.GetType().GetProperty("events")?.GetValue(value);
        List<QueueEventEnvelope> events =
            Assert.IsType<List<QueueEventEnvelope>>(eventValue);
        Assert.Single(events);
        Assert.Equal(allowedJobId, events[0].JobId);
        Assert.Equal(2, events[0].Sequence);
        Assert.DoesNotContain(events, evt =>
            evt.EventType == BedClearAcknowledgementService.BackendStartCommandEventType);
        Assert.Equal(3L, value.GetType().GetProperty("nextSequence")?.GetValue(value));
        Assert.Equal(false, value.GetType().GetProperty("hasMore")?.GetValue(value));
    }

    [Fact]
    public async Task DispatchJobAsync_MissingIfMatch_Returns428()
    {
        Guid jobId = Guid.NewGuid();
        _printJobManagementServiceMock
            .Setup(service => service.DispatchJobAsync(
                jobId.ToString(),
                It.IsAny<string>(),
                string.Empty,
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new QueuePreconditionRequiredException("If-Match is required."));

        IActionResult result = await _controller.DispatchJobAsync(jobId);

        ObjectResult response = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status428PreconditionRequired, response.StatusCode);
    }

    [Fact]
    public async Task DispatchJobAsync_StaleIfMatch_Returns412()
    {
        Guid jobId = Guid.NewGuid();
        _controller.ControllerContext.HttpContext.Request.Headers.IfMatch = "\"c3RhbGU=\"";
        _printJobManagementServiceMock
            .Setup(service => service.DispatchJobAsync(
                jobId.ToString(),
                It.IsAny<string>(),
                "c3RhbGU=",
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new QueueRevisionConflictException("The job changed."));

        IActionResult result = await _controller.DispatchJobAsync(jobId);

        ObjectResult response = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status412PreconditionFailed, response.StatusCode);
    }

    [Fact]
    public async Task SyncOrphanedJobsAsync_AuthenticatedCaller_PassesActorSubject()
    {
        string actorSubject = QueueActorIdentity.Resolve(_controller.User);
        _printJobCompletionServiceMock
            .Setup(service => service.SyncOrphanedPrintingJobsAsync(
                It.IsAny<Func<Guid, string?>>(),
                actorSubject,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);

        IActionResult result = await _controller.SyncOrphanedJobsAsync();

        _ = Assert.IsType<OkObjectResult>(result);
        _printJobCompletionServiceMock.Verify(
            service => service.SyncOrphanedPrintingJobsAsync(
                It.IsAny<Func<Guid, string?>>(),
                actorSubject,
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    private JobQueueController CreateController(
        AppDbContext db,
        IQueueResourceAuthorizationService authorization)
    {
        var controller = new JobQueueController(
            _queueServiceMock.Object,
            _printJobManagementServiceMock.Object,
            _printJobCompletionServiceMock.Object,
            _jobDispatchServiceMock.Object,
            _batchDispatchServiceMock.Object,
            _bedClearAcknowledgementServiceMock.Object,
            _printerStatusCacheMock.Object,
            _telemetryServiceMock.Object,
            Mock.Of<Farm.Infrastructure.Services.PartsInventory.IPartHarvestService>(),
            Mock.Of<Farm.Infrastructure.Services.OperatorFeatures.IOperatorFeatureGate>(),
            _loggerMock.Object,
            db,
            authorization);
        controller.ControllerContext = _controller.ControllerContext;
        return controller;
    }

    private static QueueDispatchOutbox CreateOutboxEvent(Guid jobId, long sequence) =>
        new()
        {
            Id = Guid.NewGuid(),
            Sequence = sequence,
            AggregateType = nameof(PrintJob),
            AggregateId = jobId,
            EventType = QueueLifecycleEventWriter.EventTypeJobCompleted,
            SchemaVersion = "1",
            JobStatus = PrintJobStatus.Completed.ToString(),
            JobKind = JobKind.Standard.ToString(),
            PayloadJson = "{}",
            Status = QueueOutboxEventStatus.Pending,
            CreatedAtUtc = DateTime.UtcNow,
        };

    [Fact]
    public async Task QueueJobAsync_WithNullRequest_ReturnsBadRequest()
    {
        // Act
        ActionResult<JobQueuePrintJobDto> result = await _controller.QueueJobAsync(null!);

        // Assert
        ActionResult<JobQueuePrintJobDto> actionResult = Assert.IsType<ActionResult<JobQueuePrintJobDto>>(result);
        BadRequestObjectResult badRequest = Assert.IsType<BadRequestObjectResult>(actionResult.Result);
        Assert.Equal("Request body is required", badRequest.Value);
    }

    [Fact]
    public async Task QueueJobAsync_WithValidRequest_ReturnsCreated()
    {
        // Arrange
        var request = new QueuePrintJobDto
        {
            GcodeFileId = Guid.NewGuid(),
            AssignedPrinterId = Guid.NewGuid(),
            Priority = PrintJobPriority.Normal
        };

        var jobDto = new JobQueuePrintJobDto
        {
            Id = Guid.NewGuid(),
            GcodeFileId = request.GcodeFileId,
            GcodeFileName = "test.gcode",
            AssignedPrinterId = request.AssignedPrinterId,
            Status = PrintJobStatus.Queued,
            Priority = PrintJobPriority.Low,
            QueuePosition = 1,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        _queueServiceMock
            .Setup(s => s.AddJobToQueueAsync(request, It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(jobDto);

        // Act
        ActionResult<JobQueuePrintJobDto> result = await _controller.QueueJobAsync(request);

        // Assert
        ActionResult<JobQueuePrintJobDto> actionResult = Assert.IsType<ActionResult<JobQueuePrintJobDto>>(result);
        CreatedResult createdResult = Assert.IsType<CreatedResult>(actionResult.Result);
        Assert.Equal(jobDto, createdResult.Value);
        Assert.Contains(jobDto.Id.ToString(), createdResult.Location);
    }

    [Fact]
    public async Task QueueJobAsync_WithRowVersion_SetsETagHeader()
    {
        var request = new QueuePrintJobDto
        {
            GcodeFileId = Guid.NewGuid(),
            AssignedPrinterId = Guid.NewGuid(),
            Priority = PrintJobPriority.Normal,
        };

        var jobDto = new JobQueuePrintJobDto
        {
            Id = Guid.NewGuid(),
            GcodeFileId = request.GcodeFileId,
            GcodeFileName = "test.gcode",
            AssignedPrinterId = request.AssignedPrinterId,
            Status = PrintJobStatus.Queued,
            Priority = PrintJobPriority.Low,
            QueuePosition = 1,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            RowVersion = Convert.ToBase64String([0x01, 0x02, 0x03]),
        };

        _queueServiceMock
            .Setup(s => s.AddJobToQueueAsync(request, It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(jobDto);

        _ = await _controller.QueueJobAsync(request);

        Assert.Equal($"\"{jobDto.RowVersion}\"", _controller.Response.Headers.ETag.ToString());
    }

    [Fact]
    public async Task QueueJobAsync_WithIdempotentReplay_ReturnsOk()
    {
        var request = new QueuePrintJobDto
        {
            GcodeFileId = Guid.NewGuid(),
            AssignedPrinterId = Guid.NewGuid(),
            Priority = PrintJobPriority.Normal,
        };

        var jobDto = new JobQueuePrintJobDto
        {
            Id = Guid.NewGuid(),
            GcodeFileId = request.GcodeFileId,
            GcodeFileName = "test.gcode",
            AssignedPrinterId = request.AssignedPrinterId,
            Status = PrintJobStatus.Queued,
            Priority = PrintJobPriority.Low,
            QueuePosition = 1,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            IsIdempotentReplay = true,
        };

        _queueServiceMock
            .Setup(s => s.AddJobToQueueAsync(request, It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(jobDto);

        ActionResult<JobQueuePrintJobDto> result = await _controller.QueueJobAsync(request);

        OkObjectResult ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal(jobDto, ok.Value);
    }

    [Fact]
    public async Task QueueJobAsync_WithIdempotencyConflict_ReturnsConflict()
    {
        var request = new QueuePrintJobDto
        {
            GcodeFileId = Guid.NewGuid(),
            AssignedPrinterId = Guid.NewGuid(),
            Priority = PrintJobPriority.Normal,
        };

        _queueServiceMock
            .Setup(s => s.AddJobToQueueAsync(request, It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new QueueJobIdempotencyConflictException("conflict"));

        ActionResult<JobQueuePrintJobDto> result = await _controller.QueueJobAsync(request);

        ConflictObjectResult conflict = Assert.IsType<ConflictObjectResult>(result.Result);
        Assert.Contains("idempotency_payload_mismatch", conflict.Value?.ToString());
    }

    [Fact]
    public async Task QueueJobAsync_WithNonExistentFile_ReturnsNotFound()
    {
        // Arrange
        var request = new QueuePrintJobDto
        {
            GcodeFileId = Guid.NewGuid(),
            Priority = PrintJobPriority.Normal
        };

        _queueServiceMock
            .Setup(s => s.AddJobToQueueAsync(request, It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((JobQueuePrintJobDto?)null);

        // Act
        ActionResult<JobQueuePrintJobDto> result = await _controller.QueueJobAsync(request);

        // Assert
        ActionResult<JobQueuePrintJobDto> actionResult = Assert.IsType<ActionResult<JobQueuePrintJobDto>>(result);
        Assert.IsType<NotFoundObjectResult>(actionResult.Result);
    }

    [Fact]
    public async Task QueueJobAsync_WithPolicyValidationError_ReturnsBadRequest()
    {
        // Arrange
        var request = new QueuePrintJobDto
        {
            GcodeFileId = Guid.NewGuid(),
            Priority = PrintJobPriority.Normal
        };

        _queueServiceMock
            .Setup(s => s.AddJobToQueueAsync(request, It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ValidationException("Deadline is required by queue policy."));

        // Act
        ActionResult<JobQueuePrintJobDto> result = await _controller.QueueJobAsync(request);

        // Assert
        ActionResult<JobQueuePrintJobDto> actionResult = Assert.IsType<ActionResult<JobQueuePrintJobDto>>(result);
        BadRequestObjectResult badRequest = Assert.IsType<BadRequestObjectResult>(actionResult.Result);
        Assert.Contains("Deadline is required by queue policy.", badRequest.Value?.ToString());
    }

    [Fact]
    public async Task GetJobAsync_WithValidId_ReturnsOk()
    {
        // Arrange
        var jobId = Guid.NewGuid();
        var jobDto = new JobQueuePrintJobDto
        {
            Id = jobId,
            RowVersion = "job-row-version",
            Revision = 9,
            DispatchStateRowVersion = "dispatch-row-version",
            DispatchStateRevision = 14,
            GcodeFileId = Guid.NewGuid(),
            GcodeFileName = "test.gcode",
            Status = PrintJobStatus.Queued,
            Priority = PrintJobPriority.Low,
            QueuePosition = 1,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        _queueServiceMock
            .Setup(s => s.GetJobAsync(jobId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(jobDto);

        // Act
        ActionResult<JobQueuePrintJobDto> result = await _controller.GetJobAsync(jobId);

        // Assert
        ActionResult<JobQueuePrintJobDto> actionResult = Assert.IsType<ActionResult<JobQueuePrintJobDto>>(result);
        OkObjectResult okResult = Assert.IsType<OkObjectResult>(actionResult.Result);
        Assert.Equal(jobDto, okResult.Value);
        Assert.Equal("\"job-row-version\"", _controller.Response.Headers.ETag);
        Assert.Equal(
            "\"dispatch-row-version\"",
            _controller.Response.Headers["X-Dispatch-State-ETag"]);
    }

    [Fact]
    public async Task GetJobAsync_WithNonExistentId_ReturnsNotFound()
    {
        // Arrange
        var jobId = Guid.NewGuid();

        _queueServiceMock
            .Setup(s => s.GetJobAsync(jobId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((JobQueuePrintJobDto?)null);

        // Act
        ActionResult<JobQueuePrintJobDto> result = await _controller.GetJobAsync(jobId);

        // Assert
        ActionResult<JobQueuePrintJobDto> actionResult = Assert.IsType<ActionResult<JobQueuePrintJobDto>>(result);
        Assert.IsType<NotFoundObjectResult>(actionResult.Result);
    }

    [Fact]
    public async Task GetJobAsync_InaccessibleResource_ReturnsHiddenNotFound()
    {
        await using var db = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options);
        Guid jobId = Guid.NewGuid();
        Mock<IQueueResourceAuthorizationService> authorization = new();
        authorization
            .Setup(service => service.CanAccessJobAsync(
                It.IsAny<ClaimsPrincipal>(),
                jobId,
                PrinterGroupAccessLevel.View,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        JobQueueController controller = CreateController(db, authorization.Object);

        ActionResult<JobQueuePrintJobDto> result = await controller.GetJobAsync(jobId);

        _ = Assert.IsType<NotFoundResult>(result.Result);
        _queueServiceMock.Verify(
            service => service.GetJobAsync(
                It.IsAny<Guid>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task UpdateJobAsync_WithNullRequest_ReturnsBadRequest()
    {
        // Arrange
        var jobId = Guid.NewGuid();

        // Act
        ActionResult<JobQueuePrintJobDto> result = await _controller.UpdateJobAsync(jobId, null!);

        // Assert
        ActionResult<JobQueuePrintJobDto> actionResult = Assert.IsType<ActionResult<JobQueuePrintJobDto>>(result);
        BadRequestObjectResult badRequest = Assert.IsType<BadRequestObjectResult>(actionResult.Result);
        Assert.Equal("Request body is required", badRequest.Value);
    }

    [Fact]
    public async Task UpdateJobAsync_WithValidRequest_ReturnsOk()
    {
        // Arrange
        var jobId = Guid.NewGuid();
        var request = new UpdatePrintJobStatusDto
        {
            Status = PrintJobStatus.Printing,
            Priority = PrintJobPriority.High
        };

        var updatedDto = new JobQueuePrintJobDto
        {
            Id = jobId,
            GcodeFileId = Guid.NewGuid(),
            GcodeFileName = "test.gcode",
            Status = PrintJobStatus.Printing,
            Priority = PrintJobPriority.High,
            QueuePosition = 1,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        _queueServiceMock
            .Setup(s => s.UpdateJobAsync(
                jobId,
                request,
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(updatedDto);
        _controller.ControllerContext.HttpContext.Request.Headers.IfMatch = "\"dGVzdA==\"";

        // Act
        ActionResult<JobQueuePrintJobDto> result = await _controller.UpdateJobAsync(jobId, request);

        // Assert
        ActionResult<JobQueuePrintJobDto> actionResult = Assert.IsType<ActionResult<JobQueuePrintJobDto>>(result);
        OkObjectResult okResult = Assert.IsType<OkObjectResult>(actionResult.Result);
        Assert.Equal(updatedDto, okResult.Value);
    }

    [Fact]
    public async Task UpdateJobAsync_WithNonExistentId_ReturnsNotFound()
    {
        // Arrange
        var jobId = Guid.NewGuid();
        var request = new UpdatePrintJobStatusDto
        {
            Status = PrintJobStatus.Printing
        };

        _queueServiceMock
            .Setup(s => s.UpdateJobAsync(
                jobId,
                request,
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((JobQueuePrintJobDto?)null);
        _controller.ControllerContext.HttpContext.Request.Headers.IfMatch = "\"dGVzdA==\"";

        // Act
        ActionResult<JobQueuePrintJobDto> result = await _controller.UpdateJobAsync(jobId, request);

        // Assert
        ActionResult<JobQueuePrintJobDto> actionResult = Assert.IsType<ActionResult<JobQueuePrintJobDto>>(result);
        Assert.IsType<NotFoundObjectResult>(actionResult.Result);
    }

    [Fact]
    public async Task UpdateJobAsync_WithPolicyValidationError_ReturnsBadRequest()
    {
        // Arrange
        var jobId = Guid.NewGuid();
        var request = new UpdatePrintJobStatusDto
        {
            DeadlineAtUtc = DateTime.UtcNow.AddMinutes(30)
        };

        _queueServiceMock
            .Setup(s => s.UpdateJobAsync(
                jobId,
                request,
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ValidationException("Deadline must be at least 2 hour(s) in the future."));
        _controller.ControllerContext.HttpContext.Request.Headers.IfMatch = "\"dGVzdA==\"";

        // Act
        ActionResult<JobQueuePrintJobDto> result = await _controller.UpdateJobAsync(jobId, request);

        // Assert
        ActionResult<JobQueuePrintJobDto> actionResult = Assert.IsType<ActionResult<JobQueuePrintJobDto>>(result);
        BadRequestObjectResult badRequest = Assert.IsType<BadRequestObjectResult>(actionResult.Result);
        Assert.Contains("Deadline must be at least 2 hour(s) in the future.", badRequest.Value?.ToString());
    }

    [Fact]
    public async Task DeleteJobAsync_WithValidId_ReturnsNoContent()
    {
        // Arrange
        var jobId = Guid.NewGuid();

        _queueServiceMock
            .Setup(s => s.RemoveJobAsync(
                jobId,
                It.IsAny<string?>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _controller.ControllerContext.HttpContext.Request.Headers.IfMatch = "\"dGVzdA==\"";

        // Act
        IActionResult result = await _controller.DeleteJobAsync(jobId);

        // Assert
        Assert.IsType<NoContentResult>(result);
    }

    [Fact]
    public async Task DeleteJobAsync_WithNonExistentOrActiveJob_ReturnsBadRequest()
    {
        // Arrange
        var jobId = Guid.NewGuid();

        _queueServiceMock
            .Setup(s => s.RemoveJobAsync(
                jobId,
                It.IsAny<string?>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        _controller.ControllerContext.HttpContext.Request.Headers.IfMatch = "\"dGVzdA==\"";

        // Act
        IActionResult result = await _controller.DeleteJobAsync(jobId);

        // Assert
        BadRequestObjectResult badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Contains("Cannot delete the job", badRequest.Value?.ToString());
    }

    [Fact]
    public async Task DeleteJobAsync_WithException_ReturnsProblem()
    {
        // Arrange
        var jobId = Guid.NewGuid();

        _queueServiceMock
            .Setup(s => s.RemoveJobAsync(
                jobId,
                It.IsAny<string?>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Test exception"));
        _controller.ControllerContext.HttpContext.Request.Headers.IfMatch = "\"dGVzdA==\"";

        // Act
        IActionResult result = await _controller.DeleteJobAsync(jobId);

        // Assert
        ObjectResult problemResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(500, problemResult.StatusCode);
    }

    [Fact]
    public async Task HarvestJobAsync_MapsOkResultFromService()
    {
        var jobId = Guid.NewGuid();
        var expectedResponse = new Farm.Infrastructure.Dtos.PartsInventory.HarvestJobResponse(
            jobId,
            DateTime.UtcNow,
            null,
            null,
            AlreadyHarvested: false,
            new List<Farm.Infrastructure.Dtos.PartsInventory.PartAdjustmentResponse>(),
            new List<Farm.Infrastructure.Dtos.PartsInventory.HarvestOutputResponse>());
        _partHarvestServiceMock
            .Setup(s => s.HarvestJobAsync(
                jobId,
                It.IsAny<Farm.Infrastructure.Dtos.PartsInventory.HarvestJobRequest>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Farm.Infrastructure.Services.PartsInventory.HarvestResult(
                Farm.Infrastructure.Services.PartsInventory.PartInventoryOutcome.Ok,
                expectedResponse,
                null));

        ActionResult<Farm.Infrastructure.Dtos.PartsInventory.HarvestJobResponse> result =
            await _controller.HarvestJobAsync(jobId, null, CancellationToken.None);

        OkObjectResult ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Same(expectedResponse, ok.Value);
    }

    [Fact]
    public async Task HarvestJobAsync_JobNotCompleted_MapsToConflict()
    {
        var jobId = Guid.NewGuid();
        _partHarvestServiceMock
            .Setup(s => s.HarvestJobAsync(
                jobId,
                It.IsAny<Farm.Infrastructure.Dtos.PartsInventory.HarvestJobRequest>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Farm.Infrastructure.Services.PartsInventory.HarvestResult(
                Farm.Infrastructure.Services.PartsInventory.PartInventoryOutcome.JobNotCompleted,
                null,
                "not completed"));

        ActionResult<Farm.Infrastructure.Dtos.PartsInventory.HarvestJobResponse> result =
            await _controller.HarvestJobAsync(jobId, null, CancellationToken.None);

        _ = Assert.IsType<ConflictObjectResult>(result.Result);
    }

    [Fact]
    public async Task HarvestJobAsync_NoMappings_ReturnsCanonicalConflictProblemDetails()
    {
        var jobId = Guid.NewGuid();
        Guid projectFileId = Guid.NewGuid();
        Guid gcodeFileId = Guid.NewGuid();
        const string Guidance =
            "Configure a printed-part output mapping or resubmit with a complete explicit outputs[] list.";
        _partHarvestServiceMock
            .Setup(s => s.HarvestJobAsync(
                jobId,
                It.IsAny<Farm.Infrastructure.Dtos.PartsInventory.HarvestJobRequest>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Farm.Infrastructure.Services.PartsInventory.HarvestResult(
                Farm.Infrastructure.Services.PartsInventory.PartInventoryOutcome.NoMappings,
                null,
                "no mappings",
                MappingRequired: new Farm.Infrastructure.Dtos.PartsInventory.PartMappingRequiredResponse(
                    jobId,
                    projectFileId,
                    gcodeFileId,
                    Guidance)));

        ActionResult<Farm.Infrastructure.Dtos.PartsInventory.HarvestJobResponse> result =
            await _controller.HarvestJobAsync(jobId, null, CancellationToken.None);

        ObjectResult conflict = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status409Conflict, conflict.StatusCode);
        Assert.Contains("application/problem+json", conflict.ContentTypes);
        ProblemDetails problem = Assert.IsType<ProblemDetails>(conflict.Value);
        Assert.Equal("partMappingRequired", problem.Extensions["code"]);
        Assert.Equal(jobId, problem.Extensions["jobId"]);
        Assert.Equal(projectFileId, problem.Extensions["projectFileId"]);
        Assert.Equal(gcodeFileId, problem.Extensions["gcodeFileId"]);
        Assert.Equal(Guidance, problem.Extensions["guidance"]);
    }

    [Fact]
    public async Task HarvestJobAsync_WrongBin_ReturnsCanonicalConflictProblemDetails()
    {
        Guid jobId = Guid.NewGuid();
        var mismatches = new[]
        {
            new Farm.Infrastructure.Dtos.PartsInventory.WrongBinMismatchResponse(
                "SKU-A",
                "BIN-A",
                "BIN-B"),
        };
        _partHarvestServiceMock
            .Setup(service => service.HarvestJobAsync(
                jobId,
                It.IsAny<Farm.Infrastructure.Dtos.PartsInventory.HarvestJobRequest>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Farm.Infrastructure.Services.PartsInventory.HarvestResult(
                Farm.Infrastructure.Services.PartsInventory.PartInventoryOutcome.WrongBin,
                null,
                "wrong bin",
                new Farm.Infrastructure.Dtos.PartsInventory.WrongBinResponse(mismatches)));

        ActionResult<Farm.Infrastructure.Dtos.PartsInventory.HarvestJobResponse> result =
            await _controller.HarvestJobAsync(jobId, null, CancellationToken.None);

        ObjectResult conflict = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status409Conflict, conflict.StatusCode);
        Assert.Contains("application/problem+json", conflict.ContentTypes);
        ProblemDetails problem = Assert.IsType<ProblemDetails>(conflict.Value);
        Assert.Equal("wrongBin", problem.Extensions["code"]);
        IReadOnlyList<Farm.Infrastructure.Dtos.PartsInventory.WrongBinMismatchResponse> payload =
            Assert.IsAssignableFrom<IReadOnlyList<Farm.Infrastructure.Dtos.PartsInventory.WrongBinMismatchResponse>>(
                problem.Extensions["mismatches"]);
        Farm.Infrastructure.Dtos.PartsInventory.WrongBinMismatchResponse mismatch = Assert.Single(payload);
        Assert.Equal("SKU-A", mismatch.PartSku);
        Assert.Equal("BIN-A", mismatch.ExpectedBinCode);
        Assert.Equal("BIN-B", mismatch.ScannedBinCode);
    }

    [Fact]
    public async Task HarvestJobAsync_FeatureDisabled_Returns404WithoutCallingService()
    {
        _operatorFeatureGateMock
            .Setup(gate => gate.IsEnabled(OperatorFeature.PrintedPartsInventory))
            .Returns(false);
        _operatorFeatureGateMock
            .Setup(gate => gate.IsEnabledAsync(OperatorFeature.PrintedPartsInventory, It.IsAny<CancellationToken>()))
            .Returns(() => Task.FromResult(false));
        _operatorFeatureGateMock
            .Setup(gate => gate.GetFlagName(OperatorFeature.PrintedPartsInventory))
            .Returns("printedPartsInventoryEnabled");

        ActionResult<Farm.Infrastructure.Dtos.PartsInventory.HarvestJobResponse> result =
            await _controller.HarvestJobAsync(Guid.NewGuid(), null, CancellationToken.None);

        NotFoundObjectResult notFound = Assert.IsType<NotFoundObjectResult>(result.Result);
        ProblemDetails problem = Assert.IsType<ProblemDetails>(notFound.Value);
        Assert.Equal("featureDisabled", problem.Extensions["code"]);
        _partHarvestServiceMock.VerifyNoOtherCalls();
    }
}
