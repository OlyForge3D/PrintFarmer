using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Farm.Web.Api.Tests.Security;

/// <summary>
/// Integration tests for issue #1666's acceptance criteria: <c>POST /api/files/local</c> (the
/// OctoPrint-compat upload endpoint) must reject an enqueue attempt from a caller with no
/// <c>queue:*</c> permission, and must reject an enqueue attempt targeting a printer in a
/// <see cref="PrinterGroup"/> the caller cannot submit to — in both cases without creating a
/// <see cref="PrintJob"/> row. A third test confirms the legitimate, correctly-scoped upload
/// flow still works end to end.
/// </summary>
/// <remarks>
/// The endpoint is decorated with
/// <c>[Authorize(AuthenticationSchemes = "Bearer,OctoPrintApiKey")]</c>, which names explicit
/// schemes and therefore bypasses <c>TestAuthHandler</c>'s <c>X-Test-*</c> header shortcut
/// entirely (that shortcut only applies when the host's <c>DefaultAuthenticateScheme</c> is
/// consulted). These tests use a real JWT (test 1, minted directly via
/// <see cref="CustomWebApplicationFactory.CreateAuthenticatedClientAsync"/>, which seeds a user
/// with zero role assignments) and a real seeded <see cref="ApiKeyPurpose.OctoPrint"/> API key
/// (tests 2 and 3, whose owning user's permissions are read live from the database on every
/// request — see <c>OctoPrintAuthService.ResolveApiKeyPrincipalAsync</c>).
/// </remarks>
public sealed class OctoPrintUploadAuthorizationTests : IAsyncLifetime
{
    private readonly CustomWebApplicationFactory _factory = new();

    public Task InitializeAsync() => _factory.ResetDatabaseAsync();

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public async Task Upload_UserWithNoQueuePermission_Returns403AndCreatesNoPrintJob()
    {
        using HttpClient client = await _factory.CreateAuthenticatedClientAsync(
            $"no-perm-{Guid.NewGuid():N}",
            $"no-perm-{Guid.NewGuid():N}@example.test",
            "TestPassword123!");

        using var form = new MultipartFormDataContent();
        using var fileContent = new ByteArrayContent(Encoding.UTF8.GetBytes(MinimalGcodeContent()));
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(fileContent, "file", "no-permission-test.gcode");
        form.Add(new StringContent("true"), "print");

        HttpResponseMessage response = await client.PostAsync("/api/files/local", form);

        response.StatusCode.Should().Be(
            HttpStatusCode.Forbidden,
            "a caller with no queue:* permission must be denied by [RequirePermission] before the " +
            "action body (and therefore any enqueue) ever runs — see issue #1666");

        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        (await db.PrintJobs.CountAsync()).Should().Be(
            0,
            "no PrintJob row should ever be created for a request denied at the permission gate");
    }

    [Fact]
    public async Task Upload_ApiKeyUserBarredFromTargetPrinterGroup_Returns403AndCreatesNoPrintJob()
    {
        RestrictedPrinterFixture fixture = await SeedRestrictedPrinterFixtureAsync();
        using HttpClient client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", fixture.RawApiKey);

        using var form = new MultipartFormDataContent();
        using var fileContent = new ByteArrayContent(Encoding.UTF8.GetBytes(MinimalGcodeContent()));
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(fileContent, "file", "barred-group-test.gcode");
        form.Add(new StringContent("true"), "print");

        HttpResponseMessage response = await client.PostAsync(
            $"/api/files/local?printerId={fixture.RestrictedPrinterId}",
            form);

        response.StatusCode.Should().Be(
            HttpStatusCode.Forbidden,
            "an ApiKeyPurpose.OctoPrint key for a user with queue:write, but no submit access to " +
            "the target printer's group, must still be denied — the group-ACL check is " +
            "independent of the queue:write permission gate (see issue #1666)");

        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        (await db.PrintJobs.CountAsync(job => job.AssignedPrinterId == fixture.RestrictedPrinterId))
            .Should().Be(0, "no PrintJob row should ever be created against the barred printer");
    }

    [Fact]
    public async Task Upload_ApiKeyUserWithGroupAccess_SucceedsAndQueuesToThatPrinter()
    {
        RestrictedPrinterFixture fixture = await SeedRestrictedPrinterFixtureAsync(grantCallerAccess: true);
        using HttpClient client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", fixture.RawApiKey);

        using var form = new MultipartFormDataContent();
        using var fileContent = new ByteArrayContent(Encoding.UTF8.GetBytes(MinimalGcodeContent()));
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(fileContent, "file", "authorized-upload-test.gcode");
        form.Add(new StringContent("true"), "print");

        HttpResponseMessage response = await client.PostAsync(
            $"/api/files/local?printerId={fixture.RestrictedPrinterId}",
            form);

        string body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(
            HttpStatusCode.Accepted,
            $"a correctly-scoped OctoPrint key with submit access to the target printer's group " +
            $"must still be able to upload and enqueue end to end. Response body: {body}");

        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        (await db.PrintJobs.CountAsync(job => job.AssignedPrinterId == fixture.RestrictedPrinterId))
            .Should().Be(1, "the legitimate upload+print flow must still create exactly one PrintJob");
    }

    private static string MinimalGcodeContent() =>
        "; generated by OctoPrintUploadAuthorizationTests\nG28\nG1 X0 Y0 Z0.2 F1200\n";

