using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
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
/// Proves every granted Desktop scope boundary through the <b>real</b> HTTP pipeline - real
/// exchanged JWTs, real routing, real authorization policies - rather than through the scope map or
/// the token's claim set alone.
/// </summary>
/// <remarks>
/// <para>
/// A scope that maps to the right permission but cannot actually reach its route (or, worse, reaches
/// a route it should not) is invisible to map-level and claim-level tests. That gap is exactly how a
/// guaranteed-dead key shape - acknowledge-bed-clear without <c>queue:start</c> - survived earlier
/// review, so each boundary here is asserted in both directions: the scope reaches what it should,
/// and is refused everything adjacent.
/// </para>
/// <para>
/// <b>Reading the assertions.</b> A positive case asserts the request is neither 401 nor 403. It
/// deliberately does not assert 2xx: authorization runs before the domain, so a non-existent id or
/// an incomplete body legitimately yields 400/404/409/422 <i>after</i> the boundary under test has
/// already been cleared. Route existence is pinned separately and more strongly, by replaying the
/// same route with a deliberately under-scoped token and requiring a 403 — a mistyped path 404s for
/// that token too, so a positive assertion can never pass because the route does not exist.
/// </para>
/// </remarks>
[Trait("Category", "DbHeavy")]
[TestTiming]
public class DesktopScopeRouteMatrixIntegrationTests : IClassFixture<DesktopScopeRouteMatrixIntegrationTests.Factory>, IAsyncLifetime
{
    public class Factory : CustomWebApplicationFactory
    {
        public Factory()
            : base(new Dictionary<string, string?>
            {
                ["Security:DevModeBypassAuth"] = "false",
                // This class's host (and its singleton in-memory rate limiter) is shared
                // across every test via IClassFixture, and several tests here exchange the
                // same API key more than once. Raise the ceiling well above what any single
                // test performs so cumulative attempts across the whole class never trip the
                // default limit (5/minute) meant for a single client in production.
                ["RateLimiting:Authentication:MaxApiKeyExchangeAttemptsPerMinute"] = "1000"
            })
        {
        }
    }

    private readonly Factory _factory;
    private HttpClient _anonymousClient = null!;
    private HttpClient _unscopedClient = null!;
    private Guid _ownerId;

    public DesktopScopeRouteMatrixIntegrationTests(Factory factory)
    {
        _factory = factory;
    }

    public async Task InitializeAsync()
    {
        await _factory.ResetDataAsync();
        _anonymousClient = _factory.CreateClient();

        using HttpClient loginClient = await _factory.CreateAuthenticatedClientAsync(
            "scope-matrix-owner",
            "scope-matrix-owner@example.com",
            "TestPassword123!");

        using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        User owner = await context.Users.SingleAsync(u => u.Username == "scope-matrix-owner");
        _ownerId = owner.Id;

        // A model/library-only token: authenticated and accepted, but holding no calibration,
        // slicing, or queue permission. Used to pin that every route asserted positively below
        // really exists and really is permission-gated.
        _unscopedClient = await ScopedClientAsync(ApiKeyScope.ModelRead);
    }

    public Task DisposeAsync()
    {
        _anonymousClient?.Dispose();
        _unscopedClient?.Dispose();
        return Task.CompletedTask;
    }

    #region Fixture helpers

