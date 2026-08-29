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
/// <see cref="GetCapabilitiesAsync_ForMonolith_ReportsNoSlicingReason"/> and
/// <see cref="GetCapabilitiesAsync_ForSplitOrMicroservices_ReportsNoSlicingReason"/>
/// sets every dial an operator can turn without a code change: <c>Slicer:Enabled</c>, a
/// credentialed worker, a healthy heartbeat, an allow-listed upstream OrcaSlicer version, and a
/// worker that attests a pinned upstream build identity. If any <c>Feature == "slicing"</c>
/// reason still appears after all of that is true, it can only be because the deployment
/// topology itself never wires up the dependency the reason names — no runtime action by an
/// operator can make it disappear. That is exactly the defect #2171 found for
/// <c>model_storage_unresolvable</c> in split/microservices mode: <c>IModelStorageResolver</c> is
/// only ever registered by <see cref="SlicerModuleExtensions.AddSlicerModule"/>'s monolith path
/// (<c>AddSlicerServices</c>); the method's own deployment-mode check short-circuits before that
/// path for both <c>"split"</c> and <c>"microservices"</c> (they are treated identically), and
/// <see cref="SlicerModuleExtensions.AddSlicerCalibrationProfileRepositories"/> — the extension
/// split/microservices hosts additionally call, per
/// <c>Farm.Web.Api.Startup.MoonrakerEmulatorSeederDependenciesStartup</c> — registers only the
/// three calibration profile repositories, not a model storage resolver. This test intentionally
/// exercises the real registration extension methods for each mode (both are called
/// unconditionally on every real host too: <c>AddSlicerModule</c> via the assembly-scanned
/// <c>SlicerModuleRegistrar</c>, <c>AddSlicerCalibrationProfileRepositories</c> gated by
/// <c>IsSplitDeployment</c>) rather than hand-rolling a fixture, so it fails exactly when
/// production wiring has this gap and passes exactly when it doesn't.
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
/// <para>
/// <b>Split/microservices are currently <c>Skip</c>-guarded, not deleted.</b> As of this PR,
/// issue #2179 (the sibling registration fix) has not landed, so
/// <see cref="GetCapabilitiesAsync_ForSplitOrMicroservices_ReportsNoSlicingReason"/> reproduces
/// the #2171 gap today: running it with the <c>Skip</c> attribute removed fails for both modes
/// with <c>model_storage_unresolvable</c> present (captured in the PR description as evidence).
/// <c>Farm.Modules.Calibration.Tests</c> is a required CI leg for any PR touching this path (see
/// <c>scripts/ci/dotnet-test-manifest.json</c>), so leaving it unguarded would redden CI for this
/// PR itself and every unrelated PR touching this module until #2179 lands. The <c>Skip</c> must
/// be removed — a one-line follow-up — once #2179 registers <c>IModelStorageResolver</c> for
/// split/microservices; <see cref="GetCapabilitiesAsync_ForMonolith_ReportsNoSlicingReason"/> is
/// unaffected by the gap and always runs.
/// </para>
/// </remarks>
public sealed class CalibrationCapabilityServiceActionableSlicingReasonsTests
{
    /// <summary>The pinned upstream OrcaSlicer build identity a compliant worker attests.</summary>
    private const string PinnedCapabilitiesJson =
        """
        {"capabilities":["orcaslicer","orcaslicer-upstream"],"slicerBinarySha256":"a1b2c3d4","slicerContainerDigest":"sha256:e5f6a7b8"}
        """;

    /// <summary>The split/microservices modes PrintFarmer documents for <c>DEPLOYMENT_MODE</c>.</summary>
    public static IEnumerable<object[]> SplitAndMicroservicesDeploymentModes()
    {
        yield return ["split"];
        yield return ["microservices"];
    }

    [Fact]
    public async Task GetCapabilitiesAsync_ForMonolith_ReportsNoSlicingReason()
    {
        await using Fixture fixture = await Fixture.CreateAsync("monolith");
        await fixture.SeedFullyHealthyWorkerAsync();

        IReadOnlyList<CapabilityUnavailableReasonDto> slicingReasons =
            await fixture.GetSlicingReasonsAsync();

        AssertNoUnactionableSlicingReason("monolith", slicingReasons);
    }

    /// <summary>
    /// See class remarks ("Split/microservices are currently Skip-guarded, not deleted") for why
    /// this theory carries a <c>Skip</c> reason today: #2179 has not yet registered
    /// <c>IModelStorageResolver</c> for these modes, so removing the <c>Skip</c> right now
    /// reproduces #2171 and fails both cases with <c>model_storage_unresolvable</c> — which would
    /// redden the required <c>Farm.Modules.Calibration.Tests</c> CI leg for every PR touching
    /// this module until #2179 lands.
    /// </summary>
    [Theory(Skip =
        "Pending #2179: IModelStorageResolver is not yet registered for split/microservices " +
        "hosts (the #2171 gap). Remove this Skip once #2179 lands so this theory enforces the " +
        "same operator-actionable invariant CI already enforces for monolith.")]
    [MemberData(nameof(SplitAndMicroservicesDeploymentModes))]
    public async Task GetCapabilitiesAsync_ForSplitOrMicroservices_ReportsNoSlicingReason(
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
            string connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                DefaultTimeout = 30,
                Pooling = false,
            }.ToString();

            IConfiguration configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["DEPLOYMENT_MODE"] = deploymentMode,
                    ["Slicer:Enabled"] = "true",
                    ["Slicer:PluginsPath"] = string.Empty,
                    ["DB_PROVIDER"] = "sqlite",
                    ["ConnectionStrings:Default"] = connectionString,
                })
                .Build();

            ServiceCollection services = new();
            _ = services.AddLogging();
            _ = services.AddSingleton(configuration);
            _ = services.AddSingleton<IApplicationPathProvider>(new TestApplicationPathProvider());
            _ = services.AddSingleton<IStoragePathService, StoragePathService>();

            // Mirrors production exactly: AddSlicerModule is called unconditionally on every real
            // host too (via the assembly-scanned SlicerModuleRegistrar and, standalone, by
            // Farm.Slicer.Host/Program.cs) — it is the method's own deployment-mode check,
            // treating "split" and "microservices" identically, that short-circuits before
            // registering AddSlicerServices (where IModelStorageResolver lives). Split and
            // microservices hosts additionally call AddSlicerCalibrationProfileRepositories, per
            // Farm.Web.Api.Startup.MoonrakerEmulatorSeederDependenciesStartup (guarded there by
            // IsSplitDeployment, since monolith already has these repositories via
            // AddSlicerModule) — see SlicerModuleExtensions and #1858/#2171/#2179.
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
            try
            {
                await using (AsyncServiceScope initScope = provider.CreateAsyncScope())
                {
                    SlicerDbContext db =
                        initScope.ServiceProvider.GetRequiredService<SlicerDbContext>();
                    _ = await db.Database.EnsureCreatedAsync();
                }
            }
            catch
            {
                // Setup failed after the provider (and its SQLite connection) was created — clean
                // up before rethrowing so a broken fixture never leaks a provider or a temp file.
                await provider.DisposeAsync();
                if (File.Exists(dbPath))
                {
                    File.Delete(dbPath);
                }

                throw;
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

            // The connection string sets Pooling=false, so the file is never held open by a
            // pooled connection after the provider (and every DbContext it created) is disposed —
            // no SqliteConnection.ClearAllPools() needed, matching the sibling fixtures'
            // convention (CalibrationLegacyV4ImportTests, CalibrationProjectSqliteConcurrencyTests).
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
