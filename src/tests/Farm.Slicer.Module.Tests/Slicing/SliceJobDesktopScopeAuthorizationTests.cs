using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Farm.Infrastructure.Contracts.Auth;
using Farm.Infrastructure.Security;
using Farm.Infrastructure.Services.StorageManagement;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Farm.Slicer.Module.Tests.Slicing;

/// <summary>
/// Regression coverage for the Desktop-scope authorization gap found in review of issue #1770:
/// removing the uploader-ownership check from <c>Model3DStorageResolver</c> made any existing
/// <c>model3DId</c> resolvable, but <c>SlicingSubmit</c> is a distinct scope from
/// <c>ModelRead</c>/<c>LibrarySync</c>, so a Desktop-exchange token issued only for slicing
/// submission (e.g. calibration generation, issue #838) could otherwise reference - and thereby
/// read the metadata of - an arbitrary library model it was never granted read access to.
/// <c>SliceJobController.BindStoredModelAsync</c> now requires a Desktop-exchange token to also
/// carry <c>ModelRead</c> or <c>LibrarySync</c> before it can resolve a <c>model3DId</c>. Normal
/// login/session tokens (proven by every other test in
/// <see cref="SliceJobCanonicalSubmissionTests"/>, which authenticate as a worker) are unaffected.
/// </summary>
public sealed class SliceJobDesktopScopeAuthorizationTests : IAsyncLifetime, IDisposable
{
    private readonly CustomWebApplicationFactory _factory = new();
    private HttpClient _anonymousClient = null!;
    private Guid _ownerId;

