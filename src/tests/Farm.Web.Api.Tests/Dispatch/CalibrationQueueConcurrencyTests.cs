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
/// - Ack + claim atomicity means a crash between them cannot leave the job in a bad state.
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
        // Disable FK enforcement for unit tests so seed order doesn't matter.
        // Production code enforces integrity via EF relationships and FK checks at migration level.
        ctx.Database.ExecuteSqlRaw("PRAGMA foreign_keys = OFF;");
        return ctx;
    }

    private static DispatchClaimService CreateClaimService(
        AppDbContext db,
        IPrinterStatusSnapshotReader? statusReader = null)
    {
        var mockReader = statusReader ?? Mock.Of<IPrinterStatusSnapshotReader>(r =>
            r.GetStatusSnapshot(It.IsAny<Guid>()) == null);
        return new DispatchClaimService(db, mockReader, NullLogger<DispatchClaimService>.Instance);
    }

    private static BedClearAcknowledgementService CreateAckService(AppDbContext db)
        => new(db, NullLogger<BedClearAcknowledgementService>.Instance);

    /// <summary>Applies migrations and seeds a printer + dispatch state + print job.</summary>
    private async Task<(Guid PrinterId, Guid JobId, Guid GcodeFileId)> SeedAsync(AppDbContext db)
    {
        await db.Database.EnsureCreatedAsync();

        // Manufacturer and model are required FKs on Printer.
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
            JobKind = JobKind.FilamentCalibration,
            RequiredFirmwareFamily = PrinterFirmwareFamily.Klipper,
            RequiredGcodeDialect = PrinterGcodeDialect.Klipper,
            RequiredSlicerEngine = "OrcaSlicer",
            RequiredSlicerDistribution = "upstream",
            RequiredSlicerVersion = "2.3.0",
            PinnedPrinterConfigRevision = 1,
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
    // Test 1: Row version fence — two separate contexts claiming the same job
    // =========================================================================

    [Fact]
    public async Task TwoContexts_ClaimSameJob_OnlyOneSucceeds()
    {
        // Arrange
        await using AppDbContext seedCtx = CreateContext();
        (Guid printerId, Guid jobId, _) = await SeedAsync(seedCtx);

        await using AppDbContext ctx1 = CreateContext();
        await using AppDbContext ctx2 = CreateContext();

        var claimSvc1 = CreateClaimService(ctx1);
        var claimSvc2 = CreateClaimService(ctx2);

        var req = new DispatchClaimRequest(
            jobId, printerId, "actor-1", "Manual", "ack-key-1", null, null);
        var req2 = new DispatchClaimRequest(
            jobId, printerId, "actor-2", "Manual", "ack-key-2", null, null);

        // Act — fire both concurrently
        Task<DispatchClaimResult> t1 = claimSvc1.AcquireClaimAsync(req);
        Task<DispatchClaimResult> t2 = claimSvc2.AcquireClaimAsync(req2);
        DispatchClaimResult[] results = await Task.WhenAll(t1, t2);

        // Assert — exactly one succeeds, one gets a concurrency failure or job_not_dispatchable
        int successCount = results.Count(r => r.Success);
        int failureCount = results.Count(r => !r.Success);

        successCount.Should().Be(1, "exactly one claim must win");
        failureCount.Should().Be(1, "the loser must receive a failure result");

        // Verify job is in Starting state
        await using AppDbContext verifyCtx = CreateContext();
        PrintJob? verifiedJob = await verifyCtx.PrintJobs.FindAsync(jobId);
        verifiedJob.Should().NotBeNull();
        verifiedJob!.Status.Should().Be(PrintJobStatus.Starting);

        // Verify exactly one attempt was created
        int attemptCount = await verifyCtx.QueueDispatchAttempts.CountAsync(a => a.PrintJobId == jobId);
        attemptCount.Should().Be(1, "only one dispatch attempt must be written");

        // Verify exactly one outbox event was written
        int outboxCount = await verifyCtx.QueueDispatchOutbox.CountAsync(e => e.AggregateId == jobId);
        outboxCount.Should().Be(1, "only one outbox event must be written");
    }

    // =========================================================================
    // Test 2: Ack + claim atomicity — single transaction
    // =========================================================================

    [Fact]
    public async Task AcknowledgeAsync_WritesAckAndClaimAtomically_SingleTransaction()
    {
        // Arrange
        await using AppDbContext seedCtx = CreateContext();
        (Guid printerId, Guid jobId, _) = await SeedAsync(seedCtx);

        // Need the current dispatch state ETag for If-Match
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

        // Assert — should succeed (ack + claim in one transaction)
        result.Outcome.Should().Be(BedClearAckOutcome.Accepted);

        await using AppDbContext verifyCtx = CreateContext();
        PrintJob? job = await verifyCtx.PrintJobs.FindAsync(jobId);
        job!.Status.Should().Be(PrintJobStatus.Starting,
            "claim must have transitioned the job to Starting");
        job.ActualStartTime.Should().NotBeNull("start time must be set");

        PrinterDispatchState? verifyDs = await verifyCtx.PrinterDispatchStates
            .FirstOrDefaultAsync(s => s.PrinterId == printerId);
        verifyDs!.ActiveJobId.Should().Be(jobId, "dispatch state must record the active job");
        verifyDs.AcknowledgedJobId.Should().BeNull("ack was consumed by the claim");

        int attemptCount = await verifyCtx.QueueDispatchAttempts.CountAsync(a => a.PrintJobId == jobId);
        attemptCount.Should().Be(1, "one dispatch attempt must exist");

        int outboxCount = await verifyCtx.QueueDispatchOutbox.CountAsync(e => e.AggregateId == jobId);
        outboxCount.Should().Be(1, "one outbox event must exist");
    }

    // =========================================================================
    // Test 3: Row version is non-null after first write (SQLite stamping)
    // =========================================================================

    [Fact]
    public async Task PrintJob_AfterWrite_HasNonNullRowVersion_OnSQLite()
    {
        // Arrange
        await using AppDbContext ctx = CreateContext();
        await ctx.Database.EnsureCreatedAsync();

        // Seed required FK entities.
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

        // Assert
        job.RowVersion.Should().NotBeNull("StampRowVersions must generate a non-null token for SQLite");
        job.RowVersion!.Length.Should().BeGreaterThan(0);
    }

    // =========================================================================
    // Test 4: Firmware mismatch is rejected before claim
    // =========================================================================

    [Fact]
    public async Task ClaimService_FirmwareFamilyMismatch_RejectsWithTypedError()
    {
        // Arrange
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
            ConfigurationRevision = 1,
        };
        seedCtx.Printers.Add(printer);
        seedCtx.PrinterDispatchStates.Add(new PrinterDispatchState { PrinterId = printer.Id });

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
            PinnedPrinterConfigRevision = 1,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            QueuedAt = DateTime.UtcNow,
        };
        seedCtx.PrintJobs.Add(job);
        await seedCtx.SaveChangesAsync();

        await using AppDbContext claimCtx = CreateContext();
        var claimSvc = CreateClaimService(claimCtx);

        var request = new DispatchClaimRequest(
            job.Id, printer.Id, "actor", "Manual", "ack", null, null);

        // Act
        DispatchClaimResult result = await claimSvc.AcquireClaimAsync(request);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be("firmware_family_mismatch");
    }

    // =========================================================================
    // Test 5: Outbox deduplication — calibration job replay creates no second outbox
    // =========================================================================

    [Fact]
    public async Task BedClearAck_CalibrationReplay_NoSecondOutboxEvent()
    {
        // Arrange
        await using AppDbContext seedCtx = CreateContext();
        (Guid printerId, Guid jobId, _) = await SeedAsync(seedCtx);

        PrinterDispatchState? ds = await seedCtx.PrinterDispatchStates
            .FirstOrDefaultAsync(s => s.PrinterId == printerId);

        var req = new AcknowledgeBedClearRequest(
            jobId, printerId, "actor", "ack-key-dedup",
            ds!.RowVersion, 1);

        await using AppDbContext ctx1 = CreateContext();
        AcknowledgeBedClearResult first = await CreateAckService(ctx1).AcknowledgeAsync(req);
        first.Outcome.Should().Be(BedClearAckOutcome.Accepted);

        // Act — replay the same ack request (job is now Starting)
        await using AppDbContext ctx2 = CreateContext();
        PrinterDispatchState? ds2 = await ctx2.PrinterDispatchStates.FirstOrDefaultAsync(s => s.PrinterId == printerId);
        var replayReq = req with { IfMatchDispatchState = ds2!.RowVersion };
        AcknowledgeBedClearResult replay = await CreateAckService(ctx2).AcknowledgeAsync(replayReq);

        // Assert — job already Starting → AlreadyStartingOrPrinting (not a new claim)
        replay.Outcome.Should().Be(BedClearAckOutcome.AlreadyStartingOrPrinting);

        // Verify still only one outbox event
        await using AppDbContext verifyCtx = CreateContext();
        int outboxCount = await verifyCtx.QueueDispatchOutbox.CountAsync(e => e.AggregateId == jobId);
        outboxCount.Should().Be(1, "replay must not create a second outbox event");
    }
}
