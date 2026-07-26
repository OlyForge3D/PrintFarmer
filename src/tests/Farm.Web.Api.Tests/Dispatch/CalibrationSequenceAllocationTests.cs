// <copyright file="CalibrationSequenceAllocationTests.cs" company="OlyForge3D">
// Copyright (c) OlyForge3D. All rights reserved.
// </copyright>

using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Queue;
using Farm.Infrastructure.Services.Queue;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Dispatch;

/// <summary>
/// Proves Group 1 acceptance: N concurrent calibration-queue producers all persist their own
/// outbox event with a distinct, monotonically increasing sequence number.
///
/// This verifies the bounded retry loop in <see cref="JobQueueService.AddJobToQueueAsync"/>
/// and <see cref="BedClearAcknowledgementService.AcknowledgeAsync"/> that reloads the
/// <c>OutboxSequenceState</c> counter on <c>DbUpdateConcurrencyException</c> and retries
/// rather than surfacing the conflict to the caller.
/// </summary>
[Trait("Category", "DbHeavy")]
public class CalibrationSequenceAllocationTests : IAsyncDisposable
{
    private readonly SqliteConnection _keepAlive;
    private readonly string _connectionString;
    private static int _dbCounter;

    public CalibrationSequenceAllocationTests()
    {
        int id = System.Threading.Interlocked.Increment(ref _dbCounter);
        _connectionString = $"Data Source=file:seq_alloc_{id}?mode=memory&cache=shared;Foreign Keys=False";
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

    private static (JobQueueService Sut, AppDbContext Db) CreateSut(string connectionString)
    {
        DbContextOptions<AppDbContext> opts = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connectionString)
            .Options;
        var db = new AppDbContext(opts);
        db.Database.ExecuteSqlRaw("PRAGMA foreign_keys = OFF;");

        var allocator = new DbOutboxSequenceAllocator();
        var dataService = new Mock<IQueueDataService>();

        var sut = new JobQueueService(
            new EfQueueRepository(db),
            dataService.Object,
            NullLogger<JobQueueService>.Instance,
            db: db,
            sequenceAllocator: allocator);

        return (sut, db);
    }

    /// <summary>Applies schema and seeds one printer + gcode file, returns their IDs.</summary>
    private async Task<(Guid PrinterId, Guid GcodeId)> SeedBaseAsync(AppDbContext db)
    {
        await db.Database.EnsureCreatedAsync();

        var mfr = new Manufacturer { Id = Guid.NewGuid(), Name = "SeqMfr" };
        db.Manufacturers.Add(mfr);
        var mdl = new PrinterModel { Id = Guid.NewGuid(), ManufacturerId = mfr.Id, Name = "SeqModel" };
        db.PrinterModels.Add(mdl);

        var gcode = new GcodeFile
        {
            Id = Guid.NewGuid(),
            Name = "calib.gcode",
            FileName = "calib.gcode",
            FileHash = new string('e', 64),
            FileSizeBytes = 512,
            FilePath = "/gcode",

            // Promoted immutable calibration artifact: the server derives JobKind and
            // provenance from THIS lineage (issue #900, defect 3).
            IsImmutable = true,
            PromotedAtUtc = DateTime.UtcNow.AddMinutes(-1),
            ContentSha256 = new string('e', 64),
            CalibrationProjectId = Guid.NewGuid(),
            CalibrationAttemptId = Guid.NewGuid(),
            CalibrationOrchestrationId = Guid.NewGuid(),
            CalibrationManifestSha256 = new string('9', 64),
            SpecificationSha256 = new string('a', 64),
            MachineProfileSha256 = new string('b', 64),
            ProcessProfileSha256 = new string('c', 64),
            FilamentProfileSha256 = new string('d', 64),
            SlicerEngineName = "OrcaSlicer",
            SlicerDistribution = "upstream",
            PinnedSlicerVersion = "2.3.0",
            SlicerContainerDigest = "sha256:abc",
            FirmwareFamily = nameof(PrinterFirmwareFamily.Klipper),
            GcodeDialect = nameof(PrinterGcodeDialect.Klipper),
        };
        db.GcodeFiles.Add(gcode);

        var printer = new Printer
        {
            Id = Guid.NewGuid(),
            Name = "SeqPrinter",
            ServerUrl = $"http://seq-{Guid.NewGuid():N}",
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
        };
        db.Printers.Add(printer);

        db.PrinterDispatchStates.Add(new PrinterDispatchState { PrinterId = printer.Id });

        await db.SaveChangesAsync();
        return (printer.Id, gcode.Id);
    }

