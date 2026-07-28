using Farm.Slicer.Module.Data;
using Farm.Slicer.Module.Data.Repositories;
using Farm.Slicer.Module.Domain;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Farm.Slicer.Module.Tests.Slicing;

public sealed class SliceJobOwnerRetryConcurrencyTests
{
    [Fact]
    public async Task TryRetryJobAsync_ConcurrentRetryAndClaim_DoesNotOverwriteNewClaim()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        DbContextOptions<SlicerDbContext> options = new DbContextOptionsBuilder<SlicerDbContext>()
            .UseSqlite(connection)
            .Options;
        await using SlicerDbContext staleRequestDb = new(options);
        _ = await staleRequestDb.Database.EnsureCreatedAsync();
        Guid jobId = Guid.NewGuid();
        Guid userId = Guid.NewGuid();
        DateTime observedUpdatedAt = DateTime.UtcNow.AddMinutes(-1);
        _ = staleRequestDb.SliceJobs.Add(CreateFailedJob(jobId, userId, observedUpdatedAt));
        _ = await staleRequestDb.SaveChangesAsync();
        var staleRequestRepository = new EfSliceJobRepository(staleRequestDb);
        SliceJob observed = (await staleRequestRepository.GetByIdAsync(jobId))!;

        Guid newWorkerId = Guid.NewGuid();
        await using (var concurrentDb = new SlicerDbContext(options))
        {
            var concurrentRepository = new EfSliceJobRepository(concurrentDb);
            SliceJob? retried = await concurrentRepository.TryRetryJobAsync(
                jobId,
                userId,
                observed.Status,
                observed.UpdatedAt);
            retried.Should().NotBeNull();
            SliceJob? claimed = await concurrentRepository.ClaimNextJobAsync(
                WorkerClaimIdentity.CreateUnattested(newWorkerId, capabilities: null),
                leaseDurationSeconds: 300,
                maxRetries: 3);
            claimed.Should().NotBeNull();
        }

        SliceJob? staleRetry = await staleRequestRepository.TryRetryJobAsync(
            jobId,
            userId,
            observed.Status,
            observed.UpdatedAt);

        staleRetry.Should().BeNull();
        await using var verificationDb = new SlicerDbContext(options);
        SliceJob persisted = await verificationDb.SliceJobs.AsNoTracking().SingleAsync();
        persisted.Status.Should().Be(SliceJobStatus.Processing);
        persisted.WorkerId.Should().Be(newWorkerId);
        persisted.ClaimToken.Should().NotBeNull();
        persisted.LeaseExpiresAt.Should().BeAfter(DateTime.UtcNow);
    }

    private static SliceJob CreateFailedJob(Guid jobId, Guid userId, DateTime updatedAt) =>
        new()
        {
            Id = jobId,
            UserId = userId,
            Status = SliceJobStatus.Failed,
            QueuedAt = DateTime.UtcNow.AddMinutes(-5),
            UpdatedAt = updatedAt,
            CompletedAt = DateTime.UtcNow.AddMinutes(-1),
            ErrorMessage = "failed",
            ModelFileName = "model.stl",
            ModelFileUrl = "file:///model.stl",
            SlicerEngine = 0,
            RequiredCapabilitiesJson = "[]",
            Priority = 1,
        };
}
