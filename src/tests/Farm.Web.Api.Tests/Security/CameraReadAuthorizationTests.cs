using System.Net;
using System.Net.Http.Json;
using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Discovery;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Security;
using Farm.Infrastructure.Services.Cameras;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;

namespace Farm.Web.Api.Tests.Security;

/// <summary>
/// Regression coverage for issue #1421: <c>CamerasController</c> read and proxy endpoints must
/// enforce the same <see cref="PrinterGroupAccess"/> check that <c>PrintersController</c>
/// already enforces on printer-scoped reads and its camera proxy (issue #1292). A caller
/// without a matching role on a restricted <see cref="PrinterGroup"/> must never observe a
/// printer-attached camera belonging to that group, whether via a collection endpoint,
/// a per-id read, or the stream/snapshot proxy.
///
/// Standalone cameras (<c>PrinterId == null</c>) have no <see cref="PrinterGroup"/> to scope
/// against and remain visible to any authenticated caller by deliberate, documented design
/// (see <c>CamerasController.CanAccessCameraPrinterAsync</c>).
/// </summary>
public sealed class CameraReadAuthorizationTests : IAsyncLifetime
{
    private readonly Mock<ICameraService> _cameras = new();
    private readonly Mock<IPrinterCameraEndpointDetectionService> _detection = new();
    private readonly CameraReadFactory _factory;