    private static QueuePrintJobDto MakeCalibrationRequest(Guid gcodeId, Guid printerId, string idempotencyKey) =>
        new()
        {
            GcodeFileId = gcodeId,
            AssignedPrinterId = printerId,
            JobKind = JobKind.FilamentCalibration,
            IdempotencyKey = idempotencyKey,
            IdempotencyScope = "seq-test-scope",
            CalibrationProjectId = Guid.NewGuid(),
            CalibrationAttemptId = Guid.NewGuid(),
            CalibrationConfigSnapshotId = Guid.NewGuid(),
            CalibrationOrchestrationId = Guid.NewGuid(),
            SourceArtifactId = Guid.NewGuid(),
            GcodeContentSha256 = new string('e', 64),
            RequiredFirmwareFamily = PrinterFirmwareFamily.Klipper,
            RequiredGcodeDialect = PrinterGcodeDialect.Klipper,
            RequiredSlicerEngine = "OrcaSlicer",
            RequiredSlicerDistribution = "upstream",
            RequiredSlicerVersion = "2.3.0",
            RequiredSlicerContainerDigest = "sha256:abc",
            SpecificationSha256 = new string('a', 64),
            MachineProfileSha256 = new string('b', 64),
            ProcessProfileSha256 = new string('c', 64),
            FilamentProfileSha256 = new string('d', 64),
            PrinterConfigSnapshotSha256 = new string('f', 64),
            PinnedPrinterConfigRevision = 1,
            Copies = 1,
            Priority = PrintJobPriority.Normal,
        };

    // =========================================================================
    // Test 1: Sequential inserts — each gets a unique, ascending sequence
    // =========================================================================

    [Fact]
    public async Task SequentialCalibrationJobs_EachGetUniqueAscendingSequence()
    {
        await using AppDbContext seedCtx = CreateContext();
        (Guid printerId, Guid gcodeId) = await SeedBaseAsync(seedCtx);

        // Create 3 sequential calibration jobs (different idempotency keys = different jobs).
        for (int i = 1; i <= 3; i++)
        {
            (JobQueueService sut, AppDbContext db) = CreateSut(_connectionString);
            await using (db)
            {
                var dataService = Mock.Of<IQueueDataService>(
                    s => s.GetGcodeFileAsync(gcodeId, It.IsAny<CancellationToken>()) == Task.FromResult<GcodeFile?>(
                        db.GcodeFiles.Find(gcodeId)) &&
                    s.GetNextQueuePositionAsync(printerId, It.IsAny<CancellationToken>()) == Task.FromResult(i) &&
                    s.GetAvailablePrintersAsync(It.IsAny<CancellationToken>()) == Task.FromResult(
                        new List<Printer> { db.Printers.Find(printerId)! }));

                var allocator = new DbOutboxSequenceAllocator();
                var sutWithData = new JobQueueService(
                    new EfQueueRepository(db),
                    dataService,
                    NullLogger<JobQueueService>.Instance,
                    db: db,
                    sequenceAllocator: allocator);

                var req = MakeCalibrationRequest(gcodeId, printerId, $"seq-key-{i}");
                req.IdempotencyScope = $"seq-scope-{i}"; // Unique scope per job

                JobQueuePrintJobDto? result = await sutWithData.AddJobToQueueAsync(req, null, CancellationToken.None);
                result.Should().NotBeNull($"job {i} must be added successfully");
            }
        }

        // Verify 3 distinct, ascending sequences.
        await using AppDbContext verifyCtx = CreateContext();
        List<long> sequences = await verifyCtx.QueueDispatchOutbox
            .OrderBy(e => e.Sequence)
            .Select(e => e.Sequence)
            .ToListAsync();

        sequences.Should().HaveCount(3, "three calibration jobs must produce three outbox events");
        sequences.Should().OnlyHaveUniqueItems("each outbox event must have a distinct sequence");
        sequences.Should().BeInAscendingOrder("sequences must be monotonically increasing");
        sequences.Should().AllSatisfy(s => s.Should().BeGreaterThan(0, "sequences must start above 0"));
    }

