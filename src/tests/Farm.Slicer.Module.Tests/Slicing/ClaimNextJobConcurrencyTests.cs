using System.ComponentModel.DataAnnotations;
using System.Data.Common;
using Farm.Slicer.Module.Contracts;
using Farm.Slicer.Module.Data;
using Farm.Slicer.Module.Data.Repositories;
using Farm.Slicer.Module.Domain;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Farm.Slicer.Module.Tests.Slicing;

public sealed class ClaimNextJobConcurrencyTests : IAsyncDisposable
{
    private readonly string _databasePath =
        Path.Combine(Path.GetTempPath(), $"printfarmer-claim-{Guid.NewGuid():N}.db");

    [Fact]
    public async Task ClaimNextJobAsync_TwoConcurrentWorkers_ExactlyOneClaimsJob()
    {
        string connectionString = $"Data Source={_databasePath};Cache=Shared;Default Timeout=10";
        await using (SlicerDbContext setup = CreateContext(connectionString))
        {
            _ = await setup.Database.EnsureCreatedAsync();
            _ = setup.SliceJobs.Add(CreateQueuedJob());
            _ = await setup.SaveChangesAsync();
        }

        var rendezvous = new SelectRendezvous();
        await using SlicerDbContext firstContext =
            CreateContext(connectionString, new CandidateSelectInterceptor(rendezvous));
        await using SlicerDbContext secondContext =
            CreateContext(connectionString, new CandidateSelectInterceptor(rendezvous));
        var firstRepository = new EfSliceJobRepository(firstContext);
        var secondRepository = new EfSliceJobRepository(secondContext);
        Guid firstWorker = Guid.NewGuid();
        Guid secondWorker = Guid.NewGuid();

        SliceJob?[] claims = await Task.WhenAll(
            firstRepository.ClaimNextJobAsync(
                WorkerClaimIdentity.CreateUnattested(firstWorker, ["orcaslicer"]),
                30,
                3),
            secondRepository.ClaimNextJobAsync(
                WorkerClaimIdentity.CreateUnattested(secondWorker, ["orcaslicer"]),
                30,
                3));

        claims.Count(claim => claim is not null).Should().Be(1);
        claims.Count(claim => claim is null).Should().Be(1);
        SliceJob winner = claims.Single(claim => claim is not null)!;
        winner.WorkerId.Should().NotBeNull();
        new[] { firstWorker, secondWorker }.Should().Contain(winner.WorkerId!.Value);

        await using SlicerDbContext verification = CreateContext(connectionString);
        SliceJob persisted = await verification.SliceJobs.AsNoTracking().SingleAsync();
        persisted.Status.Should().Be(SliceJobStatus.Processing);
        persisted.WorkerId.Should().Be(winner.WorkerId);
    }

    [Fact]
    public async Task ClaimNextJobAsync_TwoConcurrentWorkersAndTwoJobs_BothClaimDistinctJobs()
    {
        string connectionString = $"Data Source={_databasePath};Cache=Shared;Default Timeout=10";
        await using (SlicerDbContext setup = CreateContext(connectionString))
        {
            _ = await setup.Database.EnsureCreatedAsync();
            DateTime firstQueuedAt = DateTime.UtcNow.AddMinutes(-1);
            _ = setup.SliceJobs.Add(CreateQueuedJob(firstQueuedAt));
            _ = setup.SliceJobs.Add(CreateQueuedJob(firstQueuedAt.AddSeconds(1)));
            _ = await setup.SaveChangesAsync();
        }

        var rendezvous = new SelectRendezvous();
        await using SlicerDbContext firstContext =
            CreateContext(connectionString, new CandidateSelectInterceptor(rendezvous));
        await using SlicerDbContext secondContext =
            CreateContext(connectionString, new CandidateSelectInterceptor(rendezvous));
        var firstRepository = new EfSliceJobRepository(firstContext);
        var secondRepository = new EfSliceJobRepository(secondContext);

        SliceJob?[] claims = await Task.WhenAll(
            firstRepository.ClaimNextJobAsync(
                WorkerClaimIdentity.CreateUnattested(Guid.NewGuid(), ["orcaslicer"]),
                30,
                3),
            secondRepository.ClaimNextJobAsync(
                WorkerClaimIdentity.CreateUnattested(Guid.NewGuid(), ["orcaslicer"]),
                30,
                3));

        claims.Should().OnlyContain(claim => claim != null);
        claims.Select(claim => claim!.Id).Should().OnlyHaveUniqueItems();
        await using SlicerDbContext verification = CreateContext(connectionString);
        (await verification.SliceJobs.CountAsync(job => job.Status == SliceJobStatus.Processing))
            .Should().Be(2);
    }

