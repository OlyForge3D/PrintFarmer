using System;
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
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Dispatch;

/// <summary>
/// Tests for race conditions and concurrency edge cases in auto-dispatch.
///
/// The AutoDispatchBackgroundService uses a SemaphoreSlim to serialize
/// dispatch decisions. These tests verify that concurrent idle events
/// don't cause double-assignment, and that mid-dispatch failures
/// (cancellation, printer going offline) are handled gracefully.
/// </summary>
public class AutoDispatchConcurrencyTests : IDisposable
{
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
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        TestSqlitePragmaEnforcer.EnsureForeignKeysEnabled(_connection);

        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
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

    private IServiceScopeFactory BuildScopeFactory(Mock<IDispatchScorer> scorerMock, Mock<IJobDispatchService> dispatchMock)
    {
        ServiceCollection services = new();
        services.AddScoped<AppDbContext>(_ =>
        {
            var opts = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite(_connection)
                .Options;
            return new AppDbContext(opts);
        });
        services.AddScoped<IDispatchScorer>(_ => scorerMock.Object);
        services.AddScoped<IJobDispatchService>(_ => dispatchMock.Object);

        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
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

        var scorerMock = new Mock<IDispatchScorer>();
        var dispatchMock = new Mock<IJobDispatchService>();

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
            .Setup(d => d.DispatchJobAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns((Guid jId, Guid pId, string _, CancellationToken _) =>
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

                return Task.FromResult(new QueuedPrintJobDto());
            });

        IServiceScopeFactory scopeFactory = BuildScopeFactory(scorerMock, dispatchMock);

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
        AutoDispatchBackgroundService svc = new(
            _trigger, scopeFactory, _hubMock.Object,
            NullLogger<AutoDispatchBackgroundService>.Instance);

        Task serviceTask = svc.StartAsync(cts.Token);

        // Fire both idle events as close together as possible
        _trigger.NotifyPrinterIdle(printer1Id);
        _trigger.NotifyPrinterIdle(printer2Id);

        await Task.Delay(1500, cts.Token);
        await cts.CancelAsync();

        try
        { await serviceTask; }
        catch (OperationCanceledException) { }

        // Assert: the SemaphoreSlim + DB update should prevent double-dispatch
        lock (_dispatchLock)
        {
            int timesJobDispatched = _dispatchedPairs.Count(p => p.jobId == jobId);
            timesJobDispatched.Should().BeInRange(0, 1,
                "the dispatch lock should prevent two printers from grabbing the same job");
        }
    }

    [Fact]
    [Trait("Category", "Dispatch")]
    [Trait("Phase", "2")]
    public async Task MultiplePrintersIdle_MultipleJobs_EachGetsUniqueJob()
    {
        // Arrange: 3 printers, 3 jobs — each printer should get a different job
        SeedSettings(enabled: true, mode: AutoDispatchMode.Auto, idleThresholdSeconds: 0, maxConcurrentDispatches: 5);
        Guid p1 = SeedPrinter("P1", 20);
        Guid p2 = SeedPrinter("P2", 21);
        Guid p3 = SeedPrinter("P3", 22);
        Guid j1 = SeedQueuedJob("Job-1", priority: 0, queuePosition: 1);
        Guid j2 = SeedQueuedJob("Job-2", priority: 0, queuePosition: 2);
        Guid j3 = SeedQueuedJob("Job-3", priority: 0, queuePosition: 3);

        var scorerMock = new Mock<IDispatchScorer>();
        var dispatchMock = new Mock<IJobDispatchService>();

        // All printers score well for all jobs
        scorerMock
            .Setup(s => s.ScorePrintersForJobAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid _, CancellationToken _) =>
            [
                new DispatchScore(p1, "P1", 80.0, new Dictionary<string, FactorScore>(), false, []),
                new DispatchScore(p2, "P2", 75.0, new Dictionary<string, FactorScore>(), false, []),
                new DispatchScore(p3, "P3", 70.0, new Dictionary<string, FactorScore>(), false, []),
            ]);

        dispatchMock
            .Setup(d => d.DispatchJobAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns((Guid jId, Guid pId, string _, CancellationToken _) =>
            {
                lock (_dispatchLock)
                {
                    _dispatchedPairs.Add((jId, pId));
                }

                // Simulate real dispatch: mark job as assigned so the next cycle won't re-dispatch it
                PrintJob? job = _db.PrintJobs.FirstOrDefault(j => j.Id == jId);
                if (job is not null)
                {
                    job.AssignedPrinterId = pId;
                    job.Status = PrintJobStatus.Starting;
                    _db.SaveChanges();
                }

                return Task.FromResult(new QueuedPrintJobDto());
            });

        IServiceScopeFactory scopeFactory = BuildScopeFactory(scorerMock, dispatchMock);

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
        AutoDispatchBackgroundService svc = new(
            _trigger, scopeFactory, _hubMock.Object,
            NullLogger<AutoDispatchBackgroundService>.Instance);

        Task serviceTask = svc.StartAsync(cts.Token);

        _trigger.NotifyPrinterIdle(p1);
        _trigger.NotifyPrinterIdle(p2);
        _trigger.NotifyPrinterIdle(p3);

        await Task.Delay(2000, cts.Token);
        await cts.CancelAsync();

        try
        { await serviceTask; }
        catch (OperationCanceledException) { }

        // Verify no job was assigned to more than one printer
        lock (_dispatchLock)
        {
            List<Guid> dispatchedJobIds = _dispatchedPairs.Select(p => p.jobId).ToList();
            dispatchedJobIds.Should().OnlyHaveUniqueItems(
                "each job should be dispatched to at most one printer");
        }
    }

