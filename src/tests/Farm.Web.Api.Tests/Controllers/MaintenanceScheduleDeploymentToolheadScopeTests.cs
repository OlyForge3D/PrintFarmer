using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Maintenance;
using Farm.Infrastructure.Repositories.Printers;
using Farm.Infrastructure.Services.OperatorFeatures;
using Farm.Web.Api.Controllers;
using Farm.Web.Api.Controllers.Requests;
using Farm.Web.Api.DTOs;
using Farm.Web.Api.Tests.TestInfrastructure;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Controllers;

/// <summary>
/// Integration tests for the per-toolhead maintenance deployment scope wiring in
/// <see cref="MaintenanceScheduleDeploymentController"/> (issue #711, F6). Verifies the
/// controller validates the optional toolhead scope, persists it, preserves legacy
/// printer-wide behavior, and lets two schedules that differ only by toolhead coexist.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public class MaintenanceScheduleDeploymentToolheadScopeTests : IAsyncLifetime
{
    private readonly CustomWebApplicationFactory _factory;
    private AsyncServiceScope _scope;
    private AppDbContext _db = null!;
    private MaintenanceScheduleDeploymentController _controller = null!;

    public MaintenanceScheduleDeploymentToolheadScopeTests()
    {
        _factory = CustomWebApplicationFactory.CreateWithIsolatedDatabase();
    }

    public async Task InitializeAsync()
    {
        _scope = _factory.Services.CreateAsyncScope();
        _db = _scope.ServiceProvider.GetRequiredService<AppDbContext>();
        _controller = new MaintenanceScheduleDeploymentController(
            NullLogger<MaintenanceScheduleDeploymentController>.Instance,
            _scope.ServiceProvider.GetRequiredService<IPrinterMaintenanceScheduleRepository>(),
            _scope.ServiceProvider.GetRequiredService<IMaintenancePlanRepository>(),
            _scope.ServiceProvider.GetRequiredService<IPrintersRepository>(),
            _scope.ServiceProvider.GetRequiredService<IOperatorFeatureGate>());
        await Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _scope.DisposeAsync();
        _factory?.Dispose();
    }

    private async Task<(Printer Printer, Toolhead T0, Toolhead Mmu, MaintenancePlan Plan)> SeedAsync()
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
            ServerUrl = $"http://printer-{Guid.NewGuid():N}.local",
            IsEnabled = true,
        };
        Toolhead t0 = new() { Id = Guid.NewGuid(), PrinterId = printer.Id, Index = 0, Name = "T0", ToolheadType = ToolheadType.Physical };
        Toolhead mmu = new() { Id = Guid.NewGuid(), PrinterId = printer.Id, Index = 1, Name = "MMU-1", ToolheadType = ToolheadType.MmuGate };
        MaintenancePlan plan = new() { Id = Guid.NewGuid(), Name = $"Plan-{suffix}", IsActive = true };

        _db.Manufacturers.Add(mfg);
        _db.PrinterModels.Add(model);
        _db.Printers.Add(printer);
        _db.Toolheads.AddRange(t0, mmu);
        _db.MaintenancePlans.Add(plan);
        await _db.SaveChangesAsync();

        return (printer, t0, mmu, plan);
    }

    private static PrinterMaintenanceScheduleResponse GetCreatedBody(ActionResult<PrinterMaintenanceScheduleResponse> result)
    {
        CreatedResult created = result.Result.Should().BeOfType<CreatedResult>().Subject;
        return created.Value.Should().BeOfType<PrinterMaintenanceScheduleResponse>().Subject;
    }

    [Fact]
    public async Task Deploy_WithPhysicalToolhead_PersistsToolheadScope()
    {
        (Printer p, Toolhead t0, _, MaintenancePlan plan) = await SeedAsync();

        ActionResult<PrinterMaintenanceScheduleResponse> result = await _controller.DeployAsync(
            new DeployMaintenancePlanRequest(plan.Id, p.Id, t0.Id), CancellationToken.None);

        PrinterMaintenanceScheduleResponse body = GetCreatedBody(result);
        body.ToolheadId.Should().Be(t0.Id);
        body.ToolheadName.Should().Be("T0");
    }

    [Fact]
    public async Task Deploy_WithPhysicalToolheadAndFeatureDisabled_ReturnsBadRequest()
    {
        (Printer p, Toolhead t0, _, MaintenancePlan plan) = await SeedAsync();
        Mock<IOperatorFeatureGate> gate = new();
        gate.Setup(g => g.IsEnabled(OperatorFeature.MultiSlotFallback)).Returns(false);
        var controller = new MaintenanceScheduleDeploymentController(
            NullLogger<MaintenanceScheduleDeploymentController>.Instance,
            _scope.ServiceProvider.GetRequiredService<IPrinterMaintenanceScheduleRepository>(),
            _scope.ServiceProvider.GetRequiredService<IMaintenancePlanRepository>(),
            _scope.ServiceProvider.GetRequiredService<IPrintersRepository>(),
            gate.Object);

        ActionResult<PrinterMaintenanceScheduleResponse> result = await controller.DeployAsync(
            new DeployMaintenancePlanRequest(plan.Id, p.Id, t0.Id), CancellationToken.None);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
        (await _db.Set<PrinterMaintenanceSchedule>().CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Deploy_WithoutToolhead_PreservesPrinterWideScope()
    {
        (Printer p, _, _, MaintenancePlan plan) = await SeedAsync();

        ActionResult<PrinterMaintenanceScheduleResponse> result = await _controller.DeployAsync(
            new DeployMaintenancePlanRequest(plan.Id, p.Id), CancellationToken.None);

        PrinterMaintenanceScheduleResponse body = GetCreatedBody(result);
        body.ToolheadId.Should().BeNull();
    }

    [Fact]
    public async Task Deploy_WithMmuGateToolhead_ReturnsBadRequest()
    {
        (Printer p, _, Toolhead mmu, MaintenancePlan plan) = await SeedAsync();

        ActionResult<PrinterMaintenanceScheduleResponse> result = await _controller.DeployAsync(
            new DeployMaintenancePlanRequest(plan.Id, p.Id, mmu.Id), CancellationToken.None);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Deploy_WithForeignToolhead_ReturnsBadRequest()
    {
        (Printer p, _, _, MaintenancePlan plan) = await SeedAsync();
        (_, Toolhead otherToolhead, _, _) = await SeedAsync();

        ActionResult<PrinterMaintenanceScheduleResponse> result = await _controller.DeployAsync(
            new DeployMaintenancePlanRequest(plan.Id, p.Id, otherToolhead.Id), CancellationToken.None);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Deploy_SamePlanDifferentToolheads_BothSucceed()
    {
        (Printer p, Toolhead t0, _, MaintenancePlan plan) = await SeedAsync();
        Toolhead t1 = new() { Id = Guid.NewGuid(), PrinterId = p.Id, Index = 2, Name = "T1", ToolheadType = ToolheadType.Physical };
        _db.Toolheads.Add(t1);
        await _db.SaveChangesAsync();

        ActionResult<PrinterMaintenanceScheduleResponse> first = await _controller.DeployAsync(
            new DeployMaintenancePlanRequest(plan.Id, p.Id, t0.Id), CancellationToken.None);
        ActionResult<PrinterMaintenanceScheduleResponse> second = await _controller.DeployAsync(
            new DeployMaintenancePlanRequest(plan.Id, p.Id, t1.Id), CancellationToken.None);
        ActionResult<PrinterMaintenanceScheduleResponse> printerWide = await _controller.DeployAsync(
            new DeployMaintenancePlanRequest(plan.Id, p.Id), CancellationToken.None);

        GetCreatedBody(first).ToolheadId.Should().Be(t0.Id);
        GetCreatedBody(second).ToolheadId.Should().Be(t1.Id);
        GetCreatedBody(printerWide).ToolheadId.Should().BeNull();
    }

    [Fact]
    public async Task Deploy_SamePlanSameToolheadTwice_ReturnsConflict()
    {
        (Printer p, Toolhead t0, _, MaintenancePlan plan) = await SeedAsync();

        await _controller.DeployAsync(new DeployMaintenancePlanRequest(plan.Id, p.Id, t0.Id), CancellationToken.None);
        ActionResult<PrinterMaintenanceScheduleResponse> duplicate = await _controller.DeployAsync(
            new DeployMaintenancePlanRequest(plan.Id, p.Id, t0.Id), CancellationToken.None);

        duplicate.Result.Should().BeOfType<ConflictObjectResult>();
    }

    [Fact]
    public async Task Deploy_SamePlanPrinterWideTwice_ReturnsConflict()
    {
        (Printer p, _, _, MaintenancePlan plan) = await SeedAsync();

        await _controller.DeployAsync(new DeployMaintenancePlanRequest(plan.Id, p.Id), CancellationToken.None);
        ActionResult<PrinterMaintenanceScheduleResponse> duplicate = await _controller.DeployAsync(
            new DeployMaintenancePlanRequest(plan.Id, p.Id), CancellationToken.None);

        duplicate.Result.Should().BeOfType<ConflictObjectResult>();
    }

    [Fact]
    public async Task Update_ToolheadScopedScheduleAndFeatureDisabled_ReturnsBadRequest()
    {
        (Printer p, Toolhead t0, _, MaintenancePlan plan) = await SeedAsync();
        PrinterMaintenanceScheduleResponse deployed = GetCreatedBody(
            await _controller.DeployAsync(
                new DeployMaintenancePlanRequest(plan.Id, p.Id, t0.Id),
                CancellationToken.None));
        Mock<IOperatorFeatureGate> gate = new();
        gate.Setup(g => g.IsEnabled(OperatorFeature.MultiSlotFallback)).Returns(false);
        MaintenanceScheduleDeploymentController controller = new(
            NullLogger<MaintenanceScheduleDeploymentController>.Instance,
            _scope.ServiceProvider.GetRequiredService<IPrinterMaintenanceScheduleRepository>(),
            _scope.ServiceProvider.GetRequiredService<IMaintenancePlanRepository>(),
            _scope.ServiceProvider.GetRequiredService<IPrintersRepository>(),
            gate.Object);

        ActionResult<PrinterMaintenanceScheduleResponse> result = await controller.UpdateAsync(
            deployed.Id,
            new UpdateScheduleDeploymentRequest(IsActive: false, Notes: "blocked"),
            CancellationToken.None);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
        _db.ChangeTracker.Clear();
        PrinterMaintenanceSchedule stored = await _db.Set<PrinterMaintenanceSchedule>()
            .SingleAsync(s => s.Id == deployed.Id);
        stored.IsActive.Should().BeTrue();
        stored.Notes.Should().BeNull();
    }

    [Fact]
    public async Task Delete_ToolheadScopedScheduleAndFeatureDisabled_ReturnsBadRequest()
    {
        (Printer p, Toolhead t0, _, MaintenancePlan plan) = await SeedAsync();
        PrinterMaintenanceScheduleResponse deployed = GetCreatedBody(
            await _controller.DeployAsync(
                new DeployMaintenancePlanRequest(plan.Id, p.Id, t0.Id),
                CancellationToken.None));
        Mock<IOperatorFeatureGate> gate = new();
        gate.Setup(g => g.IsEnabled(OperatorFeature.MultiSlotFallback)).Returns(false);
        MaintenanceScheduleDeploymentController controller = new(
            NullLogger<MaintenanceScheduleDeploymentController>.Instance,
            _scope.ServiceProvider.GetRequiredService<IPrinterMaintenanceScheduleRepository>(),
            _scope.ServiceProvider.GetRequiredService<IMaintenancePlanRepository>(),
            _scope.ServiceProvider.GetRequiredService<IPrintersRepository>(),
            gate.Object);

        IActionResult result = await controller.DeleteAsync(deployed.Id, CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
        _db.ChangeTracker.Clear();
        (await _db.Set<PrinterMaintenanceSchedule>().AnyAsync(s => s.Id == deployed.Id))
            .Should().BeTrue();
    }
}
