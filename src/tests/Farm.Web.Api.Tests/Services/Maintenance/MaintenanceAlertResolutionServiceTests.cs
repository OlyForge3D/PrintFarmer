using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Maintenance;
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
}