    public async Task InitializeAsync()
    {
        await _factory.ResetDatabaseAsync();
        _anonymousClient = _factory.CreateClient();

        using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        User owner = new()
        {
            Id = Guid.NewGuid(),
            Username = "desktop-scope-owner",
            Email = "desktop-scope-owner@example.com",
            PasswordHash = "not-used-for-exchange-flow",
            FirstName = "Desktop",
            LastName = "Owner",
            IsActive = true,
            EmailConfirmed = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        context.Users.Add(owner);
        await context.SaveChangesAsync();
        _ownerId = owner.Id;
    }

    public Task DisposeAsync()
    {
        Dispose();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _anonymousClient?.Dispose();
        _factory?.Dispose();
    }

    private static string ComputeSha256Hash(string rawData) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawData)));

    /// <summary>
    /// Grants the owner every permission the scope set maps to, mirroring the real exchange flow's
    /// live owner-authorization intersection, so a positive case cannot fail merely because the
    /// mapped permission was never granted.
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
            Name = $"desktop-scope-role-{Guid.NewGuid():N}",
            DisplayName = "Desktop scope test role",
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
    private async Task<HttpClient> ExchangeClientAsync(ApiKeyScope scopes)
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
                Name = "desktop-scope-test-key",
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

        HttpClient client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("Authorization", $"Bearer {body!.Token}");
        return client;
    }

    private async Task<(Guid ModelId, string Sha256)> AddStoredModelAsync(Guid? ownerId)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        IStoragePathService storagePaths = scope.ServiceProvider.GetRequiredService<IStoragePathService>();
        SlicerDbContext db = scope.ServiceProvider.GetRequiredService<SlicerDbContext>();

        string root = storagePaths.GetModelUploadDirectory();
        Directory.CreateDirectory(root);
        string storedName = $"{Guid.NewGuid():N}.stl";
        string modelName = $"desktop-scope-test-model-{Guid.NewGuid():N}";
        byte[] bytes = Encoding.UTF8.GetBytes($"solid {modelName}\nendsolid {modelName}\n");
        await File.WriteAllBytesAsync(Path.Join(root, storedName), bytes);
        string hash = Convert.ToHexString(SHA256.HashData(bytes));

        Model3D model = new()
        {
            Id = Guid.NewGuid(),
            Name = "calibration.stl",
            FileName = storedName,
            FilePath = root,
            FileSizeBytes = bytes.Length,
            FileHash = hash,
            UploadedByUserId = ownerId,
            UploadedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        db.Models3D.Add(model);
        await db.SaveChangesAsync();
        return (model.Id, hash);
    }

    /// <summary>
    /// A minimal, validly-formed multipart form for the legacy slice-model route: an empty
    /// <see cref="MultipartFormDataContent"/> with no parts fails ASP.NET Core's form-reader with a
    /// "Form section has invalid Content-Disposition" 400 before the action (and its Desktop-scope
    /// guard) ever runs, so at least one well-formed field is required to reach that guard at all.
    /// </summary>
    private static MultipartFormDataContent BuildSliceModelForm() =>
        new() { { new StringContent("OrcaSlicer"), "slicerEngine" } };

    private static async Task<string?> ReadCodeAsync(HttpResponseMessage response)
    {
        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument document = JsonDocument.Parse(body);
        return document.RootElement.TryGetProperty("code", out JsonElement code) ? code.GetString() : null;
    }

    [Fact(DisplayName =
        "A Desktop token holding only slicing:submit cannot reference an existing library model")]
    public async Task SlicingSubmitOnlyToken_CannotReferenceLibraryModel()
    {
        (Guid modelId, _) = await AddStoredModelAsync(Guid.NewGuid());
        using HttpClient client = await ExchangeClientAsync(ApiKeyScope.SlicingSubmit);

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/slice", new SubmitSliceJobRequest
        {
            UserId = _ownerId,
            Model3DId = modelId,
            ModelFileName = "calibration.stl",
            SlicerEngine = SlicerEngineType.OrcaSlicer,
        });

        _ = response.StatusCode.Should().Be(
            HttpStatusCode.Forbidden,
            "slicing:submit alone never grants model-read authority (issue #1770 follow-up)");
        _ = (await ReadCodeAsync(response)).Should().Be("resource_forbidden");
    }

    [Fact(DisplayName = "A Desktop token holding slicing:submit + ModelRead can reference a library model")]
    public async Task SlicingSubmitTokenWithModelRead_CanReferenceLibraryModel()
    {
        (Guid modelId, string sha256) = await AddStoredModelAsync(Guid.NewGuid());
        using HttpClient client = await ExchangeClientAsync(
            ApiKeyScope.SlicingSubmit | ApiKeyScope.ModelRead);

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/slice", new SubmitSliceJobRequest
        {
            UserId = _ownerId,
            Model3DId = modelId,
            ModelFileName = "calibration.stl",
            SlicerEngine = SlicerEngineType.OrcaSlicer,
        });
        string body = await response.Content.ReadAsStringAsync();

        _ = response.StatusCode.Should().Be(HttpStatusCode.Created, body);
        using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        SlicerDbContext db = scope.ServiceProvider.GetRequiredService<SlicerDbContext>();
        SubmitSliceJobResponse submitted = JsonSerializer.Deserialize<SubmitSliceJobResponse>(
            body, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidOperationException("Missing submit response.");
        SliceJob job = await db.SliceJobs.AsNoTracking().SingleAsync(value => value.Id == submitted.JobId);
        _ = job.Model3DId.Should().Be(modelId);
        _ = job.ModelSha256.Should().Be(sha256);
    }

    [Fact(DisplayName = "A Desktop token holding slicing:submit + LibrarySync can reference a library model")]
    public async Task SlicingSubmitTokenWithLibrarySync_CanReferenceLibraryModel()
    {
        (Guid modelId, _) = await AddStoredModelAsync(Guid.NewGuid());
        using HttpClient client = await ExchangeClientAsync(
            ApiKeyScope.SlicingSubmit | ApiKeyScope.LibrarySync);

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/slice", new SubmitSliceJobRequest
        {
            UserId = _ownerId,
            Model3DId = modelId,
            ModelFileName = "calibration.stl",
            SlicerEngine = SlicerEngineType.OrcaSlicer,
        });
        string body = await response.Content.ReadAsStringAsync();

        _ = response.StatusCode.Should().Be(HttpStatusCode.Created, body);
    }

    /// <summary>
    /// The legacy <c>modelFileUrl</c> form can also point at a stored library model via the
    /// "/api/3d-models/file/{id}" route rather than an external location. Ralph/Vasquez's follow-up
    /// finding: <c>BindStoredModelAsync</c>'s original guard only covered <c>model3DId</c>, so a
    /// Desktop-exchange token lacking ModelRead/LibrarySync could route around it by submitting the
    /// same stored model through <c>modelFileUrl</c> instead - the worker later dereferences that URL
    /// via <c>TryGetStoredModelId</c> with no per-caller scope check. This must be forbidden
    /// identically to the model3DId path.
    /// </summary>
    [Fact(DisplayName =
        "A Desktop token holding only slicing:submit cannot reference a stored model via modelFileUrl")]
    public async Task SlicingSubmitOnlyToken_CannotReferenceLibraryModelViaModelFileUrl()
    {
        (Guid modelId, _) = await AddStoredModelAsync(Guid.NewGuid());
        using HttpClient client = await ExchangeClientAsync(ApiKeyScope.SlicingSubmit);

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/slice", new SubmitSliceJobRequest
        {
            UserId = _ownerId,
            ModelFileUrl = $"/api/3d-models/file/{modelId}",
            ModelFileName = "calibration.stl",
            SlicerEngine = SlicerEngineType.OrcaSlicer,
        });

        _ = response.StatusCode.Should().Be(
            HttpStatusCode.Forbidden,
            "the legacy modelFileUrl form must be guarded identically to model3DId (issue #1770 follow-up)");
        _ = (await ReadCodeAsync(response)).Should().Be("resource_forbidden");
    }

    /// <summary>
    /// Same gap as <see cref="SlicingSubmitOnlyToken_CannotReferenceLibraryModelViaModelFileUrl"/> but
    /// for the multi-model <c>modelFileUrls</c> array (each entry is checked independently in
    /// <c>SubmitAsync</c>'s URL-resolution loop, not inside <c>BindStoredModelAsync</c>).
    /// </summary>
    [Fact(DisplayName =
        "A Desktop token holding only slicing:submit cannot reference a stored model via modelFileUrls")]
    public async Task SlicingSubmitOnlyToken_CannotReferenceLibraryModelViaModelFileUrls()
    {
        (Guid modelId, _) = await AddStoredModelAsync(Guid.NewGuid());
        string storedUrl = $"/api/3d-models/file/{modelId}";
        using HttpClient client = await ExchangeClientAsync(ApiKeyScope.SlicingSubmit);

        // Deliberately leave ModelFileUrl unset (default string.Empty) so this test can only pass
        // via the ModelFileUrls array-loop guard, not by short-circuiting on the single-URL guard
        // (Bishop/Hicks review round 3: the prior version set both fields to the same URL, so the
        // single-URL guard rejected first and the test would still pass even if the per-entry
        // array guard were broken or removed).
        HttpResponseMessage response = await client.PostAsJsonAsync("/api/slice", new SubmitSliceJobRequest
        {
            UserId = _ownerId,
            ModelFileUrls = [storedUrl],
            ModelFileName = "calibration.stl",
            SlicerEngine = SlicerEngineType.OrcaSlicer,
        });

        _ = response.StatusCode.Should().Be(
            HttpStatusCode.Forbidden,
            "the multi-model modelFileUrls form must be guarded identically to model3DId (issue #1770 follow-up)");
        _ = (await ReadCodeAsync(response)).Should().Be("resource_forbidden");
    }

    [Fact(DisplayName =
        "A Desktop token holding slicing:submit + ModelRead can reference a stored model via modelFileUrl")]
    public async Task SlicingSubmitTokenWithModelRead_CanReferenceLibraryModelViaModelFileUrl()
    {
        (Guid modelId, _) = await AddStoredModelAsync(Guid.NewGuid());
        using HttpClient client = await ExchangeClientAsync(
            ApiKeyScope.SlicingSubmit | ApiKeyScope.ModelRead);

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/slice", new SubmitSliceJobRequest
        {
            UserId = _ownerId,
            ModelFileUrl = $"/api/3d-models/file/{modelId}",
            ModelFileName = "calibration.stl",
            SlicerEngine = SlicerEngineType.OrcaSlicer,
        });
        string body = await response.Content.ReadAsStringAsync();

        _ = response.StatusCode.Should().Be(HttpStatusCode.Created, body);
    }

    /// <summary>
    /// Bishop's review round-3 finding: the deprecated <c>POST /api/slicer/slice-model/{modelId}</c>
    /// route (<see cref="Farm.Slicer.Module.Api.Controllers.Slicing.SlicingSubmissionController"/>)
    /// is a structurally separate legacy code path from <c>SliceJobController</c> - it resolves any
    /// model by ID with no ownership/scope check of its own, and predates issue #1770 entirely - so
    /// every guard added above for <c>/api/slice</c> left this route fully exploitable by a
    /// submit-only-scoped Desktop token. Proves the guard now added to
    /// <c>SlicingSubmissionController.SubmitModelAsync</c> actually rejects the request before the
    /// legacy service ever touches the model or the orchestrator.
    /// </summary>
    [Fact(DisplayName =
        "A Desktop token holding only slicing:submit cannot reference a library model via the legacy slice-model route")]
    public async Task SlicingSubmitOnlyToken_CannotReferenceLibraryModelViaLegacySliceModelRoute()
    {
        (Guid modelId, _) = await AddStoredModelAsync(Guid.NewGuid());
        using HttpClient client = await ExchangeClientAsync(ApiKeyScope.SlicingSubmit);

        HttpResponseMessage response = await client.PostAsync(
            $"/api/slicer/slice-model/{modelId}", BuildSliceModelForm());
        string body = await response.Content.ReadAsStringAsync();

        _ = response.StatusCode.Should().Be(
            HttpStatusCode.Forbidden,
            $"the deprecated slice-model route must be guarded identically to /api/slice (issue #1770 follow-up): {body}");
        _ = (await ReadCodeAsync(response)).Should().Be("resource_forbidden");
    }

    [Fact(DisplayName =
        "A Desktop token holding slicing:submit + ModelRead can reference a library model via the legacy slice-model route")]
    public async Task SlicingSubmitTokenWithModelRead_CanReferenceLibraryModelViaLegacySliceModelRoute()
    {
        (Guid modelId, _) = await AddStoredModelAsync(Guid.NewGuid());
        using HttpClient client = await ExchangeClientAsync(
            ApiKeyScope.SlicingSubmit | ApiKeyScope.ModelRead);

        HttpResponseMessage response = await client.PostAsync(
            $"/api/slicer/slice-model/{modelId}", BuildSliceModelForm());
        string body = await response.Content.ReadAsStringAsync();

        _ = response.StatusCode.Should().NotBe(
            HttpStatusCode.Forbidden,
            $"ModelRead scope must clear the guard so the request reaches the legacy submission service: {body}");
    }
}
