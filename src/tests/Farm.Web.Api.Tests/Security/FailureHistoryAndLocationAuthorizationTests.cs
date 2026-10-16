using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.FailureDetection;
using Farm.Infrastructure.Services.Locations;
using Farm.Infrastructure.Services.Printers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Farm.Web.Api.Tests.Security;

/// <summary>
/// DB-backed coverage for printer-group visibility on failure history and location subtree reads.
/// </summary>
[Trait("Category", "Integration")]
public sealed class FailureHistoryAndLocationAuthorizationTests :
    IClassFixture<FailureHistoryAndLocationAuthorizationTests.Factory>, IAsyncLifetime
{
    public sealed class Factory : CustomWebApplicationFactory
    {
        public Factory() : base(new Dictionary<string, string?>
        {
            ["Testing:UseTestAuthentication"] = "true",
            ["Security:DevModeBypassAuth"] = "false",
        })
        {
        }
    }

    private readonly Factory _factory;

    public FailureHistoryAndLocationAuthorizationTests(Factory factory) => _factory = factory;

    public Task InitializeAsync() => _factory.ResetDataAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Theory]
    [InlineData("foreign")]
    [InlineData("active")]
    [InlineData("inactive")]
    [InlineData("admin")]
    public async Task GetHistoryAsync_GroupVisibility_ReturnsExactVisibleIncidentSet(string access)
    {
        Seed seed = await SeedAsync(access);
        using HttpClient client = CreateClient(seed.UserId, access);

        using HttpResponseMessage response = await client.GetAsync("/api/failure-detection/history?take=200");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        List<FailureDetectionDto> incidents = (await response.Content.ReadFromJsonAsync<List<FailureDetectionDto>>())!;
        incidents.Select(incident => incident.Id).Should().BeEquivalentTo(seed.Incidents
            .Where(incident => VisiblePrinterIds(seed, access).Contains(incident.PrinterId))
            .Select(incident => (Guid?)incident.Id));
        incidents.Select(incident => incident.DetectedAt).Should().BeInDescendingOrder();
    }

    [Fact]
    public async Task GetHistoryAsync_NewerRestrictedRowsExceedTake_FillsPageWithOlderVisibleRows()
    {
        Seed seed = await SeedAsync("foreign");
        using HttpClient client = CreateClient(seed.UserId, "foreign");

        using HttpResponseMessage response = await client.GetAsync("/api/failure-detection/history?take=3");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        List<FailureDetectionDto> incidents = (await response.Content.ReadFromJsonAsync<List<FailureDetectionDto>>())!;
        incidents.Select(incident => incident.Id).Should().Equal(seed.Incidents
            .Where(incident => incident.PrinterId == seed.UngroupedId || incident.PrinterId == seed.OpenGroupId)
            .OrderByDescending(incident => incident.DetectedAt)
            .Take(3)
            .Select(incident => (Guid?)incident.Id));
    }

    [Theory]
    [InlineData("foreign", HttpStatusCode.NotFound)]
    [InlineData("inactive", HttpStatusCode.NotFound)]
    [InlineData("active", HttpStatusCode.OK)]
    [InlineData("admin", HttpStatusCode.OK)]
    public async Task GetHistoryAsync_ExplicitRestrictedPrinter_EnforcesViewAccess(string access, HttpStatusCode expected)
    {
        Seed seed = await SeedAsync(access);
        using HttpClient client = CreateClient(seed.UserId, access);

        using HttpResponseMessage response = await client.GetAsync(
            $"/api/failure-detection/history?printerId={seed.MatchingId}&take=200");

        response.StatusCode.Should().Be(expected);
        if (expected == HttpStatusCode.OK)
        {
            List<FailureDetectionDto> incidents = (await response.Content.ReadFromJsonAsync<List<FailureDetectionDto>>())!;
            incidents.Select(incident => incident.Id).Should().BeEquivalentTo(seed.Incidents
                .Where(incident => incident.PrinterId == seed.MatchingId)
                .Select(incident => (Guid?)incident.Id));
        }
        else
        {
            string body = await response.Content.ReadAsStringAsync();
            body.Should().NotContain("private-job").And.NotContain("private-camera");
        }
    }

    [Theory]
    [InlineData("foreign")]
    [InlineData("active")]
    [InlineData("inactive")]
    [InlineData("admin")]
    public async Task GetSubtreePrintersAsync_RootAndDescendantGroups_ReturnsExactVisiblePrinterSet(string access)
    {
        Seed seed = await SeedAsync(access);
        using HttpClient client = CreateClient(seed.UserId, access);

        using HttpResponseMessage response = await client.GetAsync($"/api/locations/{seed.RootId}/printers/subtree");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        List<LocationSubtreePrinterDto> printers =
            (await response.Content.ReadFromJsonAsync<List<LocationSubtreePrinterDto>>())!;
        printers.Select(printer => printer.PrinterId).Should().BeEquivalentTo(VisiblePrinterIds(seed, access));
        foreach (LocationSubtreePrinterDto printer in printers)
        {
            printer.IsOnline.Should().BeTrue();
            printer.Status.Should().Be("Printing");
            printer.CurrentJobName.Should().Be($"live-job-{printer.PrinterId}.gcode");
        }
    }

    [Fact]
    public async Task GetSubtreePrintersAsync_OnlyRestrictedPrinters_ReturnsEmptySet()
    {
        Seed seed = await SeedAsync("foreign");
        using HttpClient client = CreateClient(seed.UserId, "foreign");

        using HttpResponseMessage response = await client.GetAsync($"/api/locations/{seed.RestrictedLeafId}/printers/subtree");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        List<LocationSubtreePrinterDto> printers =
            (await response.Content.ReadFromJsonAsync<List<LocationSubtreePrinterDto>>())!;
        printers.Should().BeEmpty();
    }

    [Fact]
    public async Task GetRecentAsync_MissingAuthorizationService_ReturnsNoIncidents()
    {
        Seed seed = await SeedAsync("admin");
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        FailureDetectionIncidentHistoryService service = new(db);
        ClaimsPrincipal principal = new(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, seed.UserId.ToString()), new Claim(ClaimTypes.Role, "farm_admin")],
            "Test"));

        List<FailureDetectionDto> incidents = await service.GetRecentAsync(principal, null, 200);

        incidents.Should().BeEmpty();
        (await service.GetRecentAsync(null, 200)).Should().HaveCount(seed.Incidents.Length);
    }

    private HttpClient CreateClient(Guid userId, string access)
    {
        HttpClient client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User-Id", userId.ToString());
        client.DefaultRequestHeaders.Add("X-Test-Roles", access == "admin" ? "farm_admin" : "operator");
        return client;
    }

    private static Guid[] VisiblePrinterIds(Seed seed, string access) => access switch
    {
        "admin" => [seed.UngroupedId, seed.OpenGroupId, seed.MatchingId, seed.RestrictedRootId, seed.RestrictedDescendantId],
        "active" => [seed.UngroupedId, seed.OpenGroupId, seed.MatchingId],
        _ => [seed.UngroupedId, seed.OpenGroupId],
    };

    private async Task<Seed> SeedAsync(string access)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        DateTime now = DateTime.UtcNow;
        Guid userId = Guid.NewGuid();
        Manufacturer manufacturer = new() { Id = Guid.NewGuid(), Name = $"visibility-maker-{Guid.NewGuid():N}" };
        PrinterModel model = new() { Id = Guid.NewGuid(), ManufacturerId = manufacturer.Id, Name = "Visibility model" };
        Location root = new() { Id = Guid.NewGuid(), Name = "Visibility root", Path = "/Visibility root" };
        Location child = new()
        {
            Id = Guid.NewGuid(),
            Name = "Visibility child",
            ParentId = root.Id,
            Path = $"{root.Path}/Visibility child",
            Depth = 1,
        };
        Location leaf = new()
        {
            Id = Guid.NewGuid(),
            Name = "Restricted leaf",
            ParentId = child.Id,
            Path = $"{child.Path}/Restricted leaf",
            Depth = 2,
        };
        PrinterGroup openGroup = new() { Id = Guid.NewGuid(), Name = "Open visibility group" };
        PrinterGroup matchingGroup = new() { Id = Guid.NewGuid(), Name = "Matching visibility group" };
        PrinterGroup restrictedGroup = new() { Id = Guid.NewGuid(), Name = "Restricted visibility group" };
        Role matchingRole = new()
        {
            Id = Guid.NewGuid(),
            Name = $"visibility-match-{Guid.NewGuid():N}",
            DisplayName = "Matching",
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        };
        Role foreignRole = new()
        {
            Id = Guid.NewGuid(),
            Name = $"visibility-foreign-{Guid.NewGuid():N}",
            DisplayName = "Foreign",
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        };
        Printer CreatePrinter(string name, Location location, PrinterGroup? group) => new()
        {
            Id = Guid.NewGuid(),
            Name = name,
            ServerUrl = $"http://visibility-{Guid.NewGuid():N}",
            ManufacturerId = manufacturer.Id,
            ModelId = model.Id,
            LocationId = location.Id,
            PrinterGroupId = group?.Id,
            IsEnabled = true,
            IsAvailable = true,
        };
        Printer ungrouped = CreatePrinter("Ungrouped", root, null);
        Printer open = CreatePrinter("Open group", child, openGroup);
        Printer matching = CreatePrinter("Matching group", child, matchingGroup);
        Printer restrictedRoot = CreatePrinter("Restricted root", root, restrictedGroup);
        Printer restrictedDescendant = CreatePrinter("Restricted descendant", leaf, restrictedGroup);
        db.AddRange(manufacturer, model, root, child, leaf, openGroup, matchingGroup, restrictedGroup,
            matchingRole, foreignRole, ungrouped, open, matching, restrictedRoot, restrictedDescendant,
            new PrinterGroupAccess
            {
                Id = Guid.NewGuid(),
                PrinterGroupId = matchingGroup.Id,
                RoleId = matchingRole.Id,
                AccessLevel = PrinterGroupAccessLevel.View,
            },
            new PrinterGroupAccess
            {
                Id = Guid.NewGuid(),
                PrinterGroupId = restrictedGroup.Id,
                RoleId = foreignRole.Id,
                AccessLevel = PrinterGroupAccessLevel.View,
            });
        if (access is "active" or "inactive")
        {
            db.AddRange(
                new User
                {
                    Id = userId,
                    Username = $"visibility-{userId:N}",
                    Email = $"visibility-{userId:N}@example.com",
                    PasswordHash = "unused",
                    FirstName = "Visibility",
                    LastName = "User",
                    IsActive = true,
                    EmailConfirmed = true,
                    CreatedAt = now,
                    UpdatedAt = now,
                },
                new UserRole
                {
                    Id = Guid.NewGuid(),
                    UserId = userId,
                    RoleId = matchingRole.Id,
                    IsActive = access == "active",
                    AssignedAt = now,
                });
        }

        Printer[] incidentPrinters =
            [ungrouped, open, ungrouped, matching, restrictedRoot, restrictedDescendant, restrictedRoot, restrictedDescendant, matching];
        FailureDetectionIncident[] incidents = incidentPrinters.Select((printer, index) => new FailureDetectionIncident
        {
            Id = Guid.NewGuid(),
            PrinterId = printer.Id,
            DetectedAt = now.AddMinutes(index),
            Confidence = 0.95m,
            JobName = $"private-job-{index}.gcode",
            FileName = $"private-file-{index}.gcode",
            SnapshotUrl = $"http://private-camera-{index}/snapshot",
        }).ToArray();
        db.FailureDetectionIncidents.AddRange(incidents);
        await db.SaveChangesAsync();
        IPrinterStatusCacheWriter cache = scope.ServiceProvider.GetRequiredService<IPrinterStatusCacheWriter>();
        foreach (Printer printer in new[] { ungrouped, open, matching, restrictedRoot, restrictedDescendant })
        {
            cache.UpdateStatus(new PrinterStatusDto(printer.Id, true, "Printing", JobName: $"live-job-{printer.Id}.gcode"));
        }

        return new Seed(userId, root.Id, leaf.Id, ungrouped.Id, open.Id, matching.Id,
            restrictedRoot.Id, restrictedDescendant.Id, incidents);
    }

    private sealed record Seed(
        Guid UserId, Guid RootId, Guid RestrictedLeafId, Guid UngroupedId, Guid OpenGroupId,
        Guid MatchingId, Guid RestrictedRootId, Guid RestrictedDescendantId, FailureDetectionIncident[] Incidents);
}
