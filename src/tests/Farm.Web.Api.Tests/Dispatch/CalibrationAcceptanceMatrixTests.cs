// <copyright file="CalibrationAcceptanceMatrixTests.cs" company="OlyForge3D">
// Copyright (c) OlyForge3D. All rights reserved.
// </copyright>

using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text;
using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Queue;
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
/// Comprehensive acceptance matrix tests covering all 11 acceptance groups from
/// issue #900.  Every test asserts specific, production-service-level behavior —
/// no mock-only or null-check-only coverage.
///
/// Group coverage in this file:
///   G2: Exact acknowledgement replay / mismatched key / durable semantics
///   G3: Full authoritative claim policy (IsAvailable, GcodeHash, lineage, hashes)
///   G4: Calibration creation race / ETag / Idempotency-Replayed semantics
///   G5: Production bypass elimination (all start paths use shared claim)
///   G6: Typed backend outcome: Unknown/ReconciliationRequired, FailedBeforeStart
///   G7: Terminal lease lifecycle (cancel → ack invalidated, requeue → new ack required)
///   G8: Priority validation — undefined priority rejected on every mutation
///   G9: Audit / event authority (outbox events carry schemaVersion/eventType/revisions)
///  G10: ETag / If-Match concurrency (412 on stale dispatch state)
///  G11: Migration-applied correctness already covered by CalibrationProviderMigrationHarnessTests
/// </summary>
[Trait("Category", "DbHeavy")]
public class CalibrationAcceptanceMatrixTests : IAsyncDisposable
{
    /// <summary>Spool the seeded calibration job pins; the printer must have it loaded.</summary>
    private const int CalibrationSpoolId = 4242;

    /// <summary>Material the seeded calibration job pins; the printer must have it loaded.</summary>
    private const string CalibrationMaterial = "PLA";

    private readonly SqliteConnection _keepAlive;
    private readonly string _connectionString;
    private static int _dbCounter;

    public CalibrationAcceptanceMatrixTests()
    {
        int id = System.Threading.Interlocked.Increment(ref _dbCounter);
        _connectionString = $"Data Source=file:accept_matrix_{id}?mode=memory&cache=shared;Foreign Keys=False";
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
        ctx.Database.ExecuteSqlRaw("PRAGMA foreign_keys = OFF;");
        return ctx;
    }

    private static IPrinterStatusSnapshotReader MakeOnlineIdleReader(Guid printerId)
    {
        var statusDto = new PrinterStatusDto(Id: printerId, IsOnline: true, State: "idle");
        var snapshot = new PrinterStatusSnapshot(
            Status: statusDto,
            ObservedAtUtc: DateTime.UtcNow,
            LastSeenAtUtc: DateTime.UtcNow,
            Source: "test");
        return Mock.Of<IPrinterStatusSnapshotReader>(
            r => r.GetStatusSnapshot(printerId) == snapshot);
    }

    private static BedClearAcknowledgementService CreateAckService(
        AppDbContext db,
        IPrinterStatusSnapshotReader? statusReader = null) =>
        new(
            db,
            new DbOutboxSequenceAllocator(),
            statusReader ?? DispatchTestDoubles.OnlineIdleReader(Guid.Empty),
            NullLogger<BedClearAcknowledgementService>.Instance,
            DispatchTestDoubles.ValidByteIntegrityVerifier());

    private static DispatchClaimService CreateClaimService(AppDbContext db, IPrinterStatusSnapshotReader? reader = null)
    {
        reader ??= Mock.Of<IPrinterStatusSnapshotReader>(
            r => r.GetStatusSnapshot(It.IsAny<Guid>()) == (PrinterStatusSnapshot?)null);
        return new(
            db,
            reader,
            new DbOutboxSequenceAllocator(),
            NullLogger<DispatchClaimService>.Instance,
            DispatchTestDoubles.ValidByteIntegrityVerifier());
    }

