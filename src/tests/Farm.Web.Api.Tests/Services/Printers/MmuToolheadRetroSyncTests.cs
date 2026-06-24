using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Printers;
using Farm.Web.Api.Tests.TestInfrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Farm.Web.Api.Tests.Services.Printers;

/// <summary>
/// Tests for retroactive MMU virtual toolhead creation.
/// Verifies that legacy multi-material printers (created before the multi-toolhead feature)
/// get their MmuGate toolhead rows auto-created on demand.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public class MmuToolheadRetroSyncTests : IAsyncLifetime
{
    private readonly CustomWebApplicationFactory _factory;
    private AsyncServiceScope _scope;
    private IPrintersService _printersService = null!;
    private AppDbContext _dbContext = null!;

    public MmuToolheadRetroSyncTests()
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

    private async Task<Printer> SeedLegacyMmuPrinter(string name = "Legacy MMU Printer")
    {
        await using AsyncServiceScope seedScope = _factory.Services.CreateAsyncScope();
        AppDbContext seedDb = seedScope.ServiceProvider.GetRequiredService<AppDbContext>();

        Guid manufacturerId = Guid.NewGuid();
        Guid modelId = Guid.NewGuid();

        seedDb.Manufacturers.Add(new Manufacturer { Id = manufacturerId, Name = $"{name} Mfg" });
        seedDb.PrinterModels.Add(new PrinterModel { Id = modelId, ManufacturerId = manufacturerId, Name = $"{name} Model" });

        var toolhead = new Toolhead
        {
            Id = Guid.NewGuid(),
            Name = "Extruder",
            Index = 0,
            ToolheadType = ToolheadType.Physical,
            IsPrimary = true,
            UpdatedAt = DateTime.UtcNow
        };

        var printer = new Printer
        {
            Id = Guid.NewGuid(),
            Name = name,
            ServerUrl = "http://192.168.1.50",
            BackendPort = 7125,
            Backend = (int)PrinterBackend.Moonraker,
            MultiMaterial = true,
            ManufacturerId = manufacturerId,
            ModelId = modelId
        };

        toolhead.PrinterId = printer.Id;
        printer.Toolheads.Add(toolhead);

        seedDb.Printers.Add(printer);
        await seedDb.SaveChangesAsync();
        return printer;
    }

    private async Task<Printer> SeedSingleToolheadPrinter()
    {
        await using AsyncServiceScope seedScope = _factory.Services.CreateAsyncScope();
        AppDbContext seedDb = seedScope.ServiceProvider.GetRequiredService<AppDbContext>();

        Guid manufacturerId = Guid.NewGuid();
        Guid modelId = Guid.NewGuid();

        seedDb.Manufacturers.Add(new Manufacturer { Id = manufacturerId, Name = "Single Mfg" });
        seedDb.PrinterModels.Add(new PrinterModel { Id = modelId, ManufacturerId = manufacturerId, Name = "Single Model" });

        var toolhead = new Toolhead
        {
            Id = Guid.NewGuid(),
            Name = "Extruder",
            Index = 0,
            ToolheadType = ToolheadType.Physical,
            IsPrimary = true,
            UpdatedAt = DateTime.UtcNow
        };

        var printer = new Printer
        {
            Id = Guid.NewGuid(),
            Name = "Single Toolhead Printer",
            ServerUrl = "http://192.168.1.51",
            BackendPort = 7125,
            Backend = (int)PrinterBackend.Moonraker,
            MultiMaterial = false,
            ManufacturerId = manufacturerId,
            ModelId = modelId
        };

        toolhead.PrinterId = printer.Id;
        printer.Toolheads.Add(toolhead);

        seedDb.Printers.Add(printer);
        await seedDb.SaveChangesAsync();
        return printer;
    }

    private async Task<Printer> SeedMmuPrinterWithGates()
    {
        await using AsyncServiceScope seedScope = _factory.Services.CreateAsyncScope();
        AppDbContext seedDb = seedScope.ServiceProvider.GetRequiredService<AppDbContext>();

        Guid manufacturerId = Guid.NewGuid();
        Guid modelId = Guid.NewGuid();

        seedDb.Manufacturers.Add(new Manufacturer { Id = manufacturerId, Name = "Gates Mfg" });
        seedDb.PrinterModels.Add(new PrinterModel { Id = modelId, ManufacturerId = manufacturerId, Name = "Gates Model" });

        var printer = new Printer
        {
            Id = Guid.NewGuid(),
            Name = "MMU Printer With Gates",
            ServerUrl = "http://192.168.1.52",
            BackendPort = 7125,
            Backend = (int)PrinterBackend.Moonraker,
            MultiMaterial = true,
            ManufacturerId = manufacturerId,
            ModelId = modelId
        };

        var t0 = new Toolhead
        {
            Id = Guid.NewGuid(),
            PrinterId = printer.Id,
            Name = "Extruder",
            Index = 0,
            ToolheadType = ToolheadType.Physical,
            IsPrimary = true,
            UpdatedAt = DateTime.UtcNow
        };
        printer.Toolheads.Add(t0);

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
        return printer;
    }

    // ------- EnsureMmuToolheadsAsync -------

    [Fact]
    public async Task EnsureMmuToolheadsAsync_CreatesGates_ForLegacyMmuPrinter()
    {
        Printer printer = await SeedLegacyMmuPrinter();

        CommandResult result = await _printersService.EnsureMmuToolheadsAsync(printer.Id, CancellationToken.None);

        result.Success.Should().BeTrue();
        result.Message.Should().Contain("Created");

        List<Toolhead> toolheads = await _dbContext.Toolheads
            .Where(t => t.PrinterId == printer.Id)
            .OrderBy(t => t.Index)
            .ToListAsync();

        toolheads.Should().HaveCount(5);
        toolheads[0].ToolheadType.Should().Be(ToolheadType.Physical);
        toolheads[1].ToolheadType.Should().Be(ToolheadType.MmuGate);
        toolheads[2].ToolheadType.Should().Be(ToolheadType.MmuGate);
        toolheads[3].ToolheadType.Should().Be(ToolheadType.MmuGate);
        toolheads[4].ToolheadType.Should().Be(ToolheadType.MmuGate);
    }

    [Fact]
    public async Task EnsureMmuToolheadsAsync_IsIdempotent_WhenGatesAlreadyExist()
    {
        Printer printer = await SeedMmuPrinterWithGates();

        CommandResult result = await _printersService.EnsureMmuToolheadsAsync(printer.Id, CancellationToken.None);

        result.Success.Should().BeTrue();
        result.Message.Should().Contain("already has");

        int toolheadCount = await _dbContext.Toolheads.CountAsync(t => t.PrinterId == printer.Id);
        toolheadCount.Should().Be(4);
    }

    [Fact]
    public async Task EnsureMmuToolheadsAsync_NoOp_ForNonMmuPrinter()
    {
        Printer printer = await SeedSingleToolheadPrinter();

        CommandResult result = await _printersService.EnsureMmuToolheadsAsync(printer.Id, CancellationToken.None);

        result.Success.Should().BeTrue();
        result.Message.Should().Contain("not multi-material");

        int toolheadCount = await _dbContext.Toolheads.CountAsync(t => t.PrinterId == printer.Id);
        toolheadCount.Should().Be(1);
    }

    [Fact]
    public async Task EnsureMmuToolheadsAsync_ReturnsFailure_ForUnknownPrinter()
    {
        CommandResult result = await _printersService.EnsureMmuToolheadsAsync(Guid.NewGuid(), CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Message.Should().Contain("not found");
    }

    // ------- On-demand auto-creation in spool endpoints -------

    [Fact]
    public async Task SetToolheadSpoolAsync_AutoCreatesGates_ForLegacyMmuPrinter()
    {
        Printer printer = await SeedLegacyMmuPrinter();

        int beforeCount = await _dbContext.Toolheads.CountAsync(t => t.PrinterId == printer.Id);
        beforeCount.Should().Be(1);

        // Call spool assignment on T1 — gate doesn't exist yet but should be auto-created
        await _printersService.SetToolheadSpoolAsync(printer.Id, 1, spoolId: 999, CancellationToken.None);

        // Verify gates were created regardless of spool assignment outcome (Spoolman may not be configured)
        List<Toolhead> toolheads = await _dbContext.Toolheads
            .Where(t => t.PrinterId == printer.Id)
            .OrderBy(t => t.Index)
            .ToListAsync();

        toolheads.Count.Should().BeGreaterThanOrEqualTo(4);
        toolheads.Should().Contain(t => t.Index == 1 && t.ToolheadType == ToolheadType.MmuGate);
    }

    [Fact]
    public async Task ClearToolheadSpoolAsync_AutoCreatesGates_ForLegacyMmuPrinter()
    {
        Printer printer = await SeedLegacyMmuPrinter();

        int beforeCount = await _dbContext.Toolheads.CountAsync(t => t.PrinterId == printer.Id);
        beforeCount.Should().Be(1);

        CommandResult result = await _printersService.ClearToolheadSpoolAsync(printer.Id, 2, CancellationToken.None);

        result.Success.Should().BeTrue();

        List<Toolhead> toolheads = await _dbContext.Toolheads
            .Where(t => t.PrinterId == printer.Id)
            .OrderBy(t => t.Index)
            .ToListAsync();

        toolheads.Count.Should().BeGreaterThanOrEqualTo(4);
        toolheads.Should().Contain(t => t.Index == 2 && t.ToolheadType == ToolheadType.MmuGate);
    }

    [Fact]
    public async Task SetToolheadSpoolAsync_AutoPromotesMultiMaterial_ForNonMmuPrinterMissingToolhead()
    {
        Printer printer = await SeedSingleToolheadPrinter();

        CommandResult result = await _printersService.SetToolheadSpoolAsync(printer.Id, 1, spoolId: 999, CancellationToken.None);

        result.Success.Should().BeTrue("auto-promotion should create the needed gate");

        // Printer should now be MultiMaterial with virtual gates created
        Printer? reloaded = await _dbContext.Printers
            .Include(p => p.Toolheads)
            .FirstOrDefaultAsync(p => p.Id == printer.Id);

        reloaded!.MultiMaterial.Should().BeTrue("printer should be promoted to MultiMaterial");
        reloaded.Toolheads.Count.Should().BeGreaterThanOrEqualTo(4, "virtual MMU gates should be created");
        reloaded.Toolheads.Should().Contain(t => t.Index == 1 && t.ToolheadType == ToolheadType.MmuGate);
    }
}
