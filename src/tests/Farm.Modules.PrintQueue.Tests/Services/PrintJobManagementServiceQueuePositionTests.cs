using System.Security.Claims;
using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Queue;
using Farm.Infrastructure.Services.AutoDispatch;
using Farm.Infrastructure.Services.Cameras;
using Farm.Infrastructure.Services.Cost;
using Farm.Infrastructure.Services.FileManagement;
using Farm.Infrastructure.Services.Interfaces;
using Farm.Infrastructure.Services.Notifications;
using Farm.Infrastructure.Services.OperatorFeatures;
using Farm.Infrastructure.Services.PartsInventory;
using Farm.Infrastructure.Services.Printers;
using Farm.Infrastructure.Services.Queue;
using Farm.Infrastructure.Services.Queue.Dispatch;
using Farm.Infrastructure.Services.SignalR;
using Farm.Infrastructure.Services.Spoolman;
using Farm.Infrastructure.Services.StorageManagement;
using Farm.Infrastructure.Telemetry;
using Farm.Modules.PrintQueue.Controllers;
using Farm.Modules.PrintQueue.Controllers.Requests;
using Farm.Modules.PrintQueue.Services.PrintQueue;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Farm.Modules.PrintQueue.Tests.Services;

public class PrintJobManagementServiceQueuePositionTests
{
    [Fact]
    public async Task GetPrinterQueue_ActiveBands_PreservesQueuedHeadAndPersistedRevisionsAsync()
    {
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync();
        await using AppDbContext db = await CreateContextAsync(connection);
        Guid printerId = await SeedPrinterAsync(db);
        Guid otherPrinterId = await SeedPrinterAsync(db);
        PrintJob starting = CreateJob(90, PrintJobPriority.Normal, printerId);
        starting.Status = PrintJobStatus.Starting;
        PrintJob printing = CreateJob(80, PrintJobPriority.High, printerId);
        printing.Status = PrintJobStatus.Printing;
        PrintJob paused = CreateJob(70, PrintJobPriority.Normal, printerId);
        paused.Status = PrintJobStatus.Paused;
        paused.QueuedAt = starting.QueuedAt.AddMinutes(1);
        PrintJob assigned = CreateJob(1, PrintJobPriority.Urgent, printerId);
        assigned.Status = PrintJobStatus.Assigned;
        PrintJob assignedTail = CreateJob(2, PrintJobPriority.Normal, printerId);
        assignedTail.Status = PrintJobStatus.Assigned;
        PrintJob queuedHead = CreateJob(10, PrintJobPriority.Normal, printerId);
        PrintJob queuedTail = CreateJob(20, PrintJobPriority.Normal, printerId);
        queuedTail.QueuedAt = queuedHead.QueuedAt.AddMinutes(-1);
        PrintJob completed = CreateJob(3, PrintJobPriority.Urgent, printerId);
        completed.Status = PrintJobStatus.Completed;
        PrintJob failed = CreateJob(4, PrintJobPriority.Urgent, printerId);
        failed.Status = PrintJobStatus.Failed;
        PrintJob cancelled = CreateJob(5, PrintJobPriority.Urgent, printerId);
        cancelled.Status = PrintJobStatus.Cancelled;
        PrintJob otherPrinterJob = CreateJob(1, PrintJobPriority.Urgent, otherPrinterId);
        db.PrintJobs.AddRange(
            starting, printing, paused, assigned, assignedTail, queuedHead, queuedTail,
            completed, failed, cancelled, otherPrinterJob);
        await db.SaveChangesAsync();
        var original = await db.PrintJobs.AsNoTracking().ToDictionaryAsync(
            job => job.Id,
            job => (job.QueuePosition, job.Priority, Version: ETag(job)));
        db.ChangeTracker.Clear();
        JobQueueAnalyticsController analytics = new(
            CreateService(db),
            Mock.Of<IJobCostCalculationService>(),
            NullLogger<JobQueueAnalyticsController>.Instance);
        Guid[] expected =
        [
            printing.Id, starting.Id, paused.Id, assigned.Id, assignedTail.Id,
            queuedHead.Id, queuedTail.Id,
        ];

        for (int limit = 1; limit <= expected.Length + 1; limit++)
        {
            var rows = Assert.IsType<List<Farm.Infrastructure.Dtos.PrintQueue.QueuedPrintJobDto>>(
                Assert.IsType<OkObjectResult>(await analytics.GetPrinterQueueAsync(
                    printerId.ToString(), limit)).Value);
            Assert.Equal(expected.Take(limit).Select(id => id.ToString()), rows.Select(row => row.Id));
            Assert.All(rows, row => Assert.Equal(original[Guid.Parse(row.Id)].Version, row.RowVersion));
        }
        var allRows = Assert.IsType<List<Farm.Infrastructure.Dtos.PrintQueue.QueuedPrintJobDto>>(
            Assert.IsType<OkObjectResult>(await analytics.GetPrinterQueueAsync(
                printerId.ToString())).Value);
        Assert.Equal(
            ["Assigned", "Paused", "Printing", "Queued", "Starting"],
            allRows.Select(row => row.Status).Distinct().OrderBy(status => status));
        Assert.Equal(queuedHead.Id.ToString(), allRows.First(row => row.Status == "Queued").Id);
        Assert.All(await db.PrintJobs.AsNoTracking().ToListAsync(), job =>
            Assert.Equal(original[job.Id], (job.QueuePosition, job.Priority, ETag(job))));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MoveQueuedJob_SelfNeighbor_Returns409WithoutChangingJobAsync(bool before)
    {
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync();
        await using AppDbContext db = await CreateContextAsync(connection);
        PrintJob job = CreateJob(1, PrintJobPriority.Normal);
        db.PrintJobs.Add(job);
        await db.SaveChangesAsync();
        string initialETag = ETag(job);
        JobQueueController controller = CreateQueueController(CreateService(db));
        controller.Request.Headers.IfMatch = $"\"{initialETag}\"";

        var result = await controller.MoveQueuedJobAsync(
            job.Id,
            new MoveQueuedJobRequest
            {
                BeforeJobId = before ? job.Id : null,
                BeforeJobETag = before ? initialETag : null,
                AfterJobId = before ? null : job.Id,
                AfterJobETag = before ? null : initialETag,
            });

        Assert.IsType<ConflictObjectResult>(result.Result);
        db.ChangeTracker.Clear();
        PrintJob persisted = await db.PrintJobs.SingleAsync();
        Assert.Equal(1, persisted.QueuePosition);
        Assert.Equal((int)PrintJobPriority.Normal, persisted.Priority);
        Assert.Equal(initialETag, ETag(persisted));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MoveQueuedJob_DifferentPrinterScopes_Returns409WithoutChangingEitherScopeAsync(bool before)
    {
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync();
        await using AppDbContext db = await CreateContextAsync(connection);
        Guid printerA = await SeedPrinterAsync(db);
        Guid printerB = await SeedPrinterAsync(db);
        PrintJob moved = CreateJob(1, PrintJobPriority.Normal, printerA);
        PrintJob sameScope = CreateJob(2, PrintJobPriority.Normal, printerA);
        PrintJob neighbor = CreateJob(1, PrintJobPriority.High, printerB);
        PrintJob otherScope = CreateJob(2, PrintJobPriority.High, printerB);
        db.PrintJobs.AddRange(moved, sameScope, neighbor, otherScope);
        await db.SaveChangesAsync();
        var original = await db.PrintJobs.AsNoTracking().ToDictionaryAsync(
            job => job.Id,
            job => (job.QueuePosition, job.Priority, Version: ETag(job)));
        JobQueueController controller = CreateQueueController(CreateService(db));
        controller.Request.Headers.IfMatch = $"\"{ETag(moved)}\"";

        var result = await controller.MoveQueuedJobAsync(
            moved.Id,
            new MoveQueuedJobRequest
            {
                BeforeJobId = before ? neighbor.Id : null,
                BeforeJobETag = before ? ETag(neighbor) : null,
                AfterJobId = before ? null : neighbor.Id,
                AfterJobETag = before ? null : ETag(neighbor),
            });

        Assert.IsType<ConflictObjectResult>(result.Result);
        db.ChangeTracker.Clear();
        Assert.All(await db.PrintJobs.AsNoTracking().ToListAsync(), job =>
            Assert.Equal(original[job.Id], (job.QueuePosition, job.Priority, ETag(job))));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MoveQueuedJob_RefetchedQueueEndpoint_MatchesReadyHeadAndDispatchedJobAsync(bool assignedScope)
    {
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync();
        await using AppDbContext db = await CreateContextAsync(connection);
        Guid printerId = await SeedPrinterAsync(db);
        Guid otherPrinterId = await SeedPrinterAsync(db);
        Printer printer = await db.Printers.SingleAsync(value => value.Id == printerId);
        printer.AutoDispatchEnabled = true;
        printer.IsAvailable = true;
        printer.CurrentSpoolId = 42;
        printer.DispatchState = new PrinterDispatchState
        {
            PrinterId = printerId,
            AutoDispatchState = AutoDispatchState.PendingReady,
        };
        Guid? scopeId = assignedScope ? printerId : null;
        PrintJob first = CreateJob(10, PrintJobPriority.Normal, scopeId);
        PrintJob moved = CreateJob(20, PrintJobPriority.Normal, scopeId);
        first.RequiredMaterialType = moved.RequiredMaterialType = "PLA";
        first.EstimatedFilamentUsage = moved.EstimatedFilamentUsage = 10;
        PrintJob unrelated = CreateJob(1, PrintJobPriority.Urgent, otherPrinterId);
        PrintJob unrelatedTail = CreateJob(2, PrintJobPriority.Low, otherPrinterId);
        db.PrintJobs.AddRange(first, moved, unrelated, unrelatedTail);
        await db.SaveChangesAsync();
        PrintJobManagementService service = CreateService(db);
        JobQueueAnalyticsController analytics = new(
            service,
            Mock.Of<IJobCostCalculationService>(),
            NullLogger<JobQueueAnalyticsController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
        var initial = Assert.IsType<List<Farm.Infrastructure.Dtos.PrintQueue.QueuedPrintJobWithFileMetaDto>>(
            Assert.IsType<OkObjectResult>(await analytics.GetAllQueueAsync("Queued", null, null)).Value);
        var movedDto = initial.Single(row => row.Job.Id == moved.Id.ToString()).Job;
        var neighborDto = initial.Single(row => row.Job.Id == first.Id.ToString()).Job;
        JobQueueController controller = CreateQueueController(service);
        controller.Request.Headers.IfMatch = $"\"{movedDto.RowVersion}\"";

        var moveResult = await controller.MoveQueuedJobAsync(
            moved.Id,
            new MoveQueuedJobRequest
            {
                BeforeJobId = first.Id,
                BeforeJobETag = neighborDto.RowVersion,
            });

        Assert.IsType<OkObjectResult>(moveResult.Result);
        db.ChangeTracker.Clear();
        foreach (string? statusFilter in new[] { null, "Queued" })
        {
            var refreshed = Assert.IsType<List<Farm.Infrastructure.Dtos.PrintQueue.QueuedPrintJobWithFileMetaDto>>(
                Assert.IsType<OkObjectResult>(await analytics.GetAllQueueAsync(statusFilter, null, null)).Value);
            Assert.Equal(
                [moved.Id.ToString(), first.Id.ToString()],
                refreshed.Where(row => row.Job.AssignedPrinterId == scopeId?.ToString())
                    .Select(row => row.Job.Id));
            var scopes = refreshed.Select(row => row.Job.AssignedPrinterId).ToList();
            int scopeGroups = 1 + scopes.Zip(scopes.Skip(1)).Count(pair => pair.First != pair.Second);
            Assert.Equal(scopes.Distinct().Count(), scopeGroups);
            if (!assignedScope)
            {
                Assert.Null(scopes[0]);
            }
            for (int offset = 0; offset < refreshed.Count; offset++)
            {
                var page = Assert.IsType<List<Farm.Infrastructure.Dtos.PrintQueue.QueuedPrintJobWithFileMetaDto>>(
                    Assert.IsType<OkObjectResult>(await analytics.GetAllQueueAsync(
                        statusFilter, null, null, limit: 1, offset: offset)).Value);
                Assert.Equal(refreshed[offset].Job.Id, Assert.Single(page).Job.Id);
            }
        }

        // The general reporting query still uses FIFO, not persisted scope position.
        List<PrintJob> reporting = await new EfPrintJobManagementRepository(db).GetFilteredJobsAsync(
            filterStatus: PrintJobStatus.Queued);
        Assert.Equal(
            [first.Id, moved.Id],
            reporting.Where(job => job.AssignedPrinterId == scopeId).Select(job => job.Id));

        Mock<IHubContext<PrinterHub>> hub = new();
        hub.Setup(value => value.Clients.Group(It.IsAny<string>()))
            .Returns(Mock.Of<IClientProxy>());
        Mock<IDispatchScorer> scorer = new();
        scorer.Setup(value => value.ScorePrintersForJobAsync(
                It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new DispatchScore(
                printerId, "Queue test printer", 100, new Dictionary<string, FactorScore>(), false, [])]);
        Mock<ISpoolmanService> spoolman = new();
        spoolman.Setup(value => value.GetSpoolByIdAsync(42, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SpoolmanSpoolDto(42, "PLA spool", "PLA", 1000, null, false));
        Mock<IJobDispatchService> dispatch = new();
        dispatch.Setup(value => value.DispatchReviewedJobAsync(
                moved.Id,
                printerId,
                QueueActorIdentity.AutoDispatch,
                It.IsAny<string>(),
                It.IsAny<byte[]>(),
                It.IsAny<FilamentOverrideAuthorization>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Farm.Infrastructure.Dtos.PrintQueue.QueuedPrintJobDto
            {
                Id = moved.Id.ToString(),
                AssignedPrinterId = printerId.ToString(),
                Status = nameof(PrintJobStatus.Printing),
                DispatchResult = new Farm.Infrastructure.Dtos.PrintQueue.DispatchAttemptResultDto
                {
                    Outcome = DispatchAttemptOutcome.Accepted,
                },
            });
        AutoDispatchService ready = new(
            db, hub.Object, NullLogger<AutoDispatchService>.Instance,
            spoolmanService: spoolman.Object, dispatchScorer: scorer.Object,
            jobDispatchService: dispatch.Object);

        Assert.Equal(moved.Id, (await ready.GetStatusAsync(printerId)).NextJobId);
        AutoDispatchReadyResult result = await ready.MarkReadyAsync(printerId);
        Assert.Equal(moved.Id, result.NextJob!.Id);
        Assert.True(result.DispatchInitiated);
        dispatch.VerifyAll();
    }

    [Fact]
    public async Task MoveQueuedJob_BeforeNeighbor_ReordersPositionsAndAdoptsPriorityAsync()
    {
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync();
        await using AppDbContext db = await CreateContextAsync(connection);

        PrintJob first = CreateJob(1, PrintJobPriority.High);
        PrintJob middle = CreateJob(2, PrintJobPriority.Normal);
        PrintJob moved = CreateJob(3, PrintJobPriority.Normal);
        db.PrintJobs.AddRange(first, middle, moved);
        await db.SaveChangesAsync();
        string movedETag = ETag(moved);
        string neighborETag = ETag(first);
        byte[] originalMovedRowVersion = moved.RowVersion!.ToArray();
        byte[] originalNeighborRowVersion = first.RowVersion!.ToArray();
        byte[] originalMiddleRowVersion = middle.RowVersion!.ToArray();
        db.ChangeTracker.Clear();

        Farm.Infrastructure.Dtos.PrintQueue.QueuedPrintJobDto result =
            await CreateService(db).MoveQueuedJobAsync(
                moved.Id,
                first.Id,
                null,
                movedETag,
                neighborETag,
                "operator");

        Assert.Equal(PrintJobPriority.High, result.Priority);
        List<PrintJob> jobs = await db.PrintJobs
            .OrderWithinScope()
            .ToListAsync();
        Assert.Equal([moved.Id, first.Id, middle.Id], jobs.Select(job => job.Id));
        Assert.Equal([1, 2, 3], jobs.Select(job => job.QueuePosition));
        Assert.NotEqual(originalMovedRowVersion, jobs[0].RowVersion);
        Assert.NotEqual(originalNeighborRowVersion, jobs[1].RowVersion);
        Assert.NotEqual(originalMiddleRowVersion, jobs[2].RowVersion);
    }

    [Fact]
    public async Task MoveQueuedJob_AssignedRowBetweenQueuedPositions_LeavesAssignedAndPrintingRowsFixedAsync()
    {
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync();
        await using AppDbContext db = await CreateContextAsync(connection);
        Guid printerId = await SeedPrinterAsync(db);
        PrintJob moved = CreateJob(1, PrintJobPriority.Normal, printerId);
        PrintJob assigned = CreateJob(2, PrintJobPriority.Normal, printerId);
        assigned.Status = PrintJobStatus.Assigned;
        PrintJob neighbor = CreateJob(3, PrintJobPriority.Normal, printerId);
        PrintJob printing = CreateJob(2, PrintJobPriority.Normal, printerId);
        printing.Status = PrintJobStatus.Printing;
        db.PrintJobs.AddRange(moved, assigned, neighbor, printing);
        await db.SaveChangesAsync();
        string movedETag = ETag(moved);
        string neighborETag = ETag(neighbor);
        byte[] assignedVersion = assigned.RowVersion!.ToArray();
        byte[] printingVersion = printing.RowVersion!.ToArray();

        await CreateService(db).MoveQueuedJobAsync(
            moved.Id,
            null,
            neighbor.Id,
            movedETag,
            neighborETag,
            "operator");

        Assert.Equal(2, assigned.QueuePosition);
        Assert.Equal(2, printing.QueuePosition);
        Assert.Equal(assignedVersion, assigned.RowVersion);
        Assert.Equal(printingVersion, printing.RowVersion);
        Assert.Equal(3, moved.QueuePosition);
        Assert.Equal(1, neighbor.QueuePosition);
        Assert.All(
            new[] { moved, neighbor },
            job => Assert.InRange(job.QueuePosition, 1, 3));
    }

    [Theory]
    [InlineData(PrintJobStatus.Assigned)]
    [InlineData(PrintJobStatus.Starting)]
    [InlineData(PrintJobStatus.Printing)]
    [InlineData(PrintJobStatus.Paused)]
    public async Task MoveQueuedJob_NonQueuedJob_ReturnsSemanticConflictAsync(PrintJobStatus status)
    {
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync();
        await using AppDbContext db = await CreateContextAsync(connection);
        PrintJob moved = CreateJob(1, PrintJobPriority.Normal);
        moved.Status = status;
        PrintJob neighbor = CreateJob(2, PrintJobPriority.Normal);
        db.PrintJobs.AddRange(moved, neighbor);
        await db.SaveChangesAsync();

        await Assert.ThrowsAsync<QueueSemanticConflictException>(
            () => CreateService(db).MoveQueuedJobAsync(
                moved.Id,
                neighbor.Id,
                null,
                ETag(moved),
                ETag(neighbor),
                "operator"));
    }

    [Fact]
    public async Task MoveQueuedJob_MissingMovedJobReturnsNotFoundAndMissingNeighborIsConflictAsync()
    {
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync();
        await using AppDbContext db = await CreateContextAsync(connection);
        PrintJob existing = CreateJob(1, PrintJobPriority.Normal);
        db.PrintJobs.Add(existing);
        await db.SaveChangesAsync();
        PrintJobManagementService service = CreateService(db);

        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => service.MoveQueuedJobAsync(
                Guid.NewGuid(),
                existing.Id,
                null,
                ETag(existing),
                ETag(existing),
                "operator"));
        await Assert.ThrowsAsync<QueueSemanticConflictException>(
            () => service.MoveQueuedJobAsync(
                existing.Id,
                Guid.NewGuid(),
                null,
                ETag(existing),
                ETag(existing),
                "operator"));
    }

    [Fact]
    public async Task MoveQueuedJob_StaleETagReturnsCurrentMovedAndNeighborETagsAsync()
    {
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync();
        await using AppDbContext db = await CreateContextAsync(connection);
        PrintJob moved = CreateJob(1, PrintJobPriority.Normal);
        PrintJob neighbor = CreateJob(2, PrintJobPriority.Normal);
        db.PrintJobs.AddRange(moved, neighbor);
        await db.SaveChangesAsync();
        string staleMovedETag = ETag(moved);
        string neighborETag = ETag(neighbor);
        moved.Name = "updated.gcode";
        await db.SaveChangesAsync();
        string currentMovedETag = ETag(moved);
        db.ChangeTracker.Clear();

        QueueRevisionConflictException exception =
            await Assert.ThrowsAsync<QueueRevisionConflictException>(
                () => CreateService(db).MoveQueuedJobAsync(
                    moved.Id,
                    neighbor.Id,
                    null,
                    staleMovedETag,
                    neighborETag,
                    "operator"));

        Assert.Equal(currentMovedETag, Convert.ToBase64String(exception.CurrentJobRowVersion!));
        Assert.Equal(neighborETag, Convert.ToBase64String(exception.CurrentNeighborRowVersion!));
    }

    [Fact]
    public async Task MoveQueuedJob_EnqueueAfterReorderUsesWatermarkWithoutCollisionAsync()
    {
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync();
        await using AppDbContext db = await CreateContextAsync(connection);
        Guid printerId = await SeedPrinterAsync(db);
        PrintJob first = CreateJob(1, PrintJobPriority.Normal, printerId);
        PrintJob moved = CreateJob(2, PrintJobPriority.Normal, printerId);
        PrintJob last = CreateJob(3, PrintJobPriority.Normal, printerId);
        db.PrintJobs.AddRange(first, moved, last);
        db.QueuePositionStates.Add(new QueuePositionState
        {
            ScopeId = printerId,
            NextPosition = 3,
        });
        await db.SaveChangesAsync();
        string movedETag = ETag(moved);
        string neighborETag = ETag(first);

        await CreateService(db).MoveQueuedJobAsync(
            moved.Id,
            first.Id,
            null,
            movedETag,
            neighborETag,
            "operator");

        Assert.Equal(3, (await db.QueuePositionStates.SingleAsync()).NextPosition);
        int nextPosition = await new QueuePositionAllocator(db).AllocateAsync(printerId);
        PrintJob enqueued = CreateJob(nextPosition, PrintJobPriority.Normal, printerId);
        db.PrintJobs.Add(enqueued);
        await db.SaveChangesAsync();

        Assert.Equal(4, nextPosition);
        Assert.Equal(4, await db.PrintJobs
            .Where(job => job.AssignedPrinterId == printerId)
            .Select(job => job.QueuePosition)
            .Distinct()
            .CountAsync());
    }

    [Fact]
    public async Task MoveQueuedJob_ConcurrentMoves_SecondWithStaleNeighborETagReturns412Async()
    {
        string databasePath = Path.Combine(Path.GetTempPath(), $"queue-position-{Guid.NewGuid():N}.db");
        try
        {
            DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite($"Data Source={databasePath};Default Timeout=30")
                .Options;
            Guid firstId;
            Guid secondId;
            Guid thirdId;
            string initialFirstETag;
            string initialSecondETag;
            string initialThirdETag;
            await using (AppDbContext seedDb = new(options))
            {
                await seedDb.Database.EnsureCreatedAsync();
                PrintJob first = CreateJob(1, PrintJobPriority.Normal);
                PrintJob second = CreateJob(2, PrintJobPriority.Normal);
                PrintJob third = CreateJob(3, PrintJobPriority.Normal);
                firstId = first.Id;
                secondId = second.Id;
                thirdId = third.Id;
                seedDb.PrintJobs.AddRange(first, second, third);
                await seedDb.SaveChangesAsync();
                initialFirstETag = ETag(first);
                initialSecondETag = ETag(second);
                initialThirdETag = ETag(third);
            }

            await using AppDbContext firstDb = new(options);
            await using AppDbContext secondDb = new(options);
            await CreateService(firstDb).MoveQueuedJobAsync(
                secondId,
                firstId,
                null,
                initialSecondETag,
                initialFirstETag,
                "operator");

            QueueRevisionConflictException exception =
                await Assert.ThrowsAsync<QueueRevisionConflictException>(
                    () => CreateService(secondDb).MoveQueuedJobAsync(
                        thirdId,
                        null,
                        firstId,
                        initialThirdETag,
                        initialFirstETag,
                        "operator"));

            Assert.NotNull(exception.CurrentJobRowVersion);
            Assert.NotEqual(initialFirstETag, Convert.ToBase64String(exception.CurrentNeighborRowVersion!));
            await using AppDbContext verifyDb = new(options);
            List<PrintJob> queue = await verifyDb.PrintJobs
                .OrderBy(job => job.QueuePosition)
                .ToListAsync();
            Assert.Equal(3, queue.Select(job => job.QueuePosition).Distinct().Count());
            Assert.Equal(3, queue.Count);
        }
        finally
        {
            if (File.Exists(databasePath))
            {
                File.Delete(databasePath);
            }
        }
    }

    private static async Task<AppDbContext> CreateContextAsync(SqliteConnection connection)
    {
        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;
        AppDbContext db = new(options);
        await db.Database.EnsureCreatedAsync();
        return db;
    }

    private static async Task<Guid> SeedPrinterAsync(AppDbContext db)
    {
        Guid manufacturerId = Guid.NewGuid();
        Guid modelId = Guid.NewGuid();
        Guid printerId = Guid.NewGuid();
        db.AddRange(
            new Manufacturer
            {
                Id = manufacturerId,
                Name = $"Queue test manufacturer {manufacturerId:N}",
            },
            new PrinterModel
            {
                Id = modelId,
                Name = "Queue test model",
                ManufacturerId = manufacturerId,
            },
            new Printer
            {
                Id = printerId,
                Name = "Queue test printer",
                ServerUrl = $"http://queue-test-{printerId:N}.local",
                BackendPort = 7125,
                Backend = (int)PrinterBackend.Moonraker,
                ManufacturerId = manufacturerId,
                ModelId = modelId,
            });
        await db.SaveChangesAsync();
        return printerId;
    }

    private static string ETag(PrintJob job) => Convert.ToBase64String(job.RowVersion ?? []);

    private static PrintJob CreateJob(
        int queuePosition,
        PrintJobPriority priority,
        Guid? printerId = null) =>
        new()
        {
            Id = Guid.NewGuid(),
            Name = $"job-{queuePosition}.gcode",
            Status = PrintJobStatus.Queued,
            Priority = (int)priority,
            AssignedPrinterId = printerId,
            QueuePosition = queuePosition,
            QueuedAt = DateTime.UtcNow.AddMinutes(queuePosition),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

    private static PrintJobManagementService CreateService(AppDbContext db)
    {
        Mock<IClientProxy> clientProxy = new();
        clientProxy.Setup(proxy => proxy.SendCoreAsync(
                "jobqueueupdate",
                It.IsAny<object?[]>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        Mock<IHubClients> clients = new();
        clients.Setup(value => value.Group(Farm.Infrastructure.Security.AuthorizedHubGroups.Farm))
            .Returns(clientProxy.Object);
        Mock<IHubContext<PrinterHub>> hub = new();
        hub.SetupGet(value => value.Clients).Returns(clients.Object);

        return new PrintJobManagementService(
            new EfPrintJobManagementRepository(db),
            NullLogger<PrintJobManagementService>.Instance,
            Mock.Of<IPrintersService>(),
            Mock.Of<IStoragePathService>(),
            hub.Object,
            Mock.Of<IStoredFileOperationsService>(),
            Mock.Of<IPrinterStatusCacheReader>(),
            notificationService: Mock.Of<INotificationService>(),
            retryService: null,
            printerStatusRefreshService: null,
            jobCostCalculationService: Mock.Of<IJobCostCalculationService>(),
            cameraSnapshotService: Mock.Of<ICameraSnapshotService>(),
            serviceScopeFactory: Mock.Of<IServiceScopeFactory>(),
            appDbContext: db);
    }

    private static JobQueueController CreateQueueController(IPrintJobManagementService service)
    {
        JobQueueController controller = new(
            Mock.Of<IJobQueueService>(),
            service,
            Mock.Of<IPrintJobCompletionService>(),
            Mock.Of<IJobDispatchService>(),
            Mock.Of<IBatchDispatchService>(),
            Mock.Of<IBedClearAcknowledgementService>(),
            Mock.Of<IPrinterStatusCacheReader>(),
            Mock.Of<IPrintFarmerTelemetryService>(),
            Mock.Of<IPartHarvestService>(),
            Mock.Of<IOperatorFeatureGate>(),
            NullLogger<JobQueueController>.Instance);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString())], "TestAuth")),
            },
        };
        return controller;
    }
}