    private async Task<(Guid PrinterId, Guid JobId, Guid GcodeId)> SeedFullCalibrationJobAsync(
        AppDbContext db,
        bool setAck = false)
    {
        await db.Database.EnsureCreatedAsync();

        Guid calibrationProjectId = Guid.NewGuid();
        Guid calibrationAttemptId = Guid.NewGuid();
        Guid calibrationOrchestrationId = Guid.NewGuid();
        Guid calibrationSnapshotId = Guid.NewGuid();
        Guid sourceArtifactId = Guid.NewGuid();
        Guid sourceSliceJobId = Guid.NewGuid();

        var mfr = new Manufacturer { Id = Guid.NewGuid(), Name = "Mfr" };
        db.Manufacturers.Add(mfr);
        var mdl = new PrinterModel { Id = Guid.NewGuid(), ManufacturerId = mfr.Id, Name = "Mdl" };
        db.PrinterModels.Add(mdl);

        var gcode = new GcodeFile
        {
            Id = Guid.NewGuid(),
            Name = "calib.gcode",
            FileName = "calib.gcode",
            FileHash = new string('a', 64),
            FileSizeBytes = 1024,
            FilePath = "/calib",

            // Promoted immutable calibration artifact — the ONLY artifact a calibration
            // job may print (issue #900, defects 3 and 7).
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
            FirmwareFamily = nameof(PrinterFirmwareFamily.Klipper),
            GcodeDialect = nameof(PrinterGcodeDialect.Klipper),
            PrinterModelId = mdl.Id,
            ObjectDimensionX = 20,
            ObjectDimensionY = 20,
            ObjectDimensionZ = 20,
            EstimatedFilamentWeightG = 10,
        };
        db.GcodeFiles.Add(gcode);

        var printer = new Printer
        {
            Id = Guid.NewGuid(),
            Name = "Printer",
            ServerUrl = $"http://p-{Guid.NewGuid():N}",
            ManufacturerId = mfr.Id,
            ModelId = mdl.Id,
            IsEnabled = true,
            InMaintenance = false,
            IsAvailable = true,
            FirmwareFamily = PrinterFirmwareFamily.Klipper,
            GcodeDialect = PrinterGcodeDialect.Klipper,
            CalibrationSlicerEngine = "OrcaSlicer",
            CalibrationSlicerDistribution = "upstream",
            CalibrationSlicerVersion = "2.3.0",
            ConfigurationRevision = 1,

            // Hard filament gate inputs: the exact spool/material the job pins.
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
        db.CalibrationProjects.Add(new CalibrationProject
        {
            Id = calibrationProjectId,
            OwnerUserId = Guid.NewGuid(),
            Name = "Acceptance calibration",
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
            MachineProfileSha256 = new string('c', 64),
            ProcessProfileSha256 = new string('d', 64),
            FilamentProfileSha256 = new string('e', 64),
        });
        db.CalibrationAttempts.Add(new CalibrationAttempt
        {
            Id = calibrationAttemptId,
            ProjectId = calibrationProjectId,
            SpecificationSha256 = new string('b', 64),
            PrinterConfigurationSnapshotId = calibrationSnapshotId,
        });
        db.CalibrationOrchestrations.Add(new CalibrationOrchestration
        {
            Id = calibrationOrchestrationId,
            ProjectId = calibrationProjectId,
            AttemptId = calibrationAttemptId,
            SpecificationSha256 = new string('b', 64),
            SliceJobId = sourceSliceJobId,
            FinalArtifactId = sourceArtifactId,
            GcodeFileId = gcode.Id,
            GcodeSha256 = new string('a', 64),
            ManifestSha256 = new string('9', 64),
            SlicerContainerDigest = "sha256:test",
        });

        var ds = new PrinterDispatchState { PrinterId = printer.Id };
        db.PrinterDispatchStates.Add(ds);

        var job = new PrintJob
        {
            Id = Guid.NewGuid(),
            Name = "calib job",
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
            RequiredSlicerContainerDigest = "sha256:test",
            PinnedPrinterConfigRevision = 1,
            GcodeContentSha256 = new string('a', 64), // Matches gcode.FileHash
            PinnedGcodeFileSizeBytes = gcode.FileSizeBytes,
            SpoolmanSpoolId = CalibrationSpoolId,
            RequiredMaterialType = CalibrationMaterial,
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
            FilamentSnapshotSha256 = ComputeSha256("""{"material":"PLA"}"""),
            SourceModelSha256 = gcode.SourceModelSha256,
            CalibrationManifestSha256 = gcode.CalibrationManifestSha256,
            RequiredNozzleDiameter = 0.4m,
            RequiredCapabilities = [],
            PinnedObjectDimensionX = gcode.ObjectDimensionX,
            PinnedObjectDimensionY = gcode.ObjectDimensionY,
            PinnedObjectDimensionZ = gcode.ObjectDimensionZ,
            EstimatedFilamentUsage = gcode.EstimatedFilamentWeightG,
            FilamentName = "PLA",
            IdempotencyScope = "test-scope",
            IdempotencyKey = Guid.NewGuid().ToString(),
            IdempotencyRequestSha256 = new string('f', 64),
            QueuePosition = 1,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            QueuedAt = DateTime.UtcNow,
        };
        db.PrintJobs.Add(job);
        await db.SaveChangesAsync();

        if (setAck)
        {
            await using var transaction = await db.Database.BeginTransactionAsync();
            ds.AcknowledgedJobId = job.Id;
            ds.AcknowledgementIdempotencyKey = "valid-ack-key";
            ds.AcknowledgementExpiresAtUtc = DateTime.UtcNow.AddMinutes(15);
            ds.AcknowledgedJobRowVersion = job.RowVersion;
            ds.AcknowledgedQueueRevision = ds.QueueRevision;
            ds.AcknowledgedPrinterConfigRevision = printer.ConfigurationRevision;
            Guid commandId = Guid.NewGuid();
            db.BedClearCommandRecords.Add(new BedClearCommandRecord
            {
                Id = Guid.NewGuid(),
                PrinterId = printer.Id,
                JobId = job.Id,
                IdempotencyKey = "valid-ack-key",
                RequestSha256 = new string('a', 64),
                ActorSubject = "actor",
                JobRowVersion = job.RowVersion ?? [],
                DispatchStateRowVersion = ds.RowVersion ?? [],
                QueueRevision = ds.QueueRevision,
                PrinterConfigRevision = printer.ConfigurationRevision,
                Status = BedClearCommandStatus.Pending,
                OutboxEventId = commandId,
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow,
                ExpiresAtUtc = DateTime.UtcNow.AddMinutes(15),
            });
            db.QueueDispatchOutbox.Add(new QueueDispatchOutbox
            {
                Id = commandId,
                Sequence = await new DbOutboxSequenceAllocator().AllocateAsync(db),
                AggregateType = nameof(PrintJob),
                AggregateId = job.Id,
                PrinterId = printer.Id,
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

        return (printer.Id, job.Id, gcode.Id);
    }

    // =========================================================================
    // G2: Exact acknowledgement replay and mismatched key semantics
    // =========================================================================

    [Fact]
    public async Task G2_BedClearAck_ExactReplay_SameKeyAndJob_ReturnsReplayed_NoSecondCommand()
    {
        await using AppDbContext seedCtx = CreateContext();
        (Guid printerId, Guid jobId, _) = await SeedFullCalibrationJobAsync(seedCtx);

        PrinterDispatchState ds = await seedCtx.PrinterDispatchStates.SingleAsync(s => s.PrinterId == printerId);
        PrintJob job = await seedCtx.PrintJobs.SingleAsync(candidate => candidate.Id == jobId);
        var req = new AcknowledgeBedClearRequest(
            jobId,
            printerId,
            "actor",
            "ack-key-g2",
            ds.RowVersion,
            1,
            job.RowVersion);

        // First ack.
        await using AppDbContext ctx1 = CreateContext();
        var r1 = await CreateAckService(ctx1).AcknowledgeAsync(req);
        r1.Outcome.Should().Be(BedClearAckOutcome.Accepted, "first ack must succeed");

        // Replay with same key.
        await using AppDbContext ctx2 = CreateContext();
        PrinterDispatchState ds2 = await ctx2.PrinterDispatchStates.SingleAsync(s => s.PrinterId == printerId);
        var replayReq = req with { IfMatchDispatchState = ds2.RowVersion };
        var r2 = await CreateAckService(ctx2).AcknowledgeAsync(replayReq);

        r2.Outcome.Should().Be(BedClearAckOutcome.Replayed, "exact replay must return Replayed");

        // Still only one BackendStartCommand event.
        await using AppDbContext verifyCtx = CreateContext();
        int cmdCount = await verifyCtx.QueueDispatchOutbox
            .CountAsync(e => e.EventType == BedClearAcknowledgementService.BackendStartCommandEventType);
        cmdCount.Should().Be(1, "replay must not create a second BackendStartCommand");
    }

    [Fact]
    public async Task G2_BedClearAck_SameKeyDifferentJob_ReturnsIdempotencyMismatch()
    {
        // IdempotencyMismatch occurs when the SAME key is used for a DIFFERENT job.
        // (Different key, same job is a valid new acknowledgement and returns Accepted.)
        await using AppDbContext seedCtx = CreateContext();
        await seedCtx.Database.EnsureCreatedAsync();

        var mfr = new Manufacturer { Id = Guid.NewGuid(), Name = "Mfr-Mism" };
        seedCtx.Manufacturers.Add(mfr);
        var mdl = new PrinterModel { Id = Guid.NewGuid(), ManufacturerId = mfr.Id, Name = "Mdl-Mism" };
        seedCtx.PrinterModels.Add(mdl);

        var gcode = new GcodeFile
        {
            Id = Guid.NewGuid(),
            Name = "mism.gcode",
            FileName = "mism.gcode",
            FileSizeBytes = 1,
            FilePath = "/m",
        };
        seedCtx.GcodeFiles.Add(gcode);

        var printer = new Printer
        {
            Id = Guid.NewGuid(),
            Name = "MismPrinter",
            ServerUrl = $"http://mism-{Guid.NewGuid():N}",
            ManufacturerId = mfr.Id,
            ModelId = mdl.Id,
            IsEnabled = true,
            IsAvailable = true,
        };
        seedCtx.Printers.Add(printer);

        var ds = new PrinterDispatchState { PrinterId = printer.Id };
        seedCtx.PrinterDispatchStates.Add(ds);

        var jobA = new PrintJob
        {
            Id = Guid.NewGuid(),
            Name = "job-A",
            GcodeFileId = gcode.Id,
            AssignedPrinterId = printer.Id,
            Status = PrintJobStatus.Assigned,
            Priority = (int)PrintJobPriority.Normal,
            QueuePosition = 1,
            // Idempotency-key semantics are job-kind agnostic; a Standard job keeps
            // this test focused on the key contract.
            JobKind = JobKind.Standard,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            QueuedAt = DateTime.UtcNow,
        };
        var jobB = new PrintJob
        {
            Id = Guid.NewGuid(),
            Name = "job-B",
            GcodeFileId = gcode.Id,
            AssignedPrinterId = printer.Id,
            Status = PrintJobStatus.Assigned,
            Priority = (int)PrintJobPriority.Normal,
            QueuePosition = 2,
            // Idempotency-key semantics are job-kind agnostic; a Standard job keeps
            // this test focused on the key contract.
            JobKind = JobKind.Standard,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            QueuedAt = DateTime.UtcNow,
        };
        seedCtx.PrintJobs.Add(jobA);
        seedCtx.PrintJobs.Add(jobB);
        await seedCtx.SaveChangesAsync();

        // First ack: key "shared-key" for job-A.
        await using AppDbContext ctx1 = CreateContext();
        PrinterDispatchState? ds1 = await ctx1.PrinterDispatchStates.SingleAsync(s => s.PrinterId == printer.Id);
        var r1 = await CreateAckService(ctx1).AcknowledgeAsync(
            new AcknowledgeBedClearRequest(
                jobA.Id,
                printer.Id,
                "actor",
                "shared-key",
                ds1.RowVersion,
                1,
                jobA.RowVersion));
        r1.Outcome.Should().Be(BedClearAckOutcome.Accepted, "first ack (key A for job-A) must succeed");

        // Second ack: SAME key "shared-key" for job-B → IdempotencyMismatch.
        await using AppDbContext ctx2 = CreateContext();
        PrinterDispatchState? ds2 = await ctx2.PrinterDispatchStates.SingleAsync(s => s.PrinterId == printer.Id);
        var r2 = await CreateAckService(ctx2).AcknowledgeAsync(
            new AcknowledgeBedClearRequest(
                jobB.Id,
                printer.Id,
                "actor",
                "shared-key",
                ds2.RowVersion,
                1,
                jobB.RowVersion));

        r2.Outcome.Should().Be(BedClearAckOutcome.IdempotencyMismatch,
            "using the same idempotency key for a different job must return IdempotencyMismatch");
    }

    [Fact]
    public async Task G2_BedClearAck_ExpiredAck_CannotBeUsedForClaim()
    {
        await using AppDbContext seedCtx = CreateContext();
        (Guid printerId, Guid jobId, _) = await SeedFullCalibrationJobAsync(seedCtx);

        // Pre-seed an EXPIRED ack directly.
        PrinterDispatchState ds = await seedCtx.PrinterDispatchStates.SingleAsync(s => s.PrinterId == printerId);
        ds.AcknowledgedJobId = jobId;
        ds.AcknowledgementIdempotencyKey = "expired-key";
        ds.AcknowledgedAtUtc = DateTime.UtcNow.AddHours(-1);
        ds.AcknowledgementExpiresAtUtc = DateTime.UtcNow.AddMinutes(-30); // Already expired
        await seedCtx.SaveChangesAsync();

        await using AppDbContext claimCtx = CreateContext();
        var claimSvc = CreateClaimService(claimCtx, MakeOnlineIdleReader(printerId));

        var result = await claimSvc.AcquireClaimAsync(
            new DispatchClaimRequest(jobId, printerId, "actor", "BedClear", "expired-key", null, null));

        result.Success.Should().BeFalse("expired acknowledgement must not be consumed by claim");
        result.ErrorCode.Should().Be("acknowledgement_expired");
    }

    // =========================================================================
    // G3: Authoritative claim policy — IsAvailable, GcodeHash, lineage
    // =========================================================================

    [Fact]
    public async Task G3_ClaimPolicy_PrinterNotAvailable_RejectsWithPrinterUnavailable()
    {
        await using AppDbContext seedCtx = CreateContext();
        await seedCtx.Database.EnsureCreatedAsync();

        var mfr = new Manufacturer { Id = Guid.NewGuid(), Name = "Mfr-Avail" };
        seedCtx.Manufacturers.Add(mfr);
        var mdl = new PrinterModel { Id = Guid.NewGuid(), ManufacturerId = mfr.Id, Name = "Mdl-Avail" };
        seedCtx.PrinterModels.Add(mdl);

        var gcode = new GcodeFile
        {
            Id = Guid.NewGuid(),
            Name = "a.gcode",
            FileName = "a.gcode",
            FileSizeBytes = 100,
            FilePath = "/a",
        };
        seedCtx.GcodeFiles.Add(gcode);

        var printer = new Printer
        {
            Id = Guid.NewGuid(),
            Name = "UnavailablePrinter",
            ServerUrl = $"http://ua-{Guid.NewGuid():N}",
            ManufacturerId = mfr.Id,
            ModelId = mdl.Id,
            IsEnabled = true,
            InMaintenance = false,
            IsAvailable = false, // ← explicitly unavailable
        };
        seedCtx.Printers.Add(printer);
        seedCtx.PrinterDispatchStates.Add(new PrinterDispatchState { PrinterId = printer.Id });

        var job = new PrintJob
        {
            Id = Guid.NewGuid(),
            Name = "avail-job",
            GcodeFileId = gcode.Id,
            AssignedPrinterId = printer.Id,
            Status = PrintJobStatus.Assigned,
            Priority = (int)PrintJobPriority.Normal,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            QueuedAt = DateTime.UtcNow,
        };
        seedCtx.PrintJobs.Add(job);
        await seedCtx.SaveChangesAsync();

        await using AppDbContext claimCtx = CreateContext();
        var claimSvc = CreateClaimService(claimCtx);

        var result = await claimSvc.AcquireClaimAsync(
            new DispatchClaimRequest(job.Id, printer.Id, "actor", "Manual", null, null, null));

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be("printer_unavailable",
            "claim must fail if printer.IsAvailable is false even when IsEnabled is true");
    }

    [Fact]
    public async Task G3_ClaimPolicy_GcodeHashMismatch_RejectsWithGcodeHashMismatch()
    {
        await using AppDbContext seedCtx = CreateContext();
        await seedCtx.Database.EnsureCreatedAsync();

        var mfr = new Manufacturer { Id = Guid.NewGuid(), Name = "Mfr-Hash" };
        seedCtx.Manufacturers.Add(mfr);
        var mdl = new PrinterModel { Id = Guid.NewGuid(), ManufacturerId = mfr.Id, Name = "Mdl-Hash" };
        seedCtx.PrinterModels.Add(mdl);

        // GcodeFile has FileHash 'aaaa...', but job has GcodeContentSha256 'bbbb...' — mismatch.
        var gcode = new GcodeFile
        {
            Id = Guid.NewGuid(),
            Name = "hash.gcode",
            FileName = "hash.gcode",
            FileHash = new string('a', 64),
            FileSizeBytes = 1,
            FilePath = "/h",

            // Promoted immutable calibration artifact — the only artifact a
            // calibration job may print (issue #900, defects 3 and 7).
            IsImmutable = true,
            PromotedAtUtc = DateTime.UtcNow.AddMinutes(-1),
            ContentSha256 = new string('a', 64),
            CalibrationProjectId = Guid.NewGuid(),
            CalibrationAttemptId = Guid.NewGuid(),
            CalibrationOrchestrationId = Guid.NewGuid(),
            CalibrationManifestSha256 = new string('9', 64),
        };
        seedCtx.GcodeFiles.Add(gcode);

        var printer = new Printer
        {
            Id = Guid.NewGuid(),
            Name = "HashPrinter",
            ServerUrl = $"http://h-{Guid.NewGuid():N}",
            ManufacturerId = mfr.Id,
            ModelId = mdl.Id,
            IsEnabled = true,
            IsAvailable = true,
            InMaintenance = false,
        };
        seedCtx.Printers.Add(printer);
        seedCtx.PrinterDispatchStates.Add(new PrinterDispatchState { PrinterId = printer.Id });

        var job = new PrintJob
        {
            Id = Guid.NewGuid(),
            Name = "hash-job",
            GcodeFileId = gcode.Id,
            GcodeFile = gcode,
            AssignedPrinterId = printer.Id,
            Status = PrintJobStatus.Assigned,
            Priority = (int)PrintJobPriority.Normal,
            GcodeContentSha256 = new string('b', 64), // ← MISMATCH with gcode.FileHash
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            QueuedAt = DateTime.UtcNow,
        };
        seedCtx.PrintJobs.Add(job);
        await seedCtx.SaveChangesAsync();

        await using AppDbContext claimCtx = CreateContext();
        var claimSvc = CreateClaimService(claimCtx);

        var result = await claimSvc.AcquireClaimAsync(
            new DispatchClaimRequest(job.Id, printer.Id, "actor", "Manual", null, null, null));

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be("gcode_hash_mismatch",
            "claim must fail if job.GcodeContentSha256 != gcode.FileHash");
    }

    [Fact]
    public async Task G3_ClaimPolicy_MissingCalibrationLineage_RejectsWithLineageIncomplete()
    {
        // Create a calibration job WITHOUT lineage IDs to test the lineage completeness check.
        // (We cannot modify immutable fields after creation, so we create the job without them.)
        await using AppDbContext seedCtx = CreateContext();
        await seedCtx.Database.EnsureCreatedAsync();

        var mfr = new Manufacturer { Id = Guid.NewGuid(), Name = "Mfr-Lin" };
        seedCtx.Manufacturers.Add(mfr);
        var mdl = new PrinterModel { Id = Guid.NewGuid(), ManufacturerId = mfr.Id, Name = "Mdl-Lin" };
        seedCtx.PrinterModels.Add(mdl);

        var gcode = new GcodeFile
        {
            Id = Guid.NewGuid(),
            Name = "lin.gcode",
            FileName = "lin.gcode",
            FileHash = new string('l', 64),
            FileSizeBytes = 1,
            FilePath = "/lin",

            // Promoted immutable calibration artifact — the only artifact a
            // calibration job may print (issue #900, defects 3 and 7).
            IsImmutable = true,
            PromotedAtUtc = DateTime.UtcNow.AddMinutes(-1),
            ContentSha256 = new string('l', 64),
            CalibrationProjectId = Guid.NewGuid(),
            CalibrationAttemptId = Guid.NewGuid(),
            CalibrationOrchestrationId = Guid.NewGuid(),
            CalibrationManifestSha256 = new string('9', 64),
        };
        seedCtx.GcodeFiles.Add(gcode);

        var printer = new Printer
        {
            Id = Guid.NewGuid(),
            Name = "LinPrinter",
            ServerUrl = $"http://lin-{Guid.NewGuid():N}",
            ManufacturerId = mfr.Id,
            ModelId = mdl.Id,
            IsEnabled = true,
            IsAvailable = true,
            InMaintenance = false,
            FirmwareFamily = PrinterFirmwareFamily.Klipper,
            GcodeDialect = PrinterGcodeDialect.Klipper,
            CalibrationSlicerEngine = "OrcaSlicer",
            CalibrationSlicerDistribution = "upstream",
            CalibrationSlicerVersion = "2.3.0",
            ConfigurationRevision = 1,
        };
        seedCtx.Printers.Add(printer);

        var ds = new PrinterDispatchState
        {
            PrinterId = printer.Id,
            AcknowledgedJobId = Guid.NewGuid(), // Will be corrected below
            AcknowledgementIdempotencyKey = "valid-ack-key",
            AcknowledgementExpiresAtUtc = DateTime.UtcNow.AddMinutes(15),
        };
        seedCtx.PrinterDispatchStates.Add(ds);

        var job = new PrintJob
        {
            Id = Guid.NewGuid(),
            Name = "lin-job",
            GcodeFileId = gcode.Id,
            GcodeFile = gcode,
            AssignedPrinterId = printer.Id,
            Status = PrintJobStatus.Assigned,
            Priority = (int)PrintJobPriority.Normal,
            JobKind = JobKind.FilamentCalibration,
            RequiredFirmwareFamily = PrinterFirmwareFamily.Klipper,
            RequiredGcodeDialect = PrinterGcodeDialect.Klipper,
            RequiredSlicerEngine = "OrcaSlicer",
            RequiredSlicerDistribution = "upstream",
            RequiredSlicerVersion = "2.3.0",
            PinnedPrinterConfigRevision = 1,
            GcodeContentSha256 = new string('l', 64),
            // Lineage IDs intentionally omitted (null) to test the lineage check.
            CalibrationProjectId = null,
            CalibrationAttemptId = null,
            CalibrationConfigSnapshotId = null,
            CalibrationOrchestrationId = null,
            SpecificationSha256 = new string('s', 64),
            MachineProfileSha256 = new string('m', 64),
            ProcessProfileSha256 = new string('p', 64),
            FilamentProfileSha256 = new string('f', 64),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            QueuedAt = DateTime.UtcNow,
        };
        seedCtx.PrintJobs.Add(job);

        // Point the ack to this job.
        ds.AcknowledgedJobId = job.Id;
        await seedCtx.SaveChangesAsync();

        await using AppDbContext claimCtx = CreateContext();
        var claimSvc = CreateClaimService(claimCtx, MakeOnlineIdleReader(printer.Id));

        var result = await claimSvc.AcquireClaimAsync(
            new DispatchClaimRequest(job.Id, printer.Id, "actor", "BedClear", "valid-ack-key", null, null));

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be("calibration_lineage_incomplete",
            "claim must fail when CalibrationProjectId and other lineage IDs are null");
    }

    [Fact]
    public async Task G3_ClaimPolicy_MissingProfileHashes_RejectsWithHashesIncomplete()
    {
        // Create a calibration job WITHOUT profile hashes to test the hash completeness check.
        await using AppDbContext seedCtx = CreateContext();
        await seedCtx.Database.EnsureCreatedAsync();

        var mfr = new Manufacturer { Id = Guid.NewGuid(), Name = "Mfr-Hash2" };
        seedCtx.Manufacturers.Add(mfr);
        var mdl = new PrinterModel { Id = Guid.NewGuid(), ManufacturerId = mfr.Id, Name = "Mdl-Hash2" };
        seedCtx.PrinterModels.Add(mdl);

        var gcode = new GcodeFile
        {
            Id = Guid.NewGuid(),
            Name = "hash2.gcode",
            FileName = "hash2.gcode",
            FileHash = new string('h', 64),
            FileSizeBytes = 1,
            FilePath = "/h2",

            // Promoted immutable calibration artifact — the only artifact a
            // calibration job may print (issue #900, defects 3 and 7).
            IsImmutable = true,
            PromotedAtUtc = DateTime.UtcNow.AddMinutes(-1),
            ContentSha256 = new string('h', 64),
            CalibrationProjectId = Guid.NewGuid(),
            CalibrationAttemptId = Guid.NewGuid(),
            CalibrationOrchestrationId = Guid.NewGuid(),
            CalibrationManifestSha256 = new string('9', 64),
        };
        seedCtx.GcodeFiles.Add(gcode);

        var printer = new Printer
        {
            Id = Guid.NewGuid(),
            Name = "Hash2Printer",
            ServerUrl = $"http://h2-{Guid.NewGuid():N}",
            ManufacturerId = mfr.Id,
            ModelId = mdl.Id,
            IsEnabled = true,
            IsAvailable = true,
            InMaintenance = false,
            FirmwareFamily = PrinterFirmwareFamily.Klipper,
            GcodeDialect = PrinterGcodeDialect.Klipper,
            CalibrationSlicerEngine = "OrcaSlicer",
            CalibrationSlicerDistribution = "upstream",
            CalibrationSlicerVersion = "2.3.0",
            ConfigurationRevision = 1,
        };
        seedCtx.Printers.Add(printer);

        var ds = new PrinterDispatchState
        {
            PrinterId = printer.Id,
            AcknowledgedJobId = Guid.NewGuid(), // Will be corrected below
            AcknowledgementIdempotencyKey = "valid-ack-key",
            AcknowledgementExpiresAtUtc = DateTime.UtcNow.AddMinutes(15),
        };
        seedCtx.PrinterDispatchStates.Add(ds);

        var job = new PrintJob
        {
            Id = Guid.NewGuid(),
            Name = "hash2-job",
            GcodeFileId = gcode.Id,
            GcodeFile = gcode,
            AssignedPrinterId = printer.Id,
            Status = PrintJobStatus.Assigned,
            Priority = (int)PrintJobPriority.Normal,
            JobKind = JobKind.FilamentCalibration,
            RequiredFirmwareFamily = PrinterFirmwareFamily.Klipper,
            RequiredGcodeDialect = PrinterGcodeDialect.Klipper,
            RequiredSlicerEngine = "OrcaSlicer",
            RequiredSlicerDistribution = "upstream",
            RequiredSlicerVersion = "2.3.0",
            PinnedPrinterConfigRevision = 1,
            GcodeContentSha256 = new string('h', 64),
            CalibrationProjectId = Guid.NewGuid(),
            CalibrationAttemptId = Guid.NewGuid(),
            CalibrationConfigSnapshotId = Guid.NewGuid(),
            CalibrationOrchestrationId = Guid.NewGuid(),
            // Profile hashes intentionally omitted (null) to test the hash completeness check.
            SpecificationSha256 = null,
            MachineProfileSha256 = null,
            ProcessProfileSha256 = null,
            FilamentProfileSha256 = null,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            QueuedAt = DateTime.UtcNow,
        };
        seedCtx.PrintJobs.Add(job);

        // Point the ack to this job.
        ds.AcknowledgedJobId = job.Id;
        await seedCtx.SaveChangesAsync();

        await using AppDbContext claimCtx = CreateContext();
        var claimSvc = CreateClaimService(claimCtx, MakeOnlineIdleReader(printer.Id));

        var result = await claimSvc.AcquireClaimAsync(
            new DispatchClaimRequest(job.Id, printer.Id, "actor", "BedClear", "valid-ack-key", null, null));

        result.Success.Should().BeFalse();
        result.ErrorCode.Should().Be("calibration_hashes_incomplete",
            "claim must fail when SpecificationSha256 and other profile hashes are null");
    }

    [Fact]
    public async Task G3_ClaimPolicy_FullValidJob_WithAck_Succeeds()
    {
        await using AppDbContext seedCtx = CreateContext();
        (Guid printerId, Guid jobId, _) = await SeedFullCalibrationJobAsync(seedCtx, setAck: true);

        await using AppDbContext claimCtx = CreateContext();
        var claimSvc = CreateClaimService(claimCtx, MakeOnlineIdleReader(printerId));

        var result = await claimSvc.AcquireClaimAsync(
            new DispatchClaimRequest(jobId, printerId, "actor", "BedClear", "valid-ack-key", null, null));

        result.Success.Should().BeTrue(
            "all claim policy checks must pass for a fully-configured calibration job with a valid ack");
        result.Attempt.Should().NotBeNull();

        await using AppDbContext verifyCtx = CreateContext();
        PrintJob? verifiedJob = await verifyCtx.PrintJobs.FindAsync(jobId);
        verifiedJob!.Status.Should().Be(PrintJobStatus.Starting, "successful claim must advance job to Starting");
    }

    // =========================================================================
    // G4: Calibration creation — idempotency ETag and replay semantics
    // =========================================================================

    [Fact]
    public async Task G4_CalibrationCreate_TerminalJobReplay_UniqueIndexPreventsRawDuplicate()
    {
        // The filtered unique index on (IdempotencyScope, IdempotencyKey) covers all
        // calibration jobs — including terminal ones. Raw duplicate inserts that bypass
        // the application layer are prevented at DB level.
        // Application-level replay is tested in CalibrationQueueIdempotencyTests.
        await using AppDbContext seedCtx = CreateContext();
        await seedCtx.Database.EnsureCreatedAsync();

        var mfr = new Manufacturer { Id = Guid.NewGuid(), Name = "Mfr-G4" };
        seedCtx.Manufacturers.Add(mfr);
        var mdl = new PrinterModel { Id = Guid.NewGuid(), ManufacturerId = mfr.Id, Name = "Mdl-G4" };
        seedCtx.PrinterModels.Add(mdl);

        var gcode = new GcodeFile
        {
            Id = Guid.NewGuid(),
            Name = "g4.gcode",
            FileName = "g4.gcode",
            FileSizeBytes = 1,
            FilePath = "/g4",
        };
        seedCtx.GcodeFiles.Add(gcode);
        await seedCtx.SaveChangesAsync();

        const string scope = "g4-scope";
        const string key = "g4-key";

        // Insert a TERMINAL calibration job (Completed) with the given key.
        await using AppDbContext ctx1 = CreateContext();
        ctx1.PrintJobs.Add(new PrintJob
        {
            Id = Guid.NewGuid(),
            Name = "terminal-g4-job",
            GcodeFileId = gcode.Id,
            Status = PrintJobStatus.Completed,
            JobKind = JobKind.FilamentCalibration,
            IdempotencyScope = scope,
            IdempotencyKey = key,
            Priority = (int)PrintJobPriority.Normal,
            CreatedAt = DateTime.UtcNow.AddHours(-1),
            UpdatedAt = DateTime.UtcNow.AddHours(-1),
            QueuedAt = DateTime.UtcNow.AddHours(-1),
        });
        await ctx1.SaveChangesAsync();

        // Attempting to raw-insert a new active job with the same scope+key must fail
        // at the DB level — the unique index covers all calibration jobs regardless of
        // their status, preventing bypasses of the application-layer replay logic.
        await using AppDbContext ctx2 = CreateContext();
        ctx2.PrintJobs.Add(new PrintJob
        {
            Id = Guid.NewGuid(),
            Name = "new-g4-job",
            GcodeFileId = gcode.Id,
            Status = PrintJobStatus.Queued,
            JobKind = JobKind.FilamentCalibration,
            IdempotencyScope = scope,
            IdempotencyKey = key,
            Priority = (int)PrintJobPriority.Normal,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            QueuedAt = DateTime.UtcNow,
        });

        Func<Task> rawDuplicateInsert = async () => await ctx2.SaveChangesAsync();
        await rawDuplicateInsert.Should().ThrowAsync<Exception>(
            "raw duplicate inserts with the same scope+key are prevented at DB level " +
            "regardless of terminal status — application replay logic must be the only allowed path");
    }

    [Fact]
    public async Task G4_CalibrationCreate_ActiveJobWithSameKey_ViolatesUniqueIndex()
    {
        await using AppDbContext seedCtx = CreateContext();
        await seedCtx.Database.EnsureCreatedAsync();

        var mfr = new Manufacturer { Id = Guid.NewGuid(), Name = "Mfr-G4B" };
        seedCtx.Manufacturers.Add(mfr);
        var mdl = new PrinterModel { Id = Guid.NewGuid(), ManufacturerId = mfr.Id, Name = "Mdl-G4B" };
        seedCtx.PrinterModels.Add(mdl);

        var gcode = new GcodeFile
        {
            Id = Guid.NewGuid(),
            Name = "g4b.gcode",
            FileName = "g4b.gcode",
            FileSizeBytes = 1,
            FilePath = "/g4b",
        };
        seedCtx.GcodeFiles.Add(gcode);
        await seedCtx.SaveChangesAsync();

        // Insert first ACTIVE calibration job.
        await using AppDbContext ctx1 = CreateContext();
        ctx1.PrintJobs.Add(new PrintJob
        {
            Id = Guid.NewGuid(),
            Name = "active-g4b-job-1",
            GcodeFileId = gcode.Id,
            Status = PrintJobStatus.Queued,
            JobKind = JobKind.FilamentCalibration,
            IdempotencyScope = "g4b-scope",
            IdempotencyKey = "g4b-key",
            Priority = (int)PrintJobPriority.Normal,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            QueuedAt = DateTime.UtcNow,
        });
        await ctx1.SaveChangesAsync();

        // Insert second ACTIVE job with same scope+key — must be rejected.
        await using AppDbContext ctx2 = CreateContext();
        ctx2.PrintJobs.Add(new PrintJob
        {
            Id = Guid.NewGuid(),
            Name = "active-g4b-job-2-DUPLICATE",
            GcodeFileId = gcode.Id,
            Status = PrintJobStatus.Queued,
            JobKind = JobKind.FilamentCalibration,
            IdempotencyScope = "g4b-scope",
            IdempotencyKey = "g4b-key",
            Priority = (int)PrintJobPriority.Normal,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            QueuedAt = DateTime.UtcNow,
        });

        Func<Task> duplicateInsert = async () => await ctx2.SaveChangesAsync();
        await duplicateInsert.Should().ThrowAsync<Exception>(
            "concurrent insert of an active calibration job with the same (scope, key) must fail at DB level");
    }

    // =========================================================================
    // G6: Typed backend outcomes — Unknown / ReconciliationRequired
    // =========================================================================

    [Fact]
    public async Task G6_RecordUnknownOutcome_MarksAttemptUnknown_RequiresReconciliation()
    {
        await using AppDbContext seedCtx = CreateContext();
        (Guid printerId, Guid jobId, _) = await SeedFullCalibrationJobAsync(seedCtx, setAck: true);

        // Acquire claim first.
        await using AppDbContext claimCtx = CreateContext();
        var claimSvc = CreateClaimService(claimCtx, MakeOnlineIdleReader(printerId));
        var claimResult = await claimSvc.AcquireClaimAsync(
            new DispatchClaimRequest(jobId, printerId, "actor", "BedClear", "valid-ack-key", null, null));
        claimResult.Success.Should().BeTrue();

        // Record unknown outcome (e.g., network timeout after sending to backend).
        await using AppDbContext unknownCtx = CreateContext();
        var unknownSvc = CreateClaimService(unknownCtx);
        await unknownSvc.RecordUnknownOutcomeAsync(
            claimResult.Attempt!.Id,
            "Network timeout after upload — outcome unknown",
            CancellationToken.None);

        // Verify: attempt is Unknown + RequiresReconciliation; job stays Starting.
        await using AppDbContext verifyCtx = CreateContext();
        QueueDispatchAttempt? attempt = await verifyCtx.QueueDispatchAttempts.FindAsync(claimResult.Attempt.Id);
        attempt.Should().NotBeNull();
        attempt!.Outcome.Should().Be(DispatchAttemptOutcome.Unknown,
            "timeout-after-send must remain Unknown, not FailedBeforeStart");
        attempt.RequiresReconciliation.Should().BeTrue(
            "Unknown outcome must set RequiresReconciliation=true for the reconciler");

        PrintJob? job = await verifyCtx.PrintJobs.FindAsync(jobId);
        job!.Status.Should().Be(PrintJobStatus.Starting,
            "job must remain in Starting state — not blindly re-queued — when outcome is unknown");
    }

    [Fact]
    public async Task G6_RecordKnownFailure_ReleasesLease_JobBackToAssigned_StartTimeCleared()
    {
        await using AppDbContext seedCtx = CreateContext();
        (Guid printerId, Guid jobId, _) = await SeedFullCalibrationJobAsync(seedCtx, setAck: true);

        // Acquire claim.
        await using AppDbContext claimCtx = CreateContext();
        var claimSvc = CreateClaimService(claimCtx, MakeOnlineIdleReader(printerId));
        var claimResult = await claimSvc.AcquireClaimAsync(
            new DispatchClaimRequest(jobId, printerId, "actor", "BedClear", "valid-ack-key", null, null));
        claimResult.Success.Should().BeTrue();

        // Record known failure (e.g., backend rejected the G-code file).
        await using AppDbContext failCtx = CreateContext();
        var failSvc = CreateClaimService(failCtx);
        await failSvc.ReleaseClaimOnKnownFailureAsync(
            claimResult.Attempt!.Id,
            "backend_rejected",
            "Backend rejected the G-code file.",
            CancellationToken.None);

        // Verify: job back to Assigned, ActualStartTime cleared, lease released.
        await using AppDbContext verifyCtx = CreateContext();
        PrintJob? job = await verifyCtx.PrintJobs.FindAsync(jobId);
        job!.Status.Should().Be(PrintJobStatus.Assigned,
            "known failure must return job to Assigned, not leave it in Starting");
        job.ActualStartTime.Should().BeNull("ActualStartTime must be cleared on known failure");

        QueueDispatchAttempt? attempt = await verifyCtx.QueueDispatchAttempts.FindAsync(claimResult.Attempt.Id);
        attempt!.Outcome.Should().Be(DispatchAttemptOutcome.FailedBeforeStart);
        attempt.ErrorCode.Should().Be("backend_rejected");
        attempt.IsRetryable.Should().BeTrue();

        PrinterDispatchState? ds = await verifyCtx.PrinterDispatchStates.SingleAsync(s => s.PrinterId == printerId);
        ds.ActiveJobId.Should().BeNull("lease must be released from dispatch state on known failure");
        ds.ActiveDispatchAttemptId.Should().BeNull();
    }

    // =========================================================================
    // G7: Terminal lease lifecycle — ack invalidated on job removal
    // =========================================================================

    [Fact]
    public async Task G7_RemoveJob_WithActiveAck_InvalidatesAck()
    {
        await using AppDbContext seedCtx = CreateContext();
        (Guid printerId, Guid jobId, Guid gcodeId) = await SeedFullCalibrationJobAsync(seedCtx, setAck: true);

        // Verify ack is set before removal.
        PrinterDispatchState ds = await seedCtx.PrinterDispatchStates.SingleAsync(s => s.PrinterId == printerId);
        ds.AcknowledgedJobId.Should().Be(jobId, "setup: ack must be set before removal");

        // Remove the job.
        await using AppDbContext removeCtx = CreateContext();
        PrintJob? job = await removeCtx.PrintJobs
            .Include(j => j.GcodeFile)
            .Include(j => j.AssignedPrinter)
            .FirstOrDefaultAsync(j => j.Id == jobId);
        job!.Status.Should().Be(PrintJobStatus.Assigned);

        var dataService = Mock.Of<IQueueDataService>(
            s => s.GetPrintJobByIdAsync(jobId, It.IsAny<CancellationToken>()) == Task.FromResult<PrintJob?>(job));

        var sut = new JobQueueService(
            new EfQueueRepository(removeCtx),
            dataService,
            NullLogger<JobQueueService>.Instance,
            db: removeCtx);

        bool removed = await sut.RemoveJobAsync(jobId, CancellationToken.None);
        removed.Should().BeTrue("job must be removed");

        // Verify ack is cleared.
        await using AppDbContext verifyCtx = CreateContext();
        PrinterDispatchState? verifyDs = await verifyCtx.PrinterDispatchStates.SingleAsync(s => s.PrinterId == printerId);
        verifyDs.AcknowledgedJobId.Should().BeNull("ack must be invalidated when the acknowledged job is removed");
        verifyDs.AcknowledgementIdempotencyKey.Should().BeNull();
        verifyDs.AcknowledgementExpiresAtUtc.Should().BeNull();
    }

    // =========================================================================
    // G8: Priority validation — undefined values rejected
    // =========================================================================

    [Fact]
    public async Task G8_PriorityUpdate_InvalidValue_ThrowsValidationException()
    {
        await using AppDbContext seedCtx = CreateContext();
        (Guid printerId, Guid jobId, _) = await SeedFullCalibrationJobAsync(seedCtx);

        await using AppDbContext editCtx = CreateContext();
        PrintJob? job = await editCtx.PrintJobs
            .Include(j => j.GcodeFile)
            .Include(j => j.AssignedPrinter)
            .FirstOrDefaultAsync(j => j.Id == jobId);

        var dataService = Mock.Of<IQueueDataService>(
            s => s.GetPrintJobByIdAsync(jobId, It.IsAny<CancellationToken>()) == Task.FromResult<PrintJob?>(job));

        var sut = new JobQueueService(
            new EfQueueRepository(editCtx),
            dataService,
            NullLogger<JobQueueService>.Instance,
            db: editCtx);

        // Priority = 99 is not a valid enum value.
        var updateRequest = new UpdateJobPriorityDto { Priority = 99 };

        Func<Task> act = async () => await sut.UpdateJobPriorityAsync(jobId, updateRequest, CancellationToken.None);
        await act.Should().ThrowAsync<ValidationException>(
            "priority value 99 is not a valid PrintJobPriority enum value and must be rejected");
    }

    [Fact]
    public async Task G8_PriorityUpdate_CalibrationPriorityIsImmutable()
    {
        await using AppDbContext seedCtx = CreateContext();
        (_, Guid jobId, _) = await SeedFullCalibrationJobAsync(seedCtx);

        await using AppDbContext editCtx = CreateContext();
        PrintJob? job = await editCtx.PrintJobs
            .Include(j => j.GcodeFile)
            .Include(j => j.AssignedPrinter)
            .FirstOrDefaultAsync(j => j.Id == jobId);

        var dataService = Mock.Of<IQueueDataService>(
            s => s.GetPrintJobByIdAsync(jobId, It.IsAny<CancellationToken>()) == Task.FromResult<PrintJob?>(job));

        var sut = new JobQueueService(
            new EfQueueRepository(editCtx),
            dataService,
            NullLogger<JobQueueService>.Instance,
            db: editCtx);

        // Priority = 3 (Urgent) is valid.
        var updateRequest = new UpdateJobPriorityDto { Priority = (int)PrintJobPriority.Urgent };
        Func<Task> act = async () =>
            await sut.UpdateJobPriorityAsync(jobId, updateRequest, CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>(
            "calibration priority is part of the canonical physical queue input");
    }

    // =========================================================================
    // G9: Outbox events carry schemaVersion/eventType/revisions
    // =========================================================================

    [Fact]
    public async Task G9_BedClearAck_OutboxEvent_HasRequiredMetadataFields()
    {
        await using AppDbContext seedCtx = CreateContext();
        (Guid printerId, Guid jobId, _) = await SeedFullCalibrationJobAsync(seedCtx);

        PrinterDispatchState ds = await seedCtx.PrinterDispatchStates.SingleAsync(s => s.PrinterId == printerId);
        PrintJob job = await seedCtx.PrintJobs.SingleAsync(candidate => candidate.Id == jobId);

        await using AppDbContext ackCtx = CreateContext();
        var ackSvc = CreateAckService(ackCtx);
        var result = await ackSvc.AcknowledgeAsync(
            new AcknowledgeBedClearRequest(
                jobId,
                printerId,
                "actor-g9",
                "ack-key-g9",
                ds.RowVersion,
                1,
                job.RowVersion));

        result.Outcome.Should().Be(BedClearAckOutcome.Accepted);

        await using AppDbContext verifyCtx = CreateContext();
        QueueDispatchOutbox? evt = await verifyCtx.QueueDispatchOutbox.SingleAsync(
            candidate =>
                candidate.EventType ==
                BedClearAcknowledgementService.BackendStartCommandEventType);

        // Verify required metadata fields (schemaVersion=1, eventType, non-zero sequence).
        evt.SchemaVersion.Should().Be("1", "outbox events must use schemaVersion 1");
        evt.EventType.Should().Be(BedClearAcknowledgementService.BackendStartCommandEventType,
            "outbox event type must be the canonical BackendStartCommand type");
        evt.Sequence.Should().BeGreaterThan(0, "outbox event must have a non-zero monotonic sequence");
        evt.AggregateId.Should().Be(jobId, "aggregate ID must match the print job ID");
        evt.AggregateType.Should().Be("PrintJob", "aggregate type must be PrintJob");
        evt.PrinterId.Should().Be(printerId, "printer ID must be present in the outbox event");
        evt.PayloadJson.Should().NotBeNullOrWhiteSpace("payload must be non-empty");
        evt.Status.Should().Be(QueueOutboxEventStatus.Pending, "new events must start in Pending state");
        evt.CreatedAtUtc.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));

        QueueDispatchOutbox acknowledged = await verifyCtx.QueueDispatchOutbox
            .SingleAsync(candidate =>
                candidate.EventType ==
                QueueLifecycleEventWriter.EventTypeBedClearAcknowledged);
        acknowledged.BedClearState.Should().Be("Acknowledged");
        acknowledged.BedClearCommandId.Should().NotBeNull();
        acknowledged.BedClearExpiresAtUtc.Should().NotBeNull();
        acknowledged.AttemptId.Should().BeNull();
    }

    [Fact]
    public async Task G9_ClaimOutboxEvent_HasRequiredMetadataFields()
    {
        await using AppDbContext seedCtx = CreateContext();
        (Guid printerId, Guid jobId, _) = await SeedFullCalibrationJobAsync(seedCtx, setAck: true);

        await using AppDbContext claimCtx = CreateContext();
        var claimSvc = CreateClaimService(claimCtx, MakeOnlineIdleReader(printerId));
        var claimResult = await claimSvc.AcquireClaimAsync(
            new DispatchClaimRequest(jobId, printerId, "actor", "BedClear", "valid-ack-key", null, null));
        claimResult.Success.Should().BeTrue();

        await using AppDbContext verifyCtx = CreateContext();
        QueueDispatchOutbox? claimEvent = await verifyCtx.QueueDispatchOutbox
            .FirstOrDefaultAsync(e => e.EventType == "PrintFarmer.Queue.JobDispatchStarted.v1");

        claimEvent.Should().NotBeNull("claim must write a JobDispatchStarted outbox event");
        claimEvent!.SchemaVersion.Should().Be("1");
        claimEvent.Sequence.Should().BeGreaterThan(0);
        claimEvent.AggregateId.Should().Be(jobId);
        claimEvent.PrinterId.Should().Be(printerId);
    }

    // =========================================================================
    // G10: ETag / If-Match — 412 on stale dispatch state
    // =========================================================================

    [Fact]
    public async Task G10_BedClearAck_StaleIfMatch_Returns412DispatchRevisionConflict()
    {
        await using AppDbContext seedCtx = CreateContext();
        (Guid printerId, Guid jobId, _) = await SeedFullCalibrationJobAsync(seedCtx);

        // Use a deliberately wrong (stale) dispatch state row version.
        byte[] staleRowVersion = [0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01];

        await using AppDbContext ackCtx = CreateContext();
        var ackSvc = CreateAckService(ackCtx);
        PrintJob job = await ackCtx.PrintJobs.SingleAsync(candidate => candidate.Id == jobId);
        var result = await ackSvc.AcknowledgeAsync(
            new AcknowledgeBedClearRequest(
                jobId, printerId, "actor", "ack-key-412",
                IfMatchDispatchState: staleRowVersion,
                ExpectedPrinterConfigRevision: null,
                IfMatchJob: job.RowVersion));

        result.Outcome.Should().Be(BedClearAckOutcome.DispatchRevisionConflict,
            "stale If-Match on dispatch state must return 412 DispatchRevisionConflict");
    }

    [Fact]
    public async Task G10_BedClearAck_MissingIfMatch_Returns428PreconditionRequired()
    {
        await using AppDbContext seedCtx = CreateContext();
        (Guid printerId, Guid jobId, _) = await SeedFullCalibrationJobAsync(seedCtx);

        await using AppDbContext ackCtx = CreateContext();
        var ackSvc = CreateAckService(ackCtx);
        PrintJob job = await ackCtx.PrintJobs.SingleAsync(candidate => candidate.Id == jobId);
        var result = await ackSvc.AcknowledgeAsync(
            new AcknowledgeBedClearRequest(
                jobId, printerId, "actor", "ack-key-428",
                IfMatchDispatchState: null,
                ExpectedPrinterConfigRevision: null,
                IfMatchJob: job.RowVersion));

        result.Outcome.Should().Be(BedClearAckOutcome.PreconditionRequired,
            "missing If-Match must return 428 PreconditionRequired");
    }

    [Fact]
    public async Task G10_BedClearAck_MissingIdempotencyKey_Returns428PreconditionRequired()
    {
        await using AppDbContext seedCtx = CreateContext();
        (Guid printerId, Guid jobId, _) = await SeedFullCalibrationJobAsync(seedCtx);

        await using AppDbContext ackCtx = CreateContext();
        var ackSvc = CreateAckService(ackCtx);
        var result = await ackSvc.AcknowledgeAsync(
            new AcknowledgeBedClearRequest(
                jobId, printerId, "actor",
                IdempotencyKey: "", // Missing key
                IfMatchDispatchState: [],
                ExpectedPrinterConfigRevision: null));

        result.Outcome.Should().Be(BedClearAckOutcome.PreconditionRequired,
            "missing Idempotency-Key must return 428 PreconditionRequired");
    }

    // =========================================================================
    // G5: Production paths — claim required before Starting
    // =========================================================================

    [Fact]
    public async Task G5_StandardJobClaim_RequiresNoAck_ButMustUseSharedClaimPath()
    {
        // Standard jobs must use the same IDispatchClaimService path — no bypass.
        await using AppDbContext seedCtx = CreateContext();
        await seedCtx.Database.EnsureCreatedAsync();

        var mfr = new Manufacturer { Id = Guid.NewGuid(), Name = "Mfr-G5" };
        seedCtx.Manufacturers.Add(mfr);
        var mdl = new PrinterModel { Id = Guid.NewGuid(), ManufacturerId = mfr.Id, Name = "Mdl-G5" };
        seedCtx.PrinterModels.Add(mdl);

        var gcode = new GcodeFile
        {
            Id = Guid.NewGuid(),
            Name = "std.gcode",
            FileName = "std.gcode",
            FileSizeBytes = 1,
            FilePath = "/std",
        };
        seedCtx.GcodeFiles.Add(gcode);

        var printer = new Printer
        {
            Id = Guid.NewGuid(),
            Name = "StdPrinter",
            ServerUrl = $"http://std-{Guid.NewGuid():N}",
            ManufacturerId = mfr.Id,
            ModelId = mdl.Id,
            IsEnabled = true,
            IsAvailable = true,
        };
        seedCtx.Printers.Add(printer);
        seedCtx.PrinterDispatchStates.Add(new PrinterDispatchState { PrinterId = printer.Id });

        var job = new PrintJob
        {
            Id = Guid.NewGuid(),
            Name = "std-job",
            GcodeFileId = gcode.Id,
            GcodeFile = gcode,
            AssignedPrinterId = printer.Id,
            Status = PrintJobStatus.Assigned,
            Priority = (int)PrintJobPriority.Normal,
            JobKind = JobKind.Standard, // Standard, not calibration
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            QueuedAt = DateTime.UtcNow,
        };
        seedCtx.PrintJobs.Add(job);
        await seedCtx.SaveChangesAsync();

        // Standard jobs do NOT require ack — claim with null ack key must succeed.
        await using AppDbContext claimCtx = CreateContext();
        var claimSvc = CreateClaimService(claimCtx, MakeOnlineIdleReader(printer.Id));

        var result = await claimSvc.AcquireClaimAsync(
            new DispatchClaimRequest(
                job.Id, printer.Id, "actor", "Manual",
                AcknowledgementIdempotencyKey: null, // No ack required for Standard
                ExpectedJobRowVersion: null,
                ExpectedDispatchStateRowVersion: null));

        result.Success.Should().BeTrue(
            "Standard jobs must be able to claim without an acknowledgement key");

        await using AppDbContext verifyCtx = CreateContext();
        PrintJob? claimedJob = await verifyCtx.PrintJobs.FindAsync(job.Id);
        claimedJob!.Status.Should().Be(PrintJobStatus.Starting,
            "successful claim must advance Standard job to Starting via the shared claim path");
    }

    [Theory]
    [InlineData("sku")]
    [InlineData("lot")]
    public async Task PhysicalSpool_SameMaterialSubstitution_AcknowledgementFailsClosed(
        string changedIdentity)
    {
        await using AppDbContext seed = CreateContext();
        (Guid printerId, Guid jobId, _) = await SeedFullCalibrationJobAsync(seed);
        Spool spool = await seed.Spools.SingleAsync(
            candidate => candidate.AssignedPrinterId == printerId);
        if (changedIdentity == "sku")
        {
            spool.Sku = "DIFFERENT-SKU";
        }
        else
        {
            spool.LotNumber = "DIFFERENT-LOT";
        }

        await seed.SaveChangesAsync();
        PrinterDispatchState state = await seed.PrinterDispatchStates
            .SingleAsync(candidate => candidate.PrinterId == printerId);
        PrintJob job = await seed.PrintJobs.SingleAsync(candidate => candidate.Id == jobId);

        await using AppDbContext ackContext = CreateContext();
        AcknowledgeBedClearResult result = await CreateAckService(ackContext)
            .AcknowledgeAsync(new AcknowledgeBedClearRequest(
                jobId,
                printerId,
                "operator",
                $"spool-{changedIdentity}",
                state.RowVersion,
                1,
                job.RowVersion));

        result.Outcome.Should().Be(BedClearAckOutcome.FilamentCheckFailed);
        (await ackContext.QueueDispatchOutbox.CountAsync(candidate =>
            candidate.EventType ==
            BedClearAcknowledgementService.BackendStartCommandEventType)).Should().Be(0);
    }

    [Theory]
    [InlineData(false, "Invalidated")]
    [InlineData(true, "Expired")]
    public async Task BedClearLifecycle_StaleOrExpired_EmitsTypedDurableEvent(
        bool expire,
        string expectedState)
    {
        await using AppDbContext seed = CreateContext();
        (Guid printerId, Guid jobId, _) = await SeedFullCalibrationJobAsync(seed);
        PrinterDispatchState state = await seed.PrinterDispatchStates
            .SingleAsync(candidate => candidate.PrinterId == printerId);
        PrintJob job = await seed.PrintJobs.SingleAsync(candidate => candidate.Id == jobId);
        await using (AppDbContext ackContext = CreateContext())
        {
            AcknowledgeBedClearResult accepted = await CreateAckService(ackContext)
                .AcknowledgeAsync(new AcknowledgeBedClearRequest(
                    jobId,
                    printerId,
                    "operator",
                    "lifecycle-key",
                    state.RowVersion,
                    1,
                    job.RowVersion));
            accepted.Outcome.Should().Be(BedClearAckOutcome.Accepted);
        }

        await using (AppDbContext mutate = CreateContext())
        {
            PrinterDispatchState current = await mutate.PrinterDispatchStates
                .SingleAsync(candidate => candidate.PrinterId == printerId);
            if (expire)
            {
                current.AcknowledgementExpiresAtUtc = DateTime.UtcNow.AddSeconds(-1);
                BedClearCommandRecord command = await mutate.BedClearCommandRecords.SingleAsync();
                command.ExpiresAtUtc = DateTime.UtcNow.AddSeconds(-1);
            }
            else
            {
                current.QueueRevision++;
            }

            await mutate.SaveChangesAsync();
        }

        await using AppDbContext lifecycleContext = CreateContext();
        await CreateAckService(lifecycleContext)
            .InvalidateStaleAcknowledgementsAsync(printerId);

        string expectedType = expire
            ? QueueLifecycleEventWriter.EventTypeBedClearExpired
            : QueueLifecycleEventWriter.EventTypeBedClearInvalidated;
        QueueDispatchOutbox lifecycle = await lifecycleContext.QueueDispatchOutbox
            .SingleAsync(candidate => candidate.EventType == expectedType);
        lifecycle.BedClearState.Should().Be(expectedState);
        lifecycle.BedClearCommandId.Should().NotBeNull();
        lifecycle.BedClearExpiresAtUtc.Should().NotBeNull();
        lifecycle.FailureRetryable.Should().BeFalse();
        lifecycle.FailureRequiresReconciliation.Should().BeFalse();
    }

    private static string ComputeSha256(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();
}
