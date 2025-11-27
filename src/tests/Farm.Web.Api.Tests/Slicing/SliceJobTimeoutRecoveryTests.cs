using System;
using System.Threading.Tasks;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Slicing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Farm.Web.Api.Tests.Slicing;

public class SliceJobTimeoutRecoveryTests
{
    private AppDbContext CreateInMemoryContext()
    {
        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AppDbContext(options);
    }

    [Fact]
    public async Task RenewLeaseAsync_ExtendsLease()
    {
        await using AppDbContext db = CreateInMemoryContext();
        EfSliceJobRepository repo = new Farm.Infrastructure.Repositories.Slicing.EfSliceJobRepository(db);

        SliceJob job = new SliceJob { Id = Guid.NewGuid(), Status = SliceJobStatus.Processing, ClaimedAt = DateTime.UtcNow, LeaseExpiresAt = DateTime.UtcNow.AddSeconds(10) };
        await repo.AddAsync(job);
        await repo.SaveChangesAsync();

        await repo.RenewLeaseAsync(job.Id, 300);
        await repo.SaveChangesAsync();

        SliceJob? reloaded = await db.SliceJobs.FindAsync(job.Id);
#pragma warning disable CS8602 // Dereference of a possibly null reference.
        Assert.NotNull(reloaded.LeaseExpiresAt);
#pragma warning restore CS8602 // Dereference of a possibly null reference.
        Assert.True(reloaded.LeaseExpiresAt > DateTime.UtcNow.AddSeconds(200));
    }

    [Fact]
    public async Task IncrementRetryAndRequeueAsync_RequeuesOrFails()
    {
        await using AppDbContext db = CreateInMemoryContext();
        EfSliceJobRepository repo = new Farm.Infrastructure.Repositories.Slicing.EfSliceJobRepository(db);

        SliceJob job = new SliceJob { Id = Guid.NewGuid(), Status = SliceJobStatus.Processing, RetryCount = 0 };
        await repo.AddAsync(job);
        await repo.SaveChangesAsync();

        // First retry should requeue
        await repo.IncrementRetryAndRequeueAsync(job.Id, maxRetries: 3);
        SliceJob? j1 = await db.SliceJobs.FindAsync(job.Id);
#pragma warning disable CS8602 // Dereference of a possibly null reference.
        Assert.Equal(SliceJobStatus.Queued, j1.Status);
#pragma warning restore CS8602 // Dereference of a possibly null reference.
        Assert.Equal(1, j1.RetryCount);

        // Exceed max retries -> marked failed
        j1.RetryCount = 3;
        j1.Status = SliceJobStatus.Processing;
        await repo.SaveChangesAsync();

        await repo.IncrementRetryAndRequeueAsync(job.Id, maxRetries: 3);
        SliceJob? j2 = await db.SliceJobs.FindAsync(job.Id);
#pragma warning disable CS8602 // Dereference of a possibly null reference.
        Assert.Equal(SliceJobStatus.Failed, j2.Status);
#pragma warning restore CS8602 // Dereference of a possibly null reference.
    }
}
