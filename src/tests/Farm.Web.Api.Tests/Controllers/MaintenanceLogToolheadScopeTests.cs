using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Maintenance;
using Farm.Web.Api.Controllers;
using Farm.Web.Api.Tests.TestInfrastructure;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Farm.Web.Api.Tests.Controllers;

/// <summary>
/// Integration tests for the per-toolhead maintenance log scope wiring in
/// <see cref="MaintenanceController"/> (issue #711, F6). Verifies manual log creation
/// validates and persists the optional toolhead scope, rejects ineligible toolheads,
/// and preserves legacy printer-wide behavior.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public class MaintenanceLogToolheadScopeTests : IAsyncLifetime
{
    private readonly CustomWebApplicationFactory _factory;
    private AsyncServiceScope _scope;
    private AppDbContext _db = null!;
    private MaintenanceController _controller = null!;

    public MaintenanceLogToolheadScopeTests()
    {
        _factory = CustomWebApplicationFactory.CreateWithIsolatedDatabase();
    }

    public async Task InitializeAsync()
    {
        _scope = _factory.Services.CreateAsyncScope();
        _db = _scope.ServiceProvider.GetRequiredService<AppDbContext>();
        _controller = ActivatorUtilities.CreateInstance<MaintenanceController>(_scope.ServiceProvider);
        await Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _scope.DisposeAsync();
        _factory?.Dispose();
    }

    private async Task<(Printer Printer, Toolhead T0, Toolhead Mmu)> SeedAsync()
    {
        string suffix = Guid.NewGuid().ToString("N")[..8];
        Manufacturer mfg = new() { Id = Guid.NewGuid(), Name = $"Mfg-{suffix}" };
        PrinterModel model = new() { Id = Guid.NewGuid(), ManufacturerId = mfg.Id, Name = $"Model-{suffix}" };
        Printer printer = new()
        {
            Id = Guid.NewGuid(),
            Name = $"Printer-{suffix}",
            ManufacturerId = mfg.Id,
            ModelId = model.Id,
            ServerUrl = $"http://10.0.2.{(Math.Abs(suffix.GetHashCode(StringComparison.Ordinal)) % 240) + 2}",
            IsEnabled = true,
        };
        Toolhead t0 = new() { Id = Guid.NewGuid(), PrinterId = printer.Id, Index = 0, Name = "T0", ToolheadType = ToolheadType.Physical };
        Toolhead mmu = new() { Id = Guid.NewGuid(), PrinterId = printer.Id, Index = 1, Name = "MMU-1", ToolheadType = ToolheadType.MmuGate };

        _db.Manufacturers.Add(mfg);
        _db.PrinterModels.Add(model);
        _db.Printers.Add(printer);
        _db.Toolheads.AddRange(t0, mmu);
        await _db.SaveChangesAsync();

        return (printer, t0, mmu);
    }

    private static CreateMaintenanceLogRequest LogRequest(Guid printerId, Guid? toolheadId) => new(
        PrinterId: printerId,
        DeploymentId: null,
        TaskId: null,
        TaskName: "Manual",
        ComponentName: null,
        PerformedAt: null,
        PerformedBy: "operator",
        Notes: null,
        DurationMinutes: null,
        Cost: null,
        PartsReplaced: null,
        ToolheadId: toolheadId);

    [Fact]
    public async Task CreateLog_WithPhysicalToolhead_PersistsScope()
    {
        (Printer p, Toolhead t0, _) = await SeedAsync();

        ActionResult<MaintenanceLog> result = await _controller.CreateMaintenanceLogAsync(
            LogRequest(p.Id, t0.Id), CancellationToken.None);

        CreatedAtActionResult created = result.Result.Should().BeOfType<CreatedAtActionResult>().Subject;
        MaintenanceLog body = created.Value.Should().BeOfType<MaintenanceLog>().Subject;
        body.ToolheadId.Should().Be(t0.Id);

        MaintenanceLog persisted = await _db.MaintenanceLogs.AsNoTracking().SingleAsync(l => l.Id == body.Id);
        persisted.ToolheadId.Should().Be(t0.Id);
    }

    [Fact]
    public async Task CreateLog_WithoutToolhead_PersistsPrinterWide()
    {
        (Printer p, _, _) = await SeedAsync();

        ActionResult<MaintenanceLog> result = await _controller.CreateMaintenanceLogAsync(
            LogRequest(p.Id, toolheadId: null), CancellationToken.None);

        CreatedAtActionResult created = result.Result.Should().BeOfType<CreatedAtActionResult>().Subject;
        MaintenanceLog body = created.Value.Should().BeOfType<MaintenanceLog>().Subject;
        body.ToolheadId.Should().BeNull();
    }

    [Fact]
    public async Task CreateLog_WithMmuGateToolhead_ReturnsBadRequest()
    {
        (Printer p, _, Toolhead mmu) = await SeedAsync();

        ActionResult<MaintenanceLog> result = await _controller.CreateMaintenanceLogAsync(
            LogRequest(p.Id, mmu.Id), CancellationToken.None);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task CreateLog_WithForeignToolhead_ReturnsBadRequest()
    {
        (Printer p, _, _) = await SeedAsync();
        (_, Toolhead otherToolhead, _) = await SeedAsync();

        ActionResult<MaintenanceLog> result = await _controller.CreateMaintenanceLogAsync(
            LogRequest(p.Id, otherToolhead.Id), CancellationToken.None);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }
}
