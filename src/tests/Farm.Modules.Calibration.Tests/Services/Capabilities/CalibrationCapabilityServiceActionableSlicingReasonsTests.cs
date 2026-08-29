using Farm.Infrastructure.Dtos;
using Farm.Infrastructure.PrinterCalibration;
using Farm.Infrastructure.Services.StorageManagement;
using Farm.Modules.Calibration.Services.Capabilities;
using Farm.Slicer.Module;
using Farm.Slicer.Module.Data;
using Farm.Slicer.Module.Domain;
using Farm.Slicer.Module.Services;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit.Sdk;

namespace Farm.Modules.Calibration.Tests.Services.Capabilities;

/// <summary>
/// Covers issue #2177 (part of epic #2176, following the analysis in #2171): every
/// <c>CapabilityUnavailableReasonDto</c> with <c>Feature == "slicing"</c> that
/// <see cref="CalibrationCapabilityService.GetCapabilitiesAsync"/> can emit must correspond to a
/// condition an operator can actually clear in that deployment topology — monolith, split, or
/// microservices, the three modes PrintFarmer documents (<c>DEPLOYMENT_MODE</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>What "operator-actionable" means here.</b> Every fixture in
/// <see cref="GetCapabilitiesAsync_WithEveryOperatorControllablePreconditionSatisfied_ReportsNoSlicingReason"/>
/// sets every dial an operator can turn without a code change: <c>Slicer:Enabled</c>, a
/// credentialed worker, a healthy heartbeat, an allow-listed upstream OrcaSlicer version, and a
/// worker that attests a pinned upstream build identity. If any <c>Feature == "slicing"</c>
/// reason still appears after all of that is true, it can only be because the deployment
/// topology itself never wires up the dependency the reason names — no runtime action by an
/// operator can make it disappear. That is exactly the defect #2171 found for
/// <c>model_storage_unresolvable</c> in split/microservices mode: <c>IModelStorageResolver</c> is
/// only ever registered by <see cref="SlicerModuleExtensions.AddSlicerModule"/>'s monolith path
/// (<c>AddSlicerServices</c>), and split/microservices hosts skip that path entirely (they only
/// call <see cref="SlicerModuleExtensions.AddSlicerCalibrationProfileRepositories"/>, which does
/// not register a model storage resolver). This test intentionally exercises the real
/// registration extension methods for each mode rather than hand-rolling a fixture, so it fails
/// exactly when production wiring has this gap and passes exactly when it doesn't.
/// </para>
/// <para>
/// <b>Why this is not vacuous.</b> The assertion is factored into
/// <see cref="AssertNoUnactionableSlicingReason"/>, a fixed rule ("no slicing reason once every
/// operator dial is set") that never changes just because a new reason code is added — a future
/// engineer adding a new "slicing" reason must either make it reachable only when something an
/// operator can't yet control is still missing (fine, the fixture already covers it), or
/// deliberately widen this remark to document a new, reviewed structurally-unresolvable case.
/// <see cref="AssertNoUnactionableSlicingReason_WhenModelStorageResolverRegistrationIsReintroducedAsMissing_Fails"/>
/// is the negative control: it deliberately reintroduces the #2171 condition — by removing the
/// <c>IModelStorageResolver</c> registration a healthy monolith fixture would otherwise have —
/// and asserts that <see cref="AssertNoUnactionableSlicingReason"/> then fails. That proves the
/// assertion actually discriminates between the healthy and the broken case, rather than being
/// trivially true regardless of what reasons are passed to it.
/// </para>
/// </remarks>
public sealed class CalibrationCapabilityServiceActionableSlicingReasonsTests
{
    /// <summary>The pinned upstream OrcaSlicer build identity a compliant worker attests.</summary>
    private const string PinnedCapabilitiesJson =
        """
        {"capabilities":["orcaslicer","orcaslicer-upstream"],"slicerBinarySha256":"a1b2c3d4","slicerContainerDigest":"sha256:e5f6a7b8"}
        """;

    /// <summary>Every deployment mode PrintFarmer documents for <c>DEPLOYMENT_MODE</c>.</summary>
    public static IEnumerable<object[]> DocumentedDeploymentModes()
    {
        yield return ["monolith"];
        yield return ["split"];
        yield return ["microservices"];
    }

    [Theory]
    [MemberData(nameof(DocumentedDeploymentModes))]
    public async Task GetCapabilitiesAsync_WithEveryOperatorControllablePreconditionSatisfied_ReportsNoSlicingReason(
        string deploymentMode)
    {
        await using Fixture fixture = await Fixture.CreateAsync(deploymentMode);
        await fixture.SeedFullyHealthyWorkerAsync();

        IReadOnlyList<CapabilityUnavailableReasonDto> slicingReasons =
            await fixture.GetSlicingReasonsAsync();

        AssertNoUnactionableSlicingReason(deploymentMode, slicingReasons);
    }

