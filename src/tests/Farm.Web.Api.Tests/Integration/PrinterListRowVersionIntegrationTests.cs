using System.Net;
using System.Text.Json;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Web.Api.Tests.TestInfrastructure;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Farm.Web.Api.Tests.Integration;

/// <summary>
/// Regression tests for the HTTP printer list concurrency-token contract.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public class PrinterListRowVersionIntegrationTests : IAsyncLifetime
{
    private readonly CustomWebApplicationFactory _factory;
    private HttpClient? _adminClient;

    public PrinterListRowVersionIntegrationTests()
    {
        _factory = CustomWebApplicationFactory.CreateWithIsolatedDatabase();
    }

    public async Task InitializeAsync()
    {
        _adminClient = await _factory.CreateAdminClientAsync();
    }

    public async Task DisposeAsync()
    {
        _adminClient?.Dispose();
        await _factory.DisposeAsync();
    }

    [Fact]
    public async Task GetPrinters_SerializesCamelCaseRowVersionMatchingSinglePrinterEndpoint()
    {
        Guid printerId = await SeedPrinterAsync();

        HttpResponseMessage listResponse = await _adminClient!.GetAsync("/api/printers");
        HttpResponseMessage singleResponse = await _adminClient.GetAsync($"/api/printers/{printerId}");

        listResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        singleResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        string listJson = await listResponse.Content.ReadAsStringAsync();
        string singleJson = await singleResponse.Content.ReadAsStringAsync();

        using JsonDocument listDocument = JsonDocument.Parse(listJson);
        using JsonDocument singleDocument = JsonDocument.Parse(singleJson);

        JsonElement printerJson = listDocument.RootElement.EnumerateArray()
            .Single(element => element.GetProperty("id").GetGuid() == printerId);

        printerJson.TryGetProperty("rowVersion", out JsonElement listRowVersionElement).Should().BeTrue();
        printerJson.TryGetProperty("RowVersion", out _).Should().BeFalse();

        string? listRowVersion = listRowVersionElement.GetString();
        listRowVersion.Should().NotBeNullOrWhiteSpace();

        Action decode = () => Convert.FromBase64String(listRowVersion!);
        decode.Should().NotThrow();

        string? singleRowVersion = singleDocument.RootElement.GetProperty("rowVersion").GetString();
        listRowVersion.Should().Be(singleRowVersion,
            "GET /api/printers and GET /api/printers/{id} must expose the same concurrency token");
    }

    private async Task<Guid> SeedPrinterAsync()
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        Guid manufacturerId = Guid.NewGuid();
        Guid modelId = Guid.NewGuid();
        Guid printerId = Guid.NewGuid();

        db.Manufacturers.Add(new Manufacturer
        {
            Id = manufacturerId,
            Name = "HTTP RowVersion Mfg",
        });
        db.PrinterModels.Add(new PrinterModel
        {
            Id = modelId,
            ManufacturerId = manufacturerId,
            Name = "HTTP RowVersion Model",
        });
        db.Printers.Add(new Printer
        {
            Id = printerId,
            Name = "HTTP RowVersion Printer",
            ServerUrl = "http://192.168.1.61",
            BackendPort = 7125,
            Backend = (int)PrinterBackend.Moonraker,
            ManufacturerId = manufacturerId,
            ModelId = modelId,
        });
        await db.SaveChangesAsync();

        return printerId;
    }
}
