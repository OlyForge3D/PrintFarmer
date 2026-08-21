using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Farm.Infrastructure.Authorization;
using Farm.Infrastructure.Contracts.Auth;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.PrinterCalibration;
using Farm.Infrastructure.Security;
using Farm.Web.Api.Contracts;
using Farm.Web.Api.Services.Calibration.Generation;
using Farm.Web.Api.Tests.TestInfrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Farm.Web.Api.Tests.Calibration.Generation;

/// <summary>
/// Regression coverage for the Desktop-scope authorization gap found in round-4 review of issue
/// #1770 (Bishop non-blocking note, escalated to blocking by Hicks): <c>FinalVerificationCalibrationOptions.Model3DId</c>
/// resolves any library model via the now-permissive <c>Model3DStorageResolver</c>
/// (<see cref="Farm.Slicer.Module.Services.Model3DStorageResolver"/>) with no ModelRead/LibrarySync check of its own.
/// A Desktop-exchange token holding only <c>Calibration.Generate</c> + <c>Slicing.Submit</c> scope could reference an
/// arbitrary library model through calibration generation, bypassing every guard already applied to
/// <c>SliceJobController</c>/<c>SlicingSubmissionController</c>. <see cref="Farm.Web.Api.Controllers.CalibrationGenerationController.GenerateJobAsync"/>
/// now applies the same <see cref="DesktopScopeClaims.IsMissingModelScope"/> guard whenever the request carries a
/// <see cref="CalibrationMethodOptionsRequest.Model3DId"/>. Normal login/session tokens (proven by every other test
/// in <see cref="CalibrationGenerationApiTests"/>, which authenticate via the Test scheme) are unaffected.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public sealed class CalibrationGenerationDesktopScopeAuthorizationTests : IAsyncLifetime
{
    private readonly CustomWebApplicationFactory _factory = new(
        new Dictionary<string, string?>
        {
            ["Security:DevModeBypassAuth"] = "false",
        });

    private HttpClient _anonymousClient = null!;
    private Guid _ownerId;
    private Guid _projectId;
    private Guid _attemptId;

    public async Task InitializeAsync()
    {
        await _factory.ResetDatabaseAsync();
        _anonymousClient = _factory.CreateClient();

        using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext core = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        User owner = new()
        {
            Id = Guid.NewGuid(),
            Username = "calibration-desktop-scope-owner",
            Email = "calibration-desktop-scope-owner@example.com",
            PasswordHash = "not-used-for-exchange-flow",
            FirstName = "Calibration",
            LastName = "Owner",
            IsActive = true,
            EmailConfirmed = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        core.Users.Add(owner);
        _ownerId = owner.Id;

        Guid manufacturerId = Guid.NewGuid();
        Guid modelId = Guid.NewGuid();
        Guid printerId = Guid.NewGuid();
        Guid snapshotId = Guid.NewGuid();
        _projectId = Guid.NewGuid();
        _attemptId = Guid.NewGuid();
        DateTime nowUtc = DateTime.UtcNow;

        core.Manufacturers.Add(new Manufacturer { Id = manufacturerId, Name = $"M-{manufacturerId:N}" });
        core.PrinterModels.Add(new PrinterModel
        {
            Id = modelId,
            ManufacturerId = manufacturerId,
            Name = $"Model-{modelId:N}",
        });
        core.Printers.Add(new Printer
        {
            Id = printerId,
            Name = $"Printer-{printerId:N}",
            ServerUrl = $"http://{printerId:N}.test",
            BackendPort = 7125,
            ManufacturerId = manufacturerId,
            ModelId = modelId,
            ConfigurationRevision = 7,
        });
        core.CalibrationProjects.Add(new CalibrationProject
        {
            Id = _projectId,
            OwnerUserId = _ownerId,
            Name = "Desktop scope generation project",
            PrinterId = printerId,
            FilamentProvider = "catalog",
            FilamentProductId = $"product-{_projectId:N}",
            FilamentProductName = "PLA",
            FilamentMaterial = "PLA",
            FilamentSnapshotJson = "{}",
            OrderedStepsJson = "[]",
            CurrentSelectionsJson = "{}",
            CreateRequestId = $"seed-{_projectId:N}",
            CreatedAtUtc = nowUtc,
            UpdatedAtUtc = nowUtc,
            CreatedBySubject = "seed",
            UpdatedBySubject = "seed",
        });
        core.PrinterConfigurationSnapshots.Add(new PrinterConfigurationSnapshot
        {
            Id = snapshotId,
            ProjectId = _projectId,
            AttemptId = _attemptId,
            PrinterId = printerId,
            SchemaVersion = CalibrationContractConstants.SchemaVersion,
            SanitizedSnapshotJson = "{}",
            SnapshotSha256 = new string('a', 64),
            PrinterConfigurationRevision = 7,
            FirmwareFamily = PrinterFirmwareFamily.Klipper,
            GcodeDialect = PrinterGcodeDialect.Klipper,
            FirmwareDetectionSource = FirmwareDetectionSource.Printer,
            SlicerEngine = CalibrationContractConstants.SlicerEngine,
            SlicerDistribution = CalibrationContractConstants.SlicerDistribution,
            SlicerVersion = CalibrationContractConstants.SlicerVersion,
            CapturedAtUtc = nowUtc,
            CapturedBySubject = "seed",
        });
        core.CalibrationAttempts.Add(new CalibrationAttempt
        {
            Id = _attemptId,
            ProjectId = _projectId,
            Sequence = 1,
            CalibrationKind = "final-verification",
            Method = CalibrationMethodNames.FinalVerification,
            DefinitionVersion = CalibrationMethodOptions.CurrentDefinitionVersion,
            InputJson = "{}",
            SpecificationJson = "{}",
            SpecificationSha256 = new string('b', 64),
            PrinterConfigurationSnapshotId = snapshotId,
            ProfileSnapshotIdsJson = "[]",
            AttemptRequestId = $"attempt-{_attemptId:N}",
            CreatedAtUtc = nowUtc,
            CreatedBySubject = "seed",
        });
        await core.SaveChangesAsync();
    }

    public Task DisposeAsync()
    {
        _anonymousClient?.Dispose();
        _factory?.Dispose();
        return Task.CompletedTask;
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
        IReadOnlyList<string> permissions = DesktopScopePermissionMap.GetPermissions(scopes);
        if (permissions.Count == 0)
        {
            return;
        }

        using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        Role role = new()
        {
            Id = Guid.NewGuid(),
            Name = $"calibration-desktop-scope-role-{Guid.NewGuid():N}",
            DisplayName = "Calibration desktop scope test role",
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
                Name = "calibration-desktop-scope-test-key",
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
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", body!.Token);
        return client;
    }

    private static CalibrationGenerateJobRequest RequestReferencingModel(Guid model3DId) => new()
    {
        Method = CalibrationMethodNames.FinalVerification,
        DefinitionVersion = CalibrationMethodOptions.CurrentDefinitionVersion,
        Options = new CalibrationMethodOptionsRequest
        {
            Model3DId = model3DId,
            ExpectedSha256 = new string('c', 64),
        },
    };

    private async Task<HttpResponseMessage> PostGenerateJobAsync(
        HttpClient client, string idempotencyKey, CalibrationGenerateJobRequest request)
    {
        using HttpRequestMessage message = new(
            HttpMethod.Post,
            $"/api/calibration-projects/{_projectId}/attempts/{_attemptId}/generate-job")
        {
            Content = JsonContent.Create(request),
        };
        message.Headers.Add("Idempotency-Key", idempotencyKey);
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return await client.SendAsync(message);
    }

    private static async Task<string?> ReadCodeAsync(HttpResponseMessage response)
    {
        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument document = JsonDocument.Parse(body);
        return document.RootElement.TryGetProperty("code", out JsonElement code) ? code.GetString() : null;
    }

    [Fact(DisplayName =
        "A Desktop token holding only calibration:generate + slicing:submit cannot reference a library model via calibration generation")]
    public async Task CalibrationGenerateOnlyToken_CannotReferenceLibraryModel()
    {
        using HttpClient client = await ExchangeClientAsync(
            ApiKeyScope.CalibrationGenerate | ApiKeyScope.CalibrationRead | ApiKeyScope.SlicingSubmit);

        HttpResponseMessage response = await PostGenerateJobAsync(
            client, "generate-desktop-scope-forbidden", RequestReferencingModel(Guid.NewGuid()));
        string body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(
            HttpStatusCode.Forbidden,
            $"calibration:generate + slicing:submit alone never grants model-read authority (issue #1770 follow-up): {body}");
        (await ReadCodeAsync(response)).Should().Be("resource_forbidden");
    }

    [Fact(DisplayName =
        "A Desktop token holding calibration:generate + slicing:submit + ModelRead can reference a library model via calibration generation")]
    public async Task CalibrationGenerateTokenWithModelRead_CanReferenceLibraryModel()
    {
        using HttpClient client = await ExchangeClientAsync(
            ApiKeyScope.CalibrationGenerate | ApiKeyScope.CalibrationRead | ApiKeyScope.SlicingSubmit
            | ApiKeyScope.ModelRead);

        HttpResponseMessage response = await PostGenerateJobAsync(
            client, "generate-desktop-scope-allowed", RequestReferencingModel(Guid.NewGuid()));
        string body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().NotBe(
            HttpStatusCode.Forbidden,
            $"ModelRead scope must clear the guard so the request reaches the generation saga: {body}");
    }

    [Fact(DisplayName =
        "A Desktop token holding calibration:generate + slicing:submit + LibrarySync can reference a library model via calibration generation")]
    public async Task CalibrationGenerateTokenWithLibrarySync_CanReferenceLibraryModel()
    {
        using HttpClient client = await ExchangeClientAsync(
            ApiKeyScope.CalibrationGenerate | ApiKeyScope.CalibrationRead | ApiKeyScope.SlicingSubmit
            | ApiKeyScope.LibrarySync);

        HttpResponseMessage response = await PostGenerateJobAsync(
            client, "generate-desktop-scope-allowed-librarysync", RequestReferencingModel(Guid.NewGuid()));
        string body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().NotBe(
            HttpStatusCode.Forbidden,
            $"LibrarySync scope must clear the guard so the request reaches the generation saga: {body}");
    }

    [Fact(DisplayName =
        "A Desktop token holding only calibration:generate + slicing:submit is unaffected when the request carries no Model3DId")]
    public async Task CalibrationGenerateOnlyToken_WithoutModel3DId_IsNotForbiddenByModelGuard()
    {
        using HttpClient client = await ExchangeClientAsync(
            ApiKeyScope.CalibrationGenerate | ApiKeyScope.CalibrationRead | ApiKeyScope.SlicingSubmit);

        CalibrationGenerateJobRequest request = new()
        {
            Method = CalibrationMethodNames.Temperature,
            DefinitionVersion = CalibrationMethodOptions.CurrentDefinitionVersion,
            Options = new CalibrationMethodOptionsRequest(),
        };
        HttpResponseMessage response = await PostGenerateJobAsync(
            client, "generate-desktop-scope-no-model", request);
        string body = await response.Content.ReadAsStringAsync();

        // The guard only fires when a Model3DId is present; a temperature-tower request (no model
        // reference) must not be rejected by it, regardless of what happens further down the pipeline.
        (await ReadCodeAsync(response)).Should().NotBe(
            "resource_forbidden",
            $"the Desktop-scope model guard must not fire for requests that never reference a model: {body}");
    }
}
