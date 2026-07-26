using System;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Farm.Slicer.Module.Tests.Slicing;

public class SliceJobTimeoutRecoveryTests
{
    private SlicerDbContext CreateInMemoryContext()
    {
        DbContextOptions<SlicerDbContext> options = new DbContextOptionsBuilder<SlicerDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new SlicerDbContext(options);
    }

    [Fact]
    public async Task RenewLeaseAsync_ExtendsLease()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        DbContextOptions<SlicerDbContext> options = new DbContextOptionsBuilder<SlicerDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new SlicerDbContext(options);
        _ = await db.Database.EnsureCreatedAsync();
        EfSliceJobRepository repo = new EfSliceJobRepository(db);
        Guid workerId = Guid.NewGuid();

        SliceJob job = new SliceJob
        {
            Id = Guid.NewGuid(),
            Status = SliceJobStatus.Processing,
            WorkerId = workerId,
            ClaimedAt = DateTime.UtcNow,
            LeaseExpiresAt = DateTime.UtcNow.AddSeconds(10),
        };
        await repo.AddAsync(job);
        await repo.SaveChangesAsync();

        bool renewed = await repo.RenewLeaseAsync(job.Id, workerId, 300);

        SliceJob? reloaded = await db.Set<SliceJob>().AsNoTracking().SingleOrDefaultAsync(item => item.Id == job.Id);
        Assert.True(renewed);
#pragma warning disable CS8602 // Dereference of a possibly null reference.
        _ = Assert.NotNull(reloaded.LeaseExpiresAt);
#pragma warning restore CS8602 // Dereference of a possibly null reference.
        Assert.True(reloaded.LeaseExpiresAt > DateTime.UtcNow.AddSeconds(200));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(SliceJob.MinimumLeaseDurationSeconds - 1)]
    [InlineData(SliceJob.MaximumLeaseDurationSeconds + 1)]
    public async Task RenewLeaseAsync_InvalidDuration_Throws(int leaseDurationSeconds)
    {
        await using SlicerDbContext db = CreateInMemoryContext();
        var repo = new EfSliceJobRepository(db);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => repo.RenewLeaseAsync(Guid.NewGuid(), Guid.NewGuid(), leaseDurationSeconds));
    }

    [Fact]
    public async Task RenewLeaseAsync_RejectsExpiredAndNonProcessingLeases()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        DbContextOptions<SlicerDbContext> options = new DbContextOptionsBuilder<SlicerDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new SlicerDbContext(options);
        _ = await db.Database.EnsureCreatedAsync();
        var repo = new EfSliceJobRepository(db);
        Guid workerId = Guid.NewGuid();
        var expired = new SliceJob
        {
            Id = Guid.NewGuid(),
            Status = SliceJobStatus.Processing,
            WorkerId = workerId,
            LeaseExpiresAt = DateTime.UtcNow.AddSeconds(-1),
        };
        var completed = new SliceJob
        {
            Id = Guid.NewGuid(),
            Status = SliceJobStatus.Completed,
            WorkerId = workerId,
            LeaseExpiresAt = DateTime.UtcNow.AddMinutes(1),
        };
        await repo.AddAsync(expired);
        await repo.AddAsync(completed);
        await repo.SaveChangesAsync();

        bool expiredRenewed = await repo.RenewLeaseAsync(expired.Id, workerId, 300);
        bool completedRenewed = await repo.RenewLeaseAsync(completed.Id, workerId, 300);

        Assert.False(expiredRenewed);
        Assert.False(completedRenewed);
    }

    [Fact]
    public async Task IncrementRetryAndRequeueAsync_RequeuesOrFails()
    {
        await using SlicerDbContext db = CreateInMemoryContext();
        EfSliceJobRepository repo = new EfSliceJobRepository(db);

        SliceJob job = new SliceJob { Id = Guid.NewGuid(), Status = SliceJobStatus.Processing, RetryCount = 0 };
        await repo.AddAsync(job);
        await repo.SaveChangesAsync();

        // First retry should requeue
        await repo.IncrementRetryAndRequeueAsync(job.Id, maxRetries: 3);
        SliceJob? j1 = await db.Set<SliceJob>().FindAsync(job.Id);
#pragma warning disable CS8602 // Dereference of a possibly null reference.
        Assert.Equal(SliceJobStatus.Queued, j1.Status);
#pragma warning restore CS8602 // Dereference of a possibly null reference.
        Assert.Equal(1, j1.RetryCount);

        // Exceed max retries -> marked failed
        j1.RetryCount = 3;
        j1.Status = SliceJobStatus.Processing;
        await repo.SaveChangesAsync();

        await repo.IncrementRetryAndRequeueAsync(job.Id, maxRetries: 3);
        SliceJob? j2 = await db.Set<SliceJob>().FindAsync(job.Id);
#pragma warning disable CS8602 // Dereference of a possibly null reference.
        Assert.Equal(SliceJobStatus.Failed, j2.Status);
#pragma warning restore CS8602 // Dereference of a possibly null reference.
    }
}
