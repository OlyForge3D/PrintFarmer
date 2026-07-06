using System.Text.Json;
using System.Text.Json.Serialization;
using Farm.Infrastructure.Domain;

namespace Farm.Web.Api.Tests;

public class MaintenanceSerializationTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    [Fact]
    public void MaintenanceAlert_WithLoadedNavigationProperties_SerializesNavigationProperties()
    {
        var alert = new MaintenanceAlert
        {
            Id = Guid.NewGuid(),
            PrinterId = Guid.NewGuid(),
            Printer = new Printer { Id = Guid.NewGuid(), Name = "Printer A" },
            MaintenanceTaskId = Guid.NewGuid(),
            MaintenanceTask = new MaintenanceTask { Id = Guid.NewGuid(), TaskName = "Clean nozzle" },
            Title = "Maintenance due",
            Message = "Clean nozzle"
        };

        string json = JsonSerializer.Serialize(alert, JsonOptions);
        using JsonDocument document = JsonDocument.Parse(json);

        _ = document.RootElement.TryGetProperty("printer", out JsonElement printer).Should().BeTrue();
        _ = printer.GetProperty("name").GetString().Should().Be("Printer A");
        _ = document.RootElement.TryGetProperty("maintenanceTask", out JsonElement maintenanceTask).Should().BeTrue();
        _ = maintenanceTask.GetProperty("taskName").GetString().Should().Be("Clean nozzle");
    }

    [Fact]
    public void MaintenanceLog_WithLoadedNavigationProperties_SerializesNavigationProperties()
    {
        var log = new MaintenanceLog
        {
            Id = Guid.NewGuid(),
            PrinterId = Guid.NewGuid(),
            Printer = new Printer { Id = Guid.NewGuid(), Name = "Printer B" },
            MaintenanceTaskId = Guid.NewGuid(),
            MaintenanceTask = new MaintenanceTask { Id = Guid.NewGuid(), TaskName = "Lubricate rails" },
            TaskName = "Lubricate rails"
        };

        string json = JsonSerializer.Serialize(log, JsonOptions);
        using JsonDocument document = JsonDocument.Parse(json);

        _ = document.RootElement.TryGetProperty("printer", out JsonElement printer).Should().BeTrue();
        _ = printer.GetProperty("name").GetString().Should().Be("Printer B");
        _ = document.RootElement.TryGetProperty("maintenanceTask", out JsonElement maintenanceTask).Should().BeTrue();
        _ = maintenanceTask.GetProperty("taskName").GetString().Should().Be("Lubricate rails");
    }
}
