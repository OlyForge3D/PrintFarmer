using System.Linq;
using System.Threading.Tasks;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Dtos.DataManagement;
using Farm.Infrastructure.Services.DataManagement;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Farm.Infrastructure.Tests.DataManagement;

/// <summary>
/// Covers issue #2328: <c>SeedAllAsync</c> and its downstream <c>DataSeedService</c> methods
/// used to issue one existence-check query per catalog row (~413 sequential queries measured
/// at boot, ~38% of warm time-to-ready). Each loop now preloads its existing rows once via a
/// single query and does in-memory lookups instead.
/// <para>
/// These tests run against a real SQLite relational provider (not <c>UseInMemoryDatabase</c>) so
/// the unique indexes on <see cref="Manufacturer"/>, <see cref="FilamentType"/>, and
/// <see cref="PrinterModel"/> are actually enforced — the in-memory provider silently ignores
/// them, which would hide a regression in the batched preload/lookup rewrite. The catalog
/// includes two manufacturers that each define a printer model with the identical name, to pin
/// the fix for a real pre-existing bug the rewrite uncovered: printer-model (and related
/// component) uniqueness is scoped to <c>(ManufacturerId, Name)</c>, not <c>Name</c> alone.
/// </para>
/// <para>
/// The tests exercise the full <c>SeedAllAsync</c> pipeline — manufacturers, filament types,
/// component models (hotends/extruders/toolheads/nozzles), printer models (with aliases,
/// filament-type associations, and toolhead assignments), and maintenance tasks/components/plans
/// — twice in a row against the same database, to pin two properties of the batched rewrite:
/// (1) it still seeds every catalog row from YAML, and (2) it remains idempotent on a second
/// boot — no duplicate rows and no lost associations.
/// </para>
/// </summary>
public sealed class DataSeedServiceIdempotencyTests : IAsyncLifetime, IDisposable
{
    private SqliteConnection _connection = null!;
    private AppDbContext _context = null!;
    private readonly Mock<IYamlSeedDataReader> _reader = new();
    private readonly Mock<ILogger<DataSeedService>> _logger = new();
    private DataSeedService _service = null!;

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection($"Data Source=file:seed-idempotency-{Guid.NewGuid()}?mode=memory&cache=shared");
        await _connection.OpenAsync();
        await EnableSqliteForeignKeysAsync(_connection);

        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;
        _context = new AppDbContext(options);
        _ = await _context.Database.EnsureCreatedAsync();

        _ = _reader.Setup(r => r.ReadManufacturersAsync()).ReturnsAsync(
        [
            new ManufacturerSeedDto { Name = "Diamondback" },
            new ManufacturerSeedDto { Name = "Copperhead" },
        ]);

        _ = _reader.Setup(r => r.ReadFilamentTypesAsync()).ReturnsAsync(
        [
            new FilamentTypeSeedDto { Name = "PLA", DefaultHotendTemp = 200, DefaultBedTemp = 60 },
        ]);

        _ = _reader.Setup(r => r.ReadHotendsAsync()).ReturnsAsync(
        [
            new HotendModelSeedDto { Name = "V6", Manufacturer = "Diamondback", MaxTemp = 260 },
            new HotendModelSeedDto { Name = "V6", Manufacturer = "Copperhead", MaxTemp = 280 },
        ]);

        _ = _reader.Setup(r => r.ReadExtrudersAsync()).ReturnsAsync(
        [
            new ExtruderModelSeedDto { Name = "Titan", Manufacturer = "Diamondback" },
            new ExtruderModelSeedDto { Name = "Titan", Manufacturer = "Copperhead" },
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
            new ToolheadModelSeedDto
            {
                Name = "Primary Toolhead",
                Manufacturer = "Copperhead",
                DefaultHotend = "V6",
                DefaultExtruder = "Titan",
                DefaultNozzle = "Volcano Nozzle",
            },
        ]);

        _ = _reader.Setup(r => r.ReadNozzlesAsync()).ReturnsAsync(
        [
            new NozzleModelSeedDto { Name = "Volcano Nozzle", Manufacturer = "Diamondback", NozzleType = "Brass" },
            new NozzleModelSeedDto { Name = "Volcano Nozzle", Manufacturer = "Copperhead", NozzleType = "Brass" },
        ]);

