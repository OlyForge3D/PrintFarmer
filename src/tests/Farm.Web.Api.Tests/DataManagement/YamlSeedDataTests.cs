using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Telemetry;
using Farm.Web.Api.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;

namespace Farm.Web.Api.Tests.DataManagement;

public class YamlSeedDataTests
{
    private readonly AppDbContext _context;
    private readonly Mock<IUnifiedLoggingService> _loggerMock;
    private readonly Mock<IConfiguration> _configMock;
    private readonly YamlSeedDataReader _yamlReader;

    public YamlSeedDataTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName: $"TestDb_{Guid.NewGuid()}")
            .Options;
        _context = new AppDbContext(options);

        _loggerMock = new Mock<IUnifiedLoggingService>();
        _configMock = new Mock<IConfiguration>();
        _configMock.Setup(c => c["SeedData:Path"]).Returns("data/seed/");
        
        _yamlReader = new YamlSeedDataReader(_loggerMock.Object, _configMock.Object);
    }

    [Fact]
    public async Task ReadManufacturersAsync_ValidYamlFile_ReturnsManufacturers()
    {
        // Act
        var manufacturers = await _yamlReader.ReadManufacturersAsync();

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
        var filamentTypes = await _yamlReader.ReadFilamentTypesAsync();

        // Assert
        filamentTypes.Should().NotBeNull();
        filamentTypes.Should().NotBeEmpty();
        
        var pla = filamentTypes.FirstOrDefault(f => f.Name == "PLA");
        pla.Should().NotBeNull();
        pla!.DefaultHotendTemp.Should().BeGreaterThan(0);
        pla.DefaultBedTemp.Should().BeGreaterThan(0);
        pla.IsAbrasive.Should().BeFalse();
    }

    [Fact]
    public async Task ReadPrinterModelsAsync_ValidYamlFile_ReturnsPrinterModels()
    {
        // Act
        var printerModels = await _yamlReader.ReadPrinterModelsAsync();

        // Assert
        printerModels.Should().NotBeNull();
        printerModels.Should().NotBeEmpty();
        
        var prusaMk4 = printerModels.FirstOrDefault(p => p.Name.Contains("MK4"));
        prusaMk4.Should().NotBeNull();
        prusaMk4!.Manufacturer.Should().NotBeEmpty();
        prusaMk4.BuildVolume.Should().NotBeNull();
        prusaMk4.BuildVolume!.X.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task ReadHotendsAsync_ValidYamlFile_ReturnsHotends()
    {
        // Act
        var hotends = await _yamlReader.ReadHotendsAsync();

        // Assert
        hotends.Should().NotBeNull();
        hotends.Should().NotBeEmpty();
        
        var hotend = hotends.First();
        hotend.Name.Should().NotBeEmpty();
        hotend.Manufacturer.Should().NotBeEmpty();
        hotend.MaxTemp.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task ReadExtrudersAsync_ValidYamlFile_ReturnsExtruders()
    {
        // Act
        var extruders = await _yamlReader.ReadExtrudersAsync();

        // Assert
        extruders.Should().NotBeNull();
        extruders.Should().NotBeEmpty();
        
        var extruder = extruders.First();
        extruder.Name.Should().NotBeEmpty();
        extruder.Manufacturer.Should().NotBeEmpty();
    }

    [Fact]
    public async Task ReadToolheadsAsync_ValidYamlFile_ReturnsToolheads()
    {
        // Act
        var toolheads = await _yamlReader.ReadToolheadsAsync();

        // Assert
        toolheads.Should().NotBeNull();
        toolheads.Should().NotBeEmpty();
        
        var toolhead = toolheads.First();
        toolhead.Name.Should().NotBeEmpty();
        toolhead.Manufacturer.Should().NotBeEmpty();
    }

    [Fact]
    public async Task ReadNozzlesAsync_ValidYamlFile_ReturnsNozzles()
    {
        // Act
        var nozzles = await _yamlReader.ReadNozzlesAsync();

        // Assert
        nozzles.Should().NotBeNull();
        nozzles.Should().NotBeEmpty();
        
        var nozzle = nozzles.First();
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
        var manufacturers = await reader.ReadManufacturersAsync();

        // Assert
        manufacturers.Should().NotBeNull();
        manufacturers.Should().BeEmpty();
    }
}
