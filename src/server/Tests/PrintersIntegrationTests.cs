using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Farm.Web.Server.Tests;

public class PrintersIntegrationTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public PrintersIntegrationTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
        {
            // Optionally override configuration here (e.g., in-memory DB) for isolation.
        });
    }

    [Fact]
    public async Task Healthz_should_return_ok()
    {
        var client = _factory.CreateClient();
        var resp = await client.GetAsync("/healthz");
        resp.IsSuccessStatusCode.Should().BeTrue();
        var body = await resp.Content.ReadFromJsonAsync<HealthzDto>();
        body!.status.Should().Be("ok");
    }

    [Fact]
    public async Task Printers_list_should_return_200()
    {
        var client = _factory.CreateClient();
        var resp = await client.GetAsync("/api/printers");
        resp.IsSuccessStatusCode.Should().BeTrue();
    }

    [Fact]
    public async Task Create_then_get_then_delete_printer_smoke()
    {
        var client = _factory.CreateClient();
        // Create minimal printer (Moonraker backend; URL can be dummy)
        var created = await client.PostAsJsonAsync("/api/printers", new CreatePrinterDto
        {
            Name = "itest-printer",
            ServerUrl = "http://localhost:9999",
            Notes = "itest",
            Backend = Farm.Web.Shared.PrinterBackend.Moonraker
        });
        created.IsSuccessStatusCode.Should().BeTrue();
        var dto = await created.Content.ReadFromJsonAsync<PrinterDto>();
        dto.Should().NotBeNull();
        dto!.Name.Should().Be("itest-printer");
        dto.Backend.Should().Be(Farm.Web.Shared.PrinterBackend.Moonraker);

        // Get by id
        var got = await client.GetAsync($"/api/printers/{dto.Id}");
        got.IsSuccessStatusCode.Should().BeTrue();
        var gotDto = await got.Content.ReadFromJsonAsync<PrinterDto>();
        gotDto!.Id.Should().Be(dto.Id);

        // Delete
        var del = await client.DeleteAsync($"/api/printers/{dto.Id}");
        del.IsSuccessStatusCode.Should().BeTrue();
    }

    private record HealthzDto(string status);

    // Shared DTOs (minimal inline copies to avoid test referencing runtime-specific JSON options)
    private class CreatePrinterDto
    {
        public string Name { get; set; } = string.Empty;
        public string ServerUrl { get; set; } = string.Empty;
        public string? Notes { get; set; }
        public Guid? ManufacturerId { get; set; }
        public Guid? ModelId { get; set; }
        public string? NewManufacturerName { get; set; }
        public string? NewModelName { get; set; }
        public DateTime? DateAcquired { get; set; }
        public Farm.Web.Shared.PrinterBackend Backend { get; set; }
        public string? ApiKey { get; set; }
    }

    private record PrinterDto(
        Guid Id,
        string Name,
        string ServerUrl,
        string? Notes,
        bool IsOnline,
        string? State,
        string? ManufacturerName,
        string? ModelName,
        double? Progress,
        string? JobName,
        string? ThumbnailUrl,
        string? CameraStreamUrl,
        string? CameraSnapshotUrl,
        double? X,
        double? Y,
        double? Z,
        double? HotendTemp,
        double? BedTemp,
        double? HotendTarget,
        double? BedTarget,
        Farm.Web.Shared.PrinterBackend Backend,
        string? ApiKey
    );
}