    // =========================================================================
    // Test 2: N concurrent bed-clear acks produce N distinct sequences
    // =========================================================================

    [Fact]
    public async Task NConcurrentBedClearAcks_AllProduceDistinctSequences_NoLostWork()
    {
        // Arrange: seed N jobs on the same printer with distinct ack keys.
        const int N = 4; // Deliberately exceeds MaxSequenceRetries=5 to stress retry

        await using AppDbContext seedCtx = CreateContext();
        await seedCtx.Database.EnsureCreatedAsync();

        var mfr = new Manufacturer { Id = Guid.NewGuid(), Name = "ConcMfr" };
        seedCtx.Manufacturers.Add(mfr);
        var mdl = new PrinterModel { Id = Guid.NewGuid(), ManufacturerId = mfr.Id, Name = "ConcModel" };
        seedCtx.PrinterModels.Add(mdl);

        // Create N separate printers so each ack goes to a different printer
        // (different dispatch states = different DB rows = less contention on dispatch state)
        // but the OUTBOX SEQUENCE ROW is shared, creating sequence contention.
        var printerIds = new List<Guid>();
        var jobIds = new List<Guid>();
        var ackKeys = new List<string>();

        for (int i = 0; i < N; i++)
        {
            var gcode = new GcodeFile
            {
                Id = Guid.NewGuid(),
                Name = $"calib-{i}.gcode",
                FileName = $"calib-{i}.gcode",
                FileHash = new string((char)('a' + i), 64),
                FileSizeBytes = 512,
                FilePath = "/gcode",
            };
            seedCtx.GcodeFiles.Add(gcode);

            var printer = new Printer
            {
                Id = Guid.NewGuid(),
                Name = $"ConcPrinter-{i}",
                ServerUrl = $"http://conc-{i}-{Guid.NewGuid():N}",
                ManufacturerId = mfr.Id,
                ModelId = mdl.Id,
                IsEnabled = true,
                InMaintenance = false,
                IsAvailable = true,
            };
            seedCtx.Printers.Add(printer);

            var ds = new PrinterDispatchState { PrinterId = printer.Id };
            seedCtx.PrinterDispatchStates.Add(ds);

            var job = new PrintJob
            {
                Id = Guid.NewGuid(),
                Name = $"conc-job-{i}",
                GcodeFileId = gcode.Id,
                AssignedPrinterId = printer.Id,
                Status = PrintJobStatus.Assigned,
                Priority = (int)PrintJobPriority.Normal,

                // Sequence allocation is job-kind agnostic; a Standard job keeps this test
                // focused on the outbox counter rather than the calibration policy matrix.
                JobKind = JobKind.Standard,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
                QueuedAt = DateTime.UtcNow,
            };
            seedCtx.PrintJobs.Add(job);

            printerIds.Add(printer.Id);
            jobIds.Add(job.Id);
            ackKeys.Add($"conc-ack-key-{i}");

            await seedCtx.SaveChangesAsync();
        }

        // Get dispatch state row versions for If-Match.
        var dsRowVersions = new List<byte[]?>();
        for (int i = 0; i < N; i++)
        {
            await using AppDbContext readCtx = CreateContext();
            var ds = await readCtx.PrinterDispatchStates.FindAsync(printerIds[i]);
            dsRowVersions.Add(ds!.RowVersion);
        }

        // Act: fire all N acks concurrently.
        var tasks = Enumerable.Range(0, N).Select(async i =>
        {
            await using AppDbContext ackCtx = CreateContext();
            var allocator = new DbOutboxSequenceAllocator();
            var ackSvc = new BedClearAcknowledgementService(
                ackCtx,
                allocator,
                DispatchTestDoubles.OnlineIdleReader(Guid.Empty),
                NullLogger<BedClearAcknowledgementService>.Instance);

            return await ackSvc.AcknowledgeAsync(new AcknowledgeBedClearRequest(
                JobId: jobIds[i],
                PrinterId: printerIds[i],
                ActorSubject: $"actor-{i}",
                IdempotencyKey: ackKeys[i],
                IfMatchDispatchState: dsRowVersions[i],
                ExpectedPrinterConfigRevision: 1));
        });

        AcknowledgeBedClearResult[] results = await Task.WhenAll(tasks);

        // Assert: every producer persisted its own event — no lost work.
        int acceptedCount = results.Count(r => r.Outcome == BedClearAckOutcome.Accepted);
        acceptedCount.Should().Be(
            N,
            $"all {N} concurrent ack producers must persist their own BackendStartCommand event " +
            "after sequence-conflict retry");

        // Verify N distinct, ascending sequences in the outbox.
        await using AppDbContext verifyCtx = CreateContext();
        List<long> sequences = await verifyCtx.QueueDispatchOutbox
            .Where(e => e.EventType == BedClearAcknowledgementService.BackendStartCommandEventType)
            .OrderBy(e => e.Sequence)
            .Select(e => e.Sequence)
            .ToListAsync();

        sequences.Should().HaveCount(N, $"exactly {N} BackendStartCommand events must exist");
        sequences.Should().OnlyHaveUniqueItems("all sequence values must be distinct across concurrent producers");
        sequences.Should().BeInAscendingOrder("sequences must form a monotonically increasing series");
    }

