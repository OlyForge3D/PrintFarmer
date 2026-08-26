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
    private static readonly DateTimeOffset Now = new(2026, 8, 7, 20, 0, 0, TimeSpan.Zero);
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly QueueQueryInterceptor _interceptor = new();

    [Theory]
    [InlineData("OrcaSlicer", SlicerEngineType.OrcaSlicer)]
    [InlineData("PrusaSlicer", SlicerEngineType.PrusaSlicer)]
    [InlineData("SuperSlicer", SlicerEngineType.SuperSlicer)]
    [InlineData("Cura", SlicerEngineType.Cura)]
    [InlineData(null, SlicerEngineType.OrcaSlicer)]
    [InlineData("", SlicerEngineType.OrcaSlicer)]
    [InlineData(" ", SlicerEngineType.OrcaSlicer)]
    [InlineData("prusaslicer", SlicerEngineType.OrcaSlicer)]
    [InlineData("PrusaSlicer ", SlicerEngineType.OrcaSlicer)]
    [InlineData(" PrusaSlicer", SlicerEngineType.OrcaSlicer)]
    [InlineData("Unknown", SlicerEngineType.OrcaSlicer)]
    public async Task SaveChangesAsync_PersistsNormalizedEngine_MatchingResolvePersistedNameFallback(
        string? engineName,
        SlicerEngineType expectedNormalizedEngine)
    {
        await using SlicerDbContext context = await CreateEmptyContextAsync();
        SliceJob job = CreateJob(SliceJobStatus.Queued, SlicerEngineType.PrusaSlicer, engineName);
        _ = context.SliceJobs.Add(job);

        _ = await context.SaveChangesAsync();

        job.NormalizedEngine.Should().Be((int)expectedNormalizedEngine);
        job.NormalizedEngine.Should().Be((int)SlicerEngineNames.ResolvePersistedName(engineName));

        context.ChangeTracker.Clear();
        SliceJob reloaded = await context.SliceJobs.SingleAsync(j => j.Id == job.Id);
        reloaded.NormalizedEngine.Should().Be((int)expectedNormalizedEngine);
    }

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
    public async Task GetAllQueueStatsAsync_WorkerStates_CountsOnlyFreshEnabledRegisteredServices()
    {
        await using SlicerDbContext context = await CreateEmptyContextAsync();
        _ = AddWorker(context, SlicerEngineType.OrcaSlicer, WorkerStatus.Online, totalSlots: 2);
        _ = AddWorker(context, SlicerEngineType.OrcaSlicer, WorkerStatus.Busy, totalSlots: 3);
        _ = AddWorker(context, SlicerEngineType.PrusaSlicer, WorkerStatus.Busy, totalSlots: 1);
        _ = AddWorker(context, SlicerEngineType.OrcaSlicer, WorkerStatus.Draining, totalSlots: 4);
        _ = AddWorker(context, SlicerEngineType.OrcaSlicer, WorkerStatus.Offline, totalSlots: 5);
        _ = AddWorker(context, SlicerEngineType.OrcaSlicer, WorkerStatus.Error, totalSlots: 6);
        _ = AddWorker(
            context,
            SlicerEngineType.OrcaSlicer,
            WorkerStatus.Online,
            totalSlots: 7,
            isDisabled: true);
        _ = AddWorker(
            context,
            SlicerEngineType.OrcaSlicer,
            WorkerStatus.Online,
            totalSlots: 8,
            lastHeartbeat: Now.UtcDateTime.AddSeconds(-61));
        _ = AddWorker(
            context,
            SlicerEngineType.OrcaSlicer,
            WorkerStatus.Online,
            totalSlots: 9,
            hasHeartbeat: false);
        _ = AddWorker(
            context,
            SlicerEngineType.OrcaSlicer,
            WorkerStatus.Online,
            totalSlots: 10,
            serviceLastSeen: Now.UtcDateTime.AddSeconds(-61));
        _ = AddWorker(
            context,
            SlicerEngineType.OrcaSlicer,
            WorkerStatus.Online,
            totalSlots: 11,
            serviceStatus: WorkerStatus.Offline);
        _ = AddWorker(
            context,
            SlicerEngineType.Cura,
            WorkerStatus.Online,
            totalSlots: 0,
            capabilitiesJson: """{"engine":"orcaslicer","capabilities":[]}""");
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
        Worker liveWorker = AddWorker(
            context,
            SlicerEngineType.OrcaSlicer,
            WorkerStatus.Online,
            totalSlots: 2);
        Worker staleWorker = AddWorker(
            context,
            SlicerEngineType.OrcaSlicer,
            WorkerStatus.Busy,
            totalSlots: 10,
            lastHeartbeat: Now.UtcDateTime.AddSeconds(-61));
        context.SliceJobs.AddRange(
            CreateCanonicalJob(SliceJobStatus.Queued, SlicerEngineType.OrcaSlicer),
            CreateCanonicalJob(SliceJobStatus.Queued, SlicerEngineType.OrcaSlicer),
            CreateCanonicalJob(SliceJobStatus.Queued, SlicerEngineType.OrcaSlicer),
            CreateProcessingJob(liveWorker.Id, Now.UtcDateTime.AddMinutes(5)),
            CreateProcessingJob(liveWorker.Id, Now.UtcDateTime.AddSeconds(-1)),
            CreateProcessingJob(staleWorker.Id, Now.UtcDateTime.AddMinutes(5)),
            CreateProcessingJob(liveWorker.Id, Now.UtcDateTime.AddMinutes(5), includeLeaseToken: false),
            CreateCompletedJob(SlicerEngineType.OrcaSlicer, durationSeconds: 10.25),
            CreateCompletedJob(SlicerEngineType.OrcaSlicer, durationSeconds: 19.75),
            CreateCompletedJob(SlicerEngineType.OrcaSlicer, durationSeconds: null),
            CreateCompletedJob(SlicerEngineType.OrcaSlicer, durationSeconds: 30, includeCompletion: false),
            CreateCompletedJob(SlicerEngineType.OrcaSlicer, durationSeconds: -5),
            CreateCompletedJob(
                SlicerEngineType.OrcaSlicer,
                durationSeconds: 100,
                startedAt: Now.UtcDateTime.AddDays(-31)));
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        DbSlicerJobQueue queue = CreateQueue(context);

        SlicerQueueStats stats =
            (await queue.GetAllQueueStatsAsync())[SlicerEngineType.OrcaSlicer];

        stats.QueuedJobs.Should().Be(3);
        stats.ProcessingJobs.Should().Be(4);
        stats.CompletedJobs.Should().Be(6);
        stats.ActiveWorkers.Should().Be(1);
        stats.AverageProcessingTimeSeconds.Should().Be(15);
        stats.EstimatedWaitTime.Should().Be(TimeSpan.FromSeconds(30));
        stats.LastUpdated.Should().Be(Now.UtcDateTime);
    }

    [Fact]
    public async Task GetAllQueueStatsAsync_NoCapacityHistoryOrWork_ReturnsDocumentedSentinels()
    {
        await using SlicerDbContext context = await CreateEmptyContextAsync();
        _ = AddWorker(context, SlicerEngineType.OrcaSlicer, WorkerStatus.Online, totalSlots: 1);
        _ = AddWorker(context, SlicerEngineType.PrusaSlicer, WorkerStatus.Online, totalSlots: 1);
        _ = AddWorker(context, SlicerEngineType.SuperSlicer, WorkerStatus.Online, totalSlots: 0);
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
    public async Task GetAllQueueStatsAsync_CapacityAboveIntMaximum_DoesNotOverflow()
    {
        await using SlicerDbContext context = await CreateEmptyContextAsync();
        _ = AddWorker(
            context,
            SlicerEngineType.OrcaSlicer,
            WorkerStatus.Online,
            totalSlots: int.MaxValue);
        _ = AddWorker(
            context,
            SlicerEngineType.OrcaSlicer,
            WorkerStatus.Busy,
            totalSlots: int.MaxValue);
        context.SliceJobs.AddRange(
            CreateCanonicalJob(SliceJobStatus.Queued, SlicerEngineType.OrcaSlicer),
            CreateCompletedJob(SlicerEngineType.OrcaSlicer, durationSeconds: 2));
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        DbSlicerJobQueue queue = CreateQueue(context);

        SlicerQueueStats stats =
            (await queue.GetAllQueueStatsAsync())[SlicerEngineType.OrcaSlicer];

        stats.ActiveWorkers.Should().Be(2);
        stats.EstimatedWaitTime.Should().Be(TimeSpan.FromSeconds(2));
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
        countCommand.Contains("NormalizedEngine", StringComparison.OrdinalIgnoreCase).Should().BeTrue();

        // The persisted NormalizedEngine column removes the need to re-derive the engine from
        // SlicerEngineName (and its Collate expression) on every query — that's the whole point
        // of the covering (NormalizedEngine, Status) index.
        countCommand.Should().NotContain("COLLATE");
        countCommand.Should().NotContain("SlicerEngineName");

        string metricCommand = _interceptor.SliceJobCommands.Single(command =>
            command.Contains("JOIN", StringComparison.OrdinalIgnoreCase));
        metricCommand.Contains("AVG(", StringComparison.OrdinalIgnoreCase).Should().BeTrue();
        metricCommand.Contains("GROUP BY", StringComparison.OrdinalIgnoreCase).Should().BeTrue();

        // Unlike the count aggregate, the timing/worker metrics query is unchanged by this fix
        // and still derives the engine from SlicerEngineName via a Collate expression.
        metricCommand.Contains("COLLATE BINARY", StringComparison.OrdinalIgnoreCase).Should().BeTrue();

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
        DateTime workerCutoff = Now.UtcDateTime.AddSeconds(-WorkerStatus.OnlineFreshnessSeconds);
        DateTime timingCutoff = Now.UtcDateTime.AddDays(-30);

        string workerSql = EfSliceJobRepository
            .BuildQueueWorkerMetricQuery(context, workerCutoff)
            .ToQueryString();
        string jobSql = EfSliceJobRepository
            .BuildQueueJobMetricQuery(context, Now.UtcDateTime, workerCutoff, timingCutoff)
            .ToQueryString();

        workerSql.Should().ContainEquivalentOf("GROUP BY");
        workerSql.Should().ContainEquivalentOf("COUNT");
        workerSql.Should().ContainEquivalentOf("SUM");
        workerSql.Should().ContainEquivalentOf("SlicerServices");
        workerSql.Should().NotContainEquivalentOf("CapabilitiesJson");
        jobSql.Should().ContainEquivalentOf("LEFT JOIN");
        jobSql.Should().ContainEquivalentOf("GROUP BY");
        jobSql.Should().ContainEquivalentOf("AVG");
        if (provider == "sqlserver")
        {
            jobSql.Should().ContainEquivalentOf("DATEDIFF(second");
            jobSql.Should().ContainEquivalentOf("DATEPART(millisecond");
        }
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

    private static Farm.Slicer.Module.Domain.Worker AddWorker(
        SlicerDbContext context,
        SlicerEngineType engine,
        string status,
        int totalSlots,
        bool isDisabled = false,
        DateTime? lastHeartbeat = null,
        bool hasHeartbeat = true,
        DateTime? serviceLastSeen = null,
        string? serviceStatus = null,
        string capabilitiesJson = "[]")
    {
        DateTime timestamp = Now.UtcDateTime;
        Guid serviceId = Guid.NewGuid();
        var worker = new Worker
        {
            Id = Guid.NewGuid(),
            ServiceId = serviceId.ToString(),
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
        var service = new SlicerService
        {
            Id = serviceId,
            Name = "Queue metric service",
            SlicerType = engine switch
            {
                SlicerEngineType.OrcaSlicer => (int)SlicerType.OrcaSlicer,
                SlicerEngineType.PrusaSlicer => (int)SlicerType.PrusaSlicer,
                SlicerEngineType.SuperSlicer => (int)SlicerType.SuperSlicer,
                SlicerEngineType.Cura => (int)SlicerType.Cura,
                _ => throw new ArgumentOutOfRangeException(nameof(engine), engine, "Unknown engine."),
            },
            Status = serviceStatus ?? status,
            LastSeen = serviceLastSeen ?? timestamp,
            CreatedAt = timestamp,
            UpdatedAt = timestamp,
        };
        context.Workers.Add(worker);
        context.SlicerServices.Add(service);
        return worker;
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
        double? durationSeconds,
        bool includeCompletion = true,
        DateTime? startedAt = null)
    {
        SliceJob job = CreateCanonicalJob(SliceJobStatus.Completed, engine);
        if (durationSeconds.HasValue)
        {
            job.StartedAt = startedAt ?? Now.UtcDateTime.AddMinutes(-10);
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
