using System.Net;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Farm.Web.Api.Tests.Security;

/// <summary>
/// Regression coverage for issue #1292: printer collection endpoints
/// (<c>GET /api/printers</c>, <c>/summary</c>, <c>/camera-urls</c>) must exclude printers that
/// belong to a <see cref="PrinterGroup"/> the caller does not have role-based access to, the
/// same way the per-id read endpoints and the SignalR hub already do. Uses the real,
/// DB-backed <see cref="Farm.Infrastructure.Services.Printers.IPrintersService"/> (no service
/// mocking) so the assertions exercise the production authorization + filtering path end to end.
/// </summary>
[Trait("Category", "Integration")]
public sealed class PrinterCollectionAuthorizationTests : IClassFixture<PrinterCollectionAuthorizationTests.Factory>, IAsyncLifetime
{
    public class Factory : CustomWebApplicationFactory
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

    public PrinterCollectionAuthorizationTests(Factory factory)
    {
        _factory = factory;
    }

    public async Task InitializeAsync() => await _factory.ResetDataAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task ListEndpoint_ExcludesPrinterFromRestrictedGroup()
    {
        Guid restrictedId = await SeedRestrictedPrinterAsync();
        Guid openId = await SeedOpenPrinterAsync();
        using HttpClient client = CreateForeignRoleClient(Guid.NewGuid());

        HttpResponseMessage response = await client.GetAsync("/api/printers");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        string body = await response.Content.ReadAsStringAsync();
        body.Should().Contain(openId.ToString());
        body.Should().NotContain(restrictedId.ToString());
    }

    [Fact]
    public async Task SummaryEndpoint_ExcludesPrinterFromRestrictedGroup()
    {
        Guid restrictedId = await SeedRestrictedPrinterAsync();
        Guid openId = await SeedOpenPrinterAsync();
        using HttpClient client = CreateForeignRoleClient(Guid.NewGuid());

        HttpResponseMessage response = await client.GetAsync("/api/printers/summary");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        string body = await response.Content.ReadAsStringAsync();
        body.Should().Contain(openId.ToString());
        body.Should().NotContain(restrictedId.ToString());
    }

    [Fact]
    public async Task CameraUrlsEndpoint_ExcludesPrinterFromRestrictedGroup()
    {
        Guid restrictedId = await SeedRestrictedPrinterAsync();
        Guid openId = await SeedOpenPrinterAsync();
        using HttpClient client = CreateForeignRoleClient(Guid.NewGuid());

        HttpResponseMessage response = await client.GetAsync("/api/printers/camera-urls");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        string body = await response.Content.ReadAsStringAsync();
        body.Should().Contain(openId.ToString());
        body.Should().NotContain(restrictedId.ToString());
    }

    [Fact]
    public async Task BackendCapabilitiesEndpoint_ExcludesPrinterFromRestrictedGroup()
    {
        Guid restrictedId = await SeedRestrictedPrinterAsync();
        Guid openId = await SeedOpenPrinterAsync();
        using HttpClient client = CreateForeignRoleClient(Guid.NewGuid());

        HttpResponseMessage response = await client.GetAsync("/api/printers/backend-capabilities");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        string body = await response.Content.ReadAsStringAsync();
        body.Should().Contain(openId.ToString());
        body.Should().NotContain(restrictedId.ToString());
    }

    [Fact]
    public async Task ListEndpoint_FarmAdmin_SeesAllPrinters()
    {
        Guid restrictedId = await SeedRestrictedPrinterAsync();
        Guid openId = await SeedOpenPrinterAsync();
        using HttpClient client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User-Id", Guid.NewGuid().ToString());
        client.DefaultRequestHeaders.Add("X-Test-Roles", "farm_admin");

        HttpResponseMessage response = await client.GetAsync("/api/printers");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        string body = await response.Content.ReadAsStringAsync();
        body.Should().Contain(openId.ToString());
        body.Should().Contain(restrictedId.ToString());
    }

