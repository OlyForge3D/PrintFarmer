using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Dtos.Attention;
using Farm.Infrastructure.Repositories.Maintenance;
using Farm.Infrastructure.Services.Attention;
using Farm.Infrastructure.Services.Maintenance;
using Farm.Infrastructure.Services.OperatorFeatures;
using Farm.Infrastructure.Services.Printers;
using Farm.Infrastructure.Services.Webhooks;
using Farm.Web.Api.Controllers;
using Farm.Web.Api.Hubs;
using Farm.Web.Api.Tests.Builders;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Services.Maintenance;

/// <summary>
/// Issue #711 round-7 Finding 5: resolving a maintenance alert must be atomic. Previously the
/// completion log was committed (immediate SaveChanges) before the alert mutator re-checked the
/// per-tool gate, so a gate that flipped between the API-side pre-check and the mutator left an
/// orphaned log with an unresolved alert while the request returned 400. These tests exercise the
/// transactional <see cref="MaintenanceAlertResolutionService"/> over real SQLite.
/// </summary>
public sealed class MaintenanceAlertResolutionServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly AppDbContext _context;

    public MaintenanceAlertResolutionServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;

        _context = new AppDbContext(_options);
        _context.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private AppDbContext NewContext() => new(_options);

    private MaintenanceAlert SeedToolheadAlert()
    {
        // A valid printer + physical toolhead graph so the FK-enforced alert/log inserts succeed
        // (EF Core keeps SQLite foreign keys ON).
        string suffix = Guid.NewGuid().ToString("N")[..8];
        var mfg = new Manufacturer { Id = Guid.NewGuid(), Name = $"Res Mfg {suffix}" };
        var model = new PrinterModel { Id = Guid.NewGuid(), ManufacturerId = mfg.Id, Name = $"Res Model {suffix}" };
        Printer printer = new PrinterBuilder().Build();
        printer.ManufacturerId = mfg.Id;
        printer.ModelId = model.Id;
        printer.ServerUrl = $"http://res-{suffix}.local";

        var toolhead = new Toolhead
        {
            Id = Guid.NewGuid(),
            PrinterId = printer.Id,
            Name = "T0",
            Index = 0,
            IsPrimary = true,
            ToolheadType = ToolheadType.Physical,
            UpdatedAt = DateTime.UtcNow
        };
        printer.Toolheads.Add(toolhead);

        _context.Manufacturers.Add(mfg);
        _context.PrinterModels.Add(model);
        _context.Printers.Add(printer);
        _context.SaveChanges();

        var alert = new MaintenanceAlert
        {
            Id = Guid.NewGuid(),
            PrinterId = printer.Id,
            ToolheadId = toolhead.Id,
            Title = "Lubricate rails",
            Status = MaintenanceAlertStatus.Active
        };
        _context.MaintenanceAlerts.Add(alert);
        _context.SaveChanges();
        return alert;
    }

    private static MaintenanceLog BuildLog(MaintenanceAlert alert) => new()
    {
        Id = Guid.NewGuid(),
        PrinterId = alert.PrinterId,
        ToolheadId = alert.ToolheadId,
        TaskName = alert.Title,
        PerformedAt = DateTime.UtcNow,
        PerformedBy = "operator"
    };

    [Fact]
    public async Task ResolveWithLog_GateDisabledForToolheadAlert_RollsBackLogAndLeavesAlertActive()
    {
        MaintenanceAlert alert = SeedToolheadAlert();

        var gate = new Mock<IOperatorFeatureGate>(MockBehavior.Loose);
        gate.Setup(g => g.IsEnabled(OperatorFeature.MultiSlotFallback)).Returns(false);

        var service = new MaintenanceAlertResolutionService(_context, gate.Object);

        Func<Task> act = () => service.ResolveWithLogAsync(alert.Id, BuildLog(alert), "operator");

        await act.Should().ThrowAsync<PerToolMaintenanceDisabledException>();

        // A fresh context reads the committed database state: the staged log must have rolled back
        // and the alert must remain in its prior (Active) state.
        await using AppDbContext verify = NewContext();
        (await verify.MaintenanceLogs.CountAsync()).Should().Be(0);
        MaintenanceAlert? persisted = await verify.MaintenanceAlerts.FirstOrDefaultAsync(a => a.Id == alert.Id);
        persisted.Should().NotBeNull();
        persisted!.Status.Should().Be(MaintenanceAlertStatus.Active);
        persisted.ResolvedAt.Should().BeNull();
    }

    [Fact]
    public async Task ResolveWithLog_GateEnabled_PersistsLogAndResolvesAlertAtomically()
    {
        MaintenanceAlert alert = SeedToolheadAlert();

        var gate = new Mock<IOperatorFeatureGate>(MockBehavior.Loose);
        gate.Setup(g => g.IsEnabled(OperatorFeature.MultiSlotFallback)).Returns(true);

        var service = new MaintenanceAlertResolutionService(_context, gate.Object);

        MaintenanceAlertResolutionResult? result =
            await service.ResolveWithLogAsync(alert.Id, BuildLog(alert), "operator");

        result.Should().NotBeNull();
        result!.Alert.Status.Should().Be(MaintenanceAlertStatus.Resolved);

        await using AppDbContext verify = NewContext();
        (await verify.MaintenanceLogs.CountAsync()).Should().Be(1);
        MaintenanceAlert? persisted = await verify.MaintenanceAlerts.FirstOrDefaultAsync(a => a.Id == alert.Id);
        persisted!.Status.Should().Be(MaintenanceAlertStatus.Resolved);
        persisted.ResolvedBy.Should().Be("operator");
    }

    [Fact]
    public async Task ResolveWithLog_UnknownAlert_ReturnsNullAndWritesNothing()
    {
        var gate = new Mock<IOperatorFeatureGate>(MockBehavior.Loose);
        gate.Setup(g => g.IsEnabled(OperatorFeature.MultiSlotFallback)).Returns(true);
        var service = new MaintenanceAlertResolutionService(_context, gate.Object);

        var orphanLog = new MaintenanceLog
        {
            Id = Guid.NewGuid(),
            PrinterId = Guid.NewGuid(),
            TaskName = "x",
            PerformedAt = DateTime.UtcNow,
            PerformedBy = "operator"
        };

        MaintenanceAlertResolutionResult? result =
            await service.ResolveWithLogAsync(Guid.NewGuid(), orphanLog, "operator");

        result.Should().BeNull();
        await using AppDbContext verify = NewContext();
        (await verify.MaintenanceLogs.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task ResolveAlert_GateFlipsAfterPreCheck_DoesNotPersistLogAndReturnsBadRequest()
    {
        // Reproduce the exact TOCTOU: the gate is enabled at the controller's pre-check but flips to
        // disabled by the time the resolution service re-checks it inside the transaction.
        MaintenanceAlert alert = SeedToolheadAlert();

        var gate = new Mock<IOperatorFeatureGate>(MockBehavior.Strict);
        gate.SetupSequence(g => g.IsEnabled(OperatorFeature.MultiSlotFallback))
            .Returns(true)   // controller pre-check
            .Returns(false); // service re-check inside the transaction

        var statisticsRepository = new Mock<IPrinterStatisticsRepository>(MockBehavior.Loose);
        statisticsRepository
            .Setup(r => r.GetByPrinterIdAsync(alert.PrinterId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((PrinterStatistics?)null);
        var toolheadStatisticsRepository = new Mock<IToolheadStatisticsRepository>(MockBehavior.Loose);
        toolheadStatisticsRepository
            .Setup(r => r.GetCumulativeHoursAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((double?)null);

        var resolutionService = new MaintenanceAlertResolutionService(_context, gate.Object);

        var controller = new MaintenanceController(
            logger: NullLogger<MaintenanceController>.Instance,
            alertRepository: new EfMaintenanceAlertRepository(_context),
            logRepository: new EfMaintenanceLogRepository(_context),
            deploymentRepository: Mock.Of<IPrinterMaintenanceScheduleRepository>(),
            statisticsRepository: statisticsRepository.Object,
            toolheadStatisticsRepository: toolheadStatisticsRepository.Object,
            alertService: Mock.Of<IMaintenanceAlertService>(),
            printersService: Mock.Of<IPrintersService>(),
            operatorFeatureGate: gate.Object,
            maintenanceHub: Mock.Of<IHubContext<MaintenanceHub>>(),
            webhookService: Mock.Of<IWebhookService>(),
            alertResolutionService: resolutionService);

        ActionResult<ResolveAlertResponse> result = await controller.ResolveAlertAsync(
            alert.Id,
            new ResolveAlertRequest(PerformedBy: "operator", Notes: null, DurationMinutes: null, Cost: null, PartsReplaced: null),
            CancellationToken.None);

        BadRequestObjectResult bad = Assert.IsType<BadRequestObjectResult>(result.Result);
        bad.Value.Should().Be("Per-tool maintenance is disabled.");

        // The gate flipped, but the transaction must have rolled the staged log back and left the
        // alert unresolved — no orphaned log, no partial state.
        await using AppDbContext verify = NewContext();
        (await verify.MaintenanceLogs.CountAsync()).Should().Be(0);
        MaintenanceAlert? persisted = await verify.MaintenanceAlerts.FirstOrDefaultAsync(a => a.Id == alert.Id);
        persisted!.Status.Should().Be(MaintenanceAlertStatus.Active);
    }

    [Fact]
    public async Task ResolveAlertWithCompletionLog_WritesAuthoritativeLogWithBaselinesAndResolvesAlert()
    {
        // Finding H6 (issue #711): the coordinator entry point (used by the unified attention Resolve)
        // must produce a real completion log carrying the hour baselines the alert engine reads
        // (PrinterHoursAtMaintenance / ToolheadHoursAtMaintenance) so a resolved alert is not
        // re-derived as still-due and recreated on the next evaluation.
        MaintenanceAlert alert = SeedToolheadAlert();

        var gate = new Mock<IOperatorFeatureGate>(MockBehavior.Loose);
        gate.Setup(g => g.IsEnabled(OperatorFeature.MultiSlotFallback)).Returns(true);

        var printerStats = new Mock<IPrinterStatisticsRepository>(MockBehavior.Loose);
        printerStats
            .Setup(r => r.GetByPrinterIdAsync(alert.PrinterId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PrinterStatistics { PrinterId = alert.PrinterId, TotalPrintHours = 123.5 });
        var toolheadStats = new Mock<IToolheadStatisticsRepository>(MockBehavior.Loose);
        toolheadStats
            .Setup(r => r.GetCumulativeHoursAsync(alert.ToolheadId!.Value, It.IsAny<CancellationToken>()))
            .ReturnsAsync(42.0);

        var service = new MaintenanceAlertResolutionService(
            _context,
            gate.Object,
            attentionBroadcaster: null,
            printerStatisticsRepository: printerStats.Object,
            toolheadStatisticsRepository: toolheadStats.Object);

        MaintenanceAlertResolutionResult? result =
            await service.ResolveAlertWithCompletionLogAsync(alert.Id, "operator", notes: "done", CancellationToken.None);

        result.Should().NotBeNull();
        result!.Alert.Status.Should().Be(MaintenanceAlertStatus.Resolved);

        await using AppDbContext verify = NewContext();
        MaintenanceLog? log = await verify.MaintenanceLogs.FirstOrDefaultAsync(l => l.ResolvedAlertId == alert.Id);
        log.Should().NotBeNull();
        log!.ToolheadId.Should().Be(alert.ToolheadId);
        log.PrinterHoursAtMaintenance.Should().Be(123.5);
        log.ToolheadHoursAtMaintenance.Should().Be(42.0);
        log.PerformedBy.Should().Be("operator");
        log.Notes.Should().Be("done");
    }

    [Fact]
    public async Task ResolveWithLog_CalledTwiceForSameAlert_PersistsSingleLogAndSecondReturnsExisting()
    {
        // Finding H7 (issue #711) idempotency: a duplicate submission (client retry after a dropped
        // response, or a double-click) must NOT create a second completion log. The already-Resolved
        // alert short-circuits to the existing linked log.
        MaintenanceAlert alert = SeedToolheadAlert();

        var gate = new Mock<IOperatorFeatureGate>(MockBehavior.Loose);
        gate.Setup(g => g.IsEnabled(OperatorFeature.MultiSlotFallback)).Returns(true);
        var notifier = new Mock<IMaintenanceResolutionNotifier>(MockBehavior.Strict);
        notifier
            .Setup(n => n.NotifyCreatedAsync(
                It.IsAny<MaintenanceAlert>(),
                It.IsAny<MaintenanceLog>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var service = new MaintenanceAlertResolutionService(
            _context,
            gate.Object,
            resolutionNotifier: notifier.Object);

        MaintenanceAlertResolutionResult? first =
            await service.ResolveWithLogAsync(alert.Id, BuildLog(alert), "operator");
        first.Should().NotBeNull();
        first!.Created.Should().BeTrue();
        Guid firstLogId = first.Log!.Id;

        MaintenanceAlertResolutionResult? second =
            await service.ResolveWithLogAsync(alert.Id, BuildLog(alert), "operator");
        second.Should().NotBeNull();
        second!.Created.Should().BeFalse();
        second.Log!.Id.Should().Be(firstLogId);
        second.Alert.Status.Should().Be(MaintenanceAlertStatus.Resolved);

        await using AppDbContext verify = NewContext();
        (await verify.MaintenanceLogs.CountAsync()).Should().Be(1);
        notifier.Verify(
            n => n.NotifyCreatedAsync(
                It.IsAny<MaintenanceAlert>(),
                It.IsAny<MaintenanceLog>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ResolveWithLog_BroadcasterThrows_StillPersistsLogAndReturnsSuccess()
    {
        // Finding H7: broadcasting runs AFTER the resolution commits. A broadcast failure is an
        // observability concern, not a correctness one, so it must be swallowed — the resolution is
        // already durable and the caller must observe success (no HTTP 500 that would prompt a retry
        // and a duplicate completion).
        MaintenanceAlert alert = SeedToolheadAlert();

        var gate = new Mock<IOperatorFeatureGate>(MockBehavior.Loose);
        gate.Setup(g => g.IsEnabled(OperatorFeature.MultiSlotFallback)).Returns(true);

        var broadcaster = new Mock<IAttentionBroadcaster>(MockBehavior.Loose);
        broadcaster
            .Setup(b => b.NotifyChangedAsync(It.IsAny<AttentionChangedPayload>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("hub down"));

        var service = new MaintenanceAlertResolutionService(_context, gate.Object, broadcaster.Object);

        MaintenanceAlertResolutionResult? result =
            await service.ResolveWithLogAsync(alert.Id, BuildLog(alert), "operator");

        result.Should().NotBeNull();
        result!.Alert.Status.Should().Be(MaintenanceAlertStatus.Resolved);

        await using AppDbContext verify = NewContext();
        (await verify.MaintenanceLogs.CountAsync()).Should().Be(1);
        MaintenanceAlert? persisted = await verify.MaintenanceAlerts.FirstOrDefaultAsync(a => a.Id == alert.Id);
        persisted!.Status.Should().Be(MaintenanceAlertStatus.Resolved);
        broadcaster.Verify(
            b => b.NotifyChangedAsync(It.IsAny<AttentionChangedPayload>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ResolveWithLog_DuplicateCompletionForSameAlert_UniqueIndexCatchesAndReturnsWinner()
    {
        // Finding H7: even if two resolves race past the status pre-check (both observe an Active
        // alert), the filtered-unique index on ResolvedAlertId guarantees at most one completion log
        // per alert. Here the "winning" racer's log is pre-inserted directly while the alert is left
        // Active, so the service's insert collides on the index. The service must catch the
        // DbUpdateException and return the committed winner rather than surfacing an error.
        MaintenanceAlert alert = SeedToolheadAlert();

        MaintenanceLog winner = BuildLog(alert);
        winner.ResolvedAlertId = alert.Id;
        _context.MaintenanceLogs.Add(winner);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var gate = new Mock<IOperatorFeatureGate>(MockBehavior.Loose);
        gate.Setup(g => g.IsEnabled(OperatorFeature.MultiSlotFallback)).Returns(true);
        var service = new MaintenanceAlertResolutionService(_context, gate.Object);

        MaintenanceAlertResolutionResult? result =
            await service.ResolveWithLogAsync(alert.Id, BuildLog(alert), "operator");

        result.Should().NotBeNull();
        result!.Log!.Id.Should().Be(winner.Id);
        result.Created.Should().BeFalse();

        await using AppDbContext verify = NewContext();
        (await verify.MaintenanceLogs.CountAsync(l => l.ResolvedAlertId == alert.Id)).Should().Be(1);
    }

    [Fact]
    public async Task ResolveWithLog_DismissedAlert_ThrowsConflictAndDoesNotCreateLog()
    {
        MaintenanceAlert alert = SeedToolheadAlert();
        alert.Status = MaintenanceAlertStatus.Dismissed;
        alert.DismissedAt = DateTime.UtcNow;
        alert.DismissedBy = "operator";
        await _context.SaveChangesAsync();

        var service = new MaintenanceAlertResolutionService(_context);

        Func<Task> act = () =>
            service.ResolveWithLogAsync(alert.Id, BuildLog(alert), "operator");

        await act.Should().ThrowAsync<MaintenanceAlertNotResolvableException>()
            .WithMessage("*Dismissed*cannot be resolved*");
        await using AppDbContext verify = NewContext();
        (await verify.MaintenanceLogs.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task ResolveWithLog_ResolvedLegacyAlertWithoutLog_ReturnsIdempotentResult()
    {
        MaintenanceAlert alert = SeedToolheadAlert();
        alert.Status = MaintenanceAlertStatus.Resolved;
        alert.ResolvedAt = DateTime.UtcNow;
        alert.ResolvedBy = "legacy";
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var service = new MaintenanceAlertResolutionService(_context);

        MaintenanceAlertResolutionResult? result =
            await service.ResolveWithLogAsync(
                alert.Id,
                BuildLog(alert),
                "operator");

        result.Should().NotBeNull();
        result!.Created.Should().BeFalse();
        result.Log.Should().BeNull();
        result.Alert.Status.Should().Be(MaintenanceAlertStatus.Resolved);
        await using AppDbContext verify = NewContext();
        (await verify.MaintenanceLogs.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task ResolveWithLog_ResolutionNotifierThrows_ReturnsSuccessAfterCommit()
    {
        MaintenanceAlert alert = SeedToolheadAlert();
        var notifier = new Mock<IMaintenanceResolutionNotifier>(MockBehavior.Strict);
        notifier
            .Setup(n => n.NotifyCreatedAsync(
                It.IsAny<MaintenanceAlert>(),
                It.IsAny<MaintenanceLog>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("webhook unavailable"));
        var service = new MaintenanceAlertResolutionService(
            _context,
            resolutionNotifier: notifier.Object);

        MaintenanceAlertResolutionResult? result =
            await service.ResolveWithLogAsync(
                alert.Id,
                BuildLog(alert),
                "operator");

        result.Should().NotBeNull();
        result!.Created.Should().BeTrue();
        await using AppDbContext verify = NewContext();
        (await verify.MaintenanceLogs.CountAsync()).Should().Be(1);
        (await verify.MaintenanceAlerts.SingleAsync(a => a.Id == alert.Id))
            .Status.Should().Be(MaintenanceAlertStatus.Resolved);
    }

    [Fact]
    public async Task DeleteAlert_LinkedCompletionLog_RejectsDeleteAndPreservesLink()
    {
        MaintenanceAlert alert = SeedToolheadAlert();
        var service = new MaintenanceAlertResolutionService(_context);
        _ = await service.ResolveWithLogAsync(
            alert.Id,
            BuildLog(alert),
            "operator");
        _context.ChangeTracker.Clear();

        MaintenanceAlert persistedAlert =
            await _context.MaintenanceAlerts.SingleAsync(a => a.Id == alert.Id);
        _context.MaintenanceAlerts.Remove(persistedAlert);

        Func<Task> act = () => _context.SaveChangesAsync();

        await act.Should().ThrowAsync<DbUpdateException>();
        _context.ChangeTracker.Clear();
        await using AppDbContext verify = NewContext();
        (await verify.MaintenanceAlerts.CountAsync(a => a.Id == alert.Id)).Should().Be(1);
        (await verify.MaintenanceLogs.CountAsync(l => l.ResolvedAlertId == alert.Id))
            .Should().Be(1);
    }

    [Fact]
    public async Task ResolveWithLog_ConcurrentCalls_CreatesSingleCompletionLog()
    {
        string databasePath = Path.Combine(
            AppContext.BaseDirectory,
            $"maintenance-resolution-{Guid.NewGuid():N}.db");
        string connectionString =
            $"Data Source={databasePath};Pooling=False;Default Timeout=30";
        DbContextOptions<AppDbContext> options =
            new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite(connectionString)
                .Options;

        try
        {
            MaintenanceAlert alert;
            await using (var setup = new AppDbContext(options))
            {
                await setup.Database.EnsureCreatedAsync();
                string suffix = Guid.NewGuid().ToString("N")[..8];
                Manufacturer manufacturer = new()
                {
                    Id = Guid.NewGuid(),
                    Name = $"Concurrent Mfg {suffix}"
                };
                PrinterModel model = new()
                {
                    Id = Guid.NewGuid(),
                    ManufacturerId = manufacturer.Id,
                    Name = $"Concurrent Model {suffix}"
                };
                Printer printer = new PrinterBuilder().Build();
                printer.ManufacturerId = manufacturer.Id;
                printer.ModelId = model.Id;
                printer.ServerUrl = $"http://concurrent-{suffix}.local";
                alert = new MaintenanceAlert
                {
                    Id = Guid.NewGuid(),
                    PrinterId = printer.Id,
                    Title = "Concurrent resolution",
                    Status = MaintenanceAlertStatus.Active
                };

                setup.Manufacturers.Add(manufacturer);
                setup.PrinterModels.Add(model);
                setup.Printers.Add(printer);
                setup.MaintenanceAlerts.Add(alert);
                await setup.SaveChangesAsync();
            }

            var start = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);

            async Task<MaintenanceAlertResolutionResult?> ResolveAsync(
                string resolvedBy)
            {
                await start.Task;
                await using var context = new AppDbContext(options);
                var service = new MaintenanceAlertResolutionService(context);
                return await service.ResolveWithLogAsync(
                    alert.Id,
                    BuildLog(alert),
                    resolvedBy);
            }

            Task<MaintenanceAlertResolutionResult?> first =
                ResolveAsync("operator-a");
            Task<MaintenanceAlertResolutionResult?> second =
                ResolveAsync("operator-b");
            start.SetResult();

            MaintenanceAlertResolutionResult?[] results =
                await Task.WhenAll(first, second);

            results.Should().NotContainNulls();
            results.Should().ContainSingle(result => result!.Created);
            results.Should().ContainSingle(result => !result!.Created);

            await using var verify = new AppDbContext(options);
            (await verify.MaintenanceLogs.CountAsync(
                log => log.ResolvedAlertId == alert.Id)).Should().Be(1);
            (await verify.MaintenanceAlerts.SingleAsync(
                candidate => candidate.Id == alert.Id))
                .Status.Should().Be(MaintenanceAlertStatus.Resolved);
        }
        finally
        {
            if (File.Exists(databasePath))
            {
                File.Delete(databasePath);
            }
        }
    }
}
