using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Queue;
using Farm.Infrastructure.Services.Cameras;
using Farm.Infrastructure.Services.Cost;
using Farm.Infrastructure.Services.FileManagement;
using Farm.Infrastructure.Services.Interfaces;
using Farm.Infrastructure.Services.Notifications;
using Farm.Infrastructure.Services.Printers;
using Farm.Infrastructure.Services.Queue;
using Farm.Infrastructure.Services.Queue.Dispatch;
using Farm.Infrastructure.Services.SignalR;
using Farm.Infrastructure.Services.StorageManagement;
using Farm.Modules.PrintQueue.Services.PrintQueue;
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

    [Fact]
    public async Task MoveQueuedJob_NonQueuedJob_ReturnsSemanticConflictAsync()
    {
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync();
        await using AppDbContext db = await CreateContextAsync(connection);
        PrintJob moved = CreateJob(1, PrintJobPriority.Normal);
        moved.Status = PrintJobStatus.Printing;
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
                Name = "Queue test manufacturer",
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
        Guid? printerId = null) => new()
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
            Mock.Of<IPrintJobManagementRepository>(),
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
}
