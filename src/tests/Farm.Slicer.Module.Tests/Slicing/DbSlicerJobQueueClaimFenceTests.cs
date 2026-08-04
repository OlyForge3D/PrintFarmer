using Farm.Slicer.Module.Data;
using Farm.Slicer.Module.Data.Repositories;
using Farm.Slicer.Module.Domain;
using Farm.Slicer.Module.Models;
using Farm.Slicer.Module.Services;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Farm.Slicer.Module.Tests.Slicing;

public sealed class DbSlicerJobQueueClaimFenceTests : IAsyncDisposable
{
    private readonly string _databasePath =
        Path.Combine(Path.GetTempPath(), $"printfarmer-queue-fence-{Guid.NewGuid():N}.db");

    [Fact]
    public async Task EnqueueAsync_WithNamedProfileSelection_PreservesSlicerProfileJson()
    {
        string connectionString = $"Data Source={_databasePath}";
        string profileSelectionJson =
            """{"machineProfileName":"Test Machine","processProfileName":"Test Process","filamentProfileName":"Test Filament"}""";
        Guid jobId = Guid.NewGuid();

        await using SlicerDbContext context = CreateContext(connectionString);
        _ = await context.Database.EnsureCreatedAsync();
        var queue = new DbSlicerJobQueue(new EfSliceJobRepository(context));

        await queue.EnqueueAsync(new DistributedSlicingJob
        {
            Id = jobId,
            UserId = Guid.NewGuid(),
            ModelFileUrl = new Uri("file:///model.stl"),
            ModelFileName = "model.stl",
            EngineType = SlicerEngineType.OrcaSlicer,
            SlicerProfileJson = profileSelectionJson,
        });

        SliceJob persisted = await context.SliceJobs.AsNoTracking().SingleAsync(job => job.Id == jobId);
        DistributedSlicingJob? loaded = await queue.GetJobAsync(jobId);

        _ = persisted.SlicerProfileJson.Should().Be(profileSelectionJson);
        _ = loaded.Should().NotBeNull();
        _ = loaded!.SlicerProfileJson.Should().Be(profileSelectionJson);
    }

    [Fact]
    public async Task WorkerMutations_SameWorkerStaleClaimAfterReclaim_AreRejected()
    {
        string connectionString = $"Data Source={_databasePath}";
        Guid jobId = Guid.NewGuid();
        Guid workerId = Guid.NewGuid();
        Guid staleClaimToken = Guid.NewGuid();
        Guid activeClaimToken = Guid.NewGuid();

        await using (SlicerDbContext setup = CreateContext(connectionString))
        {
            _ = await setup.Database.EnsureCreatedAsync();
            _ = setup.SliceJobs.Add(CreateProcessingJob(jobId, workerId, activeClaimToken));
            _ = await setup.SaveChangesAsync();
        }

        await using SlicerDbContext mutationContext = CreateContext(connectionString);
        var queue = new DbSlicerJobQueue(new EfSliceJobRepository(mutationContext));
        var staleJob = new DistributedSlicingJob
        {
            Id = jobId,
            WorkerId = workerId.ToString(),
            ClaimToken = staleClaimToken,
        };

        Func<Task> progress = () => queue.UpdateProgressAsync(staleJob, 45);
        Func<Task> failure = () => queue.FailJobAsync(staleJob, "stale failure");
        Func<Task> requeue = () => queue.RequeueJobAsync(staleJob);
        Func<Task> completion = () => queue.CompleteJobAsync(
            staleJob,
            new SlicingResult
            {
                ResultFileUrl = new Uri("file:///stale.gcode"),
                EstimatedPrintTimeSeconds = 60,
                EstimatedFilamentUsageGrams = 1,
                LayerCount = 10,
            });

        await progress.Should().ThrowAsync<InvalidOperationException>();
        await failure.Should().ThrowAsync<InvalidOperationException>();
        await requeue.Should().ThrowAsync<InvalidOperationException>();
        await completion.Should().ThrowAsync<InvalidOperationException>();

        await using SlicerDbContext verification = CreateContext(connectionString);
        SliceJob persisted = await verification.SliceJobs.AsNoTracking().SingleAsync();
        persisted.Status.Should().Be(SliceJobStatus.Processing);
        persisted.WorkerId.Should().Be(workerId);
        persisted.ClaimToken.Should().Be(activeClaimToken);
        persisted.ProgressPercent.Should().Be(0);
        persisted.ErrorMessage.Should().BeNull();
        persisted.ResultFileUrl.Should().BeNull();
    }