    /// <summary>
    /// Negative control (see class remarks): reintroduces the #2171 condition — a deployment
    /// where <c>IModelStorageResolver</c> is not registered even though every operator-controllable
    /// dial (worker health, credentials, pinned identity, version) is satisfied — and proves
    /// <see cref="AssertNoUnactionableSlicingReason"/> actually fails against it, rather than
    /// passing regardless of input.
    /// </summary>
    [Fact]
    public async Task AssertNoUnactionableSlicingReason_WhenModelStorageResolverRegistrationIsReintroducedAsMissing_Fails()
    {
        await using Fixture fixture = await Fixture.CreateAsync(
            "monolith",
            removeModelStorageResolverRegistration: true);
        await fixture.SeedFullyHealthyWorkerAsync();

        IReadOnlyList<CapabilityUnavailableReasonDto> slicingReasons =
            await fixture.GetSlicingReasonsAsync();

        // Sanity-check the fixture actually reproduces the reported symptom before proving the
        // assertion catches it — otherwise a broken fixture could pass this negative control for
        // the wrong reason (e.g. by producing no reasons at all).
        _ = slicingReasons.Should().Contain(
            reason => reason.Code == "model_storage_unresolvable",
            "removing the IModelStorageResolver registration must reproduce the #2171 symptom");

        Action assertion = () => AssertNoUnactionableSlicingReason("monolith", slicingReasons);

        _ = assertion.Should().Throw<XunitException>(
            "the assertion must fail when a structurally-unresolvable slicing reason is present " +
            "despite every operator-controllable precondition being satisfied, proving it is not " +
            "vacuously true");
    }

    /// <summary>
    /// The single rule under test: once every operator-controllable precondition is satisfied,
    /// zero <c>Feature == "slicing"</c> reasons may remain. See class remarks for why this
    /// specific rule is the correct definition of "operator-actionable" for this assertion.
    /// </summary>
    private static void AssertNoUnactionableSlicingReason(
        string deploymentMode,
        IReadOnlyList<CapabilityUnavailableReasonDto> slicingReasons)
    {
        _ = slicingReasons.Should().BeEmpty(
            because:
            "deployment mode '{0}' has every operator-controllable slicing precondition satisfied " +
            "(worker healthy, credentialed, pinned identity attested, compatible version); any " +
            "remaining 'slicing' reason ({1}) would mean the topology itself never wires up the " +
            "dependency it names, which no operator action could ever clear",
            deploymentMode,
            string.Join(", ", slicingReasons.Select(reason => reason.Code)));
    }

    /// <summary>
    /// Builds a <see cref="CalibrationCapabilityService"/> backed by the same DI registration
    /// extension methods production hosts call for a given <c>DEPLOYMENT_MODE</c>, plus a real
    /// SQLite-backed <see cref="SlicerDbContext"/> that <see cref="SeedFullyHealthyWorkerAsync"/>
    /// populates.
    /// </summary>
    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _dbPath;
        private readonly ServiceProvider _provider;

        private Fixture(IConfiguration configuration, ServiceProvider provider, string dbPath)
        {
            Configuration = configuration;
            _provider = provider;
            _dbPath = dbPath;
        }

        private IConfiguration Configuration { get; }

