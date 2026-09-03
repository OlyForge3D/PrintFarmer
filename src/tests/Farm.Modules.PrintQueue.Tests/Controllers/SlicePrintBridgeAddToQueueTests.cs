using System.Security.Claims;
using Farm.Infrastructure;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Interfaces;
using Farm.Infrastructure.Services.Printers;
using Farm.Infrastructure.Services.Queue;
using Farm.Modules.Calibration.Services.Calibration;
using Farm.Modules.Gcode.Services.Gcode;
using Farm.Modules.PrintQueue.Controllers;
using Farm.Modules.PrintQueue.Controllers.Requests;
using Farm.Modules.PrintQueue.Controllers.Responses;
using Farm.Slicer.Module.Data.Repositories;
using Farm.Slicer.Module.Domain;
using Farm.Web.Api.Services.Gcode.Safety;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;

namespace Farm.Modules.PrintQueue.Tests.Controllers;

/// <summary>Tests promotion-first queue submission for completed slice jobs.</summary>
public sealed class SlicePrintBridgeAddToQueueTests
{
    private static readonly Guid TestUserId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");

    private readonly Mock<IPrintersService> _printers = new();
    private readonly Mock<ILogger<SlicePrintBridgeController>> _logger = new();
    private readonly Mock<IGcodeSafetyValidator> _safety = new();
    private readonly Mock<ISliceJobRepository> _jobs = new();
    private readonly Mock<IJobQueueService> _queue = new();
    private readonly Mock<ISpoolmanService> _spoolman = new();
    private readonly Mock<ISliceArtifactLibraryService> _library = new();

