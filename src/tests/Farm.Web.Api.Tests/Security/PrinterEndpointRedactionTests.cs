using System.Net;
using System.Text.Json;
using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Printers;
using Farm.Web.Api.Tests.TestInfrastructure;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Farm.Web.Api.Tests.Security;

[Trait("Category", "Integration")]
[Collection(IntegrationTestCollection.Name)]
public sealed class PrinterEndpointRedactionTests
{
    [Fact]
    public async Task PrinterList_ProjectsOnlyStoredNumericIp_WithoutChangingVisibilityOrRedaction()
    {
        using var factory = CustomWebApplicationFactory.CreateWithIsolatedDatabase();
        using HttpClient admin = await factory.CreateAdminClientAsync();
        using HttpClient viewer = await factory.CreateAuthenticatedClientAsync(
            username: "ip-projection-viewer",
            email: "ip-projection-viewer@example.com");
        using HttpClient anonymous = factory.CreateClient();
        (string Address, string? ExpectedIp, bool Online, bool Disabled, double? TimeLeft)[] cases =
        [
            ("http://192.0.2.10:7125", "192.0.2.10", true, false, null),
            ("https://[2001:db8::10]:8443", "2001:db8::10", false, false, null),
            ("http://embedded-user:embedded-password@192.0.2.11:7125/path?token=printer-token#private", "192.0.2.11", true, false, null),
            ("http://embedded-user:embedded-password@[2001:db8::11]:7125/path?token=printer-token#private", "2001:db8::11", true, false, null),
            ("http://printer.internal:7125", null, false, false, null),
            ("not a URL", null, false, false, null),
            ("", null, false, false, null),
            ("file://192.0.2.12/private", null, false, false, null),
            ("http://192.0.2.13:7125", "192.0.2.13", false, true, null),
            ("http://192.0.2.14:7125", "192.0.2.14", true, false, double.MaxValue),
        ];
        var printerIds = new List<Guid>();

        await using (AsyncServiceScope scope = factory.Services.CreateAsyncScope())
        {
            AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            IPrinterStatusCacheWriter statuses = scope.ServiceProvider.GetRequiredService<IPrinterStatusCacheWriter>();
            var manufacturer = new Manufacturer { Id = Guid.NewGuid(), Name = "IP projection manufacturer" };
            var model = new PrinterModel
            {
                Id = Guid.NewGuid(),
                Name = "IP projection model",
                ManufacturerId = manufacturer.Id,
            };
            _ = db.Manufacturers.Add(manufacturer);
            _ = db.PrinterModels.Add(model);
            foreach (var testCase in cases)
            {
                Guid id = Guid.NewGuid();
                printerIds.Add(id);
                _ = db.Printers.Add(new Printer
                {
                    Id = id,
                    Name = $"IP projection {printerIds.Count}",
                    ServerUrl = testCase.Address,
                    OriginalServerUrl = "http://printer-original.internal:7125",
                    ApiKey = "printer-api-key",
                    Backend = (int)PrinterBackend.Moonraker,
                    BackendPort = 7125,
                    IsEnabled = !testCase.Disabled,
                    ManufacturerId = manufacturer.Id,
                    ModelId = model.Id,
                });
                if (testCase.Online)
                {
                    // Out-of-range completion time exercises the existing exception fallback.
                    statuses.UpdateStatus(new PrinterStatusDto(
                        id, IsOnline: true, State: "idle", PrintTimeLeftSeconds: testCase.TimeLeft));
                }
            }
            await db.SaveChangesAsync();
        }

        using HttpResponseMessage adminResponse = await admin.GetAsync("/api/printers?includeDisabled=true");
        adminResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        string json = await adminResponse.Content.ReadAsStringAsync();
        using JsonDocument document = JsonDocument.Parse(json);
        document.RootElement.GetArrayLength().Should().Be(cases.Length);
        for (int i = 0; i < cases.Length; i++)
        {
            JsonElement printer = document.RootElement.EnumerateArray()
                .Single(item => item.GetProperty("id").GetGuid() == printerIds[i]);
            string? actualIp = printer.TryGetProperty("ipAddress", out JsonElement ip) ? ip.GetString() : null;
            actualIp.Should().Be(cases[i].ExpectedIp, "only the stored numeric host is an IP");
            printer.GetProperty("isOnline").GetBoolean().Should()
                .Be(cases[i].Online && cases[i].TimeLeft == null);
            printer.TryGetProperty("backendUrl", out _).Should().BeFalse();
            printer.TryGetProperty("originalServerUrl", out _).Should().BeFalse();
            printer.TryGetProperty("apiKey", out _).Should().BeFalse();
        }
        AssertSecretsAreAbsent(json, includePrinterHost: false);

        using HttpResponseMessage viewerResponse = await viewer.GetAsync("/api/printers");
        viewerResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        using JsonDocument viewerDocument = JsonDocument.Parse(await viewerResponse.Content.ReadAsStringAsync());
        viewerDocument.RootElement.GetArrayLength().Should().Be(cases.Count(item => !item.Disabled));
        viewerDocument.RootElement.EnumerateArray().Should().NotContain(
            item => item.GetProperty("id").GetGuid() == printerIds[8]);
        viewerDocument.RootElement.EnumerateArray()
            .Single(item => item.GetProperty("id").GetGuid() == printerIds[0])
            .GetProperty("ipAddress").GetString().Should().Be("192.0.2.10");

        using HttpResponseMessage disabledResponse = await viewer.GetAsync("/api/printers?includeDisabled=true");
        disabledResponse.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        using HttpResponseMessage anonymousResponse = await anonymous.GetAsync("/api/printers");
        anonymousResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task PrinterConfigurationAndCameraRoutes_ExposePasswordOnlyInAdminDetails()
    {
        using var factory = new CustomWebApplicationFactory();
        await factory.ResetDataAsync();
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
                ApiKey = "printer-password",
                Username = "maker",
                Password = null,
                Backend = (int)PrinterBackend.PrusaLink,
                BackendPort = 7125,
                IsEnabled = true,
                ManufacturerId = manufacturer.Id,
                ModelId = model.Id,
            };
            var camera = new Camera
            {
                Id = Guid.NewGuid(),
                Name = "private camera",
                PrinterLinks = { new CameraPrinter { PrinterId = printerId, IsPrimary = true } },
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
        HttpResponseMessage printerResponse =
            await client.GetAsync($"/api/printers/{printerId:D}");
        HttpResponseMessage viewOnlyPrinterResponse =
            await viewOnlyClient.GetAsync($"/api/printers/{printerId:D}");
        HttpResponseMessage viewOnlyDetailsResponse =
            await viewOnlyClient.GetAsync($"/api/printers/{printerId:D}/details");

        _ = configResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        _ = detailsResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        _ = cameraResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        _ = cameraListResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        _ = printerListResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        _ = printerResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        _ = viewOnlyPrinterResponse.StatusCode.Should().Be(HttpStatusCode.OK);
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
        AssertSecretsAreAbsent(detailsJson, includePrinterHost: false, allowPrinterPassword: true);
        AssertSecretsAreAbsent(printerListJson, includePrinterHost: false);
        AssertSecretsAreAbsent(viewOnlyDetailsJson, includePrinterHost: false);
        _ = redactedRoutesJson.Should().Contain("\"serverConfigured\":true");
        _ = redactedRoutesJson.Should().Contain("\"apiKeyConfigured\":true");
        _ = redactedRoutesJson.Should().Contain($"/api/printers/{printerId:D}/camera/stream");
        _ = redactedRoutesJson.Should().Contain($"/api/printers/{printerId:D}/camera/snapshot");
        _ = detailsJson.Should().Contain("\"serverUrl\":\"http://printer.internal:7125\"");
        _ = detailsJson.Should().Contain("\"password\":\"printer-password\"");
        _ = detailsJson.Should().Contain("\"passwordConfigured\":true");
        _ = detailsResponse.Headers.CacheControl?.NoStore.Should().BeTrue();
        _ = printerListJson.Should().Contain("\"frontendUrl\":\"http://printer.internal:7125\"");
        foreach (HttpResponseMessage response in new[] { printerResponse, viewOnlyPrinterResponse })
        {
            string printerJson = await response.Content.ReadAsStringAsync();
            AssertSecretsAreAbsent(printerJson, includePrinterHost: false);
            _ = printerJson.Should().Contain("\"frontendUrl\":\"http://printer.internal:7125\"");
            _ = printerJson.Should().NotContain("\"backendUrl\"").And.NotContain("\"originalServerUrl\"");
        }
        _ = viewOnlyDetailsJson.Should().NotContain("\"serverUrl\"");
        _ = viewOnlyDetailsJson.Should().NotContain("printer.internal");
    }

    [Fact]
    public async Task PrinterConfiguration_RequiresFarmAdministrator()
    {
        using var factory = new CustomWebApplicationFactory();
        await factory.ResetDataAsync();
        using HttpClient client = await factory.CreateAuthenticatedClientAsync();

        HttpResponseMessage response =
            await client.GetAsync($"/api/printers/{Guid.NewGuid():D}/config");

        _ = response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    private static void AssertSecretsAreAbsent(
        string json,
        bool includePrinterHost,
        bool allowPrinterPassword = false)
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

        if (allowPrinterPassword)
        {
            _ = secrets.Remove("printer-password");
        }

        foreach (string secret in secrets)
        {
            _ = json.Should().NotContain(secret);
        }
    }
}
