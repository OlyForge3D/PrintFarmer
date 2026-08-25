using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Farm.Infrastructure.Contracts.Auth;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Security;
using Farm.Web.Api.Tests.TestInfrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Farm.Web.Api.Tests.Integration;

/// <summary>
/// End-to-end coverage for least-privilege Desktop calibration authorization: a Desktop key may
/// carry explicitly selected calibration/slicing/queue scopes, each of which becomes exactly one
/// <c>permission</c> claim on the exchanged token - but only while the key's owner independently
/// holds that permission, and never accompanied by a role claim.
/// </summary>
[Trait("Category", "DbHeavy")]
[Collection(IntegrationTestCollection.Name)]
[TestTiming]
public class DesktopCalibrationScopeIntegrationTests : IAsyncLifetime
{
    private readonly CustomWebApplicationFactory _factory;
    private HttpClient _anonymousClient = null!;
    private HttpClient _loginClient = null!;
    private Guid _ownerId;

    public DesktopCalibrationScopeIntegrationTests()
    {
        // DevModeBypassAuth would succeed every pending requirement on GET requests and mask the
        // very authorization decisions this class exists to prove.
        _factory = new CustomWebApplicationFactory(new Dictionary<string, string?>
        {
            ["Security:DevModeBypassAuth"] = "false"
        });
    }

    public async Task InitializeAsync()
    {
        await _factory.ResetDatabaseAsync();
        _anonymousClient = _factory.CreateClient();
        _loginClient = await _factory.CreateAuthenticatedClientAsync(
            "calibration-scope-owner",
            "calibration-scope-owner@example.com",
            "TestPassword123!");

        using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        User owner = await context.Users.SingleAsync(u => u.Username == "calibration-scope-owner");
        _ownerId = owner.Id;
    }

    public Task DisposeAsync()
    {
        _anonymousClient?.Dispose();
        _loginClient?.Dispose();
        _factory?.Dispose();
        return Task.CompletedTask;
    }

