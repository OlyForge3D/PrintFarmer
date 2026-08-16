using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Discovery;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Printers;
using Farm.Infrastructure.Repositories.UnitOfWork;
using Farm.Infrastructure.Services.Printers;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Farm.Web.Api.Tests.Services.Printers;

/// <summary>
/// Tests for the periodic firmware re-probe/refresh producer (#1618 / #1613 PR-5):
/// <see cref="PrintersService.RefreshDetectedFirmwareIdentityAsync"/>.
/// </summary>
public sealed class PrintersServiceFirmwareRefreshTests
{
    [Fact]
    public async Task RefreshDetectedFirmwareIdentityAsync_NoFirmwareDetected_IsNoOp()
    {
        await using AppDbContext db = CreateDbContext();
        Printer printer = CreatePrinter();
        var unitOfWork = CreateUnitOfWork(printer, out Mock<IPrintersRepository> repository);
        PrintersService service = CreateService(db, unitOfWork.Object);

        DiscoveredPrinterDto discovered = new() { FirmwareFamily = null };

        bool refreshed = await service.RefreshDetectedFirmwareIdentityAsync(printer.Id, discovered, CancellationToken.None);

        refreshed.Should().BeFalse();
        printer.FirmwareDetectedAtUtc.Should().BeNull();
        unitOfWork.Verify(work => work.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RefreshDetectedFirmwareIdentityAsync_PrinterNotFound_IsNoOp()
    {
        await using AppDbContext db = CreateDbContext();
        var repository = new Mock<IPrintersRepository>();
        repository
            .Setup(r => r.FindByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Printer?)null);
        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.Setup(work => work.Printers).Returns(repository.Object);
        PrintersService service = CreateService(db, unitOfWork.Object);

        DiscoveredPrinterDto discovered = new() { FirmwareFamily = PrinterFirmwareFamily.Klipper };

        bool refreshed = await service.RefreshDetectedFirmwareIdentityAsync(Guid.NewGuid(), discovered, CancellationToken.None);

        refreshed.Should().BeFalse();
        unitOfWork.Verify(work => work.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RefreshDetectedFirmwareIdentityAsync_NeverDetectedBefore_AppliesFreshDetection()
    {
        await using AppDbContext db = CreateDbContext();
        Printer printer = CreatePrinter();
        var unitOfWork = CreateUnitOfWork(printer, out _);
        PrintersService service = CreateService(db, unitOfWork.Object);

        DiscoveredPrinterDto discovered = new()
        {
            FirmwareFamily = PrinterFirmwareFamily.Klipper,
            GcodeDialect = PrinterGcodeDialect.Klipper,
            FirmwareDetectionSource = FirmwareDetectionSource.Printer,
            FirmwareDetectionConfidence = 1.0m,
            FirmwareDetectionVersion = MoonrakerOnboardingResolver.FirmwareProbeVersion,
            FirmwareVersion = "v0.12.0",
            FirmwareDetectedAtUtc = DateTime.UtcNow
        };

        bool refreshed = await service.RefreshDetectedFirmwareIdentityAsync(printer.Id, discovered, CancellationToken.None);

        refreshed.Should().BeTrue();
        printer.FirmwareFamily.Should().Be(PrinterFirmwareFamily.Klipper);
        printer.GcodeDialect.Should().Be(PrinterGcodeDialect.Klipper);
        printer.FirmwareDetectionSource.Should().Be(FirmwareDetectionSource.Printer);
        printer.FirmwareDetectionConfidence.Should().Be(1.0m);
        printer.FirmwareDetectionVersion.Should().Be(MoonrakerOnboardingResolver.FirmwareProbeVersion);
        printer.FirmwareVersion.Should().Be("v0.12.0");
        printer.FirmwareDetectedAtUtc.Should().NotBeNull();
        unitOfWork.Verify(work => work.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RefreshDetectedFirmwareIdentityAsync_WithinReprobeInterval_IsThrottled()
    {
        await using AppDbContext db = CreateDbContext();
        Printer printer = CreatePrinter();
        printer.FirmwareFamily = PrinterFirmwareFamily.Klipper;
        printer.GcodeDialect = PrinterGcodeDialect.Klipper;
        printer.FirmwareDetectionSource = FirmwareDetectionSource.Printer;
        printer.FirmwareVersion = "v0.11.0";
        printer.FirmwareDetectedAtUtc = DateTime.UtcNow.AddHours(-1); // well within the default 6h cadence
        var unitOfWork = CreateUnitOfWork(printer, out _);
        PrintersService service = CreateService(db, unitOfWork.Object);

        DiscoveredPrinterDto discovered = new()
        {
            FirmwareFamily = PrinterFirmwareFamily.Klipper,
            FirmwareVersion = "v0.12.0",
            FirmwareDetectedAtUtc = DateTime.UtcNow
        };

        bool refreshed = await service.RefreshDetectedFirmwareIdentityAsync(printer.Id, discovered, CancellationToken.None);

        refreshed.Should().BeFalse();
        printer.FirmwareVersion.Should().Be("v0.11.0"); // unchanged - throttled
        unitOfWork.Verify(work => work.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RefreshDetectedFirmwareIdentityAsync_PastReprobeInterval_RefreshesDetection()
    {
        await using AppDbContext db = CreateDbContext();
        Printer printer = CreatePrinter();
        printer.FirmwareFamily = PrinterFirmwareFamily.Klipper;
        printer.GcodeDialect = PrinterGcodeDialect.Klipper;
        printer.FirmwareDetectionSource = FirmwareDetectionSource.Printer;
        printer.FirmwareVersion = "v0.11.0";
        printer.FirmwareDetectedAtUtc = DateTime.UtcNow.AddHours(-7); // past the default 6h cadence
        var unitOfWork = CreateUnitOfWork(printer, out _);
        PrintersService service = CreateService(db, unitOfWork.Object);

        DateTime newDetectedAt = DateTime.UtcNow;
        DiscoveredPrinterDto discovered = new()
        {
            FirmwareFamily = PrinterFirmwareFamily.Klipper,
            GcodeDialect = PrinterGcodeDialect.Klipper,
            FirmwareDetectionSource = FirmwareDetectionSource.Printer,
            FirmwareDetectionConfidence = 0.9m,
            FirmwareDetectionVersion = MoonrakerOnboardingResolver.FirmwareProbeVersion,
            FirmwareVersion = "v0.12.0",
            FirmwareDetectedAtUtc = newDetectedAt
        };

        bool refreshed = await service.RefreshDetectedFirmwareIdentityAsync(printer.Id, discovered, CancellationToken.None);

        refreshed.Should().BeTrue();
        printer.FirmwareVersion.Should().Be("v0.12.0");
        printer.FirmwareDetectionConfidence.Should().Be(0.9m);
        printer.FirmwareDetectedAtUtc.Should().Be(newDetectedAt);
        unitOfWork.Verify(work => work.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task IsFirmwareReprobeDue_NeverProbed_ReturnsTrue()
    {
        await using AppDbContext db = CreateDbContext();
        Printer printer = CreatePrinter();
        printer.FirmwareDetectedAtUtc = null;
        var unitOfWork = CreateUnitOfWork(printer, out _);
        PrintersService service = CreateService(db, unitOfWork.Object);

        service.IsFirmwareReprobeDue(printer).Should().BeTrue();
    }

    [Fact]
    public async Task IsFirmwareReprobeDue_WithinCadenceWindow_ReturnsFalse()
    {
        await using AppDbContext db = CreateDbContext();
        Printer printer = CreatePrinter();
        printer.FirmwareDetectedAtUtc = DateTime.UtcNow.AddHours(-1); // well within the default 6h cadence
        var unitOfWork = CreateUnitOfWork(printer, out _);
        PrintersService service = CreateService(db, unitOfWork.Object);

        service.IsFirmwareReprobeDue(printer).Should().BeFalse();
    }

    [Fact]
    public async Task IsFirmwareReprobeDue_PastCadenceWindow_ReturnsTrue()
    {
        await using AppDbContext db = CreateDbContext();
        Printer printer = CreatePrinter();
        printer.FirmwareDetectedAtUtc = DateTime.UtcNow.AddHours(-7); // past the default 6h cadence
        var unitOfWork = CreateUnitOfWork(printer, out _);
        PrintersService service = CreateService(db, unitOfWork.Object);

        service.IsFirmwareReprobeDue(printer).Should().BeTrue();
    }

    [Fact]
    public void IsFirmwareReprobeDue_NullPrinter_Throws()
    {
        using AppDbContext db = CreateDbContext();
        var unitOfWork = new Mock<IUnitOfWork>();
        PrintersService service = CreateService(db, unitOfWork.Object);

        Action act = () => service.IsFirmwareReprobeDue(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task RefreshDetectedFirmwareIdentityAsync_SaveFails_RestoresOriginalFirmwareValuesBeforeRethrow()
    {
        // Bishop round-3 finding: the original fix (reload the entity from the database on
        // SaveChangesAsync failure) still left a gap — if the reload itself also fails to find or
        // restore the row (as happens here, since this test's `db` never actually persisted
        // `printer`), the tracked entity could keep the failed mutation. The fix restores the
        // pre-mutation snapshot on the tracked entity directly (no I/O dependency) before
        // attempting the best-effort reload, so this must hold regardless of what the reload does.
        await using AppDbContext db = CreateDbContext();
        Printer printer = CreatePrinter();
        printer.FirmwareFamily = PrinterFirmwareFamily.Klipper;
        printer.GcodeDialect = PrinterGcodeDialect.Klipper;
        printer.FirmwareDetectionSource = FirmwareDetectionSource.Printer;
        printer.FirmwareVersion = "v0.11.0";
        printer.FirmwareDetectionVersion = "moonraker-printer-info-v1";
        printer.FirmwareDetectionConfidence = 1.0m;
        DateTime originalDetectedAt = DateTime.UtcNow.AddHours(-7); // past the default 6h cadence
        printer.FirmwareDetectedAtUtc = originalDetectedAt;

        var repository = new Mock<IPrintersRepository>();
        repository
            .Setup(r => r.FindByIdAsync(printer.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(printer);
        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.Setup(work => work.Printers).Returns(repository.Object);
        unitOfWork
            .Setup(work => work.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("simulated database outage"));
        PrintersService service = CreateService(db, unitOfWork.Object);

        DiscoveredPrinterDto discovered = new()
        {
            FirmwareFamily = PrinterFirmwareFamily.Klipper,
            GcodeDialect = PrinterGcodeDialect.Klipper,
            FirmwareDetectionSource = FirmwareDetectionSource.Printer,
            FirmwareVersion = "v99.99.99", // must never leak through on a failed save
            FirmwareDetectionVersion = "moonraker-printer-info-v2",
            FirmwareDetectionConfidence = 0.5m,
            FirmwareDetectedAtUtc = DateTime.UtcNow,
        };

        Func<Task> act = () => service.RefreshDetectedFirmwareIdentityAsync(printer.Id, discovered, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();

        // `printer` was never added to `db`, so the best-effort ReloadAsync call finds no
        // matching row and cannot itself restore anything — proving the manual snapshot/restore
        // (not the reload) is what guarantees no caller ever observes the failed mutation.
        printer.FirmwareVersion.Should().Be("v0.11.0");
        printer.FirmwareDetectionVersion.Should().Be("moonraker-printer-info-v1");
        printer.FirmwareDetectionConfidence.Should().Be(1.0m);
        printer.FirmwareDetectedAtUtc.Should().Be(originalDetectedAt);
        printer.FirmwareFamily.Should().Be(PrinterFirmwareFamily.Klipper);
        printer.GcodeDialect.Should().Be(PrinterGcodeDialect.Klipper);
    }

    private static AppDbContext CreateDbContext()
    {
        DbContextOptions<AppDbContext> options =
            new DbContextOptionsBuilder<AppDbContext>()
                .UseInMemoryDatabase($"PrintersServiceFirmwareRefreshTests_{Guid.NewGuid():N}")
                .Options;
        return new AppDbContext(options);
    }

    private static Printer CreatePrinter() => new()
    {
        Id = Guid.NewGuid(),
        Name = "Firmware refresh test printer",
        ServerUrl = "http://moonraker.local",
        Backend = (int)PrinterBackend.Moonraker,
    };

    private static Mock<IUnitOfWork> CreateUnitOfWork(Printer printer, out Mock<IPrintersRepository> repository)
    {
        repository = new Mock<IPrintersRepository>();
        repository
            .Setup(r => r.FindByIdAsync(printer.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(printer);
        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.Setup(work => work.Printers).Returns(repository.Object);
        return unitOfWork;
    }

    private static PrintersService CreateService(AppDbContext db, IUnitOfWork unitOfWork) =>
        new(
            unitOfWork,
            db,
            Mock.Of<IBackendClientFactory>(),
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
            Mock.Of<Farm.Infrastructure.Services.Spoolman.IFilamentCoverageSpoolResolver>());
}
