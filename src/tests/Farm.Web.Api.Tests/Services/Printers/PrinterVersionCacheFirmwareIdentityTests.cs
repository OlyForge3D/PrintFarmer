using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure;
using Farm.Infrastructure.Contracts.Printers;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.PrinterCalibration;
using Farm.Infrastructure.Repositories.UnitOfWork;
using Farm.Infrastructure.Services.Printers;
using Farm.Infrastructure.Services.Spoolman;
using Farm.Web.Api.Services.Calibration;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Services.Printers;

/// <summary>
/// Regression tests for #1656: firmware identity must have exactly one authoritative store.
/// <see cref="PrinterVersionCache"/> (backing <c>GET /api/printers/{id}/version</c> and the UI)
/// and <see cref="PrinterCalibrationContextService.ValidateFirmware"/> (the calibration gate)
/// previously read from two unreconciled stores — a live, never-persisted cache vs. the
/// persisted <c>Printer.Firmware*</c> columns — so a printer could show a firmware version in
/// the UI while calibration reported it entirely missing. These tests exercise both read paths
/// against the *same* underlying database row and assert they agree.
/// </summary>
public sealed class PrinterVersionCacheFirmwareIdentityTests
{
    private static AppDbContext NewDb(string dbName) =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(dbName).Options);

    private static Printer CreateNeverProbedMoonrakerPrinter() => new()
    {
        Id = Guid.NewGuid(),
        Name = "Registered before firmware detection shipped",
        ServerUrl = "http://10.0.0.50",
        BackendPort = 7125,
        Backend = (int)PrinterBackend.Moonraker,
        IsEnabled = true,
        FirmwareFamily = PrinterFirmwareFamily.Unknown,
        GcodeDialect = PrinterGcodeDialect.Unknown,
        FirmwareDetectionSource = FirmwareDetectionSource.Unknown,
        FirmwareVersion = null,
        FirmwareDetectionVersion = null,
        FirmwareDetectionConfidence = null,
        FirmwareDetectedAtUtc = null,
        FirmwareIdentityVerified = false,
    };

    private static PrintersService CreatePrintersService(AppDbContext db, IBackendClientFactory backendFactory)
    {
        var unitOfWork = new AppUnitOfWork(db, Mock.Of<Farm.Infrastructure.Services.Security.ISensitiveDataProtector>());
        return new PrintersService(
            unitOfWork,
            db,
            backendFactory,
            Mock.Of<IBackendCapabilityFactory>(),
            Mock.Of<Farm.Infrastructure.Services.Catalog.ICatalogService>(),
            Mock.Of<IHttpClientFactory>(),
            NullLogger<PrintersService>.Instance,
            Mock.Of<IPrinterStatusBroadcaster>(),
            Mock.Of<IMultiPrinterStatusCoordinator>(),
            Mock.Of<IPrinterStatusClientFactory>(),
            Mock.Of<IPrinterStatusCacheReader>(),
            Mock.Of<Farm.Infrastructure.Services.Locations.ILocationService>(),
            Mock.Of<Farm.Infrastructure.Services.Security.ISensitiveDataProtector>(),
            Mock.Of<Farm.Infrastructure.Services.Interfaces.ISpoolmanService>(),
            Mock.Of<Farm.Infrastructure.Services.Cameras.IGo2RtcService>(),
            Mock.Of<Farm.Infrastructure.Services.StorageManagement.IStoragePathService>(),
            Mock.Of<IFilamentCoverageSpoolResolver>());
    }

    private static PrinterCalibrationContextService CreateCalibrationService(AppDbContext db)
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new System.Collections.Generic.Dictionary<string, string?>
            {
                ["Calibration:StatusStaleAfterSeconds"] = "30",
                ["Calibration:FirmwareMetadataStaleAfterSeconds"] = "86400",
                ["Calibration:HardwareMetadataStaleAfterSeconds"] = "2592000",
            })
            .Build();

        var statusReader = new Mock<IPrinterStatusSnapshotReader>();
        _ = statusReader.Setup(reader => reader.GetStatusSnapshot(It.IsAny<Guid>())).Returns((PrinterStatusSnapshot?)null);
        var capabilityFactory = new Mock<IBackendCapabilityFactory>();
        _ = capabilityFactory
            .Setup(factory => factory.GetSupportedCapabilities(It.IsAny<PrinterBackend>()))
            .Returns(BackendCapabilities.Status);

        return new PrinterCalibrationContextService(
            db,
            statusReader.Object,
            capabilityFactory.Object,
            configuration,
            TimeProvider.System);
    }

    private static (Mock<IBackendClientFactory> Factory, Mock<ISupportsPrinterInformation> Info) CreateMockedInfoBackend(
        PrinterBackend backend,
        string firmware,
        string? backendVersion = "v0.9.3",
        string? apiVersion = "v1")
    {
        Mock<IBackendClient> client = new();
        Mock<ISupportsPrinterInformation> info = client.As<ISupportsPrinterInformation>();
        _ = info
            .Setup(c => c.GetPrinterInformationAsync(It.IsAny<string>(), It.IsAny<PrinterCredential?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StandardPrinterInfo { Name = "printer", Firmware = firmware, BackendVersion = backendVersion, ApiVersion = apiVersion });

        Mock<IBackendClientFactory> factory = new();
        _ = factory.Setup(f => f.GetClient(backend)).Returns(client.Object);

        return (factory, info);
    }

    [Fact]
    public async Task GetAsync_NeverProbedMoonrakerPrinter_PersistsThroughAndCalibrationGateThenAgrees()
    {
        string dbName = $"firmware-identity-{Guid.NewGuid()}";
        Printer printer = CreateNeverProbedMoonrakerPrinter();

        await using (AppDbContext seedDb = NewDb(dbName))
        {
            _ = seedDb.Printers.Add(printer);
            _ = await seedDb.SaveChangesAsync();
        }

        // Reproduce the bug: before any read-through, the calibration gate reports firmware as
        // entirely missing.
        await using (AppDbContext calibrationDbBefore = NewDb(dbName))
        {
            PrinterCalibrationContextService calibrationServiceBefore = CreateCalibrationService(calibrationDbBefore);
            CalibrationCandidateDto before = (await calibrationServiceBefore.GetCandidatesAsync(
                new CalibrationProfileAccessScope(UserId: null, BypassOwnership: true),
                CancellationToken.None)).Value!.Should().ContainSingle().Which;
            _ = before.MissingInputs.Should().Contain(
                "firmware.family", "firmware.gcodeDialect", "firmware.detectionSource", "firmware.version");
        }

        // Act: the version endpoint's read-through triggers a cadence-gated probe and persists
        // the full firmware identity (#1656 fix), not just FirmwareVersion/FirmwareDetectedAtUtc.
        (Mock<IBackendClientFactory> backendFactory, Mock<ISupportsPrinterInformation> info) =
            CreateMockedInfoBackend(PrinterBackend.Moonraker, "v0.12.0");

        PrinterVersionInfoDto? dto;
        await using (AppDbContext cacheDb = NewDb(dbName))
        {
            PrintersService printersService = CreatePrintersService(cacheDb, backendFactory.Object);
            PrinterVersionCache cache = new(
                new MemoryCache(new MemoryCacheOptions()),
                Options.Create(new PrinterVersionCacheOptions()),
                printersService,
                backendFactory.Object);

            dto = await cache.GetAsync(printer.Id, CancellationToken.None);
        }

        _ = dto.Should().NotBeNull();
        _ = dto!.FirmwareVersion.Should().Be("v0.12.0");
        _ = dto.RecordedFirmwareIdentity.Should().NotBeNull();
        _ = dto.RecordedFirmwareIdentity!.Family.Should().Be(nameof(PrinterFirmwareFamily.Klipper));
        _ = dto.RecordedFirmwareIdentity.GcodeDialect.Should().Be(nameof(PrinterGcodeDialect.Klipper));
        _ = dto.RecordedFirmwareIdentity.DetectionSource.Should().Be("printer");
        _ = dto.RecordedFirmwareIdentity.Version.Should().Be("v0.12.0");
        info.Verify(
            c => c.GetPrinterInformationAsync(It.IsAny<string>(), It.IsAny<PrinterCredential?>(), It.IsAny<CancellationToken>()),
            Times.Once);

        // Assert: the calibration gate — reading the SAME persisted row through a fresh
        // DbContext — now agrees exactly with what the version endpoint/UI returned, and no
        // longer reports firmware as missing (except firmware.verified, which stays gated on
        // explicit operator action per #1656 constraint #3 and is unaffected by any probe).
        await using (AppDbContext calibrationDbAfter = NewDb(dbName))
        {
            PrinterCalibrationContextService calibrationServiceAfter = CreateCalibrationService(calibrationDbAfter);
            CalibrationCandidateDto after = (await calibrationServiceAfter.GetCandidatesAsync(
                new CalibrationProfileAccessScope(UserId: null, BypassOwnership: true),
                CancellationToken.None)).Value!.Should().ContainSingle().Which;

            _ = after.Firmware.Version.Should().Be(dto.FirmwareVersion);
            _ = after.Firmware.Family.Should().Be(dto.RecordedFirmwareIdentity.Family);
            _ = after.Firmware.GcodeDialect.Should().Be(dto.RecordedFirmwareIdentity.GcodeDialect);
            _ = after.Firmware.DetectionSource.Should().Be(dto.RecordedFirmwareIdentity.DetectionSource);
            _ = after.MissingInputs.Should().NotContain(
                "firmware.family", "firmware.gcodeDialect", "firmware.detectionSource",
                "firmware.version", "firmware.detectionVersion", "firmware.detectionConfidence");
        }
    }

    [Fact]
    public async Task GetAsync_RecentlyProbedMoonrakerPrinter_ProbesLiveVersionInfoButDoesNotRewriteFirmwareIdentity()
    {
        Printer printer = CreateNeverProbedMoonrakerPrinter();
        printer.FirmwareFamily = PrinterFirmwareFamily.Klipper;
        printer.GcodeDialect = PrinterGcodeDialect.Klipper;
        printer.FirmwareDetectionSource = FirmwareDetectionSource.Printer;
        printer.FirmwareVersion = "v0.11.0";
        printer.FirmwareDetectionVersion = "moonraker-printer-info-v1";
        printer.FirmwareDetectionConfidence = 1m;
        printer.FirmwareDetectedAtUtc = DateTime.UtcNow.AddHours(-1); // well within the 6h default cadence

        await using AppDbContext db = NewDb($"firmware-identity-{Guid.NewGuid()}");
        _ = db.Printers.Add(printer);
        _ = await db.SaveChangesAsync();

        (Mock<IBackendClientFactory> backendFactory, Mock<ISupportsPrinterInformation> info) =
            CreateMockedInfoBackend(PrinterBackend.Moonraker, "v99.99.99", backendVersion: "v1.2.3", apiVersion: "v2");

        PrintersService printersService = CreatePrintersService(db, backendFactory.Object);
        PrinterVersionCache cache = new(
            new MemoryCache(new MemoryCacheOptions()),
            Options.Create(new PrinterVersionCacheOptions()),
            printersService,
            backendFactory.Object);

        PrinterVersionInfoDto? dto = await cache.GetAsync(printer.Id, CancellationToken.None);

        // The live probe still runs on every cache miss — matching the pre-#1656 thin-probe
        // cadence exactly, so BackendVersion/ApiVersion (fields the calibration gate never reads)
        // stay live and this is not a functional regression versus the old behavior.
        info.Verify(
            c => c.GetPrinterInformationAsync(It.IsAny<string>(), It.IsAny<PrinterCredential?>(), It.IsAny<CancellationToken>()),
            Times.Once);

        _ = dto.Should().NotBeNull();
        _ = dto!.BackendVersion.Should().Be("v1.2.3");
        _ = dto.ApiVersion.Should().Be("v2");

        // The firmware identity itself — the single authoritative fact the calibration gate also
        // reads — is untouched: the cadence guard blocks the DB write, so the persisted (and
        // reported) version stays the last recorded value, never the fresh probe's reading.
        _ = dto.FirmwareVersion.Should().Be("v0.11.0");
        Printer? reread = await db.Printers.AsNoTracking().SingleAsync(p => p.Id == printer.Id);
        _ = reread.FirmwareVersion.Should().Be("v0.11.0");
    }

    [Theory]
    [InlineData(PrinterBackend.PrusaLink)]
    [InlineData(PrinterBackend.OctoPrint)]
    public async Task GetAsync_NonMoonrakerBackend_UsesThinProbeWithNoPersistence(PrinterBackend backend)
    {
        Printer printer = CreateNeverProbedMoonrakerPrinter();
        printer.Backend = (int)backend;

        await using AppDbContext db = NewDb($"firmware-identity-{Guid.NewGuid()}");
        _ = db.Printers.Add(printer);
        _ = await db.SaveChangesAsync();

        (Mock<IBackendClientFactory> backendFactory, _) = CreateMockedInfoBackend(backend, "1.2.3");

        PrintersService printersService = CreatePrintersService(db, backendFactory.Object);
        PrinterVersionCache cache = new(
            new MemoryCache(new MemoryCacheOptions()),
            Options.Create(new PrinterVersionCacheOptions()),
            printersService,
            backendFactory.Object);

        PrinterVersionInfoDto? dto = await cache.GetAsync(printer.Id, CancellationToken.None);

        _ = dto.Should().NotBeNull();
        _ = dto!.FirmwareVersion.Should().Be("1.2.3");
        _ = dto.RecordedFirmwareIdentity.Should().BeNull();

        Printer? reread = await db.Printers.AsNoTracking().SingleAsync(p => p.Id == printer.Id);
        _ = reread.FirmwareVersion.Should().BeNull();
        _ = reread.FirmwareDetectedAtUtc.Should().BeNull();
    }

    [Fact]
    public async Task GetAsync_MoonrakerProbeThrows_FallsBackToPersistedValuesWithMessage()
    {
        Printer printer = CreateNeverProbedMoonrakerPrinter();
        printer.FirmwareFamily = PrinterFirmwareFamily.Klipper;
        printer.GcodeDialect = PrinterGcodeDialect.Klipper;
        printer.FirmwareDetectionSource = FirmwareDetectionSource.Printer;
        printer.FirmwareVersion = "v0.10.0";
        printer.FirmwareDetectionVersion = "moonraker-printer-info-v1";
        printer.FirmwareDetectionConfidence = 1m;
        printer.FirmwareDetectedAtUtc = null; // due for re-probe

        await using AppDbContext db = NewDb($"firmware-identity-{Guid.NewGuid()}");
        _ = db.Printers.Add(printer);
        _ = await db.SaveChangesAsync();

        Mock<IBackendClient> client = new();
        Mock<ISupportsPrinterInformation> info = client.As<ISupportsPrinterInformation>();
        _ = info
            .Setup(c => c.GetPrinterInformationAsync(It.IsAny<string>(), It.IsAny<PrinterCredential?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("printer unreachable"));
        Mock<IBackendClientFactory> backendFactory = new();
        _ = backendFactory.Setup(f => f.GetClient(PrinterBackend.Moonraker)).Returns(client.Object);

        PrintersService printersService = CreatePrintersService(db, backendFactory.Object);
        PrinterVersionCache cache = new(
            new MemoryCache(new MemoryCacheOptions()),
            Options.Create(new PrinterVersionCacheOptions()),
            printersService,
            backendFactory.Object);

        PrinterVersionInfoDto? dto = await cache.GetAsync(printer.Id, CancellationToken.None);

        _ = dto.Should().NotBeNull();
        _ = dto!.FirmwareVersion.Should().Be("v0.10.0");
        _ = dto.Message.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task GetAsync_LegacyKlipperPrinterWithUnsetGcodeDialect_AgreesWithCalibrationGateOnEffectiveDialect()
    {
        // A printer whose FirmwareFamily was set (e.g. via manual-add onboarding hints or an
        // earlier detection pass) but whose GcodeDialect column was never separately populated —
        // both the version endpoint and the calibration gate must apply the same
        // family-implies-dialect fallback, or the two would disagree on GcodeDialect specifically
        // (a Hicks/Bishop review finding for #1656).
        Printer printer = CreateNeverProbedMoonrakerPrinter();
        printer.FirmwareFamily = PrinterFirmwareFamily.Klipper;
        printer.GcodeDialect = PrinterGcodeDialect.Unknown; // never explicitly populated
        printer.FirmwareDetectionSource = FirmwareDetectionSource.Configured;
        printer.FirmwareVersion = "v0.9.0";
        printer.FirmwareDetectedAtUtc = DateTime.UtcNow.AddHours(-1); // within cadence — no reprobe

        string dbName = $"firmware-identity-{Guid.NewGuid()}";
        await using (AppDbContext seedDb = NewDb(dbName))
        {
            _ = seedDb.Printers.Add(printer);
            _ = await seedDb.SaveChangesAsync();
        }

        (Mock<IBackendClientFactory> backendFactory, _) = CreateMockedInfoBackend(PrinterBackend.Moonraker, "v99.99.99");

        PrinterVersionInfoDto? dto;
        await using (AppDbContext cacheDb = NewDb(dbName))
        {
            PrintersService printersService = CreatePrintersService(cacheDb, backendFactory.Object);
            PrinterVersionCache cache = new(
                new MemoryCache(new MemoryCacheOptions()),
                Options.Create(new PrinterVersionCacheOptions()),
                printersService,
                backendFactory.Object);

            dto = await cache.GetAsync(printer.Id, CancellationToken.None);
        }

        _ = dto.Should().NotBeNull();
        _ = dto!.RecordedFirmwareIdentity.Should().NotBeNull();
        _ = dto.RecordedFirmwareIdentity!.GcodeDialect.Should().Be(nameof(PrinterGcodeDialect.Klipper));

        await using (AppDbContext calibrationDb = NewDb(dbName))
        {
            PrinterCalibrationContextService calibrationService = CreateCalibrationService(calibrationDb);
            CalibrationCandidateDto candidate = (await calibrationService.GetCandidatesAsync(
                new CalibrationProfileAccessScope(UserId: null, BypassOwnership: true),
                CancellationToken.None)).Value!.Should().ContainSingle().Which;

            _ = candidate.Firmware.GcodeDialect.Should().Be(dto.RecordedFirmwareIdentity.GcodeDialect);
        }
    }

    [Fact]
    public async Task GetAsync_MoonrakerPrinterWithConfiguredNonKlipperFamily_OverwritesFamilyOnSuccessfulProbe()
    {
        // Documents an intentional trade-off: the backend type (Moonraker) is already registered
        // and is only ever Klipper-family in this codebase, so a due, successful probe always
        // writes FirmwareFamily = Klipper — even overwriting a stale/incorrect operator-configured
        // family — because a Moonraker backend cannot actually be anything else.
        Printer printer = CreateNeverProbedMoonrakerPrinter();
        printer.FirmwareFamily = PrinterFirmwareFamily.Other;
        printer.FirmwareDetectionSource = FirmwareDetectionSource.Configured;
        printer.FirmwareVersion = "stale-manual-entry";
        printer.FirmwareDetectedAtUtc = null; // due for re-probe

        await using AppDbContext db = NewDb($"firmware-identity-{Guid.NewGuid()}");
        _ = db.Printers.Add(printer);
        _ = await db.SaveChangesAsync();

        (Mock<IBackendClientFactory> backendFactory, _) = CreateMockedInfoBackend(PrinterBackend.Moonraker, "v0.12.0");

        PrintersService printersService = CreatePrintersService(db, backendFactory.Object);
        PrinterVersionCache cache = new(
            new MemoryCache(new MemoryCacheOptions()),
            Options.Create(new PrinterVersionCacheOptions()),
            printersService,
            backendFactory.Object);

        PrinterVersionInfoDto? dto = await cache.GetAsync(printer.Id, CancellationToken.None);

        _ = dto.Should().NotBeNull();
        _ = dto!.RecordedFirmwareIdentity!.Family.Should().Be(nameof(PrinterFirmwareFamily.Klipper));
        _ = dto.FirmwareVersion.Should().Be("v0.12.0");

        Printer? reread = await db.Printers.AsNoTracking().SingleAsync(p => p.Id == printer.Id);
        _ = reread.FirmwareFamily.Should().Be(PrinterFirmwareFamily.Klipper);
    }

    [Fact]
    public async Task GetAsync_MoonrakerProbeThrows_CachesFailureForShortTtlNotFullSuccessTtl()
    {
        // Bishop round-3 finding: the cache TTL selection previously only checked
        // dto.FirmwareVersion is not null, so a failed probe that fell back to a stale persisted
        // FirmwareVersion (still non-null) was cached for the full 10-minute success TTL instead
        // of the short failure TTL — hiding a transient failure/blocking a manual retry far
        // longer than intended.
        Printer printer = CreateNeverProbedMoonrakerPrinter();
        printer.FirmwareFamily = PrinterFirmwareFamily.Klipper;
        printer.GcodeDialect = PrinterGcodeDialect.Klipper;
        printer.FirmwareDetectionSource = FirmwareDetectionSource.Printer;
        printer.FirmwareVersion = "v0.10.0"; // stale persisted value the fallback will serve
        printer.FirmwareDetectedAtUtc = null; // due for re-probe

        await using AppDbContext db = NewDb($"firmware-identity-{Guid.NewGuid()}");
        _ = db.Printers.Add(printer);
        _ = await db.SaveChangesAsync();

        Mock<IBackendClient> client = new();
        Mock<ISupportsPrinterInformation> info = client.As<ISupportsPrinterInformation>();
        _ = info
            .Setup(c => c.GetPrinterInformationAsync(It.IsAny<string>(), It.IsAny<PrinterCredential?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("printer unreachable"));
        Mock<IBackendClientFactory> backendFactory = new();
        _ = backendFactory.Setup(f => f.GetClient(PrinterBackend.Moonraker)).Returns(client.Object);

        PrintersService printersService = CreatePrintersService(db, backendFactory.Object);
        var capturingCache = new ExpirationCapturingMemoryCache();
        PrinterVersionCache cache = new(
            capturingCache,
            Options.Create(new PrinterVersionCacheOptions()),
            printersService,
            backendFactory.Object);

        PrinterVersionInfoDto? dto = await cache.GetAsync(printer.Id, CancellationToken.None);

        _ = dto.Should().NotBeNull();
        _ = dto!.FirmwareVersion.Should().Be("v0.10.0");
        _ = dto.Message.Should().NotBeNullOrEmpty();
        _ = capturingCache.LastAbsoluteExpirationRelativeToNow.Should().Be(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task GetAsync_MoonrakerProbeSucceeds_CachesForFullSuccessTtl()
    {
        Printer printer = CreateNeverProbedMoonrakerPrinter();

        await using AppDbContext db = NewDb($"firmware-identity-{Guid.NewGuid()}");
        _ = db.Printers.Add(printer);
        _ = await db.SaveChangesAsync();

        (Mock<IBackendClientFactory> backendFactory, _) = CreateMockedInfoBackend(PrinterBackend.Moonraker, "v0.12.0");

        PrintersService printersService = CreatePrintersService(db, backendFactory.Object);
        var capturingCache = new ExpirationCapturingMemoryCache();
        var options = new PrinterVersionCacheOptions();
        PrinterVersionCache cache = new(
            capturingCache,
            Options.Create(options),
            printersService,
            backendFactory.Object);

        PrinterVersionInfoDto? dto = await cache.GetAsync(printer.Id, CancellationToken.None);

        _ = dto.Should().NotBeNull();
        _ = dto!.Message.Should().BeNullOrEmpty();
        _ = capturingCache.LastAbsoluteExpirationRelativeToNow.Should().Be(options.Ttl);
    }

    /// <summary>
    /// Minimal <see cref="IMemoryCache"/> test double that never serves a hit (so
    /// <see cref="PrinterVersionCache.GetAsync"/> always exercises the fetch path) and records
    /// the <see cref="ICacheEntry.AbsoluteExpirationRelativeToNow"/> passed to the most recent
    /// <c>Set</c> call, so tests can assert on which TTL a given outcome was cached with.
    /// </summary>
    private sealed class ExpirationCapturingMemoryCache : IMemoryCache
    {
        public TimeSpan? LastAbsoluteExpirationRelativeToNow { get; private set; }

        public ICacheEntry CreateEntry(object key) => new CapturingEntry(this);

        public void Dispose()
        {
        }

        public void Remove(object key)
        {
        }

        public bool TryGetValue(object key, out object? value)
        {
            value = null;
            return false;
        }

        private sealed class CapturingEntry(ExpirationCapturingMemoryCache owner) : ICacheEntry
        {
            public object Key { get; } = new object();

            public object? Value { get; set; }

            public DateTimeOffset? AbsoluteExpiration { get; set; }

            public TimeSpan? AbsoluteExpirationRelativeToNow { get; set; }

            public TimeSpan? SlidingExpiration { get; set; }

            public IList<Microsoft.Extensions.Primitives.IChangeToken> ExpirationTokens { get; } = new List<Microsoft.Extensions.Primitives.IChangeToken>();

            public IList<PostEvictionCallbackRegistration> PostEvictionCallbacks { get; } = new List<PostEvictionCallbackRegistration>();

            public CacheItemPriority Priority { get; set; }

            public long? Size { get; set; }

            public void Dispose() => owner.LastAbsoluteExpirationRelativeToNow = AbsoluteExpirationRelativeToNow;
        }
    }
}