    [Fact]
    public async Task AddToQueueAsync_WhenPromotionServiceUnavailable_Returns503()
    {
        SlicePrintBridgeController controller = BuildController(includeLibrary: false);

        IActionResult result = await controller.AddToQueueAsync(
            Guid.NewGuid(),
            new AddSliceToQueueRequest(),
            CancellationToken.None);

        result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AddToQueueAsync_WhenCompleted_PromotesBeforeEnqueueingDurableFile(bool createdNew)
    {
        Guid jobId = Guid.NewGuid();
        Guid gcodeFileId = Guid.NewGuid();
        Guid printJobId = Guid.NewGuid();
        SetupCompletedJob(jobId);
        SetupPromotion(jobId, gcodeFileId, createdNew);
        _queue
            .Setup(service => service.AddJobToQueueAsync(
                It.Is<QueuePrintJobDto>(request => request.GcodeFileId == gcodeFileId),
                TestUserId,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new JobQueuePrintJobDto { Id = printJobId, QueuePosition = 2 });

        IActionResult result = await BuildController().AddToQueueAsync(
            jobId,
            new AddSliceToQueueRequest(),
            CancellationToken.None);

        AddSliceToQueueResponse response = result.Should().BeOfType<OkObjectResult>()
            .Which.Value.Should().BeOfType<AddSliceToQueueResponse>().Subject;
        response.PrintJobId.Should().Be(printJobId);
        _library.Verify(service => service.PromoteAsync(
            jobId,
            null,
            It.Is<CalibrationActor>(actor => actor.UserId == TestUserId),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task AddToQueueAsync_WithArtifactId_QueuesExactlyTheSelectedDurableFile()
    {
        Guid jobId = Guid.NewGuid();
        Guid artifactId = Guid.NewGuid();
        Guid gcodeFileId = Guid.NewGuid();
        SetupCompletedJob(jobId);
        SetupPromotion(jobId, gcodeFileId, createdNew: true, artifactId: artifactId);
        _queue
            .Setup(service => service.AddJobToQueueAsync(
                It.Is<QueuePrintJobDto>(request => request.GcodeFileId == gcodeFileId),
                TestUserId,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new JobQueuePrintJobDto { Id = Guid.NewGuid(), QueuePosition = 1 });

        IActionResult result = await BuildController().AddToQueueAsync(
            jobId,
            new AddSliceToQueueRequest { ArtifactId = artifactId },
            CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
        _library.Verify(service => service.PromoteAsync(
            jobId,
            artifactId,
            It.IsAny<CalibrationActor>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task AddToQueueAsync_MultipleOutputsWithoutArtifactId_ReturnsSelectionRequired()
    {
        Guid jobId = Guid.NewGuid();
        SetupCompletedJob(jobId);
        _library
            .Setup(service => service.PromoteAsync(
                jobId,
                null,
                It.IsAny<CalibrationActor>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(CalibrationApiResult<SliceArtifactLibraryResult>.Failure(
                StatusCodes.Status409Conflict,
                "source_artifact_required"));

        IActionResult result = await BuildController().AddToQueueAsync(
            jobId,
            new AddSliceToQueueRequest(),
            CancellationToken.None);

        result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status409Conflict);
        _queue.Verify(service => service.AddJobToQueueAsync(
            It.IsAny<QueuePrintJobDto>(),
            It.IsAny<Guid?>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AddToQueueAsync_WhenEnqueueFails_ReturnsRetainedPromotedFileId(bool createdNew)
    {
        Guid jobId = Guid.NewGuid();
        Guid gcodeFileId = Guid.NewGuid();
        SetupCompletedJob(jobId);
        SetupPromotion(jobId, gcodeFileId, createdNew);
        _queue
            .Setup(service => service.AddJobToQueueAsync(
                It.IsAny<QueuePrintJobDto>(),
                TestUserId,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((JobQueuePrintJobDto?)null);

        IActionResult result = await BuildController().AddToQueueAsync(
            jobId,
            new AddSliceToQueueRequest(),
            CancellationToken.None);

        object body = result.Should().BeOfType<BadRequestObjectResult>().Which.Value!;
        body.GetType().GetProperty("gcodeFileId")!.GetValue(body).Should().Be(gcodeFileId);
    }

    [Fact]
    public async Task AddToQueueAsync_WhenPromotionFails_DoesNotEnqueue()
    {
        Guid jobId = Guid.NewGuid();
        SetupCompletedJob(jobId);
        _library
            .Setup(service => service.PromoteAsync(
                jobId,
                null,
                It.IsAny<CalibrationActor>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(CalibrationApiResult<SliceArtifactLibraryResult>.Failure(
                StatusCodes.Status503ServiceUnavailable,
                "promotion_source_transport_unavailable"));

        IActionResult result = await BuildController().AddToQueueAsync(
            jobId,
            new AddSliceToQueueRequest(),
            CancellationToken.None);

        result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        _queue.Verify(service => service.AddJobToQueueAsync(
            It.IsAny<QueuePrintJobDto>(),
            It.IsAny<Guid?>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    private SlicePrintBridgeController BuildController(bool includeLibrary = true)
    {
        var controller = new SlicePrintBridgeController(
            _printers.Object,
            _logger.Object,
            _safety.Object,
            jobRepository: _jobs.Object,
            jobQueueService: _queue.Object,
            spoolmanService: _spoolman.Object,
            sliceArtifactLibraryService: includeLibrary ? _library.Object : null);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim(ClaimTypes.NameIdentifier, TestUserId.ToString())],
                    "TestAuth")),
            },
        };
        return controller;
    }

    private void SetupCompletedJob(Guid jobId)
    {
        _jobs
            .Setup(repository => repository.GetByIdAsync(jobId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SliceJob
            {
                Id = jobId,
                UserId = TestUserId,
                Status = SliceJobStatus.Completed,
                ModelFileUrl = "test://model.stl",
                ModelFileName = "model.stl",
                QueuedAt = DateTime.UtcNow,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
    }

    private void SetupPromotion(
        Guid jobId,
        Guid gcodeFileId,
        bool createdNew,
        Guid? artifactId = null)
    {
        _library
            .Setup(service => service.PromoteAsync(
                jobId,
                artifactId,
                It.IsAny<CalibrationActor>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(CalibrationApiResult<SliceArtifactLibraryResult>.Success(
                new SliceArtifactLibraryResult
                {
                    GcodeFileId = gcodeFileId,
                    Name = "model.gcode",
                    SizeBytes = 1024,
                    CreatedNew = createdNew,
                    Printable = true,
                    SliceJobId = jobId,
                    SourceArtifactId = artifactId ?? Guid.NewGuid(),
                },
                createdNew ? StatusCodes.Status201Created : StatusCodes.Status200OK,
                replayed: !createdNew));
    }
}
