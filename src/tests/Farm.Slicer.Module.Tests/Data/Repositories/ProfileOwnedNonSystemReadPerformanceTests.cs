using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Farm.Infrastructure.PrinterCalibration;
using Farm.Infrastructure.Repositories.UnitOfWork;
using Farm.Infrastructure.Services.Catalog;
using Farm.Infrastructure.Services.Gcode;
using Farm.Slicer.Module.Api.Hubs;
using Farm.Slicer.Module.Api.Services;
using Farm.Slicer.Module.Data;
using Farm.Slicer.Module.Data.Repositories;
using Farm.Slicer.Module.Domain;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;
using Xunit.Abstractions;

namespace Farm.Slicer.Module.Tests.Data.Repositories;

public sealed class ProfileOwnedNonSystemReadPerformanceTests : IDisposable
{
    private const int OwnedCountPerType = 10;
    private const int SampleCount = 9;

    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly QueryDiagnostics _diagnostics = new();
    private readonly SlicerDbContext _db;
    private readonly ITestOutputHelper _output;
    private readonly Guid _ownerId = Guid.NewGuid();
    private readonly Guid _otherUserId = Guid.NewGuid();
    private readonly EfProcessProfileRepository _processRepo;
    private readonly EfFilamentProfileRepository _filamentRepo;
    private readonly EfMachineProfileRepository _machineRepo;

