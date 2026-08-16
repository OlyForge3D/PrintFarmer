using System.Net;
using System.Text;
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
/// Tests for the on-demand firmware detection producer
/// <see cref="PrintersService.DetectFirmwareIdentityAsync"/>.
///
/// The persisted firmware columns the calibration gate reads previously had no operator-reachable
/// writer: onboarding wrote them once, and the only refresh path was a side effect of a discovery
/// scan posting back a matching ServerUrl, throttled to a multi-hour cadence. A printer registered
/// before firmware detection existed therefore could never reach a calibratable state. These tests
/// pin the behaviour that closes that gap.
/// </summary>
public sealed class PrintersServiceFirmwareDetectTests
{
    private const string PrinterInfoPayload =
        """
        {"result":{"state_message":"Printer is ready","klipper_path":"/home/pi/klipper","hostname":"qp4","software_version":"v0.12.0-321"}}
        """;

    private const string PrinterInfoPayloadWithoutVersion =
        """
        {"result":{"state_message":"Printer is ready","klipper_path":"/home/pi/klipper","hostname":"qp4"}}
        """;

    [Fact]
    public async Task DetectFirmwareIdentityAsync_PrinterNotFound_ReturnsPrinterNotFound()
    {
        await using AppDbContext db = CreateDbContext();
        var repository = new Mock<IPrintersRepository>();
        repository
            .Setup(r => r.FindByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Printer?)null);
        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.Setup(work => work.Printers).Returns(repository.Object);
        PrintersService service = CreateService(db, unitOfWork.Object, RespondWith(PrinterInfoPayload));

