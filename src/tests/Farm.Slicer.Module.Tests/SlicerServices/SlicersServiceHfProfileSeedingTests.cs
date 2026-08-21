using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure;
using Farm.Infrastructure.Dtos;
using Farm.Infrastructure.PrinterCalibration;
using Farm.Infrastructure.Repositories.UnitOfWork;
using Farm.Infrastructure.Services.Catalog;
using Farm.Infrastructure.Services.Gcode;
using Farm.Slicer.Module.Api.Hubs;
using Farm.Slicer.Module.Api.Services;
using Farm.Slicer.Module.Data.Repositories;
using Farm.Slicer.Module.Domain;
using Farm.Slicer.Module.Dtos;
using Farm.Slicer.Module.Services.Metrics;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Farm.Slicer.Module.Tests.SlicerServices;

/// <summary>
/// Regression tests for #1779 targeting the seeding path that actually populates a deployment:
/// <c>SlicersService.SeedProfilesFromWorkerAsync</c>, invoked from
/// <see cref="SlicersService.RegisterAsync"/> when an OrcaSlicer worker registers.
/// </summary>
/// <remarks>
/// <para>
/// The OrcaSlicer worker keys its <c>ByHierarchy</c> groups by each machine profile's
/// <c>printer_model</c>. High-flow variants declare their own distinct <c>printer_model</c>
/// ("Prusa CORE One HF"), which is never a catalog model <c>Name</c> — only a configured
/// OrcaSlicer alias of one. Matching hierarchy groups against base catalog names alone skipped
/// those groups outright, so the 8 CORE One / CORE One L HF machine profiles never reached the
/// database that <c>/api/slicer/profiles/extended</c> reads from, while
/// <c>/api/slicer/profiles/machine/for-model/{id}</c> — which queries the worker live and unions
/// all configured aliases — kept returning them. That is the endpoint disagreement in #1779.
/// </para>
/// <para>
/// PR #1785 fixed this in <c>ProfilesService</c>'s admin-triggered seeds only; the existing
/// <c>ProfilesServiceListExtendedTests</c> / <c>ProfilesServiceSeedBatchingTests</c> coverage
/// therefore passed while production still reproduced. These tests exercise the registration
/// seed instead, so the gap cannot reopen unnoticed.
/// </para>
/// </remarks>
public class SlicersServiceHfProfileSeedingTests
{
    private const string ManufacturerName = "Prusa";
    private const string BaseModelName = "Prusa CORE One";
    private const string HfModelName = "Prusa CORE One HF";
    private const string BaseProfileName = "Prusa CORE One 0.4 nozzle";
    private const string HfProfileName = "Prusa CORE One HF 0.4 nozzle";
    private const string LegacySeedLockKey = "SystemOrcaSlicerProfilesSeedLock";

    /// <summary>
    /// The core regression: the HF hierarchy group is reachable only through a configured OrcaSlicer
    /// alias, and must still be seeded. Before the fix only the base group was imported, so the HF
    /// machine profile never reached the database and <c>extended</c> could not surface it.
    /// </summary>
    [Fact]
    public async Task RegisterAsync_HfHierarchyGroupReachableOnlyViaAlias_SeedsHfMachineProfile()
    {
        SeedHarness harness = new();

        await harness.RegisterOrcaWorkerAsync();

        Assert.Equal(2, harness.PersistedMachineProfiles.Count);
        Assert.Contains(harness.PersistedMachineProfiles, p => p.Name == BaseProfileName);
        Assert.Contains(harness.PersistedMachineProfiles, p => p.Name == HfProfileName);
        Assert.Contains(harness.PersistedMachineProfiles, p => Regex.IsMatch(p.Name, @"\bHF\b"));
    }

    /// <summary>
    /// The HF group resolves to the SAME catalog model as its base group (it is an alias of it), so
    /// the seeded HF profile must carry the base model's <c>PrinterModelId</c>. Without this the
    /// profile would exist but stay unlinked, and Calibration Setup could not bind it to the printer.
    /// </summary>
    [Fact]
    public async Task RegisterAsync_HfProfileSeededViaAlias_IsLinkedToBaseCatalogModel()
    {
        SeedHarness harness = new();

        await harness.RegisterOrcaWorkerAsync();

        MachineProfile hf = Assert.Single(harness.PersistedMachineProfiles, p => p.Name == HfProfileName);
        Assert.Equal(harness.CatalogModelId, hf.PrinterModelId);
    }

