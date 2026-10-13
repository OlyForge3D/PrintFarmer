using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Cameras;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Farm.Web.Api.Tests.Controllers;

/// <summary>
/// Multi-printer camera API contract (#3329): DTO link/location fields, the location rule, link/unlink
/// with the one-primary-per-printer invariant, printer-scoped removal and the list filters.
/// </summary>
public class CameraMultiPrinterTests : IClassFixture<CustomWebApplicationFactory>, IAsyncLifetime
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly CustomWebApplicationFactory _factory;
    private HttpClient? _client;

    public CameraMultiPrinterTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private HttpClient Client => _client ?? throw new InvalidOperationException("Client not initialized.");

    public async Task InitializeAsync()
    {
        await _factory.ResetDataAsync();
        _client = await _factory.CreateAdminClientAsync();
    }

    public Task DisposeAsync()
    {
        _client?.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Create_WithSinglePrinter_DerivesLocationAndIgnoresSuppliedLocation()
    {
        Location printerLocation = await CreateLocationAsync("Bay A");
        Location otherLocation = await CreateLocationAsync("Bay B");
        Printer printer = await CreatePrinterAsync("Alpha", printerLocation.Id);

        CameraDto camera = await CreateCameraAsync(new CreateCameraDto
        {
            Name = "derived",
            StreamUrl = "http://example.com/stream",
            PrinterIds = [printer.Id],
            LocationId = otherLocation.Id,
        });

        camera.PrinterIds.Should().Equal(printer.Id);
        camera.PrinterId.Should().Be(printer.Id);
        camera.PrinterName.Should().Be("Alpha");
        camera.Printers.Should().ContainSingle().Which.IsPrimary.Should().BeTrue();
        camera.IsLocationDerived.Should().BeTrue();
        camera.LocationId.Should().BeNull();
        camera.EffectiveLocationId.Should().Be(printerLocation.Id);
        camera.EffectiveLocationName.Should().Be("Bay A");
    }

    [Fact]
    public async Task Create_WithTwoPrinters_OrdersPrimaryFirstAndAllowsExplicitLocation()
    {
        Location location = await CreateLocationAsync("Shared bay");
        Printer first = await CreatePrinterAsync("First");
        Printer second = await CreatePrinterAsync("Second");

        CameraDto camera = await CreateCameraAsync(new CreateCameraDto
        {
            Name = "shared",
            StreamUrl = "http://example.com/stream",
            PrinterIds = [first.Id, second.Id],
            LocationId = location.Id,
        });

        camera.PrinterIds.Should().Equal(first.Id, second.Id);
        camera.IsLocationDerived.Should().BeFalse();
        camera.LocationId.Should().Be(location.Id);
        camera.EffectiveLocationId.Should().Be(location.Id);
        camera.EffectiveLocationName.Should().Be("Shared bay");
    }

    [Fact]
    public async Task Create_Unlinked_AllowsExplicitLocation()
    {
        Location location = await CreateLocationAsync("Room");

        CameraDto camera = await CreateCameraAsync(new CreateCameraDto
        {
            Name = "room-cam",
            StreamUrl = "http://example.com/stream",
            LocationId = location.Id,
        });

        camera.PrinterIds.Should().BeEmpty();
        camera.IsStandalone.Should().BeTrue();
        camera.IsLocationDerived.Should().BeFalse();
        camera.LocationId.Should().Be(location.Id);
        camera.EffectiveLocationName.Should().Be("Room");
    }

    [Fact]
    public async Task Update_DiscoveredCamera_SetsUserModified_StandaloneDoesNot()
    {
        Printer printer = await CreatePrinterAsync("Moon");
        CameraDto discovered = await CreateCameraAsync(new CreateCameraDto
        {
            Name = "discovered",
            StreamUrl = "http://example.com/stream",
            PrinterIds = [printer.Id],
            Source = CameraSource.Moonraker,
        });
        CameraDto standalone = await CreateCameraAsync(new CreateCameraDto
        {
            Name = "manual",
            StreamUrl = "http://example.com/stream",
        });
        discovered.UserModified.Should().BeFalse();

        CameraDto? updatedDiscovered = await UpdateCameraAsync(discovered.Id, new UpdateCameraDto { Name = "renamed" });
        CameraDto? updatedStandalone = await UpdateCameraAsync(standalone.Id, new UpdateCameraDto { Name = "renamed-manual" });

        updatedDiscovered!.UserModified.Should().BeTrue();
        updatedStandalone!.UserModified.Should().BeFalse();
    }

    [Fact]
    public async Task LinkEndpoint_AsPrimary_KeepsExactlyOnePrimaryPerPrinter()
    {
        Printer printer = await CreatePrinterAsync("Primary test");
        CameraDto firstCamera = await CreateCameraAsync(LinkedCamera("cam-1", printer.Id));
        CameraDto secondCamera = await CreateCameraAsync(new CreateCameraDto { Name = "cam-2", StreamUrl = "http://example.com/s2" });

        HttpResponseMessage response = await Client.PostAsJsonAsync(
            $"/api/cameras/{secondCamera.Id}/printers/{printer.Id}",
            new LinkCameraPrinterRequest { IsPrimary = true },
            JsonOptions);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        CameraDto? linked = await response.Content.ReadFromJsonAsync<CameraDto>(JsonOptions);
        linked!.PrinterIds.Should().Equal(printer.Id);
        linked.Printers.Single().IsPrimary.Should().BeTrue();
        List<CameraPrinter> links = await GetPrinterLinksAsync(printer.Id);
        links.Should().HaveCount(2);
        links.Should().ContainSingle(l => l.IsPrimary).Which.CameraId.Should().Be(secondCamera.Id);
        links.Single(l => l.CameraId == firstCamera.Id).IsPrimary.Should().BeFalse();
    }

    [Fact]
    public async Task LinkEndpoint_WithoutBody_LinksAsSecondaryWhenPrinterHasPrimary()
    {
        Printer printer = await CreatePrinterAsync("No body");
        CameraDto firstCamera = await CreateCameraAsync(LinkedCamera("cam-1", printer.Id));
        CameraDto secondCamera = await CreateCameraAsync(new CreateCameraDto { Name = "cam-2", StreamUrl = "http://example.com/s2" });

        HttpResponseMessage response = await Client.PostAsync($"/api/cameras/{secondCamera.Id}/printers/{printer.Id}", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        List<CameraPrinter> links = await GetPrinterLinksAsync(printer.Id);
        links.Should().ContainSingle(l => l.IsPrimary).Which.CameraId.Should().Be(firstCamera.Id);
    }

    [Fact]
    public async Task LinkEndpoint_UnknownPrinter_Returns404()
    {
        CameraDto camera = await CreateCameraAsync(new CreateCameraDto { Name = "cam", StreamUrl = "http://example.com/s" });

        HttpResponseMessage response = await Client.PostAsync($"/api/cameras/{camera.Id}/printers/{Guid.NewGuid()}", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task UnlinkEndpoint_PrimaryCamera_PromotesNextCameraAndKeepsCamera()
    {
        Printer printer = await CreatePrinterAsync("Unlink");
        CameraDto primary = await CreateCameraAsync(LinkedCamera("primary", printer.Id, CameraSource.Moonraker));
        CameraDto secondary = await CreateCameraAsync(LinkedCamera("secondary", printer.Id));
        (await GetPrinterLinksAsync(printer.Id)).Single(l => l.IsPrimary).CameraId.Should().Be(primary.Id);

        HttpResponseMessage response = await Client.DeleteAsync($"/api/cameras/{primary.Id}/printers/{printer.Id}");

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        List<CameraPrinter> links = await GetPrinterLinksAsync(printer.Id);
        links.Should().ContainSingle().Which.Should().Match<CameraPrinter>(l => l.CameraId == secondary.Id && l.IsPrimary);
        (await CameraExistsAsync(primary.Id)).Should().BeTrue("the camera-scoped unlink never deletes the camera");
    }

    [Fact]
    public async Task UnlinkEndpoint_NotLinked_Returns404()
    {
        Printer printer = await CreatePrinterAsync("Not linked");
        CameraDto camera = await CreateCameraAsync(new CreateCameraDto { Name = "cam", StreamUrl = "http://example.com/s" });

        HttpResponseMessage response = await Client.DeleteAsync($"/api/cameras/{camera.Id}/printers/{printer.Id}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task PrinterScopedRemove_DiscoveredOrphan_IsDeleted()
    {
        Printer printer = await CreatePrinterAsync("Discovered");
        CameraDto camera = await CreateCameraAsync(LinkedCamera("discovered", printer.Id, CameraSource.Moonraker));

        HttpResponseMessage response = await Client.DeleteAsync($"/api/printers/{printer.Id}/cameras/{camera.Id}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadRemovalResultAsync(response)).Should().Be("Deleted");
        (await CameraExistsAsync(camera.Id)).Should().BeFalse();
    }

    [Fact]
    public async Task PrinterScopedRemove_DiscoveredCameraStillLinkedElsewhere_IsOnlyUnlinked()
    {
        Printer first = await CreatePrinterAsync("First");
        Printer second = await CreatePrinterAsync("Second");
        CameraDto camera = await CreateCameraAsync(new CreateCameraDto
        {
            Name = "shared-discovered",
            StreamUrl = "http://example.com/stream",
            PrinterIds = [first.Id, second.Id],
            Source = CameraSource.Moonraker,
        });

        HttpResponseMessage response = await Client.DeleteAsync($"/api/printers/{first.Id}/cameras/{camera.Id}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadRemovalResultAsync(response)).Should().Be("Unlinked");
        (await GetPrinterLinksAsync(second.Id)).Should().ContainSingle(l => l.CameraId == camera.Id);
    }

    [Fact]
    public async Task PrinterScopedRemove_StandaloneCamera_IsKeptUnlinked()
    {
        Printer printer = await CreatePrinterAsync("Standalone");
        CameraDto camera = await CreateCameraAsync(LinkedCamera("manual", printer.Id));

        HttpResponseMessage response = await Client.DeleteAsync($"/api/printers/{printer.Id}/cameras/{camera.Id}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadRemovalResultAsync(response)).Should().Be("Unlinked");
        (await CameraExistsAsync(camera.Id)).Should().BeTrue();
        (await GetPrinterLinksAsync(printer.Id)).Should().BeEmpty();
    }

    [Fact]
    public async Task PrinterScopedRemove_NotLinked_Returns404()
    {
        Printer printer = await CreatePrinterAsync("Unrelated");
        CameraDto camera = await CreateCameraAsync(new CreateCameraDto { Name = "cam", StreamUrl = "http://example.com/s" });

        HttpResponseMessage response = await Client.DeleteAsync($"/api/printers/{printer.Id}/cameras/{camera.Id}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ListCameras_FilteredByPrinterAndEffectiveLocation()
    {
        Location bay = await CreateLocationAsync("Filter bay");
        Printer inBay = await CreatePrinterAsync("In bay", bay.Id);
        Printer elsewhere = await CreatePrinterAsync("Elsewhere");
        CameraDto derived = await CreateCameraAsync(LinkedCamera("derived", inBay.Id));
        CameraDto other = await CreateCameraAsync(LinkedCamera("other", elsewhere.Id));
        CameraDto explicitLocation = await CreateCameraAsync(new CreateCameraDto
        {
            Name = "explicit",
            StreamUrl = "http://example.com/stream",
            LocationId = bay.Id,
        });

        List<CameraDto> byPrinter = await GetListAsync($"/api/cameras?printerId={elsewhere.Id}");
        List<CameraDto> byLocation = await GetListAsync($"/api/cameras?locationId={bay.Id}");
        List<CameraDto> all = await GetListAsync("/api/cameras");

        byPrinter.Select(c => c.Id).Should().Equal(other.Id);
        byLocation.Select(c => c.Id).Should().BeEquivalentTo([derived.Id, explicitLocation.Id]);
        all.Select(c => c.Id).Should().Contain([derived.Id, other.Id, explicitLocation.Id]);
    }

    private static CreateCameraDto LinkedCamera(string name, Guid printerId, CameraSource? source = null) => new()
    {
        Name = name,
        StreamUrl = $"http://example.com/{name}",
        PrinterIds = [printerId],
        Source = source,
    };

    private static async Task<string> ReadRemovalResultAsync(HttpResponseMessage response)
    {
        using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("result").GetString()!;
    }

    private async Task<List<CameraDto>> GetListAsync(string url)
    {
        HttpResponseMessage response = await Client.GetAsync(url);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<List<CameraDto>>(JsonOptions))!;
    }

    private async Task<CameraDto> CreateCameraAsync(CreateCameraDto dto)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        ICameraService service = scope.ServiceProvider.GetRequiredService<ICameraService>();
        return await service.CreateAsync(dto, CancellationToken.None);
    }

    private async Task<CameraDto?> UpdateCameraAsync(Guid id, UpdateCameraDto dto)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        ICameraService service = scope.ServiceProvider.GetRequiredService<ICameraService>();
        return await service.UpdateAsync(id, dto, CancellationToken.None);
    }

    private async Task<List<CameraPrinter>> GetPrinterLinksAsync(Guid printerId)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Set<CameraPrinter>().AsNoTracking().Where(l => l.PrinterId == printerId).ToListAsync();
    }

    private async Task<bool> CameraExistsAsync(Guid cameraId)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Cameras.AnyAsync(c => c.Id == cameraId);
    }

    private async Task<Location> CreateLocationAsync(string name)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var location = new Location { Id = Guid.NewGuid(), Name = name, Path = $"/{Guid.NewGuid():N}" };
        db.Locations.Add(location);
        await db.SaveChangesAsync();
        return location;
    }

    private async Task<Printer> CreatePrinterAsync(string name, Guid? locationId = null)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Manufacturer? manufacturer = await db.Manufacturers.FirstOrDefaultAsync();
        if (manufacturer is null)
        {
            manufacturer = new Manufacturer { Id = Guid.NewGuid(), Name = "Test Manufacturer" };
            db.Manufacturers.Add(manufacturer);
        }

        PrinterModel? model = await db.PrinterModels.FirstOrDefaultAsync();
        if (model is null)
        {
            model = new PrinterModel { Id = Guid.NewGuid(), Name = "Test Model", ManufacturerId = manufacturer.Id };
            db.PrinterModels.Add(model);
        }

        var printer = new Printer
        {
            Id = Guid.NewGuid(),
            Name = name,
            ServerUrl = $"http://printer-{Guid.NewGuid():N}.local",
            BackendPort = 7125,
            Backend = (int)PrinterBackend.Moonraker,
            ManufacturerId = manufacturer.Id,
            ModelId = model.Id,
            LocationId = locationId,
            IsEnabled = true,
        };
        db.Printers.Add(printer);
        await db.SaveChangesAsync();
        return printer;
    }
}