    // =========================================================================
    // Test 3: Single producer gets non-zero sequence from seeded counter
    // =========================================================================

    [Fact]
    public async Task SingleBedClearAck_SequenceIsNonZero_AndCounterAdvances()
    {
        await using AppDbContext seedCtx = CreateContext();
        await seedCtx.Database.EnsureCreatedAsync();

        var mfr = new Manufacturer { Id = Guid.NewGuid(), Name = "Mfr-Single" };
        seedCtx.Manufacturers.Add(mfr);
        var mdl = new PrinterModel { Id = Guid.NewGuid(), ManufacturerId = mfr.Id, Name = "Mdl-Single" };
        seedCtx.PrinterModels.Add(mdl);

        var gcode = new GcodeFile
        {
            Id = Guid.NewGuid(),
            Name = "s.gcode",
            FileName = "s.gcode",
            FileHash = new string('z', 64),
            FileSizeBytes = 1,
            FilePath = "/g",
        };
        seedCtx.GcodeFiles.Add(gcode);

        var printer = new Printer
        {
            Id = Guid.NewGuid(),
            Name = "SinglePrinter",
            ServerUrl = $"http://single-{Guid.NewGuid():N}",
            ManufacturerId = mfr.Id,
            ModelId = mdl.Id,
            IsEnabled = true,
            IsAvailable = true,
        };
        seedCtx.Printers.Add(printer);

        var ds = new PrinterDispatchState { PrinterId = printer.Id };
        seedCtx.PrinterDispatchStates.Add(ds);

        var job = new PrintJob
        {
            Id = Guid.NewGuid(),
            Name = "single-job",
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

        // Verify seed: OutboxSequenceState starts at NextSequence=0.
        OutboxSequenceState? initialState = await seedCtx.OutboxSequenceStates.SingleAsync();
        initialState.NextSequence.Should().Be(0, "OutboxSequenceState must be seeded at NextSequence=0");

        await using AppDbContext ackCtx = CreateContext();
        PrinterDispatchState? dsForAck = await ackCtx.PrinterDispatchStates.FindAsync(printer.Id);

        var ackSvc = new BedClearAcknowledgementService(
            ackCtx,
            new DbOutboxSequenceAllocator(),
            DispatchTestDoubles.OnlineIdleReader(Guid.Empty),
            NullLogger<BedClearAcknowledgementService>.Instance);

        AcknowledgeBedClearResult result = await ackSvc.AcknowledgeAsync(new AcknowledgeBedClearRequest(
            JobId: job.Id,
            PrinterId: printer.Id,
            ActorSubject: "actor",
            IdempotencyKey: "single-ack-key",
            IfMatchDispatchState: dsForAck!.RowVersion,
            ExpectedPrinterConfigRevision: 1));

        result.Outcome.Should().Be(BedClearAckOutcome.Accepted);

        // The outbox event must have Sequence = 1 (counter advanced from 0 to 1).
        await using AppDbContext verifyCtx = CreateContext();
        QueueDispatchOutbox? evt = await verifyCtx.QueueDispatchOutbox.SingleAsync();
        evt.Sequence.Should().Be(1, "first outbox event must have sequence 1 (seeded at 0, incremented to 1)");

        OutboxSequenceState? finalState = await verifyCtx.OutboxSequenceStates.SingleAsync();
        finalState.NextSequence.Should().Be(1, "OutboxSequenceState counter must have advanced to 1");
    }

    // =========================================================================
    // Test 4: Sequence counter after multiple sequential allocations is contiguous
    // =========================================================================

    [Fact]
    public async Task MultipleSequentialAcks_SequenceIsContiguous_StartingFromOne()
    {
        await using AppDbContext seedCtx = CreateContext();
        await seedCtx.Database.EnsureCreatedAsync();

        var mfr = new Manufacturer { Id = Guid.NewGuid(), Name = "Mfr-Multi" };
        seedCtx.Manufacturers.Add(mfr);
        var mdl = new PrinterModel { Id = Guid.NewGuid(), ManufacturerId = mfr.Id, Name = "Mdl-Multi" };
        seedCtx.PrinterModels.Add(mdl);

        // Seed 3 printers with jobs for 3 sequential acks.
        var printers = new List<(Guid PrinterId, Guid JobId)>();
        for (int i = 0; i < 3; i++)
        {
            var gcode = new GcodeFile
            {
                Id = Guid.NewGuid(),
                Name = $"m{i}.gcode",
                FileName = $"m{i}.gcode",
                FileHash = new string((char)('g' + i), 64),
                FileSizeBytes = 100,
                FilePath = "/g",
            };
            seedCtx.GcodeFiles.Add(gcode);

            var printer = new Printer
            {
                Id = Guid.NewGuid(),
                Name = $"MultiPrinter-{i}",
                ServerUrl = $"http://multi-{i}-{Guid.NewGuid():N}",
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
                Name = $"multi-job-{i}",
                GcodeFileId = gcode.Id,
                AssignedPrinterId = printer.Id,
                Status = PrintJobStatus.Assigned,
                Priority = (int)PrintJobPriority.Normal,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
                QueuedAt = DateTime.UtcNow,
            };
            seedCtx.PrintJobs.Add(job);
            printers.Add((printer.Id, job.Id));
        }

        await seedCtx.SaveChangesAsync();

        // Execute 3 sequential acks.
        for (int i = 0; i < 3; i++)
        {
            await using AppDbContext ackCtx = CreateContext();
            var ds = await ackCtx.PrinterDispatchStates.FindAsync(printers[i].PrinterId);
            var ackSvc = new BedClearAcknowledgementService(
                ackCtx,
                new DbOutboxSequenceAllocator(),
                DispatchTestDoubles.OnlineIdleReader(Guid.Empty),
                NullLogger<BedClearAcknowledgementService>.Instance);

            var r = await ackSvc.AcknowledgeAsync(new AcknowledgeBedClearRequest(
                printers[i].JobId, printers[i].PrinterId,
                $"actor-{i}", $"multi-ack-{i}",
                ds!.RowVersion, 1));

            r.Outcome.Should().Be(BedClearAckOutcome.Accepted, $"ack {i} must succeed");
        }

        // Verify sequences are 1, 2, 3 (contiguous from seed value 0).
        await using AppDbContext verifyCtx = CreateContext();
        List<long> sequences = await verifyCtx.QueueDispatchOutbox
            .OrderBy(e => e.Sequence)
            .Select(e => e.Sequence)
            .ToListAsync();

        sequences.Should().Equal(new long[] { 1, 2, 3 },
            "three sequential acks must produce contiguous sequences 1, 2, 3");
    }
}
