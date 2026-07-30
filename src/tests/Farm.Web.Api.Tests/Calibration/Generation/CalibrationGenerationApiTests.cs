using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.PrinterCalibration;
using Farm.Infrastructure.Security;
using Farm.Web.Api.Contracts;
using Farm.Web.Api.Services.Calibration.Generation;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Farm.Web.Api.Tests.Calibration.Generation;

/// <summary>
/// Covers the authenticated generation and orchestration-status routes: permission enforcement, farm
/// isolation, idempotency-key requirement, structured rejection and redaction.
/// </summary>
public sealed class CalibrationGenerationApiTests : IAsyncLifetime
{
    private static readonly Guid OwnerUserId = new("00000000-0000-0000-0000-000000000001");
    private static readonly Guid ForeignUserId = new("00000000-0000-0000-0000-0000000000ff");

    private readonly CustomWebApplicationFactory _factory = new(
        new Dictionary<string, string?>
        {
            ["Testing:UseTestAuthentication"] = "true",
        });

    private Guid _projectId;
    private Guid _attemptId;
    private Guid _orchestrationId;

    public async Task InitializeAsync()
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        AppDbContext core = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        _ = await core.Database.EnsureCreatedAsync();

        Guid manufacturerId = Guid.NewGuid();
        Guid modelId = Guid.NewGuid();
        Guid printerId = Guid.NewGuid();
        Guid snapshotId = Guid.NewGuid();
        _projectId = Guid.NewGuid();
        _attemptId = Guid.NewGuid();
        _orchestrationId = Guid.NewGuid();
        DateTime nowUtc = DateTime.UtcNow;

