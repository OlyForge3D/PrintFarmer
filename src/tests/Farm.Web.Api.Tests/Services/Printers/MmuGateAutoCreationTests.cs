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
            MultiMaterial = multiMaterial,
            IsEnabled = true
        };
    }

    // ------- CreatePrinter with MultiMaterial=true auto-creates MmuGate toolheads -------

    [Fact]
    public async Task CreatePrinter_MultiMaterialTrue_CreatesFourMmuGateToolheads()
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

        // 1 physical (T0) + 4 MmuGate (T1, T2, T3, T4) = 5 total — matches AMS hardware capacity (#302)
        toolheads.Should().HaveCount(5);
        toolheads[0].ToolheadType.Should().Be(ToolheadType.Physical);
        toolheads[0].Index.Should().Be(0);
        for (int i = 1; i <= 4; i++)
        {
            toolheads[i].ToolheadType.Should().Be(ToolheadType.MmuGate);
            toolheads[i].Index.Should().Be(i);
        }
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

        mmuGates.Should().HaveCount(4);
        mmuGates.Should().AllSatisfy(g => g.IsPrimary.Should().BeFalse("MMU gates are virtual — only the physical T0 is primary"));
    }

    [Fact]
    public async Task CreatePrinter_MultiMaterialTrue_MmuGatesCopyPrimaryToolheadComponents()
    {
        Guid mfgId = Guid.NewGuid();
        Guid modelId = Guid.NewGuid();
        Guid hotendId = Guid.NewGuid();
        Guid extruderId = Guid.NewGuid();

        // Seed manufacturer, component definitions, and model with toolhead template in one scope
        await using (AsyncServiceScope seedScope = _factory.Services.CreateAsyncScope())
        {
            AppDbContext seedDb = seedScope.ServiceProvider.GetRequiredService<AppDbContext>();

            seedDb.Manufacturers.Add(new Manufacturer { Id = mfgId, Name = "CopyComponents Mfg" });
            seedDb.HotendModelDefinitions.Add(new HotendModelDefinition { Id = hotendId, ManufacturerId = mfgId, Name = "Test Hotend" });
            seedDb.ExtruderModelDefinitions.Add(new ExtruderModelDefinition { Id = extruderId, ManufacturerId = mfgId, Name = "Test Extruder" });

            var model = new PrinterModel { Id = modelId, ManufacturerId = mfgId, Name = "CopyComponents Model", MultiMaterial = true };
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
            seedDb.PrinterModels.Add(model);

            await seedDb.SaveChangesAsync();
        }

        CreatePrinterFromDiscoveryDto dto = CreatePrinterDto("ComponentCopy Printer", mfgId, modelId, true);
        PrinterDto created = await _printersService.CreatePrinterFromDtoAsync(dto, CancellationToken.None);

        List<Toolhead> mmuGates = await _dbContext.Toolheads
            .Where(t => t.PrinterId == created.Id && t.ToolheadType == ToolheadType.MmuGate)
            .ToListAsync();

        mmuGates.Should().HaveCount(4);
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
        Guid printerId;
        await using (AsyncServiceScope seedScope = _factory.Services.CreateAsyncScope())
        {
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
            printerId = printer.Id;
        }

        // Toggle MultiMaterial on via raw update to bypass RowVersion concurrency on tracked entity
        await _dbContext.Printers
            .Where(p => p.Id == printerId)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.MultiMaterial, true));

        // EnsureMmuToolheadsAsync loads a fresh entity and uses AddToolheads to avoid RowVersion issues
        CommandResult result = await _printersService.EnsureMmuToolheadsAsync(printerId, CancellationToken.None);
        result.Success.Should().BeTrue();

        List<Toolhead> afterToolheads = await _dbContext.Toolheads
            .Where(t => t.PrinterId == printerId)
            .OrderBy(t => t.Index)
            .ToListAsync();

        afterToolheads.Should().HaveCount(5);
        afterToolheads.Count(t => t.ToolheadType == ToolheadType.MmuGate).Should().Be(4);
        afterToolheads.Where(t => t.ToolheadType == ToolheadType.MmuGate)
            .Select(t => t.Index)
            .Should().BeEquivalentTo(new[] { 1, 2, 3, 4 });
    }

    // ------- Gate-count semantics: mmuGateCount equals the number of AMS gates created (#302) -------

    [Theory]
    [InlineData(1, new[] { 1 })]
    [InlineData(4, new[] { 1, 2, 3, 4 })]
    [InlineData(16, new[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16 })]
    public async Task SyncMmuToolheadsOnEntity_ToggleOn_CreatesGateCountGates(int mmuGateCount, int[] expectedGateIndices)
    {
        Guid printerId;
        await using (AsyncServiceScope seedScope = _factory.Services.CreateAsyncScope())
        {
            AppDbContext seedDb = seedScope.ServiceProvider.GetRequiredService<AppDbContext>();

            Guid mfgId = Guid.NewGuid();
            Guid modelId = Guid.NewGuid();
            seedDb.Manufacturers.Add(new Manufacturer { Id = mfgId, Name = $"GateCount{mmuGateCount} Mfg" });
            seedDb.PrinterModels.Add(new PrinterModel { Id = modelId, ManufacturerId = mfgId, Name = $"GateCount{mmuGateCount} Model" });

            var printer = new Printer
            {
                Id = Guid.NewGuid(),
                Name = $"GateCount{mmuGateCount} Printer",
                ServerUrl = $"http://192.168.1.{100 + mmuGateCount}",
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
            printerId = printer.Id;
        }

        Printer reloaded = await _dbContext.Printers
            .Include(p => p.Toolheads)
            .FirstAsync(p => p.Id == printerId);

        reloaded.MultiMaterial = true;
        _printersService.SyncMmuToolheadsOnEntity(reloaded, wasMultiMaterial: false, mmuGateCount: mmuGateCount);
        await _dbContext.SaveChangesAsync();

        List<int> mmuIndices = await _dbContext.Toolheads
            .Where(t => t.PrinterId == printerId && t.ToolheadType == ToolheadType.MmuGate)
            .Select(t => t.Index)
            .OrderBy(i => i)
            .ToListAsync();

        mmuIndices.Should().BeEquivalentTo(expectedGateIndices, $"mmuGateCount={mmuGateCount} should produce that many AMS gates at indices 1..{mmuGateCount}");
    }

    [Fact]
    public async Task SyncMmuToolheadsOnEntity_ToggleOn_GateCountZero_CreatesNoGates()
    {
        Guid printerId;
        await using (AsyncServiceScope seedScope = _factory.Services.CreateAsyncScope())
        {
            AppDbContext seedDb = seedScope.ServiceProvider.GetRequiredService<AppDbContext>();

            Guid mfgId = Guid.NewGuid();
            Guid modelId = Guid.NewGuid();
            seedDb.Manufacturers.Add(new Manufacturer { Id = mfgId, Name = "GateCountZero Mfg" });
            seedDb.PrinterModels.Add(new PrinterModel { Id = modelId, ManufacturerId = mfgId, Name = "GateCountZero Model" });

            var printer = new Printer
            {
                Id = Guid.NewGuid(),
                Name = "GateCountZero Printer",
                ServerUrl = "http://192.168.1.50",
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
            printerId = printer.Id;
        }

        Printer reloaded = await _dbContext.Printers
            .Include(p => p.Toolheads)
            .FirstAsync(p => p.Id == printerId);

        reloaded.MultiMaterial = true;
        _printersService.SyncMmuToolheadsOnEntity(reloaded, wasMultiMaterial: false, mmuGateCount: 0);
        await _dbContext.SaveChangesAsync();

        int mmuCount = await _dbContext.Toolheads
            .CountAsync(t => t.PrinterId == printerId && t.ToolheadType == ToolheadType.MmuGate);

        mmuCount.Should().Be(0, "mmuGateCount=0 should not create any gates");
    }
}