        // Two manufacturers deliberately share the printer-model name "Rattler X1" — printer
        // model uniqueness is scoped to (ManufacturerId, Name), not Name alone, and a preload
        // dictionary keyed only on Name would silently merge these two distinct models.
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
            new PrinterModelSeedDto
            {
                Name = "Rattler X1",
                Manufacturer = "Copperhead",
                BuildVolume = new BuildVolumeDto { X = 300, Y = 300, Z = 300 },
                SupportedMaterials = ["PLA"],
                Aliases = [new SlicerAliasDto { SlicerType = "OrcaSlicer", SlicerModelName = "Copperhead Rattler X1" }],
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

    public async Task DisposeAsync()
    {
        await _context.DisposeAsync();
        await _connection.DisposeAsync();
    }

    public void Dispose()
    {
        _context?.Dispose();
        _connection?.Dispose();
    }

    private static async Task EnableSqliteForeignKeysAsync(System.Data.Common.DbConnection connection)
    {
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync();
        }

        using System.Data.Common.DbCommand cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA foreign_keys = ON;";
        _ = await cmd.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task SeedAllAsync_PopulatesEveryCatalogRowFromYaml()
    {
        await _service.SeedAllAsync();

        (await _context.Manufacturers.CountAsync()).Should().Be(2);
        (await _context.FilamentTypes.CountAsync()).Should().Be(1);
        (await _context.HotendModelDefinitions.CountAsync()).Should().Be(2, "each manufacturer's identically-named hotend is a distinct row");
        (await _context.ExtruderModelDefinitions.CountAsync()).Should().Be(2, "each manufacturer's identically-named extruder is a distinct row");
        (await _context.ToolheadModelDefinitions.CountAsync()).Should().Be(2, "each manufacturer's identically-named toolhead is a distinct row");
        (await _context.NozzleModelDefinitions.CountAsync()).Should().Be(2, "each manufacturer's identically-named nozzle is a distinct row");
        (await _context.PrinterModels.CountAsync()).Should().Be(2, "each manufacturer's identically-named printer model is a distinct row");
        (await _context.PrinterModelAliases.CountAsync()).Should().Be(2);
        (await _context.PrinterModelToolheads.CountAsync()).Should().Be(2);
        (await _context.BedTypes.CountAsync()).Should().BeGreaterThan(0, "the fixed bed type catalog must be seeded");
        (await _context.NozzleMaterials.CountAsync()).Should().BeGreaterThan(0, "the built-in nozzle material catalog must be seeded");
        (await _context.MaintenanceTasks.CountAsync()).Should().Be(1);
        (await _context.MaintenanceComponents.CountAsync()).Should().Be(1);
        (await _context.MaintenancePlans.CountAsync()).Should().Be(1);

        List<PrinterModel> models = await _context.PrinterModels
            .Include(pm => pm.Manufacturer)
            .Include(pm => pm.SupportedFilamentTypes)
            .Include(pm => pm.Toolheads)
            .Include(pm => pm.Aliases)
            .ToListAsync();

        PrinterModel diamondbackModel = models.Single(m => m.Manufacturer!.Name == "Diamondback");
        diamondbackModel.SupportedFilamentTypes.Should().ContainSingle(ft => ft.Name == "PLA");
        diamondbackModel.Toolheads.Should().ContainSingle();
        diamondbackModel.Aliases.Should().ContainSingle(a => a.SlicerModelName == "Diamondback Rattler X1");

        PrinterModel copperheadModel = models.Single(m => m.Manufacturer!.Name == "Copperhead");
        copperheadModel.SupportedFilamentTypes.Should().ContainSingle(ft => ft.Name == "PLA");
        copperheadModel.Toolheads.Should().ContainSingle();
        copperheadModel.Aliases.Should().ContainSingle(a => a.SlicerModelName == "Copperhead Rattler X1");

        PrinterModelToolhead toolhead = diamondbackModel.Toolheads.Single();
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
        // seeding must be safe to run on every application start). On a real relational
        // provider, any regression that mis-scopes a preload dictionary key (e.g. keying
        // printer models by Name alone instead of (ManufacturerId, Name)) would either throw
        // on the unique index or silently update the wrong row — both are caught below.
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

        List<PrinterModel> models = await _context.PrinterModels
            .Include(pm => pm.Manufacturer)
            .Include(pm => pm.SupportedFilamentTypes)
            .Include(pm => pm.Aliases)
            .ToListAsync();

        PrinterModel diamondbackModel = models.Single(m => m.Manufacturer!.Name == "Diamondback");
        diamondbackModel.SupportedFilamentTypes.Should().ContainSingle("the filament-type association must survive a second seed pass without duplication");
        diamondbackModel.Aliases.Should().ContainSingle("the alias must survive a second seed pass without duplication");

        PrinterModel copperheadModel = models.Single(m => m.Manufacturer!.Name == "Copperhead");
        copperheadModel.Aliases.Should().ContainSingle(a => a.SlicerModelName == "Copperhead Rattler X1", "the two manufacturers' identically-named models must remain independent across re-seeds");
    }

    [Fact]
    public async Task SeedMaintenanceTasksAsync_TolerantOfPreExistingDuplicateNamedRows()
    {
        // MaintenanceTask.TaskName has no unique DB constraint -- tasks are user-writable with
        // no uniqueness check, so two rows sharing a name are legitimate, pre-existing data.
        // BuildFirstWinsDictionary must tolerate this exactly as the original per-row
        // FirstOrDefaultAsync silently did; a naive ToDictionaryAsync throws ArgumentException
        // on the first duplicate key instead, which would crash seeding on boot.
        _context.MaintenanceTasks.Add(new MaintenanceTask { Id = Guid.NewGuid(), TaskName = "Lubricate Rails", Category = "Mechanical" });
        _context.MaintenanceTasks.Add(new MaintenanceTask { Id = Guid.NewGuid(), TaskName = "Lubricate Rails", Category = "Mechanical" });
        await _context.SaveChangesAsync();

        Func<Task> act = async () => await _service.SeedMaintenanceTasksAsync();

        await act.Should().NotThrowAsync("duplicate-named maintenance tasks are legitimate pre-existing data and must not crash seeding");
        (await _context.MaintenanceTasks.CountAsync()).Should().Be(2, "seeding must resolve the YAML row against one of the existing duplicates rather than adding a third row");
    }

    [Fact]
    public async Task SeedComponentModelsAsync_TolerantOfPreExistingDuplicateNamedHotendsWithinAManufacturer()
    {
        // HotendModelDefinition has no unique DB constraint on (ManufacturerId, Name) -- same
        // rationale as above, but for a manufacturer-scoped component definition rather than a
        // globally-keyed maintenance catalog row.
        Manufacturer manufacturer = new() { Id = Guid.NewGuid(), Name = "Diamondback" };
        _context.Manufacturers.Add(manufacturer);
        _context.HotendModelDefinitions.Add(new HotendModelDefinition { Id = Guid.NewGuid(), Name = "V6", ManufacturerId = manufacturer.Id, MaxTemp = 260 });
        _context.HotendModelDefinitions.Add(new HotendModelDefinition { Id = Guid.NewGuid(), Name = "V6", ManufacturerId = manufacturer.Id, MaxTemp = 260 });
        await _context.SaveChangesAsync();

        // SeedComponentModelsAsync also seeds nozzles, which resolve a NozzleMaterial FK;
        // built-in materials must exist first (SeedAllAsync always runs this before component
        // models, so this mirrors production ordering rather than being test-only setup).
        await _service.SeedNozzleMaterialsAsync();

        // ResolveToolheadDefaultComponentsFromYamlAsync (run as part of SeedComponentModelsAsync)
        // has its own pre-existing, unrelated composite-key ToDictionaryAsync over hotend name +
        // manufacturer name that is untouched by this PR (confirmed via `git diff` against the
        // pre-batching baseline) and is out of scope here (same class of issue flagged
        // non-blocking during review for FilamentType). Suppress toolheads for this test so it
        // isolates SeedHotendsAsync's own duplicate-tolerance rather than tripping that
        // unrelated, already-existing bug.
        _ = _reader.Setup(r => r.ReadToolheadsAsync()).ReturnsAsync([]);

        Func<Task> act = async () => await _service.SeedComponentModelsAsync();

        await act.Should().NotThrowAsync("duplicate-named hotend definitions within a manufacturer are legitimate pre-existing data and must not crash seeding");
        (await _context.HotendModelDefinitions.CountAsync(h => h.ManufacturerId == manufacturer.Id && h.Name == "V6")).Should().Be(2, "seeding must resolve the YAML row against one of the existing duplicates rather than adding a third row");
    }
}
