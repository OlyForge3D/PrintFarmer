using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using Farm.Infrastructure.PrinterCalibration;
using Farm.Slicer.Module.Api.Controllers.Calibration;
using Farm.Slicer.Module.Api.Filters;
using Farm.Slicer.Module.Api.Services;
using Farm.Slicer.Module.Data;
using Farm.Slicer.Module.Domain;
using Farm.Slicer.Module.Services.Configuration;
using Farm.Web.Api.Services.Calibration;
using Farm.Web.Api.Services.Calibration.Generation;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Farm.Web.Api.Tests.Calibration;

/// <summary>
/// Real end-to-end proof of the split-deployment worker-compatibility hop (issue #1848): the
/// production <see cref="SlicerHostCapabilityClient"/> dials the production
/// <see cref="WorkerCompatibilityController"/> (with its real <see cref="RequireSlicerApiKeyAttribute"/>
/// filter and a real <see cref="SlicerHostWorkerCompatibilityService"/> reading a real SQLite-backed
/// <c>SlicerDbContext</c>) over an in-process HTTP test server. This is what
/// <see cref="WorkerCompatibilityControllerTests"/> (direct action-method invocation) and
/// <see cref="SlicerHostCapabilityClientTests"/> (client against a hand-built JSON fixture) cannot
/// prove on their own: that the route, query parameter name, auth header, and JSON casing genuinely
/// agree between the two independently-compiled assemblies rather than merely against each side's own
/// expectations.
/// </summary>
public sealed class WorkerCompatibilityEndpointTests : IAsyncDisposable
{
    private const string SharedKey = "endpoint-test-shared-key";

    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private WebApplication? _slicerHost;

