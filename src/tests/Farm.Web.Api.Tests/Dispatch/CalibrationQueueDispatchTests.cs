using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Farm.Api.Services.PrintQueue;
using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Queue;
using Farm.Infrastructure.Security;
using Farm.Infrastructure.Services.FileManagement;
using Farm.Infrastructure.Services.Interfaces;
using Farm.Infrastructure.Services.Printers;
using Farm.Infrastructure.Services.Queue;
using Farm.Infrastructure.Services.Queue.Dispatch;
using Farm.Infrastructure.Services.SignalR;
using Farm.Infrastructure.Services.StorageManagement;
using Farm.Web.Api.Controllers;
using Farm.Web.Api.Controllers.Requests;
using Farm.Web.Api.Tests.Builders;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Dispatch;

/// <summary>
/// Tests for the calibration queue dispatch idempotency, dispatch-claim,
/// and bed-clear acknowledgement features introduced by issue #900.
/// </summary>
public class CalibrationQueueDispatchTests
{
    private readonly Mock<IBedClearAcknowledgementService> _bedClearSvc;
    private readonly JobQueueController _controller;
    private readonly Guid _actorId;

    public CalibrationQueueDispatchTests()
    {
        _bedClearSvc = new Mock<IBedClearAcknowledgementService>();

        _controller = new JobQueueController(
            Mock.Of<Farm.Infrastructure.Services.Queue.IJobQueueService>(),
            Mock.Of<Farm.Infrastructure.Services.Interfaces.IPrintJobManagementService>(),
            Mock.Of<IPrintJobCompletionService>(),
            Mock.Of<Farm.Infrastructure.Services.Queue.Dispatch.IJobDispatchService>(),
            Mock.Of<Farm.Infrastructure.Services.Queue.Dispatch.IBatchDispatchService>(),
            _bedClearSvc.Object,
            Mock.Of<Farm.Infrastructure.Services.Printers.IPrinterStatusCacheReader>(),
            Mock.Of<Farm.Infrastructure.Telemetry.IPrintFarmerTelemetryService>(),
            Mock.Of<Farm.Infrastructure.Services.PartsInventory.IPartHarvestService>(),
            Mock.Of<Farm.Infrastructure.Services.OperatorFeatures.IOperatorFeatureGate>(),
            Mock.Of<ILogger<JobQueueController>>());

        _actorId = Guid.NewGuid();
        SetControllerUser(_actorId);
    }

    // =========================================================================
    // Bed-clear acknowledgement — HTTP outcome tests
    // =========================================================================

