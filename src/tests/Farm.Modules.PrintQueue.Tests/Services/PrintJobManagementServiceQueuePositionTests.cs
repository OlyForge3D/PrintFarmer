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
        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;
        await using AppDbContext db = new(options);
        await db.Database.EnsureCreatedAsync();

        PrintJob first = CreateJob(1, PrintJobPriority.High);
        PrintJob middle = CreateJob(2, PrintJobPriority.Normal);
        PrintJob moved = CreateJob(3, PrintJobPriority.Normal);
        db.PrintJobs.AddRange(first, middle, moved);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        PrintJobManagementService service = CreateService(db);
        Farm.Infrastructure.Dtos.PrintQueue.QueuedPrintJobDto result = await service.MoveQueuedJobAsync(
            moved.Id,
            first.Id,
            null,
            "operator");

        Assert.Equal(PrintJobPriority.High, result.Priority);
        List<PrintJob> jobs = await db.PrintJobs
            .OrderBy(job => job.QueuePosition)
            .ToListAsync();
        Assert.Equal([moved.Id, first.Id, middle.Id], jobs.Select(job => job.Id));
        Assert.Equal([1, 2, 3], jobs.Select(job => job.QueuePosition));
    }

    [Fact]
    public async Task MoveQueuedJob_NonQueuedJob_RejectsWithValidationErrorAsync()
    {
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync();
        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;
        await using AppDbContext db = new(options);
        await db.Database.EnsureCreatedAsync();
        PrintJob printing = CreateJob(1, PrintJobPriority.Normal);
        printing.Status = PrintJobStatus.Printing;
        PrintJob neighbor = CreateJob(2, PrintJobPriority.Normal);
        db.PrintJobs.AddRange(printing, neighbor);
        await db.SaveChangesAsync();

        PrintJobManagementService service = CreateService(db);

        await Assert.ThrowsAsync<System.ComponentModel.DataAnnotations.ValidationException>(
            () => service.MoveQueuedJobAsync(printing.Id, neighbor.Id, null, "operator"));
    }

    [Fact]
    public async Task MoveQueuedJob_MissingJobOrNeighbor_ReturnsNotFoundAsync()
    {
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync();
        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;
        await using AppDbContext db = new(options);
        await db.Database.EnsureCreatedAsync();
        PrintJob existing = CreateJob(1, PrintJobPriority.Normal);
        db.PrintJobs.Add(existing);
        await db.SaveChangesAsync();
        PrintJobManagementService service = CreateService(db);

        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => service.MoveQueuedJobAsync(Guid.NewGuid(), existing.Id, null, "operator"));
        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => service.MoveQueuedJobAsync(existing.Id, Guid.NewGuid(), null, "operator"));
    }

    [Fact]
    public async Task MoveQueuedJob_StaleNeighbor_ReturnsConflictAsync()
    {
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync();
        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;
        await using AppDbContext db = new(options);
        await db.Database.EnsureCreatedAsync();
        PrintJob moved = CreateJob(1, PrintJobPriority.Normal);
        PrintJob neighbor = CreateJob(2, PrintJobPriority.Normal);
        neighbor.Status = PrintJobStatus.Printing;
        db.PrintJobs.AddRange(moved, neighbor);
        await db.SaveChangesAsync();
        PrintJobManagementService service = CreateService(db);

        await Assert.ThrowsAsync<QueueSemanticConflictException>(
            () => service.MoveQueuedJobAsync(moved.Id, neighbor.Id, null, "operator"));
    }

    [Fact]
    public async Task MoveQueuedJob_ConcurrentMoves_PreserveUniqueQueuePositionsAsync()
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
            }

            await using AppDbContext firstDb = new(options);
            await using AppDbContext secondDb = new(options);
            PrintJobManagementService firstService = CreateService(firstDb);
            PrintJobManagementService secondService = CreateService(secondDb);

            await Task.WhenAll(
                firstService.MoveQueuedJobAsync(secondId, firstId, null, "operator"),
                secondService.MoveQueuedJobAsync(thirdId, null, firstId, "operator"));

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

    private static PrintJob CreateJob(int queuePosition, PrintJobPriority priority) => new()
    {
        Id = Guid.NewGuid(),
        Name = $"job-{queuePosition}.gcode",
        Status = PrintJobStatus.Queued,
        Priority = (int)priority,
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
