using System.Net;
using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Security;
using Farm.Infrastructure.Services.Printers;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;

namespace Farm.Web.Api.Tests.Security;

/// <summary>
/// Regression coverage for issue #1292: printer-scoped read endpoints and the camera-proxy
/// endpoints must enforce the same <see cref="PrinterGroupAccess"/> check that write endpoints
/// and the SignalR hub already enforce. A caller without a matching role on a restricted
/// <see cref="PrinterGroup"/> must never observe another group's printer, whether by id or via
/// the collection endpoints.
/// </summary>
public sealed class PrinterReadAuthorizationTests : IAsyncLifetime
{
    private readonly Mock<IPrintersService> _printers = new();
    private readonly PrinterReadFactory _factory;

    public PrinterReadAuthorizationTests()
    {
        _factory = new PrinterReadFactory(_printers);

        _printers
            .Setup(service => service.FindByIdWithIncludesAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Returns((Guid id, CancellationToken _) => Task.FromResult<Printer?>(new Printer
            {
                Id = id,
                Name = "Stub printer",
                ServerUrl = $"http://stub-{id:N}",
                ManufacturerId = Guid.NewGuid(),
                ModelId = Guid.NewGuid(),
            }));
        _printers
            .Setup(service => service.GetPrintJobStatusAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((PrintJobStatusDto?)null);
        _printers
            .Setup(service => service.GetPrintJobObjectsAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Returns((Guid id, CancellationToken _) => Task.FromResult<PrintJobObjectListDto?>(
                new PrintJobObjectListDto(id, null, Array.Empty<PrintJobObjectDto>())));
        _printers
            .Setup(service => service.GetStatusDtoAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Returns((Guid id, CancellationToken _) => Task.FromResult(
                new PrinterStatusDto(id, IsOnline: false, State: null)));
        _printers
            .Setup(service => service.GetPrinterDtoAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Returns((Guid id, CancellationToken _) => Task.FromResult(
                new PrinterDto(id, "Stub printer", null, false, null)));
        _printers
            .Setup(service => service.GetCameraUrlsForPrinterAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(("http://camera.example.invalid/stream", "http://camera.example.invalid/snapshot"));
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    public static IEnumerable<object[]> RestrictedReadEndpoints()
    {
        yield return new object[] { "printjob" };
        yield return new object[] { "printjob/objects" };
        yield return new object[] { "status" };
        yield return new object[] { string.Empty };
        yield return new object[] { "details" };
        yield return new object[] { "camera/url" };
        yield return new object[] { "camera/stream" };
        yield return new object[] { "camera/snapshot" };
    }

    [Theory]
    [MemberData(nameof(RestrictedReadEndpoints))]
    public async Task ReadEndpoint_CrossGroupCallerDenied_Returns404(string suffix)
    {
        Guid printerId = await SeedRestrictedPrinterAsync();
        using HttpClient client = CreateForeignRoleClient(Guid.NewGuid());

        HttpResponseMessage response = await client.GetAsync(BuildUrl(printerId, suffix));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Theory]
    [MemberData(nameof(RestrictedReadEndpoints))]
    public async Task ReadEndpoint_MatchingRoleCaller_IsNotDeniedByGroupCheck(string suffix)
    {
        (Guid printerId, Guid allowedRoleId) = await SeedRestrictedPrinterWithRoleAsync();
        using HttpClient client = CreateClientWithRole(Guid.NewGuid(), allowedRoleId, "matching-role");

        HttpResponseMessage response = await client.GetAsync(BuildUrl(printerId, suffix));

        // A caller holding a role granted access on the printer's group must never be turned
        // away by the PrinterGroup gate. Any downstream failure (e.g. camera upstream 502) is
        // acceptable here; only the authorization-shaped 404 is disallowed.
        response.StatusCode.Should().NotBe(HttpStatusCode.NotFound);
    }

    [Theory]
    [MemberData(nameof(RestrictedReadEndpoints))]
    public async Task ReadEndpoint_FarmAdmin_BypassesGroupCheck(string suffix)
    {
        Guid printerId = await SeedRestrictedPrinterAsync();
        using HttpClient client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User-Id", Guid.NewGuid().ToString());
        client.DefaultRequestHeaders.Add("X-Test-Roles", "farm_admin");

        HttpResponseMessage response = await client.GetAsync(BuildUrl(printerId, suffix));

        response.StatusCode.Should().NotBe(HttpStatusCode.NotFound);
    }

    private static string BuildUrl(Guid printerId, string suffix) =>
        string.IsNullOrEmpty(suffix)
            ? $"/api/printers/{printerId}"
            : $"/api/printers/{printerId}/{suffix}";

    private HttpClient CreateForeignRoleClient(Guid actorId)
    {
        HttpClient client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User-Id", actorId.ToString());
        client.DefaultRequestHeaders.Add("X-Test-Roles", "unrelated-role");

        // Deliberately do not grant this actor any UserRole row: the actor has no role
        // that could ever satisfy a PrinterGroupAccess rule, so the group check must deny.
        return client;
    }

    private HttpClient CreateClientWithRole(Guid actorId, Guid roleId, string roleName)
    {
        HttpClient client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User-Id", actorId.ToString());
        client.DefaultRequestHeaders.Add("X-Test-Roles", roleName);
        EnsureUserRoleAsync(actorId, roleId).GetAwaiter().GetResult();
        return client;
    }

    private async Task EnsureUserRoleAsync(Guid userId, Guid roleId)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        if (await db.UserRoles.AnyAsync(ur => ur.UserId == userId && ur.RoleId == roleId))
        {
            return;
        }

        if (!await db.Users.AnyAsync(u => u.Id == userId))
        {
            db.Users.Add(new User
            {
                Id = userId,
                Username = $"read-authz-{userId:N}",
                Email = $"read-authz-{userId:N}@example.com",
                PasswordHash = "unused",
                FirstName = "Read",
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
            Name = $"Read ACL maker {Guid.NewGuid():N}",
        };
        var model = new PrinterModel
        {
            Id = Guid.NewGuid(),
            ManufacturerId = manufacturer.Id,
            Name = $"Read ACL model {Guid.NewGuid():N}",
        };
        var group = new PrinterGroup
        {
            Id = Guid.NewGuid(),
            Name = $"Read ACL group {Guid.NewGuid():N}",
        };
        var allowedRole = new Role
        {
            Id = Guid.NewGuid(),
            Name = $"read-allowed-{Guid.NewGuid():N}",
            DisplayName = "Allowed read role",
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        };
        var printer = new Printer
        {
            Id = Guid.NewGuid(),
            Name = "Restricted read printer",
            ServerUrl = $"http://restricted-read-printer-{Guid.NewGuid():N}",
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

    private sealed class PrinterReadFactory(Mock<IPrintersService> printers)
        : CustomWebApplicationFactory(
            new Dictionary<string, string?>
            {
                ["Testing:UseTestAuthentication"] = "true",
                ["Security:DevModeBypassAuth"] = "false",
            })
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IPrintersService>();
                services.AddSingleton(printers.Object);
            });
        }
    }
}
