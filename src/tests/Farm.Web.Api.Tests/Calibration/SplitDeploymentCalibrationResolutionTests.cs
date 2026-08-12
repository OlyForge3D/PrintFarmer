using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Farm.Infrastructure.PrinterCalibration;
using Farm.Infrastructure.Security;
using Farm.Web.Api.Services.Calibration;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;

namespace Farm.Web.Api.Tests.Calibration;

/// <summary>
/// End-to-end proof of the split-deployment calibration hop.
/// </summary>
/// <remarks>
/// The API host runs with <c>DEPLOYMENT_MODE=split</c>, so nothing registers the in-process profile
/// resolver. The registered resolver is the production HTTP adapter, dialling a real slicer-host
/// test server that runs the production resolution endpoint and the production database resolver.
/// No calibration service is mocked or bypassed: the calibration candidate, context and capability
/// endpoints all answer through that real hop.
/// </remarks>
[Collection("SlicerDisabled")]
public sealed class SplitDeploymentCalibrationResolutionTests : IAsyncLifetime
{
    private readonly SplitCalibrationWebApplicationFactory _factory = new();
    private SlicerHostResolutionTestServer? _slicerHost;

    public async Task InitializeAsync()
    {
        // Materialise the API host first so it creates the shared test schema, then point the
        // registered HTTP adapter at a slicer host bound to that same profile store.
        IConfiguration configuration = _factory.Services.GetRequiredService<IConfiguration>();
        _slicerHost = await SlicerHostResolutionTestServer.StartAsync(
            _factory.TestConnectionString,
            configuration["Jwt:Key"] ?? CalibrationTestJwt.Key,
            configuration["Jwt:Issuer"] ?? CalibrationTestJwt.Issuer,
            configuration["Jwt:Audience"] ?? CalibrationTestJwt.Audience);
        _factory.SlicerHostHandler = _slicerHost.CreateHandler();
    }

    public async Task DisposeAsync()
    {
        if (_slicerHost is not null)
        {
            await _slicerHost.DisposeAsync();
        }

        await _factory.DisposeAsync();
    }

    [Fact]
    public void SplitDeployment_RegistersTheHttpAdapterRatherThanAnInProcessResolver()
    {
        using IServiceScope scope = _factory.Services.CreateScope();

        ICalibrationProfileResolver? resolver =
            scope.ServiceProvider.GetService<ICalibrationProfileResolver>();

        _ = resolver.Should().BeOfType<SlicerHostCalibrationProfileResolver>();
    }

    [Fact]
    public async Task GetCandidates_ListsEligiblePrinterWithoutRequiringTheSlicerHost()
    {
        CalibrationPrinterSeeder.SeededPrinter seeded =
            await CalibrationPrinterSeeder.SeedAsync(_factory.Services);
        using HttpClient client = CreateCalibrationReaderClient();

        HttpResponseMessage response = await client.GetAsync("/api/printers/calibration-candidates");
        string body = await response.Content.ReadAsStringAsync();

        _ = response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        using JsonDocument document = JsonDocument.Parse(body);
        JsonElement candidate = document.RootElement.EnumerateArray()
            .Single(element => element.GetProperty("id").GetGuid() == seeded.PrinterId);
        _ = candidate.GetProperty("eligible").GetBoolean().Should().BeTrue(body);
        _ = candidate.GetProperty("rejectionReasons").GetArrayLength().Should().Be(0, body);
    }

    [Fact]
    public async Task GetContext_ReachesTheSlicerHostOverHttpAndReturnsTheResolvedProfiles()
    {
        CalibrationPrinterSeeder.SeededPrinter seeded =
            await CalibrationPrinterSeeder.SeedAsync(_factory.Services);
        using HttpClient client = CreateCalibrationReaderClient();

        HttpResponseMessage response = await client.GetAsync(
            $"/api/printers/{seeded.PrinterId}/calibration-context?slicerType=OrcaSlicer");
        string body = await response.Content.ReadAsStringAsync();

        _ = response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        using JsonDocument document = JsonDocument.Parse(body);
        JsonElement profiles = document.RootElement
            .GetProperty("snapshot").GetProperty("profiles");
        _ = profiles.GetProperty("machine").GetProperty("id").GetGuid()
            .Should().Be(seeded.MachineProfileId);
        _ = profiles.GetProperty("machine").GetProperty("exactJson").GetString()
            .Should().Contain("\"gcode_flavor\":\"klipper\"");
        _ = profiles.GetProperty("process").GetProperty("id").GetGuid()
            .Should().Be(seeded.ProcessProfileId);
        _ = profiles.GetProperty("filament").GetProperty("id").GetGuid()
            .Should().Be(seeded.FilamentProfileId);
        _ = document.RootElement.GetProperty("eligible").GetBoolean().Should().BeTrue(body);

        // The hop must not smuggle transport or printer credentials into the client contract.
        string normalized = body.ToLowerInvariant();
        _ = normalized.Should().NotContain("printer-api-key");
        _ = normalized.Should().NotContain("printer-password");
        _ = normalized.Should().NotContain("slicer-host.internal");
    }

