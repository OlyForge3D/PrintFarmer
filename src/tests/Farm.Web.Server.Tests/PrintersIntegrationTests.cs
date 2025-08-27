using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Farm.Web.Server.Tests;

public class PrintersIntegrationTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public PrintersIntegrationTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Healthz_should_return_ok()
    {
        var client = _factory.CreateClient();
        var resp = await client.GetAsync("/healthz");
        resp.EnsureSuccessStatusCode();
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
    public async Task Create_PrusaLink_with_ApiKey_then_delete()
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

    private record HealthzDto(string status);
}
