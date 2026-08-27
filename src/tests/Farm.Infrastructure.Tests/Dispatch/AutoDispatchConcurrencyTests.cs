using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Dtos.PrintQueue;
using Farm.Infrastructure.Services.Queue.Dispatch;
using Farm.Infrastructure.Services.SignalR;
using Farm.Infrastructure.Tests.Builders;
using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Farm.Infrastructure.Tests.Dispatch;

/// <summary>
/// Tests for race conditions and concurrency edge cases in auto-dispatch.
///
/// The AutoDispatchBackgroundService atomically claims jobs under a short selection gate
/// and runs claimed dispatches under a configurable async capacity semaphore. These tests
/// verify that concurrent idle events do not cause double-assignment and that mid-dispatch failures
/// (cancellation, printer going offline) are handled gracefully.
/// </summary>
public class AutoDispatchConcurrencyTests : IDisposable
{
    private readonly string _connectionString;
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;
    private readonly AutoDispatchTrigger _trigger;
    private readonly Mock<IHubContext<PrinterHub>> _hubMock;
    private readonly Mock<IClientProxy> _clientProxyMock;
    private readonly DispatchConcurrencyCoordinator _concurrencyCoordinator = new();
    private readonly Guid _folderId = Guid.NewGuid();

    // Track which (jobId, printerId) pairs were dispatched
    private readonly List<(Guid jobId, Guid printerId)> _dispatchedPairs = [];
    private readonly object _dispatchLock = new();

    public AutoDispatchConcurrencyTests()
    {
        _connectionString =
            $"Data Source=auto-dispatch-{Guid.NewGuid():N};Mode=Memory;Cache=Shared;Default Timeout=5";
        _connection = new SqliteConnection(_connectionString);
        _connection.Open();
        TestSqlitePragmaEnforcer.EnsureForeignKeysEnabled(_connection);

        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connectionString)
            .Options;

        _db = new AppDbContext(options);
        _db.Database.EnsureCreated();

        SeedRootFolder();

        _trigger = new AutoDispatchTrigger();

