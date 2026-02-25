using Farm.Infrastructure;
using Farm.Infrastructure.Services.Interfaces;
using Farm.Infrastructure.Services.Printers;
using Farm.Infrastructure.Services.Queue;
using Farm.Web.Api.Controllers;
using Microsoft.AspNetCore.Mvc;
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
    private readonly Mock<IPrinterStatusCacheReader> _printerStatusCacheMock;
    private readonly JobQueueController _controller;

    public JobQueueControllerTests()
    {
        _queueServiceMock = new Mock<IJobQueueService>();
        _printJobManagementServiceMock = new Mock<IPrintJobManagementService>();
        _loggerMock = new Mock<ILogger<JobQueueController>>();
        _printJobCompletionServiceMock = new Mock<IPrintJobCompletionService>();
        _printerStatusCacheMock = new Mock<IPrinterStatusCacheReader>();
        _controller = new JobQueueController(
            _queueServiceMock.Object,
            _printJobManagementServiceMock.Object,
            _printJobCompletionServiceMock.Object,
            _printerStatusCacheMock.Object,
            _loggerMock.Object);
    }

    [Fact]
    public void Constructor_WithValidDependencies_CreatesInstance()
    {
        // Arrange & Act
        var controller = new JobQueueController(
            _queueServiceMock.Object,
            _printJobManagementServiceMock.Object,
            _printJobCompletionServiceMock.Object,
            _printerStatusCacheMock.Object,
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
            Priority = 0,
            QueuePosition = 1,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        _queueServiceMock
            .Setup(s => s.AddJobToQueueAsync(request, It.IsAny<CancellationToken>()))
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
    public async Task QueueJobAsync_WithNonExistentFile_ReturnsNotFound()
    {
        // Arrange
        var request = new QueuePrintJobDto
        {
            GcodeFileId = Guid.NewGuid(),
            Priority = PrintJobPriority.Normal
        };

        _queueServiceMock
            .Setup(s => s.AddJobToQueueAsync(request, It.IsAny<CancellationToken>()))
            .ReturnsAsync((JobQueuePrintJobDto?)null);

        // Act
        ActionResult<JobQueuePrintJobDto> result = await _controller.QueueJobAsync(request);

        // Assert
        ActionResult<JobQueuePrintJobDto> actionResult = Assert.IsType<ActionResult<JobQueuePrintJobDto>>(result);
        Assert.IsType<NotFoundObjectResult>(actionResult.Result);
    }

    [Fact]
    public async Task GetJobAsync_WithValidId_ReturnsOk()
    {
        // Arrange
        var jobId = Guid.NewGuid();
        var jobDto = new JobQueuePrintJobDto
        {
            Id = jobId,
            GcodeFileId = Guid.NewGuid(),
            GcodeFileName = "test.gcode",
            Status = PrintJobStatus.Queued,
            Priority = 0,
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
            Priority = 10,
            QueuePosition = 1,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        _queueServiceMock
            .Setup(s => s.UpdateJobAsync(jobId, request, It.IsAny<CancellationToken>()))
            .ReturnsAsync(updatedDto);

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
            .Setup(s => s.UpdateJobAsync(jobId, request, It.IsAny<CancellationToken>()))
            .ReturnsAsync((JobQueuePrintJobDto?)null);

        // Act
        ActionResult<JobQueuePrintJobDto> result = await _controller.UpdateJobAsync(jobId, request);

        // Assert
        ActionResult<JobQueuePrintJobDto> actionResult = Assert.IsType<ActionResult<JobQueuePrintJobDto>>(result);
        Assert.IsType<NotFoundObjectResult>(actionResult.Result);
    }

    [Fact]
    public async Task DeleteJobAsync_WithValidId_ReturnsNoContent()
    {
        // Arrange
        var jobId = Guid.NewGuid();

        _queueServiceMock
            .Setup(s => s.RemoveJobAsync(jobId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

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
            .Setup(s => s.RemoveJobAsync(jobId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

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
            .Setup(s => s.RemoveJobAsync(jobId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Test exception"));

        // Act
        IActionResult result = await _controller.DeleteJobAsync(jobId);

        // Assert
        ObjectResult problemResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(500, problemResult.StatusCode);
    }
}