    private static string ComputeSha256Hash(string rawData) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawData)));

    /// <summary>
    /// Grants the owner every permission the scope set maps to, so the exchange's live
    /// owner-authorization intersection cannot silently drop a scope and turn a positive case into
    /// a false negative. The scope boundary, not the grant, is what is under test here.
    /// </summary>
    private async Task GrantOwnerPermissionsForAsync(ApiKeyScope scopes)
    {
        IReadOnlyList<string> permissions =
            Farm.Infrastructure.Authorization.DesktopScopePermissionMap.GetPermissions(scopes);
        if (permissions.Count == 0)
        {
            return;
        }

        using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        Role role = new()
        {
            Id = Guid.NewGuid(),
            Name = $"scope-matrix-role-{Guid.NewGuid():N}",
            DisplayName = "Scope matrix test role",
            Description = "Grants exactly the permissions the key under test selects",
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
    }

    /// <summary>
    /// Seeds a Desktop key with exactly <paramref name="scopes"/>, grants the owner the matching
    /// permissions, and exchanges it through the real endpoint for a real JWT.
    /// </summary>
    private async Task<HttpClient> ScopedClientAsync(ApiKeyScope scopes)
    {
        await GrantOwnerPermissionsForAsync(scopes);

        string rawKey = $"raw-{Guid.NewGuid():N}";
        using (AsyncServiceScope scope = _factory.Services.CreateAsyncScope())
        {
            AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            context.ApiKeys.Add(new ApiKey
            {
                Id = Guid.NewGuid(),
                UserId = _ownerId,
                Name = "scope-matrix-key",
                KeyHash = ComputeSha256Hash(rawKey),
                Purpose = ApiKeyPurpose.Desktop,
                Scopes = scopes,
                IsActive = true,
                ExpiresAt = DateTime.UtcNow.AddDays(30),
            });
            await context.SaveChangesAsync();
        }

        HttpResponseMessage response = await _anonymousClient.PostAsJsonAsync(
            "/api/auth/api-key/exchange", new { apiKey = rawKey });
        response.StatusCode.Should().Be(
            HttpStatusCode.OK,
            "the seeded key is active, in-scope, and its owner holds every mapped permission");

        ApiKeyExchangeResponse? body = await response.Content.ReadFromJsonAsync<ApiKeyExchangeResponse>();
        body.Should().NotBeNull();

        // The exchange must have kept every selected scope; otherwise a positive assertion below
        // could fail for a reason unrelated to routing.
        body!.Scopes.Should().BeEquivalentTo(
            Farm.Infrastructure.Authorization.DesktopScopePermissionMap.GetScopeNames(scopes),
            "the effective mask must equal the requested mask when the owner holds everything");

        HttpClient client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("Authorization", $"Bearer {body.Token}");
        return client;
    }

    /// <summary>
    /// Asserts the authorization boundary was cleared for <paramref name="response"/>.
    /// </summary>
    /// <remarks>
    /// Deliberately not a 2xx assertion: authorization runs before the domain, so a non-existent id
    /// or an incomplete body legitimately yields 400/404/409/422 <i>after</i> the boundary under
    /// test has been cleared. Route existence is therefore pinned separately, by
    /// <see cref="AssertRouteIsAuthorizationGatedAsync"/> — a typo'd path 404s for the denied client
    /// too and fails that check, so it can never make this assertion pass for free.
    /// </remarks>
    private static void ShouldClearAuthorization(HttpResponseMessage response, string because)
    {
        response.StatusCode.Should().NotBe(HttpStatusCode.Forbidden, because);
        response.StatusCode.Should().NotBe(
            HttpStatusCode.Unauthorized,
            "a 401 would mean the token was not accepted at all, making the assertion above vacuous");
    }

    private static void ShouldBeDenied(HttpResponseMessage response, string because) =>
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden, because);

    /// <summary>
    /// Builds a minimal but <b>bindable</b> multipart body for the artifact upload route.
    /// </summary>
    /// <remarks>
    /// The slicer module's <c>RequirePermissionAttribute</c> is an action filter, so it runs
    /// <i>after</i> model binding — unlike the main API's, which is an authorization requirement.
    /// An empty multipart body therefore fails binding with a 400 before the permission check is
    /// ever reached, which would make both the pinning assertion and the positive case meaningless.
    /// </remarks>
    private static MultipartFormDataContent CreateArtifactUploadContent()
    {
        MultipartFormDataContent content = [];
        ByteArrayContent file = new([0x47, 0x32, 0x38, 0x0A]);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Add(file, "file", "artifact.gcode");
        return content;
    }

    /// <summary>
    /// Pins that a route template really exists and really is authorization-gated, by proving a
    /// deliberately under-scoped token receives 403 there. A mistyped path yields 404 instead and
    /// fails this check, which is what stops the paired positive assertion passing vacuously.
    /// </summary>
    private async Task AssertRouteIsAuthorizationGatedAsync(HttpMethod method, string path)
    {
        using HttpRequestMessage request = new(method, path);
        if (method != HttpMethod.Get && method != HttpMethod.Delete)
        {
            // The artifact upload route binds an IFormFile behind an action-filter permission
            // check, so the body must actually bind or the 400 pre-empts the 403.
            request.Content = path.StartsWith("/api/artifacts", StringComparison.Ordinal)
                ? CreateArtifactUploadContent()
                : JsonContent.Create(new { });
        }

        using HttpResponseMessage response = await _unscopedClient.SendAsync(request);
        response.StatusCode.Should().Be(
            HttpStatusCode.Forbidden,
            $"{method} {path} must exist and be permission-gated; a 404 here would mean the route template is wrong");
    }

    #endregion

    #region Calibration write boundary

    /// <summary>
    /// Create/update/delete are three separate permissions. A key holding all three must clear
    /// their routes.
    /// </summary>
    /// <remarks>
    /// The generate/publish routes this test used to also assert against were removed by D3a
    /// (issue #1980) along with <c>CalibrationGeneratedProfilesController</c> and the
    /// generated-profile endpoints on <c>CalibrationProjectsController</c>. The
    /// <c>CalibrationGenerate</c>/<c>CalibrationPublish</c> scopes and permissions still exist
    /// (they power <c>CalibrationCapabilityService</c>'s UI capability flags), but no HTTP route
    /// enforces them anymore, so pinning them here would be vacuous or outright fail with a 404.
    /// </remarks>
    [Fact]
    public async Task CalibrationWriteScopes_ClearWriteRoutes()
    {
        using HttpClient client = await ScopedClientAsync(
            ApiKeyScope.CalibrationRead |
            ApiKeyScope.CalibrationCreate |
            ApiKeyScope.CalibrationUpdate |
            ApiKeyScope.CalibrationDelete);

        Guid projectId = Guid.NewGuid();

        await AssertRouteIsAuthorizationGatedAsync(HttpMethod.Post, "/api/calibration-projects");
        await AssertRouteIsAuthorizationGatedAsync(HttpMethod.Put, $"/api/calibration-projects/{projectId}/drafts/step-one");
        await AssertRouteIsAuthorizationGatedAsync(HttpMethod.Delete, $"/api/calibration-projects/{projectId}");

        using HttpResponseMessage create = await client.PostAsJsonAsync(
            "/api/calibration-projects", new { name = "scope-matrix-created" });
        ShouldClearAuthorization(create, "calibration:create was granted");

        using HttpResponseMessage update = await client.PutAsJsonAsync(
            $"/api/calibration-projects/{projectId}/drafts/step-one", new { value = 1 });
        ShouldClearAuthorization(update, "calibration:update was granted");

        using HttpResponseMessage delete = await client.DeleteAsync($"/api/calibration-projects/{projectId}");
        ShouldClearAuthorization(delete, "calibration:delete was granted");
    }

    #endregion

    #region Queue write boundary

    [Fact]
    public async Task QueueWriteScope_ClearsEnqueueButIsDeniedStartAndCancel()
    {
        using HttpClient client = await ScopedClientAsync(ApiKeyScope.QueueRead | ApiKeyScope.QueueWrite);

        await AssertRouteIsAuthorizationGatedAsync(HttpMethod.Post, "/api/job-queue");
        await AssertRouteIsAuthorizationGatedAsync(HttpMethod.Put, $"/api/job-queue/{Guid.NewGuid()}");

        using HttpResponseMessage enqueue = await client.PostAsJsonAsync(
            "/api/job-queue", new { gcodeFileId = Guid.NewGuid() });
        ShouldClearAuthorization(enqueue, "queue:write was granted");

        using HttpResponseMessage update = await client.PutAsJsonAsync(
            $"/api/job-queue/{Guid.NewGuid()}", new { priority = 1 });
        update.StatusCode.Should().NotBe(HttpStatusCode.Forbidden, "queue:write was granted");
        update.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized);

        using HttpResponseMessage dispatch = await client.PostAsJsonAsync(
            $"/api/job-queue/{Guid.NewGuid()}/dispatch", new { });
        ShouldBeDenied(dispatch, "queue:start was not selected");

        using HttpResponseMessage cancel = await client.PostAsJsonAsync(
            $"/api/job-queue/{Guid.NewGuid()}/cancel", new { });
        ShouldBeDenied(cancel, "queue:cancel was not selected");
    }

    #endregion

    #region Queue cancel boundary

    [Fact]
    public async Task QueueCancelScope_ClearsCancelButIsDeniedStartAndWrite()
    {
        using HttpClient client = await ScopedClientAsync(ApiKeyScope.QueueRead | ApiKeyScope.QueueCancel);

        await AssertRouteIsAuthorizationGatedAsync(HttpMethod.Post, $"/api/job-queue/{Guid.NewGuid()}/cancel");
        await AssertRouteIsAuthorizationGatedAsync(HttpMethod.Post, $"/api/job-queue/{Guid.NewGuid()}/abort-print");

        using HttpResponseMessage cancel = await client.PostAsJsonAsync(
            $"/api/job-queue/{Guid.NewGuid()}/cancel", new { });
        cancel.StatusCode.Should().NotBe(HttpStatusCode.Forbidden, "queue:cancel was granted");
        cancel.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized);

        using HttpResponseMessage abort = await client.PostAsJsonAsync(
            $"/api/job-queue/{Guid.NewGuid()}/abort-print", new { });
        abort.StatusCode.Should().NotBe(HttpStatusCode.Forbidden, "abort-print is also queue:cancel");
        abort.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized);

        using HttpResponseMessage dispatch = await client.PostAsJsonAsync(
            $"/api/job-queue/{Guid.NewGuid()}/dispatch", new { });
        ShouldBeDenied(dispatch, "queue:start was not selected");

        using HttpResponseMessage enqueue = await client.PostAsJsonAsync(
            "/api/job-queue", new { gcodeFileId = Guid.NewGuid() });
        ShouldBeDenied(enqueue, "queue:write was not selected");
    }

    #endregion

    #region Queue start boundary

    [Fact]
    public async Task QueueStartScope_ClearsDispatchButIsDeniedCancelAndWrite()
    {
        using HttpClient client = await ScopedClientAsync(ApiKeyScope.QueueRead | ApiKeyScope.QueueStart);

        await AssertRouteIsAuthorizationGatedAsync(HttpMethod.Post, $"/api/job-queue/{Guid.NewGuid()}/dispatch");
        await AssertRouteIsAuthorizationGatedAsync(HttpMethod.Post, $"/api/job-queue/{Guid.NewGuid()}/dispatch-to");

        using HttpResponseMessage dispatch = await client.PostAsJsonAsync(
            $"/api/job-queue/{Guid.NewGuid()}/dispatch", new { });
        dispatch.StatusCode.Should().NotBe(HttpStatusCode.Forbidden, "queue:start was granted");
        dispatch.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized);

        using HttpResponseMessage dispatchTo = await client.PostAsJsonAsync(
            $"/api/job-queue/{Guid.NewGuid()}/dispatch-to", new { printerId = Guid.NewGuid() });
        dispatchTo.StatusCode.Should().NotBe(HttpStatusCode.Forbidden, "dispatch-to is also queue:start");
        dispatchTo.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized);

        using HttpResponseMessage cancel = await client.PostAsJsonAsync(
            $"/api/job-queue/{Guid.NewGuid()}/cancel", new { });
        ShouldBeDenied(cancel, "queue:cancel was not selected");

        using HttpResponseMessage enqueue = await client.PostAsJsonAsync(
            "/api/job-queue", new { gcodeFileId = Guid.NewGuid() });
        ShouldBeDenied(enqueue, "queue:write was not selected");
    }

    #endregion

    #region Queue read is read-only, and bed-clear needs the pair

    [Fact]
    public async Task QueueReadScope_IsReadOnlyAcrossEveryQueueMutation()
    {
        using HttpClient client = await ScopedClientAsync(ApiKeyScope.QueueRead);

        using HttpResponseMessage list = await client.GetAsync("/api/job-queue");
        list.StatusCode.Should().Be(HttpStatusCode.OK, "queue:read was granted");

        List<(string Description, HttpResponseMessage Response)> denied =
        [
            ("enqueue", await client.PostAsJsonAsync("/api/job-queue", new { gcodeFileId = Guid.NewGuid() })),
            ("update", await client.PutAsJsonAsync($"/api/job-queue/{Guid.NewGuid()}", new { priority = 1 })),
            ("dispatch", await client.PostAsJsonAsync($"/api/job-queue/{Guid.NewGuid()}/dispatch", new { })),
            ("cancel", await client.PostAsJsonAsync($"/api/job-queue/{Guid.NewGuid()}/cancel", new { })),
            ("delete", await client.DeleteAsync($"/api/job-queue/{Guid.NewGuid()}")),
            ("acknowledge bed clear", await client.PostAsJsonAsync($"/api/job-queue/{Guid.NewGuid()}/acknowledge-bed-clear-and-start", new { })),
        ];

        foreach ((string description, HttpResponseMessage response) in denied)
        {
            response.StatusCode.Should().Be(
                HttpStatusCode.Forbidden,
                $"a read-only queue token must not be able to {description}");
            response.Dispose();
        }
    }

    /// <summary>
    /// The bed-clear routes check <c>queue:acknowledge-bed-clear</c> AND <c>queue:start</c>. Holding
    /// either alone is a dead key shape, which is why the creation-time dependency rule forces both.
    /// </summary>
    [Fact]
    public async Task BedClearRoutes_RequireAcknowledgeAndStartTogether()
    {
        using HttpClient startOnly = await ScopedClientAsync(ApiKeyScope.QueueRead | ApiKeyScope.QueueStart);
        using HttpResponseMessage deniedWithoutAck = await startOnly.PostAsJsonAsync(
            $"/api/job-queue/{Guid.NewGuid()}/acknowledge-bed-clear-and-start", new { });
        ShouldBeDenied(deniedWithoutAck, "queue:acknowledge-bed-clear is also required");

        using HttpClient both = await ScopedClientAsync(
            ApiKeyScope.QueueRead | ApiKeyScope.QueueStart | ApiKeyScope.QueueAcknowledgeBedClear);

        await AssertRouteIsAuthorizationGatedAsync(HttpMethod.Post, $"/api/job-queue/{Guid.NewGuid()}/acknowledge-bed-clear-and-start");
        await AssertRouteIsAuthorizationGatedAsync(HttpMethod.Post, $"/api/auto-dispatch/{Guid.NewGuid()}/ready");
        await AssertRouteIsAuthorizationGatedAsync(HttpMethod.Post, $"/api/auto-dispatch/{Guid.NewGuid()}/pre-clear");

        using HttpResponseMessage jobQueueAck = await both.PostAsJsonAsync(
            $"/api/job-queue/{Guid.NewGuid()}/acknowledge-bed-clear-and-start", new { });
        jobQueueAck.StatusCode.Should().NotBe(HttpStatusCode.Forbidden, "both permissions are present");
        jobQueueAck.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized);

        using HttpResponseMessage ready = await both.PostAsJsonAsync(
            $"/api/auto-dispatch/{Guid.NewGuid()}/ready", new { });
        ready.StatusCode.Should().NotBe(HttpStatusCode.Forbidden, "both permissions are present");
        ready.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized);

        using HttpResponseMessage preClear = await both.PostAsJsonAsync(
            $"/api/auto-dispatch/{Guid.NewGuid()}/pre-clear", new { });
        preClear.StatusCode.Should().NotBe(HttpStatusCode.Forbidden, "both permissions are present");
        preClear.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized);
    }

    #endregion

    #region Slicing artifact boundary

    /// <summary>
    /// <c>SlicingReadArtifact</c> and <c>SlicingSubmit</c> gate different halves of
    /// <c>ArtifactsController</c>: reads require <c>slicing:read-artifact</c>, while uploading a
    /// job's artifact requires <c>slicing:submit</c>. Neither is exercised by the calibration or
    /// queue routes, so without this test the scope would be proven only through the map.
    /// </summary>
    [Fact]
    public async Task SlicingReadArtifactScope_ClearsArtifactReadsButNotArtifactUpload()
    {
        using HttpClient client = await ScopedClientAsync(ApiKeyScope.SlicingReadArtifact);

        await AssertRouteIsAuthorizationGatedAsync(HttpMethod.Get, $"/api/artifacts/{Guid.NewGuid()}");
        await AssertRouteIsAuthorizationGatedAsync(HttpMethod.Get, $"/api/artifacts/job/{Guid.NewGuid()}");
        await AssertRouteIsAuthorizationGatedAsync(HttpMethod.Get, $"/api/artifacts/{Guid.NewGuid()}/metadata");
        await AssertRouteIsAuthorizationGatedAsync(HttpMethod.Post, $"/api/artifacts/{Guid.NewGuid()}");

        using HttpResponseMessage read = await client.GetAsync($"/api/artifacts/{Guid.NewGuid()}");
        ShouldClearAuthorization(read, "slicing:read-artifact was granted");

        using HttpResponseMessage listForJob = await client.GetAsync($"/api/artifacts/job/{Guid.NewGuid()}");
        ShouldClearAuthorization(listForJob, "slicing:read-artifact was granted");

        using HttpResponseMessage metadata = await client.GetAsync($"/api/artifacts/{Guid.NewGuid()}/metadata");
        ShouldClearAuthorization(metadata, "slicing:read-artifact was granted");

        using MultipartFormDataContent uploadContent = CreateArtifactUploadContent();
        using HttpResponseMessage upload = await client.PostAsync($"/api/artifacts/{Guid.NewGuid()}", uploadContent);
        ShouldBeDenied(upload, "slicing:submit was not selected, and artifact upload requires it");
    }

    /// <summary>
    /// The mirror image: a submit-only token clears artifact upload but cannot read artifacts back.
    /// </summary>
    [Fact]
    public async Task SlicingSubmitScope_ClearsArtifactUploadButNotArtifactReads()
    {
        using HttpClient client = await ScopedClientAsync(ApiKeyScope.SlicingSubmit);

        using MultipartFormDataContent uploadContent = CreateArtifactUploadContent();
        using HttpResponseMessage upload = await client.PostAsync($"/api/artifacts/{Guid.NewGuid()}", uploadContent);
        ShouldClearAuthorization(upload, "slicing:submit was granted, and artifact upload requires it");

        using HttpResponseMessage read = await client.GetAsync($"/api/artifacts/{Guid.NewGuid()}");
        ShouldBeDenied(read, "slicing:read-artifact was not selected");

        using HttpResponseMessage listForJob = await client.GetAsync($"/api/artifacts/job/{Guid.NewGuid()}");
        ShouldBeDenied(listForJob, "slicing:read-artifact was not selected");
    }

    #endregion

    #region Cross-boundary isolation

    /// <summary>
    /// Calibration and queue authority must not bleed into one another, and neither may be reached
    /// by a model/library-only key.
    /// </summary>
    [Fact]
    public async Task ModelOnlyKey_ReachesNeitherCalibrationNorQueue()
    {
        using HttpClient client = await ScopedClientAsync(
            ApiKeyScope.ModelRead | ApiKeyScope.ModelWrite | ApiKeyScope.LibrarySync);

        List<(string Description, HttpResponseMessage Response)> denied =
        [
            ("read calibration projects", await client.GetAsync("/api/calibration-projects")),
            ("read the queue", await client.GetAsync("/api/job-queue")),
            ("enqueue a job", await client.PostAsJsonAsync("/api/job-queue", new { gcodeFileId = Guid.NewGuid() })),
        ];

        foreach ((string description, HttpResponseMessage response) in denied)
        {
            response.StatusCode.Should().Be(
                HttpStatusCode.Forbidden,
                $"model/library scopes never imply permission to {description}");
            response.Dispose();
        }
    }

    [Fact]
    public async Task CalibrationOnlyKey_CannotReachTheQueue()
    {
        using HttpClient client = await ScopedClientAsync(
            ApiKeyScope.CalibrationRead | ApiKeyScope.CalibrationCreate);

        using HttpResponseMessage queueRead = await client.GetAsync("/api/job-queue");
        ShouldBeDenied(queueRead, "queue:read was not selected");

        using HttpResponseMessage calibrationRead = await client.GetAsync("/api/calibration-projects");
        calibrationRead.StatusCode.Should().Be(HttpStatusCode.OK, "calibration:read was granted");
    }

    /// <summary>
    /// The mirror of the above: a queue/print-capable key carries real physical-actuation authority
    /// but must not reach calibration at all.
    /// </summary>
    /// <remarks>
    /// This used to also assert against the generated-profile generate/publish routes; those were
    /// removed by D3a (issue #1980) along with <c>CalibrationGeneratedProfilesController</c>.
    /// </remarks>
    [Fact]
    public async Task QueuePrintScopedKey_CannotReachAnyCalibrationRoute()
    {
        using HttpClient client = await ScopedClientAsync(
            ApiKeyScope.QueueRead |
            ApiKeyScope.QueueWrite |
            ApiKeyScope.QueueStart |
            ApiKeyScope.QueueCancel);

        // Positive control: this key really does hold queue authority, so the calibration denials
        // below cannot be explained by the token being rejected outright.
        using HttpResponseMessage queueList = await client.GetAsync("/api/job-queue");
        queueList.StatusCode.Should().Be(HttpStatusCode.OK, "queue:read was granted");

        using HttpResponseMessage dispatch = await client.PostAsJsonAsync(
            $"/api/job-queue/{Guid.NewGuid()}/dispatch", new { });
        dispatch.StatusCode.Should().NotBe(HttpStatusCode.Forbidden, "queue:start was granted");
        dispatch.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized);

        List<(string Description, HttpResponseMessage Response)> denied =
        [
            ("read calibration projects", await client.GetAsync("/api/calibration-projects")),
            ("create a calibration project",
                await client.PostAsJsonAsync("/api/calibration-projects", new { name = "denied" })),
        ];

        foreach ((string description, HttpResponseMessage response) in denied)
        {
            response.StatusCode.Should().Be(
                HttpStatusCode.Forbidden,
                $"queue scopes never imply permission to {description}");
            response.Dispose();
        }
    }

    #endregion
}