        public static async Task<Fixture> CreateAsync(
            string deploymentMode,
            bool removeModelStorageResolverRegistration = false)
        {
            string dbPath = Path.Combine(
                Path.GetTempPath(),
                $"calibration-actionable-reasons-{Guid.NewGuid():N}.db");

            IConfiguration configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["DEPLOYMENT_MODE"] = deploymentMode,
                    ["Slicer:Enabled"] = "true",
                    ["Slicer:PluginsPath"] = string.Empty,
                    ["DB_PROVIDER"] = "sqlite",
                    ["ConnectionStrings:Default"] = $"Data Source={dbPath}",
                })
                .Build();

            ServiceCollection services = new();
            _ = services.AddLogging();
            _ = services.AddSingleton(configuration);
            _ = services.AddSingleton<IApplicationPathProvider>(new TestApplicationPathProvider());
            _ = services.AddSingleton<IStoragePathService, StoragePathService>();

            // Mirrors production exactly: monolith's AddSlicerModule fully registers the slicer
            // module (including IModelStorageResolver); split/microservices short-circuit inside
            // AddSlicerModule and rely solely on AddSlicerCalibrationProfileRepositories, which
            // registers only the three profile repositories and the DbContext (see
            // SlicerModuleExtensions and #1858/#2171/#2179). Calling both unconditionally, for
            // every mode, is what Farm.Web.Api.Startup.MoonrakerEmulatorSeederDependenciesStartup
            // effectively does across the whole fleet of hosts.
            _ = services.AddSlicerModule(configuration);
            _ = services.AddSlicerCalibrationProfileRepositories(configuration);

            if (removeModelStorageResolverRegistration)
            {
                ServiceDescriptor[] resolverDescriptors = services
                    .Where(descriptor => descriptor.ServiceType == typeof(IModelStorageResolver))
                    .ToArray();
                foreach (ServiceDescriptor descriptor in resolverDescriptors)
                {
                    _ = services.Remove(descriptor);
                }
            }

            ServiceProvider provider = services.BuildServiceProvider();

            await using (AsyncServiceScope initScope = provider.CreateAsyncScope())
            {
                SlicerDbContext db = initScope.ServiceProvider.GetRequiredService<SlicerDbContext>();
                _ = await db.Database.EnsureCreatedAsync();
            }

            return new Fixture(configuration, provider, dbPath);
        }

        /// <summary>
        /// Seeds a single worker that satisfies every operator-controllable precondition for
        /// <c>calibrationSlicingOperational</c>: slicing enabled, a credentialed and healthy
        /// worker, a fresh heartbeat, an allow-listed upstream OrcaSlicer version, and a pinned
        /// upstream build identity attestation.
        /// </summary>
        public async Task SeedFullyHealthyWorkerAsync()
        {
            await using AsyncServiceScope scope = _provider.CreateAsyncScope();
            SlicerDbContext db = scope.ServiceProvider.GetRequiredService<SlicerDbContext>();

            Guid serviceId = Guid.NewGuid();
            DateTime now = DateTime.UtcNow;
            _ = db.SlicerServices.Add(new SlicerService
            {
                Id = serviceId,
                Name = "actionable-reasons-test-service",
                SlicerType = (int)SlicerType.OrcaSlicer,
                Version = CalibrationContractConstants.SlicerVersion,
                Host = "http://actionable-reasons-worker.internal",
                CapabilitiesJson = PinnedCapabilitiesJson,
                MaxConcurrentJobs = 2,
                Status = WorkerStatus.Online,
                LastSeen = now,
                CreatedAt = now,
                UpdatedAt = now,
            });
            _ = db.Workers.Add(new Worker
            {
                Id = Guid.NewGuid(),
                ServiceId = serviceId.ToString(),
                Name = "actionable-reasons-test-worker",
                EndpointUrl = "http://actionable-reasons-worker.internal",
                CapabilitiesJson = PinnedCapabilitiesJson,
                Version = CalibrationContractConstants.SlicerVersion,
                ApiKey = "actionable-reasons-test-worker-key",
                Status = WorkerStatus.Online,
                TotalSlots = 2,
                ActiveJobs = 0,
                LastHeartbeat = now,
                RegisteredAt = now,
                CreatedAt = now,
                UpdatedAt = now,
                IsDisabled = false,
            });
            _ = await db.SaveChangesAsync();
        }

        public async Task<IReadOnlyList<CapabilityUnavailableReasonDto>> GetSlicingReasonsAsync()
        {
            CalibrationCapabilityService service = new(
                Configuration,
                _provider,
                NullLogger<CalibrationCapabilityService>.Instance);

            PlatformCapabilitiesDto capabilities =
                await service.GetCapabilitiesAsync(null, CancellationToken.None);

            return capabilities.UnavailableReasons
                .Where(reason => reason.Feature == "slicing")
                .ToArray();
        }

        public async ValueTask DisposeAsync()
        {
            await _provider.DisposeAsync();

            // Microsoft.Data.Sqlite pools connections by default, which keeps the file locked for
            // a short while after disposal — clear the pool before deleting.
            SqliteConnection.ClearAllPools();
            if (File.Exists(_dbPath))
            {
                File.Delete(_dbPath);
            }
        }
    }

    /// <summary>
    /// Minimal <see cref="IApplicationPathProvider"/> so <c>StoragePathService</c> (a real
    /// dependency of <c>Model3DStorageResolver</c>) can resolve outside a full ASP.NET Core host.
    /// Not a mock of the code under test — it stands in for the hosting layer only.
    /// </summary>
    private sealed class TestApplicationPathProvider : IApplicationPathProvider
    {
        private readonly string _root = Path.GetTempPath();

        public string GetContentRootPath() => _root;

        public string GetWebRootPath() => _root;
    }
}
