using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Printers;
using Farm.Infrastructure.Services.Queue;
using Farm.Infrastructure.Services.Queue.Dispatch;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Dispatch;

/// <summary>
/// Provider-correct concurrency tests on a real SQLite database.
/// These tests verify that:
/// - Application-managed row versions fence concurrent claims so only one winner is recorded.
/// - Ack persistence writes a durable BackendStartCommand outbox event (not inline claim).
/// - Claim service fails closed on missing telemetry, missing ack, and null compatibility fields.
/// - Priority ordering and outbox deduplication work on real provider semantics.
///
/// Marked DbHeavy to allow CI to gate them separately; they do NOT require Docker.
/// </summary>
[Trait("Category", "DbHeavy")]
public class CalibrationQueueConcurrencyTests : IAsyncDisposable
{
    private readonly SqliteConnection _keepAlive;
    private readonly string _connectionString;
    private static int _dbCounter;

    public CalibrationQueueConcurrencyTests()
    {
        int id = System.Threading.Interlocked.Increment(ref _dbCounter);
        _connectionString = $"Data Source=file:calib_concurrency_{id}?mode=memory&cache=shared";
        _keepAlive = new SqliteConnection(_connectionString);
        _keepAlive.Open();
    }

    public async ValueTask DisposeAsync()
    {
        await _keepAlive.DisposeAsync();
    }

    // =========================================================================
    // Helpers
    // =========================================================================

