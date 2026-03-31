using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Discovery;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Printers;
using Farm.Web.Api.Tests.TestInfrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Farm.Web.Api.Tests.Services.Printers;

/// <summary>
/// Integration tests for Phase 3: automatic MmuGate virtual toolhead creation
/// when a printer is created with MultiMaterial=true or toggled to MultiMaterial.
/// Tests the CreateMmuVirtualToolheads path inside PrintersService.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public class MmuGateAutoCreationTests : IAsyncLifetime
{
    private readonly CustomWebApplicationFactory _factory;
    private AsyncServiceScope _scope;
    private IPrintersService _printersService = null!;
    private AppDbContext _dbContext = null!;

    public MmuGateAutoCreationTests()
    {
        _factory = CustomWebApplicationFactory.CreateWithIsolatedDatabase();
    }

    public async Task InitializeAsync()
    {
        _scope = _factory.Services.CreateAsyncScope();
        _dbContext = _scope.ServiceProvider.GetRequiredService<AppDbContext>();
        _printersService = _scope.ServiceProvider.GetRequiredService<IPrintersService>();
        await Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _scope.DisposeAsync();
        _factory?.Dispose();
    }

    private async Task<(Guid ManufacturerId, Guid ModelId)> SeedCatalog(string prefix = "Test")
    {
        await using AsyncServiceScope seedScope = _factory.Services.CreateAsyncScope();
        AppDbContext seedDb = seedScope.ServiceProvider.GetRequiredService<AppDbContext>();

        Guid manufacturerId = Guid.NewGuid();
        Guid modelId = Guid.NewGuid();

        seedDb.Manufacturers.Add(new Manufacturer { Id = manufacturerId, Name = $"{prefix} Mfg" });
        seedDb.PrinterModels.Add(new PrinterModel { Id = modelId, ManufacturerId = manufacturerId, Name = $"{prefix} Model" });
        await seedDb.SaveChangesAsync();

        return (manufacturerId, modelId);
    }

    private static int _portCounter;

    private CreatePrinterFromDiscoveryDto CreatePrinterDto(
        string name,
        Guid manufacturerId,
        Guid modelId,
        bool multiMaterial,
        PrinterBackend backend = PrinterBackend.Moonraker)
    {
        int port = Interlocked.Increment(ref _portCounter);
        return new CreatePrinterFromDiscoveryDto
        {
            Name = name,
            ServerUrl = $"http://192.168.1.{10 + (port % 240)}",
            BackendPort = 7125,
            Backend = backend,
            ManufacturerId = manufacturerId,
            ModelId = modelId,
            IsEnabled = true
        };
    }

    // ------- CreatePrinter with MultiMaterial=true auto-creates MmuGate toolheads -------

    [Fact]
    public async Task CreatePrinter_MultiMaterialTrue_CreatesThreeMmuGateToolheads()
    {
        (Guid mfgId, Guid modelId) = await SeedCatalog("MMU");

        // Seed a model template with MultiMaterial=true so CreatePrinterFromDto picks it up
        await using (AsyncServiceScope seedScope = _factory.Services.CreateAsyncScope())
        {
            AppDbContext seedDb = seedScope.ServiceProvider.GetRequiredService<AppDbContext>();
            PrinterModel? model = await seedDb.PrinterModels.FindAsync(modelId);
            model!.MultiMaterial = true;
            await seedDb.SaveChangesAsync();
        }

        CreatePrinterFromDiscoveryDto dto = CreatePrinterDto("MMU Printer", mfgId, modelId, true);
        PrinterDto created = await _printersService.CreatePrinterFromDtoAsync(dto, CancellationToken.None);

        List<Toolhead> toolheads = await _dbContext.Toolheads
            .Where(t => t.PrinterId == created.Id)
            .OrderBy(t => t.Index)
            .ToListAsync();

        // 1 physical (T0) + 3 MmuGate (T1, T2, T3) = 4 total
        toolheads.Should().HaveCount(4);
        toolheads[0].ToolheadType.Should().Be(ToolheadType.Physical);
        toolheads[0].Index.Should().Be(0);
        toolheads[1].ToolheadType.Should().Be(ToolheadType.MmuGate);
        toolheads[1].Index.Should().Be(1);
        toolheads[2].ToolheadType.Should().Be(ToolheadType.MmuGate);
        toolheads[2].Index.Should().Be(2);
        toolheads[3].ToolheadType.Should().Be(ToolheadType.MmuGate);
        toolheads[3].Index.Should().Be(3);
    }

    [Fact]
    public async Task CreatePrinter_MultiMaterialTrue_MmuGatesAreNotPrimary()
    {
        (Guid mfgId, Guid modelId) = await SeedCatalog("NotPrimary");

        await using (AsyncServiceScope seedScope = _factory.Services.CreateAsyncScope())
        {
            AppDbContext seedDb = seedScope.ServiceProvider.GetRequiredService<AppDbContext>();
            PrinterModel? model = await seedDb.PrinterModels.FindAsync(modelId);
            model!.MultiMaterial = true;
            await seedDb.SaveChangesAsync();
        }

        CreatePrinterFromDiscoveryDto dto = CreatePrinterDto("NotPrimary Printer", mfgId, modelId, true);
        PrinterDto created = await _printersService.CreatePrinterFromDtoAsync(dto, CancellationToken.None);

        List<Toolhead> mmuGates = await _dbContext.Toolheads
            .Where(t => t.PrinterId == created.Id && t.ToolheadType == ToolheadType.MmuGate)
            .ToListAsync();

        mmuGates.Should().HaveCount(3);
        mmuGates.Should().AllSatisfy(g => g.IsPrimary.Should().BeFalse("MMU gates are virtual — only the physical T0 is primary"));
    }

    [Fact]
    public async Task CreatePrinter_MultiMaterialTrue_MmuGatesCopyPrimaryToolheadComponents()
    {
        (Guid mfgId, Guid modelId) = await SeedCatalog("CopyComponents");

        // Seed component models that the primary toolhead will reference
        Guid hotendId = Guid.NewGuid();
        Guid extruderId = Guid.NewGuid();

        await using (AsyncServiceScope seedScope = _factory.Services.CreateAsyncScope())
        {
            AppDbContext seedDb = seedScope.ServiceProvider.GetRequiredService<AppDbContext>();
            PrinterModel? model = await seedDb.PrinterModels.FindAsync(modelId);
            model!.MultiMaterial = true;

            // Seed hotend and extruder model definitions
            seedDb.HotendModelDefinitions.Add(new HotendModelDefinition { Id = hotendId, ManufacturerId = mfgId, Name = "Test Hotend" });
            seedDb.ExtruderModelDefinitions.Add(new ExtruderModelDefinition { Id = extruderId, ManufacturerId = mfgId, Name = "Test Extruder" });

            // Add a toolhead template to the model so the physical toolhead gets components
            model.Toolheads.Add(new PrinterModelToolhead
            {
                Id = Guid.NewGuid(),
                PrinterModelId = modelId,
                Name = "Primary",
                Index = 0,
                IsPrimary = true,
                HotendModelId = hotendId,
                ExtruderModelId = extruderId
            });

            await seedDb.SaveChangesAsync();
        }

        CreatePrinterFromDiscoveryDto dto = CreatePrinterDto("ComponentCopy Printer", mfgId, modelId, true);
        PrinterDto created = await _printersService.CreatePrinterFromDtoAsync(dto, CancellationToken.None);

        List<Toolhead> mmuGates = await _dbContext.Toolheads
            .Where(t => t.PrinterId == created.Id && t.ToolheadType == ToolheadType.MmuGate)
            .ToListAsync();

        mmuGates.Should().HaveCount(3);
        mmuGates.Should().AllSatisfy(g =>
        {
            g.HotendModelId.Should().Be(hotendId, "MmuGate should copy HotendModelId from primary");
            g.ExtruderModelId.Should().Be(extruderId, "MmuGate should copy ExtruderModelId from primary");
        });
    }

    [Fact]
    public async Task CreatePrinter_MultiMaterialFalse_NoMmuGatesCreated()
    {
        (Guid mfgId, Guid modelId) = await SeedCatalog("NoMMU");

        CreatePrinterFromDiscoveryDto dto = CreatePrinterDto("Single Extruder", mfgId, modelId, false);
        PrinterDto created = await _printersService.CreatePrinterFromDtoAsync(dto, CancellationToken.None);

        List<Toolhead> toolheads = await _dbContext.Toolheads
            .Where(t => t.PrinterId == created.Id)
            .ToListAsync();

        // Only the physical T0 — no virtual gates
        toolheads.Should().HaveCount(1);
        toolheads.Should().AllSatisfy(t => t.ToolheadType.Should().Be(ToolheadType.Physical));
    }

    // ------- Idempotency: re-toggle MultiMaterial=true doesn't create duplicates -------

    [Fact]
    public async Task EnsureMmuToolheads_AlreadyHasGates_NoDuplicatesCreated()
    {
        // Seed a printer with MultiMaterial=true that already has MMU gates
        await using AsyncServiceScope seedScope = _factory.Services.CreateAsyncScope();
        AppDbContext seedDb = seedScope.ServiceProvider.GetRequiredService<AppDbContext>();

        Guid mfgId = Guid.NewGuid();
        Guid modelId = Guid.NewGuid();
        seedDb.Manufacturers.Add(new Manufacturer { Id = mfgId, Name = "Idempotent Mfg" });
        seedDb.PrinterModels.Add(new PrinterModel { Id = modelId, ManufacturerId = mfgId, Name = "Idempotent Model" });

        var printer = new Printer
        {
            Id = Guid.NewGuid(),
            Name = "Idempotent MMU Printer",
            ServerUrl = "http://192.168.1.99",
            BackendPort = 7125,
            Backend = (int)PrinterBackend.Moonraker,
            MultiMaterial = true,
            ManufacturerId = mfgId,
            ModelId = modelId
        };

        // Add physical T0
        printer.Toolheads.Add(new Toolhead
        {
            Id = Guid.NewGuid(),
            PrinterId = printer.Id,
            Name = "Extruder",
            Index = 0,
            ToolheadType = ToolheadType.Physical,
            IsPrimary = true,
            UpdatedAt = DateTime.UtcNow
        });

        // Pre-create MMU gates
        for (int i = 1; i < 4; i++)
        {
            printer.Toolheads.Add(new Toolhead
            {
                Id = Guid.NewGuid(),
                PrinterId = printer.Id,
                Name = $"Gate {i}",
                Index = i,
                ToolheadType = ToolheadType.MmuGate,
                IsPrimary = false,
                UpdatedAt = DateTime.UtcNow
            });
        }

        seedDb.Printers.Add(printer);
        await seedDb.SaveChangesAsync();

        // Now call EnsureMmuToolheads again — should be idempotent
        CommandResult result = await _printersService.EnsureMmuToolheadsAsync(printer.Id, CancellationToken.None);

        result.Success.Should().BeTrue();

        int totalToolheads = await _dbContext.Toolheads.CountAsync(t => t.PrinterId == printer.Id);
        totalToolheads.Should().Be(4, "should not create duplicate gates");
    }

    // ------- Toggle MultiMaterial off removes MmuGate toolheads -------

    [Fact]
    public async Task SyncMmuToolheadsOnEntity_ToggleOff_RemovesMmuGateToolheads()
    {
        // Seed a printer with MultiMaterial=true and MMU gates
        await using AsyncServiceScope seedScope = _factory.Services.CreateAsyncScope();
        AppDbContext seedDb = seedScope.ServiceProvider.GetRequiredService<AppDbContext>();

        Guid mfgId = Guid.NewGuid();
        Guid modelId = Guid.NewGuid();
        seedDb.Manufacturers.Add(new Manufacturer { Id = mfgId, Name = "Toggle Mfg" });
        seedDb.PrinterModels.Add(new PrinterModel { Id = modelId, ManufacturerId = mfgId, Name = "Toggle Model" });

        var printer = new Printer
        {
            Id = Guid.NewGuid(),
            Name = "Toggle MMU Printer",
            ServerUrl = "http://192.168.1.98",
            BackendPort = 7125,
            Backend = (int)PrinterBackend.Moonraker,
            MultiMaterial = true,
            ManufacturerId = mfgId,
            ModelId = modelId
        };

        printer.Toolheads.Add(new Toolhead
        {
            Id = Guid.NewGuid(),
            PrinterId = printer.Id,
            Name = "Extruder",
            Index = 0,
            ToolheadType = ToolheadType.Physical,
            IsPrimary = true,
            UpdatedAt = DateTime.UtcNow
        });

        for (int i = 1; i < 4; i++)
        {
            printer.Toolheads.Add(new Toolhead
            {
                Id = Guid.NewGuid(),
                PrinterId = printer.Id,
                Name = $"Gate {i}",
                Index = i,
                ToolheadType = ToolheadType.MmuGate,
                IsPrimary = false,
                UpdatedAt = DateTime.UtcNow
            });
        }

        seedDb.Printers.Add(printer);
        await seedDb.SaveChangesAsync();

        // Reload printer with toolheads in the test scope
        Printer reloaded = await _dbContext.Printers
            .Include(p => p.Toolheads)
            .FirstAsync(p => p.Id == printer.Id);

        reloaded.Toolheads.Should().HaveCount(4, "should have T0 + 3 MMU gates before toggle");

        // Toggle MultiMaterial off and sync
        reloaded.MultiMaterial = false;
        _printersService.SyncMmuToolheadsOnEntity(reloaded, wasMultiMaterial: true);
        await _dbContext.SaveChangesAsync();

        // Verify gates removed
        List<Toolhead> afterToolheads = await _dbContext.Toolheads
            .Where(t => t.PrinterId == printer.Id)
            .ToListAsync();

        afterToolheads.Should().HaveCount(1, "only the physical T0 should remain after toggling MultiMaterial off");
        afterToolheads[0].ToolheadType.Should().Be(ToolheadType.Physical);
    }

    [Fact]
    public async Task SyncMmuToolheadsOnEntity_ToggleOn_CreatesMmuGateToolheads()
    {
        // Seed a non-MMU printer
        await using AsyncServiceScope seedScope = _factory.Services.CreateAsyncScope();
        AppDbContext seedDb = seedScope.ServiceProvider.GetRequiredService<AppDbContext>();

        Guid mfgId = Guid.NewGuid();
        Guid modelId = Guid.NewGuid();
        seedDb.Manufacturers.Add(new Manufacturer { Id = mfgId, Name = "ToggleOn Mfg" });
        seedDb.PrinterModels.Add(new PrinterModel { Id = modelId, ManufacturerId = mfgId, Name = "ToggleOn Model" });

        var printer = new Printer
        {
            Id = Guid.NewGuid(),
            Name = "ToggleOn Printer",
            ServerUrl = "http://192.168.1.97",
            BackendPort = 7125,
            Backend = (int)PrinterBackend.Moonraker,
            MultiMaterial = false,
            ManufacturerId = mfgId,
            ModelId = modelId
        };

        printer.Toolheads.Add(new Toolhead
        {
            Id = Guid.NewGuid(),
            PrinterId = printer.Id,
            Name = "Extruder",
            Index = 0,
            ToolheadType = ToolheadType.Physical,
            IsPrimary = true,
            UpdatedAt = DateTime.UtcNow
        });

        seedDb.Printers.Add(printer);
        await seedDb.SaveChangesAsync();

        // Reload in test scope
        Printer reloaded = await _dbContext.Printers
            .Include(p => p.Toolheads)
            .FirstAsync(p => p.Id == printer.Id);

        reloaded.Toolheads.Should().HaveCount(1, "only physical T0 before toggle");

        // Toggle MultiMaterial on and sync
        reloaded.MultiMaterial = true;
        _printersService.SyncMmuToolheadsOnEntity(reloaded, wasMultiMaterial: false);
        await _dbContext.SaveChangesAsync();

        List<Toolhead> afterToolheads = await _dbContext.Toolheads
            .Where(t => t.PrinterId == printer.Id)
            .OrderBy(t => t.Index)
            .ToListAsync();

        afterToolheads.Should().HaveCount(4);
        afterToolheads.Count(t => t.ToolheadType == ToolheadType.MmuGate).Should().Be(3);
        afterToolheads.Where(t => t.ToolheadType == ToolheadType.MmuGate)
            .Select(t => t.Index)
            .Should().BeEquivalentTo(new[] { 1, 2, 3 });
    }
}
