using System.Security.Cryptography;
using System.Text;
using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Interfaces;
using Farm.Infrastructure.Services.Printers;
using Farm.Infrastructure.Services.Queue;
using Farm.Infrastructure.Services.Queue.Dispatch;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
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
    /// <summary>Spool the seeded calibration job pins; the printer must have it loaded.</summary>
    private const int CalibrationSpoolId = 4242;

    /// <summary>Material the seeded calibration job pins; the printer must have it loaded.</summary>
    private const string CalibrationMaterial = "PLA";

    private readonly SqliteConnection _keepAlive;
    private readonly string _connectionString;
    private static int _dbCounter;

    public CalibrationQueueConcurrencyTests()
    {
        int id = System.Threading.Interlocked.Increment(ref _dbCounter);
        _connectionString = $"Data Source=file:calib_concurrency_{id}?mode=memory&cache=shared;Foreign Keys=False";
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
        return new DispatchClaimService(
            db,
            reader,
            allocator,
            NullLogger<DispatchClaimService>.Instance,
            DispatchTestDoubles.TelemetryFreshnessPolicy(),
            DispatchTestDoubles.ValidByteIntegrityVerifier());
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

    private static BedClearAcknowledgementService CreateAckService(AppDbContext db, IDbOutboxSequenceAllocator? allocator = null, IPrinterStatusSnapshotReader? statusReader = null)
    {
        allocator ??= new DbOutboxSequenceAllocator();
        statusReader ??= DispatchTestDoubles.OnlineIdleReader(Guid.Empty);
        return new(
            db,
            allocator,
            statusReader,
            NullLogger<BedClearAcknowledgementService>.Instance,
            DispatchTestDoubles.TelemetryFreshnessPolicy(),
            DispatchTestDoubles.ValidByteIntegrityVerifier());
    }

    private static async Task PersistAcknowledgementAsync(
        AppDbContext db,
        Guid printerId,
        Guid jobId,
        string key,
        string actor = "actor")
    {
        await using var transaction = await db.Database.BeginTransactionAsync();
        PrintJob job = await db.PrintJobs.SingleAsync(candidate => candidate.Id == jobId);
        Printer printer = await db.Printers.SingleAsync(candidate => candidate.Id == printerId);
        PrinterDispatchState state = await db.PrinterDispatchStates
            .SingleAsync(candidate => candidate.PrinterId == printerId);
        state.AcknowledgedJobId = jobId;
        state.AcknowledgedAtUtc = DateTime.UtcNow;
        state.AcknowledgedBySubject = actor;
        state.AcknowledgementIdempotencyKey = key;
        state.AcknowledgementExpiresAtUtc = DateTime.UtcNow.AddMinutes(10);
        state.AcknowledgedJobRowVersion = job.RowVersion;
        state.AcknowledgedQueueRevision = state.QueueRevision;
        state.AcknowledgedPrinterConfigRevision = printer.ConfigurationRevision;
        Guid commandId = Guid.NewGuid();
        db.BedClearCommandRecords.Add(new BedClearCommandRecord
        {
            Id = Guid.NewGuid(),
            PrinterId = printerId,
            JobId = jobId,
            IdempotencyKey = key,
            RequestSha256 = new string('a', 64),
            ActorSubject = actor,
            JobRowVersion = job.RowVersion ?? [],
            DispatchStateRowVersion = state.RowVersion ?? [],
            QueueRevision = state.QueueRevision,
            PrinterConfigRevision = printer.ConfigurationRevision,
            Status = BedClearCommandStatus.Pending,
            OutboxEventId = commandId,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow,
            ExpiresAtUtc = DateTime.UtcNow.AddMinutes(10),
        });
        db.QueueDispatchOutbox.Add(new QueueDispatchOutbox
        {
            Id = commandId,
            Sequence = await new DbOutboxSequenceAllocator().AllocateAsync(db),
            AggregateType = nameof(PrintJob),
            AggregateId = jobId,
            PrinterId = printerId,
            ProjectId = job.CalibrationProjectId,
            JobStatus = job.Status.ToString(),
            JobKind = job.JobKind?.ToString(),
            EventType = BedClearAcknowledgementService.BackendStartCommandEventType,
            SchemaVersion = "1",
            PayloadJson = "{}",
            Status = QueueOutboxEventStatus.Pending,
            CreatedAtUtc = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
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

        Guid calibrationProjectId = Guid.NewGuid();
        Guid calibrationAttemptId = Guid.NewGuid();
        Guid calibrationOrchestrationId = Guid.NewGuid();
        Guid calibrationSnapshotId = Guid.NewGuid();
        Guid sourceArtifactId = Guid.NewGuid();
        Guid sourceSliceJobId = Guid.NewGuid();
        bool isCalibration = jobKind == JobKind.FilamentCalibration;

        var gcode = new GcodeFile
        {
            Id = Guid.NewGuid(),
            Name = "calibration.gcode",
            FileName = "calibration.gcode",
            FileHash = new string('a', 64),
            FileSizeBytes = 1024,
            FilePath = "/gcode",

            // Promoted immutable calibration artifact lineage (issue #900, defects 3 and 7).
            IsImmutable = isCalibration,
            PromotedAtUtc = isCalibration ? DateTime.UtcNow.AddMinutes(-1) : null,
            ContentSha256 = isCalibration ? new string('a', 64) : null,
            SourceModelSha256 = isCalibration ? new string('8', 64) : null,
            CalibrationProjectId = isCalibration ? calibrationProjectId : null,
            CalibrationAttemptId = isCalibration ? calibrationAttemptId : null,
            CalibrationOrchestrationId = isCalibration ? calibrationOrchestrationId : null,
            SourceArtifactId = isCalibration ? sourceArtifactId : null,
            SourceSliceJobId = isCalibration ? sourceSliceJobId : null,
            CalibrationManifestSha256 = isCalibration ? new string('9', 64) : null,
            SpecificationSha256 = isCalibration ? new string('s', 64) : null,
            MachineProfileSha256 = isCalibration ? new string('m', 64) : null,
            ProcessProfileSha256 = isCalibration ? new string('p', 64) : null,
            FilamentProfileSha256 = isCalibration ? new string('f', 64) : null,
            SlicerEngineName = isCalibration ? "OrcaSlicer" : null,
            SlicerDistribution = isCalibration ? "upstream" : null,
            PinnedSlicerVersion = isCalibration ? "2.3.0" : null,
            SlicerContainerDigest = isCalibration ? "sha256:test" : null,
            PrinterModelId = isCalibration ? model.Id : null,
            ObjectDimensionX = isCalibration ? 20 : null,
            ObjectDimensionY = isCalibration ? 20 : null,
            ObjectDimensionZ = isCalibration ? 20 : null,
            EstimatedFilamentWeightG = isCalibration ? 10 : null,
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

            // Hard filament gate inputs.
            CurrentSpoolId = CalibrationSpoolId,
            CurrentMaterial = CalibrationMaterial,
            MaxBuildVolumeX = 200,
            MaxBuildVolumeY = 200,
            MaxBuildVolumeZ = 200,
        };
        var toolhead = new Toolhead
        {
            Id = Guid.NewGuid(),
            PrinterId = printer.Id,
            Name = "Primary",
            Index = 0,
            IsPrimary = true,
            NozzleDiameter = 0.4,
            CurrentSpoolId = CalibrationSpoolId,
            CurrentMaterial = CalibrationMaterial,
        };
        printer.Toolheads.Add(toolhead);
        db.Printers.Add(printer);
        var spool = new Spool
        {
            Id = Guid.NewGuid(),
            Material = CalibrationMaterial,
            Sku = "PLA-TEST-SKU",
            LotNumber = "LOT-TEST",
            WeightGrams = 1000,
            InUse = true,
            AssignedPrinterId = printer.Id,
        };
        db.Spools.Add(spool);
        if (isCalibration)
        {
            db.CalibrationProjects.Add(new CalibrationProject
            {
                Id = calibrationProjectId,
                OwnerUserId = Guid.NewGuid(),
                Name = "Test calibration",
                PrinterId = printer.Id,
                CurrentPrinterConfigurationSnapshotId = calibrationSnapshotId,
                SelectedToolheadId = toolhead.Id,
                SelectedToolheadIndex = toolhead.Index,
                FilamentProvider = "local",
                FilamentProductId = "pla",
                FilamentProductName = "PLA",
                FilamentMaterial = CalibrationMaterial,
                FilamentSku = "PLA-TEST-SKU",
                LocalSpoolId = spool.Id,
                FilamentSnapshotJson = """{"material":"PLA"}""",
            });
            db.PrinterConfigurationSnapshots.Add(new PrinterConfigurationSnapshot
            {
                Id = calibrationSnapshotId,
                ProjectId = calibrationProjectId,
                AttemptId = calibrationAttemptId,
                PrinterId = printer.Id,
                SchemaVersion = "1",
                SnapshotSha256 = new string('6', 64),
                PrinterConfigurationRevision = 1,
                FirmwareFamily = PrinterFirmwareFamily.Klipper,
                GcodeDialect = PrinterGcodeDialect.Klipper,
                SanitizedSnapshotJson = "{}",
                SlicerEngine = "OrcaSlicer",
                SlicerDistribution = "upstream",
                SlicerVersion = "2.3.0",
                SlicerContainerDigest = "sha256:test",
                MachineProfileSha256 = new string('m', 64),
                ProcessProfileSha256 = new string('p', 64),
                FilamentProfileSha256 = new string('f', 64),
            });
            db.CalibrationAttempts.Add(new CalibrationAttempt
            {
                Id = calibrationAttemptId,
                ProjectId = calibrationProjectId,
                SpecificationSha256 = new string('s', 64),
                PrinterConfigurationSnapshotId = calibrationSnapshotId,
            });
            db.CalibrationOrchestrations.Add(new CalibrationOrchestration
            {
                Id = calibrationOrchestrationId,
                ProjectId = calibrationProjectId,
                AttemptId = calibrationAttemptId,
                SpecificationSha256 = new string('s', 64),
                SliceJobId = sourceSliceJobId,
                FinalArtifactId = sourceArtifactId,
                GcodeFileId = gcode.Id,
                GcodeSha256 = new string('a', 64),
                ManifestSha256 = new string('9', 64),
                SlicerContainerDigest = "sha256:test",
            });
        }

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
            RequiredFirmwareFamily = isCalibration ? PrinterFirmwareFamily.Klipper : null,
            RequiredGcodeDialect = isCalibration ? PrinterGcodeDialect.Klipper : null,
            RequiredSlicerEngine = isCalibration ? "OrcaSlicer" : null,
            RequiredSlicerDistribution = isCalibration ? "upstream" : null,
            RequiredSlicerVersion = isCalibration ? "2.3.0" : null,
            RequiredSlicerContainerDigest = isCalibration ? "sha256:test" : null,
            PinnedPrinterConfigRevision = isCalibration ? 1L : null,
            SpoolmanSpoolId = isCalibration ? CalibrationSpoolId : null,
            RequiredMaterialType = isCalibration ? CalibrationMaterial : null,
            // Required by the authoritative claim policy for calibration jobs (#900):
            CalibrationProjectId = isCalibration ? calibrationProjectId : null,
            CalibrationAttemptId = isCalibration ? calibrationAttemptId : null,
            CalibrationConfigSnapshotId = isCalibration ? calibrationSnapshotId : null,
            CalibrationOrchestrationId = isCalibration ? calibrationOrchestrationId : null,
            SourceArtifactId = isCalibration ? sourceArtifactId : null,
            SliceJobId = isCalibration ? sourceSliceJobId : null,
            GcodeContentSha256 = isCalibration ? new string('a', 64) : null,
            PinnedGcodeFileSizeBytes = isCalibration ? gcode.FileSizeBytes : null,
            SpecificationSha256 = isCalibration ? new string('s', 64) : null,
            MachineProfileSha256 = isCalibration ? new string('m', 64) : null,
            ProcessProfileSha256 = isCalibration ? new string('p', 64) : null,
            FilamentProfileSha256 = isCalibration ? new string('f', 64) : null,
            PrinterConfigSnapshotSha256 = isCalibration ? new string('6', 64) : null,
            PinnedPrinterModelId = isCalibration ? printer.ModelId : null,
            PinnedToolheadId = isCalibration ? toolhead.Id : null,
            PinnedToolheadIndex = isCalibration ? toolhead.Index : null,
            PinnedSpoolId = isCalibration ? spool.Id : null,
            PinnedFilamentSku = isCalibration ? "PLA-TEST-SKU" : null,
            PinnedFilamentLotNumber = isCalibration ? "LOT-TEST" : null,
            FilamentSnapshotSha256 = isCalibration
                ? ComputeSha256("""{"material":"PLA"}""")
                : null,
            SourceModelSha256 = isCalibration ? gcode.SourceModelSha256 : null,
            CalibrationManifestSha256 = isCalibration ? gcode.CalibrationManifestSha256 : null,
            RequiredNozzleDiameter = isCalibration ? 0.4m : null,
            RequiredCapabilities = isCalibration ? [] : null,
            PinnedObjectDimensionX = isCalibration ? gcode.ObjectDimensionX : null,
            PinnedObjectDimensionY = isCalibration ? gcode.ObjectDimensionY : null,
            PinnedObjectDimensionZ = isCalibration ? gcode.ObjectDimensionZ : null,
            EstimatedFilamentUsage = isCalibration ? gcode.EstimatedFilamentWeightG : null,
            FilamentName = isCalibration ? "PLA" : null,
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

    private static string ComputeSha256(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();

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

        var claimSvc1 = CreateClaimService(ctx1, MakeOnlineIdleReader(printerId));
        var claimSvc2 = CreateClaimService(ctx2, MakeOnlineIdleReader(printerId));

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
        PrintJob jobForAck = await seedCtx.PrintJobs.SingleAsync(job => job.Id == jobId);
        ds.Should().NotBeNull();

        await using AppDbContext ackCtx = CreateContext();
        var ackService = CreateAckService(ackCtx);

        var request = new AcknowledgeBedClearRequest(
            JobId: jobId,
            PrinterId: printerId,
            ActorSubject: "operator-1",
            IdempotencyKey: "ack-key-atomic",
            IfMatchDispatchState: ds!.RowVersion,
            ExpectedPrinterConfigRevision: 1,
            IfMatchJob: jobForAck.RowVersion);

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

            // Promoted immutable calibration artifact — the only artifact a
            // calibration job may print (issue #900, defects 3 and 7).
            IsImmutable = true,
            PromotedAtUtc = DateTime.UtcNow.AddMinutes(-1),
            ContentSha256 = new string('b', 64),
            CalibrationProjectId = Guid.NewGuid(),
            CalibrationAttemptId = Guid.NewGuid(),
            CalibrationOrchestrationId = Guid.NewGuid(),
            CalibrationManifestSha256 = new string('9', 64),
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

        job.RowVersion.Should().NotBeNull("portable revisions must produce an ETag token for SQLite");
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

            // Promoted immutable calibration artifact — the only artifact a
            // calibration job may print (issue #900, defects 3 and 7).
            IsImmutable = true,
            PromotedAtUtc = DateTime.UtcNow.AddMinutes(-1),
            ContentSha256 = new string('c', 64),
            CalibrationProjectId = Guid.NewGuid(),
            CalibrationAttemptId = Guid.NewGuid(),
            CalibrationOrchestrationId = Guid.NewGuid(),
            CalibrationManifestSha256 = new string('9', 64),
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
            GcodeContentSha256 = new string('c', 64),
            SpoolmanSpoolId = CalibrationSpoolId,
            RequiredMaterialType = CalibrationMaterial,
            CalibrationProjectId = Guid.NewGuid(),
            CalibrationAttemptId = Guid.NewGuid(),
            CalibrationConfigSnapshotId = Guid.NewGuid(),
            CalibrationOrchestrationId = Guid.NewGuid(),
            SpecificationSha256 = new string('s', 64),
            MachineProfileSha256 = new string('m', 64),
            ProcessProfileSha256 = new string('p', 64),
            FilamentProfileSha256 = new string('f', 64),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            QueuedAt = DateTime.UtcNow,
        };
        seedCtx.PrintJobs.Add(job);

        await seedCtx.SaveChangesAsync();
        await PersistAcknowledgementAsync(
            seedCtx,
            printer.Id,
            job.Id,
            "test-ack-key");

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
        await PersistAcknowledgementAsync(
            ackCtx,
            printerId,
            jobId,
            "ack-key-1");

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

    [Fact]
    public async Task ClaimService_CalibrationWithLegacyAcknowledgedJobToken_RejectsAsStale()
    {
        await using AppDbContext seedCtx = CreateContext();
        (Guid printerId, Guid jobId, _) = await SeedAsync(seedCtx);

        await using (AppDbContext ackCtx = CreateContext())
        {
            await PersistAcknowledgementAsync(
                ackCtx,
                printerId,
                jobId,
                "legacy-ack-key");
        }

        await using (AppDbContext legacyCtx = CreateContext())
        {
            PrinterDispatchState state = await legacyCtx.PrinterDispatchStates
                .SingleAsync(candidate => candidate.PrinterId == printerId);
            state.AcknowledgedJobRowVersion = Convert.FromHexString("000000000000002A");
            await legacyCtx.SaveChangesAsync();
        }

        await using AppDbContext claimCtx = CreateContext();
        DispatchClaimService claimService = CreateClaimService(
            claimCtx,
            MakeOnlineIdleReader(printerId));

        DispatchClaimResult result = await claimService.AcquireClaimAsync(
            new DispatchClaimRequest(
                jobId,
                printerId,
                "actor",
                "Manual",
                "legacy-ack-key",
                null,
                null));

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be(
            "acknowledgement_job_revision_stale",
            "an unversioned SQL Server rowversion snapshot must never authorize dispatch");
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

            // Promoted immutable calibration artifact — the only artifact a
            // calibration job may print (issue #900, defects 3 and 7).
            IsImmutable = true,
            PromotedAtUtc = DateTime.UtcNow.AddMinutes(-1),
            ContentSha256 = new string('d', 64),
            CalibrationProjectId = Guid.NewGuid(),
            CalibrationAttemptId = Guid.NewGuid(),
            CalibrationOrchestrationId = Guid.NewGuid(),
            CalibrationManifestSha256 = new string('9', 64),
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
            GcodeContentSha256 = new string('d', 64), // Matches the promoted artifact hash.
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
        };
        seedCtx.PrinterDispatchStates.Add(ds);
        await seedCtx.SaveChangesAsync();
        await PersistAcknowledgementAsync(
            seedCtx,
            printer.Id,
            job.Id,
            "ack-key-compat");

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
        PrintJob jobForAck = await seedCtx.PrintJobs.SingleAsync(job => job.Id == jobId);

        var req = new AcknowledgeBedClearRequest(
            jobId, printerId, "actor", "ack-key-dedup",
            ds!.RowVersion, 1, jobForAck.RowVersion);

        // First ack — should succeed.
        await using AppDbContext ctx1 = CreateContext();
        AcknowledgeBedClearResult first = await CreateAckService(ctx1).AcknowledgeAsync(req);
        first.Outcome.Should().Be(BedClearAckOutcome.Accepted);

        // Act — replay the same ack request (same key, same job, ack already persisted).
        await using AppDbContext ctx2 = CreateContext();
        PrinterDispatchState? ds2 = await ctx2.PrinterDispatchStates.FirstOrDefaultAsync(s => s.PrinterId == printerId);
        PrintJob replayJob = await ctx2.PrintJobs.SingleAsync(job => job.Id == jobId);
        var replayReq = req with
        {
            IfMatchDispatchState = ds2!.RowVersion,
            IfMatchJob = replayJob.RowVersion,
        };
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
        await PersistAcknowledgementAsync(
            ackCtx,
            printerId,
            jobId,
            "valid-ack-key");

        await using AppDbContext claimCtx = CreateContext();
        var claimSvc = CreateClaimService(claimCtx, MakeOnlineIdleReader(printerId));

        var request = new DispatchClaimRequest(
            jobId, printerId, "actor", "BedClear", "valid-ack-key", null, null);

        // Act
        DispatchClaimResult result = await claimSvc.AcquireClaimAsync(request);

        // Assert — claim succeeds: all checks pass.
        result.Success.Should().BeTrue(
            $"calibration with valid ack, fresh telemetry, and all required fields must succeed: " +
            $"{result.ErrorCode} {result.ErrorDetail}");
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
        PrintJob jobForAck = await ackCtx.PrintJobs.SingleAsync(job => job.Id == jobId1);
        var ackSvc = CreateAckService(ackCtx);
        AcknowledgeBedClearResult ack1 = await ackSvc.AcknowledgeAsync(
            new AcknowledgeBedClearRequest(
                jobId1,
                printerId,
                "actor",
                "mono-key-1",
                ds1!.RowVersion,
                1,
                jobForAck.RowVersion));
        ack1.Outcome.Should().Be(BedClearAckOutcome.Accepted);

        // Pre-stamp an ack so the claim can fire and write a second outbox event.
        await using AppDbContext ackPreCtx = CreateContext();
        await PersistAcknowledgementAsync(
            ackPreCtx,
            printerId,
            jobId1,
            "claim-key-1");

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
    //          are reset to Pending during every poll.
    // =========================================================================

    [Fact]
    public async Task BackendStartConsumer_PollRecoversProcessingOnlyAfterStaleAge()
    {
        Guid eventId = Guid.NewGuid();
        await using (AppDbContext seedCtx = CreateContext())
        {
            (Guid printerId, Guid jobId, _) = await SeedAsync(seedCtx);
            seedCtx.QueueDispatchOutbox.Add(new QueueDispatchOutbox
            {
                Id = eventId,
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
                LastAttemptedAtUtc = DateTime.UtcNow.AddSeconds(-30),
                CreatedAtUtc = DateTime.UtcNow.AddMinutes(-1),
            });
            await seedCtx.SaveChangesAsync();
        }

        var management = new Mock<IPrintJobManagementService>();
        ServiceProvider provider = new ServiceCollection()
            .AddDbContext<AppDbContext>(options => options.UseSqlite(_connectionString))
            .AddSingleton(management.Object)
            .BuildServiceProvider();
        await using (provider)
        {
            var consumer = new BackendStartCommandConsumerService(
                provider.GetRequiredService<IServiceScopeFactory>(),
                NullLogger<BackendStartCommandConsumerService>.Instance);

            await consumer.ProcessPendingCommandsAsync(CancellationToken.None);

            await using (AppDbContext freshVerify = CreateContext())
            {
                QueueDispatchOutbox fresh = await freshVerify.QueueDispatchOutbox
                    .SingleAsync(candidate => candidate.Id == eventId);
                fresh.Status.Should().Be(QueueOutboxEventStatus.Processing);
            }

            await using (AppDbContext ageContext = CreateContext())
            {
                QueueDispatchOutbox aged = await ageContext.QueueDispatchOutbox
                    .SingleAsync(candidate => candidate.Id == eventId);
                aged.LastAttemptedAtUtc = DateTime.UtcNow.AddMinutes(-11);
                await ageContext.SaveChangesAsync();
            }

            await consumer.ProcessPendingCommandsAsync(CancellationToken.None);
        }

        await using AppDbContext verifyCtx = CreateContext();
        QueueDispatchOutbox recovered = await verifyCtx.QueueDispatchOutbox
            .SingleAsync(candidate => candidate.Id == eventId);
        recovered.Status.Should().Be(QueueOutboxEventStatus.Pending);
        recovered.LastError.Should().Contain("Recovered from stale lease");
        recovered.RetryAfterUtc.Should().BeAfter(DateTime.UtcNow);
        management.Verify(
            service => service.DispatchJobWithAckAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // =========================================================================
    // Test 12: DB-backed sequence allocator — two concurrent DbContexts cannot
    //          produce duplicate sequence values (cross-process safety proof).
    // =========================================================================

    /// <summary>
    /// Critical cross-process test: two separate <see cref="AppDbContext"/> instances
    /// (simulating two API processes) both attempt to allocate a sequence and write
    /// an outbox event concurrently. Each allocation and event insert share one
    /// transaction so a sequence cannot commit without its corresponding event.
    /// </summary>
    [Fact]
    public async Task DbSequenceAllocator_TwoConcurrentContexts_ProduceUniqueSequences()
    {
        // Arrange: seed a database with OutboxSequenceState + one calibration job.
        await using AppDbContext seedCtx = CreateContext();
        (Guid _, Guid jobId, _) = await SeedAsync(seedCtx, JobKind.Standard);

        // Verify the seed row exists.
        OutboxSequenceState? seedState = await seedCtx.OutboxSequenceStates.SingleOrDefaultAsync();
        seedState.Should().NotBeNull("OutboxSequenceState seed row must be created by EnsureCreated");
        seedState!.NextSequence.Should().Be(0, "fresh database should start at 0");

        // Arrange two separate DbContexts + allocators (simulating two API processes).
        await using AppDbContext ctx1 = CreateContext();
        await using AppDbContext ctx2 = CreateContext();

        Task<long> producer1 = PersistEventAsync(ctx1, jobId);
        Task<long> producer2 = PersistEventAsync(ctx2, jobId);
        long[] allocated = await Task.WhenAll(producer1, producer2);

        allocated.Should().BeEquivalentTo([1L, 2L]);

        await using AppDbContext verifyCtx = CreateContext();
        List<long> sequences = await verifyCtx.QueueDispatchOutbox
            .OrderBy(evt => evt.Sequence)
            .Select(evt => evt.Sequence)
            .ToListAsync();
        sequences.Should().Equal(1, 2);

        OutboxSequenceState? finalState = await verifyCtx.OutboxSequenceStates.SingleAsync();
        finalState.NextSequence.Should().Be(2);
    }

    private static async Task<long> PersistEventAsync(
        AppDbContext db,
        Guid jobId)
    {
        await using QueueOutboxTransactionScope transaction =
            await QueueOutboxTransactionScope.BeginAsync(db);
        long sequence = await new DbOutboxSequenceAllocator().AllocateAsync(db);
        db.QueueDispatchOutbox.Add(new QueueDispatchOutbox
        {
            Id = Guid.NewGuid(),
            Sequence = sequence,
            AggregateType = nameof(PrintJob),
            AggregateId = jobId,
            EventType = "PrintFarmer.Queue.Test.v1",
            SchemaVersion = "1",
            PayloadJson = "{}",
            Status = QueueOutboxEventStatus.Pending,
            CreatedAtUtc = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        return sequence;
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

        await using var transaction = await seedCtx.Database.BeginTransactionAsync();
        long seq1 = await alloc.AllocateAsync(seedCtx);
        long seq2 = await alloc.AllocateAsync(seedCtx);
        long seq3 = await alloc.AllocateAsync(seedCtx);
        seedCtx.QueueDispatchOutbox.AddRange(
            CreateOutboxEvent(seq1),
            CreateOutboxEvent(seq2),
            CreateOutboxEvent(seq3));
        await seedCtx.SaveChangesAsync();
        await transaction.CommitAsync();

        seq1.Should().Be(1);
        seq2.Should().Be(2);
        seq3.Should().Be(3, "in-context sequential allocations must increment the in-memory counter");
    }

    private static QueueDispatchOutbox CreateOutboxEvent(long sequence) =>
        new()
        {
            Id = Guid.NewGuid(),
            Sequence = sequence,
            AggregateType = nameof(PrintJob),
            AggregateId = Guid.NewGuid(),
            EventType = QueueLifecycleEventWriter.EventTypeJobCompleted,
            SchemaVersion = "1",
            PayloadJson = "{}",
            Status = QueueOutboxEventStatus.Pending,
            CreatedAtUtc = DateTime.UtcNow,
        };

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
        state.Revision.Should().Be(1, "seeded revisions start at one");
        state.RowVersion.Should().Equal(RevisionETag.EncodeBytes(1));

        // After a write, Revision must advance.
        state.NextSequence = 1;
        await ctx.SaveChangesAsync();

        await using AppDbContext verifyCtx = CreateContext();
        OutboxSequenceState? updated = await verifyCtx.OutboxSequenceStates.SingleAsync();
        updated.NextSequence.Should().Be(1);
        updated.Revision.Should().Be(2);
        updated.RowVersion.Should().Equal(RevisionETag.EncodeBytes(2));
        updated.RowVersion!.Length.Should().Be(sizeof(byte) + sizeof(long));
    }
}
