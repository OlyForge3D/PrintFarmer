using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Queue;
using Farm.Infrastructure.Services.Queue.Dispatch;
using Farm.Infrastructure.Services.SignalR;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Farm.Web.Api.Tests.Dispatch;

public sealed class PriorityQueueOrderingTests
{
    [Fact]
    public async Task SaveChangesAsync_ExplicitLowPriority_PreservesZeroInsteadOfDatabaseDefault()
    {
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync();
        await using AppDbContext db = await CreateContextAsync(connection);
        PrintJob job = CreateJob(
            PrintJobPriority.Low,
            new DateTime(2026, 8, 4, 12, 0, 0, DateTimeKind.Utc),
            queuePosition: 1);

        db.PrintJobs.Add(job);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        PrintJob persisted = await db.PrintJobs.SingleAsync(candidate => candidate.Id == job.Id);
        Assert.Equal((int)PrintJobPriority.Low, persisted.Priority);
    }

    [Fact]
    public async Task BatchDispatchAsync_QueuedPriorities_ProcessesUrgentFirstAndLowLast()
    {
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync();
        await using AppDbContext db = await CreateContextAsync(connection);
        DateTime queuedAt = new(2026, 8, 4, 12, 0, 0, DateTimeKind.Utc);
        List<PrintJob> jobs =
        [
            CreateJob(PrintJobPriority.Low, queuedAt, queuePosition: 1),
            CreateJob(PrintJobPriority.Normal, queuedAt.AddMinutes(1), queuePosition: 2),
            CreateJob(PrintJobPriority.High, queuedAt.AddMinutes(2), queuePosition: 3),
            CreateJob(PrintJobPriority.Urgent, queuedAt.AddMinutes(3), queuePosition: 4),
        ];
        await SeedAsync(db, jobs);

        BatchDispatchResult result = await CreateBatchDispatchService(db)
            .BatchDispatchAsync(CreateRequest(jobs), "operator");

        Assert.Equal(
            [
                jobs[3].Id,
                jobs[2].Id,
                jobs[1].Id,
                jobs[0].Id,
            ],
            result.Results.Select(item => item.JobId));
    }

    [Fact]
    public async Task BatchDispatchAsync_TwoUrgentJobs_ProcessesOldestQueuedJobFirst()
    {
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync();
        await using AppDbContext db = await CreateContextAsync(connection);
        DateTime queuedAt = new(2026, 8, 4, 12, 0, 0, DateTimeKind.Utc);
        PrintJob older = CreateJob(PrintJobPriority.Urgent, queuedAt, queuePosition: 2);
        PrintJob newer = CreateJob(PrintJobPriority.Urgent, queuedAt.AddMinutes(1), queuePosition: 1);
        List<PrintJob> jobs = [newer, older];
        await SeedAsync(db, jobs);

        BatchDispatchResult result = await CreateBatchDispatchService(db)
            .BatchDispatchAsync(CreateRequest(jobs), "operator");

        Assert.Equal([older.Id, newer.Id], result.Results.Select(item => item.JobId));
    }

    [Fact]
    public async Task GetFilteredJobsAsync_SameJobsAsBatchDispatch_UsesIdenticalOrder()
    {
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync();
        await using AppDbContext db = await CreateContextAsync(connection);
        DateTime queuedAt = new(2026, 8, 4, 12, 0, 0, DateTimeKind.Utc);
        List<PrintJob> jobs =
        [
            CreateJob(PrintJobPriority.Normal, queuedAt.AddMinutes(2), queuePosition: 1),
            CreateJob(PrintJobPriority.Urgent, queuedAt.AddMinutes(1), queuePosition: 3),
            CreateJob(PrintJobPriority.Urgent, queuedAt, queuePosition: 4),
            CreateJob(PrintJobPriority.Low, queuedAt.AddMinutes(3), queuePosition: 2),
        ];
        await SeedAsync(db, jobs);

        EfPrintJobManagementRepository repository = new(db);
        List<PrintJob> displayJobs = await repository.GetFilteredJobsAsync(
            filterStatus: PrintJobStatus.Queued);
        BatchDispatchResult dispatchResult = await CreateBatchDispatchService(db)
            .BatchDispatchAsync(CreateRequest(jobs), "operator");

        Assert.Equal(
            dispatchResult.Results.Select(item => item.JobId),
            displayJobs.Select(job => job.Id));
    }

    private static async Task<AppDbContext> CreateContextAsync(SqliteConnection connection)
    {
        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;
        var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();
        return db;
    }

    private static PrintJob CreateJob(
        PrintJobPriority priority,
        DateTime queuedAt,
        int queuePosition)
    {
        return new PrintJob
        {
            Id = Guid.NewGuid(),
            RowVersion = Guid.NewGuid().ToByteArray(),
            Name = priority.ToString(),
            Status = PrintJobStatus.Queued,
            Priority = (int)priority,
            QueuePosition = queuePosition,
            QueuedAt = queuedAt,
            CreatedAt = queuedAt,
            UpdatedAt = queuedAt,
        };
    }

    private static async Task SeedAsync(AppDbContext db, IReadOnlyCollection<PrintJob> jobs)
    {
        DispatchSettings? settings = await db.DispatchSettings.SingleOrDefaultAsync();
        if (settings is null)
        {
            db.DispatchSettings.Add(new DispatchSettings
            {
                AutoDispatchEnabled = true,
                AutoDispatchMode = AutoDispatchMode.Auto,
                MaxConcurrentDispatches = jobs.Count,
            });
        }
        else
        {
            settings.AutoDispatchEnabled = true;
            settings.AutoDispatchMode = AutoDispatchMode.Auto;
            settings.MaxConcurrentDispatches = jobs.Count;
        }

        db.PrintJobs.AddRange(jobs);
        await db.SaveChangesAsync();
    }

    private static BatchDispatchRequest CreateRequest(IEnumerable<PrintJob> jobs)
    {
        return new BatchDispatchRequest
        {
            DispatchAll = true,
            JobETags = jobs.ToDictionary(
                job => job.Id,
                job => Convert.ToBase64String(job.RowVersion ?? [])),
        };
    }

    private static BatchDispatchService CreateBatchDispatchService(AppDbContext db)
    {
        Mock<IDispatchScorer> scorer = new();
        scorer.Setup(candidate => candidate.ScorePrintersForJobAsync(
                It.IsAny<Guid>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        Mock<IClientProxy> client = new();
        client.Setup(proxy => proxy.SendCoreAsync(
                It.IsAny<string>(),
                It.IsAny<object?[]>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        Mock<IHubClients> clients = new();
        clients.Setup(candidate => candidate.Group(It.IsAny<string>()))
            .Returns(client.Object);

        Mock<IHubContext<PrinterHub>> hub = new();
        hub.SetupGet(candidate => candidate.Clients)
            .Returns(clients.Object);

        return new BatchDispatchService(
            scorer.Object,
            Mock.Of<IJobDispatchService>(),
            db,
            hub.Object,
            NullLogger<BatchDispatchService>.Instance);
    }
}