    public ProfileOwnedNonSystemReadPerformanceTests(ITestOutputHelper output)
    {
        _output = output;
        _connection.Open();
        DbContextOptions<SlicerDbContext> options = new DbContextOptionsBuilder<SlicerDbContext>()
            .UseSqlite(_connection)
            .AddInterceptors(_diagnostics)
            .Options;

        _db = new SlicerDbContext(options);
        _db.Database.EnsureCreated();
        _processRepo = new EfProcessProfileRepository(_db);
        _filamentRepo = new EfFilamentProfileRepository(_db);
        _machineRepo = new EfMachineProfileRepository(_db);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task ListCustomProfilesAsync_ReportsSyntheticLegacyAndOwnedReadMeasurements()
    {
        foreach (int publicCount in new[] { 0, 1_000, 10_000 })
        {
            await SeedAsync(publicCount);
            ProfilesService currentService = CreateService(_processRepo, _filamentRepo, _machineRepo);
            ProfilesService legacyService = CreateService(
                LegacyReadProxy.For<IProcessProfileRepository>(_processRepo),
                LegacyReadProxy.For<IFilamentProfileRepository>(_filamentRepo),
                LegacyReadProxy.For<IMachineProfileRepository>(_machineRepo));

            CustomProfilesListResponseDto expected = await legacyService.ListCustomProfilesAsync(_ownerId, CancellationToken.None);
            CustomProfilesListResponseDto actual = await currentService.ListCustomProfilesAsync(_ownerId, CancellationToken.None);
            Assert.Equal(JsonSerializer.Serialize(expected), JsonSerializer.Serialize(actual));
            Assert.Equal(OwnedCountPerType * 3, actual.TotalCount);
            Assert.Equal(OwnedCountPerType, actual.ProcessProfileCount);
            Assert.Equal(OwnedCountPerType, actual.FilamentProfileCount);
            Assert.Equal(OwnedCountPerType, actual.MachineProfileCount);
            AssertOwnedResponseFields(actual);

            _diagnostics.Reset();
            _ = await legacyService.ListCustomProfilesAsync(_ownerId, CancellationToken.None);
            int legacyProcessMaterialized = _diagnostics.Materialized<ProcessProfile>();
            int legacyFilamentMaterialized = _diagnostics.Materialized<FilamentProfile>();
            int legacyMachineMaterialized = _diagnostics.Materialized<MachineProfile>();

            _diagnostics.Reset();
            _ = await currentService.ListCustomProfilesAsync(_ownerId, CancellationToken.None);
            int currentProcessMaterialized = _diagnostics.Materialized<ProcessProfile>();
            int currentFilamentMaterialized = _diagnostics.Materialized<FilamentProfile>();
            int currentMachineMaterialized = _diagnostics.Materialized<MachineProfile>();

            Assert.Equal(OwnedCountPerType + publicCount + 1, legacyProcessMaterialized);
            Assert.Equal(OwnedCountPerType + publicCount, legacyFilamentMaterialized);
            Assert.Equal(OwnedCountPerType + publicCount, legacyMachineMaterialized);
            Assert.Equal(OwnedCountPerType, currentProcessMaterialized);
            Assert.Equal(OwnedCountPerType, currentFilamentMaterialized);
            Assert.Equal(OwnedCountPerType, currentMachineMaterialized);

            ServiceSamples legacySamples = await MeasureServiceAsync(legacyService);
            ServiceSamples currentSamples = await MeasureServiceAsync(currentService);
            _output.WriteLine(
                "Synthetic DB-backed service proxy (ListCustomProfilesAsync; excludes HTTP/auth/serialization), " +
                "other-user public rows per type={0}; owned per type={1}; " +
                "materialized legacy P/F/M={2}/{3}/{4}, owned-read P/F/M={5}/{6}/{7}; " +
                "p50/p95 ms legacy={8:F3}/{9:F3}, owned-read={10:F3}/{11:F3}; " +
                "mean allocated bytes/call legacy={12}, owned-read={13}",
                publicCount,
                OwnedCountPerType,
                legacyProcessMaterialized,
                legacyFilamentMaterialized,
                legacyMachineMaterialized,
                currentProcessMaterialized,
                currentFilamentMaterialized,
                currentMachineMaterialized,
                legacySamples.P50Milliseconds,
                legacySamples.P95Milliseconds,
                currentSamples.P50Milliseconds,
                currentSamples.P95Milliseconds,
                legacySamples.MeanAllocatedBytes,
                currentSamples.MeanAllocatedBytes);

            await ClearProfilesAsync();
        }
    }

    private async Task SeedAsync(int publicCount)
    {
        DateTime now = DateTime.UtcNow;
        List<ProcessProfile> processes = [];
        List<FilamentProfile> filaments = [];
        List<MachineProfile> machines = [];

        for (int i = 0; i < OwnedCountPerType; i++)
        {
            processes.Add(new ProcessProfile
            {
                Id = Guid.NewGuid(),
                Name = $"Owned Process {i:D2}",
                SlicerType = i == 0 ? SlicerType.PrusaSlicer : SlicerType.OrcaSlicer,
                CreatedByUserId = _ownerId,
                IsPublic = false,
                RawJson = Json("owned-process", i),
                SettingsJson = Json("owned-settings", i),
                CompatiblePrinters = "Synthetic Printer",
                CreatedAt = now,
                UpdatedAt = now
            });
            filaments.Add(new FilamentProfile
            {
                Id = Guid.NewGuid(),
                Name = $"Owned Filament {i:D2}",
                Material = $"Material {i:D2}",
                SlicerType = SlicerType.OrcaSlicer,
                CreatedByUserId = _ownerId,
                IsPublic = false,
                RawJson = Json("owned-filament", i),
                SettingsJson = Json("owned-settings", i),
                CompatiblePrinters = "Synthetic Printer",
                CreatedAt = now,
                UpdatedAt = now
            });
            machines.Add(new MachineProfile
            {
                Id = Guid.NewGuid(),
                Name = $"Owned Machine {i:D2}",
                Manufacturer = $"Manufacturer {i:D2}",
                SlicerType = SlicerType.OrcaSlicer,
                CreatedByUserId = _ownerId,
                IsPublic = false,
                RawJson = Json("owned-machine", i),
                SettingsJson = Json("owned-settings", i),
                CreatedAt = now,
                UpdatedAt = now
            });
        }

        processes.Add(new ProcessProfile
        {
            Id = Guid.NewGuid(),
            Name = "Owned System Process",
            SlicerType = SlicerType.OrcaSlicer,
            CreatedByUserId = _ownerId,
            IsSystem = true,
            IsPublic = false,
            RawJson = Json("owned-system-process", 0),
            CreatedAt = now,
            UpdatedAt = now
        });

        for (int i = 0; i < publicCount; i++)
        {
            processes.Add(new ProcessProfile
            {
                Id = Guid.NewGuid(),
                Name = $"Other Public Process {i:D5}",
                SlicerType = SlicerType.OrcaSlicer,
                CreatedByUserId = _otherUserId,
                IsPublic = true,
                RawJson = Json("other-public-process", i),
                SettingsJson = Json("other-public-settings", i),
                CreatedAt = now,
                UpdatedAt = now
            });
            filaments.Add(new FilamentProfile
            {
                Id = Guid.NewGuid(),
                Name = $"Other Public Filament {i:D5}",
                Material = $"Other Material {i:D5}",
                SlicerType = SlicerType.OrcaSlicer,
                CreatedByUserId = _otherUserId,
                IsPublic = true,
                RawJson = Json("other-public-filament", i),
                SettingsJson = Json("other-public-settings", i),
                CreatedAt = now,
                UpdatedAt = now
            });
            machines.Add(new MachineProfile
            {
                Id = Guid.NewGuid(),
                Name = $"Other Public Machine {i:D5}",
                Manufacturer = $"Other Manufacturer {i:D5}",
                SlicerType = SlicerType.OrcaSlicer,
                CreatedByUserId = _otherUserId,
                IsPublic = true,
                RawJson = Json("other-public-machine", i),
                SettingsJson = Json("other-public-settings", i),
                CreatedAt = now,
                UpdatedAt = now
            });
        }

        processes.Add(new ProcessProfile
        {
            Id = Guid.NewGuid(),
            Name = "Other Private Process",
            SlicerType = SlicerType.OrcaSlicer,
            CreatedByUserId = _otherUserId,
            IsPublic = false,
            RawJson = Json("other-private-process", 0),
            CreatedAt = now,
            UpdatedAt = now
        });
        filaments.Add(new FilamentProfile
        {
            Id = Guid.NewGuid(),
            Name = "Other Private Filament",
            Material = "Private Material",
            SlicerType = SlicerType.OrcaSlicer,
            CreatedByUserId = _otherUserId,
            IsPublic = false,
            RawJson = Json("other-private-filament", 0),
            CreatedAt = now,
            UpdatedAt = now
        });
        machines.Add(new MachineProfile
        {
            Id = Guid.NewGuid(),
            Name = "Other Private Machine",
            Manufacturer = "Private Manufacturer",
            SlicerType = SlicerType.OrcaSlicer,
            CreatedByUserId = _otherUserId,
            IsPublic = false,
            RawJson = Json("other-private-machine", 0),
            CreatedAt = now,
            UpdatedAt = now
        });

        _db.ProcessProfiles.AddRange(processes);
        _db.FilamentProfiles.AddRange(filaments);
        _db.MachineProfiles.AddRange(machines);
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
        _diagnostics.Reset();
    }

    private static void AssertOwnedResponseFields(CustomProfilesListResponseDto response)
    {
        CustomProfileDto[] profiles = response.Profiles.ToArray();
        Assert.Equal("process", profiles[0].ProfileType);
        Assert.Equal(Json("owned-process", 0), profiles[0].RawJson);
        Assert.Equal(new[] { "Synthetic Printer" }, profiles[0].CompatiblePrinters);
        Assert.Equal("filament", profiles[OwnedCountPerType].ProfileType);
        Assert.Equal(Json("owned-filament", 0), profiles[OwnedCountPerType].RawJson);
        Assert.Equal(new[] { "Synthetic Printer" }, profiles[OwnedCountPerType].CompatiblePrinters);
        Assert.Equal("machine", profiles[OwnedCountPerType * 2].ProfileType);
        Assert.Equal(Json("owned-machine", 0), profiles[OwnedCountPerType * 2].RawJson);
        Assert.Null(profiles[OwnedCountPerType * 2].CompatiblePrinters);
    }

    private async Task<ServiceSamples> MeasureServiceAsync(ProfilesService service)
    {
        _ = await service.ListCustomProfilesAsync(_ownerId, CancellationToken.None);
        double[] durations = new double[SampleCount];
        long allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);

        for (int i = 0; i < SampleCount; i++)
        {
            Stopwatch stopwatch = Stopwatch.StartNew();
            _ = await service.ListCustomProfilesAsync(_ownerId, CancellationToken.None);
            stopwatch.Stop();
            durations[i] = stopwatch.Elapsed.TotalMilliseconds;
        }

        long allocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
        Array.Sort(durations);
        int p95Index = (int)Math.Ceiling(0.95 * SampleCount) - 1;
        return new ServiceSamples(
            durations[SampleCount / 2],
            durations[p95Index],
            allocatedBytes / SampleCount);
    }

