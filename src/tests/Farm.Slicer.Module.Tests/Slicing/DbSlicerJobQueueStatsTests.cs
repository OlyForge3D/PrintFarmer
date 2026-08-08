using System.Data.Common;
using Farm.Slicer.Module.Data;
using Farm.Slicer.Module.Data.Repositories;
using Farm.Slicer.Module.Domain;
using Farm.Slicer.Module.Models;
using Farm.Slicer.Module.Services;
using Farm.Slicer.Module.Services.Configuration;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Options;

namespace Farm.Slicer.Module.Tests.Slicing;

public sealed class DbSlicerJobQueueStatsTests : IAsyncDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 8, 7, 20, 0, 0, TimeSpan.Zero);
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly QueueQueryInterceptor _interceptor = new();

    [Fact]
    public async Task GetQueueStatsAsync_MixedEngines_ReturnsPerEngineCountsAndMapsLegacyRowsToOrca()
    {
        await using SlicerDbContext context = await CreatePopulatedContextAsync();
        DbSlicerJobQueue queue = CreateQueue(context);
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
            allStats[engine].ActiveWorkers.Should().Be(0);
            allStats[engine].AverageProcessingTimeSeconds.Should().Be(0);
            allStats[engine].EstimatedWaitTime.Should().BeNull();
        }
    }

    [Fact]
    public async Task GetAllQueueStatsAsync_WorkerStates_CountsOnlyFreshEnabledDispatchableCapabilities()
    {
        await using SlicerDbContext context = await CreateEmptyContextAsync();
        context.Workers.AddRange(
            CreateWorker("""["orcaslicer"]""", WorkerStatus.Online, totalSlots: 2),
            CreateWorker(
                """{"capabilities":["ORCASLICER","prusaslicer"]}""",
                WorkerStatus.Busy,
                totalSlots: 3),
            CreateWorker("""["orcaslicer"]""", WorkerStatus.Draining, totalSlots: 4),
            CreateWorker("""["orcaslicer"]""", WorkerStatus.Offline, totalSlots: 5),
            CreateWorker("""["orcaslicer"]""", WorkerStatus.Error, totalSlots: 6),
            CreateWorker("""["orcaslicer"]""", WorkerStatus.Online, totalSlots: 7, isDisabled: true),
            CreateWorker(
                """["orcaslicer"]""",
                WorkerStatus.Online,
                totalSlots: 8,
                lastHeartbeat: Now.UtcDateTime.AddMinutes(-31)),
            CreateWorker(
                """["orcaslicer"]""",
                WorkerStatus.Online,
                totalSlots: 9,
                hasHeartbeat: false),
            CreateWorker("""["cura"]""", WorkerStatus.Online, totalSlots: 0));
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        DbSlicerJobQueue queue = CreateQueue(context);

        IReadOnlyDictionary<SlicerEngineType, SlicerQueueStats> stats =
            await queue.GetAllQueueStatsAsync();

        stats[SlicerEngineType.OrcaSlicer].ActiveWorkers.Should().Be(2);
        stats[SlicerEngineType.PrusaSlicer].ActiveWorkers.Should().Be(1);
        stats[SlicerEngineType.SuperSlicer].ActiveWorkers.Should().Be(0);
        stats[SlicerEngineType.Cura].ActiveWorkers.Should().Be(1);
    }

    [Fact]
    public async Task GetAllQueueStatsAsync_LeaseAndTimingEdges_UsesAuthoritativeWorkAndValidSamples()
    {
        await using SlicerDbContext context = await CreateEmptyContextAsync();
        Worker liveWorker = CreateWorker("""["orcaslicer"]""", WorkerStatus.Online, totalSlots: 2);
        Worker staleWorker = CreateWorker(
            """["orcaslicer"]""",
            WorkerStatus.Online,
            totalSlots: 10,
            lastHeartbeat: Now.UtcDateTime.AddHours(-1));
        context.Workers.AddRange(liveWorker, staleWorker);
        context.SliceJobs.AddRange(
            CreateCanonicalJob(SliceJobStatus.Queued, SlicerEngineType.OrcaSlicer),
            CreateCanonicalJob(SliceJobStatus.Queued, SlicerEngineType.OrcaSlicer),
            CreateCanonicalJob(SliceJobStatus.Queued, SlicerEngineType.OrcaSlicer),
            CreateProcessingJob(liveWorker.Id, Now.UtcDateTime.AddMinutes(5)),
            CreateProcessingJob(liveWorker.Id, Now.UtcDateTime.AddSeconds(-1)),
            CreateProcessingJob(staleWorker.Id, Now.UtcDateTime.AddMinutes(5)),
            CreateProcessingJob(liveWorker.Id, Now.UtcDateTime.AddMinutes(5), includeLeaseToken: false),
            CreateCompletedJob(SlicerEngineType.OrcaSlicer, durationSeconds: 10),
            CreateCompletedJob(SlicerEngineType.OrcaSlicer, durationSeconds: 20),
            CreateCompletedJob(SlicerEngineType.OrcaSlicer, durationSeconds: null),
            CreateCompletedJob(SlicerEngineType.OrcaSlicer, durationSeconds: 30, includeCompletion: false),
            CreateCompletedJob(SlicerEngineType.OrcaSlicer, durationSeconds: -5));
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        DbSlicerJobQueue queue = CreateQueue(context);

        SlicerQueueStats stats =
            (await queue.GetAllQueueStatsAsync())[SlicerEngineType.OrcaSlicer];

        stats.QueuedJobs.Should().Be(3);
        stats.ProcessingJobs.Should().Be(4);
        stats.CompletedJobs.Should().Be(5);
        stats.ActiveWorkers.Should().Be(1);
        stats.AverageProcessingTimeSeconds.Should().Be(15);
        stats.EstimatedWaitTime.Should().Be(TimeSpan.FromSeconds(30));
        stats.LastUpdated.Should().Be(Now.UtcDateTime);
    }

    [Fact]
    public async Task GetAllQueueStatsAsync_NoCapacityHistoryOrWork_ReturnsDocumentedSentinels()
    {
        await using SlicerDbContext context = await CreateEmptyContextAsync();
        context.Workers.AddRange(
            CreateWorker("""["orcaslicer"]""", WorkerStatus.Online, totalSlots: 1),
            CreateWorker("""["prusaslicer"]""", WorkerStatus.Online, totalSlots: 1),
            CreateWorker("""["superslicer"]""", WorkerStatus.Online, totalSlots: 0));
        context.SliceJobs.AddRange(
            CreateCompletedJob(SlicerEngineType.OrcaSlicer, durationSeconds: 5),
            CreateCanonicalJob(SliceJobStatus.Queued, SlicerEngineType.PrusaSlicer),
            CreateCanonicalJob(SliceJobStatus.Queued, SlicerEngineType.SuperSlicer),
            CreateCompletedJob(SlicerEngineType.SuperSlicer, durationSeconds: 12),
            CreateCanonicalJob(SliceJobStatus.Queued, SlicerEngineType.Cura),
            CreateCompletedJob(SlicerEngineType.Cura, durationSeconds: 9));
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        DbSlicerJobQueue queue = CreateQueue(context);

        IReadOnlyDictionary<SlicerEngineType, SlicerQueueStats> stats =
            await queue.GetAllQueueStatsAsync();

        stats[SlicerEngineType.OrcaSlicer].EstimatedWaitTime.Should().Be(TimeSpan.Zero);
        stats[SlicerEngineType.PrusaSlicer].AverageProcessingTimeSeconds.Should().Be(0);
        stats[SlicerEngineType.PrusaSlicer].EstimatedWaitTime.Should().BeNull();
        stats[SlicerEngineType.SuperSlicer].ActiveWorkers.Should().Be(1);
        stats[SlicerEngineType.SuperSlicer].AverageProcessingTimeSeconds.Should().Be(12);
        stats[SlicerEngineType.SuperSlicer].EstimatedWaitTime.Should().BeNull();
        stats[SlicerEngineType.Cura].ActiveWorkers.Should().Be(0);
        stats[SlicerEngineType.Cura].AverageProcessingTimeSeconds.Should().Be(9);
        stats[SlicerEngineType.Cura].EstimatedWaitTime.Should().BeNull();
    }

    [Fact]
    public async Task GetQueueStatsAsync_ExistingRows_ExecutesFixedAggregatesWithoutTrackingEntities()
    {
        await using SlicerDbContext context = await CreatePopulatedContextAsync();
        DbSlicerJobQueue queue = CreateQueue(context);
        _interceptor.Reset();

        _ = await queue.GetQueueStatsAsync(SlicerEngineType.PrusaSlicer);

        context.ChangeTracker.Entries().Should().BeEmpty();
        _interceptor.Commands.Should().HaveCount(3);
        _interceptor.SliceJobCommands.Should().HaveCount(2);
        _interceptor.WorkerCommands.Should().HaveCount(2);

        string countCommand = _interceptor.SliceJobCommands.Single(command =>
            !command.Contains("JOIN", StringComparison.OrdinalIgnoreCase));
        countCommand.Contains("COUNT(", StringComparison.OrdinalIgnoreCase).Should().BeTrue();
        countCommand.Contains("GROUP BY", StringComparison.OrdinalIgnoreCase).Should().BeTrue();
        countCommand.Contains("COLLATE BINARY", StringComparison.OrdinalIgnoreCase).Should().BeTrue();

        string metricCommand = _interceptor.SliceJobCommands.Single(command =>
            command.Contains("JOIN", StringComparison.OrdinalIgnoreCase));
        metricCommand.Contains("AVG(", StringComparison.OrdinalIgnoreCase).Should().BeTrue();
        metricCommand.Contains("GROUP BY", StringComparison.OrdinalIgnoreCase).Should().BeTrue();

        foreach (string command in _interceptor.Commands)
        {
            command.Should().NotContain("MachineProfileJson");
            command.Should().NotContain("ProcessProfileJson");
            command.Should().NotContain("FilamentProfileJson");
            command.Should().NotContain("SlicerProfileJson");
        }
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("postgres")]
    [InlineData("sqlserver")]
    public void QueueMetricQueries_RelationalProviders_TranslateToGroupedServerSql(string provider)
    {
        using SlicerDbContext context = CreateProviderContext(provider);
        DateTime cutoff = Now.UtcDateTime.AddMinutes(-30);

        string workerSql = EfSliceJobRepository
            .BuildQueueWorkerMetricQuery(context, cutoff)
            .ToQueryString();
        string jobSql = EfSliceJobRepository
            .BuildQueueJobMetricQuery(context, Now.UtcDateTime, cutoff)
            .ToQueryString();

        workerSql.Should().ContainEquivalentOf("GROUP BY");
        workerSql.Should().ContainEquivalentOf("COUNT");
        workerSql.Should().ContainEquivalentOf("SUM");
        jobSql.Should().ContainEquivalentOf("LEFT JOIN");
        jobSql.Should().ContainEquivalentOf("GROUP BY");
        jobSql.Should().ContainEquivalentOf("AVG");
    }

    [Fact]
    public async Task GetQueueStatsAsync_UndefinedEngine_ThrowsBeforeQuerying()
    {
        await using SlicerDbContext context = await CreatePopulatedContextAsync();
        DbSlicerJobQueue queue = CreateQueue(context);
        _interceptor.Reset();

        Func<Task> action = () => queue.GetQueueStatsAsync((SlicerEngineType)999);

        _ = await action.Should().ThrowAsync<ArgumentOutOfRangeException>();
        _interceptor.Commands.Should().BeEmpty();
    }

    [Fact]
    public async Task GetAllQueueStatsAsync_CancelledToken_PropagatesCancellation()
    {
        await using SlicerDbContext context = await CreatePopulatedContextAsync();
        DbSlicerJobQueue queue = CreateQueue(context);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        Func<Task> action = () => queue.GetAllQueueStatsAsync(cancellation.Token);

        _ = await action.Should().ThrowAsync<OperationCanceledException>();
    }

    private DbSlicerJobQueue CreateQueue(SlicerDbContext context)
    {
        return new DbSlicerJobQueue(
            new EfSliceJobRepository(context),
            staleWorkerOptions: Options.Create(new StaleWorkerCleanupSettings
            {
                StaleAfterMinutes = 30,
            }),
            timeProvider: new FixedTimeProvider(Now));
    }

    private async Task<SlicerDbContext> CreateEmptyContextAsync()
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
        return context;
    }

    private async Task<SlicerDbContext> CreatePopulatedContextAsync()
    {
        SlicerDbContext context = await CreateEmptyContextAsync();
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

    private static SlicerDbContext CreateProviderContext(string provider)
    {
        DbContextOptionsBuilder<SlicerDbContext> builder = new();
        _ = provider switch
        {
            "sqlite" => builder.UseSqlite("Data Source=:memory:"),
            "postgres" => builder.UseNpgsql("Host=localhost;Database=model_only;Username=model_only"),
            "sqlserver" => builder.UseSqlServer(
                "Server=(localdb)\\ModelOnly;Database=model_only;Trusted_Connection=True;TrustServerCertificate=True"),
            _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, "Unknown provider."),
        };
        return new SlicerDbContext(builder.Options);
    }

    private static Worker CreateWorker(
        string capabilitiesJson,
        string status,
        int totalSlots,
        bool isDisabled = false,
        DateTime? lastHeartbeat = null,
        bool hasHeartbeat = true)
    {
        DateTime timestamp = Now.UtcDateTime;
        return new Worker
        {
            Id = Guid.NewGuid(),
            ServiceId = Guid.NewGuid().ToString(),
            Name = "Queue metric worker",
            EndpointUrl = "http://worker.invalid",
            CapabilitiesJson = capabilitiesJson,
            Status = status,
            TotalSlots = totalSlots,
            ActiveJobs = 0,
            LastHeartbeat = hasHeartbeat ? lastHeartbeat ?? timestamp : null,
            RegisteredAt = timestamp,
            CreatedAt = timestamp,
            UpdatedAt = timestamp,
            IsDisabled = isDisabled,
        };
    }

    private static SliceJob CreateProcessingJob(
        Guid workerId,
        DateTime leaseExpiresAt,
        bool includeLeaseToken = true)
    {
        SliceJob job = CreateCanonicalJob(SliceJobStatus.Processing, SlicerEngineType.OrcaSlicer);
        job.WorkerId = workerId;
        job.StartedAt = Now.UtcDateTime.AddMinutes(-1);
        job.ClaimToken = Guid.NewGuid();
        job.LeaseToken = includeLeaseToken ? job.ClaimToken : null;
        job.LeaseFence = includeLeaseToken ? 1 : 0;
        job.LeaseExpiresAt = leaseExpiresAt;
        return job;
    }

    private static SliceJob CreateCompletedJob(
        SlicerEngineType engine,
        int? durationSeconds,
        bool includeCompletion = true)
    {
        SliceJob job = CreateCanonicalJob(SliceJobStatus.Completed, engine);
        if (durationSeconds.HasValue)
        {
            job.StartedAt = Now.UtcDateTime.AddMinutes(-10);
            job.CompletedAt = includeCompletion
                ? job.StartedAt.Value.AddSeconds(durationSeconds.Value)
                : null;
        }
        else
        {
            job.StartedAt = null;
            job.CompletedAt = Now.UtcDateTime;
        }

        return job;
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
            CreatedAt = Now.UtcDateTime,
            UpdatedAt = Now.UtcDateTime,
            QueuedAt = Now.UtcDateTime,
        };
    }

    public async ValueTask DisposeAsync()
    {
        await _connection.DisposeAsync();
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed class QueueQueryInterceptor : DbCommandInterceptor
    {
        private readonly List<string> _commands = [];

        public IReadOnlyList<string> Commands => _commands;

        public IEnumerable<string> SliceJobCommands =>
            _commands.Where(command => command.Contains("SliceJobs", StringComparison.Ordinal));

        public IEnumerable<string> WorkerCommands =>
            _commands.Where(command => command.Contains("Workers", StringComparison.Ordinal));

        public void Reset() => _commands.Clear();

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            _commands.Add(command.CommandText);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