    public async ValueTask DisposeAsync()
    {
        if (_slicerHost is not null)
        {
            await _slicerHost.StopAsync();
            await _slicerHost.DisposeAsync();
        }

        await _connection.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task GetWorkerCompatibilityAsync_RealHttpHop_ReturnsThePinnedIdentityTheClientExpects()
    {
        Guid workerId = Guid.NewGuid();
        await StartSlicerHostAsync();
        await SeedHealthyAttestedWorkerAsync(workerId);
        ISlicerHostCapabilityClient client = CreateClient(SharedKey);

        WorkerCompatibilitySnapshotDto snapshot =
            await client.GetWorkerCompatibilityAsync(null, CancellationToken.None);

        _ = snapshot.HasSupportedVersion.Should().BeTrue();
        _ = snapshot.ObservedVersions.Should().Contain(CalibrationContractConstants.SlicerVersion);
        _ = snapshot.PinnedIdentity.Should().NotBeNull();
        _ = snapshot.PinnedIdentity!.WorkerId.Should().Be(workerId);
        _ = snapshot.PinnedIdentity.Version.Should().Be(CalibrationContractConstants.SlicerVersion);
        _ = snapshot.PinnedIdentity.Distribution.Should().Be(CalibrationContractConstants.SlicerDistribution);
    }

    [Fact]
    public async Task GetWorkerCompatibilityAsync_RequiredVersionQueryParam_ReachesTheServerAndFiltersThePin()
    {
        Guid workerId = Guid.NewGuid();
        await StartSlicerHostAsync();
        await SeedHealthyAttestedWorkerAsync(workerId);
        ISlicerHostCapabilityClient client = CreateClient(SharedKey);

        WorkerCompatibilitySnapshotDto snapshot =
            await client.GetWorkerCompatibilityAsync("9.9.9", CancellationToken.None);

        // The version filter is honoured server-side (the client only sent a query string); the
        // service itself remains observed/supported, but nothing can be pinned to the mismatched
        // required version.
        _ = snapshot.PinnedIdentity.Should().BeNull();
        _ = snapshot.HasSupportedVersion.Should().BeTrue();
    }

    [Fact]
    public async Task GetWorkerCompatibilityAsync_WrongSharedKey_Returns401AndClientDegradesToEmpty()
    {
        await StartSlicerHostAsync();
        using HttpClient rawClient = _slicerHost!.GetTestServer().CreateClient();
        rawClient.DefaultRequestHeaders.Add(
            WorkerCompatibilityContract.ApiKeyHeaderName,
            "not-the-configured-key");

        HttpResponseMessage response = await rawClient.GetAsync(
            "/" + WorkerCompatibilityContract.WorkerCompatibilityRelativeRoute);

        _ = response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        // The production client must still degrade to Empty rather than throw or surface the 401.
        ISlicerHostCapabilityClient client = CreateClient("wrong-key-configured-on-the-client-side");
        WorkerCompatibilitySnapshotDto snapshot =
            await client.GetWorkerCompatibilityAsync(null, CancellationToken.None);
        _ = snapshot.Should().Be(WorkerCompatibilitySnapshotDto.Empty);
    }

    [Fact]
    public async Task GetWorkerCompatibilityAsync_MissingSharedKey_Returns401()
    {
        await StartSlicerHostAsync();
        using HttpClient rawClient = _slicerHost!.GetTestServer().CreateClient();

        HttpResponseMessage response = await rawClient.GetAsync(
            "/" + WorkerCompatibilityContract.WorkerCompatibilityRelativeRoute);

        _ = response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetWorkerCompatibilityAsync_NoEligibleWorker_ReturnsCamelCaseEmptySnapshotOverTheWire()
    {
        await StartSlicerHostAsync();
        using HttpClient rawClient = _slicerHost!.GetTestServer().CreateClient();
        rawClient.DefaultRequestHeaders.Add(WorkerCompatibilityContract.ApiKeyHeaderName, SharedKey);

        HttpResponseMessage response = await rawClient.GetAsync(
            "/" + WorkerCompatibilityContract.WorkerCompatibilityRelativeRoute);
        string body = await response.Content.ReadAsStringAsync();

        _ = response.StatusCode.Should().Be(HttpStatusCode.OK, body);

        // Proves the server actually serializes camelCase (not merely that the client's own
        // camelCase serializer options can round-trip a payload it wrote itself).
        _ = body.Should().Contain("\"pinnedIdentity\"");
        _ = body.Should().Contain("\"observedVersions\"");
        _ = body.Should().Contain("\"hasSupportedVersion\"");
    }

    private async Task StartSlicerHostAsync()
    {
        await _connection.OpenAsync();
        DbContextOptions<SlicerDbContext> options = new DbContextOptionsBuilder<SlicerDbContext>()
            .UseSqlite(_connection)
            .Options;
        await using (SlicerDbContext setup = new(options))
        {
            _ = await setup.Database.EnsureCreatedAsync();
        }

        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder(
            new WebApplicationOptions
            {
                EnvironmentName = Environments.Development,
                ContentRootPath = AppContext.BaseDirectory,
            });
        builder.Logging.ClearProviders();
        builder.WebHost.UseTestServer();

        _ = builder.Services.AddSingleton<IDbContextFactory<SlicerDbContext>>(
            new FixedOptionsSlicerDbContextFactory(options));
        _ = builder.Services.AddScoped<ISlicerHostWorkerCompatibilityService, SlicerHostWorkerCompatibilityService>();
        _ = builder.Services.AddScoped<ISlicerApiKeyValidator>(_ => new SharedKeyOnlyValidator(SharedKey));

        _ = builder.Services
            .AddControllers()
            .ConfigureApplicationPartManager(manager =>
            {
                manager.ApplicationParts.Clear();
                manager.ApplicationParts.Add(new WorkerCompatibilityApplicationPart());
            });

        WebApplication app = builder.Build();
        _ = app.MapControllers();

        await app.StartAsync();
        _slicerHost = app;
    }

    private async Task SeedHealthyAttestedWorkerAsync(Guid workerId)
    {
        DbContextOptions<SlicerDbContext> options = new DbContextOptionsBuilder<SlicerDbContext>()
            .UseSqlite(_connection)
            .Options;
        Guid serviceId = Guid.NewGuid();
        DateTime now = DateTime.UtcNow;
        string capabilitiesJson =
            $$"""
              {
                "capabilities": ["orcaslicer-upstream"],
                "slicerContainerDigest": "digest-container",
                "slicerBinarySha256": "digest-binary"
              }
              """;

        await using SlicerDbContext db = new(options);
        _ = db.SlicerServices.Add(new SlicerService
        {
            Id = serviceId,
            Name = "orca-endpoint-1",
            SlicerType = (int)SlicerType.OrcaSlicer,
            Version = CalibrationContractConstants.SlicerVersion,
            Status = WorkerStatus.Online,
            LastSeen = now,
            CapabilitiesJson = capabilitiesJson,
        });
        _ = db.Workers.Add(new Worker
        {
            Id = workerId,
            ServiceId = serviceId.ToString(),
            Name = "orca-endpoint-1-worker",
            EndpointUrl = "https://orca-endpoint-1.internal",
            Status = WorkerStatus.Online,
            Version = CalibrationContractConstants.SlicerVersion,
            CapabilitiesJson = capabilitiesJson,
            ApiKey = "worker-key",
            IsDisabled = false,
            LastHeartbeat = now,
            RegisteredAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        });
        _ = await db.SaveChangesAsync();
    }

    private ISlicerHostCapabilityClient CreateClient(string configuredSharedKey)
    {
        HttpClient httpClient = _slicerHost!.GetTestServer().CreateClient();
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [WorkerAuthConfiguration.SharedKeyPath] = configuredSharedKey,
            })
            .Build();
        SlicerHostCalibrationResolverOptions options = new()
        {
            BaseUrl = httpClient.BaseAddress!,
        };

        return new SlicerHostCapabilityClient(
            httpClient,
            configuration,
            options,
            NullLogger<SlicerHostCapabilityClient>.Instance);
    }

    /// <summary>Shares one already-open SQLite connection across every context in this test.</summary>
    private sealed class FixedOptionsSlicerDbContextFactory(DbContextOptions<SlicerDbContext> options)
        : IDbContextFactory<SlicerDbContext>
    {
        public SlicerDbContext CreateDbContext() => new(options);
    }

    /// <summary>
    /// Validates only the shared key, exactly what <see cref="RequireSlicerApiKeyAttribute"/> calls on
    /// this endpoint. The production <c>SlicerApiKeyValidator</c> also validates per-service worker
    /// keys via repositories this test does not need to stand up.
    /// </summary>
    private sealed class SharedKeyOnlyValidator(string sharedKey) : ISlicerApiKeyValidator
    {
        public Task<bool> ValidateSharedKeyAsync(string? apiKey, CancellationToken ct = default) =>
            Task.FromResult(!string.IsNullOrEmpty(apiKey) && string.Equals(apiKey, sharedKey, StringComparison.Ordinal));

        public Task<bool> ValidateServiceKeyAsync(Guid serviceId, string? apiKey, CancellationToken ct = default) =>
            throw new NotSupportedException("This endpoint only requires the shared key.");
    }

    /// <summary>Exposes only the worker-compatibility controller on the in-process test server.</summary>
    private sealed class WorkerCompatibilityApplicationPart : ApplicationPart, IApplicationPartTypeProvider
    {
        public override string Name => nameof(WorkerCompatibilityApplicationPart);

        public IEnumerable<TypeInfo> Types { get; } =
            [typeof(WorkerCompatibilityController).GetTypeInfo()];
    }
}
