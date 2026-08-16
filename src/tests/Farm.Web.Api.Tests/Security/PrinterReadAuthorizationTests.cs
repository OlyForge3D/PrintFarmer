using System.Net;
using System.Net.Http.Json;
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
    private readonly Mock<IPrinterVersionCache> _versionCache = new();
    private readonly Mock<Farm.Infrastructure.Services.Printers.IPrinterToolheadSwapValidator> _swapValidator = new();
    private readonly PrinterReadFactory _factory;

    public PrinterReadAuthorizationTests()
    {
        _factory = new PrinterReadFactory(_printers, _versionCache, _swapValidator);

        _versionCache
            .Setup(cache => cache.GetAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .ReturnsAsync((Guid id, CancellationToken _, bool _) => new PrinterVersionInfoDto(
                id,
                PrinterBackend.Moonraker,
                Supported: true,
                FirmwareVersion: "1.0.0",
                BackendVersion: "1.0.0",
                ApiVersion: "1",
                RetrievedAtUtc: DateTime.UtcNow));
        _swapValidator
            .Setup(validator => validator.ValidateAsync(It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Farm.Infrastructure.Services.Printers.SwapValidationResult(
                Farm.Infrastructure.Services.Printers.SwapValidationOutcome.Validated,
                new Farm.Infrastructure.Services.Printers.SwapValidationResultDto(
                    Farm.Infrastructure.Services.Printers.SwapValidationStatus.Ok,
                    null,
                    null,
                    Array.Empty<Farm.Infrastructure.Services.Printers.SwapValidationAffectedJobDto>(),
                    null)));

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
        _printers
            .Setup(service => service.GetCameraSnapshotAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 });
        _printers
            .Setup(service => service.ListPrinterSpoolsAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<SpoolmanSpoolDto>?)Array.Empty<SpoolmanSpoolDto>());
        _printers
            .Setup(service => service.GetHistoryListAsync(
                It.IsAny<Guid>(),
                It.IsAny<int?>(),
                It.IsAny<int?>(),
                It.IsAny<DateTime?>(),
                It.IsAny<DateTime?>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HistoryListResponse { Count = 0, Jobs = [] });
        _printers
            .Setup(service => service.GetHistoryJobAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HistoryJob { JobId = "job-1", Exists = true });
        _printers
            .Setup(service => service.GetHistoryTotalsAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HistoryTotals { JobTotals = new JobTotals() });

        // These "success path" setups exist so that a matching-role caller (or a hypothetical
        // guard regression) is *distinguishable* from the authorization-shaped 404: if the
        // PrinterGroup gate on these mutating endpoints were removed, the mocked service would
        // now report success (or, for SetToolheadSpoolAsync, the 428 If-Match precondition
        // reached only once FindByIdAsync resolves a real printer) — never the coincidental 404
        // an unconfigured mock produces — which is what proves the deny tests below are
        // load-bearing rather than tautological.
        _printers
            .Setup(service => service.FindByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Returns((Guid id, CancellationToken _) => Task.FromResult<Printer?>(new Printer
            {
                Id = id,
                Name = "Stub printer",
                ServerUrl = $"http://stub-{id:N}",
                ManufacturerId = Guid.NewGuid(),
                ModelId = Guid.NewGuid(),
            }));
        _printers
            .Setup(service => service.EnableCameraAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _printers
            .Setup(service => service.DisableCameraAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _printers
            .Setup(service => service.UnloadFilamentAsync(It.IsAny<Guid>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FilamentUnloadResult(true, "unloaded"));
        _printers
            .Setup(service => service.SetToolheadSpoolAsync(
                It.IsAny<Guid>(),
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<Farm.Infrastructure.Services.Printers.FilamentSwapOverrideContext?>(),
                It.IsAny<Farm.Infrastructure.Services.Printers.SpoolBindPolicy>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CommandResult(true, null));
        _printers
            .Setup(service => service.DeleteHistoryJobAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
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
        yield return new object[] { "snapshot" };
        yield return new object[] { "spoolman/spools" };
        yield return new object[] { "history" };
        yield return new object[] { "history/totals" };
        yield return new object[] { "history/job-1" };
        yield return new object[] { "session-timeline" };
        yield return new object[] { "version" };
        yield return new object[] { "backend-capabilities" };
        yield return new object[] { "toolheads/0/swap-validation?spoolId=1" };
    }

    [Fact]
    public async Task DeleteHistoryJob_CrossGroupCallerDenied_Returns404()
    {
        Guid printerId = await SeedRestrictedPrinterAsync();
        using HttpClient client = CreateForeignRoleClient(Guid.NewGuid(), PrintFarmerPermissions.Queue.Write);

        HttpResponseMessage response = await client.DeleteAsync(BuildUrl(printerId, "history/job-1"));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task UnloadFilament_CrossGroupCallerDenied_Returns404()
    {
        Guid printerId = await SeedRestrictedPrinterAsync();
        using HttpClient client = CreateForeignRoleClient(Guid.NewGuid(), PrintFarmerPermissions.Queue.Start);

        HttpResponseMessage response = await client.PostAsync(BuildUrl(printerId, "filament-unload"), content: null);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task SetToolheadSpool_CrossGroupCallerDenied_Returns404()
    {
        Guid printerId = await SeedRestrictedPrinterAsync();
        using HttpClient client = CreateForeignRoleClient(Guid.NewGuid(), PrintFarmerPermissions.Queue.Write);

        HttpResponseMessage response = await client.PutAsJsonAsync(
            BuildUrl(printerId, "toolheads/0/spool"),
            new { spoolId = 1 });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task EnableCamera_CrossGroupCallerDenied_Returns404()
    {
        Guid printerId = await SeedRestrictedPrinterAsync();
        using HttpClient client = CreateForeignRoleClient(Guid.NewGuid(), PrintFarmerPermissions.Queue.Write);

        HttpResponseMessage response = await client.PostAsync(BuildUrl(printerId, "camera/enable"), content: null);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task DisableCamera_CrossGroupCallerDenied_Returns404()
    {
        Guid printerId = await SeedRestrictedPrinterAsync();
        using HttpClient client = CreateForeignRoleClient(Guid.NewGuid(), PrintFarmerPermissions.Queue.Write);

        HttpResponseMessage response = await client.PostAsync(BuildUrl(printerId, "camera/disable"), content: null);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
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

    /// <summary>
    /// Endpoints that proxy an outbound HTTP call to the printer's camera (via
    /// <see cref="ProxyCameraAsync"/>) against a non-routable stub URL. The proxy's upstream
    /// failure surfaces as some non-2xx status, so these can only be asserted "not the
    /// authorization-shaped 404" rather than a specific success code.
    /// </summary>
    private static readonly HashSet<string> ProxyEndpoints = new(StringComparer.Ordinal)
    {
        "camera/stream",
        "camera/snapshot",
    };

    [Theory]
    [MemberData(nameof(RestrictedReadEndpoints))]
    public async Task ReadEndpoint_MatchingRoleCaller_IsNotDeniedByGroupCheck(string suffix)
    {
        (Guid printerId, Guid allowedRoleId) = await SeedRestrictedPrinterWithRoleAsync();
        using HttpClient client = CreateClientWithRole(Guid.NewGuid(), allowedRoleId, "matching-role");

        HttpResponseMessage response = await client.GetAsync(BuildUrl(printerId, suffix));

        if (ProxyEndpoints.Contains(suffix))
        {
            // A caller holding a role granted access on the printer's group must never be
            // turned away by the PrinterGroup gate. The upstream camera call against a
            // non-routable stub URL fails independently of authorization (e.g. 502/503); only
            // the authorization-shaped 404 is disallowed here.
            response.StatusCode.Should().NotBe(HttpStatusCode.NotFound);
        }
        else
        {
            // Every other route here is backed by a deterministic mock/real service that
            // succeeds for an existing, accessible printer, so a matching-role caller must
            // observe an actual 2xx success — not just "any non-404" — to prove the group
            // gate let the request through to the real handler logic.
            ((int)response.StatusCode).Should().BeInRange(200, 299);
        }
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

        if (ProxyEndpoints.Contains(suffix))
        {
            response.StatusCode.Should().NotBe(HttpStatusCode.NotFound);
        }
        else
        {
            ((int)response.StatusCode).Should().BeInRange(200, 299);
        }
    }

    private static string BuildUrl(Guid printerId, string suffix) =>
        string.IsNullOrEmpty(suffix)
            ? $"/api/printers/{printerId}"
            : $"/api/printers/{printerId}/{suffix}";

    private HttpClient CreateForeignRoleClient(Guid actorId, params string[] permissions)
    {
        HttpClient client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User-Id", actorId.ToString());
        client.DefaultRequestHeaders.Add("X-Test-Roles", "unrelated-role");
        if (permissions.Length > 0)
        {
            client.DefaultRequestHeaders.Add("X-Test-Permissions", string.Join(',', permissions));
        }

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

    private sealed class PrinterReadFactory(
        Mock<IPrintersService> printers,
        Mock<IPrinterVersionCache> versionCache,
        Mock<Farm.Infrastructure.Services.Printers.IPrinterToolheadSwapValidator> swapValidator)
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
                services.RemoveAll<IPrinterVersionCache>();
                services.AddSingleton(versionCache.Object);
                services.RemoveAll<Farm.Infrastructure.Services.Printers.IPrinterToolheadSwapValidator>();
                services.AddSingleton(swapValidator.Object);
            });
        }
    }
}