    [Fact]
    public async Task RenewLeaseAsync_ConcurrentReassignment_PreviousWorkerCannotExtendLease()
    {
        string connectionString = $"Data Source={_databasePath};Cache=Shared;Default Timeout=10";
        Guid originalWorker = Guid.NewGuid();
        Guid newWorker = Guid.NewGuid();
        Guid jobId = Guid.NewGuid();
        Guid claimToken = Guid.NewGuid();
        await using (SlicerDbContext setup = CreateContext(connectionString))
        {
            _ = await setup.Database.EnsureCreatedAsync();
            _ = setup.SliceJobs.Add(new SliceJob
            {
                Id = jobId,
                Status = SliceJobStatus.Processing,
                WorkerId = originalWorker,
                ClaimToken = claimToken,
                LeaseExpiresAt = DateTime.UtcNow.AddMinutes(1),
            });
            _ = await setup.SaveChangesAsync();
        }

        await using SlicerDbContext staleContext = CreateContext(connectionString);
        var staleRepository = new EfSliceJobRepository(staleContext);
        _ = await staleRepository.GetByIdAsync(jobId);
        DateTime reassignedLease = DateTime.UtcNow.AddMinutes(5);
        await using (SlicerDbContext reassignmentContext = CreateContext(connectionString))
        {
            _ = await reassignmentContext.SliceJobs
                .Where(job => job.Id == jobId)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(job => job.WorkerId, newWorker)
                    .SetProperty(job => job.LeaseExpiresAt, reassignedLease));
        }

        bool renewed = await staleRepository.RenewLeaseAsync(
            jobId,
            originalWorker,
            claimToken,
            300);

