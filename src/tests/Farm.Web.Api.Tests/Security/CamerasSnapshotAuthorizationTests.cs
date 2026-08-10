using System.Net;
using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.StorageManagement;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Farm.Web.Api.Tests.Security;

/// <summary>
/// Regression coverage for the <c>CameraSnapshotsController</c> PrinterGroup scoping added
/// alongside issue #1421. <c>CameraSnapshotsController</c> reads camera snapshot metadata and
/// images directly from <see cref="AppDbContext"/>, bypassing <c>CamerasController</c>
/// entirely — so without its own <see cref="PrinterGroupAccess"/> check, it would remain a
/// bypass of the exact protection <c>CamerasController</c>'s stream/snapshot proxy enforces.
/// </summary>
public sealed class CamerasSnapshotAuthorizationTests : IAsyncLifetime
{
    private readonly CameraSnapshotFactory _factory = new();

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public async Task GetByPrinter_CrossGroupCallerDenied_Returns404()
    {
        (Guid printerId, _, _) = await SeedRestrictedSnapshotAsync();
        using HttpClient client = CreateForeignRoleClient();

        HttpResponseMessage response = await client.GetAsync($"/api/snapshots/by-printer/{printerId}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetByPrintJob_CrossGroupCaller_OmitsRestrictedSnapshot()
    {
        (_, Guid printJobId, Guid snapshotId) = await SeedRestrictedSnapshotAsync();
        using HttpClient client = CreateForeignRoleClient();

        HttpResponseMessage response = await client.GetAsync($"/api/snapshots/by-job/{printJobId}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        string body = await response.Content.ReadAsStringAsync();
        body.Should().NotContain(snapshotId.ToString());
    }

    [Fact]
    public async Task GetImage_CrossGroupCallerDenied_Returns404()
    {
        (_, _, Guid snapshotId) = await SeedRestrictedSnapshotAsync();
        using HttpClient client = CreateForeignRoleClient();

        HttpResponseMessage response = await client.GetAsync($"/api/snapshots/{snapshotId}/image");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Delete_CrossGroupCallerDenied_Returns404()
    {
        (_, _, Guid snapshotId) = await SeedRestrictedSnapshotAsync();
        using HttpClient client = CreateForeignRoleClient();

        HttpResponseMessage response = await client.DeleteAsync($"/api/snapshots/{snapshotId}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);

        // Denial must not have side effects: the record must remain intact.
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        bool stillExists = await db.CameraSnapshots.AnyAsync(s => s.Id == snapshotId);
        stillExists.Should().BeTrue();
    }

    [Fact]
    public async Task GetByPrinter_MatchingRoleCaller_IsNotDeniedByGroupCheck()
    {
        (Guid printerId, Guid allowedRoleId) = await SeedRestrictedPrinterWithRoleAsync();
        Guid snapshotId = await SeedSnapshotForPrinterAsync(printerId, printJobId: null);
        using HttpClient client = CreateClientWithRole(allowedRoleId);

        HttpResponseMessage response = await client.GetAsync($"/api/snapshots/by-printer/{printerId}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        string body = await response.Content.ReadAsStringAsync();
        body.Should().Contain(snapshotId.ToString());
    }

    [Fact]
    public async Task GetByPrinter_FarmAdmin_BypassesGroupCheck()
    {
        (Guid printerId, _, _) = await SeedRestrictedSnapshotAsync();
        using HttpClient client = CreateAdminClient();

        HttpResponseMessage response = await client.GetAsync($"/api/snapshots/by-printer/{printerId}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task GetByPrintJob_MatchingRoleCaller_IsNotDeniedByGroupCheck()
    {
        (Guid printerId, Guid allowedRoleId) = await SeedRestrictedPrinterWithRoleAsync(PrinterGroupAccessLevel.View);
        Guid printJobId = Guid.NewGuid();
        await using (AsyncServiceScope scope = _factory.Services.CreateAsyncScope())
        {
            AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Set<PrintJob>().Add(new PrintJob { Id = printJobId, Name = "Matching-role job", Status = PrintJobStatus.Queued });
            await db.SaveChangesAsync();
        }

        Guid snapshotId = await SeedSnapshotForPrinterAsync(printerId, printJobId);
        using HttpClient client = CreateClientWithRole(allowedRoleId);

        HttpResponseMessage response = await client.GetAsync($"/api/snapshots/by-job/{printJobId}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        string body = await response.Content.ReadAsStringAsync();
        body.Should().Contain(snapshotId.ToString());
    }

    [Fact]
    public async Task GetByPrintJob_FarmAdmin_BypassesGroupCheck()
    {
        (_, Guid printJobId, Guid snapshotId) = await SeedRestrictedSnapshotAsync();
        using HttpClient client = CreateAdminClient();

        HttpResponseMessage response = await client.GetAsync($"/api/snapshots/by-job/{printJobId}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        string body = await response.Content.ReadAsStringAsync();
        body.Should().Contain(snapshotId.ToString());
    }

    [Fact]
    public async Task GetImage_MatchingRoleCaller_IsNotDeniedByGroupCheck()
    {
        (Guid printerId, Guid allowedRoleId) = await SeedRestrictedPrinterWithRoleAsync(PrinterGroupAccessLevel.View);
        Guid snapshotId = await SeedSnapshotForPrinterAsync(printerId, printJobId: null);
        using HttpClient client = CreateClientWithRole(allowedRoleId);

        HttpResponseMessage response = await client.GetAsync($"/api/snapshots/{snapshotId}/image");

        // The authorization check must not deny; a 404 here would be from the (mocked-out) file
        // not existing on disk, not from the group check, so accept anything except NotFound.
        response.StatusCode.Should().NotBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetImage_FarmAdmin_BypassesGroupCheck()
    {
        (_, _, Guid snapshotId) = await SeedRestrictedSnapshotAsync();
        using HttpClient client = CreateAdminClient();

        HttpResponseMessage response = await client.GetAsync($"/api/snapshots/{snapshotId}/image");

        response.StatusCode.Should().NotBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Delete_MatchingManageRoleCaller_IsNotDeniedByGroupCheck()
    {
        // DeleteAsync requires PrinterGroupAccessLevel.Manage, not View, so this fixture must
        // grant Manage explicitly -- a View-only role must still be denied (see next test).
        (Guid printerId, Guid manageRoleId) = await SeedRestrictedPrinterWithRoleAsync(PrinterGroupAccessLevel.Manage);
        Guid snapshotId = await SeedSnapshotForPrinterAsync(printerId, printJobId: null);
        using HttpClient client = CreateClientWithRole(manageRoleId);

        HttpResponseMessage response = await client.DeleteAsync($"/api/snapshots/{snapshotId}");

        response.StatusCode.Should().NotBe(HttpStatusCode.NotFound);

        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        bool stillExists = await db.CameraSnapshots.AnyAsync(s => s.Id == snapshotId);
        stillExists.Should().BeFalse();
    }

    [Fact]
    public async Task Delete_ViewOnlyRoleCaller_IsDeniedByGroupCheck()
    {
        // A caller whose PrinterGroupAccess only grants View must not be able to delete: Delete
        // requires Manage. This proves the access-level distinction (not just group membership)
        // is enforced.
        (Guid printerId, Guid viewOnlyRoleId) = await SeedRestrictedPrinterWithRoleAsync(PrinterGroupAccessLevel.View);
        Guid snapshotId = await SeedSnapshotForPrinterAsync(printerId, printJobId: null);
        using HttpClient client = CreateClientWithRole(viewOnlyRoleId);

        HttpResponseMessage response = await client.DeleteAsync($"/api/snapshots/{snapshotId}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);

        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        bool stillExists = await db.CameraSnapshots.AnyAsync(s => s.Id == snapshotId);
        stillExists.Should().BeTrue();
    }

    [Fact]
    public async Task Delete_FarmAdmin_BypassesGroupCheck()
    {
        (_, _, Guid snapshotId) = await SeedRestrictedSnapshotAsync();
        using HttpClient client = CreateAdminClient();

        HttpResponseMessage response = await client.DeleteAsync($"/api/snapshots/{snapshotId}");

        response.StatusCode.Should().NotBe(HttpStatusCode.NotFound);

        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        bool stillExists = await db.CameraSnapshots.AnyAsync(s => s.Id == snapshotId);
        stillExists.Should().BeFalse();
    }

    [Fact]
    public async Task ZeroAclGroup_And_UngroupedPrinter_SnapshotsRemainVisible()
    {
        (Guid zeroAclPrinterId, Guid ungroupedPrinterId) = await SeedOpenByDefaultPrintersAsync();
        Guid zeroAclSnapshotId = await SeedSnapshotForPrinterAsync(zeroAclPrinterId, printJobId: null);
        Guid ungroupedSnapshotId = await SeedSnapshotForPrinterAsync(ungroupedPrinterId, printJobId: null);
        using HttpClient client = CreateForeignRoleClient();

        HttpResponseMessage zeroAclResponse = await client.GetAsync($"/api/snapshots/by-printer/{zeroAclPrinterId}");
        HttpResponseMessage ungroupedResponse = await client.GetAsync($"/api/snapshots/by-printer/{ungroupedPrinterId}");

        zeroAclResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        ungroupedResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        (await zeroAclResponse.Content.ReadAsStringAsync()).Should().Contain(zeroAclSnapshotId.ToString());
        (await ungroupedResponse.Content.ReadAsStringAsync()).Should().Contain(ungroupedSnapshotId.ToString());
    }

    // --- Fixture seeding -----------------------------------------------------------------

    private async Task<(Guid PrinterId, Guid PrintJobId, Guid SnapshotId)> SeedRestrictedSnapshotAsync()
    {
        (Guid printerId, _) = await SeedRestrictedPrinterWithRoleAsync();
        Guid printJobId = Guid.NewGuid();
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Set<PrintJob>().Add(new PrintJob { Id = printJobId, Name = "Restricted job", Status = PrintJobStatus.Queued });
        await db.SaveChangesAsync();

        Guid snapshotId = await SeedSnapshotForPrinterAsync(printerId, printJobId);
        return (printerId, printJobId, snapshotId);
    }

    private async Task<Guid> SeedSnapshotForPrinterAsync(Guid printerId, Guid? printJobId)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        IStoragePathService storagePath = scope.ServiceProvider.GetRequiredService<IStoragePathService>();
        Guid cameraId = Guid.NewGuid();
        db.Cameras.Add(new Camera { Id = cameraId, Name = "snapshot camera", PrinterId = printerId, IsEnabled = true });

        string relativePath = $"{printerId}/{Guid.NewGuid():N}/snapshot.jpg";
        string fullPath = Path.Join(storagePath.GetSnapshotStorageDirectory(), relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await File.WriteAllBytesAsync(fullPath, [0xFF, 0xD8, 0xFF, 0xE0]); // JPEG magic bytes so GetImageAsync's File.Exists check succeeds

        var snapshot = new CameraSnapshot
        {
            Id = Guid.NewGuid(),
            PrinterId = printerId,
            CameraId = cameraId,
            PrintJobId = printJobId,
            EventType = "PrintStarted",
            FilePath = relativePath,
            CapturedAt = DateTime.UtcNow,
            FileSizeBytes = 1024,
        };
        db.CameraSnapshots.Add(snapshot);
        await db.SaveChangesAsync();
        return snapshot.Id;
    }

    private async Task<(Guid PrinterId, Guid AllowedRoleId)> SeedRestrictedPrinterWithRoleAsync(
        PrinterGroupAccessLevel accessLevel = PrinterGroupAccessLevel.View)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        DateTime now = DateTime.UtcNow;
        var manufacturer = new Manufacturer { Id = Guid.NewGuid(), Name = $"Snapshot ACL maker {Guid.NewGuid():N}" };
        var model = new PrinterModel { Id = Guid.NewGuid(), ManufacturerId = manufacturer.Id, Name = $"Snapshot ACL model {Guid.NewGuid():N}" };
        var group = new PrinterGroup { Id = Guid.NewGuid(), Name = $"Snapshot ACL group {Guid.NewGuid():N}" };
        var allowedRole = new Role
        {
            Id = Guid.NewGuid(),
            Name = $"snapshot-read-allowed-{Guid.NewGuid():N}",
            DisplayName = "Allowed snapshot read role",
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        };
        var printer = new Printer
        {
            Id = Guid.NewGuid(),
            Name = "Restricted snapshot printer",
            ServerUrl = $"http://restricted-snapshot-printer-{Guid.NewGuid():N}",
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
                AccessLevel = accessLevel,
            },
            new PrinterDispatchState { PrinterId = printer.Id });
        await db.SaveChangesAsync();
        return (printer.Id, allowedRole.Id);
    }

    private async Task<(Guid ZeroAclPrinterId, Guid UngroupedPrinterId)> SeedOpenByDefaultPrintersAsync()
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var manufacturer = new Manufacturer { Id = Guid.NewGuid(), Name = $"Open snapshot maker {Guid.NewGuid():N}" };
        var model = new PrinterModel { Id = Guid.NewGuid(), ManufacturerId = manufacturer.Id, Name = $"Open snapshot model {Guid.NewGuid():N}" };
        var zeroAclGroup = new PrinterGroup { Id = Guid.NewGuid(), Name = $"Zero ACL snapshot group {Guid.NewGuid():N}" };
        var zeroAclPrinter = new Printer
        {
            Id = Guid.NewGuid(),
            Name = "Zero-ACL snapshot printer",
            ServerUrl = $"http://zero-acl-snapshot-{Guid.NewGuid():N}",
            ManufacturerId = manufacturer.Id,
            ModelId = model.Id,
            PrinterGroupId = zeroAclGroup.Id,
            IsEnabled = true,
            IsAvailable = true,
        };
        var ungroupedPrinter = new Printer
        {
            Id = Guid.NewGuid(),
            Name = "Ungrouped snapshot printer",
            ServerUrl = $"http://ungrouped-snapshot-{Guid.NewGuid():N}",
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
        return (zeroAclPrinter.Id, ungroupedPrinter.Id);
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
                Username = $"snapshot-read-authz-{userId:N}",
                Email = $"snapshot-read-authz-{userId:N}@example.com",
                PasswordHash = "unused",
                FirstName = "Snapshot",
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

    private HttpClient CreateForeignRoleClient()
    {
        HttpClient client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User-Id", Guid.NewGuid().ToString());
        client.DefaultRequestHeaders.Add("X-Test-Roles", "unrelated-role");
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

    private sealed class CameraSnapshotFactory()
        : CustomWebApplicationFactory(
            new Dictionary<string, string?>
            {
                ["Testing:UseTestAuthentication"] = "true",
                ["Security:DevModeBypassAuth"] = "false",
            });
}
