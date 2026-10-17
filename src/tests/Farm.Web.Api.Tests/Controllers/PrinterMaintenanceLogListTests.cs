using System.Text.Json;
using System.Text.Json.Nodes;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Maintenance;
using Farm.Infrastructure.Services.OperatorFeatures;
using Farm.Modules.Maintenance.Controllers;
using Farm.Web.Api.Tests.TestInfrastructure;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Controllers;

/// <summary>
/// Integration tests for the bounded printer maintenance-history list (issue #3297):
/// <c>GET /api/maintenance/printers/{printerId}/logs?limit=&amp;scope=</c>.
/// Omitting both parameters must keep today's response byte-for-byte; a limit returns only the
/// newest rows; invalid input is a 400 validation problem.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public sealed class PrinterMaintenanceLogListTests : IAsyncLifetime
{
    private static readonly DateTime Now = new(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc);

    private readonly CustomWebApplicationFactory _factory;
    private AsyncServiceScope _scope;

    public PrinterMaintenanceLogListTests()
    {
        _factory = CustomWebApplicationFactory.CreateWithIsolatedDatabase();
    }

    public Task InitializeAsync()
    {
        _scope = _factory.Services.CreateAsyncScope();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _scope.DisposeAsync();
        _factory.Dispose();
    }

    private sealed record Seed(Guid PrinterId, Guid ToolheadId, List<Guid> NewestFirst, List<Guid> PrinterWideNewestFirst);

    /// <summary>
    /// Seeds one printer with a physical toolhead, a catalog task, and four logs with distinct
    /// timestamps (two printer-wide, two toolhead-scoped), plus a log on another printer.
    /// </summary>
    private async Task<Seed> SeedAsync()
    {
        await using AsyncServiceScope seedScope = _factory.Services.CreateAsyncScope();
        AppDbContext db = seedScope.ServiceProvider.GetRequiredService<AppDbContext>();

        Guid printerId = Guid.NewGuid();
        string suffix = printerId.ToString("N")[..8];
        Manufacturer mfg = new() { Id = Guid.NewGuid(), Name = $"Mfg-{suffix}" };
        PrinterModel model = new() { Id = Guid.NewGuid(), ManufacturerId = mfg.Id, Name = $"Model-{suffix}" };
        Printer printer = new()
        {
            Id = printerId,
            Name = $"Printer-{suffix}",
            ManufacturerId = mfg.Id,
            ModelId = model.Id,
            ServerUrl = $"http://printer-{printerId:N}.test",
            IsEnabled = true,
        };
        Printer other = new()
        {
            Id = Guid.NewGuid(),
            Name = $"Other-{suffix}",
            ManufacturerId = mfg.Id,
            ModelId = model.Id,
            ServerUrl = $"http://other-{printerId:N}.test",
            IsEnabled = true,
        };
        Toolhead t0 = new() { Id = Guid.NewGuid(), PrinterId = printer.Id, Index = 0, Name = "T0", ToolheadType = ToolheadType.Physical };
        MaintenanceTask task = new() { Id = Guid.NewGuid(), TaskName = "Lubricate rails", Category = "Motion", IntervalDays = 30 };

        MaintenanceLog Log(string name, int hoursAgo, Guid? toolheadId = null, Guid? onPrinter = null) => new()
        {
            Id = Guid.NewGuid(),
            PrinterId = onPrinter ?? printer.Id,
            ToolheadId = toolheadId,
            MaintenanceTaskId = task.Id,
            TaskName = name,
            Notes = $"{name} notes",
            Component = "Rails",
            PerformedBy = "operator",
            PerformedAt = Now.AddHours(-hoursAgo),
            DurationMinutes = 15,
            Cost = 12.5m,
            PrinterHoursAtMaintenance = 100 - hoursAgo,
            ToolheadHoursAtMaintenance = toolheadId.HasValue ? 50 - hoursAgo : null,
            CreatedAt = Now.AddHours(-hoursAgo),
        };

        MaintenanceLog newestTool = Log("newest-tool", 1, t0.Id);
        MaintenanceLog wideA = Log("wide-a", 2);
        MaintenanceLog oldTool = Log("old-tool", 3, t0.Id);
        MaintenanceLog wideB = Log("wide-b", 4);

        db.Manufacturers.Add(mfg);
        db.PrinterModels.Add(model);
        db.Printers.AddRange(printer, other);
        db.Toolheads.Add(t0);
        db.MaintenanceTasks.Add(task);
        db.MaintenanceLogs.AddRange(newestTool, wideA, oldTool, wideB, Log("other-printer", 0, onPrinter: other.Id));
        await db.SaveChangesAsync();

        return new Seed(
            printer.Id,
            t0.Id,
            [newestTool.Id, wideA.Id, oldTool.Id, wideB.Id],
            [wideA.Id, wideB.Id]);
    }

    private MaintenanceController CreateController(bool multiSlotEnabled = true)
    {
        Mock<IOperatorFeatureGate> gate = new();
        gate.Setup(g => g.IsEnabled(OperatorFeature.MultiSlotFallback)).Returns(multiSlotEnabled);
        gate.Setup(g => g.IsEnabledAsync(OperatorFeature.MultiSlotFallback, It.IsAny<CancellationToken>()))
            .ReturnsAsync(multiSlotEnabled);
        MaintenanceController controller = ActivatorUtilities.CreateInstance<MaintenanceController>(
            _scope.ServiceProvider,
            gate.Object);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { RequestServices = _scope.ServiceProvider },
        };
        return controller;
    }

    private static List<MaintenanceLogDto> Body(ActionResult<IEnumerable<MaintenanceLogDto>> result) =>
        Assert.IsType<List<MaintenanceLogDto>>(Assert.IsType<OkObjectResult>(result.Result).Value);

    [Fact]
    public async Task OmittedLimit_ReturnsFullHistory_SerializedExactlyLikeTheLegacyEntityResponse()
    {
        Seed seed = await SeedAsync();
        JsonSerializerOptions json = _scope.ServiceProvider.GetRequiredService<IOptions<JsonOptions>>().Value.JsonSerializerOptions;

        // The pre-#3297 endpoint returned the tracked entities from GetByPrinterIdAsync, loaded
        // as the first query on a fresh request context. Reproduce that as the baseline.
        List<MaintenanceLog> legacy;
        await using (AsyncServiceScope legacyScope = _factory.Services.CreateAsyncScope())
        {
            legacy = await legacyScope.ServiceProvider.GetRequiredService<IMaintenanceLogRepository>()
                .GetByPrinterIdAsync(seed.PrinterId);
        }

        MaintenanceController controller = CreateController();
        List<MaintenanceLogDto> body = Body(await controller.GetPrinterMaintenanceLogsAsync(seed.PrinterId));

        body.Select(l => l.Id).Should().Equal(seed.NewestFirst);
        JsonNode? expected = JsonSerializer.SerializeToNode(legacy, json);
        JsonNode? actual = JsonSerializer.SerializeToNode(body, json);
        JsonNode.DeepEquals(actual, expected).Should().BeTrue(
            $"the DTO must keep the wire shape.\nexpected: {expected?.ToJsonString()}\nactual:   {actual?.ToJsonString()}");
        expected!.AsArray()[0]!["maintenanceTask"].Should().NotBeNull("the baseline must exercise the embedded task");
    }

    [Fact]
    public async Task Limit_ReturnsOnlyNewestRows()
    {
        Seed seed = await SeedAsync();
        MaintenanceController controller = CreateController();

        List<MaintenanceLogDto> body = Body(await controller.GetPrinterMaintenanceLogsAsync(seed.PrinterId, limit: 2));

        body.Select(l => l.Id).Should().Equal(seed.NewestFirst.Take(2));
    }

    [Fact]
    public async Task LimitLargerThanHistory_ReturnsEverything()
    {
        Seed seed = await SeedAsync();
        MaintenanceController controller = CreateController();

        List<MaintenanceLogDto> body = Body(await controller.GetPrinterMaintenanceLogsAsync(
            seed.PrinterId, limit: MaintenanceController.MaxPrinterLogLimit));

        body.Select(l => l.Id).Should().Equal(seed.NewestFirst);
    }

    [Fact]
    public async Task PrinterScope_ReturnsOnlyPrinterWideLogs()
    {
        Seed seed = await SeedAsync();
        MaintenanceController controller = CreateController();

        List<MaintenanceLogDto> body = Body(await controller.GetPrinterMaintenanceLogsAsync(
            seed.PrinterId, limit: 1, scope: "printer"));

        body.Select(l => l.Id).Should().Equal(seed.PrinterWideNewestFirst.Take(1));
    }

    [Fact]
    public async Task ToolheadScope_ReturnsOnlyThatToolheadsLogs()
    {
        Seed seed = await SeedAsync();
        MaintenanceController controller = CreateController();

        List<MaintenanceLogDto> body = Body(await controller.GetPrinterMaintenanceLogsAsync(
            seed.PrinterId, limit: 10, scope: seed.ToolheadId.ToString()));

        body.Should().HaveCount(2).And.OnlyContain(l => l.ToolheadId == seed.ToolheadId);
    }

    [Fact]
    public async Task FeatureDisabled_HidesToolheadLogs_EvenWhenToolheadScopeRequested()
    {
        Seed seed = await SeedAsync();

        List<MaintenanceLogDto> all = Body(await CreateController(multiSlotEnabled: false)
            .GetPrinterMaintenanceLogsAsync(seed.PrinterId));
        MaintenanceController scopedController = CreateController(multiSlotEnabled: false);
        List<MaintenanceLogDto> scoped = Body(await scopedController.GetPrinterMaintenanceLogsAsync(
            seed.PrinterId, limit: 10, scope: seed.ToolheadId.ToString()));

        all.Select(l => l.Id).Should().Equal(seed.PrinterWideNewestFirst);
        scoped.Should().BeEmpty();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(MaintenanceController.MaxPrinterLogLimit + 1)]
    public async Task InvalidLimit_ReturnsValidationProblem(int limit)
    {
        MaintenanceController controller = CreateController();

        ActionResult<IEnumerable<MaintenanceLogDto>> result =
            await controller.GetPrinterMaintenanceLogsAsync(Guid.NewGuid(), limit: limit);

        ObjectResult problem = Assert.IsAssignableFrom<ObjectResult>(result.Result);
        problem.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        Assert.IsType<ValidationProblemDetails>(problem.Value).Errors.Should().ContainKey("limit");
    }

    [Theory]
    [InlineData("toolhead-0")]
    [InlineData("all")]
    public async Task InvalidScope_ReturnsValidationProblem(string scope)
    {
        MaintenanceController controller = CreateController();

        ActionResult<IEnumerable<MaintenanceLogDto>> result =
            await controller.GetPrinterMaintenanceLogsAsync(Guid.NewGuid(), limit: 10, scope: scope);

        ObjectResult problem = Assert.IsAssignableFrom<ObjectResult>(result.Result);
        problem.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        Assert.IsType<ValidationProblemDetails>(problem.Value).Errors.Should().ContainKey("scope");
    }
}
