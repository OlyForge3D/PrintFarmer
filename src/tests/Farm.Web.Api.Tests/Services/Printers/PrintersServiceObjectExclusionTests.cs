using Farm.Backend.Plugin.Core;
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

namespace Farm.Web.Api.Tests.Services.Printers;

public class PrintersServiceObjectExclusionTests
{
    [Fact]
    public async Task ExcludePrintJobObjectAsync_WhenNoActiveJob_ReturnsFailureAndDoesNotSend()
    {
        await using AppDbContext db = CreateDbContext();
        Printer printer = CreatePrinter();
        Mock<ISupportsObjectExclusion> objectExclusionClient = CreateObjectExclusionClient(
            new PrintJobObjectListDto(printer.Id, null, Array.Empty<PrintJobObjectDto>()));
        PrintersService service = CreateService(db, printer, objectExclusionClient);

        CommandResult result = await service.ExcludePrintJobObjectAsync(printer.Id, "cube", CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Message.Should().Be("No active printing job is available for object exclusion.");
        objectExclusionClient.Verify(c => c.ExcludeObjectAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExcludePrintJobObjectAsync_WhenObjectIsUnknown_ReturnsFailureAndDoesNotSend()
    {
        await using AppDbContext db = CreateDbContext();
        Printer printer = CreatePrinter();
        Mock<ISupportsObjectExclusion> objectExclusionClient = CreateObjectExclusionClient(
            new PrintJobObjectListDto(
                printer.Id,
                "plate.gcode",
                new[] { new PrintJobObjectDto("cube", IsExcluded: false, IsCurrent: true) }));
        PrintersService service = CreateService(db, printer, objectExclusionClient);

        CommandResult result = await service.ExcludePrintJobObjectAsync(printer.Id, "sphere", CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Message.Should().Be("Object 'sphere' was not found in the current print job.");
        objectExclusionClient.Verify(c => c.ExcludeObjectAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExcludePrintJobObjectAsync_WhenObjectIsAlreadyExcluded_ReturnsFailureAndDoesNotSend()
    {
        await using AppDbContext db = CreateDbContext();
        Printer printer = CreatePrinter();
        Mock<ISupportsObjectExclusion> objectExclusionClient = CreateObjectExclusionClient(
            new PrintJobObjectListDto(
                printer.Id,
                "plate.gcode",
                new[] { new PrintJobObjectDto("cube", IsExcluded: true, IsCurrent: false) }));
        PrintersService service = CreateService(db, printer, objectExclusionClient);

        CommandResult result = await service.ExcludePrintJobObjectAsync(printer.Id, "cube", CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Message.Should().Be("Object 'cube' is already excluded.");
        objectExclusionClient.Verify(c => c.ExcludeObjectAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExcludePrintJobObjectAsync_WhenObjectNameHasSurroundingWhitespace_SendsExactName()
    {
        await using AppDbContext db = CreateDbContext();
        Printer printer = CreatePrinter();
        const string objectName = " cube ";
        Mock<ISupportsObjectExclusion> objectExclusionClient = CreateObjectExclusionClient(
            new PrintJobObjectListDto(
                printer.Id,
                "plate.gcode",
                new[] { new PrintJobObjectDto(objectName, IsExcluded: false, IsCurrent: true) }));
        objectExclusionClient
            .Setup(c => c.ExcludeObjectAsync(It.IsAny<string>(), objectName, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        PrintersService service = CreateService(db, printer, objectExclusionClient);

        CommandResult result = await service.ExcludePrintJobObjectAsync(printer.Id, objectName, CancellationToken.None);

        result.Success.Should().BeTrue();
        result.Message.Should().Be("Object ' cube ' skipped");
        objectExclusionClient.Verify(c => c.ExcludeObjectAsync(It.IsAny<string>(), objectName, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExcludePrintJobObjectAsync_WhenPausedJobHasObject_SendsCommand()
    {
        await using AppDbContext db = CreateDbContext();
        Printer printer = CreatePrinter();
        Mock<ISupportsObjectExclusion> objectExclusionClient = CreateObjectExclusionClient(
            new PrintJobObjectListDto(
                printer.Id,
                "paused-plate.gcode",
                new[] { new PrintJobObjectDto("cube", IsExcluded: false, IsCurrent: true) }));
        objectExclusionClient
            .Setup(c => c.ExcludeObjectAsync(It.IsAny<string>(), "cube", It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        PrintersService service = CreateService(db, printer, objectExclusionClient);

        CommandResult result = await service.ExcludePrintJobObjectAsync(printer.Id, "cube", CancellationToken.None);

        result.Success.Should().BeTrue();
        result.Message.Should().Be("Object 'cube' skipped");
        objectExclusionClient.Verify(c => c.ExcludeObjectAsync(It.IsAny<string>(), "cube", It.IsAny<CancellationToken>()), Times.Once);
    }

    private static AppDbContext CreateDbContext()
    {
        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"PrintersServiceObjectExclusionTests_{Guid.NewGuid():N}")
            .Options;

        return new AppDbContext(options);
    }

    private static Printer CreatePrinter() => new()
    {
        Id = Guid.NewGuid(),
        Name = "Moonraker",
        ServerUrl = "http://moonraker.local",
        FrontendPort = 7125,
        Backend = (int)PrinterBackend.Moonraker,
    };

    private static Mock<ISupportsObjectExclusion> CreateObjectExclusionClient(PrintJobObjectListDto? objects)
    {
        var backendClient = new Mock<IBackendClient>();
        Mock<ISupportsObjectExclusion> objectExclusionClient = backendClient.As<ISupportsObjectExclusion>();
        objectExclusionClient
            .Setup(c => c.GetCurrentJobObjectsAsync(It.IsAny<string>(), It.IsAny<PrinterCredential?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(objects);

        return objectExclusionClient;
    }

    private static PrintersService CreateService(
        AppDbContext db,
        Printer printer,
        Mock<ISupportsObjectExclusion> objectExclusionClient)
    {
        var printersRepository = new Mock<IPrintersRepository>();
        printersRepository
            .Setup(r => r.FindByIdAsync(printer.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(printer);

        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.Setup(u => u.Printers).Returns(printersRepository.Object);

        var backendClientFactory = new Mock<IBackendClientFactory>();
        backendClientFactory
            .Setup(f => f.GetClient(PrinterBackend.Moonraker))
            .Returns((IBackendClient)objectExclusionClient.Object);

        return new PrintersService(
            unitOfWork.Object,
            db,
            backendClientFactory.Object,
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
            Mock.Of<Farm.Infrastructure.Services.StorageManagement.IStoragePathService>());
    }
}