    /// <summary>
    /// Acceptance criterion: <c>extended</c> and <c>for-model</c> must agree for a model whose
    /// standard and HF variants share a nozzle diameter. This drives both endpoints off one worker
    /// payload — <c>extended</c> through the seeded database, <c>for-model</c> through the live
    /// alias-union worker query — and asserts the machine-profile name sets match exactly.
    /// </summary>
    [Fact]
    public async Task ExtendedAndForModel_AgreeOnMachineProfilesForSameNozzleHfVariants()
    {
        SeedHarness harness = new();
        await harness.RegisterOrcaWorkerAsync();

        ProfilesService profilesService = harness.CreateProfilesService();

        ExtendedProfilesResponseDto extended = await profilesService.ListExtendedAsync(CancellationToken.None);
        IReadOnlyList<MachineProfileDto> forModel = await profilesService.GetMachineProfilesForCatalogModelAsync(
            harness.CreateWorkerHttpClient(),
            new[] { BaseModelName, HfModelName },
            CancellationToken.None);

        HashSet<string> extendedNames = new(extended.MachineProfiles.Select(p => p.Name), StringComparer.Ordinal);
        HashSet<string> forModelNames = new(forModel.Select(p => p.Name!), StringComparer.Ordinal);

        Assert.Equal(forModelNames, extendedNames);
        Assert.Contains(extendedNames, n => Regex.IsMatch(n, @"\bHF\b"));

        // Same-nozzle variants: the HF row is distinguishable only by name, so both must survive.
        Assert.Equal(2, extended.MachineProfiles.Count);
        Assert.All(extended.MachineProfiles, p => Assert.Equal(0.4, p.NozzleDiameter));
    }

    /// <summary>
    /// The reason every prior fix was invisible in production: the seed latch is a run-once flag, so
    /// an already-seeded deployment never re-ran the corrected seed. The versioned latch must let a
    /// deployment that completed the legacy lock — and therefore already holds system profiles —
    /// run once more and backfill only the missing rows.
    /// </summary>
    [Fact]
    public async Task RegisterAsync_DeploymentSeededUnderLegacyLock_BackfillsMissingHfProfile()
    {
        SeedHarness harness = new();

        // Simulate a deployment seeded by the previous version: the legacy latch is "completed" and
        // system profiles already exist, including the base variant but NOT the HF one.
        harness.MarkLockCompleted(LegacySeedLockKey);
        harness.ExistingSystemProcessProfiles.Add(new ProcessProfile
        {
            Id = Guid.NewGuid(),
            Name = "0.20mm Standard @CORE One",
            SlicerType = SlicerType.OrcaSlicer,
            IsSystem = true,
            Hash = "pre-existing-process-hash"
        });

        await harness.RegisterOrcaWorkerAsync();

        Assert.Contains(harness.PersistedMachineProfiles, p => p.Name == HfProfileName);
        Assert.NotEqual(LegacySeedLockKey, harness.AcquiredLockKey);
    }

    /// <summary>
    /// The versioned latch must still prevent repeated full seeds: once it reports completed, a
    /// subsequent registration does no work at all.
    /// </summary>
    [Fact]
    public async Task RegisterAsync_VersionedLockAlreadyCompleted_DoesNotReseed()
    {
        SeedHarness harness = new();
        await harness.RegisterOrcaWorkerAsync();

        int afterFirstSeed = harness.PersistedMachineProfiles.Count;
        await harness.RegisterOrcaWorkerAsync();

        Assert.Equal(afterFirstSeed, harness.PersistedMachineProfiles.Count);
    }

    /// <summary>
    /// A hierarchy group that is neither a catalog model name nor a configured alias must still be
    /// skipped — the fix widens matching to aliases, it does not disable catalog filtering.
    /// </summary>
    [Fact]
    public async Task RegisterAsync_HierarchyGroupThatIsNeitherCatalogNameNorAlias_IsStillSkipped()
    {
        SeedHarness harness = new();
        harness.AddHierarchyGroup("Prusa MK4S", "Prusa MK4S 0.4 nozzle");

        await harness.RegisterOrcaWorkerAsync();

        Assert.DoesNotContain(harness.PersistedMachineProfiles, p => p.Name == "Prusa MK4S 0.4 nozzle");
    }

