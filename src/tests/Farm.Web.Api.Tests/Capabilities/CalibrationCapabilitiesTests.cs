using System.Net;
using System.Text.Json;
using Farm.Infrastructure.PrinterCalibration;
using Farm.Infrastructure.Security;
using Farm.Slicer.Module.Data;
using Farm.Slicer.Module.Domain;
using Farm.Web.Api.Services.Capabilities;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Farm.Web.Api.Tests.Capabilities;

public sealed class CalibrationCapabilitiesTests : IAsyncLifetime
{
    private readonly CustomWebApplicationFactory _anonymousFactory = new();
    private readonly CustomWebApplicationFactory _authenticatedFactory = new(
        new Dictionary<string, string?>
        {
            ["Testing:UseTestAuthentication"] = "true",
        });

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await _anonymousFactory.DisposeAsync();
        await _authenticatedFactory.DisposeAsync();
    }

    [Fact]
    public async Task GetSystemCapabilitiesAsync_WithoutNegotiation_ReturnsAdditiveSafeContract()
    {
        using HttpClient client = _anonymousFactory.CreateClient();

        HttpResponseMessage response = await client.GetAsync("/api/system/capabilities");
        string body = await response.Content.ReadAsStringAsync();

        _ = response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        _ = response.Headers.GetValues("X-PrintFarmer-Api-Contract-Version")
            .Should().ContainSingle("1.0");
        _ = response.Headers.GetValues("X-PrintFarmer-Minimum-Api-Contract-Version")
            .Should().ContainSingle("1.0");
        _ = response.Headers.GetValues("X-PrintFarmer-Minimum-Supported-Api-Contract-Version")
            .Should().ContainSingle("1.0");
        _ = response.Headers.CacheControl.Should().NotBeNull();
        _ = response.Headers.CacheControl!.Public.Should().BeTrue();

        using JsonDocument document = JsonDocument.Parse(body);
        JsonElement root = document.RootElement;
        _ = root.GetProperty("apiContractVersion").GetString().Should().Be("1.0");
        _ = root.GetProperty("minimumSupportedApiContractVersion").GetString().Should().Be("1.0");
        _ = root.TryGetProperty("minimumApiContractVersion", out _).Should().BeFalse();
        _ = root.GetProperty("calibrationApiVersion").GetString().Should().Be("1.0");
        _ = root.GetProperty("calibrationSchemaVersion").GetString().Should().Be("1.0");
        _ = root.GetProperty("calibration").GetProperty("contextImplemented")
            .GetBoolean().Should().BeTrue();
        _ = root.GetProperty("calibration").GetProperty("operational")
            .GetBoolean().Should().BeTrue();
        _ = root.GetProperty("calibrationContextEnabled").GetBoolean().Should().BeTrue();
        _ = root.GetProperty("supportedSlicerEngines")[0].GetProperty("type")
            .GetString().Should().Be("OrcaSlicer");
        _ = root.GetProperty("supportedSlicerEngines")[0].GetProperty("version")
            .GetString().Should().Be("2.3.1");
        _ = root.GetProperty("supportedSlicerEngines")[0].GetProperty("distribution")
            .GetString().Should().Be("upstream");

        _ = root.TryGetProperty("supportedPrinterBackends", out _).Should().BeFalse(
            "a network backend must never imply calibration firmware or dialect eligibility");
        _ = root.GetProperty("supportedFirmwareFamilies").EnumerateArray()
            .Select(value => value.GetString()).Should().Equal("Klipper");
        _ = root.GetProperty("supportedGcodeDialects").EnumerateArray()
            .Select(value => value.GetString()).Should().Equal("Klipper");
        foreach (string flag in new[]
                 {
                     "calibrationPersistenceEnabled",
                     "calibrationSyncEnabled",
                     "calibrationPhotosEnabled",
                     "calibrationProfileHistoryEnabled",
                     "calibrationGenerationEnabled",
                     "calibrationSlicingEnabled",
                     "calibrationArtifactPromotionEnabled",
                     "calibrationQueueEnabled",
                     "calibrationJobBoundBedClearEnabled",
                     "calibrationEventsEnabled",
                 })
        {
            _ = root.GetProperty(flag).GetBoolean().Should().BeFalse();
        }

        _ = root.GetProperty("routes").GetProperty("sliceJobs").GetString()
            .Should().Be("/api/slice-jobs");
        string normalizedBody = body.ToLowerInvariant();
        _ = normalizedBody.Should().NotContain("apikey");
        _ = normalizedBody.Should().NotContain("endpointurl");
        _ = normalizedBody.Should().NotContain("password");
        _ = root.GetProperty("healthyCompatibleWorker").GetProperty("distribution")
            .GetString().Should().Be("upstream");
    }

    [Theory]
    [InlineData("0.9")]
    [InlineData("not-a-version")]
    public async Task GetSystemCapabilitiesAsync_WithUnsupportedNegotiatedVersion_ReturnsUpgradeProblem(
        string requestedVersion)
    {
        using HttpClient client = _anonymousFactory.CreateClient();
        client.DefaultRequestHeaders.Add("X-PrintFarmer-Api-Contract-Version", requestedVersion);

        HttpResponseMessage response = await client.GetAsync("/api/system/capabilities");
        string body = await response.Content.ReadAsStringAsync();

        _ = response.StatusCode.Should().Be(HttpStatusCode.UpgradeRequired, body);
        using JsonDocument document = JsonDocument.Parse(body);
        _ = document.RootElement.GetProperty("code").GetString()
            .Should().Be("client_upgrade_required");
        _ = document.RootElement.GetProperty("minimumSupportedApiContractVersion").GetString()
            .Should().Be("1.0");
    }

    [Fact]
    public async Task GetCalibrationCapabilitiesAsync_WithoutAuthentication_ReturnsStableUnauthorizedProblem()
    {
        using HttpClient client = _anonymousFactory.CreateClient();

        HttpResponseMessage response = await client.GetAsync("/api/calibration/capabilities");
        string body = await response.Content.ReadAsStringAsync();

        _ = response.StatusCode.Should().Be(HttpStatusCode.Unauthorized, body);
        using JsonDocument document = JsonDocument.Parse(body);
        _ = document.RootElement.GetProperty("code").GetString()
            .Should().Be("authentication_required");
    }

    [Fact]
    public async Task GetCalibrationCapabilitiesAsync_WithOnlyOctoPrintApiKey_ReturnsStableUnauthorizedProblem()
    {
        using HttpClient client = _anonymousFactory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", "unscoped-octoprint-key");

        HttpResponseMessage response = await client.GetAsync("/api/calibration/capabilities");
        string body = await response.Content.ReadAsStringAsync();

        _ = response.StatusCode.Should().Be(HttpStatusCode.Unauthorized, body);
        using JsonDocument document = JsonDocument.Parse(body);
        _ = document.RootElement.GetProperty("code").GetString()
            .Should().Be("authentication_required");
    }

    [Fact]
    public async Task GetCalibrationCapabilitiesAsync_WithScopedPermissions_ReturnsOnlyEffectivePermissions()
    {
        await AddHealthyOrcaServiceAsync(_authenticatedFactory);
        using HttpClient client = _authenticatedFactory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Roles", "operator");
        client.DefaultRequestHeaders.Add(
            "X-Test-Permissions",
            $"{PrintFarmerPermissions.Calibration.Read},{PrintFarmerPermissions.Slicing.Submit}");

        HttpResponseMessage response = await client.GetAsync("/api/calibration/capabilities");
        string body = await response.Content.ReadAsStringAsync();

        _ = response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        _ = response.Headers.CacheControl.Should().NotBeNull();
        _ = response.Headers.CacheControl!.Private.Should().BeTrue();
        _ = response.Headers.Vary.Should().Contain("Authorization");

        using JsonDocument document = JsonDocument.Parse(body);
        JsonElement root = document.RootElement;
        _ = root.GetProperty("effectivePermissions").EnumerateArray()
            .Select(permission => permission.GetString())
            .Should()
            .BeEquivalentTo(
                PrintFarmerPermissions.Calibration.Read,
                PrintFarmerPermissions.Slicing.Submit);
        _ = root.GetProperty("slicingConfigured").GetBoolean().Should().BeTrue();
        _ = root.GetProperty("slicingOperational").GetBoolean().Should().BeTrue();
        _ = root.GetProperty("effectiveCapabilities").GetProperty("canSubmitSlicing")
            .GetBoolean().Should().BeTrue();
        _ = root.GetProperty("effectiveCapabilities").GetProperty("canReadArtifacts")
            .GetBoolean().Should().BeFalse();
        _ = root.GetProperty("effectiveCapabilities").GetProperty("canRead")
            .GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task GetCalibrationCapabilitiesAsync_ForFarmAdministrator_ReturnsFoundationPermissions()
    {
        using HttpClient client = _authenticatedFactory.CreateClient();

        HttpResponseMessage response = await client.GetAsync("/api/calibration/capabilities");
        string body = await response.Content.ReadAsStringAsync();

        _ = response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        using JsonDocument document = JsonDocument.Parse(body);
        _ = document.RootElement.GetProperty("effectivePermissions").EnumerateArray()
            .Select(permission => permission.GetString())
            .Should()
            .BeEquivalentTo(PrintFarmerPermissions.CalibrationFoundation);
    }

    [Fact]
    public async Task GetSystemCapabilitiesAsync_WithIncompatibleWorker_RemainsConfiguredButNotOperational()
    {
        await AddOrcaServiceAsync(_anonymousFactory, version: "2.2.0");
        using HttpClient client = _anonymousFactory.CreateClient();

        JsonDocument document = await client.GetFromJsonAsync<JsonDocument>(
            "/api/system/capabilities") ?? throw new InvalidOperationException("Missing capability response.");

        using (document)
        {
            _ = document.RootElement.GetProperty("slicingConfigured").GetBoolean().Should().BeTrue();
            _ = document.RootElement.GetProperty("slicingOperational").GetBoolean().Should().BeFalse();
            _ = document.RootElement.GetProperty("healthyCompatibleWorker")
                .GetProperty("available").GetBoolean().Should().BeFalse();
            _ = document.RootElement.GetProperty("unavailableReasons").EnumerateArray()
                .Select(reason => reason.GetProperty("code").GetString())
                .Should().Contain("compatible_worker_unavailable");
        }
    }

    [Fact]
    public async Task GetSystemCapabilitiesAsync_WithStaleCompatibleWorker_RemainsNotOperational()
    {
        await AddOrcaWorkerAsync(
            _anonymousFactory,
            version: "2.3.1",
            lastHeartbeat: DateTime.UtcNow.AddMinutes(-3));
        using HttpClient client = _anonymousFactory.CreateClient();

        JsonDocument document = await client.GetFromJsonAsync<JsonDocument>(
            "/api/system/capabilities") ?? throw new InvalidOperationException("Missing capability response.");

        using (document)
        {
            _ = document.RootElement.GetProperty("slicingConfigured").GetBoolean().Should().BeTrue();
            _ = document.RootElement.GetProperty("slicingOperational").GetBoolean().Should().BeFalse();
            _ = document.RootElement.GetProperty("healthyCompatibleWorker")
                .GetProperty("available").GetBoolean().Should().BeFalse();
        }
    }

    [Fact]
    public async Task GetSystemCapabilitiesAsync_WithoutExplicitUpstreamAttestation_RemainsNotOperational()
    {
        await AddOrcaWorkerAsync(
            _anonymousFactory,
            version: "2.3.1",
            lastHeartbeat: DateTime.UtcNow,
            capabilitiesJson: """{"capabilities":["orcaslicer"]}""");
        using HttpClient client = _anonymousFactory.CreateClient();

        JsonDocument document = await client.GetFromJsonAsync<JsonDocument>(
            "/api/system/capabilities") ?? throw new InvalidOperationException("Missing capability response.");

        using (document)
        {
            _ = document.RootElement.GetProperty("slicingOperational").GetBoolean().Should().BeFalse();
            _ = document.RootElement.GetProperty("healthyCompatibleWorker")
                .GetProperty("available").GetBoolean().Should().BeFalse();
        }
    }

    [Fact]
    public async Task GetSystemCapabilitiesAsync_WhenWorkerDoesNotAttestUpstreamDistribution_RemainsNotOperational()
    {
        await AddOrcaWorkerAsync(
            _anonymousFactory,
            version: "2.3.1",
            lastHeartbeat: DateTime.UtcNow,
            workerCapabilitiesJson: """{"capabilities":["orcaslicer"]}""");
        using HttpClient client = _anonymousFactory.CreateClient();

        JsonDocument document = await client.GetFromJsonAsync<JsonDocument>(
            "/api/system/capabilities") ?? throw new InvalidOperationException("Missing capability response.");

        using (document)
        {
            _ = document.RootElement.GetProperty("slicingOperational").GetBoolean().Should().BeFalse();
            _ = document.RootElement.GetProperty("healthyCompatibleWorker")
                .GetProperty("available").GetBoolean().Should().BeFalse();
        }
    }

    [Fact]
    public async Task GetCapabilitiesAsync_WithUnreachableProfileStore_ReportsContextUnavailable()
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection()
            .Build();
        ServiceCollection services = new();
        _ = services.AddSingleton<ICalibrationProfileResolver>(
            new UnavailableCalibrationProfileResolver());
        await using ServiceProvider provider = services.BuildServiceProvider();
        CalibrationCapabilityService service = new(
            configuration,
            provider,
            NullLogger<CalibrationCapabilityService>.Instance);

        var capabilities =
            await service.GetCapabilitiesAsync(null, CancellationToken.None);

        _ = capabilities.CalibrationContextEnabled.Should().BeFalse();
        _ = capabilities.Calibration.Operational.Should().BeFalse();
        _ = capabilities.UnavailableReasons.Select(reason => reason.Code)
            .Should().Contain("profile_service_unavailable");
    }

    private static Task AddHealthyOrcaServiceAsync(CustomWebApplicationFactory factory) =>
        AddOrcaWorkerAsync(factory, "2.3.1", DateTime.UtcNow);

    private static async Task AddOrcaServiceAsync(
        CustomWebApplicationFactory factory,
        string version) =>
        await AddOrcaWorkerAsync(factory, version, DateTime.UtcNow);

    private static async Task AddOrcaWorkerAsync(
        CustomWebApplicationFactory factory,
        string version,
        DateTime lastHeartbeat,
        string capabilitiesJson =
            """{"capabilities":["orcaslicer","orcaslicer-upstream"]}""",
        string? workerCapabilitiesJson = null)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        SlicerDbContext db = scope.ServiceProvider.GetRequiredService<SlicerDbContext>();
        Guid serviceId = Guid.NewGuid();
        _ = db.SlicerServices.Add(new SlicerService
        {
            Id = serviceId,
            Name = "test-orca-service",
            SlicerType = (int)SlicerType.OrcaSlicer,
            Version = version,
            Host = "http://private-worker.internal",
            CapabilitiesJson = capabilitiesJson,
            MaxConcurrentJobs = 2,
            Status = WorkerStatus.Online,
            LastSeen = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        _ = db.Workers.Add(new Worker
        {
            Id = Guid.NewGuid(),
            ServiceId = serviceId.ToString(),
            Name = "test-orca-worker",
            EndpointUrl = "http://private-worker.internal",
            CapabilitiesJson = workerCapabilitiesJson ?? capabilitiesJson,
            Version = version,
            Status = WorkerStatus.Online,
            TotalSlots = 2,
            ActiveJobs = 0,
            LastHeartbeat = lastHeartbeat,
            RegisteredAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        _ = await db.SaveChangesAsync();
    }

    private sealed class UnavailableCalibrationProfileResolver
        : ICalibrationProfileResolver
    {
        public Task<bool> IsAvailableAsync(CancellationToken cancellationToken) =>
            Task.FromResult(false);

        public Task<ResolvedCalibrationProfiles> ResolveAsync(
            Guid machineProfileId,
            Guid processProfileId,
            Guid filamentProfileId,
            CalibrationProfileAccessScope accessScope,
            CancellationToken cancellationToken) =>
            Task.FromException<ResolvedCalibrationProfiles>(
                new CalibrationProfileResolverUnavailableException());
    }
}