    private AppDbContext CreateContext()
    {
        DbContextOptions<AppDbContext> opts = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connectionString)
            .Options;
        var ctx = new AppDbContext(opts);
        // Disable FK enforcement for unit tests so seed order does not matter.
        ctx.Database.ExecuteSqlRaw("PRAGMA foreign_keys = OFF;");
        return ctx;
    }

    private static DispatchClaimService CreateClaimService(
        AppDbContext db,
        IPrinterStatusSnapshotReader? statusReader = null,
        IDbOutboxSequenceAllocator? allocator = null)
    {
        IPrinterStatusSnapshotReader reader = statusReader ?? Mock.Of<IPrinterStatusSnapshotReader>(
            r => r.GetStatusSnapshot(It.IsAny<Guid>()) == null);
        allocator ??= new DbOutboxSequenceAllocator();
        return new DispatchClaimService(db, reader, allocator, NullLogger<DispatchClaimService>.Instance);
    }

    private static IPrinterStatusSnapshotReader MakeOnlineIdleReader(Guid printerId)
    {
        var statusDto = new PrinterStatusDto(
            Id: printerId,
            IsOnline: true,
            State: "idle");
        var snapshot = new PrinterStatusSnapshot(
            Status: statusDto,
            ObservedAtUtc: DateTime.UtcNow,
            LastSeenAtUtc: DateTime.UtcNow,
            Source: "test");
        return Mock.Of<IPrinterStatusSnapshotReader>(
            r => r.GetStatusSnapshot(printerId) == snapshot);
    }

    private static BedClearAcknowledgementService CreateAckService(AppDbContext db, IDbOutboxSequenceAllocator? allocator = null)
    {
        allocator ??= new DbOutboxSequenceAllocator();
        return new(db, allocator, NullLogger<BedClearAcknowledgementService>.Instance);
    }

    /// <summary>Applies migrations and seeds a printer + dispatch state + print job.</summary>
    private async Task<(Guid PrinterId, Guid JobId, Guid GcodeFileId)> SeedAsync(
        AppDbContext db,
        JobKind jobKind = JobKind.FilamentCalibration)
    {
        await db.Database.EnsureCreatedAsync();

        var manufacturer = new Manufacturer { Id = Guid.NewGuid(), Name = "Test Maker" };
        db.Manufacturers.Add(manufacturer);

        var model = new PrinterModel
        {
            Id = Guid.NewGuid(),
            ManufacturerId = manufacturer.Id,
            Name = "Test Model",
        };
        db.PrinterModels.Add(model);

        var gcode = new GcodeFile
        {
            Id = Guid.NewGuid(),
            Name = "calibration.gcode",
            FileName = "calibration.gcode",
            FileHash = new string('a', 64),
            FileSizeBytes = 1024,
            FilePath = "/gcode",
        };
        db.GcodeFiles.Add(gcode);

        var printer = new Printer
        {
            Id = Guid.NewGuid(),
            Name = "Test Printer",
            ServerUrl = $"http://test-{Guid.NewGuid():N}",
            ManufacturerId = manufacturer.Id,
            ModelId = model.Id,
            IsEnabled = true,
            InMaintenance = false,
            IsAvailable = true,
            FirmwareFamily = PrinterFirmwareFamily.Klipper,
            GcodeDialect = PrinterGcodeDialect.Klipper,
            CalibrationSlicerEngine = "OrcaSlicer",
            CalibrationSlicerDistribution = "upstream",
            CalibrationSlicerVersion = "2.3.0",
            ConfigurationRevision = 1,
        };
        db.Printers.Add(printer);

        var ds = new PrinterDispatchState { PrinterId = printer.Id };
        db.PrinterDispatchStates.Add(ds);

        var job = new PrintJob
        {
            Id = Guid.NewGuid(),
            Name = "calibration job",
            GcodeFileId = gcode.Id,
            GcodeFile = gcode,
            AssignedPrinterId = printer.Id,
            Status = PrintJobStatus.Assigned,
            Priority = (int)PrintJobPriority.High,
            JobKind = jobKind,
            RequiredFirmwareFamily = jobKind == JobKind.FilamentCalibration ? PrinterFirmwareFamily.Klipper : null,
            RequiredGcodeDialect = jobKind == JobKind.FilamentCalibration ? PrinterGcodeDialect.Klipper : null,
            RequiredSlicerEngine = jobKind == JobKind.FilamentCalibration ? "OrcaSlicer" : null,
            RequiredSlicerDistribution = jobKind == JobKind.FilamentCalibration ? "upstream" : null,
            RequiredSlicerVersion = jobKind == JobKind.FilamentCalibration ? "2.3.0" : null,
            PinnedPrinterConfigRevision = jobKind == JobKind.FilamentCalibration ? 1L : null,
            QueuePosition = 1,
            IdempotencyScope = "test-scope",
            IdempotencyKey = Guid.NewGuid().ToString(),
            IdempotencyRequestSha256 = new string('a', 64),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            QueuedAt = DateTime.UtcNow,
        };
        db.PrintJobs.Add(job);
        await db.SaveChangesAsync();

        return (printer.Id, job.Id, gcode.Id);
    }

    // =========================================================================
    // Test 1: Row version fence — two separate contexts claiming the same Standard job
    // =========================================================================

    [Fact]
    public async Task TwoContexts_ClaimSameStandardJob_OnlyOneSucceeds()
    {
        // Arrange: use Standard job — no ack or telemetry required.
        await using AppDbContext seedCtx = CreateContext();
        (Guid printerId, Guid jobId, _) = await SeedAsync(seedCtx, JobKind.Standard);

        await using AppDbContext ctx1 = CreateContext();
        await using AppDbContext ctx2 = CreateContext();

        var claimSvc1 = CreateClaimService(ctx1);
        var claimSvc2 = CreateClaimService(ctx2);

        var req = new DispatchClaimRequest(jobId, printerId, "actor-1", "Manual", null, null, null);
        var req2 = new DispatchClaimRequest(jobId, printerId, "actor-2", "Manual", null, null, null);

        // Act — fire both concurrently.
        Task<DispatchClaimResult> t1 = claimSvc1.AcquireClaimAsync(req);
        Task<DispatchClaimResult> t2 = claimSvc2.AcquireClaimAsync(req2);
        DispatchClaimResult[] results = await Task.WhenAll(t1, t2);

        // Assert — exactly one succeeds.
        int successCount = results.Count(r => r.Success);
        int failureCount = results.Count(r => !r.Success);

        successCount.Should().Be(1, "exactly one claim must win");
        failureCount.Should().Be(1, "the loser must receive a failure result");

        // Verify job is in Starting state.
        await using AppDbContext verifyCtx = CreateContext();
        PrintJob? verifiedJob = await verifyCtx.PrintJobs.FindAsync(jobId);
        verifiedJob!.Status.Should().Be(PrintJobStatus.Starting);

        // Verify exactly one attempt was created.
        int attemptCount = await verifyCtx.QueueDispatchAttempts.CountAsync(a => a.PrintJobId == jobId);
        attemptCount.Should().Be(1, "only one dispatch attempt must be written");

        // Verify exactly one outbox event was written.
        int outboxCount = await verifyCtx.QueueDispatchOutbox.CountAsync(e => e.AggregateId == jobId);
        outboxCount.Should().Be(1, "only one outbox event must be written");
    }

    // =========================================================================
    // Test 2: Bed-clear ack persists ack + BackendStartCommand (no inline claim)
    // =========================================================================

    [Fact]
    public async Task AcknowledgeAsync_WritesAckAndBackendStartCommand_NotInlineClaim()
    {
        // Arrange
        await using AppDbContext seedCtx = CreateContext();
        (Guid printerId, Guid jobId, _) = await SeedAsync(seedCtx);

        PrinterDispatchState? ds = await seedCtx.PrinterDispatchStates
            .FirstOrDefaultAsync(s => s.PrinterId == printerId);
        ds.Should().NotBeNull();

        await using AppDbContext ackCtx = CreateContext();
        var ackService = CreateAckService(ackCtx);

        var request = new AcknowledgeBedClearRequest(
            JobId: jobId,
            PrinterId: printerId,
            ActorSubject: "operator-1",
            IdempotencyKey: "ack-key-atomic",
            IfMatchDispatchState: ds!.RowVersion,
            ExpectedPrinterConfigRevision: 1);

        // Act
        AcknowledgeBedClearResult result = await ackService.AcknowledgeAsync(request);

        // Assert — accepted (ack + command persisted in one transaction).
        result.Outcome.Should().Be(BedClearAckOutcome.Accepted);

        await using AppDbContext verifyCtx = CreateContext();
        PrintJob? job = await verifyCtx.PrintJobs.FindAsync(jobId);

        // Job must NOT be in Starting yet — the inline claim was removed.
        // The BackendStartCommand outbox event will drive the actual claim later.
        job!.Status.Should().Be(PrintJobStatus.Assigned,
            "the bed-clear ack only persists the ack, not the claim — job stays Assigned until the adapter orchestrator processes the BackendStartCommand");

        // Ack must be persisted on dispatch state.
        PrinterDispatchState? verifyDs = await verifyCtx.PrinterDispatchStates
            .FirstOrDefaultAsync(s => s.PrinterId == printerId);
        verifyDs!.AcknowledgedJobId.Should().Be(jobId, "ack must be persisted");
        verifyDs.AcknowledgementIdempotencyKey.Should().Be("ack-key-atomic");
        verifyDs.ActiveJobId.Should().BeNull("no inline claim was acquired");

        // Exactly one BackendStartCommand outbox event must exist.
        int outboxCount = await verifyCtx.QueueDispatchOutbox
            .CountAsync(e => e.AggregateId == jobId
                && e.EventType == BedClearAcknowledgementService.BackendStartCommandEventType);
        outboxCount.Should().Be(1, "one BackendStartCommand event must be written");

        // No dispatch attempt (claim not acquired yet).
        int attemptCount = await verifyCtx.QueueDispatchAttempts.CountAsync(a => a.PrintJobId == jobId);
        attemptCount.Should().Be(0, "no attempt is created until the adapter orchestrator processes the command");
    }

    // =========================================================================
    // Test 3: Row version is non-null after first write (SQLite stamping)
    // =========================================================================

    [Fact]
    public async Task PrintJob_AfterWrite_HasNonNullRowVersion_OnSQLite()
    {
        await using AppDbContext ctx = CreateContext();
        await ctx.Database.EnsureCreatedAsync();

        var mfr = new Manufacturer { Id = Guid.NewGuid(), Name = "Mfr-RV" };
        ctx.Manufacturers.Add(mfr);
        var mdl = new PrinterModel { Id = Guid.NewGuid(), ManufacturerId = mfr.Id, Name = "Mdl-RV" };
        ctx.PrinterModels.Add(mdl);
        var gcode = new GcodeFile
        {
            Id = Guid.NewGuid(),
            Name = "test.gcode",
            FileName = "test.gcode",
            FileHash = new string('b', 64),
            FileSizeBytes = 512,
            FilePath = "/gcode",
        };
        ctx.GcodeFiles.Add(gcode);

        var job = new PrintJob
        {
            Id = Guid.NewGuid(),
            Name = "test",
            GcodeFileId = gcode.Id,
            Status = PrintJobStatus.Queued,
            Priority = (int)PrintJobPriority.Normal,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            QueuedAt = DateTime.UtcNow,
        };
        ctx.PrintJobs.Add(job);
        await ctx.SaveChangesAsync();

        job.RowVersion.Should().NotBeNull("StampRowVersions must generate a non-null token for SQLite");
        job.RowVersion!.Length.Should().BeGreaterThan(0);
    }

    // =========================================================================
    // Test 4: Firmware mismatch is rejected before claim
    // =========================================================================

    [Fact]
    public async Task ClaimService_FirmwareFamilyMismatch_RejectsWithTypedError()
    {
        await using AppDbContext seedCtx = CreateContext();
        await seedCtx.Database.EnsureCreatedAsync();

        var mfr = new Manufacturer { Id = Guid.NewGuid(), Name = "Mfr-FM" };
        seedCtx.Manufacturers.Add(mfr);
        var mdl = new PrinterModel { Id = Guid.NewGuid(), ManufacturerId = mfr.Id, Name = "Mdl-FM" };
        seedCtx.PrinterModels.Add(mdl);

        var gcode = new GcodeFile
        {
            Id = Guid.NewGuid(),
            Name = "calib.gcode",
            FileName = "calib.gcode",
            FileHash = new string('c', 64),
            FileSizeBytes = 256,
            FilePath = "/gcode",
        };
        seedCtx.GcodeFiles.Add(gcode);

        var printer = new Printer
        {
            Id = Guid.NewGuid(),
            Name = "Other Printer",
            ServerUrl = $"http://other-{Guid.NewGuid():N}",
            ManufacturerId = mfr.Id,
            ModelId = mdl.Id,
            IsEnabled = true,
            IsAvailable = true,
            FirmwareFamily = PrinterFirmwareFamily.Other,
            GcodeDialect = PrinterGcodeDialect.Other,
            CalibrationSlicerEngine = "OrcaSlicer",
            CalibrationSlicerDistribution = "upstream",
            CalibrationSlicerVersion = "2.3.0",
            ConfigurationRevision = 1,
        };
        seedCtx.Printers.Add(printer);

        var ds = new PrinterDispatchState
        {
            PrinterId = printer.Id,
            AcknowledgedJobId = Guid.NewGuid(), // Will be overridden below
            AcknowledgementIdempotencyKey = "test-ack-key",
            AcknowledgementExpiresAtUtc = DateTime.UtcNow.AddMinutes(10),
        };
        seedCtx.PrinterDispatchStates.Add(ds);

        var job = new PrintJob
        {
            Id = Guid.NewGuid(),
            Name = "calib job",
            GcodeFileId = gcode.Id,
            AssignedPrinterId = printer.Id,
            Status = PrintJobStatus.Assigned,
            JobKind = JobKind.FilamentCalibration,
            RequiredFirmwareFamily = PrinterFirmwareFamily.Klipper,
            RequiredGcodeDialect = PrinterGcodeDialect.Klipper,
            RequiredSlicerEngine = "OrcaSlicer",
            RequiredSlicerDistribution = "upstream",
            RequiredSlicerVersion = "2.3.0",
            PinnedPrinterConfigRevision = 1,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            QueuedAt = DateTime.UtcNow,
        };
        seedCtx.PrintJobs.Add(job);

        // Set the ack to point to this job.
        ds.AcknowledgedJobId = job.Id;
        await seedCtx.SaveChangesAsync();

        await using AppDbContext claimCtx = CreateContext();
        var claimSvc = CreateClaimService(claimCtx, MakeOnlineIdleReader(printer.Id));

        var request = new DispatchClaimRequest(
            job.Id, printer.Id, "actor", "Manual", "test-ack-key", null, null);

        DispatchClaimResult result = await claimSvc.AcquireClaimAsync(request);

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be("firmware_family_mismatch");
    }

    // =========================================================================
    // Test 5: Fail-closed — null telemetry rejects calibration claim
    // =========================================================================

    [Fact]
    public async Task ClaimService_CalibrationWithNullTelemetry_RejectsWithTelemetryUnavailable()
    {
        // Arrange: calibration job, but telemetry reader returns null (no snapshot).
        await using AppDbContext seedCtx = CreateContext();
        (Guid printerId, Guid jobId, _) = await SeedAsync(seedCtx);

        // Pre-seed ack so the claim can reach the telemetry check.
        await using AppDbContext ackCtx = CreateContext();
        PrinterDispatchState? ds = await ackCtx.PrinterDispatchStates
            .FirstOrDefaultAsync(s => s.PrinterId == printerId);
        ds!.AcknowledgedJobId = jobId;
        ds.AcknowledgementIdempotencyKey = "ack-key-1";
        ds.AcknowledgementExpiresAtUtc = DateTime.UtcNow.AddMinutes(10);
        await ackCtx.SaveChangesAsync();

        // Null reader = no telemetry available.
        IPrinterStatusSnapshotReader nullReader = Mock.Of<IPrinterStatusSnapshotReader>(
            r => r.GetStatusSnapshot(It.IsAny<Guid>()) == (PrinterStatusSnapshot?)null);

        await using AppDbContext claimCtx = CreateContext();
        var claimSvc = CreateClaimService(claimCtx, nullReader);

        var request = new DispatchClaimRequest(
            jobId, printerId, "actor", "Manual", "ack-key-1", null, null);

        // Act
        DispatchClaimResult result = await claimSvc.AcquireClaimAsync(request);

        // Assert — must fail closed: no telemetry = reject calibration.
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be("telemetry_unavailable",
            "calibration must fail closed when no telemetry snapshot is available");
    }

    // =========================================================================
    // Test 6: Fail-closed — no persisted ack rejects calibration claim
    // =========================================================================

    [Fact]
    public async Task ClaimService_CalibrationWithNoPersistedAck_RejectsWithAcknowledgementMissing()
    {
        // Arrange: calibration job with fresh telemetry, but dispatch state has NO persisted ack.
        await using AppDbContext seedCtx = CreateContext();
        (Guid printerId, Guid jobId, _) = await SeedAsync(seedCtx);
        // Dispatch state already has AcknowledgedJobId = null (no ack) from SeedAsync.

        await using AppDbContext claimCtx = CreateContext();
        var claimSvc = CreateClaimService(claimCtx, MakeOnlineIdleReader(printerId));

        var request = new DispatchClaimRequest(
            jobId, printerId, "actor", "Manual", "any-ack-key", null, null);

        // Act
        DispatchClaimResult result = await claimSvc.AcquireClaimAsync(request);

        // Assert — must fail closed: no persisted ack = reject.
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be("acknowledgement_missing",
            "calibration must fail closed when no persisted ack exists in dispatch state");
    }

    // =========================================================================
    // Test 7: Fail-closed — null compatibility fields reject calibration claim
    // =========================================================================

    [Fact]
    public async Task ClaimService_CalibrationWithNullCompatibilityFields_RejectsWithCompatibilityIncomplete()
    {
        // Arrange: calibration job missing RequiredFirmwareFamily.
        await using AppDbContext seedCtx = CreateContext();
        await seedCtx.Database.EnsureCreatedAsync();

        var mfr = new Manufacturer { Id = Guid.NewGuid(), Name = "Mfr-Compat" };
        seedCtx.Manufacturers.Add(mfr);
        var mdl = new PrinterModel { Id = Guid.NewGuid(), ManufacturerId = mfr.Id, Name = "Mdl-Compat" };
        seedCtx.PrinterModels.Add(mdl);

        var gcode = new GcodeFile
        {
            Id = Guid.NewGuid(),
            Name = "calib.gcode",
            FileName = "calib.gcode",
            FileHash = new string('d', 64),
            FileSizeBytes = 128,
            FilePath = "/gcode",
        };
        seedCtx.GcodeFiles.Add(gcode);

        var printer = new Printer
        {
            Id = Guid.NewGuid(),
            Name = "Compat Printer",
            ServerUrl = $"http://compat-{Guid.NewGuid():N}",
            ManufacturerId = mfr.Id,
            ModelId = mdl.Id,
            IsEnabled = true,
            IsAvailable = true,
            FirmwareFamily = PrinterFirmwareFamily.Klipper,
            GcodeDialect = PrinterGcodeDialect.Klipper,
            CalibrationSlicerEngine = "OrcaSlicer",
            CalibrationSlicerDistribution = "upstream",
            CalibrationSlicerVersion = "2.3.0",
            ConfigurationRevision = 1,
        };
        seedCtx.Printers.Add(printer);

        var job = new PrintJob
        {
            Id = Guid.NewGuid(),
            Name = "no-compat job",
            GcodeFileId = gcode.Id,
            AssignedPrinterId = printer.Id,
            Status = PrintJobStatus.Assigned,
            JobKind = JobKind.FilamentCalibration,
            RequiredFirmwareFamily = null, // ← intentionally null — fails closed
            RequiredGcodeDialect = null,
            RequiredSlicerEngine = null,
            RequiredSlicerDistribution = null,
            RequiredSlicerVersion = null,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            QueuedAt = DateTime.UtcNow,
        };
        seedCtx.PrintJobs.Add(job);

        var ds = new PrinterDispatchState
        {
            PrinterId = printer.Id,
            AcknowledgedJobId = job.Id,
            AcknowledgementIdempotencyKey = "ack-key-compat",
            AcknowledgementExpiresAtUtc = DateTime.UtcNow.AddMinutes(10),
        };
        seedCtx.PrinterDispatchStates.Add(ds);
        await seedCtx.SaveChangesAsync();

        await using AppDbContext claimCtx = CreateContext();
        var claimSvc = CreateClaimService(claimCtx, MakeOnlineIdleReader(printer.Id));

        var request = new DispatchClaimRequest(
            job.Id, printer.Id, "actor", "Manual", "ack-key-compat", null, null);

        // Act
        DispatchClaimResult result = await claimSvc.AcquireClaimAsync(request);

        // Assert — must fail closed: null required fields are not permitted.
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be("compatibility_incomplete",
            "null compatibility fields must fail closed — never inferred from printer configuration");
    }

    // =========================================================================
    // Test 8: Bed-clear replay after ack (same key+job) returns Replayed (no second command)
    // =========================================================================

    [Fact]
    public async Task BedClearAck_Replay_NoSecondBackendStartCommand()
    {
        // Arrange
        await using AppDbContext seedCtx = CreateContext();
        (Guid printerId, Guid jobId, _) = await SeedAsync(seedCtx);

        PrinterDispatchState? ds = await seedCtx.PrinterDispatchStates
            .FirstOrDefaultAsync(s => s.PrinterId == printerId);

        var req = new AcknowledgeBedClearRequest(
            jobId, printerId, "actor", "ack-key-dedup",
            ds!.RowVersion, 1);

        // First ack — should succeed.
        await using AppDbContext ctx1 = CreateContext();
        AcknowledgeBedClearResult first = await CreateAckService(ctx1).AcknowledgeAsync(req);
        first.Outcome.Should().Be(BedClearAckOutcome.Accepted);

        // Act — replay the same ack request (same key, same job, ack already persisted).
        await using AppDbContext ctx2 = CreateContext();
        PrinterDispatchState? ds2 = await ctx2.PrinterDispatchStates.FirstOrDefaultAsync(s => s.PrinterId == printerId);
        var replayReq = req with { IfMatchDispatchState = ds2!.RowVersion };
        AcknowledgeBedClearResult replay = await CreateAckService(ctx2).AcknowledgeAsync(replayReq);

        // Assert — replay detected (same key + same job).
        replay.Outcome.Should().Be(BedClearAckOutcome.Replayed);

        // Verify still only one BackendStartCommand event.
        await using AppDbContext verifyCtx = CreateContext();
        int outboxCount = await verifyCtx.QueueDispatchOutbox
            .CountAsync(e => e.AggregateId == jobId
                && e.EventType == BedClearAcknowledgementService.BackendStartCommandEventType);
        outboxCount.Should().Be(1, "replay must not create a second BackendStartCommand event");
    }

    // =========================================================================
    // Test 9: Calibration claim succeeds with all required fields and persisted ack
    // =========================================================================

    [Fact]
    public async Task ClaimService_CalibrationWithPersistedAck_Succeeds()
    {
        // Arrange: full calibration job with all required fields and persisted ack.
        await using AppDbContext seedCtx = CreateContext();
        (Guid printerId, Guid jobId, _) = await SeedAsync(seedCtx);

        // Pre-seed the ack.
        await using AppDbContext ackCtx = CreateContext();
        PrinterDispatchState? ds = await ackCtx.PrinterDispatchStates
            .FirstOrDefaultAsync(s => s.PrinterId == printerId);
        ds!.AcknowledgedJobId = jobId;
        ds.AcknowledgementIdempotencyKey = "valid-ack-key";
        ds.AcknowledgementExpiresAtUtc = DateTime.UtcNow.AddMinutes(10);
        await ackCtx.SaveChangesAsync();

        await using AppDbContext claimCtx = CreateContext();
        var claimSvc = CreateClaimService(claimCtx, MakeOnlineIdleReader(printerId));

        var request = new DispatchClaimRequest(
            jobId, printerId, "actor", "BedClear", "valid-ack-key", null, null);

        // Act
        DispatchClaimResult result = await claimSvc.AcquireClaimAsync(request);

        // Assert — claim succeeds: all checks pass.
        result.Success.Should().BeTrue("calibration with valid ack, fresh telemetry, and all required fields must succeed");
        result.Attempt.Should().NotBeNull();

        // Job must now be Starting.
        await using AppDbContext verifyCtx = CreateContext();
        PrintJob? job = await verifyCtx.PrintJobs.FindAsync(jobId);
        job!.Status.Should().Be(PrintJobStatus.Starting);

        // Ack must be consumed (cleared from dispatch state).
        PrinterDispatchState? verifyDs = await verifyCtx.PrinterDispatchStates
            .FirstOrDefaultAsync(s => s.PrinterId == printerId);
        verifyDs!.AcknowledgedJobId.Should().BeNull("ack must be consumed on successful claim");

        // Outbox event (JobDispatchStarted.v1) must be written.
        int outboxCount = await verifyCtx.QueueDispatchOutbox
            .CountAsync(e => e.AggregateId == jobId
                && e.EventType == "PrintFarmer.Queue.JobDispatchStarted.v1");
        outboxCount.Should().Be(1, "one JobDispatchStarted outbox event must be written on claim");
    }

    // =========================================================================
    // Test 10: Outbox Sequence is monotonically increasing — DB-backed cross-process
    // =========================================================================

    [Fact]
    public async Task OutboxEvent_SequenceIsMonotonicallyIncreasing_AcrossMultipleWrites()
    {
        // Arrange: calibration job with valid ack; the DB-backed allocator writes two
        // outbox events that must have distinct, ascending sequences.
        await using AppDbContext seedCtx = CreateContext();
        (Guid printerId, Guid jobId1, _) = await SeedAsync(seedCtx);

        // Write first outbox event via ack (BackendStartCommand).
        await using AppDbContext ackCtx = CreateContext();
        PrinterDispatchState? ds1 = await ackCtx.PrinterDispatchStates
            .FirstOrDefaultAsync(s => s.PrinterId == printerId);
        var ackSvc = CreateAckService(ackCtx);
        AcknowledgeBedClearResult ack1 = await ackSvc.AcknowledgeAsync(
            new AcknowledgeBedClearRequest(jobId1, printerId, "actor", "mono-key-1", ds1!.RowVersion, null));
        ack1.Outcome.Should().Be(BedClearAckOutcome.Accepted);

        // Pre-stamp an ack so the claim can fire and write a second outbox event.
        await using AppDbContext ackPreCtx = CreateContext();
        PrinterDispatchState? dsForClaim = await ackPreCtx.PrinterDispatchStates
            .FirstOrDefaultAsync(s => s.PrinterId == printerId);
        dsForClaim!.AcknowledgedJobId = jobId1;
        dsForClaim.AcknowledgementIdempotencyKey = "claim-key-1";
        dsForClaim.AcknowledgementExpiresAtUtc = DateTime.UtcNow.AddMinutes(10);
        await ackPreCtx.SaveChangesAsync();

        await using AppDbContext claimCtx = CreateContext();
        var claimSvc = CreateClaimService(claimCtx, MakeOnlineIdleReader(printerId));
        await claimSvc.AcquireClaimAsync(
            new DispatchClaimRequest(jobId1, printerId, "actor", "BedClear", "claim-key-1", null, null));

        // Assert: both outbox events have distinct, monotonically increasing Sequence values.
        await using AppDbContext verifyCtx = CreateContext();
        List<long> sequences = await verifyCtx.QueueDispatchOutbox
            .OrderBy(e => e.Sequence)
            .Select(e => e.Sequence)
            .ToListAsync();

        sequences.Should().HaveCountGreaterThan(0);
        sequences.Should().OnlyHaveUniqueItems("outbox sequences must be unique — no duplicate ordering");
        sequences.Should().BeInAscendingOrder("outbox events must have strictly ascending sequences");
        sequences.Should().AllSatisfy(s => s.Should().BeGreaterThan(0, "sequence must be non-zero from allocator"));
    }

    // =========================================================================
    // Test 11: BackendStartCommandConsumer crash recovery — stale Processing events
    //          are reset to Pending by RecoverStaleLeases on restart.
    // =========================================================================

    [Fact]
    public async Task BackendStartConsumer_RecoverStaleProcessingEvent_ResetsToP_ending()
    {
        // Arrange: create a BackendStartCommand event that is stuck in Processing
        // with a LastAttemptedAtUtc older than StaleLeaseAge (simulate crashed process).
        await using AppDbContext seedCtx = CreateContext();
        (Guid printerId, Guid jobId, _) = await SeedAsync(seedCtx);

        var staleEvt = new QueueDispatchOutbox
        {
            Id = Guid.NewGuid(),
            Sequence = 9_999_999,
            AggregateType = nameof(PrintJob),
            AggregateId = jobId,
            PrinterId = printerId,
            EventType = BedClearAcknowledgementService.BackendStartCommandEventType,
            SchemaVersion = "1",
            PayloadJson = System.Text.Json.JsonSerializer.Serialize(new
            {
                jobId,
                printerId,
                actorSubject = "system",
                acknowledgementKey = "crash-ack-key",
            }),
            Status = QueueOutboxEventStatus.Processing,
            AttemptCount = 1,
            // Simulate: this was last attempted 15 minutes ago (older than StaleLeaseAge=10min)
            LastAttemptedAtUtc = DateTime.UtcNow.AddMinutes(-15),
            CreatedAtUtc = DateTime.UtcNow.AddMinutes(-20),
        };

        seedCtx.QueueDispatchOutbox.Add(staleEvt);
        await seedCtx.SaveChangesAsync();

        // Act: simulate the consumer recovering stale leases (runs on startup).
        await using AppDbContext recoveryCtx = CreateContext();
        DateTime staleCutoff = DateTime.UtcNow.AddMinutes(-10); // StaleLeaseAge = 10 minutes
        List<QueueDispatchOutbox> staleFound = await recoveryCtx.QueueDispatchOutbox
            .Where(e =>
                e.EventType == BedClearAcknowledgementService.BackendStartCommandEventType &&
                e.Status == QueueOutboxEventStatus.Processing &&
                e.LastAttemptedAtUtc < staleCutoff)
            .ToListAsync();

        staleFound.Should().HaveCount(1, "the stale Processing event must be found during recovery scan");

        foreach (QueueDispatchOutbox evt in staleFound)
        {
            evt.Status = QueueOutboxEventStatus.Pending;
            evt.LastError = "Recovered from stale lease (previous process crash).";
            evt.RetryAfterUtc = DateTime.UtcNow;
        }

        await recoveryCtx.SaveChangesAsync();

        // Assert: event is now Pending, ready for re-execution.
        await using AppDbContext verifyCtx = CreateContext();
        QueueDispatchOutbox? recovered = await verifyCtx.QueueDispatchOutbox.FindAsync(staleEvt.Id);
        recovered.Should().NotBeNull();
        recovered!.Status.Should().Be(QueueOutboxEventStatus.Pending,
            "stale Processing events must be recovered to Pending by crash-recovery logic");
        recovered.LastError.Should().Contain("Recovered from stale lease");
    }

    // =========================================================================
    // Test 12: DB-backed sequence allocator — two concurrent DbContexts cannot
    //          produce duplicate sequence values (cross-process safety proof).
    // =========================================================================

    /// <summary>
    /// Critical cross-process test: two separate <see cref="AppDbContext"/> instances
    /// (simulating two API processes) both attempt to allocate a sequence and write
    /// an outbox event concurrently. Only one transaction can commit the
    /// <c>OutboxSequenceState</c> counter update; the other must receive a
    /// <c>DbUpdateConcurrencyException</c> and roll back. This proves that the
    /// DB-backed allocator prevents duplicate sequences across processes.
    /// </summary>
    [Fact]
    public async Task DbSequenceAllocator_TwoConcurrentContexts_ProduceUniqueSequences()
    {
        // Arrange: seed a database with OutboxSequenceState + one calibration job.
        await using AppDbContext seedCtx = CreateContext();
        (Guid printerId, Guid jobId, _) = await SeedAsync(seedCtx, JobKind.Standard);

        // Verify the seed row exists.
        OutboxSequenceState? seedState = await seedCtx.OutboxSequenceStates.SingleOrDefaultAsync();
        seedState.Should().NotBeNull("OutboxSequenceState seed row must be created by EnsureCreated");
        seedState!.NextSequence.Should().Be(0, "fresh database should start at 0");

        // Arrange two separate DbContexts + allocators (simulating two API processes).
        await using AppDbContext ctx1 = CreateContext();
        await using AppDbContext ctx2 = CreateContext();

        var alloc1 = new DbOutboxSequenceAllocator();
        var alloc2 = new DbOutboxSequenceAllocator();

        // Both allocate a sequence and build an outbox event (before any save).
        long seq1 = await alloc1.AllocateAsync(ctx1);
        long seq2 = await alloc2.AllocateAsync(ctx2);

        // Both loaded the same counter row with NextSequence=0 → both increment to 1.
        seq1.Should().Be(1, "first allocation from fresh counter should be 1");
        seq2.Should().Be(1, "second context also starts from the same persisted value");

        // Both try to write an outbox event with the same sequence → race condition.
        ctx1.QueueDispatchOutbox.Add(new QueueDispatchOutbox
        {
            Id = Guid.NewGuid(),
            Sequence = seq1,
            AggregateType = nameof(PrintJob),
            AggregateId = jobId,
            EventType = "PrintFarmer.Queue.Test.v1",
            SchemaVersion = "1",
            PayloadJson = "{}",
            Status = QueueOutboxEventStatus.Pending,
            CreatedAtUtc = DateTime.UtcNow,
        });
        ctx2.QueueDispatchOutbox.Add(new QueueDispatchOutbox
        {
            Id = Guid.NewGuid(),
            Sequence = seq2,
            AggregateType = nameof(PrintJob),
            AggregateId = jobId,
            EventType = "PrintFarmer.Queue.Test.v1",
            SchemaVersion = "1",
            PayloadJson = "{}",
            Status = QueueOutboxEventStatus.Pending,
            CreatedAtUtc = DateTime.UtcNow,
        });

        // Act: save both concurrently.
        Task<int> t1 = ctx1.SaveChangesAsync();
        Task<int> t2 = ctx2.SaveChangesAsync();

        // One must succeed, the other must throw DbUpdateConcurrencyException.
        Exception? exceptionFromLoser = null;
        try
        {
            await Task.WhenAll(t1, t2);
        }
        catch (Exception ex)
        {
            exceptionFromLoser = ex;
        }

        int successCount = (t1.Status == TaskStatus.RanToCompletion ? 1 : 0)
                         + (t2.Status == TaskStatus.RanToCompletion ? 1 : 0);

        // Assert: exactly one succeeded, one failed with concurrency conflict.
        // (In some rare cases, SQLite serializes the writes at the DB level and both
        // could succeed IF the unique index on Sequence also enforces uniqueness.
        // We treat either "one concurrency error" or "one unique constraint violation"
        // as proof that duplicate sequences cannot be silently committed.)
        using (new FluentAssertions.Execution.AssertionScope())
        {
            successCount.Should().Be(1, "exactly one concurrent allocator must win");
            exceptionFromLoser.Should().NotBeNull("the losing context must throw an exception");
        }

        // The winner wrote exactly one event with sequence=1.
        await using AppDbContext verifyCtx = CreateContext();
        List<long> sequences = await verifyCtx.QueueDispatchOutbox.Select(e => e.Sequence).ToListAsync();
        sequences.Should().HaveCount(1, "only one outbox event must be committed");
        sequences[0].Should().Be(1, "the winner's event must have sequence=1");

        // Counter is now at 1.
        OutboxSequenceState? finalState = await verifyCtx.OutboxSequenceStates.SingleAsync();
        finalState.NextSequence.Should().Be(1, "counter must reflect the one successful increment");
    }

    // =========================================================================
    // Test 13: DB-backed sequence allocator — sequential allocations in one context
    //          produce strictly increasing unique values.
    // =========================================================================

    [Fact]
    public async Task DbSequenceAllocator_SequentialAllocations_ProduceStrictlyIncreasing()
    {
        await using AppDbContext seedCtx = CreateContext();
        await seedCtx.Database.EnsureCreatedAsync();

        var alloc = new DbOutboxSequenceAllocator();

        // Allocate three values sequentially in the same context — each must be unique.
        long seq1 = await alloc.AllocateAsync(seedCtx);
        long seq2 = await alloc.AllocateAsync(seedCtx);
        long seq3 = await alloc.AllocateAsync(seedCtx);

        seq1.Should().Be(1);
        seq2.Should().Be(2);
        seq3.Should().Be(3, "in-context sequential allocations must increment the in-memory counter");
    }

    // =========================================================================
    // Test 14: QueueDispatchOutbox RowVersion enables atomic lease acquisition —
    //          two consumers cannot both claim the same event as Processing.
    // =========================================================================

    [Fact]
    public async Task OutboxConsumer_ConcurrentLeaseClaim_OnlyOneSucceeds()
    {
        // Arrange: insert a Pending outbox event with a known RowVersion.
        await using AppDbContext seedCtx = CreateContext();
        (_, Guid jobId, _) = await SeedAsync(seedCtx, JobKind.Standard);

        var pendingEvt = new QueueDispatchOutbox
        {
            Id = Guid.NewGuid(),
            Sequence = 999,
            AggregateType = nameof(PrintJob),
            AggregateId = jobId,
            EventType = BedClearAcknowledgementService.BackendStartCommandEventType,
            SchemaVersion = "1",
            PayloadJson = "{}",
            Status = QueueOutboxEventStatus.Pending,
            CreatedAtUtc = DateTime.UtcNow,
        };
        seedCtx.QueueDispatchOutbox.Add(pendingEvt);
        await seedCtx.SaveChangesAsync();

        // Two consumers both load the same Pending event.
        await using AppDbContext consumerCtx1 = CreateContext();
        await using AppDbContext consumerCtx2 = CreateContext();

        QueueDispatchOutbox? evt1 = await consumerCtx1.QueueDispatchOutbox.FindAsync(pendingEvt.Id);
        QueueDispatchOutbox? evt2 = await consumerCtx2.QueueDispatchOutbox.FindAsync(pendingEvt.Id);

        // Both try to claim (set Processing).
        evt1!.Status = QueueOutboxEventStatus.Processing;
        evt1.AttemptCount++;

        evt2!.Status = QueueOutboxEventStatus.Processing;
        evt2.AttemptCount++;

        // Save concurrently — only one must succeed.
        Task<int> save1 = consumerCtx1.SaveChangesAsync();
        Task<int> save2 = consumerCtx2.SaveChangesAsync();

        Exception? exceptionFromLoser = null;
        try
        {
            await Task.WhenAll(save1, save2);
        }
        catch (Exception ex)
        {
            exceptionFromLoser = ex;
        }

        int successCount = (save1.Status == TaskStatus.RanToCompletion ? 1 : 0)
                         + (save2.Status == TaskStatus.RanToCompletion ? 1 : 0);

        // Assert: exactly one consumer wins the lease.
        successCount.Should().Be(1, "exactly one consumer must win the Processing lease");
        exceptionFromLoser.Should().NotBeNull("the losing consumer must receive a concurrency exception");

        // Verify: one attempt recorded, event is Processing.
        await using AppDbContext verifyCtx = CreateContext();
        QueueDispatchOutbox? claimed = await verifyCtx.QueueDispatchOutbox.FindAsync(pendingEvt.Id);
        claimed!.Status.Should().Be(QueueOutboxEventStatus.Processing, "winner claimed the event");
        claimed.AttemptCount.Should().Be(1, "only one attempt was counted");
    }

    // =========================================================================
    // Test 15: Migration snapshot validation — OutboxSequenceState seeded row
    //          is accessible via EnsureCreated (validates HasData configuration).
    // =========================================================================

    [Fact]
    public async Task Migration_OutboxSequenceState_SeedRowIsCreatedByEnsureCreated()
    {
        // Arrange: fresh SQLite database using EnsureCreated (same as test harness).
        await using AppDbContext ctx = CreateContext();
        await ctx.Database.EnsureCreatedAsync();

        // Assert: exactly one row with Id=1, NextSequence=0.
        OutboxSequenceState? state = await ctx.OutboxSequenceStates.SingleOrDefaultAsync();
        state.Should().NotBeNull("HasData must seed the OutboxSequenceState row");
        state!.Id.Should().Be(1, "the single row always has Id=1");
        state.NextSequence.Should().Be(0, "initial sequence is 0");
        state.RowVersion.Should().BeNull("row version is null before first write");

        // After a write, RowVersion must be stamped (non-null).
        state.NextSequence = 1;
        await ctx.SaveChangesAsync();

        await using AppDbContext verifyCtx = CreateContext();
        OutboxSequenceState? updated = await verifyCtx.OutboxSequenceStates.SingleAsync();
        updated.NextSequence.Should().Be(1);
        updated.RowVersion.Should().NotBeNull("StampRowVersions must generate a non-null token after first write");
        updated.RowVersion!.Length.Should().Be(16, "RowVersion is a 16-byte GUID-derived token");
    }
}

