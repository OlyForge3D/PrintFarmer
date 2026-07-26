using System.Data.Common;
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
            firstRepository.ClaimNextJobAsync(firstWorker, ["orcaslicer"], 30),
            secondRepository.ClaimNextJobAsync(secondWorker, ["orcaslicer"], 30));

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

    private static SliceJob CreateQueuedJob() =>
        new()
        {
            Id = Guid.NewGuid(),
            Status = SliceJobStatus.Queued,
            QueuedAt = DateTime.UtcNow,
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
