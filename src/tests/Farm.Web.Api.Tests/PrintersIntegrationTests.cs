using System.Text.Json;
using Moq;

namespace Farm.Web.Api.Tests;

public class PrintersIntegrationTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public PrintersIntegrationTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Healthz_should_return_okAsync()
    {
        var client = _factory.CreateClient();
        var resp = await client.GetAsync("/healthz");
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<HealthzDto>();
        body!.Status.Should().Be("ok");
    }

    [Fact]
    public async Task ApiHealthz_alias_should_return_okAsync()
    {
        var client = _factory.CreateClient();
        var resp = await client.GetAsync("/api/healthz");
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<HealthzDto>();
        body!.Status.Should().Be("ok");
    }

    [Fact]
    public async Task ApiHealth_alias_should_return_comprehensive_jsonAsync()
    {
        var client = _factory.CreateClient();
        var resp = await client.GetAsync("/api/health");
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
        json.TryGetProperty("status", out var statusProp).Should().BeTrue();
        statusProp.GetString().Should().NotBeNull();
        json.TryGetProperty("results", out var resultsProp).Should().BeTrue();
        resultsProp.ValueKind.Should().Be(JsonValueKind.Object);
    }

    [Fact]
    public async Task Printers_list_should_return_200Async()
    {
        var client = _factory.CreateClient();
        var resp = await client.GetAsync("/api/printers");
        resp.IsSuccessStatusCode.Should().BeTrue();
    }

    [Fact]
    public async Task Create_then_get_then_delete_printer_smokeAsync()
    {
        var client = _factory.CreateClient();

        var createDto = new Farm.Web.Shared.CreatePrinterDto
        {
            Name = "itest-printer",
            ServerUrl = "http://localhost:9999",
            Notes = "itest",
            Backend = Farm.Web.Shared.PrinterBackend.Moonraker
        };

        var created = await client.PostAsJsonAsync("/api/printers", createDto);
        created.IsSuccessStatusCode.Should().BeTrue();

        var dto = await created.Content.ReadFromJsonAsync<Farm.Web.Shared.PrinterDto>();
        dto.Should().NotBeNull();
        dto!.Name.Should().Be("itest-printer");
        dto.Backend.Should().Be(Farm.Web.Shared.PrinterBackend.Moonraker);

        // Get by id
        var got = await client.GetAsync($"/api/printers/{dto.Id}");
        got.IsSuccessStatusCode.Should().BeTrue();
        var gotDto = await got.Content.ReadFromJsonAsync<Farm.Web.Shared.PrinterDto>();
        gotDto!.Id.Should().Be(dto.Id);

        // Delete
        var del = await client.DeleteAsync($"/api/printers/{dto.Id}");
        del.IsSuccessStatusCode.Should().BeTrue();
    }

    [Fact]
    public async Task Create_PrusaLink_with_ApiKey_then_deleteAsync()
    {
        var client = _factory.CreateClient();

        var createDto = new Farm.Web.Shared.CreatePrinterDto
        {
            Name = "itest-prusa",
            ServerUrl = "http://localhost:8080",
            Backend = Farm.Web.Shared.PrinterBackend.PrusaLink,
            ApiKey = "test-api-key"
        };

        var created = await client.PostAsJsonAsync("/api/printers", createDto);
        created.IsSuccessStatusCode.Should().BeTrue();

        var dto = await created.Content.ReadFromJsonAsync<Farm.Web.Shared.PrinterDto>();
        dto.Should().NotBeNull();
        dto!.Name.Should().Be("itest-prusa");
        dto.Backend.Should().Be(Farm.Web.Shared.PrinterBackend.PrusaLink);
        dto.ApiKey.Should().Be("test-api-key");

        var del = await client.DeleteAsync($"/api/printers/{dto.Id}");
        del.IsSuccessStatusCode.Should().BeTrue();
    }

    [Fact]
    public async Task Create_SDCP_printer_then_test_endpointsAsync()
    {
        var client = _factory.CreateClient();

        var createDto = new Farm.Web.Shared.CreatePrinterDto
        {
            Name = "itest-sdcp",
            ServerUrl = "http://192.168.1.100",
            Backend = Farm.Web.Shared.PrinterBackend.SDCP,
            Notes = "Test SDCP printer"
        };

        var created = await client.PostAsJsonAsync("/api/printers", createDto);
        created.IsSuccessStatusCode.Should().BeTrue();

        var dto = await created.Content.ReadFromJsonAsync<Farm.Web.Shared.PrinterDto>();
        dto.Should().NotBeNull();
        dto!.Name.Should().Be("itest-sdcp");
        dto.Backend.Should().Be(Farm.Web.Shared.PrinterBackend.SDCP);

        // Mock SDCP camera endpoints to return URLs
        _factory.MockSdcpClient
            .Setup(x => x.GetCameraUrlAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("http://192.168.1.100:8080/video");
        _factory.MockSdcpClient
            .Setup(x => x.GetCameraSnapshotUrlAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("http://192.168.1.100:8080/snapshot");

        // Test camera URL endpoint returns typed JSON
        var cameraUrl = await client.GetAsync($"/api/printers/{dto.Id}/camera/url");
        cameraUrl.IsSuccessStatusCode.Should().BeTrue();
        var cam = await cameraUrl.Content.ReadFromJsonAsync<CameraUrlResultDto>();
        cam.Should().NotBeNull();
        cam!.StreamUrl.Should().EndWith("/video");
        cam.SnapshotUrl.Should().EndWith("/snapshot");

        // Test print control endpoints (will fail to connect but should not crash)
        var pauseResult = await client.PostAsync($"/api/printers/{dto.Id}/pause", null);
        pauseResult.IsSuccessStatusCode.Should().BeTrue(); // Should return CommandResult even if operation fails

        var del = await client.DeleteAsync($"/api/printers/{dto.Id}");
        del.IsSuccessStatusCode.Should().BeTrue();
    }

    [Fact]
    public async Task Discovery_should_filter_existing_printersAsync()
    {
        var client = _factory.CreateClient();

        // Set up mock discovery service to return some test printers
        var mockDiscoveredPrinters = new List<Farm.Web.Shared.DiscoveredPrinterDto>
        {
            new()
            {
                IpAddress = "192.168.1.100",
                Port = 80,
                ServerUrl = "http://192.168.1.100:80",
                Backend = Farm.Web.Shared.PrinterBackend.PrusaLink,
                Name = "Test Printer 1",
                IsReachable = true,
                DiscoveredAt = DateTime.UtcNow
            },
            new()
            {
                IpAddress = "192.168.1.101",
                Port = 7125,
                ServerUrl = "http://192.168.1.101:7125",
                Backend = Farm.Web.Shared.PrinterBackend.Moonraker,
                Name = "Test Printer 2",
                IsReachable = true,
                DiscoveredAt = DateTime.UtcNow
            }
        };

        _factory.MockNetworkDiscoveryService
            .Setup(x => x.DiscoverPrintersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(mockDiscoveredPrinters);

        // First, create a test printer with one of the URLs that will be discovered
        var createDto = new Farm.Web.Shared.CreatePrinterDto
        {
            Name = "existing-printer",
            ServerUrl = "http://192.168.1.100:80",
            Backend = Farm.Web.Shared.PrinterBackend.PrusaLink,
            Notes = "Test existing printer"
        };

        var created = await client.PostAsJsonAsync("/api/printers", createDto);
        created.IsSuccessStatusCode.Should().BeTrue();

        var existingPrinter = await created.Content.ReadFromJsonAsync<Farm.Web.Shared.PrinterDto>();
        existingPrinter.Should().NotBeNull();

        try
        {
            // Now test the discovery endpoint
            var discoveryResponse = await client.GetAsync("/api/printers/discover");
            discoveryResponse.IsSuccessStatusCode.Should().BeTrue();

            var discoveredPrinters = await discoveryResponse.Content.ReadFromJsonAsync<Farm.Web.Shared.DiscoveredPrinterDto[]>();
            discoveredPrinters.Should().NotBeNull();

            // Should only return one printer (the second one) since the first matches existing printer
            discoveredPrinters!.Length.Should().Be(1);
            discoveredPrinters[0].ServerUrl.Should().Be("http://192.168.1.101:7125");
            discoveredPrinters[0].Name.Should().Be("Test Printer 2");

            // Verify that the existing printer is NOT in the discovered results
            var duplicatePrinter = discoveredPrinters!.FirstOrDefault(d => d.ServerUrl == existingPrinter!.ServerUrl);
            duplicatePrinter.Should().BeNull("because existing printers should be filtered out");

            // Debug: log what we got vs what we expected
            Console.WriteLine($"Expected existing URL: {existingPrinter!.ServerUrl}");
            Console.WriteLine($"Discovery returned {discoveredPrinters.Length} printers:");
            foreach (var printer in discoveredPrinters)
            {
                Console.WriteLine($"  - {printer.Name}: {printer.ServerUrl}");
            }
        }
        finally
        {
            // Clean up - delete the test printer
            var del = await client.DeleteAsync($"/api/printers/{existingPrinter!.Id}");
            del.IsSuccessStatusCode.Should().BeTrue();
        }
    }

    private record HealthzDto(string Status);
}

internal sealed record CameraUrlResultDto(string? StreamUrl, string? SnapshotUrl);
