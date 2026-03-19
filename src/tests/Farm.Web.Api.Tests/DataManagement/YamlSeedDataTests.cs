using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Dtos.DataManagement;
using Farm.Infrastructure.Services.DataManagement;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;

namespace Farm.Web.Api.Tests.DataManagement;

public class YamlSeedDataTests
{
    private readonly AppDbContext _context;
    private readonly Mock<ILogger<YamlSeedDataReader>> _loggerMock;
    private readonly Mock<IConfiguration> _configMock;
    private readonly YamlSeedDataReader _yamlReader;

    public YamlSeedDataTests()
    {
        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName: $"TestDb_{Guid.NewGuid()}")
            .Options;
        _context = new AppDbContext(options);

        _loggerMock = new Mock<ILogger<YamlSeedDataReader>>();
        _configMock = new Mock<IConfiguration>();
        _configMock.Setup(c => c["SeedData:Path"]).Returns("Data/seed/");

        _yamlReader = new YamlSeedDataReader(_loggerMock.Object, _configMock.Object);
    }

    [Fact]
    public async Task ReadManufacturersAsync_ValidYamlFile_ReturnsManufacturers()
    {
        // Act
        List<ManufacturerSeedDto> manufacturers = await _yamlReader.ReadManufacturersAsync();

        // Assert
        manufacturers.Should().NotBeNull();
        manufacturers.Should().NotBeEmpty();
        manufacturers.Should().Contain(m => m.Name == "Prusa");
        manufacturers.Should().Contain(m => m.Name == "Voron");
        manufacturers.Should().Contain(m => m.Name == "Bambu Lab");
    }

    [Fact]
    public async Task ReadFilamentTypesAsync_ValidYamlFile_ReturnsFilamentTypes()
    {
        // Act
        List<FilamentTypeSeedDto> filamentTypes = await _yamlReader.ReadFilamentTypesAsync();

        // Assert
        filamentTypes.Should().NotBeNull();
        filamentTypes.Should().NotBeEmpty();

        FilamentTypeSeedDto? pla = filamentTypes.FirstOrDefault(f => f.Name == "PLA");
        pla.Should().NotBeNull();
        pla!.DefaultHotendTemp.Should().BeGreaterThan(0);
        pla.DefaultBedTemp.Should().BeGreaterThan(0);
        pla.IsAbrasive.Should().BeFalse();
    }

    [Fact]
    public async Task ReadPrinterModelsAsync_ValidYamlFile_ReturnsPrinterModels()
    {
        // Act
        List<PrinterModelSeedDto> printerModels = await _yamlReader.ReadPrinterModelsAsync();

        // Assert
        printerModels.Should().NotBeNull();
        printerModels.Should().NotBeEmpty();

        PrinterModelSeedDto? prusaMk4 = printerModels.FirstOrDefault(p => p.Name.Contains("MK4"));
        prusaMk4.Should().NotBeNull();
        prusaMk4!.Manufacturer.Should().NotBeEmpty();
        prusaMk4.BuildVolume.Should().NotBeNull();
        prusaMk4.BuildVolume!.X.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task ReadHotendsAsync_ValidYamlFile_ReturnsHotends()
    {
        // Act
        List<HotendModelSeedDto> hotends = await _yamlReader.ReadHotendsAsync();

        // Assert
        hotends.Should().NotBeNull();
        hotends.Should().NotBeEmpty();

        HotendModelSeedDto hotend = hotends.First();
        hotend.Name.Should().NotBeEmpty();
        hotend.Manufacturer.Should().NotBeEmpty();
        hotend.MaxTemp.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task ReadExtrudersAsync_ValidYamlFile_ReturnsExtruders()
    {
        // Act
        List<ExtruderModelSeedDto> extruders = await _yamlReader.ReadExtrudersAsync();

        // Assert
        extruders.Should().NotBeNull();
        extruders.Should().NotBeEmpty();

        ExtruderModelSeedDto extruder = extruders.First();
        extruder.Name.Should().NotBeEmpty();
        extruder.Manufacturer.Should().NotBeEmpty();
    }

    [Fact]
    public async Task ReadToolheadsAsync_ValidYamlFile_ReturnsToolheads()
    {
        // Act
        List<ToolheadModelSeedDto> toolheads = await _yamlReader.ReadToolheadsAsync();

        // Assert
        toolheads.Should().NotBeNull();
        toolheads.Should().NotBeEmpty();

        ToolheadModelSeedDto toolhead = toolheads.First();
        toolhead.Name.Should().NotBeEmpty();
        toolhead.Manufacturer.Should().NotBeEmpty();
    }

    [Fact]
    public async Task ReadNozzlesAsync_ValidYamlFile_ReturnsNozzles()
    {
        // Act
        List<NozzleModelSeedDto> nozzles = await _yamlReader.ReadNozzlesAsync();

        // Assert
        nozzles.Should().NotBeNull();
        nozzles.Should().NotBeEmpty();

        NozzleModelSeedDto nozzle = nozzles.First();
        nozzle.Name.Should().NotBeEmpty();
        nozzle.Manufacturer.Should().NotBeEmpty();
    }

    [Fact]
    public async Task ReadManufacturersAsync_MissingFile_ReturnsEmptyList()
    {
        // Arrange
        var configMock = new Mock<IConfiguration>();
        configMock.Setup(c => c["SeedData:Path"]).Returns("nonexistent/path/");
        var reader = new YamlSeedDataReader(_loggerMock.Object, configMock.Object);

        // Act
        List<ManufacturerSeedDto> manufacturers = await reader.ReadManufacturersAsync();

        // Assert
        manufacturers.Should().NotBeNull();
        manufacturers.Should().BeEmpty();
    }
}