    private async Task ClearProfilesAsync()
    {
        await _db.ProcessProfiles.ExecuteDeleteAsync();
        await _db.FilamentProfiles.ExecuteDeleteAsync();
        await _db.MachineProfiles.ExecuteDeleteAsync();
        _db.ChangeTracker.Clear();
        _diagnostics.Reset();
    }

    private static ProfilesService CreateService(
        IProcessProfileRepository processRepo,
        IFilamentProfileRepository filamentRepo,
        IMachineProfileRepository machineRepo)
    {
        Mock<IProfilesRepository> repo = new(MockBehavior.Loose);
        Mock<IUnitOfWork> unitOfWork = new(MockBehavior.Loose);
        Mock<ICatalogService> catalogService = new(MockBehavior.Loose);
        Mock<IProfileParsingService> parsingService = new(MockBehavior.Loose);
        Mock<IHubContext<SlicerHub>> hubContext = new(MockBehavior.Loose);
        Mock<Farm.Slicer.Module.Services.ISlicersService> slicersService = new(MockBehavior.Loose);
        Mock<IPrinterModelAliasService> aliasService = new(MockBehavior.Loose);
        ILogger<ProfilesService> logger = NullLogger<ProfilesService>.Instance;

        return new ProfilesService(
            repo.Object,
            logger,
            processRepo,
            machineRepo,
            filamentRepo,
            unitOfWork.Object,
            catalogService.Object,
            parsingService.Object,
            hubContext.Object,
            slicersService.Object,
            aliasService.Object);
    }