    private static string ComputeSha256Hash(string rawData) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawData)));

    private async Task<string> SeedApiKeyAsync(ApiKeyScope scopes, Guid? ownerId = null)
    {
        string rawKey = $"raw-{Guid.NewGuid():N}";

        using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        context.ApiKeys.Add(new ApiKey
        {
            Id = Guid.NewGuid(),
            UserId = ownerId ?? _ownerId,
            Name = "calibration-scope-test-key",
            KeyHash = ComputeSha256Hash(rawKey),
            Purpose = ApiKeyPurpose.Desktop,
            Scopes = scopes,
            IsActive = true,
            ExpiresAt = DateTime.UtcNow.AddDays(30)
        });
        await context.SaveChangesAsync();
        return rawKey;
    }

    /// <summary>
    /// Grants the owner a role carrying the given <c>resource:action</c> permissions, mirroring the
    /// real schema (<see cref="Resource"/> + <see cref="UserAction"/> + <see cref="RolePermission"/>)
    /// that <c>GetGrantedPermissionsAsync</c> reads.
    /// </summary>
    private async Task<Guid> GrantOwnerPermissionsAsync(params string[] permissions)
    {
        using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        Role role = new()
        {
            Id = Guid.NewGuid(),
            Name = $"calibration-scope-role-{Guid.NewGuid():N}",
            DisplayName = "Calibration scope test role",
            Description = "Grants calibration permissions for integration tests",
            IsSystemRole = false,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        db.Roles.Add(role);

        foreach (string permission in permissions)
        {
            (string resourceName, string actionName) = PrintFarmerPermissions.Split(permission);

            Resource? resource = await db.Resources.FirstOrDefaultAsync(r => r.Name == resourceName);
            if (resource is null)
            {
                resource = new Resource { Id = Guid.NewGuid(), Name = resourceName, CreatedAt = DateTime.UtcNow };
                db.Resources.Add(resource);
            }

            UserAction? action = await db.UserActions.FirstOrDefaultAsync(a => a.Name == actionName);
            if (action is null)
            {
                action = new UserAction { Id = Guid.NewGuid(), Name = actionName, CreatedAt = DateTime.UtcNow };
                db.UserActions.Add(action);
            }

            db.RolePermissions.Add(new RolePermission
            {
                Id = Guid.NewGuid(),
                RoleId = role.Id,
                ResourceId = resource.Id,
                ActionId = action.Id,
                Granted = true,
                CreatedAt = DateTime.UtcNow,
            });
        }

        db.UserRoles.Add(new UserRole
        {
            Id = Guid.NewGuid(),
            UserId = _ownerId,
            RoleId = role.Id,
            IsActive = true,
            AssignedAt = DateTime.UtcNow,
        });

        await db.SaveChangesAsync();
        return role.Id;
    }

    /// <summary>Deactivates a previously granted role assignment, simulating live revocation.</summary>
    private async Task RevokeOwnerRoleAsync(Guid roleId)
    {
        using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        UserRole assignment = await db.UserRoles.SingleAsync(ur => ur.UserId == _ownerId && ur.RoleId == roleId);
        assignment.IsActive = false;
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Deactivates the <b>role itself</b> rather than the assignment — the operation the role
    /// management API performs. Authority must be withdrawn just as promptly.
    /// </summary>
    private async Task DeactivateRoleAsync(Guid roleId)
    {
        using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Role role = await db.Roles.SingleAsync(r => r.Id == roleId);
        role.IsActive = false;
        await db.SaveChangesAsync();
    }

    private async Task<HttpResponseMessage> ExchangeAsync(string rawKey) =>
        await _anonymousClient.PostAsJsonAsync("/api/auth/api-key/exchange", new { apiKey = rawKey });

    private async Task<ApiKeyExchangeResponse> ExchangeForBodyAsync(string rawKey)
    {
        HttpResponseMessage response = await ExchangeAsync(rawKey);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        ApiKeyExchangeResponse? body = await response.Content.ReadFromJsonAsync<ApiKeyExchangeResponse>();
        body.Should().NotBeNull();
        return body!;
    }

    private HttpClient CreateBearerClient(string token)
    {
        HttpClient client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("Authorization", $"Bearer {token}");
        return client;
    }

    private async Task<HttpClient> ExchangeClientAsync(ApiKeyScope scopes)
    {
        string rawKey = await SeedApiKeyAsync(scopes);
        ApiKeyExchangeResponse body = await ExchangeForBodyAsync(rawKey);
        return CreateBearerClient(body.Token);
    }

    #region Calibration route reachability

    [Fact]
    public async Task ReadOnlyCalibrationToken_ReachesReadRoutesButIsForbiddenOnMutations()
    {
        await GrantOwnerPermissionsAsync(PrintFarmerPermissions.Calibration.Read);
        using HttpClient client = await ExchangeClientAsync(ApiKeyScope.CalibrationRead);

        HttpResponseMessage read = await client.GetAsync("/api/calibration-projects");
        read.StatusCode.Should().Be(HttpStatusCode.OK, "calibration:read was explicitly granted to this key");

        HttpResponseMessage create = await client.PostAsJsonAsync("/api/calibration-projects", new { name = "x" });
        create.StatusCode.Should().Be(HttpStatusCode.Forbidden, "calibration:create was not selected");

        HttpResponseMessage update = await client.PutAsJsonAsync(
            $"/api/calibration-projects/{Guid.NewGuid()}/drafts/step", new { });
        update.StatusCode.Should().Be(HttpStatusCode.Forbidden, "calibration:update was not selected");

        HttpResponseMessage generate = await client.PostAsJsonAsync(
            $"/api/calibration-projects/{Guid.NewGuid()}/generated-profiles", new { });
        generate.StatusCode.Should().Be(HttpStatusCode.Forbidden, "calibration:generate was not selected");
    }

    [Fact]
    public async Task TokenWithoutCalibrationRead_IsForbiddenOnCalibrationReadRoutes()
    {
        using HttpClient client = await ExchangeClientAsync(ApiKeyScope.ModelRead | ApiKeyScope.LibrarySync);

        HttpResponseMessage read = await client.GetAsync("/api/calibration-projects");
        read.StatusCode.Should().Be(HttpStatusCode.Forbidden, "model/library scopes never imply calibration authority");
    }

    #endregion

    #region Live revocation

    [Fact]
    public async Task RoleRevocation_DowngradesTheNextExchangeAndKeepsModelScopes()
    {
        Guid roleId = await GrantOwnerPermissionsAsync(PrintFarmerPermissions.Calibration.Read);
        string rawKey = await SeedApiKeyAsync(ApiKeyScope.ModelRead | ApiKeyScope.CalibrationRead);

        ApiKeyExchangeResponse before = await ExchangeForBodyAsync(rawKey);
        before.Scopes.Should().Contain("CalibrationRead");

        await RevokeOwnerRoleAsync(roleId);

        ApiKeyExchangeResponse after = await ExchangeForBodyAsync(rawKey);
        after.Scopes.Should().Contain("ModelRead", "unrelated model sync must keep working after a calibration revocation");
        after.Scopes.Should().NotContain("CalibrationRead");

        using HttpClient client = CreateBearerClient(after.Token);
        HttpResponseMessage read = await client.GetAsync("/api/calibration-projects");
        read.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task RevokedOnlyScope_FailsTheExchangeEntirely()
    {
        Guid roleId = await GrantOwnerPermissionsAsync(PrintFarmerPermissions.Calibration.Read);
        string rawKey = await SeedApiKeyAsync(ApiKeyScope.CalibrationRead);
        _ = await ExchangeForBodyAsync(rawKey);

        await RevokeOwnerRoleAsync(roleId);

        HttpResponseMessage response = await ExchangeAsync(rawKey);
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "nothing survives the intersection");
    }

    /// <summary>
    /// Deactivating the role itself — what the role-management API does — must withdraw authority
    /// just as promptly as deactivating an individual assignment. Otherwise a disabled role would
    /// keep authorizing Desktop keys indefinitely.
    /// </summary>
    [Fact]
    public async Task DeactivatingTheRoleItself_WithdrawsScopeAuthorityOnNextExchange()
    {
        Guid roleId = await GrantOwnerPermissionsAsync(PrintFarmerPermissions.Calibration.Read);
        string rawKey = await SeedApiKeyAsync(ApiKeyScope.ModelRead | ApiKeyScope.CalibrationRead);

        ApiKeyExchangeResponse before = await ExchangeForBodyAsync(rawKey);
        before.Scopes.Should().Contain("CalibrationRead");

        await DeactivateRoleAsync(roleId);

        ApiKeyExchangeResponse after = await ExchangeForBodyAsync(rawKey);
        after.Scopes.Should().NotContain(
            "CalibrationRead",
            "a deactivated role must stop conferring authority immediately");
        after.Scopes.Should().Contain("ModelRead", "unrelated model scopes are unaffected");

        using HttpClient client = CreateBearerClient(after.Token);
        HttpResponseMessage read = await client.GetAsync("/api/calibration-projects");
        read.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    /// <summary>
    /// The same rule must hold at provisioning time, or creation would mint a key the exchange
    /// immediately strips.
    /// </summary>
    [Fact]
    public async Task DeactivatingTheRoleItself_AlsoBlocksNewKeyCreation()
    {
        Guid roleId = await GrantOwnerPermissionsAsync(PrintFarmerPermissions.Calibration.Read);
        await DeactivateRoleAsync(roleId);

        using HttpResponseMessage create = await _loginClient.PostAsJsonAsync(
            $"/api/users/{_ownerId}/apikeys",
            new { name = "deactivated-role-key", purpose = "Desktop", scopeNames = new[] { "CalibrationRead" } });

        create.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    #endregion

    #region Capabilities

    [Fact]
    public async Task Capabilities_ReportExactEffectivePermissionsForAnExchangedToken()
    {
        await GrantOwnerPermissionsAsync(PrintFarmerPermissions.Calibration.Read);
        using HttpClient client = await ExchangeClientAsync(ApiKeyScope.CalibrationRead);

        using JsonDocument document = JsonDocument.Parse(
            await client.GetStringAsync("/api/calibration/capabilities"));
        JsonElement root = document.RootElement;

        List<string> effective = [.. root.GetProperty("effectivePermissions").EnumerateArray().Select(e => e.GetString()!)];
        effective.Should().Equal(PrintFarmerPermissions.Calibration.Read);

        JsonElement capabilities = root.GetProperty("effectiveCapabilities");
        capabilities.GetProperty("canRead").GetBoolean().Should().BeTrue();
        capabilities.GetProperty("canCreate").GetBoolean().Should().BeFalse();
        capabilities.GetProperty("canDelete").GetBoolean().Should().BeFalse();
        capabilities.GetProperty("canSubmitSlicing").GetBoolean().Should().BeFalse();
        capabilities.TryGetProperty("canGenerate", out _).Should().BeFalse(
            "canGenerate described generator-specific eligibility and was removed from the contract");
    }

    #endregion

    #region Admin-owned keys carry no admin authority

    [Fact]
    public async Task AdminOwnedExchangedToken_HasNoAdminRoleAndCannotReachAdminEndpoints()
    {
        await MakeOwnerFarmAdminAsync();

        using HttpClient client = await ExchangeClientAsync(ApiKeyScope.CalibrationRead);

        HttpResponseMessage calibrationRead = await client.GetAsync("/api/calibration-projects");
        calibrationRead.StatusCode.Should().Be(HttpStatusCode.OK, "the admin owner authorized the selected scope");

        // Not selected on the key, and the admin role is deliberately not copied into the token,
        // so the usual farm_admin bypass must not apply.
        HttpResponseMessage adminOnly = await client.GetAsync("/api/admin/overview");
        adminOnly.StatusCode.Should().Be(HttpStatusCode.Forbidden, "the exchanged token carries no farm_admin role");

        HttpResponseMessage otherUsersKeys = await client.GetAsync($"/api/users/{Guid.NewGuid()}/apikeys");
        otherUsersKeys.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    private async Task MakeOwnerFarmAdminAsync()
    {
        using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Role adminRole = await db.Roles.FirstAsync(r => r.Name == PrintFarmerPermissions.FarmAdminRole);
        if (!await db.UserRoles.AnyAsync(ur => ur.UserId == _ownerId && ur.RoleId == adminRole.Id))
        {
            db.UserRoles.Add(new UserRole
            {
                Id = Guid.NewGuid(),
                UserId = _ownerId,
                RoleId = adminRole.Id,
                IsActive = true,
                AssignedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }
    }

    #endregion

    #region Exchange tokens must not manage credentials

    /// <summary>
    /// A stolen 15-minute exchange token must never be usable to mint a replacement API key valid
    /// for up to a year - credential management requires a real interactive session. Covers every
    /// verb on the controller, since a gap on any one of them reopens the path.
    /// </summary>
    [Fact]
    public async Task ExchangedToken_CannotUseAnyApiKeyManagementVerb()
    {
        using HttpClient client = await ExchangeClientAsync(ApiKeyScope.ModelRead);
        string basePath = $"/api/users/{_ownerId}/apikeys";
        Guid keyId = Guid.NewGuid();

        List<(string Description, HttpResponseMessage Response)> responses =
        [
            ("list", await client.GetAsync(basePath)),
            ("create", await client.PostAsJsonAsync(basePath, new { name = "minted-by-exchange-token" })),
            ("toggle", await client.PatchAsync($"{basePath}/{keyId}/toggle", content: null)),
            ("rotate", await client.PostAsync($"{basePath}/{keyId}/rotate", content: null)),
            ("reveal", await client.GetAsync($"{basePath}/{keyId}/reveal")),
            ("delete", await client.DeleteAsync($"{basePath}/{keyId}")),
            ("settings", await client.GetAsync("/api/apikeys/settings")),
        ];

        foreach ((string description, HttpResponseMessage response) in responses)
        {
            response.StatusCode.Should().Be(
                HttpStatusCode.Forbidden,
                $"a Desktop-exchange token must not be able to {description} API keys");
            response.Dispose();
        }

        using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        (await db.ApiKeys.CountAsync(k => k.Name == "minted-by-exchange-token"))
            .Should().Be(0, "no key may be created by an exchange token");
    }

    /// <summary>
    /// The deny is scoped to exchange tokens only: a normal login session (which is what the admin
    /// UI uses) must keep working on exactly the same endpoints.
    /// </summary>
    [Fact]
    public async Task InteractiveLoginSession_CanStillManageApiKeys()
    {
        string basePath = $"/api/users/{_ownerId}/apikeys";

        using HttpResponseMessage list = await _loginClient.GetAsync(basePath);
        list.StatusCode.Should().Be(HttpStatusCode.OK);

        using HttpResponseMessage create = await _loginClient.PostAsJsonAsync(
            basePath,
            new { name = "created-by-login-session" });
        create.StatusCode.Should().Be(HttpStatusCode.OK);

        using HttpResponseMessage settings = await _loginClient.GetAsync("/api/apikeys/settings");
        settings.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    /// <summary>
    /// Same credential-laundering root cause as API-key minting: an exchange token carries the
    /// owner's identity, so plain <c>[Authorize]</c> would let a stolen token register an
    /// attacker-controlled passkey and bootstrap a full interactive login from it - or enumerate,
    /// rename, and delete the owner's existing credentials.
    /// </summary>
    [Fact]
    public async Task ExchangedToken_CannotRegisterOrManagePasskeyCredentials()
    {
        using HttpClient client = await ExchangeClientAsync(ApiKeyScope.ModelRead);

        List<(string Description, HttpResponseMessage Response)> responses =
        [
            ("begin registration", await client.PostAsync("/api/auth/passkey/register/begin", content: null)),
            ("complete registration", await client.PostAsJsonAsync("/api/auth/passkey/register/complete", new { })),
            ("list credentials", await client.GetAsync("/api/auth/passkey/credentials")),
            ("rename a credential", await client.PatchAsJsonAsync("/api/auth/passkey/credentials/1", new { deviceName = "attacker" })),
            ("delete a credential", await client.DeleteAsync("/api/auth/passkey/credentials/1")),
        ];

        foreach ((string description, HttpResponseMessage response) in responses)
        {
            response.StatusCode.Should().Be(
                HttpStatusCode.Forbidden,
                $"a Desktop-exchange token must not be able to {description}");
            response.Dispose();
        }

        using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        (await db.UserPasskeyCredentials.CountAsync(c => c.UserId == _ownerId))
            .Should().Be(0, "no passkey may be persisted by an exchange token");
    }

    /// <summary>
    /// The passkey deny must not break the browser flow: a normal login session still reaches
    /// registration and credential management. (A begin-registration ceremony legitimately
    /// succeeds; list returns the user's - currently empty - credential set.)
    /// </summary>
    [Fact]
    public async Task InteractiveLoginSession_CanStillUsePasskeyEndpoints()
    {
        using HttpResponseMessage begin = await _loginClient.PostAsync("/api/auth/passkey/register/begin", content: null);
        begin.StatusCode.Should().NotBe(
            HttpStatusCode.Forbidden,
            "an interactive session must still be able to start a passkey registration");

        using HttpResponseMessage list = await _loginClient.GetAsync("/api/auth/passkey/credentials");
        list.StatusCode.Should().Be(HttpStatusCode.OK);

        using HttpResponseMessage missing = await _loginClient.DeleteAsync("/api/auth/passkey/credentials/999999");
        missing.StatusCode.Should().Be(
            HttpStatusCode.NotFound,
            "authorization passes for an interactive session; only the credential itself is missing");
    }

    /// <summary>
    /// The passkey <i>login</i> ceremony is deliberately anonymous - the signed assertion is the
    /// credential being verified - so it must not have been swept up by the interactive-session
    /// policy.
    /// </summary>
    [Fact]
    public async Task PasskeyLoginCeremony_RemainsAnonymouslyReachable()
    {
        using HttpResponseMessage begin = await _anonymousClient.PostAsJsonAsync(
            "/api/auth/passkey/login/begin",
            new { username = "calibration-scope-owner" });

        begin.StatusCode.Should().NotBe(HttpStatusCode.Forbidden);
        begin.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized);
    }

    #endregion

    #region Scope contract over HTTP

    /// <summary>
    /// A key created through the API must round-trip its individual scope names, never the
    /// misleading composite alias.
    /// </summary>
    [Fact]
    public async Task CreatedKey_RoundTripsIndividualScopeNames()
    {
        using HttpResponseMessage create = await _loginClient.PostAsJsonAsync(
            $"/api/users/{_ownerId}/apikeys",
            new { name = "scope-names", purpose = "Desktop", scopeNames = new[] { "ModelRead", "ModelWrite", "LibrarySync" } });
        create.StatusCode.Should().Be(HttpStatusCode.OK);

        using JsonDocument created = JsonDocument.Parse(await create.Content.ReadAsStringAsync());
        List<string> createdNames =
            [.. created.RootElement.GetProperty("scopeNames").EnumerateArray().Select(e => e.GetString()!)];
        createdNames.Should().BeEquivalentTo(new[] { "ModelRead", "ModelWrite", "LibrarySync" });
        createdNames.Should().NotContain("All");

        using JsonDocument listed = JsonDocument.Parse(
            await _loginClient.GetStringAsync($"/api/users/{_ownerId}/apikeys"));
        JsonElement entry = listed.RootElement.EnumerateArray()
            .Single(e => e.GetProperty("name").GetString() == "scope-names");
        List<string> listedNames =
            [.. entry.GetProperty("scopeNames").EnumerateArray().Select(e => e.GetString()!)];
        listedNames.Should().BeEquivalentTo(new[] { "ModelRead", "ModelWrite", "LibrarySync" });
    }

    [Fact]
    public async Task CreatingPrivilegedKeyForUnauthorizedOwner_IsRejected()
    {
        using HttpResponseMessage create = await _loginClient.PostAsJsonAsync(
            $"/api/users/{_ownerId}/apikeys",
            new { name = "escalation-attempt", purpose = "Desktop", scopeNames = new[] { "CalibrationRead" } });

        create.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        (await db.ApiKeys.CountAsync(k => k.Name == "escalation-attempt")).Should().Be(0);
    }

    #endregion

    #region Queue scopes through real routes and policies

    /// <summary>
    /// Exercises the queue scopes through the real HTTP pipeline rather than only through the map,
    /// so a scope that maps correctly but cannot actually reach its route is still caught.
    /// </summary>
    [Fact]
    public async Task QueueReadScope_ReachesTheQueueListRoute()
    {
        await GrantOwnerPermissionsAsync(PrintFarmerPermissions.Queue.Read);
        using HttpClient client = await ExchangeClientAsync(ApiKeyScope.QueueRead);

        HttpResponseMessage list = await client.GetAsync("/api/job-queue");
        list.StatusCode.Should().Be(HttpStatusCode.OK, "queue:read was explicitly granted to this key");

        HttpResponseMessage dispatch = await client.PostAsJsonAsync(
            $"/api/job-queue/{Guid.NewGuid()}/dispatch", new { });
        dispatch.StatusCode.Should().Be(HttpStatusCode.Forbidden, "queue:start was not selected");
    }

    [Fact]
    public async Task TokenWithoutQueueRead_IsForbiddenOnTheQueueListRoute()
    {
        using HttpClient client = await ExchangeClientAsync(ApiKeyScope.ModelRead);

        HttpResponseMessage list = await client.GetAsync("/api/job-queue");
        list.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    /// <summary>
    /// The bed-clear route requires <c>queue:acknowledge-bed-clear</c> AND <c>queue:start</c>. A key
    /// holding only the former is a guaranteed-dead shape, which is exactly what the creation-time
    /// dependency rule now prevents - this proves the route really does demand both.
    /// </summary>
    [Fact]
    public async Task AcknowledgeBedClearWithoutQueueStart_IsForbiddenOnTheRealRoute()
    {
        await GrantOwnerPermissionsAsync(
            PrintFarmerPermissions.Queue.Read,
            PrintFarmerPermissions.Queue.AcknowledgeBedClear);
        using HttpClient client = await ExchangeClientAsync(
            ApiKeyScope.QueueRead | ApiKeyScope.QueueAcknowledgeBedClear);

        HttpResponseMessage ack = await client.PostAsJsonAsync(
            $"/api/job-queue/{Guid.NewGuid()}/acknowledge-bed-clear-and-start", new { });
        ack.StatusCode.Should().Be(HttpStatusCode.Forbidden, "queue:start is also required by this route");

        HttpResponseMessage ready = await client.PostAsJsonAsync(
            $"/api/auto-dispatch/{Guid.NewGuid()}/ready", new { });
        ready.StatusCode.Should().Be(HttpStatusCode.Forbidden, "the auto-dispatch ready route requires the same pair");
    }

    /// <summary>
    /// With the full trio the authorization layer is satisfied on every bed-clear route. The domain
    /// may still reject the random job/printer id, so this asserts only that the response is not a
    /// 403 - the authorization outcome is what is under test.
    /// </summary>
    [Fact]
    public async Task FullBedClearScopeSet_ClearsAuthorizationOnEveryBedClearRoute()
    {
        await GrantOwnerPermissionsAsync(
            PrintFarmerPermissions.Queue.Read,
            PrintFarmerPermissions.Queue.Start,
            PrintFarmerPermissions.Queue.AcknowledgeBedClear);
        using HttpClient client = await ExchangeClientAsync(
            ApiKeyScope.QueueRead | ApiKeyScope.QueueStart | ApiKeyScope.QueueAcknowledgeBedClear);

        List<(string Route, HttpResponseMessage Response)> responses =
        [
            ("job-queue acknowledge-bed-clear-and-start",
                await client.PostAsJsonAsync($"/api/job-queue/{Guid.NewGuid()}/acknowledge-bed-clear-and-start", new { })),
            ("auto-dispatch ready",
                await client.PostAsJsonAsync($"/api/auto-dispatch/{Guid.NewGuid()}/ready", new { })),
            ("auto-dispatch pre-clear",
                await client.PostAsJsonAsync($"/api/auto-dispatch/{Guid.NewGuid()}/pre-clear", new { })),
        ];

        foreach ((string route, HttpResponseMessage response) in responses)
        {
            response.StatusCode.Should().NotBe(
                HttpStatusCode.Forbidden,
                $"authorization must pass for {route} once the full scope set is granted");
            response.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized);
            response.Dispose();
        }
    }

    #endregion

    #region Slicer profile mutations are not reachable by a generation token

    private async Task<HttpClient> CreateGenerationTokenClientAsync()
    {
        await GrantOwnerPermissionsAsync(
            PrintFarmerPermissions.Calibration.Read,
            PrintFarmerPermissions.Calibration.Generate,
            PrintFarmerPermissions.Slicing.Submit);

        return await ExchangeClientAsync(
            ApiKeyScope.CalibrationRead |
            ApiKeyScope.CalibrationGenerate |
            ApiKeyScope.SlicingSubmit);
    }

    /// <summary>
    /// <c>CalibrationGenerate</c> mandates <c>SlicingSubmit</c>, and <c>ProfilesController</c> is
    /// class-gated by that single broad permission - so a legitimate calibration-generation token
    /// would otherwise also be able to upload, clone, and edit custom slicer profiles. Those
    /// mutations now additionally require an interactive session.
    /// </summary>
    [Fact]
    public async Task GenerationToken_IsForbiddenOnEverySlicerProfileMutation()
    {
        using HttpClient client = await CreateGenerationTokenClientAsync();

        List<(string Description, HttpResponseMessage Response)> responses =
        [
            ("upload a custom profile", await client.PostAsJsonAsync("/api/slicer/profiles/upload", new { rawJson = "{}", profileType = "process" })),
            ("clone a profile", await client.PostAsJsonAsync("/api/slicer/profiles/clone", new { sourceProfileId = Guid.NewGuid(), profileType = "process" })),
            ("update a custom profile", await client.PutAsJsonAsync($"/api/slicer/profiles/custom/{Guid.NewGuid()}", new { name = "renamed" })),
        ];

        foreach ((string description, HttpResponseMessage response) in responses)
        {
            response.StatusCode.Should().Be(
                HttpStatusCode.Forbidden,
                $"a Desktop-exchange token must not be able to {description}");
            response.Dispose();
        }
    }

    /// <summary>
    /// The deny must be surgical: the generation token still satisfies the authorization it was
    /// issued for. Reads on the profile catalog remain reachable, which is disclosed in the docs
    /// and in the UI scope description.
    /// </summary>
    [Fact]
    public async Task GenerationToken_StillSatisfiesItsIntendedSlicingAuthorization()
    {
        using HttpClient client = await CreateGenerationTokenClientAsync();

        HttpResponseMessage generate = await client.PostAsJsonAsync(
            $"/api/calibration-projects/{Guid.NewGuid()}/generated-profiles", new { });
        generate.StatusCode.Should().NotBe(
            HttpStatusCode.Forbidden,
            "calibration:generate + slicing:submit are both present");
        generate.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized);

        // Strict: the catalog read must actually succeed, not merely avoid a 403. A bare
        // NotBe(Forbidden) would also be satisfied by a 404 (wrong route) or a 503 (dependency
        // down), neither of which proves the scope reaches the endpoint.
        using HttpResponseMessage catalogRead = await client.GetAsync("/api/slicer/profiles");
        catalogRead.StatusCode.Should().Be(
            HttpStatusCode.OK,
            "profile catalog reads stay reachable for slicing:submit, and this is disclosed");

        // Paired negative on the same route with a real exchanged token that lacks slicing:submit,
        // proving the 200 above came from the scope rather than from an absent policy.
        using HttpClient withoutSubmit = await ExchangeClientAsync(ApiKeyScope.ModelRead);
        using HttpResponseMessage deniedCatalogRead = await withoutSubmit.GetAsync("/api/slicer/profiles");
        deniedCatalogRead.StatusCode.Should().Be(
            HttpStatusCode.Forbidden,
            "slicing:submit class-gates the profile catalog, so a token without it must be refused");
    }

    /// <summary>
    /// A normal login session must keep its existing behaviour on the same mutation endpoints -
    /// the policy denies only exchange tokens, so a session holding <c>slicing:submit</c> reaches
    /// the domain layer exactly as before.
    /// </summary>
    [Fact]
    public async Task InteractiveLoginSession_StillReachesSlicerProfileMutations()
    {
        await GrantOwnerPermissionsAsync(PrintFarmerPermissions.Slicing.Submit);

        // Re-authenticate: the client created in InitializeAsync holds a token minted before the
        // grant, and permission claims are baked into the JWT at login.
        using HttpClient client = await _factory.CreateAuthenticatedClientAsync(
            "calibration-scope-owner",
            "calibration-scope-owner@example.com",
            "TestPassword123!");

        using HttpResponseMessage upload = await client.PostAsJsonAsync(
            "/api/slicer/profiles/upload",
            new { rawJson = "{}", profileType = "process" });
        upload.StatusCode.Should().NotBe(
            HttpStatusCode.Forbidden,
            "an interactive session's existing access must be unchanged");
        upload.StatusCode.Should().NotBe(
            HttpStatusCode.Unauthorized,
            "a 401 would mean the fixture client is not authenticated, which would make the assertion above pass vacuously");

        using HttpResponseMessage update = await client.PutAsJsonAsync(
            $"/api/slicer/profiles/custom/{Guid.NewGuid()}",
            new { name = "renamed" });
        update.StatusCode.Should().NotBe(HttpStatusCode.Forbidden);
        update.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized);

        using HttpResponseMessage clone = await client.PostAsJsonAsync(
            "/api/slicer/profiles/clone",
            new { sourceProfileId = Guid.NewGuid(), profileType = "process" });
        clone.StatusCode.Should().NotBe(HttpStatusCode.Forbidden);
        clone.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized);
    }

    #endregion
}
