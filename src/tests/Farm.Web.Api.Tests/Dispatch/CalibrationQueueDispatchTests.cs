using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
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

    [Fact]
    [Trait("Category", "Unit")]
    public async Task AddJobToQueueAsync_CalibrationWithoutIdempotencyKey_ThrowsValidationException()
    {
        AppDbContext db = CreateDbContext();
        Mock<IQueueDataService> dataService = new();
        GcodeFile gcode = new() { Id = Guid.NewGuid(), Name = "calibration.gcode", FileName = "calibration.gcode" };
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
        GcodeFile gcode = new() { Id = Guid.NewGuid(), Name = "calibration.gcode", FileName = "calibration.gcode" };

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
        GcodeFile gcode = new() { Id = Guid.NewGuid(), Name = "dispatch.gcode", FileName = "dispatch.gcode" };
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
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            QueuedAt = DateTime.UtcNow,
        };

        db.GcodeFiles.Add(gcode);
        db.PrintJobs.Add(job);
        db.Printers.Add(new PrinterBuilder().WithId(printerId).WithName("Claim Printer").AsOnlineAndReady().Build());
        db.PrinterDispatchStates.Add(new PrinterDispatchState { PrinterId = printerId, RowVersion = [] });
        await db.SaveChangesAsync();

        DispatchClaimService sut = new(db, Mock.Of<IPrinterStatusSnapshotReader>(), Mock.Of<ILogger<DispatchClaimService>>());

        DispatchClaimResult result = await sut.AcquireClaimAsync(
            new DispatchClaimRequest(job.Id, printerId, "user-1", "Manual", null, null, null),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("acknowledgement_required", result.ErrorCode);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task BedClearAcknowledgement_AcknowledgeAsync_AtomicallyAcquiresClaim()
    {
        AppDbContext db = CreateDbContext();
        Guid printerId = Guid.NewGuid();
        Guid gcodeId = Guid.NewGuid();

        // Need a GcodeFile so the claim can verify it exists.
        db.GcodeFiles.Add(new GcodeFile
        {
            Id = gcodeId,
            Name = "calibration.gcode",
            FileName = "calibration.gcode",
        });

        PrintJob job = new()
        {
            Id = Guid.NewGuid(),
            Name = "Calibration Ack",
            GcodeFileId = gcodeId,
            AssignedPrinterId = printerId,
            JobKind = JobKind.FilamentCalibration,
            Status = PrintJobStatus.Assigned,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            QueuedAt = DateTime.UtcNow,
        };

        db.PrintJobs.Add(job);
        db.PrinterDispatchStates.Add(new PrinterDispatchState { PrinterId = printerId, RowVersion = [] });
        await db.SaveChangesAsync();

        // After SaveChangesAsync, StampRowVersions writes a non-null RowVersion to the dispatch state.
        // Read the current RowVersion so the If-Match header matches the stored value.
        PrinterDispatchState dispatchState = await db.PrinterDispatchStates.AsNoTracking()
            .SingleAsync(s => s.PrinterId == printerId);

        BedClearAcknowledgementService sut = new(db, Mock.Of<ILogger<BedClearAcknowledgementService>>());
        AcknowledgeBedClearRequest request = new(
            job.Id,
            printerId,
            "operator-1",
            "ack-key-1",
            dispatchState.RowVersion,  // use the stamped RowVersion, not the initial []
            null);

        AcknowledgeBedClearResult result = await sut.AcknowledgeAsync(request, CancellationToken.None);

        // New behavior: ack + claim are atomic — job is now Starting, ack is consumed.
        Assert.Equal(BedClearAckOutcome.Accepted, result.Outcome);

        PrinterDispatchState persisted = await db.PrinterDispatchStates.SingleAsync(s => s.PrinterId == printerId);
        // Ack was consumed atomically by the claim — should be null.
        Assert.Null(persisted.AcknowledgedJobId);
        Assert.Null(persisted.AcknowledgementIdempotencyKey);
        // Dispatch state should now track the active job.
        Assert.Equal(job.Id, persisted.ActiveJobId);

        PrintJob updatedJob = await db.PrintJobs.SingleAsync(j => j.Id == job.Id);
        Assert.Equal(PrintJobStatus.Starting, updatedJob.Status);
        Assert.NotNull(updatedJob.ActualStartTime);
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
    }

    private void SetIdempotencyKeyHeader(string key)
    {
        _controller.ControllerContext.HttpContext.Request.Headers["Idempotency-Key"] = key;
    }

    /// <summary>Creates a BedClearAcknowledgementService backed by an in-memory context.</summary>
    private static BedClearAcknowledgementService CreateBedClearService()
    {
        AppDbContext db = CreateDbContext();
        return new BedClearAcknowledgementService(db, Mock.Of<ILogger<BedClearAcknowledgementService>>());
    }

    private static AppDbContext CreateDbContext()
    {
        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        return new AppDbContext(options);
    }

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
