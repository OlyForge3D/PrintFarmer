using Farm.Infrastructure;
using Farm.Infrastructure.Contracts.Printers;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Printers;
using Farm.Infrastructure.Repositories.UnitOfWork;
using Farm.Infrastructure.Services.Printers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Farm.Infrastructure.Tests.Services.Printers;

public sealed class PrintersServiceCurrentJobThumbnailTests
{
    [Fact]
    public async Task GetCurrentJobThumbnailAsync_ActiveSupportedBackend_RetrievesThumbnailAndRejectsStaleToken()
    {
        await using AppDbContext db = CreateDbContext();
        Printer printer = CreatePrinter();
        var backend = new Mock<IBackendClient>();
        var jobControl = backend.As<ISupportsJobControl>();
        var thumbnailClient = backend.As<ISupportsCurrentJobThumbnail>();
        PrinterJob job = new(
            "printing",
            25,
            "same-name.gcode",
            "http://printer.internal/server/files/gcodes/thumb.png",
            ThumbnailCacheIdentity: "job-start:123");
        jobControl
            .Setup(client => client.GetJobAsync(
                printer.BackendUrl,
                printer.Credential,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(job);
        var expectedImage = new HistoryThumbnailContent([0x89, 0x50], "image/png");
        thumbnailClient
            .Setup(client => client.GetCurrentJobThumbnailAsync(
                printer.BackendUrl,
                job.ThumbnailUrl!,
                printer.Credential,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedImage);
        PrintersService service = CreateService(db, printer, backend.Object);
        string validToken = PrinterThumbnailUrl.GetCacheToken(
            job.JobName!,
            job.ThumbnailUrl!,
            job.ThumbnailCacheIdentity);

        HistoryThumbnailContent? result = await service.GetCurrentJobThumbnailAsync(
            printer.Id,
            validToken.ToUpperInvariant(),
            CancellationToken.None);
        HistoryThumbnailContent? stale = await service.GetCurrentJobThumbnailAsync(
            printer.Id,
            PrinterThumbnailUrl.GetCacheToken(job.JobName!, job.ThumbnailUrl!, "older-job"),
            CancellationToken.None);

        Assert.Same(expectedImage, result);
        Assert.Null(stale);
        thumbnailClient.Verify(
            client => client.GetCurrentJobThumbnailAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<PrinterCredential?>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task GetCurrentJobThumbnailAsync_IdleJob_ReturnsNullWithoutFetchingImage()
    {
        await using AppDbContext db = CreateDbContext();
        Printer printer = CreatePrinter();
        var backend = new Mock<IBackendClient>();
        Mock<ISupportsJobControl> jobControl = backend.As<ISupportsJobControl>();
        Mock<ISupportsCurrentJobThumbnail> thumbnailClient = backend.As<ISupportsCurrentJobThumbnail>();
        jobControl
            .Setup(client => client.GetJobAsync(
                printer.BackendUrl,
                printer.Credential,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PrinterJob("standby", null, null, null));
        PrintersService service = CreateService(db, printer, backend.Object);

        HistoryThumbnailContent? result = await service.GetCurrentJobThumbnailAsync(
            printer.Id,
            cacheToken: null,
            CancellationToken.None);

        Assert.Null(result);
        thumbnailClient.Verify(
            client => client.GetCurrentJobThumbnailAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<PrinterCredential?>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    private static AppDbContext CreateDbContext()
    {
        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"CurrentJobThumbnail_{Guid.NewGuid():N}")
            .Options;
        return new AppDbContext(options);
    }

    private static Printer CreatePrinter() => new()
    {
        Id = Guid.NewGuid(),
        Name = "Thumbnail fixture",
        ServerUrl = "http://printer.internal/",
        Backend = (int)PrinterBackend.Moonraker,
        ApiKey = "thumbnail-test-key",
    };

    private static PrintersService CreateService(
        AppDbContext db,
        Printer printer,
        IBackendClient backend)
    {
        var printersRepository = new Mock<IPrintersRepository>();
        printersRepository
            .Setup(repository => repository.FindByIdAsync(
                printer.Id,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(printer);
        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.Setup(work => work.Printers).Returns(printersRepository.Object);
        var backendFactory = new Mock<IBackendClientFactory>();
        backendFactory
            .Setup(factory => factory.GetClient(PrinterBackend.Moonraker))
            .Returns(backend);

        return new PrintersService(
            unitOfWork.Object,
            db,
            backendFactory.Object,
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
}