    private async Task<RestrictedPrinterFixture> SeedRestrictedPrinterFixtureAsync(bool grantCallerAccess = false)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        DateTime now = DateTime.UtcNow;

        var manufacturer = new Manufacturer
        {
            Id = Guid.NewGuid(),
            Name = $"OctoPrint auth maker {Guid.NewGuid():N}",
        };
        var model = new PrinterModel
        {
            Id = Guid.NewGuid(),
            ManufacturerId = manufacturer.Id,
            Name = $"OctoPrint auth model {Guid.NewGuid():N}",
        };
        var restrictedGroup = new PrinterGroup
        {
            Id = Guid.NewGuid(),
            Name = $"OctoPrint auth restricted {Guid.NewGuid():N}",
        };
        var restrictedPrinter = new Printer
        {
            Id = Guid.NewGuid(),
            Name = $"OctoPrint auth printer {Guid.NewGuid():N}",
            ServerUrl = $"http://octoprint-auth-{Guid.NewGuid():N}",
            ManufacturerId = manufacturer.Id,
            ModelId = model.Id,
            PrinterGroupId = restrictedGroup.Id,
            IsEnabled = true,
            IsAvailable = true,
        };

        // A role distinct from the caller's, granted Submit access to the restricted group.
        // Because PrinterGroupAccess rows exist for this group, CanUserAccessGroupAsync denies
        // access to any role NOT listed here (see QueueResourceAuthorizationService) — an empty
        // rule set would instead default-allow everyone, which would defeat this fixture.
        var otherRole = new Role
        {
            Id = Guid.NewGuid(),
            Name = $"octoprint-auth-other-{Guid.NewGuid():N}",
            DisplayName = "OctoPrint auth other role",
            CreatedAt = now,
            UpdatedAt = now,
        };

        var callerRole = new Role
        {
            Id = Guid.NewGuid(),
            Name = $"octoprint-auth-caller-{Guid.NewGuid():N}",
            DisplayName = "OctoPrint auth caller role",
            CreatedAt = now,
            UpdatedAt = now,
        };

        Resource queueResource = await GetOrCreateResourceAsync(db, "queue");
        UserAction writeAction = await GetOrCreateActionAsync(db, "write");

        var callerUser = new User
        {
            Id = Guid.NewGuid(),
            Username = $"octoprint-auth-caller-{Guid.NewGuid():N}",
            Email = $"octoprint-auth-caller-{Guid.NewGuid():N}@example.test",
            PasswordHash = "unused",
            IsActive = true,
            EmailConfirmed = true,
            CreatedAt = now,
            UpdatedAt = now,
        };

        db.Manufacturers.Add(manufacturer);
        db.PrinterModels.Add(model);
        db.PrinterGroups.Add(restrictedGroup);
        db.Printers.Add(restrictedPrinter);
        db.Roles.AddRange(otherRole, callerRole);
        db.Users.Add(callerUser);
        db.PrinterGroupAccesses.Add(new PrinterGroupAccess
        {
            Id = Guid.NewGuid(),
            PrinterGroupId = restrictedGroup.Id,
            RoleId = grantCallerAccess ? callerRole.Id : otherRole.Id,
            AccessLevel = PrinterGroupAccessLevel.Submit,
        });
        db.RolePermissions.Add(new RolePermission
        {
            Id = Guid.NewGuid(),
            RoleId = callerRole.Id,
            ResourceId = queueResource.Id,
            ActionId = writeAction.Id,
            Granted = true,
            CreatedAt = now,
        });
        db.UserRoles.Add(new UserRole
        {
            Id = Guid.NewGuid(),
            UserId = callerUser.Id,
            RoleId = callerRole.Id,
            AssignedAt = now,
            IsActive = true,
        });

        string rawKey = $"octoprint-auth-key-{Guid.NewGuid():N}";
        db.ApiKeys.Add(new ApiKey
        {
            Id = Guid.NewGuid(),
            UserId = callerUser.Id,
            Name = "octoprint-auth-test-key",
            KeyHash = ComputeSha256Hash(rawKey),
            Purpose = ApiKeyPurpose.OctoPrint,
            IsActive = true,
            CreatedAt = now,
        });

        await db.SaveChangesAsync();

        return new RestrictedPrinterFixture(restrictedPrinter.Id, rawKey);
    }

    private static async Task<Resource> GetOrCreateResourceAsync(AppDbContext db, string name)
    {
        Resource? existing = await db.Resources.FirstOrDefaultAsync(r => r.Name == name);
        if (existing is not null)
        {
            return existing;
        }

        var resource = new Resource
        {
            Id = Guid.NewGuid(),
            Name = name,
            DisplayName = name,
            ResourceType = "system",
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        db.Resources.Add(resource);
        await db.SaveChangesAsync();
        return resource;
    }

    private static async Task<UserAction> GetOrCreateActionAsync(AppDbContext db, string name)
    {
        UserAction? existing = await db.UserActions.FirstOrDefaultAsync(a => a.Name == name);
        if (existing is not null)
        {
            return existing;
        }

        var action = new UserAction
        {
            Id = Guid.NewGuid(),
            Name = name,
            DisplayName = name,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        db.UserActions.Add(action);
        await db.SaveChangesAsync();
        return action;
    }

    private static string ComputeSha256Hash(string rawData) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawData)));

    private sealed record RestrictedPrinterFixture(Guid RestrictedPrinterId, string RawApiKey);
}
