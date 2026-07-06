using System.Net;
using System.Text.Json;
using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace Farm.Web.Api.Tests;

public class MaintenanceSerializationTests
{
    [Fact]
    public async Task GetAllAlertsAsync_ActiveAlertWithLoadedNavigationProperties_ReturnsNavigationObjects()
    {
        await using CustomWebApplicationFactory factory = CustomWebApplicationFactory.CreateWithIsolatedDatabase();
        Guid alertId = Guid.NewGuid();
        Guid printerId = Guid.NewGuid();
        Guid maintenanceTaskId = Guid.NewGuid();

        await SeedMaintenanceAlertAsync(factory, alertId, printerId, maintenanceTaskId);
        using HttpClient client = await factory.CreateAdminClientAsync(
            username: "maintenance-serialization-admin",
            email: "maintenance-serialization-admin@example.com");

        using HttpResponseMessage response = await client.GetAsync("/api/maintenance/alerts");

        _ = response.StatusCode.Should().Be(HttpStatusCode.OK);
        string json = await response.Content.ReadAsStringAsync();
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement alert = document.RootElement
            .EnumerateArray()
            .Single(element => element.GetProperty("id").GetGuid() == alertId);

        _ = alert.GetProperty("printer").ValueKind.Should().Be(JsonValueKind.Object);
        _ = alert.GetProperty("printer").GetProperty("id").GetGuid().Should().Be(printerId);
        _ = alert.GetProperty("printer").GetProperty("name").GetString().Should().Be("Maintenance Serialization Printer");
        _ = alert.GetProperty("maintenanceTask").ValueKind.Should().Be(JsonValueKind.Object);
        _ = alert.GetProperty("maintenanceTask").GetProperty("id").GetGuid().Should().Be(maintenanceTaskId);
        _ = alert.GetProperty("maintenanceTask").GetProperty("taskName").GetString().Should().Be("Clean nozzle");
    }

    private static async Task SeedMaintenanceAlertAsync(
        CustomWebApplicationFactory factory,
        Guid alertId,
        Guid printerId,
        Guid maintenanceTaskId)
    {
        using AsyncServiceScope scope = factory.Services.CreateAsyncScope();
        AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        Guid manufacturerId = Guid.NewGuid();
        Guid modelId = Guid.NewGuid();

        var manufacturer = new Manufacturer
        {
            Id = manufacturerId,
            Name = "Maintenance Serialization Manufacturer"
        };
        var model = new PrinterModel
        {
            Id = modelId,
            ManufacturerId = manufacturerId,
            Name = "Maintenance Serialization Model"
        };
        var printer = new Printer
        {
            Id = printerId,
            ManufacturerId = manufacturerId,
            ModelId = modelId,
            Name = "Maintenance Serialization Printer",
            ServerUrl = $"http://maintenance-serialization-{printerId:N}.local",
            Backend = (int)PrinterBackend.Moonraker,
            BackendPort = 7125
        };
        var maintenanceTask = new MaintenanceTask
        {
            Id = maintenanceTaskId,
            TaskName = "Clean nozzle",
            Category = "Hotend"
        };
        var alert = new MaintenanceAlert
        {
            Id = alertId,
            PrinterId = printerId,
            MaintenanceTaskId = maintenanceTaskId,
            Title = "Maintenance due",
            Message = "Clean nozzle",
            Status = MaintenanceAlertStatus.Active,
            Severity = 3,
            CreatedAt = new DateTime(2026, 7, 6, 0, 0, 0, DateTimeKind.Utc),
            UpdatedAt = new DateTime(2026, 7, 6, 0, 0, 0, DateTimeKind.Utc)
        };

        context.Manufacturers.Add(manufacturer);
        context.PrinterModels.Add(model);
        context.Printers.Add(printer);
        context.MaintenanceTasks.Add(maintenanceTask);
        context.MaintenanceAlerts.Add(alert);
        await context.SaveChangesAsync();
    }
}