        renewed.Should().BeFalse();
        await using SlicerDbContext verification = CreateContext(connectionString);
        SliceJob persisted = await verification.SliceJobs.AsNoTracking().SingleAsync();
        persisted.WorkerId.Should().Be(newWorker);
        persisted.LeaseExpiresAt.Should().BeCloseTo(reassignedLease, TimeSpan.FromMilliseconds(1));
    }

    [Fact]
    public async Task ClaimNextJobAsync_SameWorkerReclaimsExpiredJob_StaleClaimCannotMutateNewLease()
    {
        string connectionString = $"Data Source={_databasePath};Cache=Shared;Default Timeout=10";
        Guid workerId = Guid.NewGuid();
        await using SlicerDbContext context = CreateContext(connectionString);
        _ = await context.Database.EnsureCreatedAsync();
        _ = context.SliceJobs.Add(CreateQueuedJob());
        _ = await context.SaveChangesAsync();
        var repository = new EfSliceJobRepository(context);

        SliceJob firstClaim = (await repository.ClaimNextJobAsync(
            WorkerClaimIdentity.CreateUnattested(workerId, ["orcaslicer"]),
            30,
            3))!;
        _ = await context.SliceJobs
            .Where(job => job.Id == firstClaim.Id)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(job => job.LeaseExpiresAt, DateTime.UtcNow.AddSeconds(-1)));
        SliceJob secondClaim = (await repository.ClaimNextJobAsync(
            WorkerClaimIdentity.CreateUnattested(workerId, ["orcaslicer"]),
            30,
            3))!;

        firstClaim.ClaimToken.Should().NotBeNull();
        secondClaim.ClaimToken.Should().NotBeNull();
        secondClaim.ClaimToken!.Value.Should().NotBe(firstClaim.ClaimToken!.Value);
        secondClaim.RetryCount.Should().Be(1);
        Guid staleClaimToken = firstClaim.ClaimToken!.Value;
        (await repository.GetByActiveWorkerLeaseAsync(
            firstClaim.Id,
            workerId,
            staleClaimToken)).Should().BeNull();
        (await repository.TryUpdateProgressForActiveLeaseAsync(
            firstClaim.Id,
            workerId,
            staleClaimToken,
            50,
            "stale")).Should().BeFalse();
        (await repository.TryCompleteForActiveLeaseAsync(
            firstClaim.Id,
            workerId,
            staleClaimToken,
            "/api/artifacts/stale",
            [])).Should().BeFalse();
        (await repository.TryFailForActiveLeaseAsync(
            firstClaim.Id,
            workerId,
            staleClaimToken,
            "stale")).Should().BeFalse();
        (await repository.RenewLeaseAsync(
            firstClaim.Id,
            workerId,
            staleClaimToken,
            300)).Should().BeFalse();

        await using SlicerDbContext verification = CreateContext(connectionString);
        SliceJob persisted = await verification.SliceJobs.AsNoTracking().SingleAsync();
        persisted.Status.Should().Be(SliceJobStatus.Processing);
        persisted.WorkerId.Should().Be(workerId);
        persisted.ClaimToken.Should().Be(secondClaim.ClaimToken);
        persisted.ProgressPercent.Should().Be(0);
    }

    [Fact]
    public async Task ClaimNextJobAsync_RepeatedExpiry_StopsAtRetryLimit()
    {
        string connectionString = $"Data Source={_databasePath};Cache=Shared;Default Timeout=10";
        Guid workerId = Guid.NewGuid();
        await using SlicerDbContext context = CreateContext(connectionString);
        _ = await context.Database.EnsureCreatedAsync();
        _ = context.SliceJobs.Add(CreateQueuedJob());
        _ = await context.SaveChangesAsync();
        var repository = new EfSliceJobRepository(context);

        SliceJob? claim = await repository.ClaimNextJobAsync(
            WorkerClaimIdentity.CreateUnattested(workerId, ["orcaslicer"]),
            30,
            3);
        for (int retry = 1; retry <= 3; retry++)
        {
            _ = claim.Should().NotBeNull();
            _ = await context.SliceJobs
                .Where(job => job.Id == claim!.Id)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(job => job.LeaseExpiresAt, DateTime.UtcNow.AddSeconds(-1)));

            claim = await repository.ClaimNextJobAsync(
                WorkerClaimIdentity.CreateUnattested(workerId, ["orcaslicer"]),
                30,
                3);
            _ = claim.Should().NotBeNull();
            _ = claim!.RetryCount.Should().Be(retry);
        }

        _ = await context.SliceJobs
            .Where(job => job.Id == claim!.Id)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(job => job.LeaseExpiresAt, DateTime.UtcNow.AddSeconds(-1)));
        SliceJob? exhausted = await repository.ClaimNextJobAsync(
            WorkerClaimIdentity.CreateUnattested(workerId, ["orcaslicer"]),
            30,
            3);

        await using SlicerDbContext verification = CreateContext(connectionString);
        SliceJob persisted = await verification.SliceJobs.AsNoTracking().SingleAsync();
        _ = exhausted.Should().BeNull();
        _ = persisted.Status.Should().Be(SliceJobStatus.Failed);
        _ = persisted.RetryCount.Should().Be(3);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(SliceJob.MinimumLeaseDurationSeconds - 1)]
    [InlineData(SliceJob.MaximumLeaseDurationSeconds + 1)]
    public async Task ClaimNextJobAsync_InvalidLeaseDuration_Throws(int leaseDurationSeconds)
    {
        await using SlicerDbContext context = CreateContext("Data Source=:memory:");
        var repository = new EfSliceJobRepository(context);

        Func<Task> claim = () => repository.ClaimNextJobAsync(
            WorkerClaimIdentity.CreateUnattested(Guid.NewGuid(), ["orcaslicer"]),
            leaseDurationSeconds,
            3);

        await claim.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(SliceJob.MinimumLeaseDurationSeconds - 1)]
    [InlineData(SliceJob.MaximumLeaseDurationSeconds + 1)]
    public void LeaseRequests_InvalidDuration_FailModelValidation(int leaseDurationSeconds)
    {
        object[] requests =
        [
            new ClaimJobRequest { LeaseDurationSeconds = leaseDurationSeconds },
            new RenewLeaseRequest { LeaseDurationSeconds = leaseDurationSeconds },
        ];

        foreach (object request in requests)
        {
            Validator.TryValidateObject(
                request,
                new ValidationContext(request),
                new List<ValidationResult>(),
                validateAllProperties: true).Should().BeFalse();
        }
    }

    public ValueTask DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_databasePath))
        {
            File.Delete(_databasePath);
        }

        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }

    private static SlicerDbContext CreateContext(
        string connectionString,
        DbCommandInterceptor? interceptor = null)
    {
        var options = new DbContextOptionsBuilder<SlicerDbContext>()
            .UseSqlite(connectionString);
        if (interceptor is not null)
        {
            _ = options.AddInterceptors(interceptor);
        }

        return new SlicerDbContext(options.Options);
    }

    private static SliceJob CreateQueuedJob(DateTime? queuedAt = null) =>
        new()
        {
            Id = Guid.NewGuid(),
            Status = SliceJobStatus.Queued,
            QueuedAt = queuedAt ?? DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            UserId = Guid.NewGuid(),
            ModelFileName = "model.stl",
            ModelFileUrl = "file:///model.stl",
            SlicerEngine = 0,
            RequiredCapabilitiesJson = "[\"orcaslicer\"]",
            Priority = 1,
        };

    private sealed class SelectRendezvous
    {
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrivals;

        public async Task ArriveAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _arrivals) == 2)
            {
                _release.TrySetResult();
            }

            await _release.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
        }
    }

    private sealed class CandidateSelectInterceptor(SelectRendezvous rendezvous)
        : DbCommandInterceptor
    {
        private int _hasWaited;

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command,
            CommandExecutedEventData eventData,
            DbDataReader result,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref _hasWaited, 1) == 0 &&
                command.CommandText.Contains("SliceJobs", StringComparison.Ordinal))
            {
                await rendezvous.ArriveAsync(cancellationToken);
            }

            return result;
        }
    }
}
