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
using Farm.Web.Api.Tests.Builders;
using Farm.Web.Api.Tests.TestInfrastructure;
using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Dispatch;

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
        _hubMock = new Mock<IHubContext<PrinterHub>>();
        _hubMock.Setup(h => h.Clients).Returns(hubClientsMock.Object);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
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
        scorerMock
            .Setup(s => s.ScorePrintersForJobAsync(jobId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid jId, CancellationToken _) =>
            [
                new DispatchScore(printer1Id, "Printer-1", 85.0, new Dictionary<string, FactorScore>(), false, []),
                new DispatchScore(printer2Id, "Printer-2", 82.0, new Dictionary<string, FactorScore>(), false, []),
            ]);

        // Track dispatch calls and update DB to simulate real dispatch behavior
        dispatchMock
            .Setup(d => d.DispatchJobAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<DispatchScore>(), It.IsAny<CancellationToken>()))
            .Returns((Guid jId, Guid pId, string _, DispatchScore _, CancellationToken _) =>
            {
                lock (_dispatchLock)
                {
                    _dispatchedPairs.Add((jId, pId));
                }

                // Simulate what the real dispatch does: assign the job so the next cycle won't re-dispatch it
                PrintJob? job = _db.PrintJobs.FirstOrDefault(j => j.Id == jId);
                if (job is not null)
                {
                    job.AssignedPrinterId = pId;
                    job.Status = PrintJobStatus.Starting;
                    _db.SaveChanges();
                }

                dispatchObserved.TrySetResult((jId, pId));
                return Task.FromResult(new QueuedPrintJobDto());
            });

        await using ServiceProvider provider = BuildServiceProvider(scorerMock, dispatchMock);
        IServiceScopeFactory scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

        using CancellationTokenSource shutdown = new();
        using AutoDispatchBackgroundService svc = new(
            _trigger, scopeFactory, _hubMock.Object,
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
        scorerMock
            .Setup(s => s.ScorePrintersForJobAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid _, CancellationToken _) =>
            [
                new DispatchScore(p1, "P1", 80.0, new Dictionary<string, FactorScore>(), false, []),
                new DispatchScore(p2, "P2", 75.0, new Dictionary<string, FactorScore>(), false, []),
                new DispatchScore(p3, "P3", 70.0, new Dictionary<string, FactorScore>(), false, []),
            ]);
        dispatchMock
            .Setup(d => d.DispatchJobAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<DispatchScore>(), It.IsAny<CancellationToken>()))
            .Returns((Guid jobId, Guid printerId, string _, DispatchScore _, CancellationToken _) =>
            {
                lock (_dispatchLock)
                {
                    _dispatchedPairs.Add((jobId, printerId));
                }

                PrintJob job = _db.PrintJobs.Single(value => value.Id == jobId);
                job.AssignedPrinterId = printerId;
                job.Status = PrintJobStatus.Starting;
                _db.SaveChanges();
                return Task.FromResult(new QueuedPrintJobDto());
            });

        await using ServiceProvider provider = BuildServiceProvider(scorerMock, dispatchMock);
        IServiceScopeFactory scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();
        using CancellationTokenSource shutdown = new();
        using AutoDispatchBackgroundService svc = new(
            _trigger,
            scopeFactory,
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
            await allCycles.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            await shutdown.CancelAsync();
            await allCycles.WaitAsync(TimeSpan.FromSeconds(10));
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

                        return new QueuedPrintJobDto();
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
        _ = SeedQueuedJob("startup-capacity-job");
        var scorerMock = new Mock<IDispatchScorer>();
        scorerMock.Setup(value => value.ScorePrintersForJobAsync(
                It.IsAny<Guid>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
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
        dispatchMock.VerifyNoOtherCalls();
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