    /// <summary>
    /// Test harness wiring a <see cref="SlicersService"/> and a <see cref="ProfilesService"/> onto a
    /// single shared in-memory machine-profile store, so what registration seeding writes is exactly
    /// what the extended endpoint reads back.
    /// </summary>
    private sealed class SeedHarness
    {
        private readonly Dictionary<string, List<MachineProfileDto>> _hierarchy = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _locks = new(StringComparer.Ordinal);

        public SeedHarness()
        {
            CatalogModelId = Guid.NewGuid();
            AddHierarchyGroup(BaseModelName, BaseProfileName);
            AddHierarchyGroup(HfModelName, HfProfileName);
        }

        public Guid CatalogModelId { get; }

        public List<MachineProfile> PersistedMachineProfiles { get; } = new();

        public List<ProcessProfile> ExistingSystemProcessProfiles { get; } = new();

        public string? AcquiredLockKey { get; private set; }

        public void MarkLockCompleted(string key) => _locks[key] = "completed";

        public void AddHierarchyGroup(string printerModel, string machineProfileName)
        {
            _hierarchy[printerModel] = new List<MachineProfileDto>
            {
                new()
                {
                    Name = machineProfileName,
                    Manufacturer = ManufacturerName,
                    PrinterModel = printerModel,
                    NozzleDiameter = 0.4,
                    PrinterVariant = "0.4",
                    Instantiation = true
                }
            };
        }

        public async Task RegisterOrcaWorkerAsync()
        {
            using SlicerDbContext db = TestInfrastructure.TestHelpers.CreateSqliteInMemoryDb();
            SlicersService svc = CreateSlicersService(db);

            _ = await svc.RegisterAsync(
                new RegisterSlicerDto
                {
                    Name = "orca-worker",
                    SlicerType = 1,
                    Version = "2.4.2",
                    Host = "http://worker",
                    MaxConcurrentJobs = 2,
                    CapabilitiesJson = $"[\"{CalibrationContractConstants.UpstreamSlicerCapability}\"]",
                    SeedProfilesOnRegistration = true
                },
                CancellationToken.None);
        }

        public HttpClient CreateWorkerHttpClient()
        {
            return new HttpClient(new StubHttpMessageHandler(request =>
            {
                string path = Uri.UnescapeDataString(request.RequestUri!.AbsolutePath);

                if (path.EndsWith("/version", StringComparison.Ordinal))
                {
                    return new HttpResponseMessage(HttpStatusCode.NotFound);
                }

                // Per-alias machine lookup — the source /api/slicer/profiles/machine/for-model uses.
                const string MachineRoute = "/api/profiles/machine/";
                int machineRouteIndex = path.IndexOf(MachineRoute, StringComparison.Ordinal);
                if (machineRouteIndex >= 0)
                {
                    string alias = path[(machineRouteIndex + MachineRoute.Length)..].Trim('/');
                    List<MachineProfileDto> matches = _hierarchy.TryGetValue(alias, out List<MachineProfileDto>? p)
                        ? p
                        : new List<MachineProfileDto>();

                    return JsonResponse(JsonSerializer.Serialize(matches));
                }

                return JsonResponse(BuildHierarchyJson());
            }));
        }

