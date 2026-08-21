using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
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
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Farm.Slicer.Module.Tests.Services;

/// <summary>
/// Regression tests for #1779 that run against REAL SQLite-backed repositories rather than mocks,
/// exercising <see cref="ProfilesService.SeedSystemProfilesFromWorkerAsync"/> — the catalog-wide
/// seed that actually populated the deployed database (175 machine profiles across 11 manufacturers,
/// far more than the per-printer import path produces).
/// </summary>
/// <remarks>
/// <para>
/// Mocked repositories cannot catch the failure mode that matters here. <c>MachineProfile</c> carries
/// a UNIQUE index on <c>(Name, SlicerType)</c> and another on <c>Hash</c>; a mock happily accepts a
/// duplicate insert that a real database rejects. Because the seed de-duplicated purely on a SHA256
/// of the serialized worker DTO, and <c>MachineProfileDto</c> gained <c>IsHighFlowNozzle</c> in #1806
/// after the deployment was seeded, every stored hash is now stale. Re-running the seed therefore
/// treated all existing profiles as new, and the resulting insert threw instead of quietly
/// duplicating — which could abort the very HF inserts this issue is about.
/// </para>
/// <para>
/// These tests use a real <c>SlicerDbContext</c> so those constraints are actually enforced.
/// </para>
/// </remarks>
public class ProfilesServiceRealRepositorySeedTests
{
    private const string ManufacturerName = "Prusa";
    private const string BaseModelName = "Prusa CORE One";
    private const string HfModelName = "Prusa CORE One HF";
    private const string BaseLModelName = "Prusa CORE One L";
    private const string HfLModelName = "Prusa CORE One L HF";

    /// <summary>
    /// The production scenario: a database seeded before the alias fix holds the standard profiles
    /// with hashes computed from an older DTO shape, and is missing every HF profile. Re-seeding must
    /// insert exactly the missing HF rows, must not duplicate or fail on the existing ones, and the
    /// result must be visible through <c>/api/slicer/profiles/extended</c>.
    /// </summary>
    [Fact]
    public async Task SeedSystemProfiles_ExistingRowsHaveStaleHashesAndHfMissing_BackfillsHfWithoutDuplicating()
    {
        using SlicerDbContext db = TestInfrastructure.TestHelpers.CreateSqliteInMemoryDb();
        Harness harness = new(db);

        // Pre-existing standard profiles, exactly as an older release would have left them: correct
        // names, but hashes that today's serialization can no longer reproduce.
        foreach (string name in new[] { "Prusa CORE One 0.4 nozzle", "Prusa CORE One L 0.4 nozzle" })
        {
            await harness.MachineRepo.AddAsync(new MachineProfile
            {
                Id = Guid.NewGuid(),
                Name = name,
                Manufacturer = ManufacturerName,
                SlicerType = SlicerType.OrcaSlicer,
                IsSystem = true,
                IsPublic = true,
                Hash = "stale-hash-" + name,
                RawJson = "{}",
                SettingsJson = "{\"NozzleDiameter\": 0.4, \"PrinterVariant\": \"0.4\"}",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            });
        }

        ProfilesService svc = harness.CreateService();

        _ = await svc.SeedSystemProfilesFromWorkerAsync(harness.CreateWorkerHttpClient(), CancellationToken.None);

        List<MachineProfile> persisted = (await harness.MachineRepo.GetByEngineAsync(SlicerType.OrcaSlicer, true, null, CancellationToken.None)).ToList();

        // All four variants present: two pre-existing standard, two newly backfilled HF.
        Assert.Equal(4, persisted.Count);
        Assert.Single(persisted, p => p.Name == "Prusa CORE One 0.4 nozzle");
        Assert.Single(persisted, p => p.Name == "Prusa CORE One L 0.4 nozzle");
        Assert.Single(persisted, p => p.Name == "Prusa CORE One HF 0.4 nozzle");
        Assert.Single(persisted, p => p.Name == "Prusa CORE One L HF 0.4 nozzle");

        // No name was inserted twice — the real UNIQUE (Name, SlicerType) index would have thrown.
        Assert.Equal(persisted.Count, persisted.Select(p => p.Name).Distinct(StringComparer.Ordinal).Count());

        // And the whole point of the issue: extended now surfaces the HF rows.
        ExtendedProfilesResponseDto extended = await svc.ListExtendedAsync(CancellationToken.None);
        Assert.Equal(2, extended.MachineProfiles.Count(p => Regex.IsMatch(p.Name, @"\bHF\b")));
    }

