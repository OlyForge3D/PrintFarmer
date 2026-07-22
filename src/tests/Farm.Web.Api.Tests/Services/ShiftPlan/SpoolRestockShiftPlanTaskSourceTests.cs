using System.Text;
using System.Text.Json;
using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Interfaces;
using Farm.Infrastructure.Services.Mutations;
using Farm.Infrastructure.Services.OperatorFeatures;
using Farm.Infrastructure.Services.ShiftPlan;
using Farm.Infrastructure.Services.ShiftPlan.Sources;
using Farm.Infrastructure.Services.Spoolman;
using Farm.Infrastructure.Settings;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;
using TestDbHelpers = Farm.Web.Api.Tests.TestInfrastructure.TestHelpers;

namespace Farm.Web.Api.Tests.Services.ShiftPlan;

public sealed class SpoolRestockShiftPlanTaskSourceTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 22, 12, 0, 0, TimeSpan.Zero);

    private readonly Mock<ISpoolBurnRateProjectionService> _projections = new();
    private readonly Mock<IFilamentCoverageSpoolResolver> _resolver = new();
    private readonly Mock<ISpoolmanService> _spoolman = new();
    private readonly Mock<ISettingsService> _settingsService = new();
    private readonly Mock<IOperatorFeatureGate> _gate = new();
    private readonly MutableTimeProvider _clock = new(Now);
    private SqliteConnection _connection = null!;
    private ShiftPlanSettings _settings = null!;
    private Guid _manufacturerId;
    private Guid _modelId;

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        await _connection.OpenAsync();
        await using AppDbContext db = CreateContext();
        _ = await db.Database.EnsureCreatedAsync();
        _manufacturerId = Guid.NewGuid();
        _modelId = Guid.NewGuid();
        _ = db.Manufacturers.Add(new Manufacturer
        {
            Id = _manufacturerId,
            Name = "Test Manufacturer",
        });
        _ = db.PrinterModels.Add(new PrinterModel
        {
            Id = _modelId,
            ManufacturerId = _manufacturerId,
            Name = "Test Model",
        });
        _ = await db.SaveChangesAsync();

        _settings = new ShiftPlanSettings
        {
            SpoolReorderThresholdGrams = 250,
            SpoolBurnRateLookbackDays = 30,
            SpoolBurnRateMinimumSamples = 3,
            SpoolRestockLeadMinutes = 60,
        };
        _settingsService
            .Setup(service => service.Get<ShiftPlanSettings>())
            .Returns(() => _settings);
        _spoolman
            .Setup(service => service.GetConfig())
            .Returns(new SpoolmanConfigDto("http://central.local"));
        _gate
            .Setup(service => service.IsEnabledStrictAsync(
                OperatorFeature.ShiftPlan,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
    }

    public async Task DisposeAsync() => await _connection.DisposeAsync();

    [Fact]
    public async Task ProduceAsync_ReadyProjection_MapsStableTaskAndTypedMetadata()
    {
        await SeedPrinterAsync(PrinterBackend.PrusaLink, "http://printer.local", 42);
        CanonicalSpoolIdentity identity = Identity(
            SpoolSourceKind.Central,
            "http://central.local",
            42);
        DateTime crossingUtc = Now.AddHours(2).UtcDateTime;
        SetupProjection(Ready(identity, crossingUtc));
        SetupResolvedSpool(identity, "PLA Blue");
        await using AppDbContext db = CreateContext();
        var source = CreateSource(db, new ConstantWatermarkReader(17));

        ShiftPlanSourceResult result = await source.ProduceAsync(CancellationToken.None);

        ShiftPlanTaskSpec spec = Assert.Single(result.Specs);
        Assert.Equal(17, result.OriginWatermark);
        Assert.Equal(UserTaskType.SpoolRestock, spec.TaskType);
        Assert.Equal(UserTaskSourceKind.SpoolReorder, spec.SourceKind);
        Assert.Equal(UserTaskPriority.Normal, spec.Priority);
        Assert.Equal(UserTaskAnchorKind.At, spec.AnchorKind);
        Assert.Equal(Now.AddHours(1).UtcDateTime, spec.AnchorAtUtc);
        Assert.Equal(crossingUtc, spec.DueAt);
        Assert.Equal("Spool", spec.EntityType);
        Assert.StartsWith("spoolrestock:v1:42:", spec.SourceId, StringComparison.Ordinal);
        Assert.True(spec.SourceId.Length <= 128);

        ShiftPlanKindAuthority authority = Assert.Single(result.Authority!.Kinds);
        Assert.True(authority.IsAuthoritativeComplete);
        Assert.Empty(authority.PreservedSourceIds);
        Assert.Empty(authority.IncompleteReasons);

        using JsonDocument metadata = JsonDocument.Parse(spec.MetadataJson!);
        JsonElement root = metadata.RootElement;
        Assert.Equal("central", root.GetProperty("sourceKind").GetString());
        Assert.Equal("http://central.local", root.GetProperty("sourceIdentity").GetString());
        Assert.Equal(42, root.GetProperty("spoolId").GetInt32());
        Assert.Equal("PLA Blue", root.GetProperty("spoolName").GetString());
        Assert.Equal("ready", root.GetProperty("state").GetString());
        Assert.Equal(3, root.GetProperty("sampleCount").GetInt32());
        Assert.Equal(250, root.GetProperty("thresholdGrams").GetDouble());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(60)]
    public async Task ProduceAsync_ActionAtOrBeforeNow_UsesNowWithoutMovingDueAt(int leadMinutes)
    {
        _settings.SpoolRestockLeadMinutes = leadMinutes;
        await SeedPrinterAsync(PrinterBackend.PrusaLink, "http://printer.local", 42);
        CanonicalSpoolIdentity identity = Identity(
            SpoolSourceKind.Central,
            "http://central.local",
            42);
        DateTime crossingUtc = leadMinutes == 0
            ? Now.UtcDateTime
            : Now.AddMinutes(leadMinutes - 1).UtcDateTime;
        SetupProjection(Ready(identity, crossingUtc));
        SetupResolvedSpool(identity, "PLA");
        await using AppDbContext db = CreateContext();

        ShiftPlanTaskSpec spec = Assert.Single(
            await CreateSource(db).ProduceAsync(CancellationToken.None));

        Assert.Equal(UserTaskAnchorKind.Now, spec.AnchorKind);
        Assert.Null(spec.AnchorAtUtc);
        Assert.Equal(crossingUtc, spec.DueAt);
    }

    [Theory]
    [InlineData(SpoolBurnRateProjectionState.InsufficientData)]
    [InlineData(SpoolBurnRateProjectionState.SourceUnavailable)]
    public async Task ProduceAsync_NonReadyCurrentOccurrence_PreservesStableKey(
        SpoolBurnRateProjectionState state)
    {
        await SeedPrinterAsync(PrinterBackend.PrusaLink, "http://printer.local", 42);
        CanonicalSpoolIdentity identity = Identity(
            SpoolSourceKind.Central,
            "http://central.local",
            42);
        SetupProjection(new SpoolBurnRateProjectionDto(
            identity.SourceKind,
            identity.SourceIdentity,
            identity.SpoolId,
            state == SpoolBurnRateProjectionState.InsufficientData ? 500 : null,
            15,
            null,
            null,
            Now.UtcDateTime,
            1,
            state));
        await using AppDbContext db = CreateContext();

        ShiftPlanSourceResult result = await CreateSource(db)
            .ProduceAsync(CancellationToken.None);

        Assert.Empty(result.Specs);
        string sourceId = Assert.Single(Assert.Single(result.Authority!.Kinds).PreservedSourceIds);
        Assert.StartsWith("spoolrestock:v1:42:", sourceId, StringComparison.Ordinal);
        _resolver.Verify(
            service => service.ResolveSpoolAsync(
                It.IsAny<CanonicalSpoolIdentity>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ProduceAsync_StaleProjection_PreservesWithoutResolvingName()
    {
        await SeedPrinterAsync(PrinterBackend.PrusaLink, "http://printer.local", 42);
        CanonicalSpoolIdentity identity = Identity(
            SpoolSourceKind.Central,
            "http://central.local",
            42);
        SetupProjection(Ready(identity, Now.AddHours(2).UtcDateTime) with
        {
            EvaluatedAtUtc = Now.AddTicks(-1).UtcDateTime,
        });
        await using AppDbContext db = CreateContext();

        ShiftPlanSourceResult result = await CreateSource(db)
            .ProduceAsync(CancellationToken.None);

        Assert.Empty(result.Specs);
        _ = Assert.Single(Assert.Single(result.Authority!.Kinds).PreservedSourceIds);
        _resolver.Verify(
            service => service.ResolveSpoolAsync(
                It.IsAny<CanonicalSpoolIdentity>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ProduceAsync_SameNumericIdAcrossSources_ProducesDistinctKeys()
    {
        await SeedPrinterAsync(PrinterBackend.PrusaLink, "http://central-printer.local", 42);
        await SeedPrinterAsync(PrinterBackend.Moonraker, "http://moon.local", 42);
        _projections
            .Setup(service => service.ProjectAsync(
                It.IsAny<CanonicalSpoolIdentity>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((CanonicalSpoolIdentity identity, CancellationToken _) =>
                Ready(identity, Now.AddHours(2).UtcDateTime));
        _resolver
            .Setup(service => service.ResolveSpoolAsync(
                It.IsAny<CanonicalSpoolIdentity>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((CanonicalSpoolIdentity identity, CancellationToken _) =>
                Snapshot(identity, $"Spool {identity.SourceKind}"));
        await using AppDbContext db = CreateContext();

        ShiftPlanSourceResult result = await CreateSource(db)
            .ProduceAsync(CancellationToken.None);

        Assert.Equal(2, result.Specs.Count);
        Assert.Equal(
            2,
            result.Specs.Select(spec => spec.SourceId).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(2, result.Specs.Select(spec => spec.EntityId).Distinct().Count());
    }

    [Fact]
    public async Task ProduceAsync_EquivalentNativeSourceIdentities_DeduplicatesOccurrence()
    {
        await SeedPrinterAsync(PrinterBackend.Moonraker, "HTTP://MOON.local/", 42);
        await SeedPrinterAsync(PrinterBackend.Moonraker, "http://moon.local", 42);
        CanonicalSpoolIdentity identity = Identity(
            SpoolSourceKind.MoonrakerNative,
            "http://moon.local",
            42);
        SetupProjection(Ready(identity, Now.AddHours(2).UtcDateTime));
        SetupResolvedSpool(identity, "PLA");
        await using AppDbContext db = CreateContext();

        ShiftPlanSourceResult result = await CreateSource(db)
            .ProduceAsync(CancellationToken.None);

        _ = Assert.Single(result.Specs);
        _projections.Verify(
            service => service.ProjectAsync(identity, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ProduceAsync_ToolheadAssignmentOverridesStaleLegacyScalar()
    {
        Guid printerId = await SeedPrinterAsync(
            PrinterBackend.PrusaLink,
            "http://printer.local",
            41);
        await SeedToolheadAsync(printerId, spoolId: 42, isPrimary: true);
        CanonicalSpoolIdentity effectiveIdentity = Identity(
            SpoolSourceKind.Central,
            "http://central.local",
            42);
        SetupProjection(Ready(effectiveIdentity, Now.AddHours(2).UtcDateTime));
        SetupResolvedSpool(effectiveIdentity, "Effective spool");
        await using AppDbContext db = CreateContext();

        ShiftPlanSourceResult result = await CreateSource(db)
            .ProduceAsync(CancellationToken.None);

        ShiftPlanTaskSpec spec = Assert.Single(result.Specs);
        Assert.StartsWith("spoolrestock:v1:42:", spec.SourceId, StringComparison.Ordinal);
        _projections.Verify(
            service => service.ProjectAsync(
                It.Is<CanonicalSpoolIdentity>(identity => identity.SpoolId == 41),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ProduceAsync_CanonicalOccurrenceRemoved_ReturnsAuthoritativeAbsence()
    {
        Guid printerId = await SeedPrinterAsync(
            PrinterBackend.PrusaLink,
            "http://printer.local",
            42);
        CanonicalSpoolIdentity identity = Identity(
            SpoolSourceKind.Central,
            "http://central.local",
            42);
        SetupProjection(Ready(identity, Now.AddHours(2).UtcDateTime));
        SetupResolvedSpool(identity, "PLA");
        await using (AppDbContext firstDb = CreateContext())
        {
            ShiftPlanSourceResult present = await CreateSource(firstDb)
                .ProduceAsync(CancellationToken.None);
            _ = Assert.Single(present.Specs);
        }

        await SetPrinterSpoolAsync(printerId, null);
        await using AppDbContext secondDb = CreateContext();

        ShiftPlanSourceResult absent = await CreateSource(secondDb)
            .ProduceAsync(CancellationToken.None);

        Assert.Empty(absent.Specs);
        ShiftPlanKindAuthority authority = Assert.Single(absent.Authority!.Kinds);
        Assert.True(authority.IsAuthoritativeComplete);
        Assert.Empty(authority.PreservedSourceIds);
    }

    [Fact]
    public async Task ProduceAsync_AssignmentChangesDuringProjection_FailsClosed()
    {
        Guid printerId = await SeedPrinterAsync(
            PrinterBackend.PrusaLink,
            "http://printer.local",
            42);
        CanonicalSpoolIdentity identity = Identity(
            SpoolSourceKind.Central,
            "http://central.local",
            42);
        _projections
            .Setup(service => service.ProjectAsync(
                identity,
                It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                await SetPrinterSpoolAsync(printerId, null);
                return Ready(identity, Now.AddHours(2).UtcDateTime);
            });
        SetupResolvedSpool(identity, "PLA");
        await using AppDbContext db = CreateContext();

        _ = await Assert.ThrowsAsync<InvalidOperationException>(
            () => CreateSource(db).ProduceAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ProduceAsync_SettingsChangeDuringProjection_FailsClosed()
    {
        await SeedPrinterAsync(PrinterBackend.PrusaLink, "http://printer.local", 42);
        CanonicalSpoolIdentity identity = Identity(
            SpoolSourceKind.Central,
            "http://central.local",
            42);
        _projections
            .Setup(service => service.ProjectAsync(
                identity,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                _settings.SpoolRestockLeadMinutes++;
                return Ready(identity, Now.AddHours(2).UtcDateTime);
            });
        SetupResolvedSpool(identity, "PLA");
        await using AppDbContext db = CreateContext();

        _ = await Assert.ThrowsAsync<InvalidOperationException>(
            () => CreateSource(db).ProduceAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ProduceAsync_GateDisablesAfterObservation_FailsClosed()
    {
        await SeedPrinterAsync(PrinterBackend.PrusaLink, "http://printer.local", 42);
        CanonicalSpoolIdentity identity = Identity(
            SpoolSourceKind.Central,
            "http://central.local",
            42);
        SetupProjection(Ready(identity, Now.AddHours(2).UtcDateTime));
        SetupResolvedSpool(identity, "PLA");
        _gate
            .SetupSequence(service => service.IsEnabledStrictAsync(
                OperatorFeature.ShiftPlan,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true)
            .ReturnsAsync(false);
        await using AppDbContext db = CreateContext();

        _ = await Assert.ThrowsAsync<InvalidOperationException>(
            () => CreateSource(db).ProduceAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ProduceAsync_UnqualifiedAssignedSpool_FailsBeforeProjection()
    {
        _spoolman.Setup(service => service.GetConfig()).Returns((SpoolmanConfigDto?)null);
        await SeedPrinterAsync(PrinterBackend.PrusaLink, "http://printer.local", 42);
        await using AppDbContext db = CreateContext();

        _ = await Assert.ThrowsAsync<InvalidOperationException>(
            () => CreateSource(db).ProduceAsync(CancellationToken.None));
        _projections.Verify(
            service => service.ProjectAsync(
                It.IsAny<CanonicalSpoolIdentity>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ProduceAsync_ProjectionIdentityMismatch_FailsClosed()
    {
        await SeedPrinterAsync(PrinterBackend.PrusaLink, "http://printer.local", 42);
        CanonicalSpoolIdentity identity = Identity(
            SpoolSourceKind.Central,
            "http://central.local",
            42);
        CanonicalSpoolIdentity wrong = Identity(
            SpoolSourceKind.Central,
            "http://other.local",
            42);
        SetupProjection(Ready(wrong, Now.AddHours(2).UtcDateTime), identity);
        await using AppDbContext db = CreateContext();

        _ = await Assert.ThrowsAsync<InvalidOperationException>(
            () => CreateSource(db).ProduceAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ProduceAsync_ResolverReturnsDifferentSpool_FailsClosed()
    {
        await SeedPrinterAsync(PrinterBackend.PrusaLink, "http://printer.local", 42);
        CanonicalSpoolIdentity identity = Identity(
            SpoolSourceKind.Central,
            "http://central.local",
            42);
        SetupProjection(Ready(identity, Now.AddHours(2).UtcDateTime));
        _resolver
            .Setup(service => service.ResolveSpoolAsync(
                identity,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FilamentCoverageSpoolSnapshot(
                new SpoolmanSpoolDto(43, "Wrong", "PLA", 500, null, true),
                false,
                null));
        await using AppDbContext db = CreateContext();

        _ = await Assert.ThrowsAsync<InvalidOperationException>(
            () => CreateSource(db).ProduceAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ProduceAsync_RemainingWeightChangesAfterProjection_FailsClosed()
    {
        await SeedPrinterAsync(PrinterBackend.PrusaLink, "http://printer.local", 42);
        CanonicalSpoolIdentity identity = Identity(
            SpoolSourceKind.Central,
            "http://central.local",
            42);
        SetupProjection(Ready(identity, Now.AddHours(2).UtcDateTime));
        _resolver
            .Setup(service => service.ResolveSpoolAsync(
                identity,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FilamentCoverageSpoolSnapshot(
                new SpoolmanSpoolDto(42, "Refilled", "PLA", 900, null, true),
                false,
                null));
        await using AppDbContext db = CreateContext();

        _ = await Assert.ThrowsAsync<InvalidOperationException>(
            () => CreateSource(db).ProduceAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ProduceAsync_LongUnicodeName_BoundsValidTextAndPreservesMetadata()
    {
        await SeedPrinterAsync(PrinterBackend.PrusaLink, "http://printer.local", 42);
        CanonicalSpoolIdentity identity = Identity(
            SpoolSourceKind.Central,
            "http://central.local",
            42);
        string spoolName = string.Concat(Enumerable.Repeat("e\u0301😀", 400));
        SetupProjection(Ready(identity, Now.AddHours(2).UtcDateTime));
        SetupResolvedSpool(identity, spoolName);
        await using AppDbContext db = CreateContext();

        ShiftPlanTaskSpec spec = Assert.Single(
            await CreateSource(db).ProduceAsync(CancellationToken.None));

        Assert.True(spec.Title.Length <= 200);
        Assert.True(spec.Description!.Length <= 1000);
        var strictUtf8 = new UTF8Encoding(false, true);
        _ = strictUtf8.GetByteCount(spec.Title);
        _ = strictUtf8.GetByteCount(spec.Description);
        using JsonDocument metadata = JsonDocument.Parse(spec.MetadataJson!);
        Assert.Equal(spoolName, metadata.RootElement.GetProperty("spoolName").GetString());
    }

    [Fact]
    public async Task ProduceAsync_MultipleOccurrences_ProjectsSequentially()
    {
        await SeedPrinterAsync(PrinterBackend.PrusaLink, "http://one.local", 41);
        await SeedPrinterAsync(PrinterBackend.PrusaLink, "http://two.local", 42);
        int active = 0;
        int maximumActive = 0;
        _projections
            .Setup(service => service.ProjectAsync(
                It.IsAny<CanonicalSpoolIdentity>(),
                It.IsAny<CancellationToken>()))
            .Returns(async (CanonicalSpoolIdentity identity, CancellationToken ct) =>
            {
                int current = Interlocked.Increment(ref active);
                maximumActive = Math.Max(maximumActive, current);
                await Task.Delay(10, ct);
                _ = Interlocked.Decrement(ref active);
                return Ready(identity, Now.AddHours(2).UtcDateTime);
            });
        _resolver
            .Setup(service => service.ResolveSpoolAsync(
                It.IsAny<CanonicalSpoolIdentity>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((CanonicalSpoolIdentity identity, CancellationToken _) =>
                Snapshot(identity, $"Spool {identity.SpoolId}"));
        await using AppDbContext db = CreateContext();

        ShiftPlanSourceResult result = await CreateSource(db)
            .ProduceAsync(CancellationToken.None);

        Assert.Equal(2, result.Specs.Count);
        Assert.Equal(1, maximumActive);
    }

    [Fact]
    public async Task ProduceAsync_CapturesOriginBeforeFeatureAndAssignmentObservations()
    {
        bool captured = false;
        var reader = new CallbackWatermarkReader(91, () => captured = true);
        _gate
            .Setup(service => service.IsEnabledStrictAsync(
                OperatorFeature.ShiftPlan,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                Assert.True(captured);
                return true;
            });
        await using AppDbContext db = CreateContext();

        ShiftPlanSourceResult result = await CreateSource(db, reader)
            .ProduceAsync(CancellationToken.None);

        Assert.Equal(91, result.OriginWatermark);
    }

    [Fact]
    public async Task ProduceAsync_ProjectionCancellation_Propagates()
    {
        await SeedPrinterAsync(PrinterBackend.PrusaLink, "http://printer.local", 42);
        CanonicalSpoolIdentity identity = Identity(
            SpoolSourceKind.Central,
            "http://central.local",
            42);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        _projections
            .Setup(service => service.ProjectAsync(
                identity,
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException(cancellation.Token));
        await using AppDbContext db = CreateContext();

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => CreateSource(db).ProduceAsync(cancellation.Token));
    }

    private SpoolRestockShiftPlanTaskSource CreateSource(
        AppDbContext db,
        IMutationWatermarkReader? watermarkReader = null) =>
        new(
            db,
            _projections.Object,
            _resolver.Object,
            _spoolman.Object,
            _settingsService.Object,
            _gate.Object,
            NullLogger<SpoolRestockShiftPlanTaskSource>.Instance,
            watermarkReader,
            _clock);

    private AppDbContext CreateContext() => TestDbHelpers.CreateContext(_connection);

    private async Task<Guid> SeedPrinterAsync(
        PrinterBackend backend,
        string serverUrl,
        int? spoolId)
    {
        await using AppDbContext db = CreateContext();
        Printer printer = new()
        {
            Id = Guid.NewGuid(),
            Name = $"Printer {Guid.NewGuid():N}",
            ServerUrl = serverUrl,
            BackendPort = 7125,
            Backend = (int)backend,
            CurrentSpoolId = spoolId,
            ManufacturerId = _manufacturerId,
            ModelId = _modelId,
        };
        _ = db.Printers.Add(printer);
        _ = await db.SaveChangesAsync();
        return printer.Id;
    }

    private async Task SetPrinterSpoolAsync(Guid printerId, int? spoolId)
    {
        await using AppDbContext db = CreateContext();
        Printer printer = await db.Printers.SingleAsync(item => item.Id == printerId);
        printer.CurrentSpoolId = spoolId;
        _ = await db.SaveChangesAsync();
    }

    private async Task SeedToolheadAsync(Guid printerId, int? spoolId, bool isPrimary)
    {
        await using AppDbContext db = CreateContext();
        _ = db.Toolheads.Add(new Toolhead
        {
            Id = Guid.NewGuid(),
            PrinterId = printerId,
            Name = "Extruder",
            Index = 0,
            IsPrimary = isPrimary,
            CurrentSpoolId = spoolId,
        });
        _ = await db.SaveChangesAsync();
    }

    private void SetupProjection(
        SpoolBurnRateProjectionDto projection,
        CanonicalSpoolIdentity? requestedIdentity = null)
    {
        CanonicalSpoolIdentity identity = requestedIdentity ?? new CanonicalSpoolIdentity(
            projection.SourceKind,
            projection.SourceIdentity,
            projection.SpoolId);
        _projections
            .Setup(service => service.ProjectAsync(
                identity,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(projection);
    }

    private void SetupResolvedSpool(CanonicalSpoolIdentity identity, string name) =>
        _resolver
            .Setup(service => service.ResolveSpoolAsync(
                identity,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Snapshot(identity, name));

    private static FilamentCoverageSpoolSnapshot Snapshot(
        CanonicalSpoolIdentity identity,
        string name) =>
        new(
            new SpoolmanSpoolDto(identity.SpoolId, name, "PLA", 500, null, true),
            false,
            null,
            17);

    private static SpoolBurnRateProjectionDto Ready(
        CanonicalSpoolIdentity identity,
        DateTime crossingUtc) =>
        new(
            identity.SourceKind,
            identity.SourceIdentity,
            identity.SpoolId,
            500,
            90,
            3,
            crossingUtc,
            Now.UtcDateTime,
            3,
            SpoolBurnRateProjectionState.Ready);

    private static CanonicalSpoolIdentity Identity(
        SpoolSourceKind sourceKind,
        string sourceIdentity,
        int spoolId) =>
        new(sourceKind, sourceIdentity, spoolId);

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class ConstantWatermarkReader(long value) : IMutationWatermarkReader
    {
        public Task<long> GetCurrentAsync(CancellationToken ct = default) =>
            Task.FromResult(value);
    }

    private sealed class CallbackWatermarkReader(long value, Action callback)
        : IMutationWatermarkReader
    {
        public Task<long> GetCurrentAsync(CancellationToken ct = default)
        {
            callback();
            return Task.FromResult(value);
        }
    }
}