        FirmwareDetectionResult result =
            await service.DetectFirmwareIdentityAsync(Guid.NewGuid(), CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Failure.Should().Be(FirmwareDetectionFailure.PrinterNotFound);
        unitOfWork.Verify(work => work.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DetectFirmwareIdentityAsync_NonMoonrakerBackend_ReturnsBackendNotSupported()
    {
        await using AppDbContext db = CreateDbContext();
        Printer printer = CreatePrinter();
        printer.Backend = (int)PrinterBackend.PrusaLink;
        var unitOfWork = CreateUnitOfWork(printer);
        PrintersService service = CreateService(db, unitOfWork.Object, RespondWith(PrinterInfoPayload));

        FirmwareDetectionResult result =
            await service.DetectFirmwareIdentityAsync(printer.Id, CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Failure.Should().Be(FirmwareDetectionFailure.BackendNotSupported);
        printer.FirmwareFamily.Should().Be(PrinterFirmwareFamily.Unknown);
        unitOfWork.Verify(work => work.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DetectFirmwareIdentityAsync_ServerUrlNotAbsolute_ReturnsServerUrlInvalid()
    {
        await using AppDbContext db = CreateDbContext();
        Printer printer = CreatePrinter();
        printer.ServerUrl = "moonraker.local";
        var unitOfWork = CreateUnitOfWork(printer);
        PrintersService service = CreateService(db, unitOfWork.Object, RespondWith(PrinterInfoPayload));

        FirmwareDetectionResult result =
            await service.DetectFirmwareIdentityAsync(printer.Id, CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Failure.Should().Be(FirmwareDetectionFailure.ServerUrlInvalid);
        unitOfWork.Verify(work => work.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DetectFirmwareIdentityAsync_ProbeUnreachable_ReturnsProbeFailed()
    {
        await using AppDbContext db = CreateDbContext();
        Printer printer = CreatePrinter();
        var unitOfWork = CreateUnitOfWork(printer);
        PrintersService service = CreateService(db, unitOfWork.Object, Throwing());

        FirmwareDetectionResult result =
            await service.DetectFirmwareIdentityAsync(printer.Id, CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Failure.Should().Be(FirmwareDetectionFailure.ProbeFailed);
        printer.FirmwareFamily.Should().Be(PrinterFirmwareFamily.Unknown);
        printer.FirmwareDetectedAtUtc.Should().BeNull();
        unitOfWork.Verify(work => work.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DetectFirmwareIdentityAsync_ProbeSucceeds_PersistsIdentityButDoesNotMarkVerified()
    {
        await using AppDbContext db = CreateDbContext();
        Printer printer = CreatePrinter();
        var unitOfWork = CreateUnitOfWork(printer);
        PrintersService service = CreateService(db, unitOfWork.Object, RespondWith(PrinterInfoPayload));

        FirmwareDetectionResult result =
            await service.DetectFirmwareIdentityAsync(printer.Id, CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        result.Failure.Should().Be(FirmwareDetectionFailure.None);

        printer.FirmwareFamily.Should().Be(PrinterFirmwareFamily.Klipper);
        printer.GcodeDialect.Should().Be(PrinterGcodeDialect.Klipper);
        printer.FirmwareDetectionSource.Should().Be(FirmwareDetectionSource.Printer);
        printer.FirmwareVersion.Should().Be("v0.12.0-321");
        printer.FirmwareDetectionVersion.Should().Be(MoonrakerOnboardingResolver.FirmwareProbeVersion);
        printer.FirmwareDetectionConfidence.Should().NotBeNull();
        printer.FirmwareDetectedAtUtc.Should().NotBeNull();

        // Detection populates facts; only a human attests them (#1613 AC #3). If this ever flips,
        // the "Mark firmware verified" action becomes a no-op the operator can silently skip.
        printer.FirmwareIdentityVerified.Should().BeFalse();
        result.IdentityVerified.Should().BeFalse();

        unitOfWork.Verify(work => work.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DetectFirmwareIdentityAsync_ProbeOmitsSoftwareVersion_PreservesRecordedVersion()
    {
        await using AppDbContext db = CreateDbContext();
        Printer printer = CreatePrinter();
        printer.FirmwareVersion = "v0.11.0-previously-recorded";
        var unitOfWork = CreateUnitOfWork(printer);
        PrintersService service =
            CreateService(db, unitOfWork.Object, RespondWith(PrinterInfoPayloadWithoutVersion));

        FirmwareDetectionResult result =
            await service.DetectFirmwareIdentityAsync(printer.Id, CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        printer.FirmwareFamily.Should().Be(PrinterFirmwareFamily.Klipper);

        // A probe that cannot read software_version must not erase a version recorded earlier,
        // because firmware.version is itself one of the calibration gate's required inputs.
        printer.FirmwareVersion.Should().Be("v0.11.0-previously-recorded");
    }

    /// <summary>
    /// Detection is deliberately not subject to the re-probe cadence guard that
    /// <see cref="PrintersService.RefreshDetectedFirmwareIdentityAsync"/> applies: an operator who
    /// just fixed their printer must not be told to wait six hours.
    ///
    /// The refresh assertion is the control. Both halves run the same freshly-detected printer
    /// through the same cadence window, so the contrast is what carries the meaning — without it,
    /// a detect call that updated for some unrelated reason would look identical to one that
    /// correctly bypassed the throttle.
    /// </summary>
    [Fact]
    public async Task DetectFirmwareIdentityAsync_RecentlyDetected_BypassesTheReprobeCadenceGuard()
    {
        await using AppDbContext db = CreateDbContext();
        Printer printer = CreatePrinter();
        printer.FirmwareVersion = "v0.10.0-stale";
        printer.FirmwareDetectedAtUtc = DateTime.UtcNow; // well inside the default 6h cadence
        var unitOfWork = CreateUnitOfWork(printer);
        PrintersService service = CreateService(db, unitOfWork.Object, RespondWith(PrinterInfoPayload));

        // Control: the throttled refresh path declines the identical printer in the same window.
        bool refreshed = await service.RefreshDetectedFirmwareIdentityAsync(
            printer.Id,
            new DiscoveredPrinterDto
            {
                FirmwareFamily = PrinterFirmwareFamily.Klipper,
                FirmwareVersion = "v0.12.0-321",
            },
            CancellationToken.None);

        refreshed.Should().BeFalse();
        printer.FirmwareVersion.Should().Be("v0.10.0-stale");

        // Subject: the on-demand path proceeds anyway.
        FirmwareDetectionResult result =
            await service.DetectFirmwareIdentityAsync(printer.Id, CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        printer.FirmwareVersion.Should().Be("v0.12.0-321");
    }

    private static AppDbContext CreateDbContext()
    {
        DbContextOptions<AppDbContext> options =
            new DbContextOptionsBuilder<AppDbContext>()
                .UseInMemoryDatabase($"PrintersServiceFirmwareDetectTests_{Guid.NewGuid():N}")
                .Options;
        return new AppDbContext(options);
    }

    private static Printer CreatePrinter() => new()
    {
        Id = Guid.NewGuid(),
        Name = "Firmware detect test printer",
        ServerUrl = "http://moonraker.local",
        BackendPort = MoonrakerOnboardingResolver.DefaultMoonrakerPort,
        Backend = (int)PrinterBackend.Moonraker,
    };

    private static Mock<IUnitOfWork> CreateUnitOfWork(Printer printer)
    {
        var repository = new Mock<IPrintersRepository>();
        repository
            .Setup(r => r.FindByIdAsync(printer.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(printer);
        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.Setup(work => work.Printers).Returns(repository.Object);
        return unitOfWork;
    }

    private static HttpMessageHandler RespondWith(string payload) =>
        new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        });

    private static HttpMessageHandler Throwing() =>
        new StubHandler(_ => throw new HttpRequestException("connection refused"));

    private static PrintersService CreateService(
        AppDbContext db,
        IUnitOfWork unitOfWork,
        HttpMessageHandler handler)
    {
        var httpClientFactory = new Mock<IHttpClientFactory>();
        httpClientFactory
            .Setup(f => f.CreateClient(It.IsAny<string>()))
            .Returns(() => new HttpClient(handler, disposeHandler: false));

        return new PrintersService(
            unitOfWork,
            db,
            Mock.Of<IBackendClientFactory>(),
            Mock.Of<IBackendCapabilityFactory>(),
            Mock.Of<Farm.Infrastructure.Services.Catalog.ICatalogService>(),
            httpClientFactory.Object,
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

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(responder(request));
    }
}
