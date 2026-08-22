using System.Net;
using System.Text.Json;
using Farm.Infrastructure.Dtos;
using Farm.Infrastructure.PrinterCalibration;
using Farm.Infrastructure.Security;
using Farm.Slicer.Module.Data;
using Farm.Slicer.Module.Domain;
using Farm.Web.Api.Services.Calibration.Generation;
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
        _ = root.GetProperty("calibration").GetProperty("commandsImplemented")
            .GetBoolean().Should().BeTrue();
        _ = root.GetProperty("calibration").GetProperty("generationImplemented")
            .GetBoolean().Should().BeTrue();
        _ = root.GetProperty("calibration").GetProperty("queueIntegrationImplemented")
            .GetBoolean().Should().BeTrue();
        _ = root.GetProperty("calibration").GetProperty("eventStreamImplemented")
            .GetBoolean().Should().BeTrue();
        _ = root.GetProperty("calibration").GetProperty("operational")
            .GetBoolean().Should().BeTrue();
        _ = root.GetProperty("calibrationContextEnabled").GetBoolean().Should().BeTrue();
        _ = root.GetProperty("supportedSlicerEngines")[0].GetProperty("type")
            .GetString().Should().Be("OrcaSlicer");
        _ = root.GetProperty("supportedSlicerEngines")[0].GetProperty("version")
            .GetString().Should().Be(CalibrationContractConstants.SlicerVersion);
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
                 })
        {
            _ = root.GetProperty(flag).GetBoolean().Should().BeTrue();
        }

        foreach (string flag in new[]
                 {
                     "calibrationGenerationEnabled",
                     "calibrationSlicingEnabled",
                     "calibrationQueueEnabled",
                     "calibrationJobBoundBedClearEnabled",
                     "calibrationEventsEnabled",
                 })
        {
            _ = root.GetProperty(flag).GetBoolean().Should().BeFalse();
        }

        // Promotion runs entirely inside this monolith host: artifacts are routable in process, the
        // library storage is writable, the durable checkpoint store answers and the reconciler is wired.
        _ = root.GetProperty("calibrationArtifactPromotionEnabled").GetBoolean().Should().BeTrue();
        _ = root.GetProperty("unavailableReasons").EnumerateArray()
            .Select(reason => reason.GetProperty("feature").GetString())
            .Should().NotContain("calibrationArtifactPromotion");

        _ = root.GetProperty("routes").GetProperty("sliceJobs").GetString()
            .Should().Be("/api/slice");
        _ = root.GetProperty("routes").GetProperty("sliceJob").GetString()
            .Should().Be("/api/slice/{id}");
        _ = root.GetProperty("routes").GetProperty("calibrationProjects").GetString()
            .Should().Be("/api/calibration-projects");
        _ = root.GetProperty("routes").GetProperty("calibrationSync").GetString()
            .Should().Be("/api/calibration-sync/changes");
        _ = root.GetProperty("limits").GetProperty("photoUploadMaxBytes").GetInt64()
            .Should().BeGreaterThan(0);
        _ = root.GetProperty("acceptedMimeTypes").GetProperty("photo").EnumerateArray()
            .Select(value => value.GetString())
            .Should().BeEquivalentTo("image/jpeg", "image/png", "image/webp");
        string normalizedBody = body.ToLowerInvariant();
        _ = normalizedBody.Should().NotContain("apikey");
        _ = normalizedBody.Should().NotContain("endpointurl");
        _ = normalizedBody.Should().NotContain("password");
        _ = root.GetProperty("healthyCompatibleWorker").GetProperty("distribution")
            .GetString().Should().Be("upstream");
        _ = root.GetProperty("healthyCompatibleWorker").GetProperty("versionPolicy")
            .GetString().Should().Be("allow-list");
        _ = root.GetProperty("healthyCompatibleWorker").GetProperty("supportedVersions")
            .EnumerateArray()
            .Select(value => value.GetString())
            .Should().Equal(CalibrationContractConstants.SlicerVersion);
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
        _ = root.GetProperty("effectiveCapabilities").GetProperty("canCreate")
            .GetBoolean().Should().BeFalse();
        _ = root.GetProperty("effectiveCapabilities").GetProperty("canUpdate")
            .GetBoolean().Should().BeFalse();
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
            JsonElement worker = document.RootElement.GetProperty("healthyCompatibleWorker");
            _ = worker.GetProperty("observedVersions").EnumerateArray()
                .Select(value => value.GetString()).Should().Equal("2.2.0");
            _ = worker.GetProperty("supportedVersions").EnumerateArray()
                .Select(value => value.GetString())
                .Should().Equal(CalibrationContractConstants.SlicerVersion);

            JsonElement[] versionReasons = document.RootElement
                .GetProperty("unavailableReasons")
                .EnumerateArray()
                .Where(reason =>
                    reason.GetProperty("code").GetString() ==
                    CalibrationGenerationProblemCodes.SlicerVersionUnsupported)
                .ToArray();
            _ = versionReasons.Should().Contain(reason =>
                reason.GetProperty("feature").GetString() == "slicing");
            _ = versionReasons.Should().Contain(reason =>
                reason.GetProperty("feature").GetString() == "calibrationGeneration");
            _ = versionReasons.Should().OnlyContain(reason =>
                reason.GetProperty("message").GetString() ==
                $"Observed upstream OrcaSlicer version(s) 2.2.0; configured supported version(s): {CalibrationContractConstants.SlicerVersion}.");
        }
    }

    [Fact]
    public async Task GetSystemCapabilitiesAsync_WithStaleCompatibleWorker_RemainsNotOperational()
    {
        await AddOrcaWorkerAsync(
            _anonymousFactory,
            version: CalibrationContractConstants.SlicerVersion,
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
            version: CalibrationContractConstants.SlicerVersion,
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
            version: CalibrationContractConstants.SlicerVersion,
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

        // Issue #1849: the subsystems these flags describe (calibration commands/generation,
        // queue integration, event streaming) are always compiled in and registered, so they
        // must never be misreported as unimplemented just because the profile store happens to
        // be unreachable in this deployment. Only "operational" should reflect that.
        _ = capabilities.Calibration.ContextImplemented.Should().BeTrue();
        _ = capabilities.Calibration.CommandsImplemented.Should().BeTrue();
        _ = capabilities.Calibration.GenerationImplemented.Should().BeTrue();
        _ = capabilities.Calibration.QueueIntegrationImplemented.Should().BeTrue();
        _ = capabilities.Calibration.EventStreamImplemented.Should().BeTrue();
    }

    [Fact]
    public async Task GetCapabilitiesAsync_WithoutRegisteredPromoter_KeepsArtifactPromotionFalse()
    {
        // A split host does not load the slicer module in process, so nothing registers a promoter and
        // artifacts are not routable from here.
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DEPLOYMENT_MODE"] = "split",
            })
            .Build();
        ServiceCollection services = new();
        await using ServiceProvider provider = services.BuildServiceProvider();
        CalibrationCapabilityService service = new(
            configuration,
            provider,
            NullLogger<CalibrationCapabilityService>.Instance);

        PlatformCapabilitiesDto capabilities =
            await service.GetCapabilitiesAsync(null, CancellationToken.None);

        _ = capabilities.DeploymentMode.Should().Be("split");
        _ = capabilities.CalibrationArtifactPromotionEnabled.Should().BeFalse();
        _ = capabilities.UnavailableReasons.Select(reason => reason.Code)
            .Should().Contain("promotion_dependency_unavailable");
    }

    [Fact]
    public async Task GetSystemCapabilitiesAsync_WithHealthyWorkerLackingPinnedIdentity_KeepsCalibrationSlicingFalse()
    {
        await AddHealthyOrcaServiceAsync(_anonymousFactory);
        using HttpClient client = _anonymousFactory.CreateClient();

        JsonDocument document = await client.GetFromJsonAsync<JsonDocument>(
            "/api/system/capabilities") ?? throw new InvalidOperationException("Missing capability response.");

        using (document)
        {
            _ = document.RootElement.GetProperty("slicingOperational").GetBoolean().Should().BeTrue();
            _ = document.RootElement.GetProperty("calibrationSlicingEnabled").GetBoolean()
                .Should().BeFalse("a worker that does not attest a pinned binary and container digest is unverifiable");
        }
    }

    [Fact]
    public async Task GetSystemCapabilitiesAsync_WithoutCredentialedWorker_ReportsAuthenticationNotConfigured()
    {
        await AddOrcaWorkerAsync(
            _anonymousFactory,
            version: CalibrationContractConstants.SlicerVersion,
            lastHeartbeat: DateTime.UtcNow,
            apiKey: null);
        using HttpClient client = _anonymousFactory.CreateClient();

        JsonDocument document = await client.GetFromJsonAsync<JsonDocument>(
            "/api/system/capabilities") ?? throw new InvalidOperationException("Missing capability response.");

        using (document)
        {
            _ = document.RootElement.GetProperty("slicingConfigured").GetBoolean().Should().BeFalse();
            _ = document.RootElement.GetProperty("slicingOperational").GetBoolean().Should().BeFalse();
            _ = document.RootElement.GetProperty("calibrationSlicingEnabled").GetBoolean().Should().BeFalse();
            _ = document.RootElement.GetProperty("unavailableReasons").EnumerateArray()
                .Select(reason => reason.GetProperty("code").GetString())
                .Should().Contain("worker_authentication_not_configured");
        }
    }

    [Fact]
    public async Task GetSystemCapabilitiesAsync_AdvertisedSliceRoutes_ResolveInTheApiRouteTable()
    {
        using HttpClient client = _anonymousFactory.CreateClient();

        JsonDocument document = await client.GetFromJsonAsync<JsonDocument>(
            "/api/system/capabilities") ?? throw new InvalidOperationException("Missing capability response.");

        using (document)
        {
            string sliceJobsRoute = document.RootElement.GetProperty("routes")
                .GetProperty("sliceJobs").GetString()!;

            // An advertised route must actually exist: an unknown path returns 404, whereas the
            // real protected route rejects the anonymous caller with 401.
            HttpResponseMessage response = await client.GetAsync(sliceJobsRoute);
            _ = response.StatusCode.Should().NotBe(HttpStatusCode.NotFound);
        }
    }

    [Fact]
    public async Task GetSystemCapabilitiesAsync_AdvertisedPromotionRoute_RejectsAnonymousCallerInsteadOf404()
    {
        using HttpClient client = _anonymousFactory.CreateClient();

        JsonDocument document = await client.GetFromJsonAsync<JsonDocument>(
            "/api/system/capabilities") ?? throw new InvalidOperationException("Missing capability response.");

        using (document)
        {
            string promotionRoute = document.RootElement.GetProperty("routes")
                .GetProperty("gcodePromotions").GetString()!;

            // An advertised route must exist and be protected: an unknown path returns 404, whereas
            // the real promotion route rejects the anonymous caller.
            HttpResponseMessage response = await client.PostAsync(promotionRoute, content: null);
            _ = response.StatusCode.Should().NotBe(HttpStatusCode.NotFound);
            _ = response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }
    }

    private static Task AddHealthyOrcaServiceAsync(CustomWebApplicationFactory factory) =>
        AddOrcaWorkerAsync(factory, CalibrationContractConstants.SlicerVersion, DateTime.UtcNow);

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
        string? workerCapabilitiesJson = null,
        string? apiKey = "registry-issued-worker-key")
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
            ApiKey = apiKey,
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

    [Fact]
    public async Task GetCapabilitiesAsync_WithReachableProfileStore_ReportsContextOperational()
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DEPLOYMENT_MODE"] = "split",
            })
            .Build();
        ServiceCollection services = new();
        _ = services.AddSingleton<ICalibrationProfileResolver>(
            new AvailableCalibrationProfileResolver());
        await using ServiceProvider provider = services.BuildServiceProvider();
        CalibrationCapabilityService service = new(
            configuration,
            provider,
            NullLogger<CalibrationCapabilityService>.Instance);

        PlatformCapabilitiesDto capabilities =
            await service.GetCapabilitiesAsync(null, CancellationToken.None);

        _ = capabilities.DeploymentMode.Should().Be("split");
        _ = capabilities.CalibrationContextEnabled.Should().BeTrue();
        _ = capabilities.Calibration.Operational.Should().BeTrue();
        _ = capabilities.UnavailableReasons.Select(reason => reason.Code)
            .Should().NotContain("profile_service_unavailable");

        // The capability document must never disclose where the profile store lives.
        _ = JsonSerializer.Serialize(capabilities)
            .Should().NotContain("slicer-host", "the internal resolver address is not public");
    }

    [Fact]
    public async Task GetCapabilitiesAsync_WithUnobservableWorkerRegistry_ReportsRegistryUnavailable()
    {
        // Slicing is enabled but the worker registry cannot be read, so the credentialed-worker
        // count is 0 for want of evidence rather than for want of credentials. The diagnostic must
        // name the registry outage instead of sending operators to rotate worker keys.
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Slicer:Enabled"] = "true",
            })
            .Build();
        ServiceCollection services = new();
        await using ServiceProvider provider = services.BuildServiceProvider();
        CalibrationCapabilityService service = new(
            configuration,
            provider,
            NullLogger<CalibrationCapabilityService>.Instance);

        PlatformCapabilitiesDto capabilities =
            await service.GetCapabilitiesAsync(null, CancellationToken.None);

        string[] slicingReasons = capabilities.UnavailableReasons
            .Where(reason => reason.Feature == "slicing")
            .Select(reason => reason.Code)
            .ToArray();
        _ = slicingReasons.Should().Contain("slicer_registry_unavailable");
        _ = slicingReasons.Should().NotContain("worker_authentication_not_configured");
        _ = capabilities.SlicingOperational.Should().BeFalse();
    }

    [Fact]
    public async Task GetCapabilitiesAsync_WhenProfileResolverThrows_DegradesInsteadOfFailing()
    {
        // The capability document is public: a split-mode transport failure inside the resolver
        // must degrade the flag, never surface as an error to an anonymous caller.
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection()
            .Build();
        ServiceCollection services = new();
        _ = services.AddSingleton<ICalibrationProfileResolver>(
            new ThrowingCalibrationProfileResolver());
        await using ServiceProvider provider = services.BuildServiceProvider();
        CalibrationCapabilityService service = new(
            configuration,
            provider,
            NullLogger<CalibrationCapabilityService>.Instance);

        PlatformCapabilitiesDto capabilities =
            await service.GetCapabilitiesAsync(null, CancellationToken.None);

        _ = capabilities.CalibrationContextEnabled.Should().BeFalse();
        _ = capabilities.UnavailableReasons.Select(reason => reason.Code)
            .Should().Contain("profile_service_unavailable");
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

    private sealed class AvailableCalibrationProfileResolver
        : ICalibrationProfileResolver
    {
        public Task<bool> IsAvailableAsync(CancellationToken cancellationToken) =>
            Task.FromResult(true);

        public Task<ResolvedCalibrationProfiles> ResolveAsync(
            Guid machineProfileId,
            Guid processProfileId,
            Guid filamentProfileId,
            CalibrationProfileAccessScope accessScope,
            CancellationToken cancellationToken) =>
            Task.FromResult(new ResolvedCalibrationProfiles(null, null, null));
    }

    private sealed class ThrowingCalibrationProfileResolver
        : ICalibrationProfileResolver
    {
        public Task<bool> IsAvailableAsync(CancellationToken cancellationToken) =>
            Task.FromException<bool>(
                new HttpIOException(HttpRequestError.ResponseEnded, "response ended prematurely"));

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
