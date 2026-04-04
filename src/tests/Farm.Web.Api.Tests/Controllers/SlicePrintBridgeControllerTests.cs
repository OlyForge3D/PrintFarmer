using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Printers;
using Farm.Slicer.Module.Data.Repositories;
using Farm.Slicer.Module.Domain;
using Farm.Slicer.Module.Services;
using Farm.Web.Api.Controllers;
using Farm.Web.Api.Controllers.Requests;
using Farm.Web.Api.Controllers.Responses;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Controllers;

/// <summary>
/// Unit tests for <see cref="SlicePrintBridgeController"/>.
/// Tests the send-to-printer bridge between slicer artifact storage and printer backends.
/// </summary>
public sealed class SlicePrintBridgeControllerTests : IDisposable
{
    private readonly Mock<ISliceJobRepository> _jobRepoMock = new();
    private readonly Mock<IArtifactsService> _artifactsMock = new();
    private readonly Mock<IPrintersService> _printersMock = new();
    private readonly Mock<ILogger<SlicePrintBridgeController>> _loggerMock = new();
    private readonly SlicePrintBridgeController _controller;
    private readonly string _tempDir;

    public SlicePrintBridgeControllerTests()
    {
        _controller = new SlicePrintBridgeController(
            _printersMock.Object,
            _loggerMock.Object,
            _jobRepoMock.Object,
            _artifactsMock.Object);

        _tempDir = Path.Combine(Path.GetTempPath(), $"slice_bridge_test_{Guid.NewGuid()}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    // =========================================================================
    // Slicer disabled (503)
    // =========================================================================

    [Fact]
    [Trait("Category", "SlicePrintBridge")]
    public async Task SendToPrinter_SlicerDisabled_Returns503()
    {
        // Arrange: controller without slicer services (simulating disabled slicer)
        var controller = new SlicePrintBridgeController(
            _printersMock.Object,
            _loggerMock.Object,
            jobRepository: null,
            artifactsService: null);

        var request = new SendToPrinterRequest { PrinterId = Guid.NewGuid() };

        // Act
        IActionResult result = await controller.SendToPrinterAsync(Guid.NewGuid(), request, CancellationToken.None);

        // Assert
        var statusResult = result.Should().BeOfType<ObjectResult>().Subject;
        statusResult.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
    }

    // =========================================================================
    // Job not found (404)
    // =========================================================================

    [Fact]
    [Trait("Category", "SlicePrintBridge")]
    public async Task SendToPrinter_JobNotFound_Returns404()
    {
        Guid jobId = Guid.NewGuid();
        _jobRepoMock
            .Setup(r => r.GetByIdAsync(jobId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((SliceJob?)null);

        var request = new SendToPrinterRequest { PrinterId = Guid.NewGuid() };

        IActionResult result = await _controller.SendToPrinterAsync(jobId, request, CancellationToken.None);

        var notFound = result.Should().BeOfType<NotFoundObjectResult>().Subject;
        notFound.StatusCode.Should().Be(StatusCodes.Status404NotFound);
    }

    // =========================================================================
    // Job not completed (400)
    // =========================================================================

    [Theory]
    [InlineData(SliceJobStatus.Queued)]
    [InlineData(SliceJobStatus.Processing)]
    [InlineData(SliceJobStatus.Failed)]
    [InlineData(SliceJobStatus.Cancelled)]
    [Trait("Category", "SlicePrintBridge")]
    public async Task SendToPrinter_JobNotCompleted_Returns400(string status)
    {
        Guid jobId = Guid.NewGuid();
        _jobRepoMock
            .Setup(r => r.GetByIdAsync(jobId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateJob(jobId, status));

        var request = new SendToPrinterRequest { PrinterId = Guid.NewGuid() };

        IActionResult result = await _controller.SendToPrinterAsync(jobId, request, CancellationToken.None);

        var badRequest = result.Should().BeOfType<BadRequestObjectResult>().Subject;
        badRequest.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }

    // =========================================================================
    // No gcode artifact (400)
    // =========================================================================

    [Fact]
    [Trait("Category", "SlicePrintBridge")]
    public async Task SendToPrinter_NoGcodeArtifact_Returns400()
    {
        Guid jobId = Guid.NewGuid();
        _jobRepoMock
            .Setup(r => r.GetByIdAsync(jobId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateJob(jobId, SliceJobStatus.Completed));

        // Return only non-gcode artifacts
        _artifactsMock
            .Setup(a => a.ListByJobAsync(jobId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Artifact>
            {
                CreateArtifact(jobId, "thumbnail", "preview.png"),
                CreateArtifact(jobId, "log", "slicer.log")
            });

        var request = new SendToPrinterRequest { PrinterId = Guid.NewGuid() };

        IActionResult result = await _controller.SendToPrinterAsync(jobId, request, CancellationToken.None);

        var badRequest = result.Should().BeOfType<BadRequestObjectResult>().Subject;
        badRequest.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }

    [Fact]
    [Trait("Category", "SlicePrintBridge")]
    public async Task SendToPrinter_EmptyArtifactList_Returns400()
    {
        Guid jobId = Guid.NewGuid();
        _jobRepoMock
            .Setup(r => r.GetByIdAsync(jobId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateJob(jobId, SliceJobStatus.Completed));

        _artifactsMock
            .Setup(a => a.ListByJobAsync(jobId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Artifact>());

        var request = new SendToPrinterRequest { PrinterId = Guid.NewGuid() };

        IActionResult result = await _controller.SendToPrinterAsync(jobId, request, CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    // =========================================================================
    // Printer not found (404)
    // =========================================================================

    [Fact]
    [Trait("Category", "SlicePrintBridge")]
    public async Task SendToPrinter_PrinterNotFound_Returns404()
    {
        Guid jobId = Guid.NewGuid();
        Guid printerId = Guid.NewGuid();
        Artifact gcode = CreateArtifact(jobId, "gcode", "model.gcode");

        SetupCompletedJobWithGcode(jobId, gcode);

        _printersMock
            .Setup(p => p.FindByIdAsync(printerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Printer?)null);

        var request = new SendToPrinterRequest { PrinterId = printerId };

        IActionResult result = await _controller.SendToPrinterAsync(jobId, request, CancellationToken.None);

        var notFound = result.Should().BeOfType<NotFoundObjectResult>().Subject;
        notFound.StatusCode.Should().Be(StatusCodes.Status404NotFound);
    }

    // =========================================================================
    // Artifact file missing from disk (400)
    // =========================================================================

    [Fact]
    [Trait("Category", "SlicePrintBridge")]
    public async Task SendToPrinter_ArtifactFileMissing_Returns400()
    {
        Guid jobId = Guid.NewGuid();
        Guid printerId = Guid.NewGuid();
        Artifact gcode = CreateArtifact(jobId, "gcode", "model.gcode");

        SetupCompletedJobWithGcode(jobId, gcode);
        SetupPrinterExists(printerId);

        // GetWithPathAsync returns a path that does not exist on disk
        _artifactsMock
            .Setup(a => a.GetWithPathAsync(gcode.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync((gcode, Path.Combine(_tempDir, "nonexistent.gcode")));

        var request = new SendToPrinterRequest { PrinterId = printerId };

        IActionResult result = await _controller.SendToPrinterAsync(jobId, request, CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    [Trait("Category", "SlicePrintBridge")]
    public async Task SendToPrinter_GetWithPathReturnsNull_Returns400()
    {
        Guid jobId = Guid.NewGuid();
        Guid printerId = Guid.NewGuid();
        Artifact gcode = CreateArtifact(jobId, "gcode", "model.gcode");

        SetupCompletedJobWithGcode(jobId, gcode);
        SetupPrinterExists(printerId);

        _artifactsMock
            .Setup(a => a.GetWithPathAsync(gcode.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(((Artifact, string)?)null);

        var request = new SendToPrinterRequest { PrinterId = printerId };

        IActionResult result = await _controller.SendToPrinterAsync(jobId, request, CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    // =========================================================================
    // Happy path: upload only (200)
    // =========================================================================

    [Fact]
    [Trait("Category", "SlicePrintBridge")]
    public async Task SendToPrinter_UploadOnly_Returns200()
    {
        Guid jobId = Guid.NewGuid();
        Guid printerId = Guid.NewGuid();
        Artifact gcode = CreateArtifact(jobId, "gcode", "model.gcode");
        string filePath = CreateTempGcodeFile("model.gcode");

        SetupCompletedJobWithGcode(jobId, gcode);
        SetupPrinterExists(printerId);
        SetupArtifactPath(gcode, filePath);

        _printersMock
            .Setup(p => p.UploadGcodeAsync(printerId, gcode.FileName, It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var request = new SendToPrinterRequest { PrinterId = printerId, StartPrint = false };

        IActionResult result = await _controller.SendToPrinterAsync(jobId, request, CancellationToken.None);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var response = ok.Value.Should().BeOfType<SendToPrinterResponse>().Subject;
        response.JobId.Should().Be(jobId);
        response.PrinterId.Should().Be(printerId);
        response.FileName.Should().Be("model.gcode");
        response.PrintStarted.Should().BeFalse();
    }

    // =========================================================================
    // Happy path: upload and start print (200)
    // =========================================================================

    [Fact]
    [Trait("Category", "SlicePrintBridge")]
    public async Task SendToPrinter_UploadAndStartPrint_Returns200()
    {
        Guid jobId = Guid.NewGuid();
        Guid printerId = Guid.NewGuid();
        Artifact gcode = CreateArtifact(jobId, "gcode", "model.gcode");
        string filePath = CreateTempGcodeFile("model.gcode");

        SetupCompletedJobWithGcode(jobId, gcode);
        SetupPrinterExists(printerId);
        SetupArtifactPath(gcode, filePath);

        _printersMock
            .Setup(p => p.UploadAndStartPrintAsync(
                printerId, gcode.FileName, It.IsAny<Stream>(), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(UploadAndPrintResult.Ok());

        var request = new SendToPrinterRequest { PrinterId = printerId, StartPrint = true };

        IActionResult result = await _controller.SendToPrinterAsync(jobId, request, CancellationToken.None);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var response = ok.Value.Should().BeOfType<SendToPrinterResponse>().Subject;
        response.JobId.Should().Be(jobId);
        response.PrinterId.Should().Be(printerId);
        response.FileName.Should().Be("model.gcode");
        response.PrintStarted.Should().BeTrue();
    }

    // =========================================================================
    // Upload fails (502)
    // =========================================================================

    [Fact]
    [Trait("Category", "SlicePrintBridge")]
    public async Task SendToPrinter_UploadFails_Returns502()
    {
        Guid jobId = Guid.NewGuid();
        Guid printerId = Guid.NewGuid();
        Artifact gcode = CreateArtifact(jobId, "gcode", "model.gcode");
        string filePath = CreateTempGcodeFile("model.gcode");

        SetupCompletedJobWithGcode(jobId, gcode);
        SetupPrinterExists(printerId);
        SetupArtifactPath(gcode, filePath);

        _printersMock
            .Setup(p => p.UploadGcodeAsync(printerId, gcode.FileName, It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var request = new SendToPrinterRequest { PrinterId = printerId, StartPrint = false };

        IActionResult result = await _controller.SendToPrinterAsync(jobId, request, CancellationToken.None);

        var statusResult = result.Should().BeOfType<ObjectResult>().Subject;
        statusResult.StatusCode.Should().Be(StatusCodes.Status502BadGateway);
    }

    // =========================================================================
    // Upload-and-print fails (502)
    // =========================================================================

    [Fact]
    [Trait("Category", "SlicePrintBridge")]
    public async Task SendToPrinter_UploadAndPrintFails_Returns502()
    {
        Guid jobId = Guid.NewGuid();
        Guid printerId = Guid.NewGuid();
        Artifact gcode = CreateArtifact(jobId, "gcode", "model.gcode");
        string filePath = CreateTempGcodeFile("model.gcode");

        SetupCompletedJobWithGcode(jobId, gcode);
        SetupPrinterExists(printerId);
        SetupArtifactPath(gcode, filePath);

        _printersMock
            .Setup(p => p.UploadAndStartPrintAsync(
                printerId, gcode.FileName, It.IsAny<Stream>(), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(UploadAndPrintResult.Fail(UploadAndPrintStage.Uploading, "Connection refused"));

        var request = new SendToPrinterRequest { PrinterId = printerId, StartPrint = true };

        IActionResult result = await _controller.SendToPrinterAsync(jobId, request, CancellationToken.None);

        var statusResult = result.Should().BeOfType<ObjectResult>().Subject;
        statusResult.StatusCode.Should().Be(StatusCodes.Status502BadGateway);
    }

    // =========================================================================
    // Helpers
    // =========================================================================

    private static SliceJob CreateJob(Guid id, string status) => new()
    {
        Id = id,
        UserId = Guid.NewGuid(),
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

    private void SetupPrinterExists(Guid printerId)
    {
        _printersMock
            .Setup(p => p.FindByIdAsync(printerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Printer { Id = printerId, Name = "Test Printer" });
    }

    private void SetupArtifactPath(Artifact artifact, string filePath)
    {
        _artifactsMock
            .Setup(a => a.GetWithPathAsync(artifact.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync((artifact, filePath));
    }

    private string CreateTempGcodeFile(string fileName)
    {
        string path = Path.Combine(_tempDir, fileName);
        File.WriteAllText(path, "; G-code test file\nG28\nG1 X10 Y10 Z0.2 F1500\n");
        return path;
    }
}