    /// <summary>
    /// Seeding twice in a row against a real database must be a no-op the second time. This is the
    /// property that makes the seed safe to re-run as the production remedy.
    /// </summary>
    [Fact]
    public async Task SeedSystemProfiles_RunTwice_IsIdempotentAgainstRealUniqueConstraints()
    {
        using SlicerDbContext db = TestInfrastructure.TestHelpers.CreateSqliteInMemoryDb();
        Harness harness = new(db);
        ProfilesService svc = harness.CreateService();

        _ = await svc.SeedSystemProfilesFromWorkerAsync(harness.CreateWorkerHttpClient(), CancellationToken.None);
        int afterFirst = (await harness.MachineRepo.GetByEngineAsync(SlicerType.OrcaSlicer, true, null, CancellationToken.None)).Count;

        _ = await svc.SeedSystemProfilesFromWorkerAsync(harness.CreateWorkerHttpClient(), CancellationToken.None);
        List<MachineProfile> afterSecond = (await harness.MachineRepo.GetByEngineAsync(SlicerType.OrcaSlicer, true, null, CancellationToken.None)).ToList();

        Assert.Equal(4, afterFirst);
        Assert.Equal(afterFirst, afterSecond.Count);
        Assert.Equal(afterSecond.Count, afterSecond.Select(p => p.Name).Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>
    /// Covers BOTH affected models from the issue — CORE One and CORE One L — across all four HF
    /// nozzle sizes, i.e. the full set of 8 rows reported missing from <c>extended</c>.
    /// </summary>
    [Fact]
    public async Task SeedSystemProfiles_AllFourNozzleSizesForBothModels_YieldsEightHfProfiles()
    {
        using SlicerDbContext db = TestInfrastructure.TestHelpers.CreateSqliteInMemoryDb();
        Harness harness = new(db, allNozzleSizes: true);
        ProfilesService svc = harness.CreateService();

        _ = await svc.SeedSystemProfilesFromWorkerAsync(harness.CreateWorkerHttpClient(), CancellationToken.None);

        ExtendedProfilesResponseDto extended = await svc.ListExtendedAsync(CancellationToken.None);
        List<string> hfNames = extended.MachineProfiles
            .Where(p => Regex.IsMatch(p.Name, @"\bHF\b"))
            .Select(p => p.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(8, hfNames.Count);
        foreach (string nozzle in new[] { "0.4", "0.5", "0.6", "0.8" })
        {
            Assert.Contains($"Prusa CORE One HF {nozzle} nozzle", hfNames);
            Assert.Contains($"Prusa CORE One L HF {nozzle} nozzle", hfNames);
        }
    }

    /// <summary>
    /// A filament profile compatible with several models appears under every one of their hierarchy
    /// groups. Staging it once per group would collide with the filament table's unique name index,
    /// so the seed must stage it only once.
    /// </summary>
    [Fact]
    public async Task SeedSystemProfiles_FilamentSharedAcrossHierarchyGroups_IsPersistedOnce()
    {
        using SlicerDbContext db = TestInfrastructure.TestHelpers.CreateSqliteInMemoryDb();
        Harness harness = new(db, sharedFilamentName: "Prusa Generic PLA @COREOne");
        ProfilesService svc = harness.CreateService();

        _ = await svc.SeedSystemProfilesFromWorkerAsync(harness.CreateWorkerHttpClient(), CancellationToken.None);

        List<FilamentProfile> filaments = (await harness.FilamentRepo.GetByEngineAsync(SlicerType.OrcaSlicer, true, null, CancellationToken.None)).ToList();
        Assert.Single(filaments, f => f.Name == "Prusa Generic PLA @COREOne");
    }

    /// <summary>
    /// Process profiles are unique on <c>(Name, SlicerType, PrinterModelId)</c>, so a row already
    /// bound to a printer model does not block a bundle profile of the same name that carries no
    /// model binding. A name-only identity guard would wrongly treat the existing row as occupying
    /// the name and silently skip the bundle profile.
    /// </summary>
    [Fact]
    public async Task SeedSystemProfiles_ProcessNameAlreadyUsedUnderAnotherModelBinding_StillImportsBundleProfile()
    {
        using SlicerDbContext db = TestInfrastructure.TestHelpers.CreateSqliteInMemoryDb();
        Harness harness = new(db, processProfileName: "0.20mm Standard");

        // Same name, but bound to a printer model — a distinct row under the real unique index.
        await harness.ProcessRepo.AddAsync(new ProcessProfile
        {
            Id = Guid.NewGuid(),
            Name = "0.20mm Standard",
            SlicerType = SlicerType.OrcaSlicer,
            PrinterModelId = harness.BaseModelId,
            IsSystem = true,
            Hash = "pre-existing-bound-process",
            RawJson = "{}"
        });

        ProfilesService svc = harness.CreateService();
        _ = await svc.SeedSystemProfilesFromWorkerAsync(harness.CreateWorkerHttpClient(), CancellationToken.None);

        List<ProcessProfile> processes = (await harness.ProcessRepo.GetByEngineAsync(SlicerType.OrcaSlicer, true, null, CancellationToken.None)).ToList();

        Assert.Equal(2, processes.Count(p => p.Name == "0.20mm Standard"));
        Assert.Single(processes, p => p.Name == "0.20mm Standard" && p.PrinterModelId == harness.BaseModelId);
        Assert.Single(processes, p => p.Name == "0.20mm Standard" && p.PrinterModelId == null);
    }

    /// <summary>
    /// Filament profiles are unique on <c>(Name, Material, SlicerType)</c>. When the bundle offers
    /// two filaments sharing a name but differing in material, both are legal rows and both must be
    /// imported; a name-only identity guard would discard one.
    /// </summary>
    [Fact]
    public async Task SeedSystemProfiles_BundleHasSameFilamentNameInTwoMaterials_ImportsBoth()
    {
        using SlicerDbContext db = TestInfrastructure.TestHelpers.CreateSqliteInMemoryDb();
        Harness harness = new(db, filamentMaterials: new[] { "PLA", "PETG" }, sharedFilamentName: "Prusa Generic");
        ProfilesService svc = harness.CreateService();

        _ = await svc.SeedSystemProfilesFromWorkerAsync(harness.CreateWorkerHttpClient(), CancellationToken.None);

        List<FilamentProfile> filaments = (await harness.FilamentRepo.GetByEngineAsync(SlicerType.OrcaSlicer, true, null, CancellationToken.None)).ToList();

        Assert.Equal(2, filaments.Count(f => f.Name == "Prusa Generic"));
        Assert.Single(filaments, f => f.Name == "Prusa Generic" && f.Material == "PLA");
        Assert.Single(filaments, f => f.Name == "Prusa Generic" && f.Material == "PETG");
    }

    /// <summary>
    /// The UNIQUE indexes are global, not scoped to system rows. A user-created profile occupying a
    /// name the bundle also uses must therefore be treated as occupied: the seed must skip it rather
    /// than collide, and must still import every other profile — notably the HF rows.
    /// </summary>
    [Fact]
    public async Task SeedSystemProfiles_UserProfileOccupiesABundleName_SkipsItAndStillImportsHf()
    {
        using SlicerDbContext db = TestInfrastructure.TestHelpers.CreateSqliteInMemoryDb();
        Harness harness = new(db);

        await harness.MachineRepo.AddAsync(new MachineProfile
        {
            Id = Guid.NewGuid(),
            Name = "Prusa CORE One 0.4 nozzle",
            Manufacturer = ManufacturerName,
            SlicerType = SlicerType.OrcaSlicer,
            IsSystem = false,
            CreatedByUserId = Guid.NewGuid(),
            Hash = "user-created-hash",
            RawJson = "{}"
        });

        ProfilesService svc = harness.CreateService();
        _ = await svc.SeedSystemProfilesFromWorkerAsync(harness.CreateWorkerHttpClient(), CancellationToken.None);

        List<MachineProfile> persisted = (await harness.MachineRepo.GetByEngineAsync(SlicerType.OrcaSlicer, true, null, CancellationToken.None)).ToList();

        Assert.Single(persisted, p => p.Name == "Prusa CORE One 0.4 nozzle");
        Assert.False(Assert.Single(persisted, p => p.Name == "Prusa CORE One 0.4 nozzle").IsSystem);
        Assert.Contains(persisted, p => p.Name == "Prusa CORE One HF 0.4 nozzle");
        Assert.Contains(persisted, p => p.Name == "Prusa CORE One L HF 0.4 nozzle");
    }

    /// <summary>
    /// Proves the identity guard survives the exact churn that broke the hash guard. The seed runs
    /// twice, but the second run computes completely different content hashes — simulating a DTO
    /// shape change between releases, as when <c>MachineProfileDto</c> gained <c>IsHighFlowNozzle</c>
    /// in #1806. Row counts must be identical: the index-keyed identity, unlike the hash, is stable.
    /// </summary>
    [Fact]
    public async Task SeedSystemProfiles_SecondRunComputesDifferentHashes_StillIdempotent()
    {
        using SlicerDbContext db = TestInfrastructure.TestHelpers.CreateSqliteInMemoryDb();
        Harness harness = new(db);

        _ = await harness.CreateService().SeedSystemProfilesFromWorkerAsync(harness.CreateWorkerHttpClient(), CancellationToken.None);
        List<MachineProfile> afterFirst = (await harness.MachineRepo.GetByEngineAsync(SlicerType.OrcaSlicer, true, null, CancellationToken.None)).ToList();

        // Every hash the second run produces differs from the first run's, as a DTO shape change would.
        harness.HashSalt = "post-dto-change";
        _ = await harness.CreateService().SeedSystemProfilesFromWorkerAsync(harness.CreateWorkerHttpClient(), CancellationToken.None);
        List<MachineProfile> afterSecond = (await harness.MachineRepo.GetByEngineAsync(SlicerType.OrcaSlicer, true, null, CancellationToken.None)).ToList();

        Assert.Equal(4, afterFirst.Count);
        Assert.Equal(afterFirst.Count, afterSecond.Count);
        Assert.Equal(afterSecond.Count, afterSecond.Select(p => p.Name).Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>
    /// The state a losing instance sees in a multi-replica rolling deploy: another reconciler has
    /// already written every row, under hashes this instance cannot reproduce. Reconciliation must
    /// then be a clean no-op — no duplicate rows, and no exception escaping to the caller. Combined
    /// with the repositories detaching failed inserts, this is what keeps a concurrent start from
    /// recreating the batch-poisoning failure mode.
    /// </summary>
    [Fact]
    public async Task SeedSystemProfiles_AnotherInstanceAlreadyWroteEveryRow_IsNoOpAndDoesNotThrow()
    {
        using SlicerDbContext db = TestInfrastructure.TestHelpers.CreateSqliteInMemoryDb();
        Harness harness = new(db);

        // Simulate the winner's writes, with hashes this instance will never compute.
        foreach (string name in new[]
        {
            "Prusa CORE One 0.4 nozzle",
            "Prusa CORE One HF 0.4 nozzle",
            "Prusa CORE One L 0.4 nozzle",
            "Prusa CORE One L HF 0.4 nozzle"
        })
        {
            await harness.MachineRepo.AddAsync(new MachineProfile
            {
                Id = Guid.NewGuid(),
                Name = name,
                Manufacturer = ManufacturerName,
                SlicerType = SlicerType.OrcaSlicer,
                IsSystem = true,
                Hash = "winner-instance-hash-" + name,
                RawJson = "{}",
                SettingsJson = "{\"NozzleDiameter\": 0.4, \"PrinterVariant\": \"0.4\"}"
            });
        }

        ProfilesService svc = harness.CreateService();
        _ = await svc.SeedSystemProfilesFromWorkerAsync(harness.CreateWorkerHttpClient(), CancellationToken.None);

        List<MachineProfile> persisted = (await harness.MachineRepo.GetByEngineAsync(SlicerType.OrcaSlicer, true, null, CancellationToken.None)).ToList();
        Assert.Equal(4, persisted.Count);
        Assert.Equal(4, persisted.Select(p => p.Name).Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>
    /// The identity guard is load-bearing for process profiles, not merely an optimisation. This
    /// seed writes them with a null <c>PrinterModelId</c>, and because the unique index includes
    /// that column and SQL treats NULLs as distinct, the database will happily accept the same
    /// process profile once per hierarchy group it appears under. Only the guard prevents that, so
    /// removing it silently multiplies process profiles across the catalog.
    /// </summary>
    [Fact]
    public async Task SeedSystemProfiles_ProcessProfileSharedAcrossHierarchyGroups_IsPersistedOnce()
    {
        using SlicerDbContext db = TestInfrastructure.TestHelpers.CreateSqliteInMemoryDb();

        // The same process profile is offered under all four model groups, as the worker does for a
        // profile whose compatible_printers spans them.
        Harness harness = new(db, processProfileName: "0.20mm Standard");
        ProfilesService svc = harness.CreateService();

        _ = await svc.SeedSystemProfilesFromWorkerAsync(harness.CreateWorkerHttpClient(), CancellationToken.None);

        List<ProcessProfile> processes = (await harness.ProcessRepo.GetByEngineAsync(SlicerType.OrcaSlicer, true, null, CancellationToken.None)).ToList();
        Assert.Single(processes, p => p.Name == "0.20mm Standard");
    }

    private sealed class Harness
    {
        private readonly Dictionary<string, List<MachineProfileDto>> _hierarchy = new(StringComparer.Ordinal);
        private readonly string? _sharedFilamentName;
        private readonly string[] _filamentMaterials;
        private readonly string? _processProfileName;

        public Harness(
            SlicerDbContext db,
            bool allNozzleSizes = false,
            string? sharedFilamentName = null,
            string[]? filamentMaterials = null,
            string? processProfileName = null)
        {
            _sharedFilamentName = sharedFilamentName;
            _filamentMaterials = filamentMaterials ?? new[] { "PLA" };
            _processProfileName = processProfileName;
            MachineRepo = new EfMachineProfileRepository(db);
            FilamentRepo = new EfFilamentProfileRepository(db);
            ProcessRepo = new EfProcessProfileRepository(db);
            BaseModelId = Guid.NewGuid();
            BaseLModelId = Guid.NewGuid();

            string[] nozzles = allNozzleSizes ? new[] { "0.4", "0.5", "0.6", "0.8" } : new[] { "0.4" };
            foreach (string nozzle in nozzles)
            {
                AddGroup(BaseModelName, $"Prusa CORE One {nozzle} nozzle", nozzle);
                AddGroup(HfModelName, $"Prusa CORE One HF {nozzle} nozzle", nozzle);
                AddGroup(BaseLModelName, $"Prusa CORE One L {nozzle} nozzle", nozzle);
                AddGroup(HfLModelName, $"Prusa CORE One L HF {nozzle} nozzle", nozzle);
            }
        }

        public EfMachineProfileRepository MachineRepo { get; }

        public EfFilamentProfileRepository FilamentRepo { get; }

        public EfProcessProfileRepository ProcessRepo { get; }

        public Guid BaseModelId { get; }

        public Guid BaseLModelId { get; }

        /// <summary>
        /// Changes every content hash the parsing mock produces, simulating a worker-DTO shape
        /// change between releases (the churn that invalidated the old hash-based guard).
        /// </summary>
        public string HashSalt { get; set; } = string.Empty;

        public ProfilesService CreateService()
        {
            Mock<ICatalogService> catalog = new(MockBehavior.Loose);
            _ = catalog.Setup(c => c.GetManufacturersAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync((new List<ManufacturerDto> { new(Guid.NewGuid(), ManufacturerName) }, (string?)null));
            _ = catalog.Setup(c => c.GetModelsAsync(null, It.IsAny<CancellationToken>()))
                .ReturnsAsync((new List<PrinterModelDto>
                {
                    new(BaseModelId, BaseModelName, Guid.NewGuid()),
                    new(BaseLModelId, BaseLModelName, Guid.NewGuid())
                }, (string?)null));

            // HF group names exist ONLY as configured OrcaSlicer aliases, mirroring printer-models.yaml.
            _ = catalog.Setup(c => c.GetModelAliasesAsync(BaseModelId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new List<SlicerModelAliasDto>
                {
                    new(Guid.NewGuid(), BaseModelId, HfModelName, "OrcaSlicer"),

                    // A PrusaSlicer alias must not widen the OrcaSlicer hierarchy match.
                    new(Guid.NewGuid(), BaseModelId, "COREONE", "PrusaSlicer")
                });
            _ = catalog.Setup(c => c.GetModelAliasesAsync(BaseLModelId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new List<SlicerModelAliasDto>
                {
                    new(Guid.NewGuid(), BaseLModelId, HfLModelName, "OrcaSlicer")
                });

            Mock<Farm.Slicer.Module.Services.ISlicersService> slicers = new(MockBehavior.Loose);
            _ = slicers.Setup(s => s.ListAsync(It.IsAny<CancellationToken>()))
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

            // Preserve the serialized JSON so ListExtendedAsync can recover NozzleDiameter/PrinterVariant,
            // and derive a hash the same way a real parse would: deterministically from the content.
            Mock<IProfileParsingService> parsing = new(MockBehavior.Loose);
            _ = parsing.Setup(p => p.ParseAndPrepare(It.IsAny<string>()))
                .Returns((string json) => (json, json, Convert.ToHexString(
                    System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(HashSalt + json)))));

            return new ProfilesService(
                new Mock<IProfilesRepository>(MockBehavior.Loose).Object,
                NullLogger<ProfilesService>.Instance,
                ProcessRepo,
                MachineRepo,
                FilamentRepo,
                new Mock<IUnitOfWork>(MockBehavior.Loose).Object,
                catalog.Object,
                parsing.Object,
                CreateHubContext(),
                slicers.Object,
                new Mock<IPrinterModelAliasService>(MockBehavior.Loose).Object);
        }

        public HttpClient CreateWorkerHttpClient()
        {
            return new HttpClient(new StubHttpMessageHandler(request =>
            {
                if (request.RequestUri!.AbsolutePath.EndsWith("/version", StringComparison.Ordinal))
                {
                    return new HttpResponseMessage(HttpStatusCode.NotFound);
                }

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(BuildWorkerJson(), Encoding.UTF8, "application/json")
                };
            }));
        }

        private static IHubContext<SlicerHub> CreateHubContext()
        {
            Mock<IClientProxy> proxy = new();
            _ = proxy.Setup(p => p.SendCoreAsync(It.IsAny<string>(), It.IsAny<object[]>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);
            Mock<IHubClients> clients = new();
            _ = clients.Setup(c => c.Group(It.IsAny<string>())).Returns(proxy.Object);
            Mock<IHubContext<SlicerHub>> hub = new();
            _ = hub.SetupGet(h => h.Clients).Returns(clients.Object);
            return hub.Object;
        }

        private void AddGroup(string printerModel, string profileName, string nozzle)
        {
            if (!_hierarchy.TryGetValue(printerModel, out List<MachineProfileDto>? list))
            {
                list = new List<MachineProfileDto>();
                _hierarchy[printerModel] = list;
            }

            list.Add(new MachineProfileDto
            {
                Name = profileName,
                Manufacturer = ManufacturerName,
                PrinterModel = printerModel,
                NozzleDiameter = double.Parse(nozzle, CultureInfo.InvariantCulture),
                PrinterVariant = nozzle,
                Instantiation = true
            });
        }

        /// <summary>
        /// Mirrors the real worker: <c>ByHierarchy</c> keyed by <c>printer_model</c>, camelCase on the
        /// wire, and the legacy flat collections emitted alongside the hierarchy.
        /// </summary>
        private string BuildWorkerJson()
        {
            Dictionary<string, PrinterModelProfilesDto> models = new(StringComparer.Ordinal);
            foreach (KeyValuePair<string, List<MachineProfileDto>> group in _hierarchy)
            {
                models[group.Key] = new PrinterModelProfilesDto
                {
                    Name = group.Key,
                    ModelId = group.Key,
                    MachineProfiles = [.. group.Value],
                    FilamentProfiles = _sharedFilamentName is null
                        ? []
                        : [.. _filamentMaterials.Select(m => new FilamentProfileDto
                        {
                            Name = _sharedFilamentName,
                            Material = m,
                            Instantiation = true
                        })],
                    ProcessProfiles = _processProfileName is null
                        ? []
                        : [new ProcessProfileDto { Name = _processProfileName, Quality = "standard", LayerHeight = 0.2, Instantiation = true }]
                };
            }

            AllProfilesResponseDto response = new()
            {
                ByHierarchy = new Dictionary<string, ManufacturerProfilesDto>(StringComparer.Ordinal)
                {
                    [ManufacturerName] = new ManufacturerProfilesDto { Name = ManufacturerName, Models = models }
                },
                MachineProfiles = new Dictionary<string, IList<MachineProfileDto>>(StringComparer.Ordinal)
                {
                    [ManufacturerName] = _hierarchy.Values.SelectMany(p => p).ToList()
                }
            };

            return JsonSerializer.Serialize(response, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });
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
