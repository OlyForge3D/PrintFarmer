using System.Net;
using System.Net.Http.Json;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Dtos;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Farm.Web.Api.Tests.Integration;

/// <summary>
/// Integration tests for <c>GET /api/system/farm-shape</c> (issue #2411). Covers the hard
/// constraints from the issue: authenticated-only, non-admin accountCount visibility, and
/// printerCount/locationCount agreement with what the caller's own list endpoints return.
/// </summary>
public class FarmShapeIntegrationTests : IClassFixture<FarmShapeIntegrationTests.Factory>, IAsyncLifetime
{
    public class Factory : CustomWebApplicationFactory
    {
        public Factory()
            : base(new Dictionary<string, string?> { ["Security:DevModeBypassAuth"] = "false" })
        {
        }
    }

    private readonly Factory _factory;
    private HttpClient _adminClient = null!;
    private HttpClient _nonAdminClient = null!;

    public FarmShapeIntegrationTests(Factory factory)
    {
        _factory = factory;
    }

    public async Task InitializeAsync()
    {
        await _factory.ResetDataAsync();
        _adminClient = await _factory.CreateAdminClientAsync();
        _nonAdminClient = await _factory.CreateAuthenticatedClientAsync(
            username: "farm-shape-operator",
            email: "farm-shape-operator@example.com");
    }

