using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Telemetry;
using Farm.Web.Api.Models.Admin;
using Farm.Web.Api.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace Farm.Web.Api.Tests.DataManagement;

public class DataImportServiceTests
{
    private readonly AppDbContext _context;
    private readonly Mock<IUnifiedLoggingService> _loggerMock;
    private readonly DataImportService _importService;

    public DataImportServiceTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName: $"TestDb_{Guid.NewGuid()}")
            .Options;
        _context = new AppDbContext(options);

        _loggerMock = new Mock<IUnifiedLoggingService>();
        _importService = new DataImportService(_context, _loggerMock.Object);
    }

    [Fact]
    public async Task ImportCatalogAsync_MergeMode_AddsNewManufacturers()
    {
        // Arrange
        var catalog = new CatalogExportDto
        {
            Manufacturers = new List<ManufacturerExportDto>
            {
                new() { Name = "New Manufacturer" }
            },
            FilamentTypes = new List<FilamentTypeExportDto>(),
            PrinterModels = new List<PrinterModelExportDto>(),
            Hotends = new List<HotendModelExportDto>(),
            Extruders = new List<ExtruderModelExportDto>(),
            Toolheads = new List<ToolheadModelExportDto>(),
            Nozzles = new List<NozzleModelExportDto>()
        };

        // Act
        var result = await _importService.ImportCatalogAsync(catalog, ImportMode.Merge);

        // Assert
        result.Success.Should().BeTrue();
        result.Statistics.ManufacturersImported.Should().Be(1);
        var manufacturer = await _context.Manufacturers.FirstOrDefaultAsync(m => m.Name == "New Manufacturer");
        manufacturer.Should().NotBeNull();
    }

    [Fact]
    public async Task ImportCatalogAsync_MergeMode_SkipsDuplicateManufacturers()
    {
        // Arrange
        var existingManufacturer = new Manufacturer { Name = "Existing Manufacturer" };
        await _context.Manufacturers.AddAsync(existingManufacturer);
        await _context.SaveChangesAsync();

        var catalog = new CatalogExportDto
        {
            Manufacturers = new List<ManufacturerExportDto>
            {
                new() { Name = "Existing Manufacturer" },
                new() { Name = "New Manufacturer" }
            },
            FilamentTypes = new List<FilamentTypeExportDto>(),
            PrinterModels = new List<PrinterModelExportDto>(),
            Hotends = new List<HotendModelExportDto>(),
            Extruders = new List<ExtruderModelExportDto>(),
            Toolheads = new List<ToolheadModelExportDto>(),
            Nozzles = new List<NozzleModelExportDto>()
        };

        // Act
        var result = await _importService.ImportCatalogAsync(catalog, ImportMode.Merge);

        // Assert
        result.Success.Should().BeTrue();
        result.Statistics.ManufacturersImported.Should().Be(1); // Only the new one
        var manufacturers = await _context.Manufacturers.ToListAsync();
        manufacturers.Should().HaveCount(2); // 1 existing + 1 new
    }

    [Fact]
    public async Task ImportCatalogAsync_ReplaceMode_DeletesExistingData()
    {
        // Arrange
        var existingManufacturer = new Manufacturer { Name = "Old Manufacturer" };
        await _context.Manufacturers.AddAsync(existingManufacturer);
        await _context.SaveChangesAsync();

        var catalog = new CatalogExportDto
        {
            Manufacturers = new List<ManufacturerExportDto>
            {
                new() { Name = "New Manufacturer" }
            },
            FilamentTypes = new List<FilamentTypeExportDto>(),
            PrinterModels = new List<PrinterModelExportDto>(),
            Hotends = new List<HotendModelExportDto>(),
            Extruders = new List<ExtruderModelExportDto>(),
            Toolheads = new List<ToolheadModelExportDto>(),
            Nozzles = new List<NozzleModelExportDto>()
        };

        // Act
        var result = await _importService.ImportCatalogAsync(catalog, ImportMode.Replace);

        // Assert
        result.Success.Should().BeTrue();
        result.Warnings.Should().Contain(w => w.Contains("deleted"));
        result.Statistics.ManufacturersImported.Should().Be(1);
        
        var oldManufacturer = await _context.Manufacturers.FirstOrDefaultAsync(m => m.Name == "Old Manufacturer");
        oldManufacturer.Should().BeNull(); // Should be deleted
        
        var newManufacturer = await _context.Manufacturers.FirstOrDefaultAsync(m => m.Name == "New Manufacturer");
        newManufacturer.Should().NotBeNull(); // Should be added
    }

    [Fact]
    public async Task ImportCatalogAsync_WithFilamentTypes_ImportsCorrectly()
    {
        // Arrange
        var catalog = new CatalogExportDto
        {
            Manufacturers = new List<ManufacturerExportDto>(),
            FilamentTypes = new List<FilamentTypeExportDto>
            {
                new()
                {
                    Name = "Test PLA",
                    DefaultHotendTemp = 210,
                    DefaultBedTemp = 65,
                    IsAbrasive = false,
                    NeedsEnclosure = false
                }
            },
            PrinterModels = new List<PrinterModelExportDto>(),
            Hotends = new List<HotendModelExportDto>(),
            Extruders = new List<ExtruderModelExportDto>(),
            Toolheads = new List<ToolheadModelExportDto>(),
            Nozzles = new List<NozzleModelExportDto>()
        };

        // Act
        var result = await _importService.ImportCatalogAsync(catalog, ImportMode.Merge);

        // Assert
        result.Success.Should().BeTrue();
        result.Statistics.FilamentTypesImported.Should().Be(1);
        
        var filamentType = await _context.FilamentTypes.FirstOrDefaultAsync(f => f.Name == "Test PLA");
        filamentType.Should().NotBeNull();
        filamentType!.DefaultHotendTemp.Should().Be(210);
        filamentType.DefaultBedTemp.Should().Be(65);
        filamentType.IsAbrasive.Should().BeFalse();
    }

    [Fact]
    public async Task ImportCatalogAsync_WithInvalidManufacturerReference_ReportsError()
    {
        // Arrange
        var catalog = new CatalogExportDto
        {
            Manufacturers = new List<ManufacturerExportDto>(),
            FilamentTypes = new List<FilamentTypeExportDto>(),
            PrinterModels = new List<PrinterModelExportDto>
            {
                new()
                {
                    Name = "Test Printer",
                    ManufacturerName = "Nonexistent Manufacturer",
                    DefaultBackend = 0,
                    MaxX = 200,
                    MaxY = 200,
                    MaxZ = 200
                }
            },
            Hotends = new List<HotendModelExportDto>(),
            Extruders = new List<ExtruderModelExportDto>(),
            Toolheads = new List<ToolheadModelExportDto>(),
            Nozzles = new List<NozzleModelExportDto>()
        };

        // Act
        var result = await _importService.ImportCatalogAsync(catalog, ImportMode.Merge);

        // Assert
        result.Success.Should().BeFalse();
        result.Errors.Should().NotBeEmpty();
        result.Errors.Should().Contain(e => e.Contains("Nonexistent Manufacturer"));
    }

    [Fact]
    public async Task ImportCatalogAsync_ValidPrinterModel_ImportsSuccessfully()
    {
        // Arrange
        var manufacturer = new Manufacturer { Name = "Test Manufacturer" };
        await _context.Manufacturers.AddAsync(manufacturer);
        await _context.SaveChangesAsync();

        var catalog = new CatalogExportDto
        {
            Manufacturers = new List<ManufacturerExportDto>(),
            FilamentTypes = new List<FilamentTypeExportDto>(),
            PrinterModels = new List<PrinterModelExportDto>
            {
                new()
                {
                    Name = "Test Printer",
                    ManufacturerName = "Test Manufacturer",
                    DefaultBackend = 0,
                    MaxX = 250,
                    MaxY = 210,
                    MaxZ = 220,
                    MotionType = 0, // Cartesian
                    HasHeatedBed = true
                }
            },
            Hotends = new List<HotendModelExportDto>(),
            Extruders = new List<ExtruderModelExportDto>(),
            Toolheads = new List<ToolheadModelExportDto>(),
            Nozzles = new List<NozzleModelExportDto>()
        };

        // Act
        var result = await _importService.ImportCatalogAsync(catalog, ImportMode.Merge);

        // Assert
        result.Success.Should().BeTrue();
        result.Statistics.PrinterModelsImported.Should().Be(1);
        
        var printerModel = await _context.PrinterModels.FirstOrDefaultAsync(p => p.Name == "Test Printer");
        printerModel.Should().NotBeNull();
        printerModel!.MaxX.Should().Be(250);
        printerModel.MotionType.Should().Be(0); // Cartesian
        printerModel.HasHeatedBed.Should().BeTrue();
    }

    [Fact]
    public async Task ImportFullBackupAsync_MergeMode_ImportsAllData()
    {
        // Arrange
        var manufacturer = new Manufacturer { Name = "Backup Manufacturer" };
        await _context.Manufacturers.AddAsync(manufacturer);
        await _context.SaveChangesAsync();

        var backup = new FullBackupExportDto
        {
            Catalog = new CatalogExportDto
            {
                Manufacturers = new List<ManufacturerExportDto>
                {
                    new() { Name = "New Manufacturer From Backup" }
                },
                FilamentTypes = new List<FilamentTypeExportDto>(),
                PrinterModels = new List<PrinterModelExportDto>(),
                Hotends = new List<HotendModelExportDto>(),
                Extruders = new List<ExtruderModelExportDto>(),
                Toolheads = new List<ToolheadModelExportDto>(),
                Nozzles = new List<NozzleModelExportDto>()
            },
            Printers = new List<PrinterExportDto>(),
            Locations = new List<LocationExportDto>(),
            ExportedAt = DateTime.UtcNow
        };

        // Act
        var result = await _importService.ImportFullBackupAsync(backup, ImportMode.Merge);

        // Assert
        result.Success.Should().BeTrue();
        result.Statistics.ManufacturersImported.Should().Be(1);
        
        var manufacturers = await _context.Manufacturers.ToListAsync();
        manufacturers.Should().HaveCount(2); // 1 existing + 1 from backup
        manufacturers.Should().Contain(m => m.Name == "New Manufacturer From Backup");
    }
}
