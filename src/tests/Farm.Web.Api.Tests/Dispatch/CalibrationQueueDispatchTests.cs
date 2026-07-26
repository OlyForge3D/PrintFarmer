using System.Security.Claims;
using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Security;
using Farm.Infrastructure.Services.Printers;
using Farm.Infrastructure.Services.Queue;
using Farm.Web.Api.Controllers;
using Farm.Web.Api.Controllers.Requests;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
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
        byte[] etag = [0x01, 0x02];
        var request = new AcknowledgeBedClearRequestDto { PrinterId = printerId, IdempotencyKey = "key-new" };

        _bedClearSvc
            .Setup(s => s.AcknowledgeAsync(It.IsAny<AcknowledgeBedClearRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AcknowledgeBedClearResult(BedClearAckOutcome.Accepted, etag, etag, null));

        SetIfMatchHeader("AAAA");

        // Act
        IActionResult result = await _controller.AcknowledgeBedClearAndStartAsync(jobId, request, CancellationToken.None);

        // Assert
        var status = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status202Accepted, status.StatusCode);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task AcknowledgeBedClear_ExactReplay_Returns200()
    {
        // Arrange
        var jobId = Guid.NewGuid();
        byte[] etag = [0x03];
        var request = new AcknowledgeBedClearRequestDto { PrinterId = Guid.NewGuid(), IdempotencyKey = "key-replay" };

        _bedClearSvc
            .Setup(s => s.AcknowledgeAsync(It.IsAny<AcknowledgeBedClearRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AcknowledgeBedClearResult(BedClearAckOutcome.Replayed, etag, etag, null));

        SetIfMatchHeader("AAAA");

        // Act
        IActionResult result = await _controller.AcknowledgeBedClearAndStartAsync(jobId, request, CancellationToken.None);

        // Assert
        Assert.IsType<OkObjectResult>(result);
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
    }

    private void SetIdempotencyKeyHeader(string key)
    {
        _controller.ControllerContext.HttpContext.Request.Headers["Idempotency-Key"] = key;
    }

    /// <summary>Creates a BedClearAcknowledgementService backed by an in-memory context.</summary>
    private static BedClearAcknowledgementService CreateBedClearService()
    {
        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        var db = new AppDbContext(options);
        return new BedClearAcknowledgementService(db, Mock.Of<ILogger<BedClearAcknowledgementService>>());
    }
}
