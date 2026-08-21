using Farm.Infrastructure;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Catalog;
using Farm.Infrastructure.Repositories.Printers;
using Farm.Infrastructure.Repositories.Queue;
using Farm.Infrastructure.Repositories.UnitOfWork;
using Farm.Infrastructure.Services.Printers;
using Farm.Web.Api.Services.Startup;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Services.Startup;

public sealed class MoonrakerEmulatorSeederTests
{
    [Fact]
    public async Task ExecuteAsync_EnabledSettings_SeedsRealMoonrakerPrinters()
    {
        var printers = new Mock<IPrintersRepository>(MockBehavior.Strict);
        printers
            .Setup(repository => repository.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        List<Printer> added = [];
        printers
            .Setup(repository => repository.AddAsync(
                It.IsAny<Printer>(),
                It.IsAny<CancellationToken>()))
            .Callback<Printer, CancellationToken>((printer, _) => added.Add(printer))
            .Returns(Task.CompletedTask);

        TaskCompletionSource seeded = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var unitOfWork = new Mock<IUnitOfWork>(MockBehavior.Strict);
        unitOfWork.SetupGet(value => value.Printers).Returns(printers.Object);

        var queue = new Mock<IQueueRepository>(MockBehavior.Strict);
        queue
            .Setup(repository => repository.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        List<PrintJob> jobs = [];
        queue
            .Setup(repository => repository.AddWithoutSaveAsync(
                It.IsAny<PrintJob>(),
                It.IsAny<CancellationToken>()))
            .Callback<PrintJob, CancellationToken>((job, _) => jobs.Add(job))
            .Returns(Task.CompletedTask);
        unitOfWork.SetupGet(value => value.Queue).Returns(queue.Object);

        unitOfWork
            .Setup(value => value.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .Callback(() => seeded.TrySetResult())
            .ReturnsAsync(added.Count);
        unitOfWork.Setup(value => value.Dispose());

        Guid manufacturerId = Guid.NewGuid();
        Guid modelId = Guid.NewGuid();
        var catalog = new Mock<ICatalogRepository>(MockBehavior.Strict);
        catalog
            .Setup(repository => repository.GetUnknownManufacturerIdAsync(
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Guid.NewGuid());
        catalog
            .Setup(repository => repository.GetUnknownModelIdAsync(
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Guid.NewGuid());
        catalog
            .Setup(repository => repository.FindManufacturerByNameAsync(
                "Voron",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Manufacturer
            {
                Id = manufacturerId,
                Name = "Voron",
            });
        catalog
            .Setup(repository => repository.FindModelByNameAsync(
                "Voron 2.4 300",
                manufacturerId,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PrinterModel
            {
                Id = modelId,
                Name = "Voron 2.4 300",
                ManufacturerId = manufacturerId,
            });

        var printersService = new Mock<IPrintersService>(MockBehavior.Strict);
        printersService
            .Setup(service => service.RefreshCameraUrlsAsync(
                It.IsAny<Guid>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((PrinterDto?)null);

        ServiceProvider provider = new ServiceCollection()
            .AddSingleton(unitOfWork.Object)
            .AddSingleton(catalog.Object)
            .AddSingleton(printersService.Object)
            .BuildServiceProvider();
        var seeder = new MoonrakerEmulatorSeeder(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new MoonrakerEmulatorSeedSettings { Enabled = true }),
            NullLogger<MoonrakerEmulatorSeeder>.Instance);

        await seeder.StartAsync(CancellationToken.None);
        await seeded.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await seeder.StopAsync(CancellationToken.None);

        Assert.Equal(5, added.Count);
        Assert.All(added, printer =>
        {
            Assert.Equal((int)PrinterBackend.Moonraker, printer.Backend);
            Assert.Equal(7125, printer.BackendPort);
            Assert.Equal(7125, printer.FrontendPort);
            Assert.Equal(manufacturerId, printer.ManufacturerId);
            Assert.Equal(modelId, printer.ModelId);
            Assert.StartsWith("http://moonraker-", printer.ServerUrl, StringComparison.Ordinal);
        });
        Assert.Contains(added, printer => printer.Name == "Moonraker Offline");
        Assert.All(added, printer => Assert.NotNull(printer.DispatchState));
        Assert.Collection(
            jobs.OrderBy(job => job.Status),
            job => Assert.Equal(PrintJobStatus.Printing, job.Status),
            job => Assert.Equal(PrintJobStatus.Paused, job.Status));
        Assert.Equal(4, printersService.Invocations.Count);
    }

    [Fact]
    public async Task ExecuteAsync_DisabledSettings_DoesNotResolveDatabaseServices()
    {
        ServiceProvider provider = new ServiceCollection().BuildServiceProvider();
        var seeder = new MoonrakerEmulatorSeeder(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new MoonrakerEmulatorSeedSettings()),
            NullLogger<MoonrakerEmulatorSeeder>.Instance);

        await seeder.StartAsync(CancellationToken.None);
        await seeder.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ResetAsync_RemovesPrintersAddedFromDeterministicDiscoveryFixtures()
    {
        Guid seedId = Guid.NewGuid();
        Guid unknownManufacturerId = Guid.NewGuid();
        Guid unknownModelId = Guid.NewGuid();
        var seedPrinter = new Printer
        {
            Id = seedId,
            Name = "Moonraker Ready",
            ServerUrl = "http://moonraker-ready:7125",
            OriginalServerUrl = "http://moonraker-ready:7125",
            ManufacturerId = unknownManufacturerId,
            ModelId = unknownModelId,
            DispatchState = new PrinterDispatchState { PrinterId = seedId },
        };
        var discoveredPrinter = new Printer
        {
            Id = Guid.NewGuid(),
            Name = "Discovered Voron V2.4",
            ServerUrl = "http://172.18.0.3:7125",
            OriginalServerUrl = "http://moonraker-discovery-voron:7125",
            ManufacturerId = unknownManufacturerId,
            ModelId = unknownModelId,
        };

        var printers = new Mock<IPrintersRepository>(MockBehavior.Strict);
        printers
            .Setup(repository => repository.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([seedPrinter, discoveredPrinter]);
        printers
            .Setup(repository => repository.RemoveAsync(
                discoveredPrinter,
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        printers
            .Setup(repository => repository.FindByIdAsync(seedId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(seedPrinter);
        printers
            .Setup(repository => repository.FindDispatchStateAsync(seedId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(seedPrinter.DispatchState);

        var queue = new Mock<IQueueRepository>(MockBehavior.Strict);
        queue
            .Setup(repository => repository.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var unitOfWork = new Mock<IUnitOfWork>(MockBehavior.Strict);
        unitOfWork.SetupGet(value => value.Printers).Returns(printers.Object);
        unitOfWork.SetupGet(value => value.Queue).Returns(queue.Object);
        unitOfWork
            .Setup(value => value.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);
        unitOfWork.Setup(value => value.Dispose());

        var catalog = new Mock<ICatalogRepository>(MockBehavior.Strict);
        catalog
            .Setup(repository => repository.GetUnknownManufacturerIdAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(unknownManufacturerId);
        catalog
            .Setup(repository => repository.GetUnknownModelIdAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(unknownModelId);

        var printersService = new Mock<IPrintersService>(MockBehavior.Strict);
        printersService
            .Setup(service => service.RefreshCameraUrlsAsync(seedId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((PrinterDto?)null);

        ServiceProvider provider = new ServiceCollection()
            .AddSingleton(unitOfWork.Object)
            .AddSingleton(catalog.Object)
            .AddSingleton(printersService.Object)
            .BuildServiceProvider();
        var settings = new MoonrakerEmulatorSeedSettings
        {
            Enabled = true,
            Printers =
            [
                new(
                    seedId,
                    seedPrinter.Name,
                    seedPrinter.ServerUrl,
                    "Unknown",
                    "Unknown"),
            ],
        };
        var seeder = new MoonrakerEmulatorSeeder(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(settings),
            NullLogger<MoonrakerEmulatorSeeder>.Instance);

        bool reset = await seeder.ResetAsync(CancellationToken.None);

        Assert.True(reset);
        printers.Verify(
            repository => repository.RemoveAsync(discoveredPrinter, It.IsAny<CancellationToken>()),
            Times.Once);
    }
}
