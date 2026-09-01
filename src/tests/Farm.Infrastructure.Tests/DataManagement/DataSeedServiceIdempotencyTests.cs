using System.Linq;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Dtos.DataManagement;
using Farm.Infrastructure.Services.DataManagement;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Farm.Infrastructure.Tests.DataManagement;

/// <summary>
/// Covers issue #2328: <c>SeedAllAsync</c> and its downstream <c>DataSeedService</c> methods
/// used to issue one existence-check query per catalog row (~413 sequential queries measured
/// at boot, ~38% of warm time-to-ready). Each loop now preloads its existing rows once via a
/// single <c>ToDictionaryAsync</c>/<c>ToListAsync</c> call and does in-memory lookups instead.
/// <para>
/// These tests exercise the full <c>SeedAllAsync</c> pipeline — manufacturers, filament types,
/// component models (hotends/extruders/toolheads/nozzles), printer models (with aliases,
/// filament-type associations, and toolhead assignments), and maintenance tasks/components/plans
/// — twice in a row against the same in-memory database, to pin two properties of the batched
/// rewrite: (1) it still seeds every catalog row from YAML, and (2) it remains idempotent on a
/// second boot — no duplicate rows and no lost associations.
/// </para>
/// </summary>
public sealed class DataSeedServiceIdempotencyTests : IDisposable
{
    private readonly AppDbContext _context;
    private readonly Mock<IYamlSeedDataReader> _reader = new();
    private readonly Mock<ILogger<DataSeedService>> _logger = new();
    private readonly DataSeedService _service;

    public DataSeedServiceIdempotencyTests()
    {
        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName: $"SeedIdempotencyTestDb_{Guid.NewGuid()}")
            .Options;
        _context = new AppDbContext(options);

        _ = _reader.Setup(r => r.ReadManufacturersAsync()).ReturnsAsync(
        [
            new ManufacturerSeedDto { Name = "Diamondback" },
        ]);

        _ = _reader.Setup(r => r.ReadFilamentTypesAsync()).ReturnsAsync(
        [
            new FilamentTypeSeedDto { Name = "PLA", DefaultHotendTemp = 200, DefaultBedTemp = 60 },
        ]);

        _ = _reader.Setup(r => r.ReadHotendsAsync()).ReturnsAsync(
        [
            new HotendModelSeedDto { Name = "V6", Manufacturer = "Diamondback", MaxTemp = 260 },
        ]);

        _ = _reader.Setup(r => r.ReadExtrudersAsync()).ReturnsAsync(
        [
            new ExtruderModelSeedDto { Name = "Titan", Manufacturer = "Diamondback" },
        ]);

        _ = _reader.Setup(r => r.ReadToolheadsAsync()).ReturnsAsync(
        [
            new ToolheadModelSeedDto
            {
                Name = "Primary Toolhead",
                Manufacturer = "Diamondback",
                DefaultHotend = "V6",
                DefaultExtruder = "Titan",
                DefaultNozzle = "Volcano Nozzle",
            },
        ]);

        _ = _reader.Setup(r => r.ReadNozzlesAsync()).ReturnsAsync(
        [
            new NozzleModelSeedDto { Name = "Volcano Nozzle", Manufacturer = "Diamondback", NozzleType = "Brass" },
        ]);

        _ = _reader.Setup(r => r.ReadPrinterModelsAsync()).ReturnsAsync(
        [
            new PrinterModelSeedDto
            {
                Name = "Rattler X1",
                Manufacturer = "Diamondback",
                BuildVolume = new BuildVolumeDto { X = 250, Y = 250, Z = 250 },
                SupportedMaterials = ["PLA"],
                Aliases = [new SlicerAliasDto { SlicerType = "OrcaSlicer", SlicerModelName = "Diamondback Rattler X1" }],
                Toolheads =
                [
                    new ToolheadAssignmentDto { Name = "Primary", Toolhead = "Primary Toolhead" },
                ],
            },
        ]);

        _ = _reader.Setup(r => r.ReadMaintenanceTasksAsync()).ReturnsAsync(
        [
            new MaintenanceTaskSeedDto { TaskName = "Lubricate Rails", Category = "Mechanical" },
        ]);

        _ = _reader.Setup(r => r.ReadMaintenanceComponentsAsync()).ReturnsAsync(
        [
            new MaintenanceComponentSeedDto { Name = "PTFE Tube", Category = "Extrusion" },
        ]);

        _ = _reader.Setup(r => r.ReadMaintenancePlansAsync()).ReturnsAsync(
        [
            new MaintenancePlanSeedDto { Name = "Standard Care", Tasks = ["Lubricate Rails"] },
        ]);