        _ = core.Manufacturers.Add(new Manufacturer { Id = manufacturerId, Name = $"M-{manufacturerId:N}" });
        _ = core.PrinterModels.Add(new PrinterModel
        {
            Id = modelId,
            ManufacturerId = manufacturerId,
            Name = $"Model-{modelId:N}",
        });
        _ = core.Printers.Add(new Printer
        {
            Id = printerId,
            Name = $"Printer-{printerId:N}",
            ServerUrl = $"http://{printerId:N}.test",
            BackendPort = 7125,
            ManufacturerId = manufacturerId,
            ModelId = modelId,
            ConfigurationRevision = 7,
        });
        _ = core.CalibrationProjects.Add(new CalibrationProject
        {
            Id = _projectId,
            OwnerUserId = OwnerUserId,
            Name = "Api generation project",
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
        _ = core.PrinterConfigurationSnapshots.Add(new PrinterConfigurationSnapshot
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
        _ = core.CalibrationAttempts.Add(new CalibrationAttempt
        {
            Id = _attemptId,
            ProjectId = _projectId,
            Sequence = 1,
            CalibrationKind = "temperature",
            Method = CalibrationMethodNames.Temperature,
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
        _ = core.CalibrationOrchestrations.Add(new CalibrationOrchestration
        {
            Id = _orchestrationId,
            ProjectId = _projectId,
            AttemptId = _attemptId,
            CurrentStep = CalibrationGenerationSteps.Created,
            Status = CalibrationOrchestrationStatus.Pending,
            OperationId = $"attempt-{_attemptId:N}",
            Revision = 1,
            CreatedAtUtc = nowUtc,
            UpdatedAtUtc = nowUtc,
        });
        _ = await core.SaveChangesAsync();
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    [Fact(DisplayName = "The generation route rejects an anonymous caller")]
    public async Task GenerateJob_WhenAnonymous_Returns401()
    {
        using HttpClient client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Anonymous", "true");

        HttpResponseMessage response = await PostAsync(client, "generate-anonymous", ValidRequest());

        _ = response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Theory(DisplayName = "The generation route requires both calibration generate and slicing submit")]
    [InlineData(PrintFarmerPermissions.Calibration.Generate)]
    [InlineData(PrintFarmerPermissions.Slicing.Submit)]
    public async Task GenerateJob_WithOnlyOnePermission_Returns403(string permission)
    {
        using HttpClient client = CreateClient(permission);

        HttpResponseMessage response = await PostAsync(client, "generate-partial", ValidRequest());

        _ = response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact(DisplayName = "The generation route requires an Idempotency-Key operation identifier")]
    public async Task GenerateJob_WithoutIdempotencyKey_Returns400()
    {
        using HttpClient client = CreateClient(
            PrintFarmerPermissions.Calibration.Generate,
            PrintFarmerPermissions.Slicing.Submit);

        HttpResponseMessage response = await client.PostAsJsonAsync(
            $"/api/calibration-projects/{_projectId}/attempts/{_attemptId}/generate-job",
            ValidRequest());

        _ = response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        _ = (await ReadCodeAsync(response)).Should().Be("idempotency_key_required");
    }

    [Fact(DisplayName = "An unsupported method is rejected with structured 422 field reasons")]
    public async Task GenerateJob_WithUnsupportedMethod_Returns422WithProblems()
    {
        using HttpClient client = CreateClient(
            PrintFarmerPermissions.Calibration.Generate,
            PrintFarmerPermissions.Slicing.Submit);

        HttpResponseMessage response = await PostAsync(
            client,
            "generate-unsupported",
            new CalibrationGenerateJobRequest
            {
                Method = "definitely-not-a-method",
                DefinitionVersion = CalibrationMethodOptions.CurrentDefinitionVersion,
                Options = new CalibrationMethodOptionsRequest(),
            });
        string body = await response.Content.ReadAsStringAsync();

        _ = response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity, body);
        _ = (await ReadCodeAsync(response)).Should()
            .Be("unsupported_or_unsafe_calibration_specification");
        using JsonDocument document = JsonDocument.Parse(body);
        _ = document.RootElement.GetProperty("problems")[0].GetProperty("field").GetString()
            .Should().Be("method");
    }

    [Fact(DisplayName = "A caller from another farm cannot reach another owner's attempt")]
    public async Task GenerateJob_ForForeignOwner_Returns404()
    {
        using HttpClient client = CreateClient(
            PrintFarmerPermissions.Calibration.Generate,
            PrintFarmerPermissions.Slicing.Submit);
        client.DefaultRequestHeaders.Remove("X-Test-User-Id");
        client.DefaultRequestHeaders.Add("X-Test-User-Id", ForeignUserId.ToString());

        HttpResponseMessage response = await PostAsync(client, "generate-foreign", ValidRequest());

        _ = response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact(DisplayName = "Generation is refused with 503 while the production path is not provable")]
    public async Task GenerateJob_WithoutOperationalPath_Returns503()
    {
        using HttpClient client = CreateClient(
            PrintFarmerPermissions.Calibration.Generate,
            PrintFarmerPermissions.Slicing.Submit);

        HttpResponseMessage response = await PostAsync(client, "generate-unavailable", ValidRequest());

        _ = response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        _ = (await ReadCodeAsync(response)).Should().Be("generation_dependency_unavailable");
    }

    [Fact(DisplayName = "The orchestration status route requires calibration read permission")]
    public async Task GetOrchestration_WithoutReadPermission_Returns403()
    {
        using HttpClient client = CreateClient(PrintFarmerPermissions.Calibration.Generate);

        HttpResponseMessage response = await client.GetAsync(
            $"/api/calibration-orchestrations/{_orchestrationId}");

        _ = response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact(DisplayName = "The orchestration status route returns a redacted durable document")]
    public async Task GetOrchestration_WithReadPermission_ReturnsRedactedStatus()
    {
        using HttpClient client = CreateClient(PrintFarmerPermissions.Calibration.Read);

        HttpResponseMessage response = await client.GetAsync(
            $"/api/calibration-orchestrations/{_orchestrationId}");
        string body = await response.Content.ReadAsStringAsync();

        _ = response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        using JsonDocument document = JsonDocument.Parse(body);
        JsonElement root = document.RootElement;
        _ = root.GetProperty("id").GetGuid().Should().Be(_orchestrationId);
        _ = root.GetProperty("attemptId").GetGuid().Should().Be(_attemptId);
        _ = root.GetProperty("statusRoute").GetString().Should()
            .Be($"/api/calibration-orchestrations/{_orchestrationId}");
        _ = root.TryGetProperty("modelFilePath", out _).Should().BeFalse();
        _ = root.TryGetProperty("workerEndpoint", out _).Should().BeFalse();
        _ = root.TryGetProperty("logText", out _).Should().BeFalse();
        _ = body.Should().NotContain("apiKey");
        _ = body.Should().NotContain("X-Worker-Key");
    }

    [Fact(DisplayName = "A caller from another farm cannot read the orchestration status")]
    public async Task GetOrchestration_ForForeignOwner_Returns404()
    {
        using HttpClient client = CreateClient(PrintFarmerPermissions.Calibration.Read);
        client.DefaultRequestHeaders.Remove("X-Test-User-Id");
        client.DefaultRequestHeaders.Add("X-Test-User-Id", ForeignUserId.ToString());

        HttpResponseMessage response = await client.GetAsync(
            $"/api/calibration-orchestrations/{_orchestrationId}");

        _ = response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    private static CalibrationGenerateJobRequest ValidRequest() => new()
    {
        Method = CalibrationMethodNames.Temperature,
        DefinitionVersion = CalibrationMethodOptions.CurrentDefinitionVersion,
        Options = new CalibrationMethodOptionsRequest(),
    };

    private static async Task<string?> ReadCodeAsync(HttpResponseMessage response)
    {
        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument document = JsonDocument.Parse(body);
        return document.RootElement.TryGetProperty("code", out JsonElement code) ? code.GetString() : null;
    }

    private HttpClient CreateClient(params string[] permissions)
    {
        HttpClient client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Roles", "user");
        client.DefaultRequestHeaders.Add("X-Test-User-Id", OwnerUserId.ToString());
        if (permissions.Length > 0)
        {
            client.DefaultRequestHeaders.Add("X-Test-Permissions", string.Join(',', permissions));
        }

        return client;
    }

    private async Task<HttpResponseMessage> PostAsync(
        HttpClient client,
        string idempotencyKey,
        CalibrationGenerateJobRequest request)
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
}
