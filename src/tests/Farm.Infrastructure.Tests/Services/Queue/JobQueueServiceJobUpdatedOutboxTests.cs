using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Queue;
using Farm.Infrastructure.Services.Queue;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Farm.Infrastructure.Tests.Services.Queue;

/// <summary>
/// #3296: the generic job update path must enqueue an outbox row in the same transaction so
/// the Print Queue dashboard can rely on <c>queueevent</c> instead of polling.
/// </summary>
public class JobQueueServiceJobUpdatedOutboxTests
{
    [Fact]
    public async Task UpdateJobAsync_PriorityEdit_EnqueuesNonMembershipJobUpdatedRowAsync()
    {
        await using SqliteConnection connection = await OpenAsync();
        await using AppDbContext db = await CreateContextAsync(connection);
        PrintJob job = await SeedJobAsync(db, PrintJobStatus.Queued);

        _ = await CreateService(db).UpdateJobAsync(
            job.Id,
            new UpdatePrintJobStatusDto
            {
                Priority = PrintJobPriority.High,
                IfMatchJobRowVersion = Convert.ToBase64String(job.RowVersion!),
            },
            CancellationToken.None);

        QueueDispatchOutbox row = Assert.Single(await db.QueueDispatchOutbox.AsNoTracking().ToListAsync());
        Assert.Equal(QueueLifecycleEventWriter.EventTypeJobUpdated, row.EventType);
        Assert.Equal(job.Id, row.AggregateId);
        Assert.Equal(QueueOutboxEventStatus.Pending, row.Status);
        using JsonDocument payload = JsonDocument.Parse(row.PayloadJson!);
        Assert.Equal(
            QueueLifecycleEventWriter.JobChangeKindUpdate,
            payload.RootElement.GetProperty("changeKind").GetString());
        Assert.Equal((int)PrintJobPriority.High, payload.RootElement.GetProperty("priority").GetInt32());
    }

    [Fact]
    public async Task UpdateJobAsync_TerminalJobRequeued_EnqueuesMembershipJobQueuedRowAsync()
    {
        await using SqliteConnection connection = await OpenAsync();
        await using AppDbContext db = await CreateContextAsync(connection);
        PrintJob job = await SeedJobAsync(db, PrintJobStatus.Failed);

        _ = await CreateService(db).UpdateJobAsync(
            job.Id,
            new UpdatePrintJobStatusDto
            {
                Status = PrintJobStatus.Queued,
                IfMatchJobRowVersion = Convert.ToBase64String(job.RowVersion!),
            },
            CancellationToken.None);

        QueueDispatchOutbox row = Assert.Single(await db.QueueDispatchOutbox.AsNoTracking().ToListAsync());
        Assert.Equal(QueueLifecycleEventWriter.EventTypeJobQueued, row.EventType);
        Assert.Equal(job.Id, row.AggregateId);
    }

    private static async Task<PrintJob> SeedJobAsync(AppDbContext db, PrintJobStatus status)
    {
        PrintJob job = new()
        {
            Id = Guid.NewGuid(),
            Name = "job.gcode",
            Status = status,
            Priority = (int)PrintJobPriority.Normal,
            QueuePosition = 1,
            QueuedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        db.PrintJobs.Add(job);
        await db.SaveChangesAsync();
        return job;
    }

    private static JobQueueService CreateService(AppDbContext db)
    {
        Mock<IQueueDataService> data = new();
        data.Setup(value => value.GetPrintJobByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Returns((Guid id, CancellationToken ct) =>
                db.PrintJobs.FirstOrDefaultAsync(candidate => candidate.Id == id, ct));
        return new JobQueueService(
            new EfQueueRepository(db),
            data.Object,
            NullLogger<JobQueueService>.Instance,
            db: db,
            sequenceAllocator: new DbOutboxSequenceAllocator(),
            positionAllocator: new QueuePositionAllocator(db));
    }

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
}