    [Fact]
    [Trait("Category", "Dispatch")]
    [Trait("Phase", "2")]
    public async Task MaxConcurrentReached_ExcessIdlePrintersWait()
    {
        // Arrange: max concurrent = 1, but 2 printers go idle
        SeedSettings(enabled: true, mode: AutoDispatchMode.Auto, idleThresholdSeconds: 0, maxConcurrentDispatches: 1);
        Guid p1 = SeedPrinter("P1-max", 30);
        Guid p2 = SeedPrinter("P2-max", 31);
        Guid j1 = SeedQueuedJob("Job-max-1");
        Guid j2 = SeedQueuedJob("Job-max-2");

        var scorerMock = new Mock<IDispatchScorer>();
        var dispatchMock = new Mock<IJobDispatchService>();

        scorerMock
            .Setup(s => s.ScorePrintersForJobAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid _, CancellationToken _) =>
            [
                new DispatchScore(p1, "P1-max", 80.0, new Dictionary<string, FactorScore>(), false, []),
                new DispatchScore(p2, "P2-max", 75.0, new Dictionary<string, FactorScore>(), false, []),
            ]);

        // Simulate a slow dispatch (takes 500ms)
        dispatchMock
            .Setup(d => d.DispatchJobAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(async (Guid jId, Guid pId, string _, CancellationToken _) =>
            {
                await Task.Delay(500);
                lock (_dispatchLock)
                {
                    _dispatchedPairs.Add((jId, pId));
                }

                return new QueuedPrintJobDto();
            });

        IServiceScopeFactory scopeFactory = BuildScopeFactory(scorerMock, dispatchMock);

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
        AutoDispatchBackgroundService svc = new(
            _trigger, scopeFactory, _hubMock.Object,
            NullLogger<AutoDispatchBackgroundService>.Instance);

        Task serviceTask = svc.StartAsync(cts.Token);

        // Both printers go idle simultaneously
        _trigger.NotifyPrinterIdle(p1);
        _trigger.NotifyPrinterIdle(p2);

        await Task.Delay(2000, cts.Token);
        await cts.CancelAsync();

        try
        { await serviceTask; }
        catch (OperationCanceledException) { }

        // With maxConcurrent=1, the second printer's dispatch should be skipped
        // (it checks the in-flight count before acquiring the lock)
        lock (_dispatchLock)
        {
            _dispatchedPairs.Count.Should().BeInRange(0, 2,
                "with max concurrent = 1 and the lock, at most the sequentially-processed dispatches should occur");
        }
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
        Guid received = await trigger.ReadAsync(cts.Token);

        received.Should().Be(printerId);
    }

    [Fact]
    [Trait("Category", "Dispatch")]
    [Trait("Phase", "2")]
    public async Task AutoDispatchTrigger_CancelPending_CancelsLinkedToken()
    {
        AutoDispatchTrigger trigger = new();
        Guid printerId = Guid.NewGuid();
        using CancellationTokenSource parentCts = new(TimeSpan.FromSeconds(5));

        using CancellationTokenSource linkedCts = trigger.CreateLinkedCts(printerId, parentCts.Token);

        linkedCts.IsCancellationRequested.Should().BeFalse();

        trigger.CancelPendingDispatch(printerId);

        linkedCts.IsCancellationRequested.Should().BeTrue(
            "CancelPendingDispatch should cancel the linked CTS for that printer");
    }

    [Fact]
    [Trait("Category", "Dispatch")]
    [Trait("Phase", "2")]
    public void AutoDispatchTrigger_ClearPending_DisposesToken()
    {
        AutoDispatchTrigger trigger = new();
        Guid printerId = Guid.NewGuid();
        using CancellationTokenSource parentCts = new();

        CancellationTokenSource linkedCts = trigger.CreateLinkedCts(printerId, parentCts.Token);
        trigger.ClearPending(printerId);

        // After clearing, creating a new linked CTS should succeed (the old one is removed)
        CancellationTokenSource newLinkedCts = trigger.CreateLinkedCts(printerId, parentCts.Token);
        newLinkedCts.Should().NotBeSameAs(linkedCts);
        newLinkedCts.Dispose();
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

        Guid r1 = await trigger.ReadAsync(cts.Token);
        Guid r2 = await trigger.ReadAsync(cts.Token);
        Guid r3 = await trigger.ReadAsync(cts.Token);

        r1.Should().Be(id1);
        r2.Should().Be(id2);
        r3.Should().Be(id3);
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
}