    public CameraReadAuthorizationTests()
    {
        _factory = new CameraReadFactory(_cameras, _detection);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    // --- Restricted printer / camera fixtures -----------------------------------------------

    private void SeedCameraMocks(Guid restrictedPrinterId, Guid restrictedCameraId, Guid standaloneCameraId)
    {
        var restrictedCamera = new Camera
        {
            Id = restrictedCameraId,
            Name = "Restricted printer camera",
            PrinterId = restrictedPrinterId,
            StreamUrl = "http://camera.example.invalid/stream",
            SnapshotUrl = "http://camera.example.invalid/snapshot",
            IsEnabled = true,
        };
        var standaloneCamera = new Camera
        {
            Id = standaloneCameraId,
            Name = "Standalone camera",
            PrinterId = null,
            StreamUrl = "http://camera.example.invalid/stream",
            SnapshotUrl = "http://camera.example.invalid/snapshot",
            IsEnabled = true,
        };

        _cameras
            .Setup(s => s.FindByIdAsync(restrictedCameraId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(restrictedCamera);
        _cameras
            .Setup(s => s.FindByIdAsync(standaloneCameraId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(standaloneCamera);

        CameraDto[] allDtos =
        [
            ToCameraDto(restrictedCameraId, restrictedPrinterId),
            ToCameraDto(standaloneCameraId, null),
        ];
        _cameras.Setup(s => s.GetAllDtosAsync(It.IsAny<CancellationToken>())).ReturnsAsync(allDtos);
        _cameras.Setup(s => s.GetEnabledCamerasAsync(It.IsAny<CancellationToken>())).ReturnsAsync(allDtos);

        List<DisplayCameraDto> displayDtos =
        [
            ToDisplayCameraDto(restrictedCameraId, restrictedPrinterId),
            ToDisplayCameraDto(standaloneCameraId, null),
        ];
        _cameras.Setup(s => s.GetDisplayCamerasAsync(It.IsAny<CancellationToken>())).ReturnsAsync(displayDtos);

        _cameras
            .Setup(s => s.GetByPrinterIdAsync(restrictedPrinterId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([ToCameraDto(restrictedCameraId, restrictedPrinterId)]);

        // A non-null detection result proves a matching-role/admin caller reached the real
        // handler logic, rather than merely observing a coincidental unconfigured-mock 404.
        _detection
            .Setup(s => s.DetectAsync(restrictedPrinterId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PrinterCameraProbeResult("http://camera.example.invalid/stream", null, true, "moonraker"));
    }

    private static CameraDto ToCameraDto(Guid id, Guid? printerId) => new()
    {
        Id = id,
        Name = "camera",
        PrinterId = printerId,
        IsEnabled = true,
    };

    private static DisplayCameraDto ToDisplayCameraDto(Guid id, Guid? printerId) => new()
    {
        Id = id,
        Name = "camera",
        PrinterId = printerId,
        IsStandalone = !printerId.HasValue,
        StreamUrl = "http://camera.example.invalid/stream",
        SnapshotUrl = "http://camera.example.invalid/snapshot",
        IsEnabled = true,
    };

    private static readonly HashSet<string> ProxyEndpoints = new(StringComparer.Ordinal)
    {
        "stream",
        "snapshot",
    };

    // --- Cross-group caller denied on every read/proxy endpoint ------------------------------

    public static IEnumerable<object[]> RestrictedCameraIdEndpoints()
    {
        yield return new object[] { "" };
        yield return new object[] { "stream" };
        yield return new object[] { "snapshot" };
    }

    [Theory]
    [MemberData(nameof(RestrictedCameraIdEndpoints))]
    public async Task PerCameraEndpoint_CrossGroupCallerDenied_Returns404(string suffix)
    {
        (Guid printerId, Guid cameraId, Guid standaloneCameraId) = await SeedRestrictedFixtureAsync();
        using HttpClient client = CreateForeignRoleClient();

        HttpResponseMessage response = await client.GetAsync(BuildCameraUrl(cameraId, suffix));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetCamerasByPrinter_CrossGroupCallerDenied_Returns404()
    {
        (Guid printerId, _, _) = await SeedRestrictedFixtureAsync();
        using HttpClient client = CreateForeignRoleClient();

        HttpResponseMessage response = await client.GetAsync($"/api/cameras/by-printer/{printerId}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task DetectEndpoints_CrossGroupCallerDenied_Returns404()
    {
        (Guid printerId, _, _) = await SeedRestrictedFixtureAsync();
        using HttpClient client = CreateForeignRoleClient();

        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/cameras/detect-endpoints",
            new { printerId });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Theory]
    [InlineData("")]
    [InlineData("/enabled")]
    [InlineData("/display")]
    public async Task ListEndpoint_CrossGroupCaller_OmitsRestrictedCamera(string suffix)
    {
        (_, Guid cameraId, Guid standaloneCameraId) = await SeedRestrictedFixtureAsync();
        using HttpClient client = CreateForeignRoleClient();

        HttpResponseMessage response = await client.GetAsync($"/api/cameras{suffix}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        string body = await response.Content.ReadAsStringAsync();
        body.Should().NotContain(cameraId.ToString());
        body.Should().Contain(standaloneCameraId.ToString());
    }

    // --- Matching-role caller is not denied (regression guard against over-blocking) --------

    [Theory]
    [MemberData(nameof(RestrictedCameraIdEndpoints))]
    public async Task PerCameraEndpoint_MatchingRoleCaller_IsNotDeniedByGroupCheck(string suffix)
    {
        (Guid printerId, Guid cameraId, Guid allowedRoleId) = await SeedRestrictedFixtureWithRoleAsync();
        using HttpClient client = CreateClientWithRole(allowedRoleId);

        HttpResponseMessage response = await client.GetAsync(BuildCameraUrl(cameraId, suffix));

        if (ProxyEndpoints.Contains(suffix))
        {
            // The proxy call goes out to a non-routable stub URL, which fails independently of
            // authorization (e.g. 502). Only the authorization-shaped 404 is disallowed here.
            response.StatusCode.Should().NotBe(HttpStatusCode.NotFound);
        }
        else
        {
            response.StatusCode.Should().Be(HttpStatusCode.OK);
        }
    }

    [Fact]
    public async Task GetCamerasByPrinter_MatchingRoleCaller_IsNotDeniedByGroupCheck()
    {
        (Guid printerId, _, Guid allowedRoleId) = await SeedRestrictedFixtureWithRoleAsync();
        using HttpClient client = CreateClientWithRole(allowedRoleId);

        HttpResponseMessage response = await client.GetAsync($"/api/cameras/by-printer/{printerId}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task DetectEndpoints_MatchingRoleCaller_IsNotDeniedByGroupCheck()
    {
        (Guid printerId, _, Guid allowedRoleId) = await SeedRestrictedFixtureWithRoleAsync();
        using HttpClient client = CreateClientWithRole(allowedRoleId);

        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/cameras/detect-endpoints",
            new { printerId });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData("")]
    [InlineData("/enabled")]
    [InlineData("/display")]
    public async Task ListEndpoint_MatchingRoleCaller_SeesRestrictedCamera(string suffix)
    {
        (_, Guid cameraId, Guid allowedRoleId) = await SeedRestrictedFixtureWithRoleAsync();
        using HttpClient client = CreateClientWithRole(allowedRoleId);

        HttpResponseMessage response = await client.GetAsync($"/api/cameras{suffix}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        string body = await response.Content.ReadAsStringAsync();
        body.Should().Contain(cameraId.ToString());
    }

    // --- farm_admin bypasses the group check -------------------------------------------------

    [Theory]
    [MemberData(nameof(RestrictedCameraIdEndpoints))]
    public async Task PerCameraEndpoint_FarmAdmin_BypassesGroupCheck(string suffix)
    {
        (Guid printerId, Guid cameraId, _) = await SeedRestrictedFixtureAsync();
        using HttpClient client = CreateAdminClient();

        HttpResponseMessage response = await client.GetAsync(BuildCameraUrl(cameraId, suffix));

        if (ProxyEndpoints.Contains(suffix))
        {
            response.StatusCode.Should().NotBe(HttpStatusCode.NotFound);
        }
        else
        {
            response.StatusCode.Should().Be(HttpStatusCode.OK);
        }
    }

    [Fact]
    public async Task GetCamerasByPrinter_FarmAdmin_BypassesGroupCheck()
    {
        (Guid printerId, _, _) = await SeedRestrictedFixtureAsync();
        using HttpClient client = CreateAdminClient();

        HttpResponseMessage response = await client.GetAsync($"/api/cameras/by-printer/{printerId}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task DetectEndpoints_FarmAdmin_BypassesGroupCheck()
    {
        (Guid printerId, _, _) = await SeedRestrictedFixtureAsync();
        using HttpClient client = CreateAdminClient();

        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/cameras/detect-endpoints",
            new { printerId });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData("")]
    [InlineData("/enabled")]
    [InlineData("/display")]
    public async Task ListEndpoint_FarmAdmin_SeesRestrictedCamera(string suffix)
    {
        (_, Guid cameraId, _) = await SeedRestrictedFixtureAsync();
        using HttpClient client = CreateAdminClient();

        HttpResponseMessage response = await client.GetAsync($"/api/cameras{suffix}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        string body = await response.Content.ReadAsStringAsync();
        body.Should().Contain(cameraId.ToString());
    }

    // --- Open-by-default scenarios stay visible (documents current behavior) ---------------

    [Fact]
    public async Task ZeroAclGroup_And_UngroupedPrinter_CamerasRemainVisible()
    {
        (Guid zeroAclCameraId, Guid ungroupedCameraId) = await SeedOpenByDefaultFixtureAsync();
        using HttpClient client = CreateForeignRoleClient();

        HttpResponseMessage zeroAclResponse = await client.GetAsync(BuildCameraUrl(zeroAclCameraId, ""));
        HttpResponseMessage ungroupedResponse = await client.GetAsync(BuildCameraUrl(ungroupedCameraId, ""));

        zeroAclResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        ungroupedResponse.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task StandaloneCamera_DirectGetById_IsVisibleToAnyAuthenticatedCaller()
    {
        (_, _, Guid standaloneCameraId) = await SeedRestrictedFixtureAsync();
        using HttpClient client = CreateForeignRoleClient();

        HttpResponseMessage response = await client.GetAsync(BuildCameraUrl(standaloneCameraId, ""));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        string body = await response.Content.ReadAsStringAsync();
        body.Should().Contain(standaloneCameraId.ToString());
    }

    // --- Architecture guard: every action taking a camera/printer id must call the
    // authorization helper, so a newly added action cannot silently regress (supplements,
    // does not replace, the behavioral tests above). ----------------------------------------

    private static readonly IReadOnlyDictionary<string, (string Signature, string RequiredCall)> MethodsRequiringAuthCheck =
        new Dictionary<string, (string, string)>(StringComparer.Ordinal)
        {
            ["GetCamerasAsync"] = ("Task<ActionResult<IEnumerable<CameraDto>>> GetCamerasAsync(CancellationToken ct)", "FilterAccessibleCamerasAsync"),
            ["GetEnabledCamerasAsync"] = ("Task<ActionResult<IEnumerable<CameraDto>>> GetEnabledCamerasAsync(CancellationToken ct)", "FilterAccessibleCamerasAsync"),
            ["GetDisplayCamerasAsync"] = ("Task<ActionResult<IEnumerable<DisplayCameraDto>>> GetDisplayCamerasAsync(CancellationToken ct)", "FilterAccessibleCamerasAsync"),
            ["GetCameraAsync"] = ("Task<ActionResult<CameraDto>> GetCameraAsync(Guid id, CancellationToken ct)", "CanAccessCameraPrinterAsync"),
            ["GetCamerasByPrinterAsync"] = ("Task<ActionResult<IEnumerable<CameraDto>>> GetCamerasByPrinterAsync(Guid printerId, CancellationToken ct)", "CanAccessCameraPrinterAsync"),
            ["DetectCameraEndpointsAsync"] = ("Task<ActionResult<CameraEndpointDetectionDto>> DetectCameraEndpointsAsync(DetectCameraEndpointsRequest request, CancellationToken ct)", "CanAccessCameraPrinterAsync"),
            ["ProxyCameraAsync"] = ("Task<IActionResult> ProxyCameraAsync(Guid id, bool useSnapshot, CancellationToken ct)", "CanAccessCameraPrinterAsync"),
        };

    [Fact]
    public void EveryReadAndProxyAction_CallsTheAuthorizationHelper()
    {
        string source = ReadControllerSource();

        foreach ((string methodName, (string signature, string requiredCall)) in MethodsRequiringAuthCheck)
        {
            string body = ExtractMethodBody(source, methodName, signature);
            body.Should().Contain(
                requiredCall,
                because: $"{methodName} must enforce PrinterGroup access via {requiredCall} (issue #1421)");
        }
    }

    private static string ReadControllerSource()
    {
        string? current = AppContext.BaseDirectory;
        while (current is not null)
        {
            string candidate = Path.Join(current, "src", "api", "Controllers", "CamerasController.cs");
            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }

            DirectoryInfo? parent = Directory.GetParent(current);
            current = parent?.FullName;
        }

        throw new FileNotFoundException("Could not locate CamerasController.cs relative to the test assembly.");
    }

    private static string ExtractMethodBody(string source, string methodName, string signature)
    {
        // Anchor on the exact method signature (not just the name) so a call-site reference to
        // the same method elsewhere in the file (e.g. ProxyCameraStreamAsync calling
        // ProxyCameraAsync(...)) can never be mistaken for the declaration.
        int signatureIndex = source.IndexOf(signature, StringComparison.Ordinal);
        signatureIndex.Should().BeGreaterThan(-1, because: $"{methodName}'s declaration should match the expected signature");

        int braceStart = source.IndexOf('{', signatureIndex);
        int depth = 0;
        for (int i = braceStart; i < source.Length; i++)
        {
            if (source[i] == '{')
            {
                depth++;
            }
            else if (source[i] == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return source[braceStart..(i + 1)];
                }
            }
        }

        throw new InvalidOperationException($"Could not find the closing brace for {methodName}.");
    }

    // --- Fixture seeding -----------------------------------------------------------------

    private async Task<(Guid PrinterId, Guid CameraId, Guid StandaloneCameraId)> SeedRestrictedFixtureAsync()
    {
        (Guid printerId, _) = await SeedRestrictedPrinterWithRoleAsync();
        Guid cameraId = Guid.NewGuid();
        Guid standaloneCameraId = Guid.NewGuid();
        SeedCameraMocks(printerId, cameraId, standaloneCameraId);
        return (printerId, cameraId, standaloneCameraId);
    }

    private async Task<(Guid PrinterId, Guid CameraId, Guid AllowedRoleId)> SeedRestrictedFixtureWithRoleAsync()
    {
        (Guid printerId, Guid allowedRoleId) = await SeedRestrictedPrinterWithRoleAsync();
        Guid cameraId = Guid.NewGuid();
        Guid standaloneCameraId = Guid.NewGuid();
        SeedCameraMocks(printerId, cameraId, standaloneCameraId);
        return (printerId, cameraId, allowedRoleId);
    }

    private async Task<(Guid ZeroAclCameraId, Guid UngroupedCameraId)> SeedOpenByDefaultFixtureAsync()
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        DateTime now = DateTime.UtcNow;
        var manufacturer = new Manufacturer { Id = Guid.NewGuid(), Name = $"Open maker {Guid.NewGuid():N}" };
        var model = new PrinterModel { Id = Guid.NewGuid(), ManufacturerId = manufacturer.Id, Name = $"Open model {Guid.NewGuid():N}" };
        var zeroAclGroup = new PrinterGroup { Id = Guid.NewGuid(), Name = $"Zero ACL group {Guid.NewGuid():N}" };
        var zeroAclPrinter = new Printer
        {
            Id = Guid.NewGuid(),
            Name = "Zero-ACL printer",
            ServerUrl = $"http://zero-acl-{Guid.NewGuid():N}",
            ManufacturerId = manufacturer.Id,
            ModelId = model.Id,
            PrinterGroupId = zeroAclGroup.Id,
            IsEnabled = true,
            IsAvailable = true,
        };
        var ungroupedPrinter = new Printer
        {
            Id = Guid.NewGuid(),
            Name = "Ungrouped printer",
            ServerUrl = $"http://ungrouped-{Guid.NewGuid():N}",
            ManufacturerId = manufacturer.Id,
            ModelId = model.Id,
            PrinterGroupId = null,
            IsEnabled = true,
            IsAvailable = true,
        };
        db.AddRange(
            manufacturer,
            model,
            zeroAclGroup,
            zeroAclPrinter,
            ungroupedPrinter,
            new PrinterDispatchState { PrinterId = zeroAclPrinter.Id },
            new PrinterDispatchState { PrinterId = ungroupedPrinter.Id });
        await db.SaveChangesAsync();

        Guid zeroAclCameraId = Guid.NewGuid();
        Guid ungroupedCameraId = Guid.NewGuid();
        _cameras
            .Setup(s => s.FindByIdAsync(zeroAclCameraId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Camera { Id = zeroAclCameraId, Name = "Zero ACL camera", PrinterId = zeroAclPrinter.Id, IsEnabled = true });
        _cameras
            .Setup(s => s.FindByIdAsync(ungroupedCameraId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Camera { Id = ungroupedCameraId, Name = "Ungrouped camera", PrinterId = ungroupedPrinter.Id, IsEnabled = true });

        return (zeroAclCameraId, ungroupedCameraId);
    }

    private async Task<(Guid PrinterId, Guid AllowedRoleId)> SeedRestrictedPrinterWithRoleAsync()
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        DateTime now = DateTime.UtcNow;
        var manufacturer = new Manufacturer { Id = Guid.NewGuid(), Name = $"Camera ACL maker {Guid.NewGuid():N}" };
        var model = new PrinterModel { Id = Guid.NewGuid(), ManufacturerId = manufacturer.Id, Name = $"Camera ACL model {Guid.NewGuid():N}" };
        var group = new PrinterGroup { Id = Guid.NewGuid(), Name = $"Camera ACL group {Guid.NewGuid():N}" };
        var allowedRole = new Role
        {
            Id = Guid.NewGuid(),
            Name = $"camera-read-allowed-{Guid.NewGuid():N}",
            DisplayName = "Allowed camera read role",
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        };
        var printer = new Printer
        {
            Id = Guid.NewGuid(),
            Name = "Restricted camera printer",
            ServerUrl = $"http://restricted-camera-printer-{Guid.NewGuid():N}",
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
                Username = $"camera-read-authz-{userId:N}",
                Email = $"camera-read-authz-{userId:N}@example.com",
                PasswordHash = "unused",
                FirstName = "Camera",
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

    private static string BuildCameraUrl(Guid cameraId, string suffix) =>
        string.IsNullOrEmpty(suffix)
            ? $"/api/cameras/{cameraId}"
            : $"/api/cameras/{cameraId}/{suffix}";

    private HttpClient CreateForeignRoleClient()
    {
        HttpClient client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User-Id", Guid.NewGuid().ToString());
        client.DefaultRequestHeaders.Add("X-Test-Roles", "unrelated-role");

        // Deliberately do not grant this actor any UserRole row: the actor has no role that
        // could ever satisfy a PrinterGroupAccess rule, so the group check must deny.
        return client;
    }

    private HttpClient CreateClientWithRole(Guid roleId)
    {
        Guid actorId = Guid.NewGuid();
        HttpClient client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User-Id", actorId.ToString());
        client.DefaultRequestHeaders.Add("X-Test-Roles", "matching-role");
        EnsureUserRoleAsync(actorId, roleId).GetAwaiter().GetResult();
        return client;
    }

    private HttpClient CreateAdminClient()
    {
        HttpClient client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User-Id", Guid.NewGuid().ToString());
        client.DefaultRequestHeaders.Add("X-Test-Roles", "farm_admin");
        return client;
    }

    private sealed class CameraReadFactory(
        Mock<ICameraService> cameras,
        Mock<IPrinterCameraEndpointDetectionService> detection)
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
                services.RemoveAll<ICameraService>();
                services.AddSingleton(cameras.Object);
                services.RemoveAll<IPrinterCameraEndpointDetectionService>();
                services.AddSingleton(detection.Object);
            });
        }
    }
}
