using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Printers;
using Farm.Infrastructure.Services.Queue.Dispatch;
using Farm.Slicer.Module.Data.Repositories;
using Farm.Slicer.Module.Domain;
using Farm.Slicer.Module.Services;
using Farm.Web.Api.Controllers;
using Farm.Web.Api.Controllers.Requests;
using Farm.Web.Api.Controllers.Responses;
using Farm.Web.Api.Services.Gcode.Safety;
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
    private static readonly Guid TestUserId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");

    private readonly Mock<ISliceJobRepository> _jobRepoMock = new();
    private readonly Mock<IArtifactsService> _artifactsMock = new();
    private readonly Mock<IPrintersService> _printersMock = new();
    private readonly Mock<ILogger<SlicePrintBridgeController>> _loggerMock = new();
    private readonly Mock<IDispatchClaimService> _dispatchClaimMock = new();
    private readonly Mock<IGcodeSafetyValidator> _safetyValidatorMock = new();
    private readonly SlicePrintBridgeController _controller;
    private readonly string _tempDir;

    public SlicePrintBridgeControllerTests()
    {
        // Default to a clean safety report so upload happy-path tests are not coupled to
        // the general validator's g-code interpretation; dedicated tests below cover both
        // the accept and reject behavior of the send-to-printer safety gate itself.
        _safetyValidatorMock
            .Setup(v => v.Validate(It.IsAny<GcodeSafetyRequest>()))
            .Returns(GcodeSafetyResult<GcodeSafetyReport>.Success(new GcodeSafetyReport(
                GcodeSafetyCheckpoint.BeforeSendToPrinter,
                "test-sha256",
                1,
                DateTime.UtcNow)));

        // The slice→print bridge is a START PATH and must acquire the shared dispatch
        // claim before touching an adapter (issue #900, defect 5). The stub grants the
        // claim so the test exercises the adapter orchestration that follows it.
        _dispatchClaimMock
            .Setup(s => s.AcquireAdHocClaimAsync(
                It.IsAny<AdHocDispatchClaimRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => DispatchClaimResult.Ok(new QueueDispatchAttempt
            {
                Id = Guid.NewGuid(),
                PrinterId = Guid.NewGuid(),
                BackendCommandId = "test-command",
                BackendFileName = "model.gcode",
            }));
        _dispatchClaimMock
            .Setup(s => s.RecordBackendCallStartedAsync(
                It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _dispatchClaimMock
            .Setup(s => s.RecordBackendAcceptedAsync(
                It.IsAny<Guid>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _dispatchClaimMock
            .Setup(s => s.ReleaseClaimOnKnownFailureAsync(
                It.IsAny<Guid>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _dispatchClaimMock
            .Setup(s => s.RecordUnknownOutcomeAsync(
                It.IsAny<Guid>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        _controller = new SlicePrintBridgeController(
            _printersMock.Object,
            _loggerMock.Object,
            _safetyValidatorMock.Object,
            _jobRepoMock.Object,
            _artifactsMock.Object,
            dispatchClaimService: _dispatchClaimMock.Object);

        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, TestUserId.ToString()) };
        var identity = new ClaimsIdentity(claims, "TestAuth");
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) }
        };

        _tempDir = Path.Join(Path.GetTempPath(), $"slice_bridge_test_{Guid.NewGuid()}");
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
            _safetyValidatorMock.Object,
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "SlicePrintBridge")]
    public async Task SendToPrinter_CalibrationSlice_Returns422WithoutBackendEffect(bool startPrint)
    {
        Guid jobId = Guid.NewGuid();
        Guid printerId = Guid.NewGuid();
        SliceJob calibrationJob = CreateJob(jobId, SliceJobStatus.Completed);
        calibrationJob.CalibrationProjectId = Guid.NewGuid();
        calibrationJob.CalibrationAttemptId = Guid.NewGuid();

        _jobRepoMock
            .Setup(r => r.GetByIdAsync(jobId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(calibrationJob);

        IActionResult result = await _controller.SendToPrinterAsync(
            jobId,
            new SendToPrinterRequest { PrinterId = printerId, StartPrint = startPrint },
            CancellationToken.None);

        result.Should().BeOfType<UnprocessableEntityObjectResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status422UnprocessableEntity);
        _artifactsMock.Verify(
            a => a.ListByJobAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _dispatchClaimMock.Verify(
            c => c.AcquireAdHocClaimAsync(
                It.IsAny<AdHocDispatchClaimRequest>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
        _printersMock.Verify(
            p => p.UploadAndStartPrintAsync(
                It.IsAny<Guid>(),
                It.IsAny<string>(),
                It.IsAny<Stream>(),
                It.IsAny<IProgress<UploadAndPrintStage>?>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
        _printersMock.Verify(
            p => p.UploadGcodeAsync(
                It.IsAny<Guid>(),
                It.IsAny<string>(),
                It.IsAny<Stream>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
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
            .Setup(p => p.FindByIdWithIncludesAsync(printerId, It.IsAny<CancellationToken>()))
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
            .ReturnsAsync((gcode, Path.Join(_tempDir, "nonexistent.gcode")));

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
    // G-code safety gate: previously skipped, now invoked before send-to-printer
    // =========================================================================

    [Fact]
    [Trait("Category", "SlicePrintBridge")]
    public async Task SendToPrinter_SafetyValidatorRejects_Returns400AndDoesNotUpload()
    {
        Guid jobId = Guid.NewGuid();
        Guid printerId = Guid.NewGuid();
        Artifact gcode = CreateArtifact(jobId, "gcode", "model.gcode");
        string filePath = CreateTempGcodeFile("model.gcode");

        SetupCompletedJobWithGcode(jobId, gcode);
        SetupPrinterExists(printerId);
        SetupArtifactPath(gcode, filePath);

        _safetyValidatorMock
            .Setup(v => v.Validate(It.IsAny<GcodeSafetyRequest>()))
            .Returns(GcodeSafetyResult<GcodeSafetyReport>.Failure(
                GcodeSafetyProblemCodes.TemperatureAboveLimit,
                "M104.S",
                "Commanded nozzle temperature exceeds the configured ceiling."));

        var request = new SendToPrinterRequest { PrinterId = printerId, StartPrint = false };

        IActionResult result = await _controller.SendToPrinterAsync(jobId, request, CancellationToken.None);

        var badRequest = result.Should().BeOfType<BadRequestObjectResult>().Subject;
        badRequest.StatusCode.Should().Be(StatusCodes.Status400BadRequest);

        _printersMock.Verify(
            p => p.UploadGcodeAsync(
                It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _printersMock.Verify(
            p => p.UploadAndStartPrintAsync(
                It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<IProgress<UploadAndPrintStage>?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    [Trait("Category", "SlicePrintBridge")]
    public async Task SendToPrinter_SafetyValidatorAccepts_UploadsWithMachineEnvelopeFromPrinter()
    {
        Guid jobId = Guid.NewGuid();
        Guid printerId = Guid.NewGuid();
        Guid toolheadId = Guid.NewGuid();
        Artifact gcode = CreateArtifact(jobId, "gcode", "model.gcode");
        string filePath = CreateTempGcodeFile("model.gcode");

        SetupCompletedJobWithGcode(jobId, gcode);
        SetupArtifactPath(gcode, filePath);

        _printersMock
            .Setup(p => p.FindByIdWithIncludesAsync(printerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Printer
            {
                Id = printerId,
                Name = "Test Printer",
                MaxBedTemp = 120,
                MaxAcceleration = 5000,
                Toolheads = new List<Toolhead>
                {
                    new()
                    {
                        Id = toolheadId,
                        PrinterId = printerId,
                        IsPrimary = true,
                        NozzleMaxTemperature = 280,
                        HotendMaxTemperature = 300,
                        IsDirectDrive = true,
                    },
                },
            });

        _printersMock
            .Setup(p => p.UploadGcodeAsync(printerId, gcode.FileName, It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        GcodeSafetyRequest? capturedRequest = null;
        _safetyValidatorMock
            .Setup(v => v.Validate(It.IsAny<GcodeSafetyRequest>()))
            .Callback<GcodeSafetyRequest>(r => capturedRequest = r)
            .Returns(GcodeSafetyResult<GcodeSafetyReport>.Success(new GcodeSafetyReport(
                GcodeSafetyCheckpoint.BeforeSendToPrinter, "test-sha256", 3, DateTime.UtcNow)));

        var request = new SendToPrinterRequest { PrinterId = printerId, StartPrint = false };

        IActionResult result = await _controller.SendToPrinterAsync(jobId, request, CancellationToken.None);

        _ = result.Should().BeOfType<OkObjectResult>();

        capturedRequest.Should().NotBeNull();
        capturedRequest!.Checkpoint.Should().Be(GcodeSafetyCheckpoint.BeforeSendToPrinter);
        capturedRequest.AllowedCommands.Should().BeNull();
        capturedRequest.Limits.Machine.MaxBedTemperatureCelsius.Should().Be(120);
        capturedRequest.Limits.Machine.MaxAcceleration.Should().Be(5000);

        // Proves BuildSafetyLimits actually sources the primary toolhead's ceilings (via
        // FindByIdWithIncludesAsync), not just the printer-level machine envelope above.
        capturedRequest.Limits.Toolhead.NozzleMaxTemperatureCelsius.Should().Be(280);
        capturedRequest.Limits.Toolhead.HotendMaxTemperatureCelsius.Should().Be(300);
        capturedRequest.Limits.Toolhead.IsDirectDrive.Should().BeTrue();
    }

    [Fact]
    [Trait("Category", "SlicePrintBridge")]
    public async Task SendToPrinter_ValidPrintablePolygonAndExcludedRegions_UploadsWithParsedGeometry()
    {
        Guid jobId = Guid.NewGuid();
        Guid printerId = Guid.NewGuid();
        Artifact gcode = CreateArtifact(jobId, "gcode", "model.gcode");
        string filePath = CreateTempGcodeFile("model.gcode");

        SetupCompletedJobWithGcode(jobId, gcode);
        SetupArtifactPath(gcode, filePath);

        _printersMock
            .Setup(p => p.FindByIdWithIncludesAsync(printerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Printer
            {
                Id = printerId,
                Name = "Test Printer",
                PrintablePolygonJson = "[{\"x\":0,\"y\":0},{\"x\":200,\"y\":0},{\"x\":200,\"y\":200},{\"x\":0,\"y\":200}]",
                ExcludedRegionsJson = "[{\"name\":\"clip\",\"polygon\":[{\"x\":0,\"y\":0},{\"x\":10,\"y\":0},{\"x\":10,\"y\":10}]}]",
            });

        _printersMock
            .Setup(p => p.UploadGcodeAsync(printerId, gcode.FileName, It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        GcodeSafetyRequest? capturedRequest = null;
        _safetyValidatorMock
            .Setup(v => v.Validate(It.IsAny<GcodeSafetyRequest>()))
            .Callback<GcodeSafetyRequest>(r => capturedRequest = r)
            .Returns(GcodeSafetyResult<GcodeSafetyReport>.Success(new GcodeSafetyReport(
                GcodeSafetyCheckpoint.BeforeSendToPrinter, "test-sha256", 3, DateTime.UtcNow)));

        var request = new SendToPrinterRequest { PrinterId = printerId, StartPrint = false };

        IActionResult result = await _controller.SendToPrinterAsync(jobId, request, CancellationToken.None);

        _ = result.Should().BeOfType<OkObjectResult>();
        capturedRequest.Should().NotBeNull();
        capturedRequest!.Limits.Bed.PrintablePolygon.Should().HaveCount(4);
        capturedRequest.Limits.Bed.ExcludedRegions.Should().ContainSingle();
        capturedRequest.Limits.Bed.ExcludedRegions[0].Name.Should().Be("clip");
        capturedRequest.Limits.Bed.ExcludedRegions[0].Polygon.Should().HaveCount(3);
    }

    [Fact]
    [Trait("Category", "SlicePrintBridge")]
    public async Task SendToPrinter_MalformedPrintablePolygonJson_Returns400AndDoesNotUpload()
    {
        Guid jobId = Guid.NewGuid();
        Guid printerId = Guid.NewGuid();
        Artifact gcode = CreateArtifact(jobId, "gcode", "model.gcode");
        string filePath = CreateTempGcodeFile("model.gcode");

        SetupCompletedJobWithGcode(jobId, gcode);
        SetupArtifactPath(gcode, filePath);

        _printersMock
            .Setup(p => p.FindByIdWithIncludesAsync(printerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Printer
            {
                Id = printerId,
                Name = "Test Printer",
                PrintablePolygonJson = "not-json",
            });

        var request = new SendToPrinterRequest { PrinterId = printerId, StartPrint = false };

        IActionResult result = await _controller.SendToPrinterAsync(jobId, request, CancellationToken.None);

        var badRequest = result.Should().BeOfType<BadRequestObjectResult>().Subject;
        badRequest.StatusCode.Should().Be(StatusCodes.Status400BadRequest);

        _safetyValidatorMock.Verify(v => v.Validate(It.IsAny<GcodeSafetyRequest>()), Times.Never);
        _printersMock.Verify(
            p => p.UploadGcodeAsync(
                It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    [Trait("Category", "SlicePrintBridge")]
    public async Task SendToPrinter_PrintablePolygonWithFewerThanThreePoints_Returns400AndDoesNotUpload()
    {
        Guid jobId = Guid.NewGuid();
        Guid printerId = Guid.NewGuid();
        Artifact gcode = CreateArtifact(jobId, "gcode", "model.gcode");
        string filePath = CreateTempGcodeFile("model.gcode");

        SetupCompletedJobWithGcode(jobId, gcode);
        SetupArtifactPath(gcode, filePath);

        _printersMock
            .Setup(p => p.FindByIdWithIncludesAsync(printerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Printer
            {
                Id = printerId,
                Name = "Test Printer",
                PrintablePolygonJson = "[{\"x\":0,\"y\":0},{\"x\":10,\"y\":0}]",
            });

        var request = new SendToPrinterRequest { PrinterId = printerId, StartPrint = false };

        IActionResult result = await _controller.SendToPrinterAsync(jobId, request, CancellationToken.None);

        var badRequest = result.Should().BeOfType<BadRequestObjectResult>().Subject;
        badRequest.StatusCode.Should().Be(StatusCodes.Status400BadRequest);

        _safetyValidatorMock.Verify(v => v.Validate(It.IsAny<GcodeSafetyRequest>()), Times.Never);
        _printersMock.Verify(
            p => p.UploadGcodeAsync(
                It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    [Trait("Category", "SlicePrintBridge")]
    public async Task SendToPrinter_ExcludedRegionWithFewerThanThreePoints_Returns400AndDoesNotUpload()
    {
        Guid jobId = Guid.NewGuid();
        Guid printerId = Guid.NewGuid();
        Artifact gcode = CreateArtifact(jobId, "gcode", "model.gcode");
        string filePath = CreateTempGcodeFile("model.gcode");

        SetupCompletedJobWithGcode(jobId, gcode);
        SetupArtifactPath(gcode, filePath);

        _printersMock
            .Setup(p => p.FindByIdWithIncludesAsync(printerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Printer
            {
                Id = printerId,
                Name = "Test Printer",
                ExcludedRegionsJson = "[{\"name\":\"clip\",\"polygon\":[{\"x\":0,\"y\":0},{\"x\":10,\"y\":0}]}]",
            });

        var request = new SendToPrinterRequest { PrinterId = printerId, StartPrint = false };

        IActionResult result = await _controller.SendToPrinterAsync(jobId, request, CancellationToken.None);

        var badRequest = result.Should().BeOfType<BadRequestObjectResult>().Subject;
        badRequest.StatusCode.Should().Be(StatusCodes.Status400BadRequest);

        _safetyValidatorMock.Verify(v => v.Validate(It.IsAny<GcodeSafetyRequest>()), Times.Never);
        _printersMock.Verify(
            p => p.UploadGcodeAsync(
                It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    [Trait("Category", "SlicePrintBridge")]
    public async Task SendToPrinter_ExcludedRegionWithNullPolygon_Returns400AndDoesNotUpload()
    {
        Guid jobId = Guid.NewGuid();
        Guid printerId = Guid.NewGuid();
        Artifact gcode = CreateArtifact(jobId, "gcode", "model.gcode");
        string filePath = CreateTempGcodeFile("model.gcode");

        SetupCompletedJobWithGcode(jobId, gcode);
        SetupArtifactPath(gcode, filePath);

        _printersMock
            .Setup(p => p.FindByIdWithIncludesAsync(printerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Printer
            {
                Id = printerId,
                Name = "Test Printer",
                // "polygon" is present (satisfies [JsonRequired]) but its value is a JSON null,
                // which JsonRequired alone does not reject. Must still fail closed rather than
                // throwing an unhandled NullReferenceException.
                ExcludedRegionsJson = "[{\"name\":\"clip\",\"polygon\":null}]",
            });

        var request = new SendToPrinterRequest { PrinterId = printerId, StartPrint = false };

        IActionResult result = await _controller.SendToPrinterAsync(jobId, request, CancellationToken.None);

        var badRequest = result.Should().BeOfType<BadRequestObjectResult>().Subject;
        badRequest.StatusCode.Should().Be(StatusCodes.Status400BadRequest);

        _safetyValidatorMock.Verify(v => v.Validate(It.IsAny<GcodeSafetyRequest>()), Times.Never);
        _printersMock.Verify(
            p => p.UploadGcodeAsync(
                It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    [Trait("Category", "SlicePrintBridge")]
    public async Task SendToPrinter_PrintablePolygonWithNonFiniteCoordinate_Returns400AndDoesNotUpload()
    {
        Guid jobId = Guid.NewGuid();
        Guid printerId = Guid.NewGuid();
        Artifact gcode = CreateArtifact(jobId, "gcode", "model.gcode");
        string filePath = CreateTempGcodeFile("model.gcode");

        SetupCompletedJobWithGcode(jobId, gcode);
        SetupArtifactPath(gcode, filePath);

        _printersMock
            .Setup(p => p.FindByIdWithIncludesAsync(printerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Printer
            {
                Id = printerId,
                Name = "Test Printer",
                // System.Text.Json accepts numeric literals like 1e309 as double.PositiveInfinity.
                // Casting that straight to decimal throws OverflowException; the parser must catch
                // this and fail closed (400) rather than let it escape as an unhandled 500.
                PrintablePolygonJson = "[{\"x\":1e309,\"y\":0},{\"x\":10,\"y\":0},{\"x\":10,\"y\":10}]",
            });

        var request = new SendToPrinterRequest { PrinterId = printerId, StartPrint = false };

        IActionResult result = await _controller.SendToPrinterAsync(jobId, request, CancellationToken.None);

        var badRequest = result.Should().BeOfType<BadRequestObjectResult>().Subject;
        badRequest.StatusCode.Should().Be(StatusCodes.Status400BadRequest);

        _safetyValidatorMock.Verify(v => v.Validate(It.IsAny<GcodeSafetyRequest>()), Times.Never);
        _printersMock.Verify(
            p => p.UploadGcodeAsync(
                It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    [Trait("Category", "SlicePrintBridge")]
    public async Task SendToPrinter_ExcludedRegionWithNonFiniteCoordinate_Returns400AndDoesNotUpload()
    {
        Guid jobId = Guid.NewGuid();
        Guid printerId = Guid.NewGuid();
        Artifact gcode = CreateArtifact(jobId, "gcode", "model.gcode");
        string filePath = CreateTempGcodeFile("model.gcode");

        SetupCompletedJobWithGcode(jobId, gcode);
        SetupArtifactPath(gcode, filePath);

        _printersMock
            .Setup(p => p.FindByIdWithIncludesAsync(printerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Printer
            {
                Id = printerId,
                Name = "Test Printer",
                ExcludedRegionsJson =
                    "[{\"name\":\"clip\",\"polygon\":[{\"x\":1e309,\"y\":0},{\"x\":10,\"y\":0},{\"x\":10,\"y\":10}]}]",
            });

        var request = new SendToPrinterRequest { PrinterId = printerId, StartPrint = false };

        IActionResult result = await _controller.SendToPrinterAsync(jobId, request, CancellationToken.None);

        var badRequest = result.Should().BeOfType<BadRequestObjectResult>().Subject;
        badRequest.StatusCode.Should().Be(StatusCodes.Status400BadRequest);

        _safetyValidatorMock.Verify(v => v.Validate(It.IsAny<GcodeSafetyRequest>()), Times.Never);
        _printersMock.Verify(
            p => p.UploadGcodeAsync(
                It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    [Trait("Category", "SlicePrintBridge")]
    public async Task SendToPrinter_GcodeWithLeadingBom_StripsBomBeforeValidationButUploadsRawBytes()
    {
        Guid jobId = Guid.NewGuid();
        Guid printerId = Guid.NewGuid();
        Artifact gcode = CreateArtifact(jobId, "gcode", "model.gcode");
        string filePath = Path.Join(_tempDir, "model.gcode");

        // Write the file WITH a leading UTF-8 BOM followed immediately by a command, so that if
        // the BOM were not stripped before validation, the interpreter would see "\uFEFFG28"
        // instead of "G28" as the first line.
        byte[] utf8Bom = [0xEF, 0xBB, 0xBF];
        byte[] body = System.Text.Encoding.UTF8.GetBytes("G28\nG1 X10 Y10 Z0.2 F1500\n");
        File.WriteAllBytes(filePath, [.. utf8Bom, .. body]);

        SetupCompletedJobWithGcode(jobId, gcode);
        SetupPrinterExists(printerId);
        SetupArtifactPath(gcode, filePath);

        string? capturedGcodeText = null;
        _safetyValidatorMock
            .Setup(v => v.Validate(It.IsAny<GcodeSafetyRequest>()))
            .Callback<GcodeSafetyRequest>(r => capturedGcodeText = r.Gcode)
            .Returns(GcodeSafetyResult<GcodeSafetyReport>.Success(new GcodeSafetyReport(
                GcodeSafetyCheckpoint.BeforeSendToPrinter, "test-sha256", 2, DateTime.UtcNow)));

        byte[]? capturedUploadBytes = null;
        _printersMock
            .Setup(p => p.UploadGcodeAsync(printerId, gcode.FileName, It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
            .Callback<Guid, string, Stream, CancellationToken>((_, _, stream, _) =>
            {
                using var buffer = new MemoryStream();
                stream.CopyTo(buffer);
                capturedUploadBytes = buffer.ToArray();
            })
            .ReturnsAsync(true);

        var request = new SendToPrinterRequest { PrinterId = printerId, StartPrint = false };

        IActionResult result = await _controller.SendToPrinterAsync(jobId, request, CancellationToken.None);

        _ = result.Should().BeOfType<OkObjectResult>();

        // The text handed to the safety validator must have the BOM stripped so command matching
        // (e.g. "G28" at position 0) is not corrupted by a leading U+FEFF character.
        capturedGcodeText.Should().NotBeNull();
        capturedGcodeText.Should().NotStartWith("\uFEFF");
        capturedGcodeText.Should().StartWith("G28");

        // The bytes actually uploaded must be byte-for-byte identical to the file on disk
        // (BOM included) - stripping is a validation-only concern and must never alter what is
        // sent to the printer.
        capturedUploadBytes.Should().NotBeNull();
        byte[] expectedBytes = await File.ReadAllBytesAsync(filePath);
        capturedUploadBytes.Should().Equal(expectedBytes);
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
        UserId = TestUserId,
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
            .Setup(p => p.FindByIdWithIncludesAsync(printerId, It.IsAny<CancellationToken>()))
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
        string path = Path.Join(_tempDir, fileName);
        File.WriteAllText(path, "; G-code test file\nG28\nG1 X10 Y10 Z0.2 F1500\n");
        return path;
    }
}
