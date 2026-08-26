using Farm.Infrastructure;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.PrinterCalibration;
using Farm.Infrastructure.Repositories.Catalog;
using Farm.Infrastructure.Repositories.Printers;
using Farm.Infrastructure.Repositories.Queue;
using Farm.Infrastructure.Repositories.UnitOfWork;
using Farm.Infrastructure.Services.Printers;
using Farm.Slicer.Module.Data.Repositories;
using Farm.Slicer.Module.Domain;
using Farm.Web.Api.Services.Startup;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Services.Startup;

public sealed class MoonrakerEmulatorSeederTests
{
    private static (
        Mock<IMachineProfileRepository> Machine,
        Mock<IProcessProfileRepository> Process,
        Mock<IFilamentProfileRepository> Filament,
        List<MachineProfile> AddedMachineProfiles,
        List<ProcessProfile> AddedProcessProfiles,
        List<FilamentProfile> AddedFilamentProfiles) CreateStrictCalibrationProfileMocks()
    {
        List<MachineProfile> addedMachineProfiles = [];
        var machine = new Mock<IMachineProfileRepository>(MockBehavior.Strict);
        machine
            .Setup(repository => repository.GetByHashAsync(
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .Returns<string, CancellationToken>((hash, _) => Task.FromResult(
                addedMachineProfiles.FirstOrDefault(profile => profile.Hash == hash)));
        machine
            .Setup(repository => repository.AddAsync(
                It.IsAny<MachineProfile>(),
                It.IsAny<CancellationToken>()))
            .Callback<MachineProfile, CancellationToken>(
                (profile, _) => addedMachineProfiles.Add(profile))
            .Returns(Task.CompletedTask);

        List<ProcessProfile> addedProcessProfiles = [];
        var process = new Mock<IProcessProfileRepository>(MockBehavior.Strict);
        process
            .Setup(repository => repository.GetByHashAsync(
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .Returns<string, CancellationToken>((hash, _) => Task.FromResult(
                addedProcessProfiles.FirstOrDefault(profile => profile.Hash == hash)));
        process
            .Setup(repository => repository.AddAsync(
                It.IsAny<ProcessProfile>(),
                It.IsAny<CancellationToken>()))
            .Callback<ProcessProfile, CancellationToken>(
                (profile, _) => addedProcessProfiles.Add(profile))
            .Returns(Task.CompletedTask);

        List<FilamentProfile> addedFilamentProfiles = [];
        var filament = new Mock<IFilamentProfileRepository>(MockBehavior.Strict);
        filament
            .Setup(repository => repository.GetByHashAsync(
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .Returns<string, CancellationToken>((hash, _) => Task.FromResult(
                addedFilamentProfiles.FirstOrDefault(profile => profile.Hash == hash)));
        filament
            .Setup(repository => repository.AddAsync(
                It.IsAny<FilamentProfile>(),
                It.IsAny<CancellationToken>()))
            .Callback<FilamentProfile, CancellationToken>(
                (profile, _) => addedFilamentProfiles.Add(profile))
            .Returns(Task.CompletedTask);

        return (
            machine,
            process,
            filament,
            addedMachineProfiles,
            addedProcessProfiles,
            addedFilamentProfiles);
    }

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

        (
            Mock<IMachineProfileRepository> machineProfiles,
            Mock<IProcessProfileRepository> processProfiles,
            Mock<IFilamentProfileRepository> filamentProfiles,
            List<MachineProfile> addedMachineProfiles,
            List<ProcessProfile> addedProcessProfiles,
            List<FilamentProfile> addedFilamentProfiles) = CreateStrictCalibrationProfileMocks();

        ServiceProvider provider = new ServiceCollection()
            .AddSingleton(unitOfWork.Object)
            .AddSingleton(catalog.Object)
            .AddSingleton(printersService.Object)
            .AddSingleton(machineProfiles.Object)
            .AddSingleton(processProfiles.Object)
            .AddSingleton(filamentProfiles.Object)
            .BuildServiceProvider();
        var seeder = new MoonrakerEmulatorSeeder(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new MoonrakerEmulatorSeedSettings { Enabled = true }),
            NullLogger<MoonrakerEmulatorSeeder>.Instance);

        await seeder.StartAsync(CancellationToken.None);
        // Widened from 2s: under full test-suite parallelism (maxParallelThreads=0),
        // thread-pool/CPU contention from dozens of concurrently-running hosts can
        // legitimately delay this background seeder's first save past a short timeout.
        await seeded.Task.WaitAsync(TimeSpan.FromSeconds(10));
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

        // #1851: every seeded printer must carry the calibration generation data that
        // calibration-scoped consumers require, not just base identity.
        Assert.All(added, printer =>
        {
            Assert.Equal(PrinterFirmwareFamily.Klipper, printer.FirmwareFamily);
            Assert.Equal(PrinterGcodeDialect.Klipper, printer.GcodeDialect);
            Assert.True(printer.FirmwareIdentityVerified);
            Assert.NotNull(printer.FirmwareDetectedAtUtc);
            Assert.Equal(CalibrationContractConstants.SlicerEngine, printer.CalibrationSlicerEngine);
            Assert.Equal(
                CalibrationContractConstants.SlicerDistribution,
                printer.CalibrationSlicerDistribution);
            Assert.Equal(250, printer.MaxBuildVolumeX);
            Assert.Equal(250, printer.MaxBuildVolumeY);
            Assert.Equal(250, printer.MaxBuildVolumeZ);
            Assert.Equal(0, printer.ActiveToolheadIndex);
            Assert.Single(printer.Toolheads);
            Toolhead toolhead = printer.Toolheads.Single();
            Assert.Equal(ToolheadType.Physical, toolhead.ToolheadType);
            Assert.Equal(0, toolhead.Index);
            Assert.Equal(0.4, toolhead.NozzleDiameter);
            Assert.Equal(["PLA", "PETG"], toolhead.SupportedMaterials);
        });

        // All five seeded printers share the same catalog model, so exactly one machine,
        // process, and filament profile should have been created (not one per printer).
        Assert.Single(addedMachineProfiles);
        Assert.Single(addedProcessProfiles);
        Assert.Single(addedFilamentProfiles);
        AssertHashMatchesRawJson(addedMachineProfiles[0].Hash, addedMachineProfiles[0].RawJson);
        AssertHashMatchesRawJson(addedProcessProfiles[0].Hash, addedProcessProfiles[0].RawJson);
        AssertHashMatchesRawJson(addedFilamentProfiles[0].Hash, addedFilamentProfiles[0].RawJson);
        Assert.Equal(addedMachineProfiles[0].Name, addedProcessProfiles[0].CompatiblePrinters);
        Assert.Equal(addedMachineProfiles[0].Name, addedFilamentProfiles[0].CompatiblePrinters);
    }

    /// <summary>
    /// Asserts a profile's stored <c>Hash</c> equals the lowercase-hex SHA256 of its <c>RawJson</c>
    /// — the exact computation <c>MoonrakerEmulatorSeeder.ComputeSha256</c> performs — for all
    /// three profile kinds in the shared calibration trio (machine, process, filament), not just
    /// the machine profile.
    /// </summary>
    private static void AssertHashMatchesRawJson(string? hash, string? rawJson)
    {
        Assert.NotNull(rawJson);
        Assert.Equal(
            Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(rawJson!)))
                .ToLowerInvariant(),
            hash);
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
            .Setup(repository => repository.FindByIdWithToolheadsAsync(
                seedId,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(seedPrinter);
        printers
            .Setup(repository => repository.FindDispatchStateAsync(seedId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(seedPrinter.DispatchState);
        printers
            .Setup(repository => repository.AddToolheads(It.IsAny<IEnumerable<Toolhead>>()))
            .Callback<IEnumerable<Toolhead>>(toolheads =>
            {
                foreach (Toolhead toolhead in toolheads)
                {
                    seedPrinter.Toolheads.Add(toolhead);
                }
            });

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

        (
            Mock<IMachineProfileRepository> machineProfiles,
            Mock<IProcessProfileRepository> processProfiles,
            Mock<IFilamentProfileRepository> filamentProfiles,
            _,
            _,
            _) = CreateStrictCalibrationProfileMocks();

        ServiceProvider provider = new ServiceCollection()
            .AddSingleton(unitOfWork.Object)
            .AddSingleton(catalog.Object)
            .AddSingleton(printersService.Object)
            .AddSingleton(machineProfiles.Object)
            .AddSingleton(processProfiles.Object)
            .AddSingleton(filamentProfiles.Object)
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
        printers.Verify(
            repository => repository.AddToolheads(It.IsAny<IEnumerable<Toolhead>>()),
            Times.Once);
        Assert.Single(seedPrinter.Toolheads);
        Toolhead recreatedToolhead = seedPrinter.Toolheads.Single();
        Assert.Equal(ToolheadType.Physical, recreatedToolhead.ToolheadType);
        Assert.Equal(0, recreatedToolhead.Index);
        Assert.Equal(["PLA", "PETG"], recreatedToolhead.SupportedMaterials);
    }
}