        public ProfilesService CreateProfilesService()
        {
            Mock<IFilamentProfileRepository> filamentRepo = new(MockBehavior.Loose);
            _ = filamentRepo
                .Setup(r => r.GetByEngineAsync(SlicerType.OrcaSlicer, true, null, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new List<FilamentProfile>());

            Mock<Farm.Slicer.Module.Services.ISlicersService> slicersService = new(MockBehavior.Loose);
            _ = slicersService
                .Setup(s => s.ListAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new List<SlicerService>
                {
                    new()
                    {
                        Name = "orca",
                        SlicerType = 1,
                        Host = "http://worker",
                        Status = "Online",
                        LastSeen = DateTime.UtcNow,
                        Version = "2.4.2",
                        CapabilitiesJson = $"[\"{CalibrationContractConstants.UpstreamSlicerCapability}\"]"
                    }
                });

            Mock<IProfileParsingService> parsingService = new(MockBehavior.Loose);
            _ = parsingService
                .Setup(p => p.ParseAndPrepare(It.IsAny<string>()))
                .Returns((string json) => (json, json, "hash-" + json.GetHashCode(StringComparison.Ordinal).ToString("x", CultureInfo.InvariantCulture)));

            return new ProfilesService(
                new Mock<IProfilesRepository>(MockBehavior.Loose).Object,
                NullLogger<ProfilesService>.Instance,
                CreateProcessProfileRepository().Object,
                CreateMachineProfileRepository().Object,
                filamentRepo.Object,
                new Mock<IUnitOfWork>(MockBehavior.Loose).Object,
                CreateCatalogService().Object,
                parsingService.Object,
                new Mock<IHubContext<SlicerHub>>(MockBehavior.Loose).Object,
                slicersService.Object,
                CreateAliasService().Object);
        }

        private SlicersService CreateSlicersService(SlicerDbContext db)
        {
            Mock<IClientProxy> clientProxy = new();
            _ = clientProxy.Setup(p => p.SendCoreAsync(It.IsAny<string>(), It.IsAny<object[]>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);
            Mock<IHubClients> hubClients = new();
            _ = hubClients.Setup(c => c.Group(It.IsAny<string>())).Returns(clientProxy.Object);
            _ = hubClients.Setup(c => c.All).Returns(clientProxy.Object);
            Mock<IHubContext<SlicerHub>> hub = new();
            _ = hub.SetupGet(h => h.Clients).Returns(hubClients.Object);

            Farm.Slicer.Module.Settings.SlicerSettings slicerSettings = new() { MaxConcurrentJobs = 10, MaxMemoryMb = 4096 };
            Mock<IOptionsMonitor<Farm.Slicer.Module.Settings.SlicerSettings>> settings = new();
            _ = settings.Setup(m => m.CurrentValue).Returns(slicerSettings);

            return new SlicersService(
                new EfSlicersRepository(db),
                new EfWorkerRepository(db),
                CreateProcessProfileRepository().Object,
                CreateFilamentProfileRepository().Object,
                CreateMachineProfileRepository().Object,
                new Mock<IMachineModelProfileRepository>(MockBehavior.Loose).Object,
                CreateCatalogService().Object,
                CreateAliasService().Object,
                CreateSettingsService().Object,
                hub.Object,
                new SlicerServiceMetrics(),
                CreateWorkerHttpClient(),
                NullLogger<SlicersService>.Instance,
                settings.Object);
        }

        private Mock<IMachineProfileRepository> CreateMachineProfileRepository()
        {
            Mock<IMachineProfileRepository> mock = new(MockBehavior.Loose);
            _ = mock.Setup(r => r.AddAsync(It.IsAny<MachineProfile>(), It.IsAny<CancellationToken>()))
                .Callback<MachineProfile, CancellationToken>((p, _) => PersistedMachineProfiles.Add(p))
                .Returns(Task.CompletedTask);
            _ = mock.Setup(r => r.GetByHashAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string hash, CancellationToken _) =>
                    PersistedMachineProfiles.Find(p => string.Equals(p.Hash, hash, StringComparison.Ordinal)));
            _ = mock.Setup(r => r.GetByEngineAsync(SlicerType.OrcaSlicer, true, null, It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => PersistedMachineProfiles);
            return mock;
        }

        private Mock<IProcessProfileRepository> CreateProcessProfileRepository()
        {
            Mock<IProcessProfileRepository> mock = new(MockBehavior.Loose);
            _ = mock.Setup(r => r.GetByEngineAsync(SlicerType.OrcaSlicer, true, null, It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => ExistingSystemProcessProfiles);
            _ = mock.Setup(r => r.GetByHashAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string hash, CancellationToken _) =>
                    ExistingSystemProcessProfiles.Find(p => string.Equals(p.Hash, hash, StringComparison.Ordinal)));
            return mock;
        }

        private static Mock<IFilamentProfileRepository> CreateFilamentProfileRepository()
        {
            Mock<IFilamentProfileRepository> mock = new(MockBehavior.Loose);
            _ = mock.Setup(r => r.GetByEngineAsync(SlicerType.OrcaSlicer, true, null, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new List<FilamentProfile>());
            return mock;
        }

        /// <summary>
        /// Catalog holding ONLY the base model name, with the HF group registered purely as an
        /// OrcaSlicer alias — exactly how <c>printer-models.yaml</c> seeds Prusa CORE One.
        /// </summary>
        private Mock<ICatalogService> CreateCatalogService()
        {
            Mock<ICatalogService> mock = new(MockBehavior.Loose);
            _ = mock.Setup(c => c.GetManufacturersAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync((new List<ManufacturerDto> { new(Guid.NewGuid(), ManufacturerName) }, (string?)null));
            _ = mock.Setup(c => c.GetModelsAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((new List<PrinterModelDto> { new(CatalogModelId, BaseModelName, Guid.NewGuid()) }, (string?)null));
            _ = mock.Setup(c => c.GetModelAliasesAsync(CatalogModelId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new List<SlicerModelAliasDto>
                {
                    new(Guid.NewGuid(), CatalogModelId, BaseModelName, "OrcaSlicer"),
                    new(Guid.NewGuid(), CatalogModelId, HfModelName, "OrcaSlicer")
                });
            return mock;
        }

        private Mock<IPrinterModelAliasService> CreateAliasService()
        {
            Mock<IPrinterModelAliasService> mock = new(MockBehavior.Loose);
            _ = mock.Setup(s => s.ResolveModelAliasAsync(It.IsAny<string>(), It.IsAny<string?>()))
                .ReturnsAsync((string name, string? _) =>
                    string.Equals(name, BaseModelName, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(name, HfModelName, StringComparison.OrdinalIgnoreCase)
                        ? CatalogModelId
                        : null);
            return mock;
        }

        private Mock<Farm.Infrastructure.Settings.ISettingsService> CreateSettingsService()
        {
            Mock<Farm.Infrastructure.Settings.ISettingsService> mock = new(MockBehavior.Loose);
            _ = mock.Setup(s => s.TryAcquireLockAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string key, CancellationToken _) =>
                {
                    if (_locks.TryGetValue(key, out string? state) && (state == "completed" || state == "in-progress"))
                    {
                        return false;
                    }

                    _locks[key] = "in-progress";
                    AcquiredLockKey = key;
                    return true;
                });
            _ = mock.Setup(s => s.CompleteLockAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Callback<string, CancellationToken>((key, _) => _locks[key] = "completed")
                .Returns(Task.CompletedTask);
            _ = mock.Setup(s => s.ClearLockAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Callback<string, CancellationToken>((key, _) => _locks.Remove(key))
                .Returns(Task.CompletedTask);
            _ = mock.Setup(s => s.Get<Farm.Slicer.Module.Settings.SlicerSettings>())
                .Returns(new Farm.Slicer.Module.Settings.SlicerSettings { Enabled = true });
            return mock;
        }

        private string BuildHierarchyJson()
        {
            Dictionary<string, PrinterModelProfilesDto> models = new(StringComparer.Ordinal);
            int index = 0;
            foreach (KeyValuePair<string, List<MachineProfileDto>> group in _hierarchy)
            {
                models["model" + (++index).ToString(CultureInfo.InvariantCulture)] = new PrinterModelProfilesDto
                {
                    Name = group.Key,
                    ModelId = group.Key,
                    MachineProfiles = [.. group.Value],
                    FilamentProfiles = [],
                    ProcessProfiles = []
                };
            }

            return JsonSerializer.Serialize(new AllProfilesResponseDto
            {
                ByHierarchy = new Dictionary<string, ManufacturerProfilesDto>(StringComparer.Ordinal)
                {
                    [ManufacturerName] = new ManufacturerProfilesDto { Name = ManufacturerName, Models = models }
                },

                // The real worker emits the legacy flat collections alongside the hierarchy, and the
                // seed treats an entirely empty set of them as "worker has no profiles" and bails.
                MachineProfiles = new Dictionary<string, IList<MachineProfileDto>>(StringComparer.Ordinal)
                {
                    [ManufacturerName] = _hierarchy.Values.SelectMany(p => p).ToList()
                }
            });
        }

        private static HttpResponseMessage JsonResponse(string json)
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(send(request));
        }
    }
}