        _service = new DataSeedService(_context, _reader.Object, _logger.Object);
    }

    public void Dispose() => _context.Dispose();

    [Fact]
    public async Task SeedAllAsync_PopulatesEveryCatalogRowFromYaml()
    {
        await _service.SeedAllAsync();

        (await _context.Manufacturers.CountAsync()).Should().Be(1);
        (await _context.FilamentTypes.CountAsync()).Should().Be(1);
        (await _context.HotendModelDefinitions.CountAsync()).Should().Be(1);
        (await _context.ExtruderModelDefinitions.CountAsync()).Should().Be(1);
        (await _context.ToolheadModelDefinitions.CountAsync()).Should().Be(1);
        (await _context.NozzleModelDefinitions.CountAsync()).Should().Be(1);
        (await _context.PrinterModels.CountAsync()).Should().Be(1);
        (await _context.PrinterModelAliases.CountAsync()).Should().Be(1);
        (await _context.PrinterModelToolheads.CountAsync()).Should().Be(1);
        (await _context.MaintenanceTasks.CountAsync()).Should().Be(1);
        (await _context.MaintenanceComponents.CountAsync()).Should().Be(1);
        (await _context.MaintenancePlans.CountAsync()).Should().Be(1);

        PrinterModel model = await _context.PrinterModels
            .Include(pm => pm.SupportedFilamentTypes)
            .Include(pm => pm.Toolheads)
            .Include(pm => pm.Aliases)
            .SingleAsync();
        model.SupportedFilamentTypes.Should().ContainSingle(ft => ft.Name == "PLA");
        model.Toolheads.Should().ContainSingle();
        model.Aliases.Should().ContainSingle(a => a.SlicerModelName == "Diamondback Rattler X1");

        PrinterModelToolhead toolhead = model.Toolheads.Single();
        toolhead.HotendModelId.Should().NotBeNull("the toolhead assignment should resolve the named hotend");
        toolhead.ExtruderModelId.Should().NotBeNull("the toolhead assignment should resolve the named extruder");

        MaintenancePlan plan = await _context.MaintenancePlans.Include(p => p.PlanTasks).SingleAsync();
        plan.PlanTasks.Should().ContainSingle();
    }

    [Fact]
    public async Task SeedAllAsync_RunTwice_IsIdempotent()
    {
        // First boot.
        await _service.SeedAllAsync();

        int manufacturerCount = await _context.Manufacturers.CountAsync();
        int filamentTypeCount = await _context.FilamentTypes.CountAsync();
        int hotendCount = await _context.HotendModelDefinitions.CountAsync();
        int extruderCount = await _context.ExtruderModelDefinitions.CountAsync();
        int toolheadModelCount = await _context.ToolheadModelDefinitions.CountAsync();
        int nozzleCount = await _context.NozzleModelDefinitions.CountAsync();
        int printerModelCount = await _context.PrinterModels.CountAsync();
        int aliasCount = await _context.PrinterModelAliases.CountAsync();
        int printerModelToolheadCount = await _context.PrinterModelToolheads.CountAsync();
        int bedTypeCount = await _context.BedTypes.CountAsync();
        int nozzleMaterialCount = await _context.NozzleMaterials.CountAsync();
        int maintenanceTaskCount = await _context.MaintenanceTasks.CountAsync();
        int maintenanceComponentCount = await _context.MaintenanceComponents.CountAsync();
        int maintenancePlanCount = await _context.MaintenancePlans.CountAsync();
        int planTaskCount = await _context.MaintenancePlans.Include(p => p.PlanTasks).SelectMany(p => p.PlanTasks).CountAsync();

        // Simulate a second boot against the same database (the scenario this fix targets:
        // seeding must be safe to run on every application start).
        await _service.SeedAllAsync();

        (await _context.Manufacturers.CountAsync()).Should().Be(manufacturerCount, "re-seeding must not duplicate manufacturers");
        (await _context.FilamentTypes.CountAsync()).Should().Be(filamentTypeCount, "re-seeding must not duplicate filament types");
        (await _context.HotendModelDefinitions.CountAsync()).Should().Be(hotendCount, "re-seeding must not duplicate hotends");
        (await _context.ExtruderModelDefinitions.CountAsync()).Should().Be(extruderCount, "re-seeding must not duplicate extruders");
        (await _context.ToolheadModelDefinitions.CountAsync()).Should().Be(toolheadModelCount, "re-seeding must not duplicate toolhead models");
        (await _context.NozzleModelDefinitions.CountAsync()).Should().Be(nozzleCount, "re-seeding must not duplicate nozzles");
        (await _context.PrinterModels.CountAsync()).Should().Be(printerModelCount, "re-seeding must not duplicate printer models");
        (await _context.PrinterModelAliases.CountAsync()).Should().Be(aliasCount, "re-seeding must not duplicate printer model aliases");
        (await _context.PrinterModelToolheads.CountAsync()).Should().Be(printerModelToolheadCount, "re-seeding must not duplicate printer model toolhead assignments");
        (await _context.BedTypes.CountAsync()).Should().Be(bedTypeCount, "re-seeding must not duplicate the fixed bed type catalog");
        (await _context.NozzleMaterials.CountAsync()).Should().Be(nozzleMaterialCount, "re-seeding must not duplicate the built-in nozzle material catalog");
        (await _context.MaintenanceTasks.CountAsync()).Should().Be(maintenanceTaskCount, "re-seeding must not duplicate maintenance tasks");
        (await _context.MaintenanceComponents.CountAsync()).Should().Be(maintenanceComponentCount, "re-seeding must not duplicate maintenance components");
        (await _context.MaintenancePlans.CountAsync()).Should().Be(maintenancePlanCount, "re-seeding must not duplicate maintenance plans");
        (await _context.MaintenancePlans.Include(p => p.PlanTasks).SelectMany(p => p.PlanTasks).CountAsync())
            .Should().Be(planTaskCount, "re-seeding must not duplicate plan-task associations");

        PrinterModel model = await _context.PrinterModels
            .Include(pm => pm.SupportedFilamentTypes)
            .Include(pm => pm.Aliases)
            .SingleAsync();
        model.SupportedFilamentTypes.Should().ContainSingle("the filament-type association must survive a second seed pass without duplication");
        model.Aliases.Should().ContainSingle("the alias must survive a second seed pass without duplication");
    }
}
