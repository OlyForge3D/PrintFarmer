using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Web.Api.Tests.TestInfrastructure;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Farm.Web.Api.Tests.Controllers;

[Trait("Category", "Integration")]
[Collection(IntegrationTestCollection.Name)]
public sealed class PrinterSessionTimelineControllerTests : IAsyncLifetime
{
    private readonly CustomWebApplicationFactory _factory;
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    public PrinterSessionTimelineControllerTests()
    {
        _factory = new CustomWebApplicationFactory();
    }

    public async Task InitializeAsync()
    {
        await _factory.ResetDatabaseAsync();
    }

    public Task DisposeAsync()
    {
        _factory.Dispose();
        return Task.CompletedTask;
    }

    [Fact(DisplayName = "Session timeline endpoint returns composed printer sessions")]
    public async Task GetSessionTimelineAsync_ReturnsComposedPrinterSessions()
    {
        Guid printerId = await SeedTimelineDataAsync();
        using HttpClient client = await _factory.CreateAuthenticatedClientAsync();

        HttpResponseMessage response = await client.GetAsync($"/api/printers/{printerId}/session-timeline?take=5");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        PrinterSessionTimelineDto? timeline = JsonSerializer.Deserialize<PrinterSessionTimelineDto>(
            await response.Content.ReadAsStringAsync(),
            _jsonOptions);

        timeline.Should().NotBeNull();
        timeline!.PrinterId.Should().Be(printerId);
        timeline.PrinterName.Should().Be("Controller Printer");
        timeline.Sessions.Should().ContainSingle();
        timeline.Sessions[0].FailureIncidentCount.Should().Be(1);
        timeline.Sessions[0].Events.Should().Contain(@event =>
            @event.Type == PrinterSessionTimelineEventType.FailureDetected &&
            @event.AutoPaused == true);
    }

    [Fact(DisplayName = "Session timeline endpoint returns 404 for unknown printer")]
    public async Task GetSessionTimelineAsync_WhenPrinterMissing_ReturnsNotFound()
    {
        using HttpClient client = await _factory.CreateAuthenticatedClientAsync();

        HttpResponseMessage response = await client.GetAsync($"/api/printers/{Guid.NewGuid()}/session-timeline");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    /// <summary>
    /// Seeds a printer, one session, and one failure incident for the integration test.
    /// </summary>
    private async Task<Guid> SeedTimelineDataAsync()
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        AppDbContext dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        Manufacturer manufacturer = new()
        {
            Id = Guid.NewGuid(),
            Name = "Controller Manufacturer",
        };
        PrinterModel model = new()
        {
            Id = Guid.NewGuid(),
            ManufacturerId = manufacturer.Id,
            Name = "Controller Model",
        };
        Printer printer = new()
        {
            Id = Guid.NewGuid(),
            Name = "Controller Printer",
            ServerUrl = "http://controller-printer.local",
            BackendPort = 7125,
            ManufacturerId = manufacturer.Id,
            ModelId = model.Id,
        };
        PrintJob job = new()
        {
            Id = Guid.NewGuid(),
            Name = "jobs/controller-print.gcode",
            AssignedPrinterId = printer.Id,
            Status = PrintJobStatus.Failed,
            Priority = 0,
            QueuePosition = 0,
            CreatedAt = new DateTime(2026, 3, 27, 12, 0, 0, DateTimeKind.Utc),
            UpdatedAt = new DateTime(2026, 3, 27, 12, 40, 0, DateTimeKind.Utc),
            QueuedAt = new DateTime(2026, 3, 27, 12, 0, 0, DateTimeKind.Utc),
            DispatchedAt = new DateTime(2026, 3, 27, 12, 3, 0, DateTimeKind.Utc),
            ActualStartTime = new DateTime(2026, 3, 27, 12, 5, 0, DateTimeKind.Utc),
            ActualEndTime = new DateTime(2026, 3, 27, 12, 40, 0, DateTimeKind.Utc),
            ActualPrintTime = TimeSpan.FromMinutes(35),
            FailureReason = "Operator aborted after failure detection",
        };

        dbContext.Manufacturers.Add(manufacturer);
        dbContext.PrinterModels.Add(model);
        dbContext.Printers.Add(printer);
        dbContext.PrintJobs.Add(job);
        dbContext.FailureDetectionIncidents.Add(new FailureDetectionIncident
        {
            Id = Guid.NewGuid(),
            PrinterId = printer.Id,
            JobId = job.Id,
            JobName = job.Name,
            FileName = "controller-print.gcode",
            Confidence = 0.944m,
            DetectedAt = new DateTime(2026, 3, 27, 12, 20, 0, DateTimeKind.Utc),
            SnapshotUrl = "http://camera.local/controller.jpg",
            AutoPaused = true,
        });

        await dbContext.SaveChangesAsync();
        return printer.Id;
    }
}
