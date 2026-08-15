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

    private async Task<Printer> SeedSingleToolheadPrinter(bool hasMmu = false)
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
            HasMmu = hasMmu,
            ManufacturerId = manufacturerId,
            ModelId = modelId
        };

        toolhead.PrinterId = printer.Id;
        printer.Toolheads.Add(toolhead);

        seedDb.Printers.Add(printer);
        await seedDb.SaveChangesAsync();
        return printer;
    }

    private async Task<Printer> SeedMmuPrinterWithGates(int gateCount = 4, string name = "MMU Printer With Gates")
    {
        await using AsyncServiceScope seedScope = _factory.Services.CreateAsyncScope();
        AppDbContext seedDb = seedScope.ServiceProvider.GetRequiredService<AppDbContext>();

        Guid manufacturerId = Guid.NewGuid();
        Guid modelId = Guid.NewGuid();

        seedDb.Manufacturers.Add(new Manufacturer { Id = manufacturerId, Name = $"{name} Mfg" });
        seedDb.PrinterModels.Add(new PrinterModel { Id = modelId, ManufacturerId = manufacturerId, Name = $"{name} Model" });

        var printer = new Printer
        {
            Id = Guid.NewGuid(),
            Name = name,
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

        for (int i = 1; i <= gateCount; i++)
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

    private async Task<Printer> SeedSnapmakerU1StylePrinter()
    {
        await using AsyncServiceScope seedScope = _factory.Services.CreateAsyncScope();
        AppDbContext seedDb = seedScope.ServiceProvider.GetRequiredService<AppDbContext>();

        Guid manufacturerId = Guid.NewGuid();
        Guid modelId = Guid.NewGuid();
        string uniqueSuffix = Guid.NewGuid().ToString("N");

        seedDb.Manufacturers.Add(new Manufacturer { Id = manufacturerId, Name = $"Snapmaker {uniqueSuffix}" });
        seedDb.PrinterModels.Add(new PrinterModel { Id = modelId, ManufacturerId = manufacturerId, Name = $"U1 {uniqueSuffix}" });

        var printer = new Printer
        {
            Id = Guid.NewGuid(),
            Name = "Snapmaker U1",
            ServerUrl = "http://192.168.1.53",
            BackendPort = 80,
            Backend = (int)PrinterBackend.Moonraker,
            MultiMaterial = true,
            ManufacturerId = manufacturerId,
            ModelId = modelId
        };

        for (int i = 0; i < 4; i++)
        {
            printer.Toolheads.Add(new Toolhead
            {
                Id = Guid.NewGuid(),
                PrinterId = printer.Id,
                Name = $"T{i}",
                Index = i,
                ToolheadType = ToolheadType.Physical,
                IsPrimary = i == 0,
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
        Printer printer = await SeedMmuPrinterWithGates(gateCount: 4);

        CommandResult result = await _printersService.EnsureMmuToolheadsAsync(printer.Id, CancellationToken.None);

        result.Success.Should().BeTrue();
        result.Message.Should().Contain("already has");

        int toolheadCount = await _dbContext.Toolheads.CountAsync(t => t.PrinterId == printer.Id);
        toolheadCount.Should().Be(5);
    }

    [Fact]
    public async Task EnsureMmuToolheadsAsync_ReconcilesPartialGateSetUpward()
    {
        // 3 persisted gates (Index 1-3) while live hardware reports 4 — the exact
        // Qidi Plus 4 scenario from issue #1588: the gate set must grow to cover
        // the missing gate rather than reporting success without acting.
        Printer printer = await SeedMmuPrinterWithGates(gateCount: 3);

        CommandResult result = await _printersService.EnsureMmuToolheadsAsync(printer.Id, CancellationToken.None);

        result.Success.Should().BeTrue();
        result.Message.Should().Contain("Created 1");

        List<Toolhead> toolheads = await _dbContext.Toolheads
            .Where(t => t.PrinterId == printer.Id)
            .OrderBy(t => t.Index)
            .ToListAsync();

        toolheads.Should().HaveCount(5);
        toolheads.Should().Contain(t => t.Index == 4 && t.ToolheadType == ToolheadType.MmuGate);

        // Pre-existing gates 1-3 must be untouched (same Ids, not renumbered/re-bound).
        Toolhead gate1 = toolheads.Single(t => t.Index == 1);
        Toolhead gate2 = toolheads.Single(t => t.Index == 2);
        Toolhead gate3 = toolheads.Single(t => t.Index == 3);
        gate1.ToolheadType.Should().Be(ToolheadType.MmuGate);
        gate2.ToolheadType.Should().Be(ToolheadType.MmuGate);
        gate3.ToolheadType.Should().Be(ToolheadType.MmuGate);
    }

    [Fact]
    public async Task EnsureMmuToolheadsAsync_DoesNotCreateMmuGates_ForU1PhysicalToolheads()
    {
        Printer printer = await SeedSnapmakerU1StylePrinter();

        CommandResult result = await _printersService.EnsureMmuToolheadsAsync(printer.Id, CancellationToken.None);

        result.Success.Should().BeTrue();

        List<Toolhead> toolheads = await _dbContext.Toolheads
            .Where(t => t.PrinterId == printer.Id)
            .OrderBy(t => t.Index)
            .ToListAsync();

        toolheads.Should().HaveCount(4);
        toolheads.Should().OnlyContain(t => t.ToolheadType == ToolheadType.Physical);
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
    public async Task SetToolheadSpoolAsync_AutoPromotesMultiMaterial_ForConfirmedMmuPrinterMissingToolhead()
    {
        // HasMmu=true is the positive hardware-reported signal (e.g. PrusaLink polling)
        // that this printer really has an AMS/MMU — auto-promotion is only legitimate
        // when that signal is present. See issue #1600.
        Printer printer = await SeedSingleToolheadPrinter(hasMmu: true);

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

    /// <summary>
    /// Regression test for issue #1600: binding a spool to a toolchanger's real toolhead
    /// (index > 0) that hasn't been synced/persisted yet must NOT promote the printer to
    /// MultiMaterial and must NOT create phantom MmuGate toolheads, because there is no
    /// positive signal (HasMmu or a persisted MmuGate) that this printer actually has an
    /// MMU/AMS. The old `physicalToolheadCount > 1` defense inside CreateMmuVirtualToolheads
    /// is order-dependent and does not fire here, since only T0 is persisted.
    /// </summary>
    [Fact]
    public async Task SetToolheadSpoolAsync_DoesNotPromoteMultiMaterial_ForToolchangerMissingToolhead()
    {
        Printer printer = await SeedSingleToolheadPrinter(hasMmu: false);

        CommandResult result = await _printersService.SetToolheadSpoolAsync(printer.Id, 1, spoolId: 999, CancellationToken.None);

        result.Success.Should().BeFalse("toolhead 1 has not been synced yet and there is no confirmed MMU signal");

        Printer? reloaded = await _dbContext.Printers
            .Include(p => p.Toolheads)
            .FirstOrDefaultAsync(p => p.Id == printer.Id);

        reloaded!.MultiMaterial.Should().BeFalse("a toolchanger must never be promoted as a side effect of binding a real toolhead");
        reloaded.Toolheads.Should().HaveCount(1, "no phantom MmuGate toolheads should be materialized");
        reloaded.Toolheads.Should().OnlyContain(t => t.ToolheadType == ToolheadType.Physical);
    }

    /// <summary>
    /// Mirror of the above for <see cref="IPrintersService.ClearToolheadSpoolAsync"/> — clearing
    /// a spool on a toolchanger's un-synced real toolhead must not promote MultiMaterial or
    /// create phantom gates either. See issue #1600.
    /// </summary>
    [Fact]
    public async Task ClearToolheadSpoolAsync_DoesNotPromoteMultiMaterial_ForToolchangerMissingToolhead()
    {
        Printer printer = await SeedSingleToolheadPrinter(hasMmu: false);

        CommandResult result = await _printersService.ClearToolheadSpoolAsync(printer.Id, 1, CancellationToken.None);

        result.Success.Should().BeFalse("toolhead 1 has not been synced yet and there is no confirmed MMU signal");

        Printer? reloaded = await _dbContext.Printers
            .Include(p => p.Toolheads)
            .FirstOrDefaultAsync(p => p.Id == printer.Id);

        reloaded!.MultiMaterial.Should().BeFalse("a toolchanger must never be promoted as a side effect of clearing a real toolhead's spool");
        reloaded.Toolheads.Should().HaveCount(1, "no phantom MmuGate toolheads should be materialized");
        reloaded.Toolheads.Should().OnlyContain(t => t.ToolheadType == ToolheadType.Physical);
    }

    /// <summary>
    /// Regression test for issue #1588: a printer whose live hardware reports 4 MMU gates
    /// (e.g. a Qidi Plus 4 QidiBox) but which only has 3 persisted <see cref="ToolheadType.MmuGate"/>
    /// rows must be able to grow its gate set on demand — assigning a spool to the 4th (live-only)
    /// gate must succeed by creating exactly the missing gate, without renumbering or re-binding
    /// gates 1-3 and their existing spool assignments.
    /// </summary>
    [Fact]
    public async Task SetToolheadSpoolAsync_ExtendsPartialGateSet_AssigningToLiveOnlyFourthGate()
    {
        Printer printer = await SeedMmuPrinterWithGates(gateCount: 3, name: "Qidi Plus 4");

        // Bind existing gates 1-3 to spools so we can assert they are preserved untouched.
        List<Toolhead> seededGates = await _dbContext.Toolheads
            .Where(t => t.PrinterId == printer.Id && t.ToolheadType == ToolheadType.MmuGate)
            .OrderBy(t => t.Index)
            .ToListAsync();
        seededGates.Should().HaveCount(3);

        foreach (Toolhead gate in seededGates)
        {
            gate.CurrentSpoolId = 100 + gate.Index;
            gate.CurrentMaterial = $"PLA-{gate.Index}";
        }
        await _dbContext.SaveChangesAsync();

        Dictionary<int, (int SpoolId, string Material)> bindingsBefore = seededGates
            .ToDictionary(g => g.Index, g => (g.CurrentSpoolId!.Value, g.CurrentMaterial!));

        // Live hardware reports gate 4 (mmuStatus.numGates == 4); assign a spool to it.
        CommandResult result = await _printersService.SetToolheadSpoolAsync(
            printer.Id, toolheadIndex: 4, spoolId: 4242, CancellationToken.None);

        result.Success.Should().BeTrue("gate 4 should be created and the spool assigned");

        List<Toolhead> toolheads = await _dbContext.Toolheads
            .Where(t => t.PrinterId == printer.Id)
            .OrderBy(t => t.Index)
            .ToListAsync();

        // Physical T0 + 4 gates — only the missing gate (4) was created.
        toolheads.Should().HaveCount(5);
        Toolhead gate4 = toolheads.Single(t => t.Index == 4);
        gate4.ToolheadType.Should().Be(ToolheadType.MmuGate);
        gate4.CurrentSpoolId.Should().Be(4242);

        // Gates 1-3 keep their original spool bindings — no renumbering, no re-binding.
        foreach ((int index, (int spoolId, string material)) in bindingsBefore)
        {
            Toolhead preserved = toolheads.Single(t => t.Index == index);
            preserved.ToolheadType.Should().Be(ToolheadType.MmuGate);
            preserved.CurrentSpoolId.Should().Be(spoolId);
            preserved.CurrentMaterial.Should().Be(material);
        }
    }
}
