using System;
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
    public async Task GetAsync_RecentlyProbedMoonrakerPrinter_DoesNotReprobeAndServesPersistedValues()
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
            CreateMockedInfoBackend(PrinterBackend.Moonraker, "v99.99.99");

        PrintersService printersService = CreatePrintersService(db, backendFactory.Object);
        PrinterVersionCache cache = new(
            new MemoryCache(new MemoryCacheOptions()),
            Options.Create(new PrinterVersionCacheOptions()),
            printersService,
            backendFactory.Object);

        PrinterVersionInfoDto? dto = await cache.GetAsync(printer.Id, CancellationToken.None);

        _ = dto.Should().NotBeNull();
        _ = dto!.FirmwareVersion.Should().Be("v0.11.0");
        info.Verify(
            c => c.GetPrinterInformationAsync(It.IsAny<string>(), It.IsAny<PrinterCredential?>(), It.IsAny<CancellationToken>()),
            Times.Never);
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
}
