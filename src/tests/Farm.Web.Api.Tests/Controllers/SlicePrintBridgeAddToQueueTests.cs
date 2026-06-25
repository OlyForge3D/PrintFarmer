using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure;
using Farm.Infrastructure.Services.Gcode;
using Farm.Infrastructure.Services.Interfaces;
using Farm.Infrastructure.Services.Printers;
using Farm.Infrastructure.Services.Queue;
using Farm.Slicer.Module.Data.Repositories;
using Farm.Slicer.Module.Domain;
using Farm.Slicer.Module.Services;
using Farm.Web.Api.Controllers;
using Farm.Web.Api.Controllers.Requests;
using Farm.Web.Api.Controllers.Responses;
using Farm.Web.Api.Services.Gcode;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Controllers;

/// <summary>
/// Unit tests for the <c>POST api/slice/{id}/add-to-queue</c> endpoint on
/// <see cref="SlicePrintBridgeController"/>.
/// </summary>
public sealed class SlicePrintBridgeAddToQueueTests
{
    private static readonly Guid TestUserId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
    private static readonly Guid OtherUserId = Guid.Parse("11111111-2222-3333-4444-555555555555");

    private readonly Mock<IPrintersService> _printersMock = new();
    private readonly Mock<ILogger<SlicePrintBridgeController>> _loggerMock = new();
    private readonly Mock<ISliceJobRepository> _jobRepoMock = new();
    private readonly Mock<IArtifactsService> _artifactsMock = new();
    private readonly Mock<IJobQueueService> _queueMock = new();
    private readonly Mock<ISliceGcodeImportService> _importMock = new();
    private readonly Mock<ISpoolmanService> _spoolmanMock = new();
    private readonly Mock<IGcodeFilesService> _gcodeFilesServiceMock = new();

    private SlicePrintBridgeController BuildController(bool slicerEnabled = true) =>
        BuildControllerWithIdentity(TestUserId, slicerEnabled);

