using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Dtos.DataManagement;
using Farm.Infrastructure.Repositories.Printers;
using Farm.Infrastructure.Services.DataManagement;
using Farm.Infrastructure.Services.Security;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;

namespace Farm.Infrastructure.Tests.DataManagement;

public class DataImportServiceTests
{
    private readonly AppDbContext _context;
    private readonly Mock<ILogger<DataImportService>> _loggerMock;
    private readonly Mock<ISensitiveDataProtector> _sensitiveDataProtectorMock;
    private readonly DataImportService _importService;

    public DataImportServiceTests()
    {
        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName: $"TestDb_{Guid.NewGuid()}")
            .Options;
        _context = new AppDbContext(options);

        _loggerMock = new Mock<ILogger<DataImportService>>();
        _sensitiveDataProtectorMock = new Mock<ISensitiveDataProtector>();
        _sensitiveDataProtectorMock
            .Setup(x => x.Protect(It.IsAny<string?>()))
            .Returns<string?>(s => string.IsNullOrEmpty(s) ? null : $"prot:{s}");

        // Real repository over the in-memory DbContext — Replace-mode tests exercise the
        // Dallas cascade adjudication cleanup that DataImportService.DeleteAllPrintersAsync
        // now routes through IPrintersRepository.RemoveAsync.
        IPrintersRepository printersRepository = new EfPrintersRepository(_context, _sensitiveDataProtectorMock.Object);

        _importService = new DataImportService(_context, _loggerMock.Object, _sensitiveDataProtectorMock.Object, printersRepository);
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
        ImportResponseDto result = await _importService.ImportCatalogAsync(catalog, ImportMode.Merge);

        // Assert
        result.Success.Should().BeTrue();
        result.Statistics.ManufacturersImported.Should().Be(1);
        Manufacturer? manufacturer = await _context.Manufacturers.FirstOrDefaultAsync(m => m.Name == "New Manufacturer");
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
        ImportResponseDto result = await _importService.ImportCatalogAsync(catalog, ImportMode.Merge);

        // Assert
        result.Success.Should().BeTrue();
        result.Statistics.ManufacturersImported.Should().Be(1); // Only the new one
        List<Manufacturer> manufacturers = await _context.Manufacturers.ToListAsync();
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
        ImportResponseDto result = await _importService.ImportCatalogAsync(catalog, ImportMode.Replace);

        // Assert
        result.Success.Should().BeTrue();
        result.Warnings.Should().Contain(w => w.Contains("deleted"));
        result.Statistics.ManufacturersImported.Should().Be(1);

        Manufacturer? oldManufacturer = await _context.Manufacturers.FirstOrDefaultAsync(m => m.Name == "Old Manufacturer");
        oldManufacturer.Should().BeNull(); // Should be deleted

        Manufacturer? newManufacturer = await _context.Manufacturers.FirstOrDefaultAsync(m => m.Name == "New Manufacturer");
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
        ImportResponseDto result = await _importService.ImportCatalogAsync(catalog, ImportMode.Merge);

        // Assert
        result.Success.Should().BeTrue();
        result.Statistics.FilamentTypesImported.Should().Be(1);

        FilamentType? filamentType = await _context.FilamentTypes.FirstOrDefaultAsync(f => f.Name == "Test PLA");
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
        ImportResponseDto result = await _importService.ImportCatalogAsync(catalog, ImportMode.Merge);

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
        ImportResponseDto result = await _importService.ImportCatalogAsync(catalog, ImportMode.Merge);

        // Assert
        result.Success.Should().BeTrue();
        result.Statistics.PrinterModelsImported.Should().Be(1);

        PrinterModel? printerModel = await _context.PrinterModels.FirstOrDefaultAsync(p => p.Name == "Test Printer");
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
        ImportResponseDto result = await _importService.ImportFullBackupAsync(backup, ImportMode.Merge);

        // Assert
        result.Success.Should().BeTrue();
        result.Statistics.ManufacturersImported.Should().Be(1);

        List<Manufacturer> manufacturers = await _context.Manufacturers.ToListAsync();
        manufacturers.Should().HaveCount(2); // 1 existing + 1 from backup
        manufacturers.Should().Contain(m => m.Name == "New Manufacturer From Backup");
    }

    [Fact]
    public async Task ImportFullBackupAsync_MergeMode_WhenCatalogHasErrors_ContinuesImportingLocationsAndPrinters()
    {
        var manufacturer = new Manufacturer { Id = Guid.NewGuid(), Name = "Existing Manufacturer" };
        var model = new PrinterModel
        {
            Id = Guid.NewGuid(),
            Name = "Existing Model",
            ManufacturerId = manufacturer.Id,
        };
        _ = _context.Manufacturers.Add(manufacturer);
        _ = _context.PrinterModels.Add(model);
        await _context.SaveChangesAsync();

        var backup = new FullBackupExportDto
        {
            Catalog = new CatalogExportDto
            {
                PrinterModels =
                [
                    new PrinterModelExportDto
                    {
                        Name = "Invalid Model",
                        ManufacturerName = "Missing Manufacturer",
                    },
                ],
            },
            Locations =
            [
                new LocationExportDto { Name = "Merged Location" },
            ],
            Printers =
            [
                new PrinterExportDto
                {
                    Name = "Merged Printer",
                    ServerUrl = "http://merged-printer",
                    ModelName = model.Name,
                    LocationName = "Merged Location",
                },
            ],
        };

        ImportResponseDto result = await _importService.ImportFullBackupAsync(backup, ImportMode.Merge);

        result.Success.Should().BeFalse();
        result.Errors.Should().Contain(error => error.Contains("Missing Manufacturer"));
        (await _context.Locations.Select(location => location.Name).ToListAsync()).Should().Contain("Merged Location");
        (await _context.Printers.Select(printer => printer.Name).ToListAsync()).Should().Contain("Merged Printer");
    }

    [Fact]
    public async Task ImportFullBackupAsync_LocationNameContainsSlash_RejectsLocationAndContinuesImportingOthers()
    {
        // A '/' in Name would be indistinguishable from a path separator in the materialized
        // Path, letting this location be misidentified as a descendant of an unrelated location
        // during subtree Path-prefix matching (EfLocationRepository.GetDescendantsAsync et al.).
        // LocationService.CreateLocationAsync/UpdateLocationAsync reject this for API-created
        // locations; ImportLocationsAsync must reject it too since it writes directly to the
        // DbContext, bypassing those service-layer guards.
        var backup = new FullBackupExportDto
        {
            Locations =
            [
                new LocationExportDto { Name = "Foo/Bar" },
                new LocationExportDto { Name = "Valid Location" },
            ],
            Printers = new List<PrinterExportDto>(),
        };

        ImportResponseDto result = await _importService.ImportFullBackupAsync(backup, ImportMode.Merge);

        result.Errors.Should().Contain(error => error.Contains("Foo/Bar") && error.Contains("cannot contain"));
        List<string> locationNames = await _context.Locations.Select(location => location.Name).ToListAsync();
        locationNames.Should().NotContain("Foo/Bar");
        locationNames.Should().Contain("Valid Location");
    }
}