    public Task DisposeAsync()
    {
        _adminClient.Dispose();
        _nonAdminClient.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task GetFarmShapeAsync_Unauthenticated_Returns401()
    {
        using HttpClient anonymousClient = _factory.CreateClient();

        HttpResponseMessage response = await anonymousClient.GetAsync("/api/system/farm-shape");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetFarmShapeAsync_NonAdmin_Returns200WithUsableAccountCount()
    {
        // Regression guard: accountCount must NOT be admin-gated. A non-admin caller
        // (the operator client created in InitializeAsync) must get a plain, non-zero,
        // usable integer — never a 403 from accidentally inheriting UsersController's
        // class-level [RequirePermission("users", "admin")].
        HttpResponseMessage response = await _nonAdminClient.GetAsync("/api/system/farm-shape");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        FarmShapeDto? dto = await response.Content.ReadFromJsonAsync<FarmShapeDto>();
        dto.Should().NotBeNull();
        dto!.AccountCount.Should().BeGreaterThan(0, "at least the operator account itself exists");
    }

    [Fact]
    public async Task GetFarmShapeAsync_SetsCacheControlNoStore()
    {
        HttpResponseMessage response = await _adminClient.GetAsync("/api/system/farm-shape");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.CacheControl.Should().NotBeNull();
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
    }

    [Fact]
    public async Task GetFarmShapeAsync_LocationCount_MatchesLocationsListForBothRoles()
    {
        await SeedLocationAsync();
        await SeedLocationAsync();

        HttpResponseMessage adminLocations = await _adminClient.GetAsync("/api/locations");
        List<FarmShapeLocationSummary>? adminLocationList =
            await adminLocations.Content.ReadFromJsonAsync<List<FarmShapeLocationSummary>>();

        HttpResponseMessage nonAdminLocations = await _nonAdminClient.GetAsync("/api/locations");
        List<FarmShapeLocationSummary>? nonAdminLocationList =
            await nonAdminLocations.Content.ReadFromJsonAsync<List<FarmShapeLocationSummary>>();

        FarmShapeDto adminShape = await GetFarmShapeAsync(_adminClient);
        FarmShapeDto nonAdminShape = await GetFarmShapeAsync(_nonAdminClient);

        adminShape.LocationCount.Should().Be(adminLocationList!.Count);
        nonAdminShape.LocationCount.Should().Be(nonAdminLocationList!.Count);
        adminShape.LocationCount.Should().Be(nonAdminShape.LocationCount,
            "locations are unscoped for any authenticated caller");
    }

    [Fact]
    public async Task GetFarmShapeAsync_LocationCount_ExcludesSoftDeletedLocations()
    {
        // GET /api/locations (LocationService.GetAllDtosAsync -> EfLocationRepository.GetAllAsync)
        // filters Where(l => l.IsActive), excluding soft-deleted rows. farm-shape's locationCount
        // must agree, or it would over-count relative to what the caller's own list actually shows.
        await SeedLocationAsync();
        await SeedLocationAsync(isActive: false);

        HttpResponseMessage listResponse = await _adminClient.GetAsync("/api/locations");
        List<FarmShapeLocationSummary>? locations =
            await listResponse.Content.ReadFromJsonAsync<List<FarmShapeLocationSummary>>();

        FarmShapeDto shape = await GetFarmShapeAsync(_adminClient);

        shape.LocationCount.Should().Be(locations!.Count,
            "farm-shape locationCount must agree with what GET /api/locations actually returns, " +
            "which excludes soft-deleted (IsActive == false) locations");
    }

    [Fact]
    public async Task GetFarmShapeAsync_PrinterCount_Admin_MatchesUnfilteredPrinterList()
    {
        (Guid enabledPrinterId, _) = await SeedPrinterAsync(isEnabled: true);
        (Guid disabledPrinterId, _) = await SeedPrinterAsync(isEnabled: false);

        HttpResponseMessage listResponse = await _adminClient.GetAsync("/api/printers");
        List<FarmShapePrinterSummary>? printers =
            await listResponse.Content.ReadFromJsonAsync<List<FarmShapePrinterSummary>>();

        FarmShapeDto shape = await GetFarmShapeAsync(_adminClient);

        printers.Should().Contain(p => p.Id == enabledPrinterId);
        printers.Should().Contain(p => p.Id == disabledPrinterId, "admins see disabled printers too");
        shape.PrinterCount.Should().Be(printers!.Count);
    }

    [Fact]
    public async Task GetFarmShapeAsync_PrinterCount_NonAdmin_ExcludesDisabledAndRestrictedGroups()
    {
        (Guid _, _) = await SeedPrinterAsync(isEnabled: true);
        (Guid _, _) = await SeedPrinterAsync(isEnabled: false);
        Guid restrictedGroupId = await SeedPrinterGroupAsync();
        (Guid restrictedPrinterId, _) = await SeedPrinterAsync(isEnabled: true, printerGroupId: restrictedGroupId);
        Guid otherRoleId = await SeedRoleAsync("some-other-role");
        await SeedPrinterGroupAccessAsync(restrictedGroupId, otherRoleId, PrinterGroupAccessLevel.View);

        HttpResponseMessage listResponse = await _nonAdminClient.GetAsync("/api/printers");
        List<FarmShapePrinterSummary>? printers =
            await listResponse.Content.ReadFromJsonAsync<List<FarmShapePrinterSummary>>();

        FarmShapeDto shape = await GetFarmShapeAsync(_nonAdminClient);

        printers.Should().NotContain(p => p.Id == restrictedPrinterId,
            "restricted group has no rule granting the caller's role View access");
        shape.PrinterCount.Should().Be(printers!.Count,
            "farm-shape printerCount must agree with what GET /api/printers actually returns for this caller");
    }

    private static async Task<FarmShapeDto> GetFarmShapeAsync(HttpClient client)
    {
        HttpResponseMessage response = await client.GetAsync("/api/system/farm-shape");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        FarmShapeDto? dto = await response.Content.ReadFromJsonAsync<FarmShapeDto>();
        dto.Should().NotBeNull();
        return dto!;
    }

    private async Task SeedLocationAsync(bool isActive = true)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        db.Locations.Add(new Location
        {
            Id = Guid.NewGuid(),
            Name = $"FarmShape Test Location {Guid.NewGuid():N}",
            Depth = 0,
            Path = "/FarmShape Test Location",
            IsActive = isActive
        });
        await db.SaveChangesAsync();
    }

    private async Task<(Guid PrinterId, Guid LocationId)> SeedPrinterAsync(
        bool isEnabled = true,
        Guid? printerGroupId = null)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        Manufacturer manufacturer = new()
        {
            Id = Guid.NewGuid(),
            Name = $"FarmShapeTestManufacturer_{Guid.NewGuid():N}",
            Url = "https://example.com"
        };

        PrinterModel model = new()
        {
            Id = Guid.NewGuid(),
            ManufacturerId = manufacturer.Id,
            Name = "FarmShape Test Model"
        };

        Location location = new()
        {
            Id = Guid.NewGuid(),
            Name = $"FarmShape Test Location {Guid.NewGuid():N}",
            Depth = 0,
            Path = "/FarmShape Test Location"
        };

        Printer printer = new()
        {
            Id = Guid.NewGuid(),
            Name = $"FarmShape Test Printer {Guid.NewGuid():N}",
            ServerUrl = $"http://printer-{Guid.NewGuid():N}.test",
            Backend = (int)PrinterBackend.Moonraker,
            ModelId = model.Id,
            ManufacturerId = manufacturer.Id,
            LocationId = location.Id,
            PrinterGroupId = printerGroupId,
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

    private async Task<Guid> SeedPrinterGroupAsync(string name = "FarmShape Test Group")
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        PrinterGroup group = new()
        {
            Id = Guid.NewGuid(),
            Name = $"{name}_{Guid.NewGuid():N}",
            CreatedDate = DateTimeOffset.UtcNow,
            UpdatedDate = DateTimeOffset.UtcNow
        };
        db.PrinterGroups.Add(group);
        await db.SaveChangesAsync();
        return group.Id;
    }

    private async Task<Guid> SeedRoleAsync(string name)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        Role role = new()
        {
            Id = Guid.NewGuid(),
            Name = $"{name}_{Guid.NewGuid():N}",
            DisplayName = name,
            IsActive = true,
            IsSystemRole = false,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.Roles.Add(role);
        await db.SaveChangesAsync();
        return role.Id;
    }

    private async Task SeedPrinterGroupAccessAsync(Guid printerGroupId, Guid roleId, PrinterGroupAccessLevel accessLevel)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        db.PrinterGroupAccesses.Add(new PrinterGroupAccess
        {
            Id = Guid.NewGuid(),
            PrinterGroupId = printerGroupId,
            RoleId = roleId,
            AccessLevel = accessLevel
        });
        await db.SaveChangesAsync();
    }
}

/// <summary>Minimal shape used only to read the <c>id</c> field out of printer list responses.</summary>
public sealed record FarmShapePrinterSummary(Guid Id);

/// <summary>Minimal shape used only to read the <c>id</c> field out of location list responses.</summary>
public sealed record FarmShapeLocationSummary(Guid Id);