    [Fact]
    public async Task PrivateProfiles_KeepOwnerScopingAcrossTheHop()
    {
        Guid ownerUserId = Guid.NewGuid();
        CalibrationPrinterSeeder.SeededPrinter seeded = await CalibrationPrinterSeeder.SeedAsync(
            _factory.Services,
            profilesPublic: false,
            profileOwnerUserId: ownerUserId);
        string route = $"/api/printers/{seeded.PrinterId}/calibration-context?slicerType=OrcaSlicer";

        using HttpClient nonOwner = CreateCalibrationReaderClient();
        HttpResponseMessage nonOwnerResponse = await nonOwner.GetAsync(route);
        string nonOwnerBody = await nonOwnerResponse.Content.ReadAsStringAsync();

        _ = nonOwnerResponse.StatusCode.Should().Be(HttpStatusCode.OK, nonOwnerBody);
        using (JsonDocument document = JsonDocument.Parse(nonOwnerBody))
        {
            _ = document.RootElement.GetProperty("eligible").GetBoolean().Should().BeFalse();
            _ = document.RootElement.GetProperty("rejectionReasons").EnumerateArray()
                .Select(reason => reason.GetProperty("code").GetString())
                .Should().Contain("machine_profile_not_found");
        }

        _ = nonOwnerBody.Should().NotContain("Split Machine");

        using HttpClient owner = CreateCalibrationReaderClient(ownerUserId);
        using JsonDocument ownerContext =
            await owner.GetFromJsonAsync<JsonDocument>(route)
            ?? throw new InvalidOperationException("Missing owner calibration context.");
        _ = ownerContext.RootElement.GetProperty("eligible").GetBoolean().Should().BeTrue();

        using HttpClient admin = CreateFarmAdminClient();
        using JsonDocument adminContext =
            await admin.GetFromJsonAsync<JsonDocument>(route)
            ?? throw new InvalidOperationException("Missing admin calibration context.");
        _ = adminContext.RootElement.GetProperty("eligible").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Capabilities_ReportCalibrationContextEnabledOnceTheSlicerHostAnswers()
    {
        using HttpClient client = CreateCalibrationReaderClient();

        using JsonDocument enabled =
            await client.GetFromJsonAsync<JsonDocument>("/api/system/capabilities")
            ?? throw new InvalidOperationException("Missing capability response.");
        _ = enabled.RootElement.GetProperty("deploymentMode").GetString().Should().Be("split");
        _ = enabled.RootElement.GetProperty("calibrationContextEnabled").GetBoolean()
            .Should().BeTrue();
        _ = enabled.RootElement.GetProperty("calibration").GetProperty("operational")
            .GetBoolean().Should().BeTrue();

        // No public surface may disclose the internal slicer-host address.
        string capabilityBody = enabled.RootElement.GetRawText();
        _ = capabilityBody.Should().NotContain("slicer-host.internal");
    }

    [Fact]
    public async Task Capabilities_ReportContextUnavailableWhileCandidatesRemainAvailable()
    {
        CalibrationPrinterSeeder.SeededPrinter seeded =
            await CalibrationPrinterSeeder.SeedAsync(_factory.Services);
        using HttpClient client = CreateCalibrationReaderClient();
        await _slicerHost!.DisposeAsync();
        _slicerHost = null;
        _factory.SlicerHostHandler = null;

        using JsonDocument disabled =
            await client.GetFromJsonAsync<JsonDocument>("/api/system/capabilities")
            ?? throw new InvalidOperationException("Missing capability response.");

        _ = disabled.RootElement.GetProperty("calibrationContextEnabled").GetBoolean()
            .Should().BeFalse();
        _ = disabled.RootElement.GetProperty("unavailableReasons").EnumerateArray()
            .Select(reason => reason.GetProperty("code").GetString())
            .Should().Contain("profile_service_unavailable");

        _factory.ResetSlicerHostRequestCount();
        HttpResponseMessage candidates =
            await client.GetAsync("/api/printers/calibration-candidates");
        string body = await candidates.Content.ReadAsStringAsync();
        _ = candidates.StatusCode.Should().Be(HttpStatusCode.OK, body);
        using JsonDocument candidateList = JsonDocument.Parse(body);
        JsonElement candidate = candidateList.RootElement.EnumerateArray()
            .Single(element => element.GetProperty("id").GetGuid() == seeded.PrinterId);
        _ = candidate.GetProperty("profilesEvaluated").GetBoolean().Should().BeFalse();
        _ = _factory.SlicerHostRequestCount.Should().Be(
            0,
            "candidate listing must not contact the unavailable slicer host");

        HttpResponseMessage context = await client.GetAsync(
            $"/api/printers/{seeded.PrinterId}/calibration-context?slicerType=OrcaSlicer");
        string contextBody = await context.Content.ReadAsStringAsync();
        _ = context.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable, contextBody);
        using JsonDocument problem = JsonDocument.Parse(contextBody);
        _ = problem.RootElement.GetProperty("code").GetString()
            .Should().Be("profile_service_unavailable");
        _ = _factory.SlicerHostRequestCount.Should().Be(
            1,
            "selected context must make exactly one resolver request");
    }

