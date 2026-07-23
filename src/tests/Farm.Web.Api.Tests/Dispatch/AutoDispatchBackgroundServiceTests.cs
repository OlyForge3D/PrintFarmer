using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
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
/// Tests for AutoDispatchBackgroundService — the event-driven background service
/// that reacts when printers become idle and dispatches the best matching job.
///
/// These tests verify the core dispatch cycle logic by directly invoking
/// the internal method paths through the AutoDispatchTrigger channel.
///
/// Architecture under test:
///   AutoDispatchTrigger.NotifyPrinterIdle(printerId)
///   → AutoDispatchBackgroundService reads from channel
///   → Reads DispatchSettings (enabled? mode?)
///   → Waits IdleThresholdSeconds
///   → Scores queued jobs against the idle printer
///   → Dispatches (Auto) or suggests (Suggest) or does nothing (Manual)
/// </summary>
public class AutoDispatchBackgroundServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;
    private readonly AutoDispatchTrigger _trigger;
    private readonly Mock<IHubContext<PrinterHub>> _hubMock;
    private readonly Mock<IHubClients> _hubClientsMock;
    private readonly Mock<IClientProxy> _clientProxyMock;
    private readonly Mock<IDispatchScorer> _scorerMock;
    private readonly Mock<IJobDispatchService> _dispatchServiceMock;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly Guid _folderId = Guid.NewGuid();

    public AutoDispatchBackgroundServiceTests()
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
        _hubClientsMock = new Mock<IHubClients>();
        _hubClientsMock.Setup(c => c.All).Returns(_clientProxyMock.Object);
        _hubMock = new Mock<IHubContext<PrinterHub>>();
        _hubMock.Setup(h => h.Clients).Returns(_hubClientsMock.Object);

        _scorerMock = new Mock<IDispatchScorer>();
        _dispatchServiceMock = new Mock<IJobDispatchService>();

        // Build a minimal service provider for the scoped factory
        ServiceCollection services = new();
        services.AddScoped<AppDbContext>(_ =>
        {
            var opts = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite(_connection)
                .Options;
            return new AppDbContext(opts);
        });
        services.AddScoped<IDispatchScorer>(_ => _scorerMock.Object);
        services.AddScoped<IJobDispatchService>(_ => _dispatchServiceMock.Object);

        ServiceProvider sp = services.BuildServiceProvider();
        _scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();
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
        double minimumScoreThreshold = 0.5,
        int maxConcurrentDispatches = 3)
    {
        // Remove seeded default if present
        DispatchSettings? existing = _db.DispatchSettings.FirstOrDefault();
        if (existing is not null)
        {
            existing.AutoDispatchEnabled = enabled;
            existing.AutoDispatchMode = mode;
            existing.IdleThresholdSeconds = idleThresholdSeconds;
            existing.MinimumScoreThreshold = minimumScoreThreshold;
            existing.MaxConcurrentDispatches = maxConcurrentDispatches;
        }
        else
        {
            _db.DispatchSettings.Add(new DispatchSettings
            {
                Id = 1,
                AutoDispatchEnabled = enabled,
                AutoDispatchMode = mode,
                IdleThresholdSeconds = idleThresholdSeconds,
                MinimumScoreThreshold = minimumScoreThreshold,
                MaxConcurrentDispatches = maxConcurrentDispatches,
                UpdatedAt = DateTime.UtcNow,
            });
        }

        _db.SaveChanges();
    }

    private (Printer printer, Guid printerId) SeedPrinter(string name = "Idle Printer", int index = 1)
    {
        Guid printerId = Guid.NewGuid();
        Guid manufacturerId = Guid.NewGuid();
        Guid modelId = Guid.NewGuid();

        // FK requirements: Manufacturer → PrinterModel → Printer
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
            .WithId(printerId)
            .WithName(name)
            .WithServerUrl($"http://192.168.1.{index}")
            .Build();
        printer.ManufacturerId = manufacturerId;
        printer.ModelId = modelId;
        printer.IsEnabled = true;
        printer.IsAvailable = true;
        printer.AutoDispatchEnabled = true;
        printer.DispatchState = new PrinterDispatchState { PrinterId = printer.Id, AutoDispatchState = AutoDispatchState.Ready };

        _db.Printers.Add(printer);
        _db.SaveChanges();
        return (printer, printerId);
    }

    private AutoDispatchBackgroundService CreateService()
    {
        return new AutoDispatchBackgroundService(
            _trigger, _scopeFactory, _hubMock.Object,
            NullLogger<AutoDispatchBackgroundService>.Instance);
    }

    private PrintJob SeedQueuedJob(string name = "Test Job", int priority = 0, int queuePosition = 1)
    {
        Guid gcodeFileId = Guid.NewGuid();
        var gcodeFile = new GcodeFile
        {
            Id = gcodeFileId,
            Name = $"{name}.gcode",
            FileName = $"{Guid.NewGuid()}.gcode",
            FilePath = "/gcode/",
            FolderId = _folderId,
            FileHash = Guid.NewGuid().ToString()[..8],

            UploadedAt = DateTime.UtcNow,
        };

        var job = new PrintJob
        {
            Id = Guid.NewGuid(),
            Name = name,
            GcodeFileId = gcodeFileId,
            GcodeFile = gcodeFile,
            Status = PrintJobStatus.Queued,
            AssignedPrinterId = null,
            Priority = priority,
            QueuePosition = queuePosition,

            QueuedAt = DateTime.UtcNow,
        };

        _db.GcodeFiles.Add(gcodeFile);
        _db.PrintJobs.Add(job);
        _db.SaveChanges();
        return job;
    }

    // =========================================================================
    // BACKGROUND SERVICE BEHAVIOR TESTS
    // =========================================================================

    [Fact]
    [Trait("Category", "Dispatch")]
    [Trait("Phase", "2")]
    public async Task OnPrinterIdle_AutoEnabled_DispatchesTopJob()
    {
        // Arrange: auto-dispatch enabled, one idle printer, one queued job
        SeedSettings(enabled: true, mode: AutoDispatchMode.Auto, idleThresholdSeconds: 0);
        (Printer printer, Guid printerId) = SeedPrinter();

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));
        AutoDispatchBackgroundService svc = CreateService();
        PrintJob job = SeedQueuedJob("benchy");

        DispatchScore goodScore = new(
            printerId, printer.Name, 85.0,
            new Dictionary<string, FactorScore>(),
            Eliminated: false,
            EliminationReasons: []);

        _scorerMock
            .Setup(s => s.ScorePrintersForJobAsync(job.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync([goodScore]);

        _dispatchServiceMock
            .Setup(d => d.DispatchJobAsync(job.Id, printerId, "system:auto-dispatch", It.IsAny<DispatchScore>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Farm.Infrastructure.Dtos.PrintQueue.QueuedPrintJobDto());

        await svc.ProcessPrinterIdleAsync(printerId, skipIdleThreshold: true, cts.Token);

        // Assert: DispatchJobAsync was called
        _dispatchServiceMock.Verify(
            d => d.DispatchJobAsync(job.Id, printerId, "system:auto-dispatch", It.IsAny<DispatchScore>(), It.IsAny<CancellationToken>()),
            Times.Once);

        // Assert: SignalR event was sent
        _clientProxyMock.Verify(
            c => c.SendCoreAsync("jobautodispatched", It.IsAny<object?[]>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    [Trait("Category", "Dispatch")]
    [Trait("Phase", "2")]
    public async Task OnStartup_ReadyAutoDispatchPrinterWithQueuedJob_DispatchesWithoutExternalTrigger()
    {
        SeedSettings(enabled: true, mode: AutoDispatchMode.Auto, idleThresholdSeconds: 0);
        (Printer printer, Guid printerId) = SeedPrinter(name: "Startup Ready Printer");
        printer.AutoDispatchEnabled = true;
        printer.DispatchState = new PrinterDispatchState { PrinterId = printer.Id, AutoDispatchState = AutoDispatchState.Ready };
        _db.SaveChanges();

        PrintJob job = SeedQueuedJob("startup-ready-job");

        DispatchScore goodScore = new(
            printerId, printer.Name, 90.0,
            new Dictionary<string, FactorScore>(),
            Eliminated: false,
            EliminationReasons: []);

        _scorerMock
            .Setup(s => s.ScorePrintersForJobAsync(job.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync([goodScore]);

        _dispatchServiceMock
            .Setup(d => d.DispatchJobAsync(job.Id, printerId, "system:auto-dispatch", It.IsAny<DispatchScore>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Farm.Infrastructure.Dtos.PrintQueue.QueuedPrintJobDto());

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));
        AutoDispatchBackgroundService svc = CreateService();
        await svc.ReconcileStartupEligiblePrintersAsync(cts.Token);
        DispatchTriggerEvent triggerEvent = await _trigger.ReadAsync(cts.Token);
        triggerEvent.PrinterId.Should().Be(printerId);
        triggerEvent.SkipIdleThreshold.Should().BeTrue();
        await svc.ProcessPrinterIdleAsync(triggerEvent.PrinterId, triggerEvent.SkipIdleThreshold, cts.Token);

        _dispatchServiceMock.Verify(
            d => d.DispatchJobAsync(job.Id, printerId, "system:auto-dispatch", It.IsAny<DispatchScore>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    [Trait("Category", "Dispatch")]
    [Trait("Phase", "2")]
    public async Task OnPrinterIdle_AutoDisabled_DoesNothing()
    {
        // Arrange: auto-dispatch disabled
        SeedSettings(enabled: false, mode: AutoDispatchMode.Manual);
        (_, Guid printerId) = SeedPrinter();
        SeedQueuedJob();

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));
        AutoDispatchBackgroundService svc = CreateService();
        await svc.ProcessPrinterIdleAsync(printerId, skipIdleThreshold: true, cts.Token);

        // Assert: scorer never called, nothing dispatched
        _scorerMock.Verify(
            s => s.ScorePrintersForJobAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _dispatchServiceMock.Verify(
            d => d.DispatchJobAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<DispatchScore>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    [Trait("Category", "Dispatch")]
    [Trait("Phase", "2")]
    public async Task OnPrinterIdle_ModeManual_DoesNothing()
    {
        // Arrange: enabled but mode is Manual
        SeedSettings(enabled: true, mode: AutoDispatchMode.Manual);
        (_, Guid printerId) = SeedPrinter();
        SeedQueuedJob();

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));
        AutoDispatchBackgroundService svc = CreateService();
        await svc.ProcessPrinterIdleAsync(printerId, skipIdleThreshold: true, cts.Token);

        _scorerMock.Verify(
            s => s.ScorePrintersForJobAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    [Trait("Category", "Dispatch")]
    [Trait("Phase", "2")]
    public async Task OnPrinterIdle_NoQueuedJobs_DoesNothing()
    {
        // Arrange: auto enabled, printer idle, NO jobs in queue
        SeedSettings(enabled: true, mode: AutoDispatchMode.Auto, idleThresholdSeconds: 0);
        (_, Guid printerId) = SeedPrinter();
        // No jobs seeded

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));
        AutoDispatchBackgroundService svc = CreateService();
        await svc.ProcessPrinterIdleAsync(printerId, skipIdleThreshold: true, cts.Token);

        // No jobs → scorer never called, no dispatch
        _scorerMock.Verify(
            s => s.ScorePrintersForJobAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _dispatchServiceMock.Verify(
            d => d.DispatchJobAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<DispatchScore>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    [Trait("Category", "Dispatch")]
    [Trait("Phase", "2")]
    public async Task OnPrinterIdle_NoCompatibleJobs_LogsAndSkips()
    {
        // Arrange: all jobs score below threshold or are eliminated
        SeedSettings(enabled: true, mode: AutoDispatchMode.Auto, idleThresholdSeconds: 0, minimumScoreThreshold: 50.0);
        (_, Guid printerId) = SeedPrinter();

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));
        AutoDispatchBackgroundService svc = CreateService();
        PrintJob job = SeedQueuedJob();

        DispatchScore eliminatedScore = new(
            printerId, "Idle Printer", 0,
            new Dictionary<string, FactorScore>(),
            Eliminated: true,
            EliminationReasons: ["No compatible material"]);

        _scorerMock
            .Setup(s => s.ScorePrintersForJobAsync(job.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync([eliminatedScore]);

        await svc.ProcessPrinterIdleAsync(printerId, skipIdleThreshold: true, cts.Token);

        // Should NOT dispatch
        _dispatchServiceMock.Verify(
            d => d.DispatchJobAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<DispatchScore>(), It.IsAny<CancellationToken>()),
            Times.Never);

        // Should send dispatchfailed SignalR event
        _clientProxyMock.Verify(
            c => c.SendCoreAsync("dispatchfailed", It.IsAny<object?[]>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    [Trait("Category", "Dispatch")]
    [Trait("Phase", "2")]
    public async Task OnPrinterIdle_ScoreBelowThreshold_DoesNotDispatch()
    {
        // Arrange: job scores 30 but threshold is 50
        SeedSettings(enabled: true, mode: AutoDispatchMode.Auto, idleThresholdSeconds: 0, minimumScoreThreshold: 50.0);
        (_, Guid printerId) = SeedPrinter();

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));
        AutoDispatchBackgroundService svc = CreateService();
        PrintJob job = SeedQueuedJob();

        DispatchScore lowScore = new(
            printerId, "Idle Printer", 30.0,
            new Dictionary<string, FactorScore>(),
            Eliminated: false,
            EliminationReasons: []);

        _scorerMock
            .Setup(s => s.ScorePrintersForJobAsync(job.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync([lowScore]);

        await svc.ProcessPrinterIdleAsync(printerId, skipIdleThreshold: true, cts.Token);

        _dispatchServiceMock.Verify(
            d => d.DispatchJobAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<DispatchScore>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    [Trait("Category", "Dispatch")]
    [Trait("Phase", "2")]
    public async Task OnPrinterIdle_SuggestMode_NotifiesButDoesNotDispatch()
    {
        // Arrange: mode is Suggest — should send suggestion event, not auto-dispatch
        SeedSettings(enabled: true, mode: AutoDispatchMode.Suggest, idleThresholdSeconds: 0);
        (Printer printer, Guid printerId) = SeedPrinter();

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));
        AutoDispatchBackgroundService svc = CreateService();
        PrintJob job = SeedQueuedJob("benchy-suggest");

        DispatchScore goodScore = new(
            printerId, printer.Name, 90.0,
            new Dictionary<string, FactorScore>(),
            Eliminated: false,
            EliminationReasons: []);

        _scorerMock
            .Setup(s => s.ScorePrintersForJobAsync(job.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync([goodScore]);

        await svc.ProcessPrinterIdleAsync(printerId, skipIdleThreshold: true, cts.Token);

        // Should NOT call DispatchJobAsync
        _dispatchServiceMock.Verify(
            d => d.DispatchJobAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<DispatchScore>(), It.IsAny<CancellationToken>()),
            Times.Never);

        // Should send dispatchsuggestion event
        _clientProxyMock.Verify(
            c => c.SendCoreAsync("dispatchsuggestion", It.IsAny<object?[]>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    [Trait("Category", "Dispatch")]
    [Trait("Phase", "2")]
    public async Task OnPrinterIdle_SuggestMode_LogsSuggestionToDispatchLog()
    {
        // Arrange
        SeedSettings(enabled: true, mode: AutoDispatchMode.Suggest, idleThresholdSeconds: 0);
        (Printer printer, Guid printerId) = SeedPrinter();

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));
        AutoDispatchBackgroundService svc = CreateService();
        PrintJob job = SeedQueuedJob("log-check");

        DispatchScore goodScore = new(
            printerId, printer.Name, 88.0,
            new Dictionary<string, FactorScore>(),
            Eliminated: false,
            EliminationReasons: []);

        _scorerMock
            .Setup(s => s.ScorePrintersForJobAsync(job.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync([goodScore]);

        await svc.ProcessPrinterIdleAsync(printerId, skipIdleThreshold: true, cts.Token);

        // Assert: DispatchLog was written with Suggested action
        List<DispatchLog> logs = await _db.DispatchLogs.ToListAsync();
        logs.Should().ContainSingle(l =>
            l.PrintJobId == job.Id
            && l.PrinterId == printerId
            && l.Action == DispatchAction.Suggested);
    }

    [Fact]
    [Trait("Category", "Dispatch")]
    [Trait("Phase", "2")]
    public async Task OnPrinterIdle_PrinterGoesOfflineDuringWait_CancelsDispatch()
    {
        // Arrange: idle threshold is 2 seconds — printer goes offline before it elapses
        SeedSettings(enabled: true, mode: AutoDispatchMode.Auto, idleThresholdSeconds: 2);
        (_, Guid printerId) = SeedPrinter();

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));
        AutoDispatchBackgroundService svc = CreateService();

        // Now seed job and trigger
        SeedQueuedJob();
        Task processTask = svc.ProcessPrinterIdleAsync(printerId, skipIdleThreshold: false, cts.Token);

        // Wait until the service has actually registered the pending idle-wait, then
        // cancel it. Polling for registration (rather than a fixed delay) makes this
        // deterministic: the service registers the per-printer CTS only after a
        // DB-bound settings read, which can take longer than a fixed delay under load.
        while (!_trigger.HasPendingDispatch(printerId))
        {
            await Task.Delay(20, cts.Token);
        }

        _trigger.CancelPendingDispatch(printerId);
        await processTask;

        // Should never reach scoring or dispatch
        _scorerMock.Verify(
            s => s.ScorePrintersForJobAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _dispatchServiceMock.Verify(
            d => d.DispatchJobAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<DispatchScore>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    [Trait("Category", "Dispatch")]
    [Trait("Phase", "2")]
    public async Task OnPrinterIdle_PrinterDisabled_AbortsDispatchCycle()
    {
        // Arrange: printer exists but IsEnabled=false
        SeedSettings(enabled: true, mode: AutoDispatchMode.Auto, idleThresholdSeconds: 0);
        (Printer printer, Guid printerId) = SeedPrinter();
        printer.IsEnabled = false;
        _db.SaveChanges();

        SeedQueuedJob();

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));
        AutoDispatchBackgroundService svc = CreateService();
        await svc.ProcessPrinterIdleAsync(printerId, skipIdleThreshold: true, cts.Token);

        _scorerMock.Verify(
            s => s.ScorePrintersForJobAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    [Trait("Category", "Dispatch")]
    [Trait("Phase", "2")]
    public async Task OnPrinterIdle_PrinterHasActiveJob_AbortsDispatch()
    {
        // Arrange: printer already has an active (Printing) job
        SeedSettings(enabled: true, mode: AutoDispatchMode.Auto, idleThresholdSeconds: 0);
        (_, Guid printerId) = SeedPrinter();

        // Add an active job assigned to this printer
        Guid gcodeFileId = Guid.NewGuid();
        _db.GcodeFiles.Add(new GcodeFile
        {
            Id = gcodeFileId,
            Name = "active.gcode",
            FileName = "active.gcode",
            FilePath = "/gcode/",
            FolderId = _folderId,
            FileHash = "xyz",

            UploadedAt = DateTime.UtcNow,
        });
        _db.PrintJobs.Add(new PrintJob
        {
            Id = Guid.NewGuid(),
            Name = "Active Print",
            GcodeFileId = gcodeFileId,
            AssignedPrinterId = printerId,
            Status = PrintJobStatus.Printing,

            QueuedAt = DateTime.UtcNow,
        });
        _db.SaveChanges();

        // Also queue a job for potential dispatch
        SeedQueuedJob();

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));
        AutoDispatchBackgroundService svc = CreateService();
        await svc.ProcessPrinterIdleAsync(printerId, skipIdleThreshold: true, cts.Token);

        // Should not score or dispatch because printer already has active job
        _scorerMock.Verify(
            s => s.ScorePrintersForJobAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    [Trait("Category", "Dispatch")]
    [Trait("Phase", "2")]
    public async Task OnPrinterIdle_PrinterAutoDispatchDisabled_DoesNotDispatch()
    {
        // Arrange: global auto-dispatch ON, but per-printer auto-dispatch is OFF
        SeedSettings(enabled: true, mode: AutoDispatchMode.Auto, idleThresholdSeconds: 0);
        (Printer printer, Guid printerId) = SeedPrinter();
        printer.AutoDispatchEnabled = false;
        _db.SaveChanges();

        SeedQueuedJob("should-not-dispatch");

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));
        AutoDispatchBackgroundService svc = CreateService();
        await svc.ProcessPrinterIdleAsync(printerId, skipIdleThreshold: true, cts.Token);

        // Per-printer auto-dispatch disabled → never score, never dispatch
        _scorerMock.Verify(
            s => s.ScorePrintersForJobAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _dispatchServiceMock.Verify(
            d => d.DispatchJobAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<DispatchScore>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    [Trait("Category", "Dispatch")]
    [Trait("Phase", "2")]
    public async Task OnPrinterIdle_DispatchThrowsException_LogsFailureAndSendsEvent()
    {
        // Arrange: dispatch service throws an exception
        SeedSettings(enabled: true, mode: AutoDispatchMode.Auto, idleThresholdSeconds: 0);
        (Printer printer, Guid printerId) = SeedPrinter();

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));
        AutoDispatchBackgroundService svc = CreateService();
        PrintJob job = SeedQueuedJob();

        DispatchScore goodScore = new(
            printerId, printer.Name, 90.0,
            new Dictionary<string, FactorScore>(),
            Eliminated: false,
            EliminationReasons: []);

        _scorerMock
            .Setup(s => s.ScorePrintersForJobAsync(job.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync([goodScore]);

        _dispatchServiceMock
            .Setup(d => d.DispatchJobAsync(job.Id, printerId, "system:auto-dispatch", It.IsAny<DispatchScore>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Printer connection failed"));

        await svc.ProcessPrinterIdleAsync(printerId, skipIdleThreshold: true, cts.Token);

        // Should send dispatchfailed SignalR event
        _clientProxyMock.Verify(
            c => c.SendCoreAsync("dispatchfailed", It.IsAny<object?[]>(), It.IsAny<CancellationToken>()),
            Times.Once);

        // Should log failure to DispatchLogs
        List<DispatchLog> logs = await _db.DispatchLogs.ToListAsync();
        logs.Should().ContainSingle(l =>
            l.PrintJobId == job.Id
            && l.Action == DispatchAction.Failed);
    }
}
