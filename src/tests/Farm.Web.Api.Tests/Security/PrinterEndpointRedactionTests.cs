using System.Net;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Web.Api.Tests.TestInfrastructure;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Farm.Web.Api.Tests.Security;

[Trait("Category", "Integration")]
[Collection(IntegrationTestCollection.Name)]
public sealed class PrinterEndpointRedactionTests
{
    [Fact]
    public async Task PrinterConfigurationAndCameraRoutes_DoNotExposeStoredSecrets()
    {
        using var factory = new CustomWebApplicationFactory();
        await factory.ResetDatabaseAsync();
        using HttpClient client = await factory.CreateAdminClientAsync();
        using HttpClient viewOnlyClient = await factory.CreateAuthenticatedClientAsync(
            username: "printer-viewer",
            email: "printer-viewer@example.com");
        Guid printerId = Guid.NewGuid();

        await using (AsyncServiceScope scope = factory.Services.CreateAsyncScope())
        {
            AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var manufacturer = new Manufacturer
            {
                Id = Guid.NewGuid(),
                Name = "Redaction Test Manufacturer",
            };
            var model = new PrinterModel
            {
                Id = Guid.NewGuid(),
                Name = "Redaction Test Model",
                ManufacturerId = manufacturer.Id,
            };
            var printer = new Printer
            {
                Id = printerId,
                Name = "private printer",
                ServerUrl = "http://embedded-user:embedded-password@printer.internal:7125?token=printer-token#private",
                OriginalServerUrl = "http://printer-original.internal:7125",
                ApiKey = "printer-api-key",
                Username = "printer-user",
                Password = "printer-password",
                Backend = 0,
                BackendPort = 7125,
                IsEnabled = true,
                ManufacturerId = manufacturer.Id,
                ModelId = model.Id,
            };
            var camera = new Camera
            {
                Id = Guid.NewGuid(),
                Name = "private camera",
                PrinterId = printerId,
                StreamUrl = "http://camera.internal/stream?token=camera-secret",
                SnapshotUrl = "http://camera.internal/snapshot?token=camera-secret",
                IsEnabled = true,
            };
            _ = db.Manufacturers.Add(manufacturer);
            _ = db.PrinterModels.Add(model);
            _ = db.Printers.Add(printer);
            _ = db.Cameras.Add(camera);
            await db.SaveChangesAsync();
        }

        HttpResponseMessage configResponse =
            await client.GetAsync($"/api/printers/{printerId:D}/config");
        HttpResponseMessage detailsResponse =
            await client.GetAsync($"/api/printers/{printerId:D}/details");
        HttpResponseMessage cameraResponse =
            await client.GetAsync($"/api/printers/{printerId:D}/camera/url");
        HttpResponseMessage cameraListResponse =
            await client.GetAsync("/api/printers/camera-urls");
        HttpResponseMessage printerListResponse =
            await client.GetAsync("/api/printers");
        HttpResponseMessage viewOnlyDetailsResponse =
            await viewOnlyClient.GetAsync($"/api/printers/{printerId:D}/details");

        _ = configResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        _ = detailsResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        _ = cameraResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        _ = cameraListResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        _ = printerListResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        _ = viewOnlyDetailsResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        string redactedRoutesJson = string.Join(
            Environment.NewLine,
            await configResponse.Content.ReadAsStringAsync(),
            await cameraResponse.Content.ReadAsStringAsync(),
            await cameraListResponse.Content.ReadAsStringAsync());
        string detailsJson = await detailsResponse.Content.ReadAsStringAsync();
        string printerListJson = await printerListResponse.Content.ReadAsStringAsync();
        string viewOnlyDetailsJson = await viewOnlyDetailsResponse.Content.ReadAsStringAsync();

        AssertSecretsAreAbsent(redactedRoutesJson, includePrinterHost: true);
        AssertSecretsAreAbsent(detailsJson, includePrinterHost: false);
        AssertSecretsAreAbsent(printerListJson, includePrinterHost: false);
        _ = redactedRoutesJson.Should().Contain("\"serverConfigured\":true");
        _ = redactedRoutesJson.Should().Contain("\"apiKeyConfigured\":true");
        _ = redactedRoutesJson.Should().Contain($"/api/printers/{printerId:D}/camera/stream");
        _ = redactedRoutesJson.Should().Contain($"/api/printers/{printerId:D}/camera/snapshot");
        _ = detailsJson.Should().Contain("\"serverUrl\":\"http://printer.internal:7125\"");
        _ = printerListJson.Should().Contain("\"frontendUrl\":\"http://printer.internal:7125\"");
        _ = viewOnlyDetailsJson.Should().NotContain("\"serverUrl\"");
        _ = viewOnlyDetailsJson.Should().NotContain("printer.internal");
    }

    [Fact]
    public async Task PrinterConfiguration_RequiresFarmAdministrator()
    {
        using var factory = new CustomWebApplicationFactory();
        await factory.ResetDatabaseAsync();
        using HttpClient client = await factory.CreateAuthenticatedClientAsync();

        HttpResponseMessage response =
            await client.GetAsync($"/api/printers/{Guid.NewGuid():D}/config");

        _ = response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    private static void AssertSecretsAreAbsent(string json, bool includePrinterHost)
    {
        List<string> secrets =
        [
            "printer-original.internal",
            "printer-api-key",
            "printer-user",
            "printer-password",
            "embedded-user",
            "embedded-password",
            "printer-token",
            "#private",
            "camera.internal",
            "camera-secret",
        ];
        if (includePrinterHost)
        {
            secrets.Add("printer.internal");
        }

        foreach (string secret in secrets)
        {
            _ = json.Should().NotContain(secret);
        }
    }
}