        _clientProxyMock = new Mock<IClientProxy>();
        var hubClientsMock = new Mock<IHubClients>();
        hubClientsMock.Setup(c => c.All).Returns(_clientProxyMock.Object);
        hubClientsMock.Setup(c => c.Group(It.IsAny<string>()))
            .Returns(_clientProxyMock.Object);
        _hubMock = new Mock<IHubContext<PrinterHub>>();
        _hubMock.Setup(h => h.Clients).Returns(hubClientsMock.Object);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
        _concurrencyCoordinator.Dispose();
        GC.SuppressFinalize(this);
    }

    private void SeedRootFolder()
    {
        _db.Set<FolderNode>().Add(new FolderNode
        {
            Id = _folderId,
            Path = "/",
            FolderType = "gcode",
            CreatedAt = DateTime.UtcNow,
        });
        _db.SaveChanges();
    }

    private void SeedSettings(
        bool enabled = true,
        AutoDispatchMode mode = AutoDispatchMode.Auto,
        int idleThresholdSeconds = 0,
        int maxConcurrentDispatches = 5)
    {
        DispatchSettings? existing = _db.DispatchSettings.FirstOrDefault();
        if (existing is not null)
        {
            existing.AutoDispatchEnabled = enabled;
            existing.AutoDispatchMode = mode;
            existing.IdleThresholdSeconds = idleThresholdSeconds;
            existing.MaxConcurrentDispatches = maxConcurrentDispatches;
            existing.MinimumScoreThreshold = 0.1;
        }
        else
        {
            _db.DispatchSettings.Add(new DispatchSettings
            {
                Id = 1,
                AutoDispatchEnabled = enabled,
                AutoDispatchMode = mode,
                IdleThresholdSeconds = idleThresholdSeconds,
                MinimumScoreThreshold = 0.1,
                MaxConcurrentDispatches = maxConcurrentDispatches,
                UpdatedAt = DateTime.UtcNow,
            });
        }

        _db.SaveChanges();
    }

    private Guid SeedPrinter(string name, int index)
    {
        Guid id = Guid.NewGuid();
        Guid manufacturerId = Guid.NewGuid();
        Guid modelId = Guid.NewGuid();

        var manufacturer = new Manufacturer
        {
            Id = manufacturerId,
            Name = $"TestMfg-{Guid.NewGuid():N}",
        };
        _db.Manufacturers.Add(manufacturer);
        _db.SaveChanges();

        var model = new PrinterModel
        {
            Id = modelId,
            Name = $"TestModel-{index}",
            ManufacturerId = manufacturerId,
        };
        _db.PrinterModels.Add(model);
        _db.SaveChanges();

        Printer printer = new PrinterBuilder()
            .WithId(id)
            .WithName(name)
            .WithServerUrl($"http://192.168.1.{index}")
            .Build();
        printer.ManufacturerId = manufacturerId;
        printer.ModelId = modelId;
        printer.IsEnabled = true;
        printer.IsAvailable = true;
        _db.Printers.Add(printer);
        _db.SaveChanges();
        return id;
    }

    private Guid SeedQueuedJob(string name, int priority = 0, int queuePosition = 1)
    {
        Guid gcodeFileId = Guid.NewGuid();
        Guid jobId = Guid.NewGuid();

        _db.GcodeFiles.Add(new GcodeFile
        {
            Id = gcodeFileId,
            Name = $"{name}.gcode",
            FileName = $"{Guid.NewGuid()}.gcode",
            FilePath = "/gcode/",
            FolderId = _folderId,
            FileHash = Guid.NewGuid().ToString()[..8],
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            UploadedAt = DateTime.UtcNow,
        });

        _db.PrintJobs.Add(new PrintJob
        {
            Id = jobId,
            Name = name,
            GcodeFileId = gcodeFileId,
            Status = PrintJobStatus.Queued,
            AssignedPrinterId = null,
            Priority = priority,
            QueuePosition = queuePosition,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            QueuedAt = DateTime.UtcNow,
        });

        _db.SaveChanges();
        return jobId;
    }

    private ServiceProvider BuildServiceProvider(Mock<IDispatchScorer> scorerMock, Mock<IJobDispatchService> dispatchMock)
    {
        ServiceCollection services = new();
        services.AddScoped<AppDbContext>(_ =>
        {
            var opts = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite(_connectionString)
                .Options;
            return new AppDbContext(opts);
        });
        services.AddScoped<IDispatchScorer>(_ => scorerMock.Object);
        services.AddScoped<IJobDispatchService>(_ => dispatchMock.Object);

        return services.BuildServiceProvider();
    }

    // =========================================================================
    // RACE CONDITION TESTS
    // =========================================================================

    [Fact]
    [Trait("Category", "Dispatch")]
    [Trait("Phase", "2")]
    public async Task TwoPrintersIdleSimultaneously_SameJobNotAssignedTwice()
    {
        // Arrange: 2 printers, 1 queued job — only ONE should get it
        SeedSettings(enabled: true, mode: AutoDispatchMode.Auto, idleThresholdSeconds: 0);
        Guid printer1Id = SeedPrinter("Printer-1", 10);
        Guid printer2Id = SeedPrinter("Printer-2", 11);
        Guid jobId = SeedQueuedJob("contested-job");

        foreach (Guid printerId in new[] { printer1Id, printer2Id })
        {
            Printer printer = _db.Printers.Single(value => value.Id == printerId);
            printer.AutoDispatchEnabled = true;
            printer.DispatchState = new PrinterDispatchState
            {
                PrinterId = printerId,
                AutoDispatchState = AutoDispatchState.Ready,
            };
        }

        _db.SaveChanges();

        var scorerMock = new Mock<IDispatchScorer>();
        var dispatchMock = new Mock<IJobDispatchService>();
        var dispatchObserved = new TaskCompletionSource<(Guid JobId, Guid PrinterId)>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        // Both printers score well for the job
        DispatchScore printer1Score = new(printer1Id, "Printer-1", 85.0, new Dictionary<string, FactorScore>(), false, []);
        DispatchScore printer2Score = new(printer2Id, "Printer-2", 82.0, new Dictionary<string, FactorScore>(), false, []);
        scorerMock
            .Setup(s => s.ScorePrintersForJobAsync(jobId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid jId, CancellationToken _) => [printer1Score, printer2Score]);
        scorerMock
            .Setup(s => s.ScorePrinterForJobAsync(jobId, printer1Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(printer1Score);
        scorerMock
            .Setup(s => s.ScorePrinterForJobAsync(jobId, printer2Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(printer2Score);

        // Track dispatch calls and update DB to simulate real dispatch behavior
        dispatchMock
            .Setup(d => d.DispatchJobAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<DispatchScore>(), It.IsAny<CancellationToken>()))
            .Returns((Guid jId, Guid pId, string _, DispatchScore _, CancellationToken _) =>
            {
                lock (_dispatchLock)
                {
                    _dispatchedPairs.Add((jId, pId));

                    // This shared fixture context is not thread-safe. Serialize the mock's
                    // durable assignment so it accurately models the real scoped dispatch service.
                    PrintJob? job = _db.PrintJobs.FirstOrDefault(j => j.Id == jId);
                    if (job is not null)
                    {
                        job.AssignedPrinterId = pId;
                        job.Status = PrintJobStatus.Starting;
                        _db.SaveChanges();
                    }
                }

                dispatchObserved.TrySetResult((jId, pId));
                return Task.FromResult(new QueuedPrintJobDto());
            });

        await using ServiceProvider provider = BuildServiceProvider(scorerMock, dispatchMock);
        IServiceScopeFactory scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

        using CancellationTokenSource shutdown = new();
        using AutoDispatchBackgroundService svc = new(
            _trigger, scopeFactory, _concurrencyCoordinator, _hubMock.Object,
            NullLogger<AutoDispatchBackgroundService>.Instance);
        var startGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task ProcessAfterStartGateAsync(Guid printerId)
        {
            await startGate.Task.WaitAsync(shutdown.Token);
            await svc.ProcessPrinterIdleAsync(
                printerId,
                skipIdleThreshold: false,
                shutdown.Token);
        }

        Task printer1Cycle = ProcessAfterStartGateAsync(printer1Id);
        Task printer2Cycle = ProcessAfterStartGateAsync(printer2Id);
        Task allCycles = Task.WhenAll(printer1Cycle, printer2Cycle);

        startGate.TrySetResult();

        try
        {
            (Guid observedJobId, _) = await dispatchObserved.Task.WaitAsync(TimeSpan.FromSeconds(10));
            observedJobId.Should().Be(jobId, "the test must observe the contested job reaching real dispatch");
            await allCycles.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            await shutdown.CancelAsync();
            await allCycles.WaitAsync(TimeSpan.FromSeconds(10));
        }

        lock (_dispatchLock)
        {
            int timesJobDispatched = _dispatchedPairs.Count(p => p.jobId == jobId);
            timesJobDispatched.Should().Be(1,
                "the atomic selection claim should prevent two printers from grabbing the same job");
        }
    }

    [Fact]
    [Trait("Category", "Dispatch")]
    [Trait("Phase", "2")]
    public async Task MultiplePrintersIdle_MultipleJobs_EachGetsUniqueJob()
    {
        SeedSettings(enabled: true, mode: AutoDispatchMode.Auto, idleThresholdSeconds: 0, maxConcurrentDispatches: 5);
        Guid p1 = SeedPrinter("P1", 20);
        Guid p2 = SeedPrinter("P2", 21);
        Guid p3 = SeedPrinter("P3", 22);
        Guid j1 = SeedQueuedJob("Job-1", priority: 0, queuePosition: 1);
        Guid j2 = SeedQueuedJob("Job-2", priority: 0, queuePosition: 2);
        Guid j3 = SeedQueuedJob("Job-3", priority: 0, queuePosition: 3);

        foreach (Guid printerId in new[] { p1, p2, p3 })
        {
            Printer printer = _db.Printers.Single(value => value.Id == printerId);
            printer.AutoDispatchEnabled = true;
            printer.DispatchState = new PrinterDispatchState
            {
                PrinterId = printerId,
                AutoDispatchState = AutoDispatchState.Ready,
            };
        }

        _db.SaveChanges();

        var scorerMock = new Mock<IDispatchScorer>();
        var dispatchMock = new Mock<IJobDispatchService>();
        Dictionary<Guid, DispatchScore> scoresByPrinter = new()
        {
            [p1] = new DispatchScore(p1, "P1", 80.0, new Dictionary<string, FactorScore>(), false, []),
            [p2] = new DispatchScore(p2, "P2", 75.0, new Dictionary<string, FactorScore>(), false, []),
            [p3] = new DispatchScore(p3, "P3", 70.0, new Dictionary<string, FactorScore>(), false, []),
        };
        scorerMock
            .Setup(s => s.ScorePrintersForJobAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid _, CancellationToken _) => scoresByPrinter.Values.ToList());
        scorerMock
            .Setup(s => s.ScorePrinterForJobAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid _, Guid printerId, CancellationToken _) =>
                scoresByPrinter.TryGetValue(printerId, out DispatchScore? score) ? score : null);
        dispatchMock
            .Setup(d => d.DispatchJobAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<DispatchScore>(), It.IsAny<CancellationToken>()))
            .Returns((Guid jobId, Guid printerId, string _, DispatchScore _, CancellationToken _) =>
            {
                lock (_dispatchLock)
                {
                    _dispatchedPairs.Add((jobId, printerId));

                    // The production service uses one scoped DbContext per dispatch. This
                    // lock gives the shared test fixture context the equivalent safety.
                    PrintJob job = _db.PrintJobs.Single(value => value.Id == jobId);
                    job.AssignedPrinterId = printerId;
                    job.Status = PrintJobStatus.Starting;
                    _db.SaveChanges();
                }

                return Task.FromResult(new QueuedPrintJobDto());
            });

        await using ServiceProvider provider = BuildServiceProvider(scorerMock, dispatchMock);
        IServiceScopeFactory scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();
        using CancellationTokenSource shutdown = new();
        using AutoDispatchBackgroundService svc = new(
            _trigger,
            scopeFactory,
            _concurrencyCoordinator,
            _hubMock.Object,
            NullLogger<AutoDispatchBackgroundService>.Instance);
        var startGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task ProcessAfterStartGateAsync(Guid printerId)
        {
            await startGate.Task.WaitAsync(shutdown.Token);
            await svc.ProcessPrinterIdleAsync(
                printerId,
                skipIdleThreshold: false,
                shutdown.Token);
        }

        Task[] cycles =
        [
            ProcessAfterStartGateAsync(p1),
            ProcessAfterStartGateAsync(p2),
            ProcessAfterStartGateAsync(p3),
        ];
        Task allCycles = Task.WhenAll(cycles);

        try
        {
            startGate.TrySetResult();
            // Widened from 10s: under full test-suite parallelism (dozens of concurrent
            // hosts), these background cycles can be delayed waiting for CPU/thread-pool time.
            await allCycles.WaitAsync(TimeSpan.FromSeconds(30));
        }
        finally
        {
            await shutdown.CancelAsync();
            await allCycles.WaitAsync(TimeSpan.FromSeconds(30));
        }

        lock (_dispatchLock)
        {
            _dispatchedPairs.Should().HaveCount(3);
            _dispatchedPairs.Select(pair => pair.jobId).Should().BeEquivalentTo([j1, j2, j3]);
            _dispatchedPairs.Select(pair => pair.jobId).Should().OnlyHaveUniqueItems();
            _dispatchedPairs.Select(pair => pair.printerId).Should().BeEquivalentTo([p1, p2, p3]);
            _dispatchedPairs.Select(pair => pair.printerId).Should().OnlyHaveUniqueItems();
        }
    }

    [Fact]
    [Trait("Category", "Dispatch")]
    [Trait("Phase", "2")]
    public async Task ExecuteAsync_MaxConcurrentTwo_OverlapsTwoBlocksThirdAndContinues()
    {
        SeedSettings(
            enabled: true,
            mode: AutoDispatchMode.Auto,
            idleThresholdSeconds: 0,
            maxConcurrentDispatches: 2);
        Guid[] printers =
        [
            SeedPrinter("P1-capacity", 30),
            SeedPrinter("P2-capacity", 31),
            SeedPrinter("P3-capacity", 32),
        ];
        Guid[] jobs =
        [
            SeedQueuedJob("Job-capacity-1", queuePosition: 1),
            SeedQueuedJob("Job-capacity-2", queuePosition: 2),
            SeedQueuedJob("Job-capacity-3", queuePosition: 3),
        ];
        var printerByJob = new Dictionary<Guid, Guid>();
        for (int i = 0; i < printers.Length; i++)
        {
            Printer printer = _db.Printers.Single(value => value.Id == printers[i]);
            printer.AutoDispatchEnabled = true;
            printer.DispatchState = new PrinterDispatchState
            {
                PrinterId = printers[i],
                AutoDispatchState = AutoDispatchState.Ready,
            };
            PrintJob job = _db.PrintJobs.Single(value => value.Id == jobs[i]);
            job.AssignedPrinterId = printers[i];
            printerByJob.Add(jobs[i], printers[i]);
        }

        _db.SaveChanges();
        var scorerMock = new Mock<IDispatchScorer>();
        scorerMock.Setup(value => value.ScorePrintersForJobAsync(
                It.IsAny<Guid>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid jobId, CancellationToken _) =>
            {
                Guid printerId = printerByJob[jobId];
                return
                [
                    new DispatchScore(
                        printerId,
                        $"capacity-{printerId:N}",
                        90,
                        new Dictionary<string, FactorScore>(),
                        false,
                        []),
                ];
            });
        scorerMock.Setup(value => value.ScorePrinterForJobAsync(
                It.IsAny<Guid>(),
                It.IsAny<Guid>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid jobId, Guid printerId, CancellationToken _) =>
                printerByJob.TryGetValue(jobId, out Guid expectedPrinterId) && expectedPrinterId == printerId
                    ? new DispatchScore(
                        printerId,
                        $"capacity-{printerId:N}",
                        90,
                        new Dictionary<string, FactorScore>(),
                        false,
                        [])
                    : null);
        var twoEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var allCapacityAttemptsEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var thirdEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSecond = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseThird = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var startedPrinters = new ConcurrentQueue<Guid>();
        int active = 0;
        int maximumActive = 0;
        int ordinal = 0;
        int capacityAttemptCount = 0;
        var serviceLogger = new CallbackLogger<AutoDispatchBackgroundService>(
            (level, message) =>
            {
                if (level == LogLevel.Debug
                    && message.Contains("waiting for dispatch capacity", StringComparison.Ordinal)
                    && Interlocked.Increment(ref capacityAttemptCount) == printers.Length)
                {
                    allCapacityAttemptsEntered.TrySetResult();
                }
            });

        void ObserveMaximum(int current)
        {
            int observed = Volatile.Read(ref maximumActive);
            while (current > observed)
            {
                int original = Interlocked.CompareExchange(
                    ref maximumActive,
                    current,
                    observed);
                if (original == observed)
                {
                    return;
                }

                observed = original;
            }
        }

        var dispatchMock = new Mock<IJobDispatchService>();
        dispatchMock.Setup(value => value.DispatchJobAsync(
                It.IsAny<Guid>(),
                It.IsAny<Guid>(),
                It.IsAny<string>(),
                It.IsAny<DispatchScore>(),
                It.IsAny<CancellationToken>()))
            .Returns<Guid, Guid, string, DispatchScore, CancellationToken>(
                async (_, printerId, _, _, cancellationToken) =>
                {
                    int enteredOrdinal = Interlocked.Increment(ref ordinal);
                    int current = Interlocked.Increment(ref active);
                    ObserveMaximum(current);
                    startedPrinters.Enqueue(printerId);
                    if (current == 2)
                    {
                        twoEntered.TrySetResult();
                    }

                    try
                    {
                        switch (enteredOrdinal)
                        {
                            case 1:
                                await releaseFirst.Task.WaitAsync(cancellationToken);
                                break;
                            case 2:
                                await releaseSecond.Task.WaitAsync(cancellationToken);
                                break;
                            case 3:
                                thirdEntered.TrySetResult();
                                await releaseThird.Task.WaitAsync(cancellationToken);
                                break;
                            default:
                                throw new InvalidOperationException(
                                    "Unexpected extra dispatch attempt.");
                        }

                        return new QueuedPrintJobDto
                        {
                            DispatchResult = new DispatchAttemptResultDto
                            {
                                Outcome = DispatchAttemptOutcome.Accepted,
                            },
                        };
                    }
                    finally
                    {
                        _ = Interlocked.Decrement(ref active);
                    }
                });
        var allEventsSent = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        int eventCount = 0;
        _clientProxyMock.Setup(value => value.SendCoreAsync(
                "jobautodispatched",
                It.IsAny<object?[]>(),
                It.IsAny<CancellationToken>()))
            .Callback(() =>
            {
                if (Interlocked.Increment(ref eventCount) == 3)
                {
                    allEventsSent.TrySetResult();
                }
            })
            .Returns(Task.CompletedTask);

        await using ServiceProvider provider = BuildServiceProvider(scorerMock, dispatchMock);
        using AutoDispatchBackgroundService service = new(
            _trigger,
            provider.GetRequiredService<IServiceScopeFactory>(),
            _concurrencyCoordinator,
            _hubMock.Object,
            serviceLogger);

        await service.StartAsync(CancellationToken.None);
        try
        {
            await Task.WhenAll(twoEntered.Task, allCapacityAttemptsEntered.Task)
                .WaitAsync(TimeSpan.FromSeconds(10));
            thirdEntered.Task.IsCompleted.Should().BeFalse();
            Volatile.Read(ref maximumActive).Should().Be(2);

            releaseFirst.TrySetResult();
            await thirdEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));

            Volatile.Read(ref active).Should().Be(2);
            Volatile.Read(ref maximumActive).Should().Be(2);
            releaseSecond.TrySetResult();
            releaseThird.TrySetResult();
            await allEventsSent.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            releaseFirst.TrySetResult();
            releaseSecond.TrySetResult();
            releaseThird.TrySetResult();
            await service.StopAsync(CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(10));
        }

        startedPrinters.Should().BeEquivalentTo(printers);
        dispatchMock.Verify(value => value.DispatchJobAsync(
                It.IsAny<Guid>(),
                It.IsAny<Guid>(),
                It.IsAny<string>(),
                It.IsAny<DispatchScore>(),
                It.IsAny<CancellationToken>()),
            Times.Exactly(3));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [Trait("Category", "Dispatch")]
    [Trait("Phase", "2")]
    public async Task ProcessPrinterIdleAsync_ConfiguredCapacity_BoundsConcurrentSlowUploads(
        int configuredCapacity)
    {
        const int dispatchCount = 4;
        SeedSettings(
            enabled: true,
            mode: AutoDispatchMode.Auto,
            idleThresholdSeconds: 0,
            maxConcurrentDispatches: configuredCapacity);
        var printerByJob = new Dictionary<Guid, Guid>();
        var printers = new List<Guid>();
        for (int index = 0; index < dispatchCount; index++)
        {
            Guid printerId =
                SeedPrinter($"configured-capacity-{index}", 300 + index);
            Guid jobId =
                SeedQueuedJob(
                    $"configured-capacity-job-{index}",
                    queuePosition: index + 1);
            Printer printer =
                _db.Printers.Single(value => value.Id == printerId);
            printer.AutoDispatchEnabled = true;
            printer.DispatchState = new PrinterDispatchState
            {
                PrinterId = printerId,
                AutoDispatchState = AutoDispatchState.Ready,
            };
            PrintJob job =
                _db.PrintJobs.Single(value => value.Id == jobId);
            job.AssignedPrinterId = printerId;
            printerByJob.Add(jobId, printerId);
            printers.Add(printerId);
        }

        _db.SaveChanges();
        var scorerMock = new Mock<IDispatchScorer>();
        scorerMock.Setup(value => value.ScorePrintersForJobAsync(
                It.IsAny<Guid>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid jobId, CancellationToken _) =>
            {
                Guid printerId = printerByJob[jobId];
                return
                [
                    new DispatchScore(
                        printerId,
                        $"configured-{printerId:N}",
                        90,
                        new Dictionary<string, FactorScore>(),
                        false,
                        []),
                ];
            });
        scorerMock.Setup(value => value.ScorePrinterForJobAsync(
                It.IsAny<Guid>(),
                It.IsAny<Guid>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid jobId, Guid printerId, CancellationToken _) =>
                printerByJob.TryGetValue(jobId, out Guid expectedPrinterId) && expectedPrinterId == printerId
                    ? new DispatchScore(
                        printerId,
                        $"configured-{printerId:N}",
                        90,
                        new Dictionary<string, FactorScore>(),
                        false,
                        [])
                    : null);
        var configuredCapacityReached = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseUploads = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        int active = 0;
        int maximumActive = 0;

        void ObserveMaximum(int current)
        {
            int observed = Volatile.Read(ref maximumActive);
            while (current > observed)
            {
                int original = Interlocked.CompareExchange(
                    ref maximumActive,
                    current,
                    observed);
                if (original == observed)
                {
                    return;
                }

                observed = original;
            }
        }

        var dispatchMock = new Mock<IJobDispatchService>();
        dispatchMock.Setup(value => value.DispatchJobAsync(
                It.IsAny<Guid>(),
                It.IsAny<Guid>(),
                It.IsAny<string>(),
                It.IsAny<DispatchScore>(),
                It.IsAny<CancellationToken>()))
            .Returns<Guid, Guid, string, DispatchScore, CancellationToken>(
                async (_, _, _, _, cancellationToken) =>
                {
                    int current = Interlocked.Increment(ref active);
                    ObserveMaximum(current);
                    if (current == configuredCapacity)
                    {
                        configuredCapacityReached.TrySetResult();
                    }

                    try
                    {
                        await releaseUploads.Task.WaitAsync(cancellationToken);
                        return new QueuedPrintJobDto
                        {
                            DispatchResult = new DispatchAttemptResultDto
                            {
                                Outcome = DispatchAttemptOutcome.Accepted,
                            },
                        };
                    }
                    finally
                    {
                        _ = Interlocked.Decrement(ref active);
                    }
                });
        _clientProxyMock.Setup(value => value.SendCoreAsync(
                It.IsAny<string>(),
                It.IsAny<object?[]>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        await using ServiceProvider provider =
            BuildServiceProvider(scorerMock, dispatchMock);
        using AutoDispatchBackgroundService service = new(
            _trigger,
            provider.GetRequiredService<IServiceScopeFactory>(),
            _concurrencyCoordinator,
            _hubMock.Object,
            NullLogger<AutoDispatchBackgroundService>.Instance);
        using CancellationTokenSource timeout =
            new(TimeSpan.FromSeconds(15));
        Task[] dispatches = printers
            .Select(printerId => service.ProcessPrinterIdleAsync(
                printerId,
                skipIdleThreshold: true,
                timeout.Token))
            .ToArray();

        try
        {
            await configuredCapacityReached.Task.WaitAsync(timeout.Token);
            _concurrencyCoordinator.InFlightCount
                .Should().Be(configuredCapacity);
            Volatile.Read(ref maximumActive)
                .Should().Be(configuredCapacity);
        }
        finally
        {
            releaseUploads.TrySetResult();
            await Task.WhenAll(dispatches).WaitAsync(timeout.Token);
        }

        Volatile.Read(ref maximumActive).Should().Be(configuredCapacity);
        _concurrencyCoordinator.InFlightCount.Should().Be(0);
        dispatchMock.Verify(value => value.DispatchJobAsync(
                It.IsAny<Guid>(),
                It.IsAny<Guid>(),
                It.IsAny<string>(),
                It.IsAny<DispatchScore>(),
                It.IsAny<CancellationToken>()),
            Times.Exactly(dispatchCount));
    }

    [Fact]
    [Trait("Category", "Dispatch")]
    [Trait("Phase", "2")]
    public async Task AutoDispatchInFlight_BatchDispatchSamePrinter_IsSkipped()
    {
        SeedSettings(
            enabled: true,
            mode: AutoDispatchMode.Auto,
            idleThresholdSeconds: 0,
            maxConcurrentDispatches: 3);
        Guid printerId = SeedPrinter("shared-claim-printer", 33);
        Guid autoJobId = SeedQueuedJob("shared-claim-auto", queuePosition: 1);
        Guid batchJobId = SeedQueuedJob("shared-claim-batch", queuePosition: 2);
        Printer printer = _db.Printers.Single(value => value.Id == printerId);
        printer.AutoDispatchEnabled = true;
        printer.DispatchState = new PrinterDispatchState
        {
            PrinterId = printerId,
            AutoDispatchState = AutoDispatchState.Ready,
        };
        PrintJob autoJob = _db.PrintJobs.Single(value => value.Id == autoJobId);
        autoJob.AssignedPrinterId = printerId;
        _db.SaveChanges();

        var scorerMock = new Mock<IDispatchScorer>();
        scorerMock.Setup(value => value.ScorePrintersForJobAsync(
                It.IsAny<Guid>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(
            [
                new DispatchScore(
                    printerId,
                    printer.Name,
                    90,
                    new Dictionary<string, FactorScore>(),
                    false,
                    []),
            ]);
        scorerMock.Setup(value => value.ScorePrinterForJobAsync(
                It.IsAny<Guid>(),
                printerId,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DispatchScore(
                printerId,
                printer.Name,
                90,
                new Dictionary<string, FactorScore>(),
                false,
                []));
        var dispatchEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseDispatch = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatchMock = new Mock<IJobDispatchService>();
        dispatchMock.Setup(value => value.DispatchJobAsync(
                It.IsAny<Guid>(),
                It.IsAny<Guid>(),
                It.IsAny<string>(),
                It.IsAny<DispatchScore>(),
                It.IsAny<CancellationToken>()))
            .Returns<Guid, Guid, string, DispatchScore, CancellationToken>(
                async (_, _, _, _, cancellationToken) =>
                {
                    dispatchEntered.TrySetResult();
                    await releaseDispatch.Task.WaitAsync(cancellationToken);
                    return new QueuedPrintJobDto
                    {
                        DispatchResult = new DispatchAttemptResultDto
                        {
                            Outcome = DispatchAttemptOutcome.Accepted,
                        },
                    };
                });

        await using ServiceProvider provider =
            BuildServiceProvider(scorerMock, dispatchMock);
        IServiceScopeFactory scopeFactory =
            provider.GetRequiredService<IServiceScopeFactory>();
        using AutoDispatchBackgroundService autoService = new(
            _trigger,
            scopeFactory,
            _concurrencyCoordinator,
            _hubMock.Object,
            NullLogger<AutoDispatchBackgroundService>.Instance);
        BatchDispatchService batchService = new(
            scorerMock.Object,
            _db,
            scopeFactory,
            _concurrencyCoordinator,
            _hubMock.Object,
            NullLogger<BatchDispatchService>.Instance);
        using CancellationTokenSource timeout =
            new(TimeSpan.FromSeconds(10));

        Task autoDispatch = autoService.ProcessPrinterIdleAsync(
            printerId,
            skipIdleThreshold: true,
            timeout.Token);
        await dispatchEntered.Task.WaitAsync(timeout.Token);
        PrintJob batchJob =
            _db.PrintJobs.Single(value => value.Id == batchJobId);
        BatchDispatchResult batchResult =
            await batchService.BatchDispatchAsync(
                new BatchDispatchRequest
                {
                    JobIds = [batchJobId],
                    JobETags = new Dictionary<Guid, string>
                    {
                        [batchJobId] =
                            Convert.ToBase64String(batchJob.RowVersion ?? []),
                    },
                },
                "operator",
                timeout.Token);

        batchResult.DispatchedCount.Should().Be(0);
        batchResult.SkippedCount.Should().Be(1);
        dispatchMock.Invocations.Should().ContainSingle();

        releaseDispatch.TrySetResult();
        await autoDispatch.WaitAsync(timeout.Token);
    }

    [Fact]
    [Trait("Category", "Dispatch")]
    [Trait("Phase", "2")]
    public async Task BatchDispatchAsync_ConfiguredCapacity_DispatchesEveryJobWithinLimit()
    {
        const int configuredCapacity = 2;
        const int dispatchCount = 4;
        SeedSettings(
            enabled: true,
            mode: AutoDispatchMode.Auto,
            idleThresholdSeconds: 0,
            maxConcurrentDispatches: configuredCapacity);
        var printers = new List<(Guid Id, string Name)>();
        var jobs = new List<Guid>();
        for (int index = 0; index < dispatchCount; index++)
        {
            Guid printerId =
                SeedPrinter($"batch-capacity-{index}", 340 + index);
            Printer printer =
                _db.Printers.Single(value => value.Id == printerId);
            printers.Add((printerId, printer.Name));
            jobs.Add(SeedQueuedJob(
                $"batch-capacity-job-{index}",
                queuePosition: index + 1));
        }

        var scorerMock = new Mock<IDispatchScorer>();
        scorerMock.Setup(value => value.ScorePrintersForJobAsync(
                It.IsAny<Guid>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(printers
                .Select((printer, index) => new DispatchScore(
                    printer.Id,
                    printer.Name,
                    100 - index,
                    new Dictionary<string, FactorScore>(),
                    false,
                    []))
                .ToList());
        scorerMock.Setup(value => value.ScorePrinterForJobAsync(
                It.IsAny<Guid>(),
                It.IsAny<Guid>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid _, Guid printerId, CancellationToken _) =>
            {
                int index = printers.FindIndex(p => p.Id == printerId);
                return index < 0
                    ? null
                    : new DispatchScore(
                        printers[index].Id,
                        printers[index].Name,
                        100 - index,
                        new Dictionary<string, FactorScore>(),
                        false,
                        []);
            });
        var capacityReached = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseUploads = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        int active = 0;
        int maximumActive = 0;

        void ObserveMaximum(int current)
        {
            int observed = Volatile.Read(ref maximumActive);
            while (current > observed)
            {
                int original = Interlocked.CompareExchange(
                    ref maximumActive,
                    current,
                    observed);
                if (original == observed)
                {
                    return;
                }

                observed = original;
            }
        }

        var dispatchMock = new Mock<IJobDispatchService>();
        dispatchMock.Setup(value => value.DispatchJobAsync(
                It.IsAny<Guid>(),
                It.IsAny<Guid>(),
                It.IsAny<string>(),
                It.IsAny<DispatchScore>(),
                It.IsAny<CancellationToken>()))
            .Returns<Guid, Guid, string, DispatchScore, CancellationToken>(
                async (_, _, _, _, cancellationToken) =>
                {
                    int current = Interlocked.Increment(ref active);
                    ObserveMaximum(current);
                    if (current == configuredCapacity)
                    {
                        capacityReached.TrySetResult();
                    }

                    try
                    {
                        await releaseUploads.Task.WaitAsync(cancellationToken);
                        return new QueuedPrintJobDto
                        {
                            DispatchResult = new DispatchAttemptResultDto
                            {
                                Outcome = DispatchAttemptOutcome.Accepted,
                            },
                        };
                    }
                    finally
                    {
                        _ = Interlocked.Decrement(ref active);
                    }
                });
        _clientProxyMock.Setup(value => value.SendCoreAsync(
                It.IsAny<string>(),
                It.IsAny<object?[]>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        await using ServiceProvider provider =
            BuildServiceProvider(scorerMock, dispatchMock);
        BatchDispatchService batchService = new(
            scorerMock.Object,
            _db,
            provider.GetRequiredService<IServiceScopeFactory>(),
            _concurrencyCoordinator,
            _hubMock.Object,
            NullLogger<BatchDispatchService>.Instance);
        var request = new BatchDispatchRequest
        {
            JobIds = jobs,
            JobETags = _db.PrintJobs
                .Where(job => jobs.Contains(job.Id))
                .ToDictionary(
                    job => job.Id,
                    job => Convert.ToBase64String(job.RowVersion ?? [])),
        };
        using CancellationTokenSource timeout =
            new(TimeSpan.FromSeconds(15));
        Task<BatchDispatchResult> batchDispatch =
            batchService.BatchDispatchAsync(
                request,
                "operator",
                timeout.Token);

        BatchDispatchResult result;
        try
        {
            await capacityReached.Task.WaitAsync(timeout.Token);
            Volatile.Read(ref maximumActive)
                .Should().Be(configuredCapacity);
            _concurrencyCoordinator.InFlightCount
                .Should().Be(configuredCapacity);
        }
        finally
        {
            releaseUploads.TrySetResult();
        }

        result = await batchDispatch.WaitAsync(timeout.Token);

        result.DispatchedCount.Should().Be(dispatchCount);
        result.Results.Should().HaveCount(dispatchCount);
        Volatile.Read(ref maximumActive).Should().Be(configuredCapacity);
        _concurrencyCoordinator.InFlightCount.Should().Be(0);
    }

    [Fact]
    [Trait("Category", "Dispatch")]
    [Trait("Phase", "2")]
    public async Task ExecuteAsync_StartupAboveLegacyCapacity_ConsidersEveryEligiblePrinter()
    {
        const int printerCount = 80;
        SeedSettings(
            enabled: true,
            mode: AutoDispatchMode.Auto,
            idleThresholdSeconds: 0,
            maxConcurrentDispatches: 8);
        var printers = new List<Guid>(printerCount);
        for (int i = 0; i < printerCount; i++)
        {
            Guid printerId = SeedPrinter($"startup-{i}", 100 + i);
            printers.Add(printerId);
            Printer printer = _db.Printers.Single(value => value.Id == printerId);
            printer.AutoDispatchEnabled = true;
            printer.DispatchState = new PrinterDispatchState
            {
                PrinterId = printerId,
                AutoDispatchState = AutoDispatchState.Ready,
            };
        }

        _db.SaveChanges();
        Guid startupJobId = SeedQueuedJob("startup-capacity-job");
        var scorerMock = new Mock<IDispatchScorer>();
        scorerMock.Setup(value => value.ScorePrintersForJobAsync(
                It.IsAny<Guid>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        var scoredPrinters = new ConcurrentDictionary<Guid, byte>();
        scorerMock.Setup(value => value.ScorePrinterForJobAsync(
                startupJobId,
                It.IsAny<Guid>(),
                It.IsAny<CancellationToken>()))
            .Callback<Guid, Guid, CancellationToken>((_, printerId, _) => scoredPrinters.TryAdd(printerId, 0))
            .ReturnsAsync((DispatchScore?)null);
        var dispatchMock = new Mock<IJobDispatchService>(MockBehavior.Strict);
        var considered = new ConcurrentDictionary<Guid, byte>();
        var allConsidered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _clientProxyMock.Setup(value => value.SendCoreAsync(
                "dispatchfailed",
                It.IsAny<object?[]>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, object?[], CancellationToken>((_, arguments, _) =>
            {
                var failed = (DispatchFailedEvent)arguments[0]!;
                _ = considered.TryAdd(failed.PrinterId, 0);
                if (considered.Count == printerCount)
                {
                    allConsidered.TrySetResult();
                }
            })
            .Returns(Task.CompletedTask);

        await using ServiceProvider provider = BuildServiceProvider(scorerMock, dispatchMock);
        using AutoDispatchBackgroundService service = new(
            _trigger,
            provider.GetRequiredService<IServiceScopeFactory>(),
            _concurrencyCoordinator,
            _hubMock.Object,
            NullLogger<AutoDispatchBackgroundService>.Instance);

        await service.StartAsync(CancellationToken.None);
        try
        {
            await allConsidered.Task.WaitAsync(TimeSpan.FromSeconds(30));
        }
        finally
        {
            await service.StopAsync(CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(10));
        }

        considered.Keys.Should().BeEquivalentTo(printers);
        scoredPrinters.Keys.Should().BeEquivalentTo(
            printers,
            "the targeted single-printer scorer (issue #1705) must be invoked once per eligible printer, " +
            "not merely once per fleet");
        dispatchMock.VerifyNoOtherCalls();
    }

    [Fact]
    [Trait("Category", "Dispatch")]
    [Trait("Phase", "2")]
    public async Task ExecuteAsync_SamePrinterBurst_UsesOneWorkerAndOneCoalescedRerun()
    {
        const int burstSize = 10_000;
        SeedSettings(
            enabled: true,
            mode: AutoDispatchMode.Auto,
            idleThresholdSeconds: 0,
            maxConcurrentDispatches: 4);
        Guid printerId = SeedPrinter("same-printer-burst", 230);
        Guid jobId = SeedQueuedJob("same-printer-burst-job");
        Printer printer = _db.Printers.Single(value => value.Id == printerId);
        printer.AutoDispatchEnabled = true;
        printer.DispatchState = new PrinterDispatchState
        {
            PrinterId = printerId,
            AutoDispatchState = AutoDispatchState.Ready,
        };
        PrintJob job = _db.PrintJobs.Single(value => value.Id == jobId);
        job.AssignedPrinterId = printerId;
        _db.SaveChanges();

        var scorerMock = new Mock<IDispatchScorer>();
        scorerMock.Setup(value => value.ScorePrintersForJobAsync(
                jobId,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(
            [
                new DispatchScore(
                    printerId,
                    printer.Name,
                    90,
                    new Dictionary<string, FactorScore>(),
                    false,
                    []),
            ]);
        scorerMock.Setup(value => value.ScorePrinterForJobAsync(
                jobId,
                printerId,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DispatchScore(
                printerId,
                printer.Name,
                90,
                new Dictionary<string, FactorScore>(),
                false,
                []));
        var dispatchEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseDispatch = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatchMock = new Mock<IJobDispatchService>();
        dispatchMock.Setup(value => value.DispatchJobAsync(
                jobId,
                printerId,
                "system:auto-dispatch",
                It.IsAny<DispatchScore>(),
                It.IsAny<CancellationToken>()))
            .Returns<Guid, Guid, string, DispatchScore, CancellationToken>(
                async (_, _, _, _, cancellationToken) =>
                {
                    dispatchEntered.TrySetResult();
                    await releaseDispatch.Task.WaitAsync(cancellationToken);
                    // Simulate the real DispatchJobService transitioning status so the
                    // coalesced rerun's SelectDispatchPlanAsync sees an active job and returns NoWork.
                    await using AppDbContext updateCtx = new(
                        new DbContextOptionsBuilder<AppDbContext>()
                            .UseSqlite(_connectionString)
                            .Options);
                    PrintJob? dispatched = await updateCtx.PrintJobs.FindAsync(jobId);
                    if (dispatched is not null)
                    {
                        dispatched.Status = PrintJobStatus.Starting;
                        await updateCtx.SaveChangesAsync(CancellationToken.None);
                    }

                    return new QueuedPrintJobDto
                    {
                        DispatchResult = new DispatchAttemptResultDto
                        {
                            Outcome = DispatchAttemptOutcome.Accepted,
                        },
                    };
                });
        var secondEvaluationEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        int evaluationCount = 0;
        var serviceLogger = new CallbackLogger<AutoDispatchBackgroundService>(
            (level, message) =>
            {
                if (level == LogLevel.Debug
                    && message.Contains("skipping idle threshold", StringComparison.Ordinal)
                    && Interlocked.Increment(ref evaluationCount) == 2)
                {
                    secondEvaluationEntered.TrySetResult();
                }
            });
        _clientProxyMock.Setup(value => value.SendCoreAsync(
                It.IsAny<string>(),
                It.IsAny<object?[]>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        await using ServiceProvider provider = BuildServiceProvider(scorerMock, dispatchMock);
        using AutoDispatchBackgroundService service = new(
            _trigger,
            provider.GetRequiredService<IServiceScopeFactory>(),
            _concurrencyCoordinator,
            _hubMock.Object,
            serviceLogger);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));

        await service.StartAsync(CancellationToken.None);
        try
        {
            await dispatchEntered.Task.WaitAsync(timeout.Token);
            for (int i = 0; i < burstSize; i++)
            {
                if ((i & 1) == 0)
                {
                    _trigger.NotifyPrinterIdle(printerId);
                }
                else
                {
                    _trigger.NotifyJobQueued(printerId);
                }
            }

            service.TrackedWorkerCount.Should().Be(1);
            _trigger.IntentStateCount.Should().Be(1);
            _trigger.PendingRerunCount.Should().Be(1);
            dispatchMock.Invocations.Should().ContainSingle();

            releaseDispatch.TrySetResult();
            await secondEvaluationEntered.Task.WaitAsync(timeout.Token);
            while (service.TrackedWorkerCount != 0)
            {
                timeout.Token.ThrowIfCancellationRequested();
                await Task.Yield();
            }
        }
        finally
        {
            releaseDispatch.TrySetResult();
            await service.StopAsync(CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(10));
        }

        Volatile.Read(ref evaluationCount).Should().Be(2);
        service.TrackedWorkerCount.Should().Be(0);
        _trigger.IntentStateCount.Should().Be(0);
        _trigger.PendingRerunCount.Should().Be(0);
        dispatchMock.Verify(value => value.DispatchJobAsync(
                jobId,
                printerId,
                "system:auto-dispatch",
                It.IsAny<DispatchScore>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    [Trait("Category", "Dispatch")]
    [Trait("Phase", "2")]
    public async Task StopAsync_InFlightPrinterWithRerun_DrainsWorkerAndDiscardsRerun()
    {
        SeedSettings(
            enabled: true,
            mode: AutoDispatchMode.Auto,
            idleThresholdSeconds: 0,
            maxConcurrentDispatches: 2);
        Guid printerId = SeedPrinter("shutdown-in-flight", 231);
        Guid jobId = SeedQueuedJob("shutdown-in-flight-job");
        Printer printer = _db.Printers.Single(value => value.Id == printerId);
        printer.AutoDispatchEnabled = true;
        printer.DispatchState = new PrinterDispatchState
        {
            PrinterId = printerId,
            AutoDispatchState = AutoDispatchState.Ready,
        };
        PrintJob job = _db.PrintJobs.Single(value => value.Id == jobId);
        job.AssignedPrinterId = printerId;
        _db.SaveChanges();

        var scorerMock = new Mock<IDispatchScorer>();
        scorerMock.Setup(value => value.ScorePrintersForJobAsync(
                jobId,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(
            [
                new DispatchScore(
                    printerId,
                    printer.Name,
                    90,
                    new Dictionary<string, FactorScore>(),
                    false,
                    []),
            ]);
        scorerMock.Setup(value => value.ScorePrinterForJobAsync(
                jobId,
                printerId,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DispatchScore(
                printerId,
                printer.Name,
                90,
                new Dictionary<string, FactorScore>(),
                false,
                []));
        var dispatchEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationObserved = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseDispatch = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatchMock = new Mock<IJobDispatchService>();
        dispatchMock.Setup(value => value.DispatchJobAsync(
                jobId,
                printerId,
                "system:auto-dispatch",
                It.IsAny<DispatchScore>(),
                It.IsAny<CancellationToken>()))
            .Returns<Guid, Guid, string, DispatchScore, CancellationToken>(
                async (_, _, _, _, cancellationToken) =>
                {
                    using CancellationTokenRegistration registration = cancellationToken.Register(
                        () => cancellationObserved.TrySetResult());
                    dispatchEntered.TrySetResult();
                    await releaseDispatch.Task;
                    cancellationToken.ThrowIfCancellationRequested();
                    return new QueuedPrintJobDto();
                });
        int evaluationCount = 0;
        var serviceLogger = new CallbackLogger<AutoDispatchBackgroundService>(
            (level, message) =>
            {
                if (level == LogLevel.Debug
                    && message.Contains("skipping idle threshold", StringComparison.Ordinal))
                {
                    Interlocked.Increment(ref evaluationCount);
                }
            });

        await using ServiceProvider provider = BuildServiceProvider(scorerMock, dispatchMock);
        using AutoDispatchBackgroundService service = new(
            _trigger,
            provider.GetRequiredService<IServiceScopeFactory>(),
            _concurrencyCoordinator,
            _hubMock.Object,
            serviceLogger);
        Task? stop = null;

        await service.StartAsync(CancellationToken.None);
        try
        {
            await dispatchEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            for (int i = 0; i < 1_000; i++)
            {
                _trigger.NotifyJobQueued(printerId);
            }

            service.TrackedWorkerCount.Should().Be(1);
            _trigger.IntentStateCount.Should().Be(1);
            _trigger.PendingRerunCount.Should().Be(1);

            stop = service.StopAsync(CancellationToken.None);
            await cancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(10));
            stop.IsCompleted.Should().BeFalse(
                "StopAsync must drain the generation-owned external dispatch worker");
            for (int i = 0; i < 1_000; i++)
            {
                _trigger.NotifyJobQueued(printerId);
            }

            releaseDispatch.TrySetResult();
            await stop.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            releaseDispatch.TrySetResult();
            if (stop is null)
            {
                await service.StopAsync(CancellationToken.None)
                    .WaitAsync(TimeSpan.FromSeconds(10));
            }
            else
            {
                await stop.WaitAsync(TimeSpan.FromSeconds(10));
            }
        }

        Volatile.Read(ref evaluationCount).Should().Be(1);
        service.TrackedWorkerCount.Should().Be(0);
        _trigger.IntentStateCount.Should().Be(0);
        _trigger.PendingRerunCount.Should().Be(0);
        dispatchMock.Verify(value => value.DispatchJobAsync(
                jobId,
                printerId,
                "system:auto-dispatch",
                It.IsAny<DispatchScore>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    [Trait("Category", "Dispatch")]
    [Trait("Phase", "2")]
    public async Task StopAsync_PendingWorker_CancelsDrainsAndClearsLease()
    {
        SeedSettings(
            enabled: true,
            mode: AutoDispatchMode.Auto,
            idleThresholdSeconds: 3600,
            maxConcurrentDispatches: 2);
        Guid printerId = SeedPrinter("shutdown-pending", 220);
        Printer printer = _db.Printers.Single(value => value.Id == printerId);
        printer.AutoDispatchEnabled = true;
        printer.DispatchState = new PrinterDispatchState
        {
            PrinterId = printerId,
            AutoDispatchState = AutoDispatchState.Ready,
        };
        _db.SaveChanges();
        var scorerMock = new Mock<IDispatchScorer>(MockBehavior.Strict);
        var dispatchMock = new Mock<IJobDispatchService>(MockBehavior.Strict);
        await using ServiceProvider provider = BuildServiceProvider(scorerMock, dispatchMock);
        using AutoDispatchBackgroundService service = new(
            _trigger,
            provider.GetRequiredService<IServiceScopeFactory>(),
            _concurrencyCoordinator,
            _hubMock.Object,
            NullLogger<AutoDispatchBackgroundService>.Instance);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));

        await service.StartAsync(CancellationToken.None);
        _trigger.NotifyPrinterIdle(printerId);
        while (!_trigger.HasPendingDispatch(printerId))
        {
            timeout.Token.ThrowIfCancellationRequested();
            await Task.Yield();
        }

        await service.StopAsync(CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(10));

        _trigger.HasPendingDispatch(printerId).Should().BeFalse();
        scorerMock.VerifyNoOtherCalls();
        dispatchMock.VerifyNoOtherCalls();
    }

    // =========================================================================
    // AUTO-DISPATCH TRIGGER UNIT TESTS
    // =========================================================================

    [Fact]
    [Trait("Category", "Dispatch")]
    [Trait("Phase", "2")]
    public async Task AutoDispatchTrigger_NotifyAndRead_DeliversPrinterId()
    {
        AutoDispatchTrigger trigger = new();
        Guid printerId = Guid.NewGuid();

        trigger.NotifyPrinterIdle(printerId);

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(2));
        Guid received = (await trigger.ReadAsync(cts.Token)).PrinterId;

        received.Should().Be(printerId);
    }

    [Fact]
    [Trait("Category", "Dispatch")]
    [Trait("Phase", "2")]
    public void AutoDispatchTrigger_CancelPending_CancelsOwnedLease()
    {
        AutoDispatchTrigger trigger = new();
        Guid printerId = Guid.NewGuid();
        using CancellationTokenSource parentCts = new();
        using PendingDispatchLease lease = trigger.CreatePendingLease(
            printerId,
            parentCts.Token);

        lease.IsCancellationRequested.Should().BeFalse();

        trigger.CancelPendingDispatch(printerId);

        lease.IsCancellationRequested.Should().BeTrue();
    }

    [Fact]
    [Trait("Category", "Dispatch")]
    [Trait("Phase", "2")]
    public void AutoDispatchTrigger_StaleLeaseClear_DoesNotRemoveNewGeneration()
    {
        AutoDispatchTrigger trigger = new();
        Guid printerId = Guid.NewGuid();
        using CancellationTokenSource parentCts = new();
        using PendingDispatchLease stale = trigger.CreatePendingLease(
            printerId,
            parentCts.Token);
        using PendingDispatchLease current = trigger.CreatePendingLease(
            printerId,
            parentCts.Token);

        stale.IsCancellationRequested.Should().BeTrue();
        current.Generation.Should().BeGreaterThan(stale.Generation);

        trigger.ClearPending(printerId, stale);

        trigger.HasPendingDispatch(printerId).Should().BeTrue();
        trigger.CancelPendingDispatch(printerId);
        current.IsCancellationRequested.Should().BeTrue();
    }

    [Fact]
    [Trait("Category", "Dispatch")]
    [Trait("Phase", "2")]
    public async Task AutoDispatchTrigger_EnqueueRacesCompletion_ProcessesOneFinalIntentAndCleansState()
    {
        const int iterations = 200;
        for (int i = 0; i < iterations; i++)
        {
            AutoDispatchTrigger trigger = new();
            Guid printerId = Guid.NewGuid();
            trigger.NotifyPrinterIdle(printerId);
            DispatchTriggerEvent first = await trigger.ReadAsync(CancellationToken.None);
            var startRace = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            DispatchTriggerEvent inlineRerun = default;
            bool completedWithInlineRerun = false;

            Task complete = Task.Run(async () =>
            {
                await startRace.Task;
                completedWithInlineRerun = trigger.TryCompleteProcessing(
                    first,
                    allowRerun: true,
                    out inlineRerun);
            });
            Task enqueue = Task.Run(async () =>
            {
                await startRace.Task;
                trigger.NotifyJobQueued(printerId);
            });

            startRace.TrySetResult();
            await Task.WhenAll(complete, enqueue).WaitAsync(TimeSpan.FromSeconds(10));

            DispatchTriggerEvent finalIntent = completedWithInlineRerun
                ? inlineRerun
                : await trigger.ReadAsync(CancellationToken.None);
            finalIntent.PrinterId.Should().Be(printerId);
            finalIntent.SkipIdleThreshold.Should().BeTrue();
            trigger.IntentStateCount.Should().Be(1);
            trigger.PendingRerunCount.Should().Be(0);

            trigger.TryCompleteProcessing(
                    finalIntent,
                    allowRerun: true,
                    out _)
                .Should().BeFalse();
            trigger.IntentStateCount.Should().Be(0);
        }
    }

    [Fact]
    [Trait("Category", "Dispatch")]
    [Trait("Phase", "2")]
    public async Task AutoDispatchTrigger_MultipleNotifications_AllDeliveredInOrder()
    {
        AutoDispatchTrigger trigger = new();
        Guid id1 = Guid.NewGuid();
        Guid id2 = Guid.NewGuid();
        Guid id3 = Guid.NewGuid();

        trigger.NotifyPrinterIdle(id1);
        trigger.NotifyPrinterIdle(id2);
        trigger.NotifyPrinterIdle(id3);

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(2));

        Guid r1 = (await trigger.ReadAsync(cts.Token)).PrinterId;
        Guid r2 = (await trigger.ReadAsync(cts.Token)).PrinterId;
        Guid r3 = (await trigger.ReadAsync(cts.Token)).PrinterId;

        r1.Should().Be(id1);
        r2.Should().Be(id2);
        r3.Should().Be(id3);
    }

    [Fact]
    [Trait("Category", "Dispatch")]
    [Trait("Phase", "2")]
    public async Task AutoDispatchTrigger_DuplicatePrinterIntents_CoalesceWithImmediateIntentWinning()
    {
        AutoDispatchTrigger trigger = new();
        Guid printerId = Guid.NewGuid();

        trigger.NotifyPrinterIdle(printerId);
        trigger.NotifyPrinterIdle(printerId);
        trigger.NotifyJobQueued(printerId);

        DispatchTriggerEvent triggerEvent = await trigger.ReadAsync(CancellationToken.None);

        triggerEvent.PrinterId.Should().Be(printerId);
        triggerEvent.SkipIdleThreshold.Should().BeTrue();
        using CancellationTokenSource cancelled = new();
        cancelled.Cancel();
        Func<Task> readAgain = async () =>
            _ = await trigger.ReadAsync(cancelled.Token);
        await readAgain.Should().ThrowAsync<OperationCanceledException>();
    }

    // =========================================================================
    // DISPATCH SETTINGS ENTITY TESTS
    // =========================================================================

    [Fact]
    [Trait("Category", "Dispatch")]
    [Trait("Phase", "2")]
    public void DispatchSettings_DefaultValues_AreCorrect()
    {
        var settings = new DispatchSettings();

        settings.Id.Should().Be(1, "singleton uses Id=1");
        settings.AutoDispatchEnabled.Should().BeFalse("default is opt-in disabled");
        settings.AutoDispatchMode.Should().Be(AutoDispatchMode.Manual, "default mode is Manual");
        settings.IdleThresholdSeconds.Should().Be(30, "default threshold is 30 seconds");
        settings.MinimumScoreThreshold.Should().Be(0.5, "default minimum score is 0.5");
        settings.MaxConcurrentDispatches.Should().Be(3, "default max concurrent is 3");
    }

    [Fact]
    [Trait("Category", "Dispatch")]
    [Trait("Phase", "2")]
    public void DispatchSettings_Seeded_HasCorrectValues()
    {
        DispatchSettings? seeded = _db.DispatchSettings.FirstOrDefault();
        seeded.Should().NotBeNull();
        seeded!.Id.Should().Be(1);
        seeded.AutoDispatchEnabled.Should().BeFalse();
        seeded.AutoDispatchMode.Should().Be(AutoDispatchMode.Manual);
    }

    // =========================================================================
    // SIGNALR EVENT DTO TESTS
    // =========================================================================

    [Fact]
    [Trait("Category", "Dispatch")]
    [Trait("Phase", "2")]
    public void JobAutoDispatchedEvent_PropertiesSetCorrectly()
    {
        Guid jobId = Guid.NewGuid();
        Guid printerId = Guid.NewGuid();

        var evt = new JobAutoDispatchedEvent
        {
            JobId = jobId,
            JobName = "benchy.gcode",
            PrinterId = printerId,
            PrinterName = "Prusa MK4",
            Score = 92.5,
            Mode = AutoDispatchMode.Auto,
        };

        evt.JobId.Should().Be(jobId);
        evt.JobName.Should().Be("benchy.gcode");
        evt.PrinterId.Should().Be(printerId);
        evt.PrinterName.Should().Be("Prusa MK4");
        evt.Score.Should().Be(92.5);
        evt.Mode.Should().Be(AutoDispatchMode.Auto);
    }

    [Fact]
    [Trait("Category", "Dispatch")]
    [Trait("Phase", "2")]
    public void DispatchSuggestionEvent_PropertiesSetCorrectly()
    {
        var evt = new DispatchSuggestionEvent
        {
            JobId = Guid.NewGuid(),
            JobName = "vase.gcode",
            PrinterId = Guid.NewGuid(),
            PrinterName = "Bambu X1C",
            Score = 88.0,
        };

        evt.JobName.Should().Be("vase.gcode");
        evt.Score.Should().Be(88.0);
    }

    [Fact]
    [Trait("Category", "Dispatch")]
    [Trait("Phase", "2")]
    public void DispatchFailedEvent_PropertiesSetCorrectly()
    {
        var evt = new DispatchFailedEvent
        {
            JobId = Guid.NewGuid(),
            PrinterId = Guid.NewGuid(),
            PrinterName = "Ender 3",
            Reason = "Printer connection lost during file upload",
        };

        evt.Reason.Should().Contain("connection lost");
        evt.JobId.Should().NotBeNull();
    }

    private sealed class CallbackLogger<T>(Action<LogLevel, string> onLog) : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
        {
            return null;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return true;
        }

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            onLog(logLevel, formatter(state, exception));
        }
    }
}
