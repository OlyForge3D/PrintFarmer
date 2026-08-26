using System.Net;
using System.Net.Http.Json;
using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Farm.Web.Api.Tests.Controllers;

[Trait("Category", "Integration")]
public class PrintersControllerLocationAuthorizationTests : IClassFixture<PrintersControllerLocationAuthorizationTests.Factory>, IAsyncLifetime
{
    public class Factory : CustomWebApplicationFactory
    {
        public Factory() : base(new Dictionary<string, string?>
        {
            ["Security:DevModeBypassAuth"] = "false"
        })
        {
        }
    }

    private readonly Factory _factory;

    private HttpClient _adminClient = null!;
    private HttpClient _nonAdminClient = null!;

    public PrintersControllerLocationAuthorizationTests(Factory factory)
    {
        _factory = factory;
    }

    public async Task InitializeAsync()
    {
        await _factory.ResetDataAsync();
        _adminClient = await _factory.CreateAdminClientAsync();
        _nonAdminClient = await _factory.CreateAuthenticatedClientAsync(
            username: "printer-location-operator",
            email: "printer-location-operator@example.com");
    }

    public Task DisposeAsync()
    {
        _adminClient.Dispose();
        _nonAdminClient.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task AssignPrinterToLocationAsync_Admin_Returns200()
    {
        (Guid printerId, Guid locationId) = await SeedPrinterAndLocationAsync();

        HttpResponseMessage response = await _adminClient.PostAsJsonAsync(
            $"/api/printers/{printerId}/location",
            new { locationId });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        await AssertPrinterLocationAsync(printerId, locationId);
    }

    [Fact]
    public async Task SummaryAsync_Unauthenticated_Returns401()
    {
        using HttpClient anonymousClient = _factory.CreateClient();

        HttpResponseMessage response = await anonymousClient.GetAsync("/api/printers/summary");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task SummaryAsync_NonAdminCannotIncludeDisabled_Returns403()
    {
        HttpResponseMessage response = await _nonAdminClient.GetAsync(
            "/api/printers/summary?includeDisabled=true");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task SummaryAsync_Admin_ReturnsMinimalContract()
    {
        HttpResponseMessage response = await _adminClient.GetAsync("/api/printers/summary");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        string body = await response.Content.ReadAsStringAsync();
        body.Should().Be("[]");
    }

    [Fact]
    public async Task SummaryAsync_Admin_ExcludesDisabledUnlessRequested()
    {
        (Guid disabledPrinterId, _) = await SeedPrinterAndLocationAsync(isEnabled: false);

        HttpResponseMessage defaultResponse = await _adminClient.GetAsync("/api/printers/summary");
        string defaultBody = await defaultResponse.Content.ReadAsStringAsync();

        HttpResponseMessage includeDisabledResponse = await _adminClient.GetAsync(
            "/api/printers/summary?includeDisabled=true");
        string includeDisabledBody = await includeDisabledResponse.Content.ReadAsStringAsync();

        defaultResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        includeDisabledResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        defaultBody.Should().NotContain(disabledPrinterId.ToString());
        includeDisabledBody.Should().Contain(disabledPrinterId.ToString());
    }

    [Fact]
    public async Task AssignPrinterToLocationAsync_NonAdmin_Returns403()
    {
        HttpResponseMessage response = await _nonAdminClient.PostAsJsonAsync(
            $"/api/printers/{Guid.NewGuid()}/location",
            new { locationId = Guid.NewGuid() });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task AssignPrinterToLocationAsync_Unauthenticated_Returns401()
    {
        using HttpClient anonymousClient = _factory.CreateClient();

        HttpResponseMessage response = await anonymousClient.PostAsJsonAsync(
            $"/api/printers/{Guid.NewGuid()}/location",
            new { locationId = Guid.NewGuid() });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task UnassignPrinterFromLocationAsync_Admin_Returns200()
    {
        (Guid printerId, Guid _) = await SeedPrinterAndLocationAsync(assignLocation: true);

        HttpResponseMessage response = await _adminClient.DeleteAsync($"/api/printers/{printerId}/location");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        await AssertPrinterLocationAsync(printerId, expectedLocationId: null);
    }

    [Fact]
    public async Task UnassignPrinterFromLocationAsync_NonAdmin_Returns403()
    {
        HttpResponseMessage response = await _nonAdminClient.DeleteAsync($"/api/printers/{Guid.NewGuid()}/location");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task UnassignPrinterFromLocationAsync_Unauthenticated_Returns401()
    {
        using HttpClient anonymousClient = _factory.CreateClient();

        HttpResponseMessage response = await anonymousClient.DeleteAsync($"/api/printers/{Guid.NewGuid()}/location");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task StartDiscoveryStreamAsync_NonAdmin_Returns403()
    {
        HttpResponseMessage response = await _nonAdminClient.PostAsJsonAsync(
            "/api/printers/discover/stream",
            new { autoRegister = false });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task StartDiscoveryStreamAsync_Unauthenticated_Returns401()
    {
        using HttpClient anonymousClient = _factory.CreateClient();

        HttpResponseMessage response = await anonymousClient.PostAsJsonAsync(
            "/api/printers/discover/stream",
            new { autoRegister = false });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task CancelDiscoveryStreamAsync_NonAdmin_Returns403()
    {
        HttpResponseMessage response = await _nonAdminClient.PostAsync(
            "/api/printers/discover/test-session/cancel",
            content: null);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task CancelDiscoveryStreamAsync_Unauthenticated_Returns401()
    {
        using HttpClient anonymousClient = _factory.CreateClient();

        HttpResponseMessage response = await anonymousClient.PostAsync(
            "/api/printers/discover/test-session/cancel",
            content: null);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    private async Task<(Guid PrinterId, Guid LocationId)> SeedPrinterAndLocationAsync(
        bool assignLocation = false,
        bool isEnabled = true)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        Manufacturer manufacturer = new()
        {
            Id = Guid.NewGuid(),
            Name = $"AuthTestManufacturer_{Guid.NewGuid():N}",
            Url = "https://example.com"
        };

        PrinterModel model = new()
        {
            Id = Guid.NewGuid(),
            ManufacturerId = manufacturer.Id,
            Name = "Auth Test Model"
        };

        Location location = new()
        {
            Id = Guid.NewGuid(),
            Name = $"Auth Test Location {Guid.NewGuid():N}",
            Depth = 0,
            Path = "/Auth Test Location"
        };

        Printer printer = new()
        {
            Id = Guid.NewGuid(),
            Name = $"Auth Test Printer {Guid.NewGuid():N}",
            ServerUrl = $"http://printer-{Guid.NewGuid():N}.test",
            Backend = (int)PrinterBackend.Moonraker,
            ModelId = model.Id,
            ManufacturerId = manufacturer.Id,
            LocationId = assignLocation ? location.Id : null,
            IsEnabled = isEnabled,
            IsAvailable = true
        };

        db.Manufacturers.Add(manufacturer);
        db.PrinterModels.Add(model);
        db.Locations.Add(location);
        db.Printers.Add(printer);
        await db.SaveChangesAsync();

        return (printer.Id, location.Id);
    }

    private async Task AssertPrinterLocationAsync(Guid printerId, Guid? expectedLocationId)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        Printer? printer = await db.Printers.FindAsync(printerId);
        printer.Should().NotBeNull();
        printer!.LocationId.Should().Be(expectedLocationId);
    }
}
