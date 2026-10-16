using System.Text.Json;
using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Dtos.PrintQueue;
using Farm.Infrastructure.Repositories.Queue;
using Farm.Infrastructure.Services.Cameras;
using Farm.Infrastructure.Services.Cost;
using Farm.Infrastructure.Services.FileManagement;
using Farm.Infrastructure.Services.Interfaces;
using Farm.Infrastructure.Services.Notifications;
using Farm.Infrastructure.Services.Printers;
using Farm.Infrastructure.Services.Queue;
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

/// <summary>
/// #3296: non-membership queue mutations must enqueue a durable outbox row in the same
/// transaction as the mutation so the dashboard can drop its 10s polling and rely on the
/// <c>queueevent</c> SignalR stream instead.
/// </summary>
public class PrintJobManagementServiceJobUpdatedOutboxTests
{
    [Fact]
    public async Task UpdateJobPriority_EnqueuesJobUpdatedRowWithPriorityChangeKindAsync()
    {
        await using SqliteConnection connection = await OpenAsync();
        await using AppDbContext db = await CreateContextAsync(connection);
        PrintJob job = CreateJob(1, PrintJobPriority.Normal);
        db.PrintJobs.Add(job);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await CreateService(db).UpdateJobPriorityAsync(
            job.Id.ToString(),
            PrintJobPriority.High,
            "operator");

        QueueDispatchOutbox row = await SingleOutboxRowAsync(db);
        Assert.Equal(QueueLifecycleEventWriter.EventTypeJobUpdated, row.EventType);
        Assert.Equal(job.Id, row.AggregateId);
        Assert.Equal(QueueOutboxEventStatus.Pending, row.Status);
        Assert.Equal(nameof(PrintJobStatus.Queued), row.JobStatus);
        PrintJob persisted = await db.PrintJobs.AsNoTracking().SingleAsync(value => value.Id == job.Id);
        Assert.Equal(persisted.RowVersion, row.AggregateRowVersion);
        using JsonDocument payload = JsonDocument.Parse(row.PayloadJson!);
        Assert.Equal(
            QueueLifecycleEventWriter.JobChangeKindPriority,
            payload.RootElement.GetProperty("changeKind").GetString());
        Assert.Equal((int)PrintJobPriority.High, payload.RootElement.GetProperty("priority").GetInt32());
        Assert.Equal(job.Id, payload.RootElement.GetProperty("jobId").GetGuid());
    }

    [Fact]
    public async Task MoveQueuedJob_EnqueuesJobUpdatedRowWithReorderChangeKindAsync()
    {
        await using SqliteConnection connection = await OpenAsync();
        await using AppDbContext db = await CreateContextAsync(connection);
        PrintJob first = CreateJob(1, PrintJobPriority.Normal);
        PrintJob moved = CreateJob(2, PrintJobPriority.Normal);
        db.PrintJobs.AddRange(first, moved);
        await db.SaveChangesAsync();
        string movedETag = Convert.ToBase64String(moved.RowVersion ?? []);
        string neighborETag = Convert.ToBase64String(first.RowVersion ?? []);
        db.ChangeTracker.Clear();

        await CreateService(db).MoveQueuedJobAsync(
            moved.Id,
            first.Id,
            null,
            movedETag,
            neighborETag,
            "operator");

        QueueDispatchOutbox row = await SingleOutboxRowAsync(db);
        Assert.Equal(QueueLifecycleEventWriter.EventTypeJobUpdated, row.EventType);
        Assert.Equal(moved.Id, row.AggregateId);
        using JsonDocument payload = JsonDocument.Parse(row.PayloadJson!);
        Assert.Equal(
            QueueLifecycleEventWriter.JobChangeKindReorder,
            payload.RootElement.GetProperty("changeKind").GetString());
    }

    [Fact]
    public async Task UpdateJobDetails_EnqueuesJobUpdatedRowWithDetailsChangeKindAsync()
    {
        await using SqliteConnection connection = await OpenAsync();
        await using AppDbContext db = await CreateContextAsync(connection);
        PrintJob job = CreateJob(1, PrintJobPriority.Normal);
        db.PrintJobs.Add(job);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        QueuedPrintJobDto? result = await CreateService(db).UpdateJobDetailsAsync(
            job.Id.ToString(),
            new UpdateJobDetailsRequest { Name = "renamed.gcode" });

        Assert.NotNull(result);
        QueueDispatchOutbox row = await SingleOutboxRowAsync(db);
        Assert.Equal(QueueLifecycleEventWriter.EventTypeJobUpdated, row.EventType);
        Assert.Equal(job.Id, row.AggregateId);
        using JsonDocument payload = JsonDocument.Parse(row.PayloadJson!);
        Assert.Equal(
            QueueLifecycleEventWriter.JobChangeKindDetails,
            payload.RootElement.GetProperty("changeKind").GetString());
    }

    [Fact]
    public async Task UpdateJobNotes_EnqueuesJobUpdatedRowAsync()
    {
        await using SqliteConnection connection = await OpenAsync();
        await using AppDbContext db = await CreateContextAsync(connection);
        PrintJob job = CreateJob(1, PrintJobPriority.Normal);
        db.PrintJobs.Add(job);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        bool updated = await CreateService(db).UpdateJobNotesAsync(job.Id.ToString(), "check bed");

        Assert.True(updated);
        QueueDispatchOutbox row = await SingleOutboxRowAsync(db);
        Assert.Equal(QueueLifecycleEventWriter.EventTypeJobUpdated, row.EventType);
        Assert.Equal(job.Id, row.AggregateId);
    }

    [Fact]
    public async Task RerunJob_EnqueuesMembershipJobQueuedRowForNewJobAsync()
    {
        await using SqliteConnection connection = await OpenAsync();
        await using AppDbContext db = await CreateContextAsync(connection);
        PrintJob original = CreateJob(1, PrintJobPriority.Normal);
        original.Status = PrintJobStatus.Completed;
        db.PrintJobs.Add(original);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        QueuedPrintJobDto rerun = await CreateService(db).RerunJobAsync(original.Id.ToString(), "operator");

        QueueDispatchOutbox row = await SingleOutboxRowAsync(db);
        Assert.Equal(QueueLifecycleEventWriter.EventTypeJobQueued, row.EventType);
        Assert.NotEqual(original.Id, row.AggregateId);
        Assert.Equal(rerun.Id, row.AggregateId.ToString());
    }

    private static async Task<QueueDispatchOutbox> SingleOutboxRowAsync(AppDbContext db) =>
        Assert.Single(await db.QueueDispatchOutbox.AsNoTracking().ToListAsync());

    private static async Task<SqliteConnection> OpenAsync()
    {
        SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync();
        return connection;
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

    private static PrintJob CreateJob(int queuePosition, PrintJobPriority priority) =>
        new()
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
                It.IsAny<string>(),
                It.IsAny<object?[]>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        Mock<IHubClients> clients = new();
        clients.Setup(value => value.Group(It.IsAny<string>())).Returns(clientProxy.Object);
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
            appDbContext: db,
            outboxSequenceAllocator: new DbOutboxSequenceAllocator(),
            queuePositionAllocator: new QueuePositionAllocator(db));
    }
}