    private HttpClient CreateCalibrationReaderClient(Guid? userId = null) =>
        CreateClient(userId ?? Guid.NewGuid(), [PrintFarmerPermissions.Calibration.Read]);

    private HttpClient CreateFarmAdminClient() =>
        CreateClient(Guid.NewGuid(), [], [PrintFarmerPermissions.FarmAdminRole]);

    private HttpClient CreateClient(
        Guid userId,
        IEnumerable<string> permissions,
        IEnumerable<string>? roles = null)
    {
        IConfiguration configuration = _factory.Services.GetRequiredService<IConfiguration>();
        HttpClient client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            CalibrationTestJwt.Create(
                configuration["Jwt:Key"] ?? CalibrationTestJwt.Key,
                configuration["Jwt:Issuer"] ?? CalibrationTestJwt.Issuer,
                configuration["Jwt:Audience"] ?? CalibrationTestJwt.Audience,
                userId,
                permissions,
                roles));
        return client;
    }
}

/// <summary>
/// Boots the API exactly the way a split deployment does — no in-process slicer module — and lets a
/// test attach the slicer-host test server that its resolver adapter dials.
/// </summary>
internal sealed class SplitCalibrationWebApplicationFactory : CustomWebApplicationFactory
{
#pragma warning disable S1075 // Deterministic in-process test address; never resolved over a network.
    internal const string SlicerHostBaseUrl = "http://slicer-host.internal:5246";
#pragma warning restore S1075

    private const string DeploymentModeVariable = "DEPLOYMENT_MODE";
    private const string SlicerHostUrlVariable = "SlicerHost__BaseUrl";

    private readonly string? _originalDeploymentMode;
    private readonly string? _originalSlicerHostUrl;
    private int _slicerHostRequestCount;

    public SplitCalibrationWebApplicationFactory()
        : base(new Dictionary<string, string?>
        {
            ["Security:DevModeBypassAuth"] = "false",
            ["Jwt:Key"] = CalibrationTestJwt.Key,
            ["Jwt:Issuer"] = CalibrationTestJwt.Issuer,
            ["Jwt:Audience"] = CalibrationTestJwt.Audience,
        })
    {
        // Both values must be visible to Program.cs at service-registration time, which rules out
        // ConfigureAppConfiguration. Environment variables are in the configuration from the start.
        _originalDeploymentMode = Environment.GetEnvironmentVariable(DeploymentModeVariable);
        _originalSlicerHostUrl = Environment.GetEnvironmentVariable(SlicerHostUrlVariable);
        Environment.SetEnvironmentVariable(DeploymentModeVariable, "microservices");
        Environment.SetEnvironmentVariable(SlicerHostUrlVariable, SlicerHostBaseUrl);
    }

    /// <summary>Transport the registered resolver adapter dials; set before the first request.</summary>
    public HttpMessageHandler? SlicerHostHandler { get; set; }

    public int SlicerHostRequestCount => Volatile.Read(ref _slicerHostRequestCount);

    public void ResetSlicerHostRequestCount() => Interlocked.Exchange(
        ref _slicerHostRequestCount,
        0);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureServices(services =>
            _ = services
                .AddHttpClient(nameof(ICalibrationProfileResolver))

                // Resolved per request, because the API host boots (and may probe the resolver)
                // before the test server that answers it exists.
                .ConfigurePrimaryHttpMessageHandler(() => new DeferredSlicerHostHandler(
                    () => SlicerHostHandler,
                    () => Interlocked.Increment(ref _slicerHostRequestCount)))
                .SetHandlerLifetime(Timeout.InfiniteTimeSpan));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Environment.SetEnvironmentVariable(DeploymentModeVariable, _originalDeploymentMode);
            Environment.SetEnvironmentVariable(SlicerHostUrlVariable, _originalSlicerHostUrl);
        }

        base.Dispose(disposing);
    }

    /// <summary>
    /// Forwards to the currently attached slicer-host test server, or fails the request the way an
    /// unreachable slicer host would.
    /// </summary>
    private sealed class DeferredSlicerHostHandler(
        Func<HttpMessageHandler?> provider,
        Action onRequest)
        : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            onRequest();
            HttpMessageHandler inner = provider()
                ?? throw new HttpRequestException("The slicer host is not reachable.");
            using HttpMessageInvoker invoker = new(inner, disposeHandler: false);
            return await invoker.SendAsync(request, cancellationToken);
        }
    }
}