    [Fact]
    [Trait("Category", "Unit")]
    public async Task AcknowledgeBedClear_WithoutIdempotencyKey_Returns428()
    {
        // Arrange
        var jobId = Guid.NewGuid();
        var request = new AcknowledgeBedClearRequestDto { PrinterId = Guid.NewGuid() };
        // No Idempotency-Key header set and no key in body.

        // Act
        IActionResult result = await _controller.AcknowledgeBedClearAndStartAsync(jobId, request, CancellationToken.None);

        // Assert
        var status = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status428PreconditionRequired, status.StatusCode);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task AcknowledgeBedClear_WithoutJobEtag_Returns428BeforeServiceCall()
    {
        var request = new AcknowledgeBedClearRequestDto
        {
            PrinterId = Guid.NewGuid(),
            IdempotencyKey = "job-etag-required",
        };
        _controller.ControllerContext.HttpContext.Request.Headers[
            "X-Dispatch-State-If-Match"] = "\"AAAA\"";

        IActionResult result = await _controller.AcknowledgeBedClearAndStartAsync(
            Guid.NewGuid(),
            request,
            CancellationToken.None);

        var status = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status428PreconditionRequired, status.StatusCode);
        _bedClearSvc.Verify(
            service => service.AcknowledgeAsync(
                It.IsAny<AcknowledgeBedClearRequest>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task AcknowledgeBedClear_WeakEtags_AreDecodedBeforeServiceCall()
    {
        byte[] expectedEtag = [0x01, 0x02];
        var request = new AcknowledgeBedClearRequestDto
        {
            PrinterId = Guid.NewGuid(),
            IdempotencyKey = "weak-etag",
        };
        AcknowledgeBedClearRequest? capturedRequest = null;
        _bedClearSvc
            .Setup(service => service.AcknowledgeAsync(
                It.IsAny<AcknowledgeBedClearRequest>(),
                It.IsAny<CancellationToken>()))
            .Callback<AcknowledgeBedClearRequest, CancellationToken>(
                (captured, _) => capturedRequest = captured)
            .ReturnsAsync(new AcknowledgeBedClearResult(
                BedClearAckOutcome.JobNotFound,
                null,
                null,
                "Not found"));
        _controller.ControllerContext.HttpContext.Request.Headers["If-Match"] = "W/\"AQI=\"";
        _controller.ControllerContext.HttpContext.Request.Headers[
            "X-Dispatch-State-If-Match"] = "W/\"AQI=\"";

        IActionResult result = await _controller.AcknowledgeBedClearAndStartAsync(
            Guid.NewGuid(),
            request,
            CancellationToken.None);

        Assert.IsType<NotFoundObjectResult>(result);
        Assert.NotNull(capturedRequest);
        Assert.Equal(expectedEtag, capturedRequest.IfMatchJob);
        Assert.Equal(expectedEtag, capturedRequest.IfMatchDispatchState);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task AcknowledgeBedClear_JobNotFound_Returns404()
    {
        // Arrange
        var jobId = Guid.NewGuid();
        var request = new AcknowledgeBedClearRequestDto { PrinterId = Guid.NewGuid(), IdempotencyKey = "key-1" };

        _bedClearSvc
            .Setup(s => s.AcknowledgeAsync(It.IsAny<AcknowledgeBedClearRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AcknowledgeBedClearResult(BedClearAckOutcome.JobNotFound, null, null, "Not found"));

        SetIfMatchHeader("AAAA");

        // Act
        IActionResult result = await _controller.AcknowledgeBedClearAndStartAsync(jobId, request, CancellationToken.None);

        // Assert
        Assert.IsType<NotFoundObjectResult>(result);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task AcknowledgeBedClear_Accepted_Returns202()
    {
        // Arrange
        var jobId = Guid.NewGuid();
        var printerId = Guid.NewGuid();
        Guid commandId = Guid.NewGuid();
        const string KeyHash =
            "ca74744a78571a723f2d7bcd1b080bf775e08a2dd9da280f52dacd041799a257";
        byte[] etag = [0x01, 0x02];
        var request = new AcknowledgeBedClearRequestDto { PrinterId = printerId, IdempotencyKey = "key-new" };

        _bedClearSvc
            .Setup(s => s.AcknowledgeAsync(It.IsAny<AcknowledgeBedClearRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AcknowledgeBedClearResult(
                BedClearAckOutcome.Accepted,
                etag,
                etag,
                null,
                commandId,
                KeyHash));

        SetIfMatchHeader("AAAA");

        // Act
        IActionResult result = await _controller.AcknowledgeBedClearAndStartAsync(jobId, request, CancellationToken.None);

        // Assert
        var status = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status202Accepted, status.StatusCode);
        string response = System.Text.Json.JsonSerializer.Serialize(status.Value);
        using System.Text.Json.JsonDocument document =
            System.Text.Json.JsonDocument.Parse(response);
        Assert.Equal(
            commandId,
            document.RootElement.GetProperty("bedClearCommandId").GetGuid());
        Assert.Equal(
            KeyHash,
            document.RootElement
                .GetProperty("bedClearIdempotencyKeySha256")
                .GetString());
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task AcknowledgeBedClear_ExactReplay_Returns200()
    {
        // Arrange
        var jobId = Guid.NewGuid();
        Guid commandId = Guid.NewGuid();
        const string KeyHash =
            "cab37eaebb1eb44420f820c759ae68bc4a6d6110c0485545c0b6fd9aec2ef357";
        byte[] etag = [0x03];
        var request = new AcknowledgeBedClearRequestDto { PrinterId = Guid.NewGuid(), IdempotencyKey = "key-replay" };

        _bedClearSvc
            .Setup(s => s.AcknowledgeAsync(It.IsAny<AcknowledgeBedClearRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AcknowledgeBedClearResult(
                BedClearAckOutcome.Replayed,
                etag,
                etag,
                null,
                commandId,
                KeyHash));

        SetIfMatchHeader("AAAA");

        // Act
        IActionResult result = await _controller.AcknowledgeBedClearAndStartAsync(jobId, request, CancellationToken.None);

        // Assert
        OkObjectResult ok = Assert.IsType<OkObjectResult>(result);
        string response = System.Text.Json.JsonSerializer.Serialize(ok.Value);
        using System.Text.Json.JsonDocument document =
            System.Text.Json.JsonDocument.Parse(response);
        Assert.Equal(
            commandId,
            document.RootElement.GetProperty("bedClearCommandId").GetGuid());
        Assert.Equal(
            KeyHash,
            document.RootElement
                .GetProperty("bedClearIdempotencyKeySha256")
                .GetString());
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task AcknowledgeBedClear_WrongJob_Returns409WrongJob()
    {
        // Arrange
        var jobId = Guid.NewGuid();
        var request = new AcknowledgeBedClearRequestDto { PrinterId = Guid.NewGuid(), IdempotencyKey = "key-wrong" };

        _bedClearSvc
            .Setup(s => s.AcknowledgeAsync(It.IsAny<AcknowledgeBedClearRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AcknowledgeBedClearResult(BedClearAckOutcome.WrongJob, null, null, "Wrong job"));

        SetIfMatchHeader("AAAA");

        // Act
        IActionResult result = await _controller.AcknowledgeBedClearAndStartAsync(jobId, request, CancellationToken.None);

        // Assert
        var conflict = Assert.IsType<ConflictObjectResult>(result);
        Assert.Contains("wrong_job", conflict.Value?.ToString());
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task AcknowledgeBedClear_PrinterBusy_Returns409PrinterBusy()
    {
        // Arrange
        var request = new AcknowledgeBedClearRequestDto { PrinterId = Guid.NewGuid(), IdempotencyKey = "key-busy" };
        _bedClearSvc
            .Setup(s => s.AcknowledgeAsync(It.IsAny<AcknowledgeBedClearRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AcknowledgeBedClearResult(BedClearAckOutcome.PrinterBusy, null, null, "Busy"));
        SetIfMatchHeader("AAAA");

        IActionResult result = await _controller.AcknowledgeBedClearAndStartAsync(Guid.NewGuid(), request, CancellationToken.None);

        var conflict = Assert.IsType<ConflictObjectResult>(result);
        Assert.Contains("printer_busy", conflict.Value?.ToString());
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task AcknowledgeBedClear_DispatchRevisionConflict_Returns412()
    {
        // Arrange
        var request = new AcknowledgeBedClearRequestDto { PrinterId = Guid.NewGuid(), IdempotencyKey = "key-conflict" };
        _bedClearSvc
            .Setup(s => s.AcknowledgeAsync(It.IsAny<AcknowledgeBedClearRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AcknowledgeBedClearResult(BedClearAckOutcome.DispatchRevisionConflict, null, null, "Conflict"));
        SetIfMatchHeader("AAAA");

        IActionResult result = await _controller.AcknowledgeBedClearAndStartAsync(Guid.NewGuid(), request, CancellationToken.None);

        var status = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status412PreconditionFailed, status.StatusCode);
        Assert.Contains("dispatch_revision_conflict", status.Value?.ToString());
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task AcknowledgeBedClear_CalibrationIncompatible_Returns422()
    {
        // Arrange
        var request = new AcknowledgeBedClearRequestDto { PrinterId = Guid.NewGuid(), IdempotencyKey = "key-incompatible" };
        _bedClearSvc
            .Setup(s => s.AcknowledgeAsync(It.IsAny<AcknowledgeBedClearRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AcknowledgeBedClearResult(
                BedClearAckOutcome.CalibrationJobIncompatible,
                null,
                null,
                "FirmwareFamilyMismatch"));
        SetIfMatchHeader("AAAA");

        IActionResult result = await _controller.AcknowledgeBedClearAndStartAsync(Guid.NewGuid(), request, CancellationToken.None);

        var unprocessable = Assert.IsType<UnprocessableEntityObjectResult>(result);
        Assert.Contains("calibration_job_incompatible", unprocessable.Value?.ToString());
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task AcknowledgeBedClear_PrinterOffline_Returns503()
    {
        // Arrange
        var request = new AcknowledgeBedClearRequestDto { PrinterId = Guid.NewGuid(), IdempotencyKey = "key-offline" };
        _bedClearSvc
            .Setup(s => s.AcknowledgeAsync(It.IsAny<AcknowledgeBedClearRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AcknowledgeBedClearResult(BedClearAckOutcome.PrinterOfflineOrStale, null, null, "Offline"));
        SetIfMatchHeader("AAAA");

        IActionResult result = await _controller.AcknowledgeBedClearAndStartAsync(Guid.NewGuid(), request, CancellationToken.None);

        var status = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, status.StatusCode);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task AcknowledgeBedClear_IdempotencyKeyFromHeader_TakesPrecedenceOverBody()
    {
        // Arrange
        var jobId = Guid.NewGuid();
        var request = new AcknowledgeBedClearRequestDto { PrinterId = Guid.NewGuid(), IdempotencyKey = "body-key" };

        AcknowledgeBedClearRequest? captured = null;
        _bedClearSvc
            .Setup(s => s.AcknowledgeAsync(It.IsAny<AcknowledgeBedClearRequest>(), It.IsAny<CancellationToken>()))
            .Callback<AcknowledgeBedClearRequest, CancellationToken>((r, _) => captured = r)
            .ReturnsAsync(new AcknowledgeBedClearResult(BedClearAckOutcome.Accepted, null, null, null));

        SetIdempotencyKeyHeader("header-key");
        SetIfMatchHeader("AAAA");

        // Act
        await _controller.AcknowledgeBedClearAndStartAsync(jobId, request, CancellationToken.None);

        // Assert: header key takes precedence over body key
        Assert.NotNull(captured);
        Assert.Equal("header-key", captured!.IdempotencyKey);
    }

    // =========================================================================
    // Domain entity tests
    // =========================================================================

    [Fact]
    [Trait("Category", "Unit")]
    public void PrintJob_CalibrationFields_AreNullableAndOptional()
    {
        // Arrange: Standard job without any calibration fields (backward-compatible).
        var job = new PrintJob
        {
            Id = Guid.NewGuid(),
            Name = "Standard Job",
            Status = PrintJobStatus.Queued,
        };

        // Assert: Standard job has no calibration fields set.
        Assert.Null(job.JobKind);
        Assert.Null(job.CalibrationProjectId);
        Assert.Null(job.CalibrationAttemptId);
        Assert.Null(job.IdempotencyScope);
        Assert.Null(job.IdempotencyKey);
        Assert.Null(job.IdempotencyRequestSha256);
        Assert.Null(job.RequiredFirmwareFamily);
        Assert.Null(job.RequiredGcodeDialect);
        Assert.Null(job.BlockedReasonCode);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void PrintJob_CalibrationJob_CanSetAllFields()
    {
        // Arrange
        var projectId = Guid.NewGuid();
        var attemptId = Guid.NewGuid();

        // Act
        var job = new PrintJob
        {
            Id = Guid.NewGuid(),
            Name = "Cal Job",
            Status = PrintJobStatus.Queued,
            JobKind = JobKind.FilamentCalibration,
            CalibrationProjectId = projectId,
            CalibrationAttemptId = attemptId,
            IdempotencyScope = $"calibration:{projectId}",
            IdempotencyKey = "idempotency-abc-123",
            IdempotencyRequestSha256 = new string('a', 64),
            RequiredFirmwareFamily = PrinterFirmwareFamily.Klipper,
            RequiredGcodeDialect = PrinterGcodeDialect.Klipper,
            RequiredSlicerEngine = "OrcaSlicer",
            RequiredSlicerDistribution = "upstream",
            SpecificationSha256 = new string('b', 64),
            PinnedPrinterConfigRevision = 42L,
        };

        // Assert
        Assert.Equal(JobKind.FilamentCalibration, job.JobKind);
        Assert.Equal(projectId, job.CalibrationProjectId);
        Assert.Equal(PrinterFirmwareFamily.Klipper, job.RequiredFirmwareFamily);
        Assert.Equal(42L, job.PinnedPrinterConfigRevision);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void PrinterDispatchState_AcknowledgementFields_AreNullable()
    {
        // Arrange: fresh dispatch state (no acknowledgement).
        var state = new PrinterDispatchState
        {
            PrinterId = Guid.NewGuid(),
        };

        // Assert
        Assert.Null(state.AcknowledgedJobId);
        Assert.Null(state.AcknowledgedAtUtc);
        Assert.Null(state.AcknowledgedBySubject);
        Assert.Null(state.AcknowledgementIdempotencyKey);
        Assert.Null(state.AcknowledgementExpiresAtUtc);
        Assert.Null(state.ActiveJobId);
        Assert.Null(state.ActiveDispatchAttemptId);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void QueueDispatchAttempt_Outcome_DefaultsToInProgress()
    {
        var attempt = new QueueDispatchAttempt
        {
            Id = Guid.NewGuid(),
            PrintJobId = Guid.NewGuid(),
            PrinterId = Guid.NewGuid(),
        };

        Assert.Equal(DispatchAttemptOutcome.InProgress, attempt.Outcome);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void QueueDispatchOutbox_Status_DefaultsToPending()
    {
        var outbox = new QueueDispatchOutbox
        {
            Id = Guid.NewGuid(),
            AggregateType = "PrintJob",
            AggregateId = Guid.NewGuid(),
        };

        Assert.Equal(QueueOutboxEventStatus.Pending, outbox.Status);
    }

    // =========================================================================
    // Priority ordering
    // =========================================================================

    [Fact]
    [Trait("Category", "Unit")]
    public void PrintJobPriority_Ordering_IsUrgentHighNormalLow()
    {
        // Semantic priority ordering: Urgent=3 > High=2 > Normal=1 > Low=0.
        Assert.True((int)PrintJobPriority.Urgent > (int)PrintJobPriority.High);
        Assert.True((int)PrintJobPriority.High > (int)PrintJobPriority.Normal);
        Assert.True((int)PrintJobPriority.Normal > (int)PrintJobPriority.Low);
    }

    // =========================================================================
    // JobBlockedReasonCode
    // =========================================================================

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData(JobBlockedReasonCode.FirmwareFamilyMismatch)]
    [InlineData(JobBlockedReasonCode.GcodeDialectMismatch)]
    [InlineData(JobBlockedReasonCode.SlicerTupleMismatch)]
    [InlineData(JobBlockedReasonCode.ContentHashMismatch)]
    [InlineData(JobBlockedReasonCode.PrinterConfigRevisionStale)]
    [InlineData(JobBlockedReasonCode.HardCompatibilityFailure)]
    [InlineData(JobBlockedReasonCode.CalibrationRecordInvalid)]
    [InlineData(JobBlockedReasonCode.FilamentCheckFailed)]
    [InlineData(JobBlockedReasonCode.MissingRequiredCapability)]
    public void PrintJob_BlockedReasonCode_CanBeSetAndRetrieved(JobBlockedReasonCode code)
    {
        // Arrange
        var job = new PrintJob
        {
            Id = Guid.NewGuid(),
            JobKind = JobKind.FilamentCalibration,
            BlockedReasonCode = code,
            BlockedReasonJson = $"{{\"reason\":\"{code}\"}}",
        };

        // Assert
        Assert.Equal(code, job.BlockedReasonCode);
        Assert.NotNull(job.BlockedReasonJson);
    }

    // =========================================================================
    // BedClearAcknowledgementService unit tests
    // =========================================================================

    [Fact]
    [Trait("Category", "Unit")]
    public async Task BedClearAcknowledgement_WithoutIdempotencyKey_ReturnsPreconditionRequired()
    {
        // Arrange: build an in-memory service with no database access needed for this path.
        var svc = CreateBedClearService();
        var request = new AcknowledgeBedClearRequest(
            JobId: Guid.NewGuid(),
            PrinterId: Guid.NewGuid(),
            ActorSubject: "user-1",
            IdempotencyKey: "",          // empty key
            IfMatchDispatchState: [0x01],
            ExpectedPrinterConfigRevision: null);

        // Act
        AcknowledgeBedClearResult result = await svc.AcknowledgeAsync(request);

        // Assert
        Assert.Equal(BedClearAckOutcome.PreconditionRequired, result.Outcome);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task AddJobToQueueAsync_CalibrationWithoutIdempotencyKey_ThrowsValidationException()
    {
        AppDbContext db = CreateDbContext();
        Mock<IQueueDataService> dataService = new();
        GcodeFile gcode = CreatePromotedCalibrationArtifact();
        Guid printerId = Guid.NewGuid();

        dataService
            .Setup(s => s.GetGcodeFileAsync(gcode.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(gcode);

        JobQueueService sut = new(
            Mock.Of<IQueueRepository>(),
            dataService.Object,
            Mock.Of<ILogger<JobQueueService>>(),
            db: db);

        QueuePrintJobDto request = CreateCalibrationQueueRequest(gcode.Id, printerId);
        request.IdempotencyKey = null;

        Func<Task> act = async () => await sut.AddJobToQueueAsync(request, Guid.NewGuid(), CancellationToken.None);

        ValidationException ex = await Assert.ThrowsAsync<ValidationException>(act);
        Assert.Contains("idempotency key", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task AddJobToQueueAsync_CalibrationWithoutAssignedPrinter_ThrowsValidationException()
    {
        AppDbContext db = CreateDbContext();
        Mock<IQueueDataService> dataService = new();
        GcodeFile gcode = CreatePromotedCalibrationArtifact();

        dataService
            .Setup(s => s.GetGcodeFileAsync(gcode.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(gcode);

        JobQueueService sut = new(
            Mock.Of<IQueueRepository>(),
            dataService.Object,
            Mock.Of<ILogger<JobQueueService>>(),
            db: db);

        QueuePrintJobDto request = CreateCalibrationQueueRequest(gcode.Id, null);

        Func<Task> act = async () => await sut.AddJobToQueueAsync(request, Guid.NewGuid(), CancellationToken.None);

        ValidationException ex = await Assert.ThrowsAsync<ValidationException>(act);
        Assert.Contains("assigned printer", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task DispatchClaimService_AcquireClaimAsync_ForCalibrationWithoutAcknowledgementKey_Fails()
    {
        AppDbContext db = CreateDbContext();
        Guid printerId = Guid.NewGuid();
        GcodeFile gcode = CreatePromotedCalibrationArtifact();
        gcode.Name = "dispatch.gcode";
        gcode.FileName = "dispatch.gcode";
        gcode.FileHash = new string('a', 64);
        gcode.ContentSha256 = new string('a', 64);
        PrintJob job = new()
        {
            Id = Guid.NewGuid(),
            Name = "Calibration Dispatch",
            GcodeFileId = gcode.Id,
            GcodeFile = gcode,
            AssignedPrinterId = printerId,
            JobKind = JobKind.FilamentCalibration,
            Status = PrintJobStatus.Assigned,
            Priority = (int)PrintJobPriority.Normal,
            Copies = 1,
            // All required compatibility fields set so the claim reaches the ack check.
            RequiredFirmwareFamily = PrinterFirmwareFamily.Klipper,
            RequiredGcodeDialect = PrinterGcodeDialect.Klipper,
            RequiredSlicerEngine = "OrcaSlicer",
            RequiredSlicerDistribution = "upstream",
            RequiredSlicerVersion = "2.3.0",
            PinnedPrinterConfigRevision = 1,
            // Lineage and hash fields — required by the authoritative claim policy.
            CalibrationConfigSnapshotId = Guid.NewGuid(),
            GcodeContentSha256 = new string('a', 64), // Matches gcode.ContentSha256
            SpecificationSha256 = gcode.SpecificationSha256,
            MachineProfileSha256 = gcode.MachineProfileSha256,
            ProcessProfileSha256 = gcode.ProcessProfileSha256,
            FilamentProfileSha256 = gcode.FilamentProfileSha256,
            CalibrationProjectId = gcode.CalibrationProjectId,
            CalibrationAttemptId = gcode.CalibrationAttemptId,
            CalibrationOrchestrationId = gcode.CalibrationOrchestrationId,
            SpoolmanSpoolId = 4242,
            RequiredMaterialType = "PLA",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            QueuedAt = DateTime.UtcNow,
        };

        db.GcodeFiles.Add(gcode);
        db.PrintJobs.Add(job);

        // Build printer directly with all required calibration properties.
        var claimPrinter = new Printer
        {
            Id = printerId,
            Name = "Claim Printer",
            ServerUrl = "http://claim-test",
            IsEnabled = true,
            IsAvailable = true,
            InMaintenance = false,
            FirmwareFamily = PrinterFirmwareFamily.Klipper,
            GcodeDialect = PrinterGcodeDialect.Klipper,
            CalibrationSlicerEngine = "OrcaSlicer",
            CalibrationSlicerDistribution = "upstream",
            CalibrationSlicerVersion = "2.3.0",
            ConfigurationRevision = 1,
            ManufacturerId = Guid.NewGuid(),
            ModelId = Guid.NewGuid(),
            CurrentSpoolId = 4242,
            CurrentMaterial = "PLA",
        };
        var claimToolhead = new Toolhead
        {
            Id = Guid.NewGuid(),
            PrinterId = printerId,
            Name = "Primary",
            Index = 0,
            IsPrimary = true,
            NozzleDiameter = 0.4,
        };
        claimPrinter.Toolheads.Add(claimToolhead);
        var claimSpool = new Spool
        {
            Id = Guid.NewGuid(),
            Material = "PLA",
            Sku = "PLA-TEST-SKU",
            LotNumber = "LOT-TEST",
            WeightGrams = 1000,
            InUse = true,
            AssignedPrinterId = printerId,
        };
        gcode.FileSizeBytes = 100;
        gcode.SourceModelSha256 = new string('8', 64);
        job.PinnedGcodeFileSizeBytes = gcode.FileSizeBytes;
        job.PinnedPrinterModelId = claimPrinter.ModelId;
        job.PinnedToolheadId = claimToolhead.Id;
        job.PinnedToolheadIndex = claimToolhead.Index;
        job.PinnedSpoolId = claimSpool.Id;
        job.PinnedFilamentSku = "PLA-TEST-SKU";
        job.PinnedFilamentLotNumber = "LOT-TEST";
        job.FilamentSnapshotSha256 = new string('7', 64);
        job.SourceModelSha256 = gcode.SourceModelSha256;
        job.CalibrationManifestSha256 = gcode.CalibrationManifestSha256;
        db.Printers.Add(claimPrinter);
        db.Spools.Add(claimSpool);
        db.PrinterDispatchStates.Add(new PrinterDispatchState { PrinterId = printerId });
        await db.SaveChangesAsync();

        // Provide a fresh online/idle telemetry snapshot so the claim reaches the ack check.
        var freshStatus = new PrinterStatusDto(Id: printerId, IsOnline: true, State: "idle");
        var freshSnapshot = new PrinterStatusSnapshot(
            Status: freshStatus,
            ObservedAtUtc: DateTime.UtcNow,
            LastSeenAtUtc: DateTime.UtcNow,
            Source: "test");
        var mockReader = Mock.Of<IPrinterStatusSnapshotReader>(r =>
            r.GetStatusSnapshot(printerId) == freshSnapshot);

        DispatchClaimService sut = CreateDispatchClaimService(db, mockReader);

        // No ack key provided — calibration must reject with acknowledgement_required.
        DispatchClaimResult result = await sut.AcquireClaimAsync(
            new DispatchClaimRequest(job.Id, printerId, "user-1", "Manual", null, null, null),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("acknowledgement_required", result.ErrorCode);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task BedClearAcknowledgement_AcknowledgeAsync_WritesAckAndBackendStartCommand()
    {
        // BedClearAcknowledgementService now writes the ack + BackendStartCommand outbox event.
        // The actual claim (Job.Status = Starting) is acquired by IDispatchClaimService when
        // the adapter orchestrator processes the BackendStartCommand.
        //
        // This test exercises generic ack-persistence mechanics, not calibration gating, so
        // the job below is Standard (see JobKind assignment): post-#1989/D3b, the calibration
        // compatibility gate in BedClearAcknowledgementService.AcknowledgeAsync now
        // unconditionally fails FilamentCalibration acks (see #1990). The gcode/project/attempt
        // fixture below still carries calibration lineage fields incidentally, which is harmless
        // for a Standard job.
        AppDbContext db = CreateDbContext();
        Guid printerId = Guid.NewGuid();
        Guid gcodeId = Guid.NewGuid();

        Guid calibrationProjectId = Guid.NewGuid();
        Guid calibrationAttemptId = Guid.NewGuid();
        Guid calibrationOrchestrationId = Guid.NewGuid();
        Guid calibrationSnapshotId = Guid.NewGuid();
        Guid sourceArtifactId = Guid.NewGuid();
        Guid sourceSliceJobId = Guid.NewGuid();

        var gcode = new GcodeFile
        {
            Id = gcodeId,
            Name = "calibration.gcode",
            FileName = "calibration.gcode",
            FileHash = new string('a', 64),
            FileSizeBytes = 1024,
            IsImmutable = true,
            PromotedAtUtc = DateTime.UtcNow.AddMinutes(-1),
            ContentSha256 = new string('a', 64),
            SourceModelSha256 = new string('8', 64),
            CalibrationProjectId = calibrationProjectId,
            CalibrationAttemptId = calibrationAttemptId,
            CalibrationOrchestrationId = calibrationOrchestrationId,
            SourceArtifactId = sourceArtifactId,
            SourceSliceJobId = sourceSliceJobId,
            CalibrationManifestSha256 = new string('9', 64),
            SpecificationSha256 = new string('b', 64),
            MachineProfileSha256 = new string('c', 64),
            ProcessProfileSha256 = new string('d', 64),
            FilamentProfileSha256 = new string('e', 64),
            SlicerEngineName = "OrcaSlicer",
            SlicerDistribution = "upstream",
            PinnedSlicerVersion = "2.3.0",
            SlicerContainerDigest = "sha256:test",
            ObjectDimensionX = 20,
            ObjectDimensionY = 20,
            ObjectDimensionZ = 20,
            EstimatedFilamentWeightG = 10,
        };
        db.GcodeFiles.Add(gcode);

        var printer = new Printer
        {
            Id = printerId,
            Name = "Ack Printer",
            ServerUrl = "http://ack-test",
            IsEnabled = true,
            IsAvailable = true,
            InMaintenance = false,
            FirmwareFamily = PrinterFirmwareFamily.Klipper,
            GcodeDialect = PrinterGcodeDialect.Klipper,
            CalibrationSlicerEngine = "OrcaSlicer",
            CalibrationSlicerDistribution = "upstream",
            CalibrationSlicerVersion = "2.3.0",
            ConfigurationRevision = 1,
            ManufacturerId = Guid.NewGuid(),
            ModelId = Guid.NewGuid(),
            CurrentSpoolId = 4242,
            CurrentMaterial = "PLA",
            MaxBuildVolumeX = 200,
            MaxBuildVolumeY = 200,
            MaxBuildVolumeZ = 200,
        };
        gcode.PrinterModelId = printer.ModelId;
        var toolhead = new Toolhead
        {
            Id = Guid.NewGuid(),
            PrinterId = printerId,
            Name = "Primary",
            Index = 0,
            IsPrimary = true,
            NozzleDiameter = 0.4,
            CurrentSpoolId = 4242,
            CurrentMaterial = "PLA",
        };
        printer.Toolheads.Add(toolhead);
        db.Printers.Add(printer);
        var spool = new Spool
        {
            Id = Guid.NewGuid(),
            Material = "PLA",
            Sku = "PLA-TEST-SKU",
            LotNumber = "LOT-TEST",
            WeightGrams = 1000,
            InUse = true,
            AssignedPrinterId = printerId,
        };
        db.Spools.Add(spool);
        db.CalibrationProjects.Add(new CalibrationProject
        {
            Id = calibrationProjectId,
            OwnerUserId = Guid.NewGuid(),
            Name = "Ack calibration",
            PrinterId = printerId,
            SelectedToolheadId = toolhead.Id,
            SelectedToolheadIndex = toolhead.Index,
            FilamentProvider = "local",
            FilamentProductId = "pla",
            FilamentProductName = "PLA",
            FilamentMaterial = "PLA",
            FilamentSku = "PLA-TEST-SKU",
            LocalSpoolId = spool.Id,
            FilamentSnapshotJson = """{"material":"PLA"}""",
        });
        db.CalibrationAttempts.Add(new CalibrationAttempt
        {
            Id = calibrationAttemptId,
            ProjectId = calibrationProjectId,
            SpecificationSha256 = new string('b', 64),
        });
        db.CalibrationOrchestrations.Add(new CalibrationOrchestration
        {
            Id = calibrationOrchestrationId,
            ProjectId = calibrationProjectId,
            AttemptId = calibrationAttemptId,
            SpecificationSha256 = new string('b', 64),
            SliceJobId = sourceSliceJobId,
            GcodeFileId = gcodeId,
        });

        PrintJob job = new()
        {
            Id = Guid.NewGuid(),
            Name = "Calibration Ack",
            GcodeFileId = gcodeId,
            AssignedPrinterId = printerId,
            JobKind = JobKind.Standard,
            Status = PrintJobStatus.Assigned,
            RequiredFirmwareFamily = PrinterFirmwareFamily.Klipper,
            RequiredGcodeDialect = PrinterGcodeDialect.Klipper,
            RequiredSlicerEngine = "OrcaSlicer",
            RequiredSlicerDistribution = "upstream",
            RequiredSlicerVersion = "2.3.0",
            RequiredSlicerContainerDigest = "sha256:test",
            PinnedPrinterConfigRevision = 1,
            GcodeContentSha256 = new string('a', 64),
            PinnedGcodeFileSizeBytes = 1024,
            SpoolmanSpoolId = 4242,
            RequiredMaterialType = "PLA",
            CalibrationProjectId = calibrationProjectId,
            CalibrationAttemptId = calibrationAttemptId,
            CalibrationConfigSnapshotId = calibrationSnapshotId,
            CalibrationOrchestrationId = calibrationOrchestrationId,
            SourceArtifactId = sourceArtifactId,
            SliceJobId = sourceSliceJobId,
            SpecificationSha256 = new string('b', 64),
            MachineProfileSha256 = new string('c', 64),
            ProcessProfileSha256 = new string('d', 64),
            FilamentProfileSha256 = new string('e', 64),
            PrinterConfigSnapshotSha256 = new string('6', 64),
            PinnedPrinterModelId = printer.ModelId,
            PinnedToolheadId = toolhead.Id,
            PinnedToolheadIndex = toolhead.Index,
            PinnedSpoolId = spool.Id,
            PinnedFilamentSku = "PLA-TEST-SKU",
            PinnedFilamentLotNumber = "LOT-TEST",
            FilamentSnapshotSha256 = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes("""{"material":"PLA"}""")))
                .ToLowerInvariant(),
            SourceModelSha256 = new string('8', 64),
            CalibrationManifestSha256 = new string('9', 64),
            RequiredNozzleDiameter = 0.4m,
            RequiredCapabilities = [],
            PinnedObjectDimensionX = gcode.ObjectDimensionX,
            PinnedObjectDimensionY = gcode.ObjectDimensionY,
            PinnedObjectDimensionZ = gcode.ObjectDimensionZ,
            EstimatedFilamentUsage = gcode.EstimatedFilamentWeightG,
            FilamentName = "PLA",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            QueuedAt = DateTime.UtcNow,
        };

        db.PrintJobs.Add(job);
        db.PrinterDispatchStates.Add(new PrinterDispatchState { PrinterId = printerId });
        await db.SaveChangesAsync();

        // Read the current RowVersion so the If-Match header matches.
        PrinterDispatchState dispatchState = await db.PrinterDispatchStates.AsNoTracking()
            .SingleAsync(s => s.PrinterId == printerId);

        BedClearAcknowledgementService sut = CreateBedClearService(
            db, DispatchTestDoubles.OnlineIdleReader(printerId));
        AcknowledgeBedClearRequest request = new(
            job.Id,
            printerId,
            "operator-1",
            "ack-key-1",
            dispatchState.RowVersion,
            ExpectedPrinterConfigRevision: 1,
            IfMatchJob: job.RowVersion);

        AcknowledgeBedClearResult result = await sut.AcknowledgeAsync(request, CancellationToken.None);

        // Accepted: ack + BackendStartCommand written atomically.
        Assert.Equal(BedClearAckOutcome.Accepted, result.Outcome);

        PrinterDispatchState persisted = await db.PrinterDispatchStates.SingleAsync(s => s.PrinterId == printerId);

        // Ack is persisted (not consumed — the claim consumes it when processing the BackendStartCommand).
        Assert.Equal(job.Id, persisted.AcknowledgedJobId);
        Assert.Equal("ack-key-1", persisted.AcknowledgementIdempotencyKey);

        // No inline claim — job stays Assigned, no ActiveJobId yet.
        Assert.Null(persisted.ActiveJobId);
        PrintJob updatedJob = await db.PrintJobs.SingleAsync(j => j.Id == job.Id);
        Assert.Equal(PrintJobStatus.Assigned, updatedJob.Status);
        Assert.Null(updatedJob.ActualStartTime);

        // BackendStartCommand outbox event must be written for the adapter orchestrator.
        bool hasBackendCmd = await db.QueueDispatchOutbox.AnyAsync(
            e => e.AggregateId == job.Id
                && e.EventType == BedClearAcknowledgementService.BackendStartCommandEventType);
        Assert.True(hasBackendCmd, "BackendStartCommand must be written to the outbox");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task PrintJobManagementService_RerunCalibrationJob_ThrowsInvalidOperation()
    {
        Mock<IPrintJobManagementRepository> repository = new();
        GcodeFile gcode = new() { Id = Guid.NewGuid(), Name = "rerun.gcode", FileName = "rerun.gcode" };
        PrintJob originalJob = new()
        {
            Id = Guid.NewGuid(),
            Name = "Original Calibration",
            GcodeFileId = gcode.Id,
            AssignedPrinterId = Guid.NewGuid(),
            JobKind = JobKind.FilamentCalibration,
            CalibrationProjectId = Guid.NewGuid(),
            CalibrationAttemptId = Guid.NewGuid(),
            IdempotencyKey = "old-key",
            RequiredFirmwareFamily = PrinterFirmwareFamily.Klipper,
            Status = PrintJobStatus.Completed,
            Priority = (int)PrintJobPriority.High,
            CreatedAt = DateTime.UtcNow.AddHours(-1),
            UpdatedAt = DateTime.UtcNow.AddHours(-1),
            QueuedAt = DateTime.UtcNow.AddHours(-1),
        };

        repository.Setup(r => r.GetByIdAsync(originalJob.Id, It.IsAny<CancellationToken>())).ReturnsAsync(originalJob);
        repository.Setup(r => r.GetGcodeFileAsync(gcode.Id, It.IsAny<CancellationToken>())).ReturnsAsync(gcode);

        PrintJobManagementService sut = new(
            repository.Object,
            Mock.Of<ILogger<PrintJobManagementService>>(),
            Mock.Of<IPrintersService>(),
            Mock.Of<IStoragePathService>(),
            Mock.Of<IHubContext<PrinterHub>>(),
            Mock.Of<IStoredFileOperationsService>(),
            Mock.Of<IPrinterStatusCacheReader>());

        // Calibration jobs cannot be rerun through the standard path — must use new calibration workflow.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.RerunJobAsync(originalJob.Id.ToString(), "user-1", CancellationToken.None));
    }

    // =========================================================================
    // Helpers
    // =========================================================================

    private void SetControllerUser(Guid userId)
    {
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
            new Claim(PrintFarmerPermissions.ClaimType, PrintFarmerPermissions.Queue.AcknowledgeBedClear),
            new Claim(PrintFarmerPermissions.ClaimType, PrintFarmerPermissions.Queue.Start),
        };
        var identity = new ClaimsIdentity(claims, "Test");
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) },
        };
    }

    private void SetIfMatchHeader(string base64)
    {
        _controller.ControllerContext.HttpContext.Request.Headers["If-Match"] = $"\"{base64}\"";
        _controller.ControllerContext.HttpContext.Request.Headers[
            "X-Dispatch-State-If-Match"] = $"\"{base64}\"";
    }

    private void SetIdempotencyKeyHeader(string key)
    {
        _controller.ControllerContext.HttpContext.Request.Headers["Idempotency-Key"] = key;
    }

    private static DispatchClaimService CreateDispatchClaimService(AppDbContext db, IPrinterStatusSnapshotReader reader)
    {
        // Use a mock allocator for unit tests — the allocator never needs DB access
        // when testing failure paths (validation returns before reaching allocation).
        Mock<IDbOutboxSequenceAllocator> allocMock = new();
        long seq = 0;
        allocMock.Setup(a => a.AllocateAsync(It.IsAny<AppDbContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => Interlocked.Increment(ref seq));
        return new DispatchClaimService(
            db,
            reader,
            allocMock.Object,
            Mock.Of<ILogger<DispatchClaimService>>(),
            DispatchTestDoubles.TelemetryFreshnessPolicy(),
            DispatchTestDoubles.ValidByteIntegrityVerifier());
    }

    /// <summary>Creates a BedClearAcknowledgementService backed by an in-memory context.</summary>
    private static BedClearAcknowledgementService CreateBedClearService(AppDbContext? db = null, IPrinterStatusSnapshotReader? statusReader = null)
    {
        db ??= CreateDbContext();
        statusReader ??= DispatchTestDoubles.OnlineIdleReader(Guid.Empty);
        // Use a mock allocator for unit tests that don't need real DB sequence generation.
        Mock<IDbOutboxSequenceAllocator> allocMock = new();
        long seq = 0;
        allocMock.Setup(a => a.AllocateAsync(It.IsAny<AppDbContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => Interlocked.Increment(ref seq));
        return new BedClearAcknowledgementService(
            db,
            allocMock.Object,
            statusReader,
            Mock.Of<ILogger<BedClearAcknowledgementService>>(),
            DispatchTestDoubles.TelemetryFreshnessPolicy(),
            DispatchTestDoubles.ValidByteIntegrityVerifier());
    }

    private static AppDbContext CreateDbContext()
    {
        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        return new AppDbContext(options);
    }

    /// <summary>
    /// Builds a promoted, immutable calibration artifact — the only kind of artifact the
    /// server will classify as a calibration job (issue #900, defect 3).
    /// </summary>
    private static GcodeFile CreatePromotedCalibrationArtifact() =>
        new()
        {
            Id = Guid.NewGuid(),
            Name = "calibration.gcode",
            FileName = "calibration.gcode",
            FileHash = new string('1', 64),
            IsImmutable = true,
            PromotedAtUtc = DateTime.UtcNow.AddMinutes(-1),
            ContentSha256 = new string('1', 64),
            CalibrationProjectId = Guid.NewGuid(),
            CalibrationAttemptId = Guid.NewGuid(),
            CalibrationOrchestrationId = Guid.NewGuid(),
            CalibrationManifestSha256 = new string('9', 64),
            SpecificationSha256 = new string('2', 64),
            MachineProfileSha256 = new string('3', 64),
            ProcessProfileSha256 = new string('4', 64),
            FilamentProfileSha256 = new string('5', 64),
            SlicerEngineName = "OrcaSlicer",
            SlicerDistribution = "upstream",
            PinnedSlicerVersion = "2.3.0",
            FirmwareFamily = nameof(PrinterFirmwareFamily.Klipper),
            GcodeDialect = nameof(PrinterGcodeDialect.Klipper),
        };

    private static QueuePrintJobDto CreateCalibrationQueueRequest(Guid gcodeFileId, Guid? printerId) =>
        new()
        {
            GcodeFileId = gcodeFileId,
            AssignedPrinterId = printerId,
            JobKind = JobKind.FilamentCalibration,
            IdempotencyKey = "calibration-key-1",
            IdempotencyScope = "scope-1",
            CalibrationProjectId = Guid.NewGuid(),
            CalibrationAttemptId = Guid.NewGuid(),
            CalibrationConfigSnapshotId = Guid.NewGuid(),
            CalibrationOrchestrationId = Guid.NewGuid(),
            SourceArtifactId = Guid.NewGuid(),
            GcodeContentSha256 = new string('1', 64),
            RequiredFirmwareFamily = PrinterFirmwareFamily.Klipper,
            RequiredGcodeDialect = PrinterGcodeDialect.Klipper,
            RequiredSlicerEngine = "OrcaSlicer",
            RequiredSlicerDistribution = "upstream",
            RequiredSlicerVersion = "2.3.0",
            RequiredSlicerContainerDigest = "sha256:test",
            SpecificationSha256 = new string('2', 64),
            MachineProfileSha256 = new string('3', 64),
            ProcessProfileSha256 = new string('4', 64),
            FilamentProfileSha256 = new string('5', 64),
            PrinterConfigSnapshotSha256 = new string('6', 64),
            PinnedPrinterConfigRevision = 7,
            Copies = 1,
            Priority = PrintJobPriority.Normal,
        };
}