    private SlicePrintBridgeController BuildControllerWithIdentity(Guid userId, bool slicerEnabled = true)
    {
        var controller = new SlicePrintBridgeController(
            _printersMock.Object,
            _loggerMock.Object,
            jobRepository: slicerEnabled ? _jobRepoMock.Object : null,
            artifactsService: slicerEnabled ? _artifactsMock.Object : null,
            jobQueueService: _queueMock.Object,
            importService: _importMock.Object,
            spoolmanService: _spoolmanMock.Object,
            gcodeFilesService: _gcodeFilesServiceMock.Object);

        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, userId.ToString()) };
        var identity = new ClaimsIdentity(claims, "TestAuth");
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) }
        };

        return controller;
    }

    // =========================================================================
    // Slicer disabled → 503
    // =========================================================================

    [Fact]
    [Trait("Category", "AddToQueue")]
    public async Task AddToQueue_SlicerDisabled_Returns503()
    {
        SlicePrintBridgeController controller = BuildController(slicerEnabled: false);
        var request = new AddSliceToQueueRequest();

        IActionResult result = await controller.AddToQueueAsync(Guid.NewGuid(), request, CancellationToken.None);

        var statusResult = result.Should().BeOfType<ObjectResult>().Subject;
        statusResult.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
    }

    // =========================================================================
    // Job not found → 404
    // =========================================================================

    [Fact]
    [Trait("Category", "AddToQueue")]
    public async Task AddToQueue_JobNotFound_Returns404()
    {
        Guid jobId = Guid.NewGuid();
        _jobRepoMock
            .Setup(r => r.GetByIdAsync(jobId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((SliceJob?)null);

        IActionResult result = await BuildController()
            .AddToQueueAsync(jobId, new AddSliceToQueueRequest(), CancellationToken.None);

        result.Should().BeOfType<NotFoundObjectResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status404NotFound);
    }

    // =========================================================================
    // Not the job owner → Forbid
    // =========================================================================

    [Fact]
    [Trait("Category", "AddToQueue")]
    public async Task AddToQueue_NotOwner_ReturnsForbid()
    {
        Guid jobId = Guid.NewGuid();
        // Job is owned by OtherUserId, but the request is made by TestUserId
        SliceJob job = CreateJob(jobId, SliceJobStatus.Completed, OtherUserId);
        _jobRepoMock
            .Setup(r => r.GetByIdAsync(jobId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(job);

        IActionResult result = await BuildController()
            .AddToQueueAsync(jobId, new AddSliceToQueueRequest(), CancellationToken.None);

        result.Should().BeOfType<ForbidResult>();
    }

    // =========================================================================
    // Job not completed → 400
    // =========================================================================

    [Theory]
    [InlineData(SliceJobStatus.Queued)]
    [InlineData(SliceJobStatus.Processing)]
    [InlineData(SliceJobStatus.Failed)]
    [InlineData(SliceJobStatus.Cancelled)]
    [Trait("Category", "AddToQueue")]
    public async Task AddToQueue_NotCompleted_Returns400(string status)
    {
        Guid jobId = Guid.NewGuid();
        _jobRepoMock
            .Setup(r => r.GetByIdAsync(jobId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateJob(jobId, status));

        IActionResult result = await BuildController()
            .AddToQueueAsync(jobId, new AddSliceToQueueRequest(), CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }

    // =========================================================================
    // Happy path — correct GcodeFileId is forwarded to the queue
    // =========================================================================

    [Fact]
    [Trait("Category", "AddToQueue")]
    public async Task AddToQueue_HappyPath_EnqueuesWithCorrectGcodeFileId()
    {
        Guid jobId = Guid.NewGuid();
        Guid gcodeFileId = Guid.NewGuid();
        Guid printJobId = Guid.NewGuid();
        Artifact artifact = CreateArtifact(jobId, "gcode", "model.gcode");

        SetupCompletedJobWithGcode(jobId, artifact);

        string fakePath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"{Guid.NewGuid()}.gcode");
        System.IO.File.WriteAllText(fakePath, "; test gcode");

        try
        {
            _artifactsMock
                .Setup(a => a.GetWithPathAsync(artifact.Id, It.IsAny<CancellationToken>()))
                .ReturnsAsync((artifact, fakePath));

            _importMock
                .Setup(s => s.ImportAsync(artifact.FileName, fakePath, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new SliceGcodeImportResult(gcodeFileId, true));

            _queueMock
                .Setup(q => q.AddJobToQueueAsync(
                    It.Is<QueuePrintJobDto>(d => d.GcodeFileId == gcodeFileId),
                    TestUserId,
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new JobQueuePrintJobDto { Id = printJobId, QueuePosition = 2 });

            IActionResult result = await BuildController()
                .AddToQueueAsync(jobId, new AddSliceToQueueRequest(), CancellationToken.None);

            var ok = result.Should().BeOfType<OkObjectResult>().Subject;
            var response = ok.Value.Should().BeOfType<AddSliceToQueueResponse>().Subject;
            response.PrintJobId.Should().Be(printJobId);
            response.QueuePosition.Should().Be(2);
        }
        finally
        {
            System.IO.File.Delete(fakePath);
        }
    }

    // =========================================================================
    // Happy path — SpoolId resolves to SpoolmanFilamentId on the queue DTO
    // =========================================================================

    [Fact]
    [Trait("Category", "AddToQueue")]
    public async Task AddToQueue_WithSpoolId_ResolvesSpoolmanFilamentId()
    {
        Guid jobId = Guid.NewGuid();
        Guid gcodeFileId = Guid.NewGuid();
        int spoolId = 7;
        int filamentId = 42;
        Artifact artifact = CreateArtifact(jobId, "gcode", "model.gcode");

        SetupCompletedJobWithGcode(jobId, artifact);

        string fakePath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"{Guid.NewGuid()}.gcode");
        System.IO.File.WriteAllText(fakePath, "; test gcode");

        try
        {
            _artifactsMock
                .Setup(a => a.GetWithPathAsync(artifact.Id, It.IsAny<CancellationToken>()))
                .ReturnsAsync((artifact, fakePath));

            _importMock
                .Setup(s => s.ImportAsync(It.IsAny<string>(), fakePath, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new SliceGcodeImportResult(gcodeFileId, true));

            _spoolmanMock
                .Setup(sp => sp.GetSpoolByIdAsync(spoolId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new SpoolmanSpoolDto(
                    Id: spoolId,
                    Name: "Red PLA",
                    Material: "PLA",
                    RemainingWeightG: 800,
                    ColorHex: "FF0000",
                    InUse: false,
                    FilamentName: "PolyTerra PLA",
                    Vendor: "Polymaker",
                    FilamentId: filamentId));

            QueuePrintJobDto? capturedDto = null;
            _queueMock
                .Setup(q => q.AddJobToQueueAsync(It.IsAny<QueuePrintJobDto>(), TestUserId, It.IsAny<CancellationToken>()))
                .Callback<QueuePrintJobDto, Guid?, CancellationToken>((dto, _, _) => capturedDto = dto)
                .ReturnsAsync(new JobQueuePrintJobDto { Id = Guid.NewGuid(), QueuePosition = 1 });

            var request = new AddSliceToQueueRequest { SpoolId = spoolId };
            IActionResult result = await BuildController().AddToQueueAsync(jobId, request, CancellationToken.None);

            result.Should().BeOfType<OkObjectResult>();
            capturedDto.Should().NotBeNull();
            capturedDto!.SpoolmanFilamentId.Should().Be(filamentId);
            capturedDto.FilamentName.Should().Be("PolyTerra PLA");
            capturedDto.FilamentVendor.Should().Be("Polymaker");
            capturedDto.FilamentColor.Should().Be("FF0000");
        }
        finally
        {
            System.IO.File.Delete(fakePath);
        }
    }

    // =========================================================================
    // Spool resolution fails — job still enqueues without spool fields
    // =========================================================================
    [Fact]
    [Trait("Category", "AddToQueue")]
    public async Task AddToQueue_SpoolResolutionFails_StillEnqueues()
    {
        Guid jobId = Guid.NewGuid();
        Guid gcodeFileId = Guid.NewGuid();
        Artifact artifact = CreateArtifact(jobId, "gcode", "model.gcode");

        SetupCompletedJobWithGcode(jobId, artifact);

        string fakePath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"{Guid.NewGuid()}.gcode");
        System.IO.File.WriteAllText(fakePath, "; test gcode");

        try
        {
            _artifactsMock
                .Setup(a => a.GetWithPathAsync(artifact.Id, It.IsAny<CancellationToken>()))
                .ReturnsAsync((artifact, fakePath));

            _importMock
                .Setup(s => s.ImportAsync(It.IsAny<string>(), fakePath, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new SliceGcodeImportResult(gcodeFileId, true));

            _spoolmanMock
                .Setup(sp => sp.GetSpoolByIdAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new HttpRequestException("Spoolman unreachable"));

            QueuePrintJobDto? capturedDto = null;
            _queueMock
                .Setup(q => q.AddJobToQueueAsync(It.IsAny<QueuePrintJobDto>(), TestUserId, It.IsAny<CancellationToken>()))
                .Callback<QueuePrintJobDto, Guid?, CancellationToken>((dto, _, _) => capturedDto = dto)
                .ReturnsAsync(new JobQueuePrintJobDto { Id = Guid.NewGuid(), QueuePosition = 3 });

            var request = new AddSliceToQueueRequest { SpoolId = 99 };
            IActionResult result = await BuildController().AddToQueueAsync(jobId, request, CancellationToken.None);

            result.Should().BeOfType<OkObjectResult>();
            capturedDto.Should().NotBeNull();
            capturedDto!.SpoolmanFilamentId.Should().BeNull();
            capturedDto.FilamentName.Should().BeNull();
            capturedDto.FilamentVendor.Should().BeNull();
        }
        finally
        {
            System.IO.File.Delete(fakePath);
        }
    }

    // =========================================================================
    // No compatible printer — orphan cleanup (Fix 1)
    // =========================================================================

    [Fact]
    [Trait("Category", "AddToQueue")]
    public async Task AddToQueue_NoCompatiblePrinter_NewFile_DeletesOrphanAndReturns400()
    {
        Guid jobId = Guid.NewGuid();
        Guid gcodeFileId = Guid.NewGuid();

        _gcodeFilesServiceMock
            .Setup(g => g.DeleteFileAsync(gcodeFileId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var (result, fakePath) = await SetupAndRunQueueReturnsNull(jobId, gcodeFileId, isNewFile: true);

        try
        {
            result.Should().BeOfType<BadRequestObjectResult>()
                .Which.StatusCode.Should().Be(StatusCodes.Status400BadRequest);

            _gcodeFilesServiceMock.Verify(
                g => g.DeleteFileAsync(gcodeFileId, It.IsAny<CancellationToken>()),
                Times.Once);
        }
        finally
        {
            System.IO.File.Delete(fakePath);
        }
    }

    [Fact]
    [Trait("Category", "AddToQueue")]
    public async Task AddToQueue_NoCompatiblePrinter_ReusedFile_NeverDeletesAndReturns400()
    {
        Guid jobId = Guid.NewGuid();
        Guid gcodeFileId = Guid.NewGuid();

        var (result, fakePath) = await SetupAndRunQueueReturnsNull(jobId, gcodeFileId, isNewFile: false);

        try
        {
            result.Should().BeOfType<BadRequestObjectResult>()
                .Which.StatusCode.Should().Be(StatusCodes.Status400BadRequest);

            _gcodeFilesServiceMock.Verify(
                g => g.DeleteFileAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()),
                Times.Never);
        }
        finally
        {
            System.IO.File.Delete(fakePath);
        }
    }

    // =========================================================================
    // Copies range validation (Fix 2)
    // =========================================================================

    [Theory]
    [InlineData(0)]
    [InlineData(100)]
    [Trait("Category", "AddToQueue")]
    public async Task AddToQueue_InvalidCopies_Returns400(int copies)
    {
        Guid jobId = Guid.NewGuid();
        var request = new AddSliceToQueueRequest { Copies = copies };

        IActionResult result = await BuildController()
            .AddToQueueAsync(jobId, request, CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(99)]
    [Trait("Category", "AddToQueue")]
    public async Task AddToQueue_BoundaryCopies_EnqueuesSuccessfully(int copies)
    {
        Guid jobId = Guid.NewGuid();
        Guid gcodeFileId = Guid.NewGuid();
        Artifact artifact = CreateArtifact(jobId, "gcode", "model.gcode");

        SetupCompletedJobWithGcode(jobId, artifact);

        string fakePath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"{Guid.NewGuid()}.gcode");
        System.IO.File.WriteAllText(fakePath, "; test gcode");

        try
        {
            _artifactsMock
                .Setup(a => a.GetWithPathAsync(artifact.Id, It.IsAny<CancellationToken>()))
                .ReturnsAsync((artifact, fakePath));

            _importMock
                .Setup(s => s.ImportAsync(It.IsAny<string>(), fakePath, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new SliceGcodeImportResult(gcodeFileId, true));

            _queueMock
                .Setup(q => q.AddJobToQueueAsync(
                    It.Is<QueuePrintJobDto>(d => d.Copies == copies),
                    TestUserId,
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new JobQueuePrintJobDto { Id = Guid.NewGuid(), QueuePosition = 1 });

            var request = new AddSliceToQueueRequest { Copies = copies };
            IActionResult result = await BuildController()
                .AddToQueueAsync(jobId, request, CancellationToken.None);

            result.Should().BeOfType<OkObjectResult>()
                .Which.StatusCode.Should().Be(StatusCodes.Status200OK);
        }
        finally
        {
            System.IO.File.Delete(fakePath);
        }
    }

    // =========================================================================
    // Helpers
    // =========================================================================

    private static SliceJob CreateJob(Guid id, string status, Guid? userId = null) => new()
    {
        Id = id,
        UserId = userId ?? TestUserId,
        Status = status,
        ModelFileUrl = "test://model.stl",
        ModelFileName = "model.stl",
        QueuedAt = DateTime.UtcNow,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow
    };

    private static Artifact CreateArtifact(Guid jobId, string kind, string fileName) => new()
    {
        Id = Guid.NewGuid(),
        JobId = jobId,
        Kind = kind,
        FileName = fileName,
        RelativePath = $"{jobId}/{fileName}",
        ContentType = kind == "gcode" ? "application/octet-stream" : "image/png",
        SizeBytes = 1024,
        CreatedAt = DateTime.UtcNow
    };

    private void SetupCompletedJobWithGcode(Guid jobId, Artifact gcodeArtifact)
    {
        _jobRepoMock
            .Setup(r => r.GetByIdAsync(jobId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateJob(jobId, SliceJobStatus.Completed));

        _artifactsMock
            .Setup(a => a.ListByJobAsync(jobId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Artifact> { gcodeArtifact });
    }

    private async Task<(IActionResult result, string fakePath)> SetupAndRunQueueReturnsNull(
        Guid jobId, Guid gcodeFileId, bool isNewFile)
    {
        Artifact artifact = CreateArtifact(jobId, "gcode", "model.gcode");
        SetupCompletedJobWithGcode(jobId, artifact);

        string fakePath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"{Guid.NewGuid()}.gcode");
        System.IO.File.WriteAllText(fakePath, "; test gcode");

        _artifactsMock
            .Setup(a => a.GetWithPathAsync(artifact.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync((artifact, fakePath));

        _importMock
            .Setup(s => s.ImportAsync(It.IsAny<string>(), fakePath, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SliceGcodeImportResult(gcodeFileId, isNewFile));

        _queueMock
            .Setup(q => q.AddJobToQueueAsync(It.IsAny<QueuePrintJobDto>(), TestUserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((JobQueuePrintJobDto?)null);

        IActionResult result = await BuildController()
            .AddToQueueAsync(jobId, new AddSliceToQueueRequest { Copies = 1 }, CancellationToken.None);

        return (result, fakePath);
    }
}
