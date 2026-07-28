using Farm.Backend.Plugin.Core;
using Farm.Backend.Plugin.FlashForge;
using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Printers;
using Farm.Infrastructure.Repositories.UnitOfWork;
using Farm.Infrastructure.Services.Printers;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Farm.Web.Api.Tests.Services.Printers;

public sealed class PrintersServiceHistoryProbeTests
{
    [Fact]
    public async Task ProbeHistoryListAsync_FlashForgeAdapterIsUnsupported()
    {
        typeof(ISupportsHistory).IsAssignableFrom(typeof(FlashForgeClient))
            .Should().BeFalse();
        await using AppDbContext db = CreateDbContext();
        Printer printer = CreatePrinter(PrinterBackend.FlashForge);
        PrintersService service = CreateService(db, printer, historyClient: null);

        HistoryListProbeResult result = await service.ProbeHistoryListAsync(
            printer.Id, 100, null, null, null, "desc", CancellationToken.None);

        result.Status.Should().Be(HistoryProbeStatus.Unsupported);
        result.History.Should().BeNull();
    }

    [Fact]
    public async Task ProbeHistoryListAsync_NullAdapterResponseIsUnavailable()
    {
        await using AppDbContext db = CreateDbContext();
        Printer printer = CreatePrinter(PrinterBackend.Moonraker);
        Mock<ISupportsHistory> historyClient = CreateHistoryClient();
        historyClient
            .Setup(client => client.GetHistoryListAsync(
                It.IsAny<string>(),
                It.IsAny<int?>(),
                It.IsAny<int?>(),
                It.IsAny<DateTime?>(),
                It.IsAny<PrinterCredential?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((HistoryListResponse?)null);
        PrintersService service = CreateService(db, printer, historyClient);

        HistoryListProbeResult result = await service.ProbeHistoryListAsync(
            printer.Id, 100, null, null, null, "desc", CancellationToken.None);

        result.Status.Should().Be(HistoryProbeStatus.Unavailable);
        result.History.Should().BeNull();
        Func<Task> legacyRead = async () => await service.GetHistoryListAsync(
            printer.Id, 100, null, null, null, "desc", CancellationToken.None);
        await legacyRead.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task ProbeHistoryListAsync_TransportFailureIsUnavailable()
    {
        await using AppDbContext db = CreateDbContext();
        Printer printer = CreatePrinter(PrinterBackend.Moonraker);
        Mock<ISupportsHistory> historyClient = CreateHistoryClient();
        historyClient
            .Setup(client => client.GetHistoryListAsync(
                It.IsAny<string>(),
                It.IsAny<int?>(),
                It.IsAny<int?>(),
                It.IsAny<DateTime?>(),
                It.IsAny<PrinterCredential?>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("backend offline"));
        PrintersService service = CreateService(db, printer, historyClient);

        HistoryListProbeResult result = await service.ProbeHistoryListAsync(
            printer.Id, 100, null, null, null, "desc", CancellationToken.None);

        result.Status.Should().Be(HistoryProbeStatus.Unavailable);
        result.History.Should().BeNull();
    }

    [Fact]
    public async Task ProbeHistoryListAsync_UnexpectedAdapterFailureIsError()
    {
        await using AppDbContext db = CreateDbContext();
        Printer printer = CreatePrinter(PrinterBackend.Moonraker);
        Mock<ISupportsHistory> historyClient = CreateHistoryClient();
        historyClient
            .Setup(client => client.GetHistoryListAsync(
                It.IsAny<string>(),
                It.IsAny<int?>(),
                It.IsAny<int?>(),
                It.IsAny<DateTime?>(),
                It.IsAny<PrinterCredential?>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new FormatException("malformed backend response"));
        PrintersService service = CreateService(db, printer, historyClient);

        HistoryListProbeResult result = await service.ProbeHistoryListAsync(
            printer.Id, 100, null, null, null, "desc", CancellationToken.None);

        result.Status.Should().Be(HistoryProbeStatus.Error);
        result.History.Should().BeNull();
    }

    [Fact]
    public async Task ProbeHistoryListAsync_EmptyAdapterResponseIsAuthoritative()
    {
        await using AppDbContext db = CreateDbContext();
        Printer printer = CreatePrinter(PrinterBackend.Moonraker);
        var emptyHistory = new HistoryListResponse();
        Mock<ISupportsHistory> historyClient = CreateHistoryClient();
        historyClient
            .Setup(client => client.GetHistoryListAsync(
                It.IsAny<string>(),
                It.IsAny<int?>(),
                It.IsAny<int?>(),
                It.IsAny<DateTime?>(),
                It.IsAny<PrinterCredential?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(emptyHistory);
        PrintersService service = CreateService(db, printer, historyClient);

        HistoryListProbeResult result = await service.ProbeHistoryListAsync(
            printer.Id, 100, null, null, null, "desc", CancellationToken.None);

        result.Status.Should().Be(HistoryProbeStatus.Authoritative);
        result.History.Should().BeSameAs(emptyHistory);
        (await service.GetHistoryListAsync(
            printer.Id,
            100,
            null,
            null,
            null,
            "desc",
            CancellationToken.None)).Should().BeSameAs(emptyHistory);
    }

    private static AppDbContext CreateDbContext()
    {
        DbContextOptions<AppDbContext> options =
            new DbContextOptionsBuilder<AppDbContext>()
                .UseInMemoryDatabase(
                    $"PrintersServiceHistoryProbeTests_{Guid.NewGuid():N}")
                .Options;
        return new AppDbContext(options);
    }

    private static Printer CreatePrinter(PrinterBackend backend) => new()
    {
        Id = Guid.NewGuid(),
        Name = $"{backend} history probe",
        ServerUrl = $"http://{backend.ToString().ToLowerInvariant()}.local",
        Backend = (int)backend,
    };

    private static Mock<ISupportsHistory> CreateHistoryClient() => new();

    private static PrintersService CreateService(
        AppDbContext db,
        Printer printer,
        Mock<ISupportsHistory>? historyClient)
    {
        var printersRepository = new Mock<IPrintersRepository>();
        printersRepository
            .Setup(repository => repository.FindByIdAsync(
                printer.Id,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(printer);
        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.Setup(work => work.Printers).Returns(printersRepository.Object);
        var capabilityFactory = new Mock<IBackendCapabilityFactory>();
        if (historyClient is null)
        {
            ISupportsHistory? unsupported = null;
            capabilityFactory
                .Setup(factory => factory.TryGetHistoryClientTyped(
                    (PrinterBackend)printer.Backend,
                    out unsupported))
                .Returns(false);
        }
        else
        {
            ISupportsHistory? supported = historyClient.Object;
            capabilityFactory
                .Setup(factory => factory.TryGetHistoryClientTyped(
                    (PrinterBackend)printer.Backend,
                    out supported))
                .Returns(true);
        }

        return new PrintersService(
            unitOfWork.Object,
            db,
            Mock.Of<IBackendClientFactory>(),
            capabilityFactory.Object,
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
            Mock.Of<Farm.Infrastructure.Services.StorageManagement.IStoragePathService>());
    }
}
