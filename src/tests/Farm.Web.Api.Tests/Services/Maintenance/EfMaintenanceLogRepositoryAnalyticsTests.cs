using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Maintenance;
using Farm.Web.Api.Tests.Builders;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Farm.Web.Api.Tests.Services.Maintenance;

/// <summary>
/// Finding H5 (issue #711): maintenance analytics must not surface per-toolhead logs
/// when the multi-slot fallback operator feature is off. These tests exercise the
/// <c>includeToolheadScope</c> filter on <see cref="EfMaintenanceLogRepository"/> over
/// real SQLite with a mixed set of printer-wide and per-toolhead logs.
/// </summary>
public sealed class EfMaintenanceLogRepositoryAnalyticsTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly AppDbContext _context;
    private readonly Guid _printerId;

    public EfMaintenanceLogRepositoryAnalyticsTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;

        _context = new AppDbContext(_options);
        _context.Database.EnsureCreated();
        _printerId = SeedPrinterWithMixedLogs();
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private EfMaintenanceLogRepository NewRepo() => new(new AppDbContext(_options));

    private Guid SeedPrinterWithMixedLogs()
    {
        string suffix = Guid.NewGuid().ToString("N")[..8];
        var mfg = new Manufacturer { Id = Guid.NewGuid(), Name = $"An Mfg {suffix}" };
        var model = new PrinterModel { Id = Guid.NewGuid(), ManufacturerId = mfg.Id, Name = $"An Model {suffix}" };
        Printer printer = new PrinterBuilder().Build();
        printer.ManufacturerId = mfg.Id;
        printer.ModelId = model.Id;
        printer.ServerUrl = $"http://an-{suffix}.local";

        var toolhead = new Toolhead
        {
            Id = Guid.NewGuid(),
            PrinterId = printer.Id,
            Name = "T0",
            Index = 0,
            IsPrimary = true,
            ToolheadType = ToolheadType.Physical,
            UpdatedAt = DateTime.UtcNow,
        };
        printer.Toolheads.Add(toolhead);

        _context.Manufacturers.Add(mfg);
        _context.PrinterModels.Add(model);
        _context.Printers.Add(printer);
        _context.SaveChanges();

        DateTime now = DateTime.UtcNow;

        // Printer-wide log (ToolheadId == null).
        _context.MaintenanceLogs.Add(new MaintenanceLog
        {
            Id = Guid.NewGuid(),
            PrinterId = printer.Id,
            ToolheadId = null,
            TaskName = "Bed level",
            Component = "Bed",
            PerformedAt = now.AddDays(-3),
            Cost = 10m,
            DurationMinutes = 20,
            PrinterHoursAtMaintenance = 100,
        });

        // Per-toolhead log (ToolheadId set) — must be hidden when the gate is off.
        _context.MaintenanceLogs.Add(new MaintenanceLog
        {
            Id = Guid.NewGuid(),
            PrinterId = printer.Id,
            ToolheadId = toolhead.Id,
            TaskName = "Nozzle swap",
            Component = "Hotend",
            PerformedAt = now.AddDays(-1),
            Cost = 25m,
            DurationMinutes = 15,
            PrinterHoursAtMaintenance = 120,
            ToolheadHoursAtMaintenance = 60,
        });

        _context.SaveChanges();
        return printer.Id;
    }

    [Fact]
    public async Task GetTrendsAsync_GateOff_ExcludesPerToolheadLogs()
    {
        EfMaintenanceLogRepository repo = NewRepo();

        var trends = await repo.GetTrendsAsync(
            DateTime.UtcNow.AddDays(-30),
            DateTime.UtcNow.AddDays(1),
            includeToolheadScope: false,
            CancellationToken.None);

        trends.Should().ContainSingle();
        trends[0].Component.Should().Be("Bed");
    }

    [Fact]
    public async Task GetTrendsAsync_GateOn_IncludesPerToolheadLogs()
    {
        EfMaintenanceLogRepository repo = NewRepo();

        var trends = await repo.GetTrendsAsync(
            DateTime.UtcNow.AddDays(-30),
            DateTime.UtcNow.AddDays(1),
            includeToolheadScope: true,
            CancellationToken.None);

        trends.Should().HaveCount(2);
        trends.Select(t => t.Component).Should().Contain(new[] { "Bed", "Hotend" });
    }

    [Fact]
    public async Task GetCostAnalysisAsync_GateOff_ExcludesPerToolheadCost()
    {
        EfMaintenanceLogRepository repo = NewRepo();

        var costsOff = await repo.GetCostAnalysisAsync(months: 6, includeToolheadScope: false, CancellationToken.None);
        var costsOn = await repo.GetCostAnalysisAsync(months: 6, includeToolheadScope: true, CancellationToken.None);

        costsOff.Sum(c => c.TotalCost).Should().Be(10m);
        costsOn.Sum(c => c.TotalCost).Should().Be(35m);
    }

    [Fact]
    public async Task GetPrinterUptimeAsync_GateOff_ExcludesPerToolheadFromCounts()
    {
        EfMaintenanceLogRepository repo = NewRepo();

        var uptimeOff = await repo.GetPrinterUptimeAsync(includeToolheadScope: false, CancellationToken.None);
        var uptimeOn = await repo.GetPrinterUptimeAsync(includeToolheadScope: true, CancellationToken.None);

        PrinterUptimeEntry off = uptimeOff.Single(u => u.PrinterId == _printerId);
        PrinterUptimeEntry on = uptimeOn.Single(u => u.PrinterId == _printerId);

        off.MaintenanceCount.Should().Be(1);
        off.TotalDowntimeMinutes.Should().Be(20);
        on.MaintenanceCount.Should().Be(2);
        on.TotalDowntimeMinutes.Should().Be(35);
    }

    [Fact]
    public async Task GetComponentLifespanAsync_GateOff_ExcludesPerToolheadComponents()
    {
        EfMaintenanceLogRepository repo = NewRepo();

        var lifespanOff = await repo.GetComponentLifespanAsync(includeToolheadScope: false, CancellationToken.None);
        var lifespanOn = await repo.GetComponentLifespanAsync(includeToolheadScope: true, CancellationToken.None);

        lifespanOff.Select(l => l.Component).Should().NotContain("Hotend");
        lifespanOn.Select(l => l.Component).Should().Contain("Hotend");
    }
}