    [Theory]
    [InlineData("progress")]
    [InlineData("failure")]
    [InlineData("completion")]
    [InlineData("requeue")]
    public async Task WorkerMutation_ActiveClaim_Succeeds(string operation)
    {
        string connectionString = $"Data Source={_databasePath}";
        Guid jobId = Guid.NewGuid();
        Guid workerId = Guid.NewGuid();
        Guid claimToken = Guid.NewGuid();

        await using (SlicerDbContext setup = CreateContext(connectionString))
        {
            _ = await setup.Database.EnsureCreatedAsync();
            _ = setup.SliceJobs.Add(CreateProcessingJob(jobId, workerId, claimToken));
            _ = await setup.SaveChangesAsync();
        }

        await using SlicerDbContext mutationContext = CreateContext(connectionString);
        var queue = new DbSlicerJobQueue(new EfSliceJobRepository(mutationContext));
        var activeJob = new DistributedSlicingJob
        {
            Id = jobId,
            WorkerId = workerId.ToString(),
            ClaimToken = claimToken,
        };

        switch (operation)
        {
            case "progress":
                await queue.UpdateProgressAsync(activeJob, 45, "slicing");
                break;
            case "failure":
                await queue.FailJobAsync(activeJob, "worker failure");
                break;
            case "completion":
                await queue.CompleteJobAsync(
                    activeJob,
                    new SlicingResult
                    {
                        ResultFileUrl = new Uri("file:///result.gcode"),
                        EstimatedPrintTimeSeconds = 60,
                        EstimatedFilamentUsageGrams = 1,
                    });
                break;
            case "requeue":
                await queue.RequeueJobAsync(activeJob);
                break;
        }

        await using SlicerDbContext verification = CreateContext(connectionString);
        SliceJob persisted = await verification.SliceJobs.AsNoTracking().SingleAsync();
        if (operation == "progress")
        {
            persisted.Status.Should().Be(SliceJobStatus.Processing);
            persisted.ProgressPercent.Should().Be(45);
            persisted.ProgressMessage.Should().Be("slicing");
        }
        else if (operation == "failure")
        {
            persisted.Status.Should().Be(SliceJobStatus.Failed);
            persisted.ErrorMessage.Should().Be("worker failure");
        }
        else if (operation == "requeue")
        {
            persisted.Status.Should().Be(SliceJobStatus.Queued);
            persisted.RetryCount.Should().Be(1);
            persisted.WorkerId.Should().BeNull();
            persisted.ClaimToken.Should().BeNull();
        }
        else
        {
            persisted.Status.Should().Be(SliceJobStatus.Completed);
            persisted.ResultFileUrl.Should().Be("file:///result.gcode");
        }
    }

    private static SlicerDbContext CreateContext(string connectionString)
    {
        DbContextOptions<SlicerDbContext> options = new DbContextOptionsBuilder<SlicerDbContext>()
            .UseSqlite(connectionString)
            .Options;
        return new SlicerDbContext(options);
    }

    private static SliceJob CreateProcessingJob(Guid jobId, Guid workerId, Guid claimToken) =>
        new()
        {
            Id = jobId,
            Status = SliceJobStatus.Processing,
            WorkerId = workerId,
            ClaimToken = claimToken,
            ClaimedAt = DateTime.UtcNow,
            LeaseExpiresAt = DateTime.UtcNow.AddMinutes(5),
            QueuedAt = DateTime.UtcNow.AddMinutes(-1),
            UpdatedAt = DateTime.UtcNow,
            UserId = Guid.NewGuid(),
            ModelFileName = "model.stl",
            ModelFileUrl = "file:///model.stl",
            SlicerEngine = 0,
            RequiredCapabilitiesJson = "[\"orcaslicer\"]",
            Priority = 1,
        };

    public ValueTask DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        File.Delete(_databasePath);
        return ValueTask.CompletedTask;
    }
}