    private static string Json(string label, int index) =>
        JsonSerializer.Serialize(new { label, index, syntheticPayload = new string('x', 192) });

    private sealed record ServiceSamples(double P50Milliseconds, double P95Milliseconds, long MeanAllocatedBytes);

    private sealed class QueryDiagnostics : IMaterializationInterceptor
    {
        private readonly Dictionary<Type, int> _materialized = [];

        public int Materialized<TProfile>() => _materialized.GetValueOrDefault(typeof(TProfile));

        public void Reset() => _materialized.Clear();

        public object InitializedInstance(MaterializationInterceptionData materializationData, object entity)
        {
            _materialized[entity.GetType()] = Materialized(entity.GetType()) + 1;
            return entity;
        }

        private int Materialized(Type type) => _materialized.GetValueOrDefault(type);
    }

    public class LegacyReadProxy : DispatchProxy
    {
        public object? Target { get; set; }

        public static TRepository For<TRepository>(TRepository target)
            where TRepository : class
        {
            TRepository proxy = Create<TRepository, LegacyReadProxy>();
            ((LegacyReadProxy)(object)proxy).Target = target;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod is null || Target is null || args is null)
            {
                throw new InvalidOperationException("The legacy read proxy was not initialized.");
            }

            if (targetMethod.Name == "GetOwnedNonSystemAsync")
            {
                return Target switch
                {
                    IProcessProfileRepository processRepo =>
                        processRepo.GetByUserAsync((Guid)args[0]!, (CancellationToken)args[1]!),
                    IFilamentProfileRepository filamentRepo =>
                        filamentRepo.GetByEngineAsync(
                            (SlicerType)args[0]!,
                            includeSystem: false,
                            userId: (Guid)args[1]!,
                            ct: (CancellationToken)args[2]!),
                    IMachineProfileRepository machineRepo =>
                        machineRepo.GetByEngineAsync(
                            (SlicerType)args[0]!,
                            includeSystem: false,
                            userId: (Guid)args[1]!,
                            ct: (CancellationToken)args[2]!),
                    _ => throw new InvalidOperationException($"Unexpected repository type {Target.GetType().Name}.")
                };
            }

            return targetMethod.Invoke(Target, args);
        }
    }
}
