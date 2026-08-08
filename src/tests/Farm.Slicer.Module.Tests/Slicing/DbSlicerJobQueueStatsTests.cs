using System.Data.Common;
using Farm.Slicer.Module.Data;
using Farm.Slicer.Module.Data.Repositories;
using Farm.Slicer.Module.Domain;
using Farm.Slicer.Module.Models;
using Farm.Slicer.Module.Services;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Farm.Slicer.Module.Tests.Slicing;

public sealed class DbSlicerJobQueueStatsTests : IAsyncDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly SliceJobQueryInterceptor _interceptor = new();

    [Fact]
    public async Task GetQueueStatsAsync_MixedEngines_ReturnsPerEngineCountsAndMapsLegacyRowsToOrca()
    {
        await using SlicerDbContext context = await CreatePopulatedContextAsync();
        var queue = new DbSlicerJobQueue(new EfSliceJobRepository(context));
        _interceptor.Reset();

        IReadOnlyDictionary<SlicerEngineType, SlicerQueueStats> allStats =
            await queue.GetAllQueueStatsAsync();
        SlicerQueueStats orca = allStats[SlicerEngineType.OrcaSlicer];
        SlicerQueueStats prusa = allStats[SlicerEngineType.PrusaSlicer];

        orca.Engine.Should().Be(SlicerEngineType.OrcaSlicer);
        orca.QueuedJobs.Should().Be(2);
        orca.ProcessingJobs.Should().Be(1);
        orca.CompletedJobs.Should().Be(6);
        orca.FailedJobs.Should().Be(1);

        prusa.Engine.Should().Be(SlicerEngineType.PrusaSlicer);
        prusa.QueuedJobs.Should().Be(1);
        prusa.ProcessingJobs.Should().Be(0);
        prusa.CompletedJobs.Should().Be(0);
        prusa.FailedJobs.Should().Be(2);

        foreach (SlicerEngineType engine in Enum.GetValues<SlicerEngineType>())
        {
            allStats[engine].QueuedJobs.Should().Be(engine == SlicerEngineType.OrcaSlicer ? 2 : 1);
        }

        _interceptor.SliceJobCommands.Should().ContainSingle();
    }

    [Fact]
    public async Task GetQueueStatsAsync_ExistingRows_ExecutesSingleAggregateWithoutTrackingEntities()
    {
        await using SlicerDbContext context = await CreatePopulatedContextAsync();
        var queue = new DbSlicerJobQueue(new EfSliceJobRepository(context));
        _interceptor.Reset();

        _ = await queue.GetQueueStatsAsync(SlicerEngineType.PrusaSlicer);

        context.ChangeTracker.Entries().Should().BeEmpty();
        string command = _interceptor.SliceJobCommands.Should().ContainSingle().Subject;
        command.Contains("COUNT(", StringComparison.OrdinalIgnoreCase).Should().BeTrue();
        command.Contains("GROUP BY", StringComparison.OrdinalIgnoreCase).Should().BeTrue();
        command.Contains("COLLATE BINARY", StringComparison.OrdinalIgnoreCase).Should().BeTrue();
        command.Should().NotContain("MachineProfileJson");
        command.Should().NotContain("ProcessProfileJson");
        command.Should().NotContain("FilamentProfileJson");
        command.Should().NotContain("SlicerProfileJson");
    }

    [Fact]
    public async Task GetQueueStatsAsync_UndefinedEngine_ThrowsBeforeQuerying()
    {
        await using SlicerDbContext context = await CreatePopulatedContextAsync();
        var queue = new DbSlicerJobQueue(new EfSliceJobRepository(context));
        _interceptor.Reset();

        Func<Task> action = () => queue.GetQueueStatsAsync((SlicerEngineType)999);

        _ = await action.Should().ThrowAsync<ArgumentOutOfRangeException>();
        _interceptor.SliceJobCommands.Should().BeEmpty();
    }

    [Fact]
    public async Task GetAllQueueStatsAsync_CancelledToken_PropagatesCancellation()
    {
        await using SlicerDbContext context = await CreatePopulatedContextAsync();
        var queue = new DbSlicerJobQueue(new EfSliceJobRepository(context));
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        Func<Task> action = () => queue.GetAllQueueStatsAsync(cancellation.Token);

        _ = await action.Should().ThrowAsync<OperationCanceledException>();
    }

    private async Task<SlicerDbContext> CreatePopulatedContextAsync()
    {
        if (_connection.State != System.Data.ConnectionState.Open)
        {
            await _connection.OpenAsync();
        }

        DbContextOptions<SlicerDbContext> options = new DbContextOptionsBuilder<SlicerDbContext>()
            .UseSqlite(_connection)
            .AddInterceptors(_interceptor)
            .Options;
        var context = new SlicerDbContext(options);
        _ = await context.Database.EnsureCreatedAsync();

        List<SliceJob> jobs = Enum.GetValues<SlicerEngineType>()
            .Select(engine => CreateCanonicalJob(SliceJobStatus.Queued, engine))
            .ToList();
        jobs.AddRange(
        [
            CreateCanonicalJob(SliceJobStatus.Queued, SlicerEngineType.OrcaSlicer),
            CreateCanonicalJob(SliceJobStatus.Processing, SlicerEngineType.OrcaSlicer),
            CreateCanonicalJob(SliceJobStatus.Failed, SlicerEngineType.PrusaSlicer),
            CreateCanonicalJob(SliceJobStatus.Failed, SlicerEngineType.PrusaSlicer),
            CreateJob(SliceJobStatus.Failed, SlicerEngineType.PrusaSlicer, engineName: null),
            CreateJob(SliceJobStatus.Completed, SlicerEngineType.PrusaSlicer, string.Empty),
            CreateJob(SliceJobStatus.Completed, SlicerEngineType.PrusaSlicer, " "),
            CreateJob(SliceJobStatus.Completed, SlicerEngineType.PrusaSlicer, "prusaslicer"),
            CreateJob(SliceJobStatus.Completed, SlicerEngineType.PrusaSlicer, "PrusaSlicer "),
            CreateJob(SliceJobStatus.Completed, SlicerEngineType.PrusaSlicer, " PrusaSlicer"),
            CreateJob(SliceJobStatus.Completed, SlicerEngineType.PrusaSlicer, "Unknown"),
            CreateCanonicalJob(SliceJobStatus.Cancelled, SlicerEngineType.PrusaSlicer),
        ]);
        context.SliceJobs.AddRange(jobs);
        _ = await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        return context;
    }

    private static SliceJob CreateCanonicalJob(string status, SlicerEngineType engine) =>
        CreateJob(status, engine, engine.ToString());

    private static SliceJob CreateJob(
        string status,
        SlicerEngineType numericEngine,
        string? engineName)
    {
        return new SliceJob
        {
            Id = Guid.NewGuid(),
            UserId = Guid.NewGuid(),
            ModelFileUrl = "file:///model.stl",
            ModelFileName = "model.stl",
            SlicerEngine = (int)numericEngine,
            SlicerEngineName = engineName,
            Status = status,
            MachineProfileJson = new string('m', 1024),
            ProcessProfileJson = new string('p', 1024),
            FilamentProfileJson = new string('f', 1024),
            SlicerProfileJson = new string('s', 1024),
        };
    }

    public async ValueTask DisposeAsync()
    {
        await _connection.DisposeAsync();
    }

    private sealed class SliceJobQueryInterceptor : DbCommandInterceptor
    {
        private readonly List<string> _sliceJobCommands = [];

        public IReadOnlyList<string> SliceJobCommands => _sliceJobCommands;

        public void Reset() => _sliceJobCommands.Clear();

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("SliceJobs", StringComparison.Ordinal))
            {
                _sliceJobCommands.Add(command.CommandText);
            }

            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
