using System.Net;
using System.Net.Http.Json;
using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Discovery;
using Farm.Web.Api.Tests.TestInfrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Farm.Web.Api.Tests.Security;

[Trait("Category", "Integration")]
[Collection(IntegrationTestCollection.Name)]
public sealed class DiscoveryRegistrationSecurityTests
{
    [Fact]
    public async Task RegisterDiscoveryResult_UsesServerSideTargetAndConsumesOpaqueIdentifier()
    {
        using var factory = new CustomWebApplicationFactory();
        await factory.ResetDatabaseAsync();
        using HttpClient client = await factory.CreateAdminClientAsync();
        const string sessionId = "secure-registration-session";
        const string serverUrl = "http://printer.internal:7125";

        IDiscoverySessionRegistry sessions =
            factory.Services.GetRequiredService<IDiscoverySessionRegistry>();
        sessions.RegisterSession(sessionId, Guid.NewGuid());
        DiscoveryPrinterFoundDto found = sessions.StorePrinter(
            new InternalDiscoveryPrinterFoundDto(
                SessionId: sessionId,
                Name: "Secure Discovery Printer",
                ServerUrl: serverUrl,
                OriginalServerUrl: null,
                IpAddress: "192.168.1.20",
                Backend: PrinterBackend.Moonraker,
                BackendPort: 7125,
                FrontendPort: 80,
                CameraStreamUrl: "http://camera.internal/stream",
                CameraSnapshotUrl: "http://camera.internal/snapshot",
                Manufacturer: null,
                Model: null,
                Notes: null,
                DiscoveredAt: DateTime.UtcNow,
                IsReachable: true))!;
        var request = new RegisterDiscoveredPrinterRequest(
            found.Printer.DiscoveryId,
            NewManufacturerName: "Secure Discovery Manufacturer",
            NewModelName: "Secure Discovery Model");

        HttpResponseMessage response = await client.PostAsJsonAsync(
            $"/api/printers/discover/{sessionId}/register",
            request);
        HttpResponseMessage replayResponse = await client.PostAsJsonAsync(
            $"/api/printers/discover/{sessionId}/register",
            request);

        _ = response.StatusCode.Should().Be(HttpStatusCode.Created);
        _ = replayResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
        string responseJson = await response.Content.ReadAsStringAsync();
        _ = responseJson.Should().NotContain("printer.internal");
        _ = responseJson.Should().NotContain("192.168.1.20");
        _ = responseJson.Should().NotContain("camera.internal");

        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Printer stored = await db.Printers.SingleAsync(
            printer => printer.Name == "Secure Discovery Printer");
        _ = stored.ServerUrl.Should().Be(serverUrl);
    }

    [Fact]
    public async Task RegisterDiscoveryResult_RequiresFarmAdministrator()
    {
        using var factory = new CustomWebApplicationFactory();
        await factory.ResetDatabaseAsync();
        using HttpClient client = await factory.CreateAuthenticatedClientAsync();

        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/printers/discover/session/register",
            new RegisterDiscoveredPrinterRequest(Guid.NewGuid()));

        _ = response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
