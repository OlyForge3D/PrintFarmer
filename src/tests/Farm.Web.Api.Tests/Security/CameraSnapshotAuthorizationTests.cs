using System.IO;
using System.Net;
using System.Net.Http.Json;
using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Dtos;
using Farm.Infrastructure.Security;
using Farm.Infrastructure.Services.StorageManagement;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Farm.Web.Api.Tests.Security;

/// <summary>
/// Regression coverage for issue #1424: <see cref="Farm.Web.Api.Controllers.CameraSnapshotsController"/>
/// must enforce the same <see cref="PrinterGroupAccess"/> scope check that <c>PrintersController</c>
/// enforces on printer-scoped reads, and the destructive delete must additionally require a
/// write-level permission and a write-level (Submit) PrinterGroup scope check distinct from the
/// View-level check used by the read endpoints.
/// </summary>
public sealed class CameraSnapshotAuthorizationTests : IAsyncLifetime
{
    private readonly CameraSnapshotAuthorizationFactory _factory = new();

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    #region GET /api/snapshots/by-printer/{printerId} — foreign caller denied

    [Fact]
    public async Task GetByPrinter_ForeignRoleCallerDenied_ReturnsNotFound()
    {
        (Guid printerId, _, _) = await SeedRestrictedPrinterAsync();
        await SeedSnapshotAsync(printerId);
        using HttpClient client = CreateForeignRoleClient(Guid.NewGuid());

        HttpResponseMessage response = await client.GetAsync($"/api/snapshots/by-printer/{printerId}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    #endregion

    #region GET /api/snapshots/by-job/{printJobId} — foreign caller denied

    [Fact]
    public async Task GetByPrintJob_ForeignRoleCallerDenied_ReturnsEmptyList()
    {
        (Guid printerId, _, _) = await SeedRestrictedPrinterAsync();
        Guid printJobId = Guid.NewGuid();
        await SeedSnapshotAsync(printerId, printJobId);
        using HttpClient client = CreateForeignRoleClient(Guid.NewGuid());

        HttpResponseMessage response = await client.GetAsync($"/api/snapshots/by-job/{printJobId}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        List<CameraSnapshotDto>? result = await response.Content.ReadFromJsonAsync<List<CameraSnapshotDto>>();
        result.Should().NotBeNull().And.BeEmpty();
    }

    #endregion

    #region GET /api/snapshots/{snapshotId}/image — foreign caller denied

    [Fact]
    public async Task GetImage_ForeignRoleCallerDenied_ReturnsNotFound()
    {
        (Guid printerId, _, _) = await SeedRestrictedPrinterAsync();
        Guid snapshotId = await SeedSnapshotAsync(printerId);
        using HttpClient client = CreateForeignRoleClient(Guid.NewGuid());

        HttpResponseMessage response = await client.GetAsync($"/api/snapshots/{snapshotId}/image");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    #endregion

    #region DELETE /api/snapshots/{snapshotId} — foreign caller denied, row/file survive

    [Fact]
    public async Task Delete_ForeignRoleCallerDenied_RowAndFileSurvive()
    {
        (Guid printerId, _, _) = await SeedRestrictedPrinterAsync();
        (Guid snapshotId, string fullPath) = await SeedSnapshotWithFileAsync(printerId);
        using HttpClient client = CreateForeignRoleClient(Guid.NewGuid(), PrintFarmerPermissions.Queue.Write);

        HttpResponseMessage response = await client.DeleteAsync($"/api/snapshots/{snapshotId}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        (await db.CameraSnapshots.AnyAsync(s => s.Id == snapshotId)).Should().BeTrue();
        File.Exists(fullPath).Should().BeTrue();
    }

    [Fact]
    public async Task Delete_ForeignRoleCallerWithoutWritePermission_ReturnsForbidden()
    {
        (Guid printerId, _, _) = await SeedRestrictedPrinterAsync();
        (Guid snapshotId, string fullPath) = await SeedSnapshotWithFileAsync(printerId);
        // No X-Test-Permissions header at all: fails the [RequirePermission] gate before the
        // printer-scope check is ever reached.
        using HttpClient client = CreateForeignRoleClient(Guid.NewGuid());

        HttpResponseMessage response = await client.DeleteAsync($"/api/snapshots/{snapshotId}");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        (await db.CameraSnapshots.AnyAsync(s => s.Id == snapshotId)).Should().BeTrue();
        File.Exists(fullPath).Should().BeTrue();
    }

    #endregion

    #region Matching-role caller is not denied by the group check (regression guard)

    [Fact]
    public async Task GetByPrinter_MatchingViewRoleCaller_IsNotDenied()
    {
        (Guid printerId, Guid viewRoleId, _) = await SeedRestrictedPrinterAsync();
        await SeedSnapshotAsync(printerId);
        using HttpClient client = await CreateClientWithRoleAsync(Guid.NewGuid(), viewRoleId, "matching-view-role");

        HttpResponseMessage response = await client.GetAsync($"/api/snapshots/by-printer/{printerId}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        List<CameraSnapshotDto>? result = await response.Content.ReadFromJsonAsync<List<CameraSnapshotDto>>();
        result.Should().NotBeNull().And.HaveCount(1);
    }

    [Fact]
    public async Task GetByPrintJob_MatchingViewRoleCaller_IsNotDenied()
    {
        (Guid printerId, Guid viewRoleId, _) = await SeedRestrictedPrinterAsync();
        Guid printJobId = Guid.NewGuid();
        await SeedSnapshotAsync(printerId, printJobId);
        using HttpClient client = await CreateClientWithRoleAsync(Guid.NewGuid(), viewRoleId, "matching-view-role");

        HttpResponseMessage response = await client.GetAsync($"/api/snapshots/by-job/{printJobId}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        List<CameraSnapshotDto>? result = await response.Content.ReadFromJsonAsync<List<CameraSnapshotDto>>();
        result.Should().NotBeNull().And.HaveCount(1);
    }

    [Fact]
    public async Task GetImage_MatchingViewRoleCaller_IsNotDenied()
    {
        (Guid printerId, Guid viewRoleId, _) = await SeedRestrictedPrinterAsync();
        (Guid snapshotId, _) = await SeedSnapshotWithFileAsync(printerId);
        using HttpClient client = await CreateClientWithRoleAsync(Guid.NewGuid(), viewRoleId, "matching-view-role");

        HttpResponseMessage response = await client.GetAsync($"/api/snapshots/{snapshotId}/image");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    #endregion

    #region Delete requires write-level scope, distinct from the read-level (View) check

    [Fact]
    public async Task Delete_ViewLevelRoleCaller_DeniedEvenThoughReadsSucceed()
    {
        (Guid printerId, Guid viewRoleId, _) = await SeedRestrictedPrinterAsync();
        (Guid snapshotId, string fullPath) = await SeedSnapshotWithFileAsync(printerId);
        using HttpClient readClient = await CreateClientWithRoleAsync(Guid.NewGuid(), viewRoleId, "matching-view-role");
        HttpResponseMessage readResponse = await readClient.GetAsync($"/api/snapshots/{snapshotId}/image");
        readResponse.StatusCode.Should().Be(HttpStatusCode.OK, "the View-level role must be able to read the snapshot");

        using HttpClient deleteClient = await CreateClientWithRoleAsync(
            Guid.NewGuid(),
            viewRoleId,
            "matching-view-role",
            PrintFarmerPermissions.Queue.Write);
        HttpResponseMessage deleteResponse = await deleteClient.DeleteAsync($"/api/snapshots/{snapshotId}");

        deleteResponse.StatusCode.Should().Be(HttpStatusCode.NotFound, "View access must not satisfy the Submit-level delete check");
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        (await db.CameraSnapshots.AnyAsync(s => s.Id == snapshotId)).Should().BeTrue();
        File.Exists(fullPath).Should().BeTrue();
    }

    [Fact]
    public async Task Delete_WriteLevelRoleCaller_Allowed()
    {
        (Guid printerId, _, Guid writeRoleId) = await SeedRestrictedPrinterAsync();
        (Guid snapshotId, string fullPath) = await SeedSnapshotWithFileAsync(printerId);
        using HttpClient client = await CreateClientWithRoleAsync(
            Guid.NewGuid(),
            writeRoleId,
            "matching-write-role",
            PrintFarmerPermissions.Queue.Write);

        HttpResponseMessage response = await client.DeleteAsync($"/api/snapshots/{snapshotId}");

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        (await db.CameraSnapshots.AnyAsync(s => s.Id == snapshotId)).Should().BeFalse();
        File.Exists(fullPath).Should().BeFalse();
    }

    #endregion

    #region farm_admin retains full access

    [Fact]
    public async Task FarmAdmin_RetainsFullAccessIncludingDelete()
    {
        (Guid printerId, _, _) = await SeedRestrictedPrinterAsync();
        Guid printJobId = Guid.NewGuid();
        (Guid snapshotId, string fullPath) = await SeedSnapshotWithFileAsync(printerId, printJobId);
        using HttpClient client = CreateAdminClient();

        HttpResponseMessage byPrinter = await client.GetAsync($"/api/snapshots/by-printer/{printerId}");
        HttpResponseMessage byJob = await client.GetAsync($"/api/snapshots/by-job/{printJobId}");
        HttpResponseMessage image = await client.GetAsync($"/api/snapshots/{snapshotId}/image");
        HttpResponseMessage delete = await client.DeleteAsync($"/api/snapshots/{snapshotId}");

        byPrinter.StatusCode.Should().Be(HttpStatusCode.OK);
        byJob.StatusCode.Should().Be(HttpStatusCode.OK);
        image.StatusCode.Should().Be(HttpStatusCode.OK);
        delete.StatusCode.Should().Be(HttpStatusCode.NoContent);
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        (await db.CameraSnapshots.AnyAsync(s => s.Id == snapshotId)).Should().BeFalse();
        File.Exists(fullPath).Should().BeFalse();
    }

    #endregion

    private HttpClient CreateForeignRoleClient(Guid actorId, params string[] permissions)
    {
        HttpClient client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User-Id", actorId.ToString());
        client.DefaultRequestHeaders.Add("X-Test-Roles", "unrelated-role");
        if (permissions.Length > 0)
        {
            client.DefaultRequestHeaders.Add("X-Test-Permissions", string.Join(',', permissions));
        }

        // Deliberately do not grant this actor any UserRole row: the actor has no role that
        // could ever satisfy a PrinterGroupAccess rule, so the group check must deny.
        return client;
    }

    private HttpClient CreateAdminClient()
    {
        HttpClient client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User-Id", Guid.NewGuid().ToString());
        client.DefaultRequestHeaders.Add("X-Test-Roles", "farm_admin");
        return client;
    }

    private async Task<HttpClient> CreateClientWithRoleAsync(Guid actorId, Guid roleId, string roleName, params string[] permissions)
    {
        HttpClient client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User-Id", actorId.ToString());
        client.DefaultRequestHeaders.Add("X-Test-Roles", roleName);
        if (permissions.Length > 0)
        {
            client.DefaultRequestHeaders.Add("X-Test-Permissions", string.Join(',', permissions));
        }

        await EnsureUserRoleAsync(actorId, roleId);
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
                Username = $"snapshot-authz-{userId:N}",
                Email = $"snapshot-authz-{userId:N}@example.com",
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

    /// <summary>
    /// Seeds a printer in a restricted <see cref="PrinterGroup"/> with two roles: one granted
    /// only <see cref="PrinterGroupAccessLevel.View"/> and one granted
    /// <see cref="PrinterGroupAccessLevel.Submit"/> (write-level), so tests can distinguish
    /// read-only access from write-level access on the same printer.
    /// </summary>
    private async Task<(Guid PrinterId, Guid ViewRoleId, Guid WriteRoleId)> SeedRestrictedPrinterAsync()
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        DateTime now = DateTime.UtcNow;
        var manufacturer = new Manufacturer
        {
            Id = Guid.NewGuid(),
            Name = $"Snapshot ACL maker {Guid.NewGuid():N}",
        };
        var model = new PrinterModel
        {
            Id = Guid.NewGuid(),
            ManufacturerId = manufacturer.Id,
            Name = $"Snapshot ACL model {Guid.NewGuid():N}",
        };
        var group = new PrinterGroup
        {
            Id = Guid.NewGuid(),
            Name = $"Snapshot ACL group {Guid.NewGuid():N}",
        };
        var viewRole = new Role
        {
            Id = Guid.NewGuid(),
            Name = $"snapshot-view-{Guid.NewGuid():N}",
            DisplayName = "Snapshot view role",
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        };
        var writeRole = new Role
        {
            Id = Guid.NewGuid(),
            Name = $"snapshot-write-{Guid.NewGuid():N}",
            DisplayName = "Snapshot write role",
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
            viewRole,
            writeRole,
            printer,
            new PrinterGroupAccess
            {
                Id = Guid.NewGuid(),
                PrinterGroupId = group.Id,
                RoleId = viewRole.Id,
                AccessLevel = PrinterGroupAccessLevel.View,
            },
            new PrinterGroupAccess
            {
                Id = Guid.NewGuid(),
                PrinterGroupId = group.Id,
                RoleId = writeRole.Id,
                AccessLevel = PrinterGroupAccessLevel.Submit,
            });
        await db.SaveChangesAsync();
        return (printer.Id, viewRole.Id, writeRole.Id);
    }

    private async Task<Guid> SeedSnapshotAsync(Guid printerId, Guid? printJobId = null)
    {
        (Guid snapshotId, _) = await SeedSnapshotWithFileAsync(printerId, printJobId, writeFile: false);
        return snapshotId;
    }

    private async Task<(Guid SnapshotId, string FullPath)> SeedSnapshotWithFileAsync(
        Guid printerId,
        Guid? printJobId = null,
        bool writeFile = true)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var storagePath = scope.ServiceProvider.GetRequiredService<IStoragePathService>();

        if (printJobId.HasValue && !await db.Set<PrintJob>().AnyAsync(j => j.Id == printJobId.Value))
        {
            db.Set<PrintJob>().Add(new PrintJob
            {
                Id = printJobId.Value,
                Name = "Test Job",
                Status = PrintJobStatus.Queued,
            });
            await db.SaveChangesAsync();
        }

        var camera = new Camera
        {
            Id = Guid.NewGuid(),
            Name = "Snapshot ACL camera",
            PrinterId = printerId,
            IsEnabled = true,
        };
        db.Cameras.Add(camera);
        await db.SaveChangesAsync();

        string snapshotRoot = storagePath.GetSnapshotStorageDirectory();
        string relativePath = Path.Join($"{printerId}", $"{Guid.NewGuid():N}.jpg");
        string fullPath = Path.Join(snapshotRoot, relativePath);

        if (writeFile)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            await File.WriteAllBytesAsync(fullPath, [0xFF, 0xD8, 0xFF, 0xE0]);
        }

        var snapshot = new CameraSnapshot
        {
            Id = Guid.NewGuid(),
            PrinterId = printerId,
            CameraId = camera.Id,
            PrintJobId = printJobId,
            EventType = "PrintStarted",
            FilePath = relativePath,
            CapturedAt = DateTime.UtcNow,
            FileSizeBytes = 1024,
        };
        db.CameraSnapshots.Add(snapshot);
        await db.SaveChangesAsync();
        return (snapshot.Id, fullPath);
    }

    private sealed class CameraSnapshotAuthorizationFactory()
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
        }
    }
}
