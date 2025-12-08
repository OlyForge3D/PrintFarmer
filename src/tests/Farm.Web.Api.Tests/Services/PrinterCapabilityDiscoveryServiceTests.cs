using System.Text;
using Farm.Infrastructure;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Printers;
using Farm.Infrastructure.Telemetry;
using Farm.Web.Api.Services;
using Farm.Web.Api.Services.Interfaces;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Services;

public class PrinterCapabilityDiscoveryServiceTests
{
    [Fact]
    public async Task GetModelDefaultCapabilitiesAsync_ReturnsDefaultsAndManufacturerFallbacks()
    {
        Mock<IPrintersRepository> printersRepository = new Mock<IPrintersRepository>();
        Mock<IMoonrakerClient> moonrakerClient = new Mock<IMoonrakerClient>();
        Mock<IPrusaLinkClient> prusaClient = new Mock<IPrusaLinkClient>();
        Mock<IUnifiedLoggingService> logger = new Mock<IUnifiedLoggingService>();

        Guid printerId = Guid.NewGuid();
        Manufacturer manufacturer = new Manufacturer { Id = Guid.NewGuid(), Name = "prusa" };
        PrinterModel model = new PrinterModel
        {
            Id = Guid.NewGuid(),
            Name = "MK4",
            ManufacturerId = manufacturer.Id,
            Manufacturer = manufacturer,
            MaxX = 220,
            MaxY = 210,
            MaxZ = 250,
            DefaultNozzleDiameter = null,
            HasHeatedBed = false,
            NumberOfExtruders = 0,
            MaxBedTemp = null,
            MinHotendTemp = null,
            MinBedTemp = null
        };
        model.SupportedFilamentTypes.Add(new PrinterModelFilamentType { FilamentType = new FilamentType { Name = "PETG" } });

        Printer printerFromRepository = new Printer { Id = printerId, Model = model, Manufacturer = manufacturer, ModelId = model.Id, ManufacturerId = manufacturer.Id };
        _ = printersRepository.Setup(r => r.FindByIdWithIncludesAsync(printerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(printerFromRepository);

        Printer printer = new Printer { Id = printerId };
        PrinterCapabilityDiscoveryService service = new(printersRepository.Object, moonrakerClient.Object, prusaClient.Object, logger.Object);

        Farm.Infrastructure.Domain.PrinterCapabilities? capabilities = await service.GetModelDefaultCapabilitiesAsync(printer);

        Assert.NotNull(capabilities);
        Assert.Equal(printerId, capabilities!.PrinterId);
        Assert.True(capabilities.HasHeatedBed);
        Assert.Equal(1, capabilities.NumberOfExtruders);
        Assert.Equal(0.4, capabilities.NozzleDiameter);
        Assert.Equal(120, capabilities.MaxBedTemp);
        Assert.Equal(170, capabilities.MinHotendTemp);
        Assert.Equal(35, capabilities.MinBedTemp);
        Assert.Contains("PETG", capabilities.SupportedMaterials ?? Array.Empty<string>());
    }

    [Fact]
    public async Task DiscoverCapabilitiesAsync_UsesMoonrakerConfigValues()
    {
        Mock<IPrintersRepository> printersRepository = new Mock<IPrintersRepository>();
        Mock<IMoonrakerClient> moonrakerClient = new Mock<IMoonrakerClient>();
        Mock<IPrusaLinkClient> prusaClient = new Mock<IPrusaLinkClient>();
        Mock<IUnifiedLoggingService> logger = new Mock<IUnifiedLoggingService>();

        Guid printerId = Guid.NewGuid();
        PrinterModel model = new PrinterModel
        {
            Id = Guid.NewGuid(),
            Name = "Voron",
            MaxX = 200,
            MaxY = 200,
            MaxZ = 200,
            DefaultNozzleDiameter = 0.4,
            Manufacturer = new Manufacturer { Name = "voron" }
        };
        Printer printerFromRepository = new Printer { Id = printerId, Model = model, Manufacturer = model.Manufacturer, ModelId = model.Id, ManufacturerId = model.ManufacturerId };
        _ = printersRepository.Setup(r => r.FindByIdWithIncludesAsync(printerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(printerFromRepository);

        string config = """
[stepper_x]
position_max: 320
[stepper_y]
position_max: 300
[stepper_z]
position_max: 250
[heater_bed]
max_temp: 110
[extruder]
nozzle_diameter: 0.6
max_temp: 290
[extruder1]
max_temp: 280
""";

        _ = moonrakerClient.Setup(c => c.DownloadFileAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Encoding.UTF8.GetBytes(config));

        Printer printer = new Printer { Id = printerId, Name = "Test", ServerUrl = "http://moonraker.local", Backend = (int)PrinterBackend.Moonraker };
        PrinterCapabilityDiscoveryService service = new(printersRepository.Object, moonrakerClient.Object, prusaClient.Object, logger.Object);

        Farm.Infrastructure.Domain.PrinterCapabilities? capabilities = await service.DiscoverCapabilitiesAsync(printer, CancellationToken.None);

        Assert.NotNull(capabilities);
        Assert.Equal(0.6, capabilities!.NozzleDiameter);
        Assert.Equal(320, capabilities.MaxBuildVolumeX);
        Assert.Equal(300, capabilities.MaxBuildVolumeY);
        Assert.Equal(250, capabilities.MaxBuildVolumeZ);
        Assert.True(capabilities.HasHeatedBed);
        Assert.Equal(2, capabilities.NumberOfExtruders);
        Assert.Equal(290, capabilities.MaxHotendTemp);
        Assert.Equal(110, capabilities.MaxBedTemp);
    }

    [Fact]
    public async Task ValidateCapabilitiesAsync_FlagsOutOfRangeValues()
    {
        Mock<IPrintersRepository> printersRepository = new Mock<IPrintersRepository>();
        Mock<IMoonrakerClient> moonrakerClient = new Mock<IMoonrakerClient>();
        Mock<IPrusaLinkClient> prusaClient = new Mock<IPrusaLinkClient>();
        Mock<IUnifiedLoggingService> logger = new Mock<IUnifiedLoggingService>();

        Guid printerId = Guid.NewGuid();
        PrinterModel model = new PrinterModel
        {
            Id = Guid.NewGuid(),
            Name = "Generic",
            MaxX = 200,
            MaxY = 200,
            MaxZ = 200
        };
        Printer printerFromRepository = new Printer { Id = printerId, Model = model, ModelId = model.Id };
        _ = printersRepository.Setup(r => r.FindByIdWithIncludesAsync(printerId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(printerFromRepository);

        Farm.Infrastructure.Domain.PrinterCapabilities capabilities = new()
        {
            PrinterId = printerId,
            MaxBuildVolumeX = 300,
            MaxBuildVolumeY = 250,
            MaxBuildVolumeZ = 210,
            NozzleDiameter = 1.7,
            MaxHotendTemp = 600,
            MaxBedTemp = 200
        };

        Printer printer = new Printer { Id = printerId };
        PrinterCapabilityDiscoveryService service = new(printersRepository.Object, moonrakerClient.Object, prusaClient.Object, logger.Object);

        CapabilityValidationResult result = await service.ValidateCapabilitiesAsync(capabilities, printer);

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
        Assert.Contains(result.Warnings, warning => warning.Contains("Build volume X"));
        Assert.Contains(result.Warnings, warning => warning.Contains("Build volume Y"));
        Assert.Contains(result.Warnings, warning => warning.Contains("Build volume Z"));
        Assert.Contains(result.Warnings, warning => warning.Contains("Unusual nozzle diameter"));
        Assert.Contains(result.Warnings, warning => warning.Contains("Very high hotend temperature limit"));
        Assert.Contains(result.Warnings, warning => warning.Contains("Very high bed temperature limit"));
    }
}
