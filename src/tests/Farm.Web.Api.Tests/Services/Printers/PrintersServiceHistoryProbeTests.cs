using System.Net.Sockets;
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
        Func<Task> legacyRead = async () => await service.GetHistoryListAsync(
            printer.Id, 100, null, null, null, "desc", CancellationToken.None);
        await legacyRead.Should().ThrowAsync<NotSupportedException>();
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
                It.IsAny<DateTime?>(),
                It.IsAny<string?>(),
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
                It.IsAny<DateTime?>(),
                It.IsAny<string?>(),
                It.IsAny<PrinterCredential?>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("backend offline"));
        PrintersService service = CreateService(db, printer, historyClient);

        HistoryListProbeResult result = await service.ProbeHistoryListAsync(
            printer.Id, 100, null, null, null, "desc", CancellationToken.None);

        result.Status.Should().Be(HistoryProbeStatus.Unavailable);
        result.History.Should().BeNull();
        result.FailureCode.Should().Be(
            HistoryProbeFailureCodes.TransportUnavailable);
        Func<Task> legacyRead = async () => await service.GetHistoryListAsync(
            printer.Id, 100, null, null, null, "desc", CancellationToken.None);
        await legacyRead.Should().ThrowAsync<HttpRequestException>();
    }

    [Theory]
    [InlineData(typeof(SocketException), typeof(HttpRequestException), HistoryProbeFailureCodes.TransportUnavailable)]
    [InlineData(typeof(TimeoutException), typeof(TimeoutException), HistoryProbeFailureCodes.Timeout)]
    public async Task ProbeHistoryListAsync_ClassifiedFailure_PreservesLegacyReason(
        Type adapterExceptionType,
        Type legacyExceptionType,
        string expectedFailureCode)
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
                It.IsAny<DateTime?>(),
                It.IsAny<string?>(),
                It.IsAny<PrinterCredential?>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync((Exception)Activator.CreateInstance(adapterExceptionType)!);
        PrintersService service = CreateService(db, printer, historyClient);

        HistoryListProbeResult result = await service.ProbeHistoryListAsync(
            printer.Id, 100, null, null, null, "desc", CancellationToken.None);

        result.Status.Should().Be(HistoryProbeStatus.Unavailable);
        result.FailureCode.Should().Be(expectedFailureCode);
        Func<Task> legacyRead = async () => await service.GetHistoryListAsync(
            printer.Id, 100, null, null, null, "desc", CancellationToken.None);
        (await legacyRead.Should().ThrowAsync<Exception>())
            .Which.Should().BeOfType(legacyExceptionType);
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
                It.IsAny<DateTime?>(),
                It.IsAny<string?>(),
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
        var emptyHistory = new HistoryListResponse
        {
            AuthorityEvidence = CompleteEvidence(0),
        };
        Mock<ISupportsHistory> historyClient = CreateHistoryClient();
        historyClient
            .Setup(client => client.GetHistoryListAsync(
                It.IsAny<string>(),
                It.IsAny<int?>(),
                It.IsAny<int?>(),
                It.IsAny<DateTime?>(),
                It.IsAny<DateTime?>(),
                It.IsAny<string?>(),
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

    [Fact]
    public async Task ProbeHistoryListAsync_NonNullWithoutCompletenessEvidenceIsError()
    {
        await using AppDbContext db = CreateDbContext();
        Printer printer = CreatePrinter(PrinterBackend.OctoPrint);
        Mock<ISupportsHistory> historyClient = CreateHistoryClient();
        historyClient
            .Setup(client => client.GetHistoryListAsync(
                It.IsAny<string>(),
                It.IsAny<int?>(),
                It.IsAny<int?>(),
                It.IsAny<DateTime?>(),
                It.IsAny<DateTime?>(),
                It.IsAny<string?>(),
                It.IsAny<PrinterCredential?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HistoryListResponse());
        PrintersService service = CreateService(db, printer, historyClient);

        HistoryListProbeResult result = await service.ProbeHistoryListAsync(
            printer.Id, 100, null, null, null, "desc", CancellationToken.None);

        result.Status.Should().Be(HistoryProbeStatus.Error);
        result.FailureCode.Should().Be("history_completeness_unproven");
    }

    [Fact]
    public async Task ProbeHistoryListAsync_AmbiguousPaginationEvidenceIsError()
    {
        await using AppDbContext db = CreateDbContext();
        Printer printer = CreatePrinter(PrinterBackend.PrusaLink);
        Mock<ISupportsHistory> historyClient = CreateHistoryClient();
        historyClient
            .Setup(client => client.GetHistoryListAsync(
                It.IsAny<string>(),
                It.IsAny<int?>(),
                It.IsAny<int?>(),
                It.IsAny<DateTime?>(),
                It.IsAny<DateTime?>(),
                It.IsAny<string?>(),
                It.IsAny<PrinterCredential?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HistoryListResponse
            {
                Count = 100,
                Jobs = Enumerable.Range(0, 100)
                    .Select(index => new HistoryJob
                    {
                        JobId = $"job-{index}",
                        Filename = $"job-{index}.gcode",
                        Status = "completed",
                    })
                    .ToArray(),
                AuthorityEvidence = new HistoryListAuthorityEvidence(
                    "prusalink",
                    100,
                    100,
                    StartsAtBeginning: true,
                    HasUnambiguousEnd: false),
            });
        PrintersService service = CreateService(db, printer, historyClient);

        HistoryListProbeResult result = await service.ProbeHistoryListAsync(
            printer.Id, 100, null, null, null, "desc", CancellationToken.None);

        result.Status.Should().Be(HistoryProbeStatus.Error);
        result.FailureCode.Should().Be("history_completeness_unproven");
    }

    [Fact]
    public async Task ProbeHistoryJobAsync_ValidDetailIsFound()
    {
        await using AppDbContext db = CreateDbContext();
        Printer printer = CreatePrinter(PrinterBackend.Moonraker);
        Mock<ISupportsHistory> historyClient = CreateHistoryClient();
        historyClient
            .Setup(client => client.GetHistoryJobAsync(
                It.IsAny<string>(),
                "provider-job",
                It.IsAny<PrinterCredential?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HistoryJob
            {
                JobId = "provider-job",
                Filename = "calibration.gcode",
            });
        PrintersService service = CreateService(db, printer, historyClient);

        HistoryJobProbeResult result = await service.ProbeHistoryJobAsync(
            printer.Id, "provider-job", CancellationToken.None);

        result.Status.Should().Be(HistoryDetailProbeStatus.Found);
        result.Job!.JobId.Should().Be("provider-job");
    }

    [Fact]
    public async Task ProbeHistoryJobAsync_ExplicitNotFoundIsAuthoritative()
    {
        await using AppDbContext db = CreateDbContext();
        Printer printer = CreatePrinter(PrinterBackend.Moonraker);
        Mock<ISupportsHistory> historyClient = CreateHistoryClient();
        historyClient
            .Setup(client => client.GetHistoryJobAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<PrinterCredential?>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HistoryJobNotFoundException("missing"));
        PrintersService service = CreateService(db, printer, historyClient);

        HistoryJobProbeResult result = await service.ProbeHistoryJobAsync(
            printer.Id, "missing", CancellationToken.None);

        result.Status.Should().Be(HistoryDetailProbeStatus.NotFound);
        result.Job.Should().BeNull();
    }

    [Fact]
    public async Task ProbeHistoryJobAsync_UnsupportedBackendIsNonAuthoritative()
    {
        await using AppDbContext db = CreateDbContext();
        Printer printer = CreatePrinter(PrinterBackend.FlashForge);
        PrintersService service = CreateService(db, printer, historyClient: null);

        HistoryJobProbeResult result = await service.ProbeHistoryJobAsync(
            printer.Id, "provider-job", CancellationToken.None);

        result.Status.Should().Be(HistoryDetailProbeStatus.Unsupported);
        result.Job.Should().BeNull();
        Func<Task> legacyRead = async () => await service.GetHistoryJobAsync(
            printer.Id,
            "provider-job",
            CancellationToken.None);
        await legacyRead.Should().ThrowAsync<NotSupportedException>();
    }

    [Fact]
    public async Task ProbeHistoryJobAsync_NullAdapterResponseIsUnavailable()
    {
        await using AppDbContext db = CreateDbContext();
        Printer printer = CreatePrinter(PrinterBackend.Moonraker);
        Mock<ISupportsHistory> historyClient = CreateHistoryClient();
        historyClient
            .Setup(client => client.GetHistoryJobAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<PrinterCredential?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((HistoryJob?)null);
        PrintersService service = CreateService(db, printer, historyClient);

        HistoryJobProbeResult result = await service.ProbeHistoryJobAsync(
            printer.Id, "provider-job", CancellationToken.None);

        result.Status.Should().Be(HistoryDetailProbeStatus.Unavailable);
        result.Job.Should().BeNull();
        Func<Task> legacyRead = async () => await service.GetHistoryJobAsync(
            printer.Id,
            "provider-job",
            CancellationToken.None);
        await legacyRead.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task ProbeHistoryJobAsync_MissingAdapterJobIdIsError()
    {
        await using AppDbContext db = CreateDbContext();
        Printer printer = CreatePrinter(PrinterBackend.Moonraker);
        Mock<ISupportsHistory> historyClient = CreateHistoryClient();
        historyClient
            .Setup(client => client.GetHistoryJobAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<PrinterCredential?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HistoryJob { Filename = "calibration.gcode" });
        PrintersService service = CreateService(db, printer, historyClient);

        HistoryJobProbeResult result = await service.ProbeHistoryJobAsync(
            printer.Id, "provider-job", CancellationToken.None);

        result.Status.Should().Be(HistoryDetailProbeStatus.Error);
        result.FailureCode.Should().Be("history_job_id_missing");
    }

    [Fact]
    public async Task ProbeHistoryJobAsync_MismatchedAdapterJobIdIsError()
    {
        await using AppDbContext db = CreateDbContext();
        Printer printer = CreatePrinter(PrinterBackend.Moonraker);
        Mock<ISupportsHistory> historyClient = CreateHistoryClient();
        historyClient
            .Setup(client => client.GetHistoryJobAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<PrinterCredential?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HistoryJob
            {
                JobId = "different-provider-job",
                Filename = "calibration.gcode",
            });
        PrintersService service = CreateService(db, printer, historyClient);

        HistoryJobProbeResult result = await service.ProbeHistoryJobAsync(
            printer.Id, "provider-job", CancellationToken.None);

        result.Status.Should().Be(HistoryDetailProbeStatus.Error);
        result.FailureCode.Should().Be("history_job_id_mismatch");
    }

    [Theory]
    [InlineData(typeof(HttpRequestException), HistoryDetailProbeStatus.Unavailable)]
    [InlineData(typeof(InvalidDataException), HistoryDetailProbeStatus.Error)]
    [InlineData(typeof(KeyNotFoundException), HistoryDetailProbeStatus.Error)]
    public async Task ProbeHistoryJobAsync_AdapterFailureIsNonAuthoritative(
        Type exceptionType,
        HistoryDetailProbeStatus expectedStatus)
    {
        await using AppDbContext db = CreateDbContext();
        Printer printer = CreatePrinter(PrinterBackend.Moonraker);
        Mock<ISupportsHistory> historyClient = CreateHistoryClient();
        historyClient
            .Setup(client => client.GetHistoryJobAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<PrinterCredential?>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync((Exception)Activator.CreateInstance(exceptionType)!);
        PrintersService service = CreateService(db, printer, historyClient);

        HistoryJobProbeResult result = await service.ProbeHistoryJobAsync(
            printer.Id, "provider-job", CancellationToken.None);

        result.Status.Should().Be(expectedStatus);
        result.Job.Should().BeNull();
    }

    [Theory]
    [InlineData(typeof(HttpRequestException), typeof(HttpRequestException), HistoryProbeFailureCodes.TransportUnavailable)]
    [InlineData(typeof(SocketException), typeof(HttpRequestException), HistoryProbeFailureCodes.TransportUnavailable)]
    [InlineData(typeof(TimeoutException), typeof(TimeoutException), HistoryProbeFailureCodes.Timeout)]
    public async Task ProbeHistoryJobAsync_ClassifiedFailure_PreservesLegacyReason(
        Type adapterExceptionType,
        Type legacyExceptionType,
        string expectedFailureCode)
    {
        await using AppDbContext db = CreateDbContext();
        Printer printer = CreatePrinter(PrinterBackend.Moonraker);
        Mock<ISupportsHistory> historyClient = CreateHistoryClient();
        historyClient
            .Setup(client => client.GetHistoryJobAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<PrinterCredential?>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync((Exception)Activator.CreateInstance(adapterExceptionType)!);
        PrintersService service = CreateService(db, printer, historyClient);

        HistoryJobProbeResult result = await service.ProbeHistoryJobAsync(
            printer.Id, "provider-job", CancellationToken.None);

        result.Status.Should().Be(HistoryDetailProbeStatus.Unavailable);
        result.FailureCode.Should().Be(expectedFailureCode);
        Func<Task> legacyRead = async () => await service.GetHistoryJobAsync(
            printer.Id,
            "provider-job",
            CancellationToken.None);
        (await legacyRead.Should().ThrowAsync<Exception>())
            .Which.Should().BeOfType(legacyExceptionType);
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

    private static HistoryListAuthorityEvidence CompleteEvidence(int count) =>
        new(
            "test",
            count,
            count,
            StartsAtBeginning: true,
            HasUnambiguousEnd: true);

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