    [Fact]
    public async Task ListEndpoint_MatchingRoleCaller_SeesRestrictedPrinter()
    {
        (Guid restrictedId, Guid allowedRoleId) = await SeedRestrictedPrinterWithRoleAsync();
        Guid openId = await SeedOpenPrinterAsync();
        Guid actorId = Guid.NewGuid();
        using HttpClient client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User-Id", actorId.ToString());
        client.DefaultRequestHeaders.Add("X-Test-Roles", "matching-role");
        await EnsureUserRoleAsync(actorId, allowedRoleId);

        HttpResponseMessage response = await client.GetAsync("/api/printers");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        string body = await response.Content.ReadAsStringAsync();
        body.Should().Contain(openId.ToString());
        body.Should().Contain(restrictedId.ToString());
    }

    private HttpClient CreateForeignRoleClient(Guid actorId)
    {
        HttpClient client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User-Id", actorId.ToString());
        client.DefaultRequestHeaders.Add("X-Test-Roles", "unrelated-role");

        // Deliberately do not grant this actor any UserRole row: the actor has no role
        // that could ever satisfy a PrinterGroupAccess rule, so the group check must deny.
        return client;
    }

    private async Task EnsureUserRoleAsync(Guid userId, Guid roleId)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        if (!await db.Users.AnyAsync(u => u.Id == userId))
        {
            db.Users.Add(new User
            {
                Id = userId,
                Username = $"collection-authz-{userId:N}",
                Email = $"collection-authz-{userId:N}@example.com",
                PasswordHash = "unused",
                FirstName = "Collection",
                LastName = "Authz",
                IsActive = true,
                EmailConfirmed = true,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
        }

        db.UserRoles.Add(new UserRole
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            RoleId = roleId,
            IsActive = true,
            AssignedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private async Task<Guid> SeedRestrictedPrinterAsync()
    {
        (Guid printerId, _) = await SeedRestrictedPrinterWithRoleAsync();
        return printerId;
    }

    private async Task<(Guid PrinterId, Guid AllowedRoleId)> SeedRestrictedPrinterWithRoleAsync()
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        DateTime now = DateTime.UtcNow;
        var manufacturer = new Manufacturer
        {
            Id = Guid.NewGuid(),
            Name = $"Collection ACL maker {Guid.NewGuid():N}",
        };
        var model = new PrinterModel
        {
            Id = Guid.NewGuid(),
            ManufacturerId = manufacturer.Id,
            Name = $"Collection ACL model {Guid.NewGuid():N}",
        };
        var group = new PrinterGroup
        {
            Id = Guid.NewGuid(),
            Name = $"Collection ACL group {Guid.NewGuid():N}",
        };
        var allowedRole = new Role
        {
            Id = Guid.NewGuid(),
            Name = $"collection-allowed-{Guid.NewGuid():N}",
            DisplayName = "Allowed collection role",
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        };
        var printer = new Printer
        {
            Id = Guid.NewGuid(),
            Name = "Restricted collection printer",
            ServerUrl = $"http://restricted-collection-printer-{Guid.NewGuid():N}",
            ManufacturerId = manufacturer.Id,
            ModelId = model.Id,
            PrinterGroupId = group.Id,
            IsEnabled = true,
            IsAvailable = true,
        };
        db.AddRange(
            manufacturer,
            model,
            group,
            allowedRole,
            printer,
            new PrinterGroupAccess
            {
                Id = Guid.NewGuid(),
                PrinterGroupId = group.Id,
                RoleId = allowedRole.Id,
                AccessLevel = PrinterGroupAccessLevel.View,
            },
            new PrinterDispatchState { PrinterId = printer.Id });
        await db.SaveChangesAsync();
        return (printer.Id, allowedRole.Id);
    }

    private async Task<Guid> SeedOpenPrinterAsync()
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var manufacturer = new Manufacturer
        {
            Id = Guid.NewGuid(),
            Name = $"Collection open maker {Guid.NewGuid():N}",
        };
        var model = new PrinterModel
        {
            Id = Guid.NewGuid(),
            ManufacturerId = manufacturer.Id,
            Name = $"Collection open model {Guid.NewGuid():N}",
        };
        var printer = new Printer
        {
            Id = Guid.NewGuid(),
            Name = "Open collection printer",
            ServerUrl = $"http://open-collection-printer-{Guid.NewGuid():N}",
            ManufacturerId = manufacturer.Id,
            ModelId = model.Id,
            IsEnabled = true,
            IsAvailable = true,
        };
        db.AddRange(
            manufacturer,
            model,
            printer,
            new PrinterDispatchState { PrinterId = printer.Id });
        await db.SaveChangesAsync();
        return printer.Id;
    }
}
