using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Farm.Web.Api.Tests.Controllers;

/// <summary>
/// Integration tests verifying that PUT /api/printers/{id} validates BuddyCameraIp
/// against injection payloads and correctly handles the clear (empty-string) case.
/// </summary>
[Trait("Category", "Integration")]
public class PrintersControllerBuddyCameraIpTests : IAsyncLifetime
{
    private readonly CustomWebApplicationFactory _factory;
    private HttpClient? _client;

    public PrintersControllerBuddyCameraIpTests()
    {
        _factory = new CustomWebApplicationFactory();
    }

    public async Task InitializeAsync()
    {
        _client = await _factory.CreateAdminClientAsync();
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        await _factory.DisposeAsync();
    }

    private async Task<Guid> SeedPrinterAsync()
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        string suffix = Guid.NewGuid().ToString("N")[..8];

        var manufacturer = new Manufacturer { Id = Guid.NewGuid(), Name = $"Mfr-{suffix}" };
        db.Manufacturers.Add(manufacturer);
        await db.SaveChangesAsync();

        var model = new PrinterModel
        {
            Id = Guid.NewGuid(),
            Name = $"Model-{suffix}",
            ManufacturerId = manufacturer.Id
        };
        db.PrinterModels.Add(model);
        await db.SaveChangesAsync();

        var printer = new Printer
        {
            Id = Guid.NewGuid(),
            Name = $"printer-{suffix}",
            ServerUrl = $"http://printer-{suffix}.local",
            ManufacturerId = manufacturer.Id,
            ModelId = model.Id,
        };
        db.Printers.Add(printer);
        await db.SaveChangesAsync();

        return printer.Id;
    }

    [Fact]
    public async Task UpdatePrinter_BuddyCameraIp_WhenValidIp_ReturnsOk()
    {
        Guid id = await SeedPrinterAsync();
        var dto = new UpdatePrinterDto(BuddyCameraIp: "192.168.1.100");

        HttpResponseMessage response = await _client!.PutAsJsonAsync($"/api/printers/{id}", dto);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task UpdatePrinter_BuddyCameraIp_WhenValidHostname_ReturnsOk()
    {
        Guid id = await SeedPrinterAsync();
        var dto = new UpdatePrinterDto(BuddyCameraIp: "myprinter.local");

        HttpResponseMessage response = await _client!.PutAsJsonAsync($"/api/printers/{id}", dto);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task UpdatePrinter_BuddyCameraIp_WhenEmptyString_ReturnsOkAndClearsCamera()
    {
        Guid id = await SeedPrinterAsync();
        var dto = new UpdatePrinterDto(BuddyCameraIp: "");

        HttpResponseMessage response = await _client!.PutAsJsonAsync($"/api/printers/{id}", dto);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData("http://192.168.1.100/snapshot")]
    [InlineData("rtsp://192.168.1.100/live")]
    [InlineData("file:///etc/passwd")]
    public async Task UpdatePrinter_BuddyCameraIp_WhenContainsScheme_ReturnsBadRequest(string injection)
    {
        Guid id = await SeedPrinterAsync();
        var dto = new UpdatePrinterDto(BuddyCameraIp: injection);

        HttpResponseMessage response = await _client!.PutAsJsonAsync($"/api/printers/{id}", dto);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData("192.168.1.100/snapshot")]
    [InlineData("192.168.1.100\\share")]
    [InlineData("user@192.168.1.100")]
    [InlineData("192.168.1.100?query=1")]
    [InlineData("192.168.1.100#frag")]
    public async Task UpdatePrinter_BuddyCameraIp_WhenContainsInjectionChars_ReturnsBadRequest(string injection)
    {
        Guid id = await SeedPrinterAsync();
        var dto = new UpdatePrinterDto(BuddyCameraIp: injection);

        HttpResponseMessage response = await _client!.PutAsJsonAsync($"/api/printers/{id}", dto);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task UpdatePrinter_BuddyCameraIp_WhenBadRequest_ResponseBodyDoesNotEchoInput()
    {
        Guid id = await SeedPrinterAsync();
        string injection = "http://evil.example.com/payload";
        var dto = new UpdatePrinterDto(BuddyCameraIp: injection);

        HttpResponseMessage response = await _client!.PutAsJsonAsync($"/api/printers/{id}", dto);
        string body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        body.Should().NotContain(injection, because: "raw user input must not be reflected in error responses");
    }
}
