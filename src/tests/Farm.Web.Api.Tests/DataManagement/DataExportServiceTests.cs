using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Dtos.DataManagement;
using Farm.Infrastructure.Services.DataManagement;
using Farm.Infrastructure.Services.Security;
using Farm.Infrastructure.Telemetry;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;

namespace Farm.Web.Api.Tests.DataManagement;

public class DataExportServiceTests
{
    private readonly AppDbContext _context;
    private readonly Mock<IUnifiedLoggingService> _loggerMock;
    private readonly Mock<ISensitiveDataProtector> _sensitiveDataProtectorMock;
    private readonly DataExportService _exportService;

    public DataExportServiceTests()
    {
        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName: $"TestDb_{Guid.NewGuid()}")
            .Options;
        _context = new AppDbContext(options);

        _loggerMock = new Mock<IUnifiedLoggingService>();
        _sensitiveDataProtectorMock = new Mock<ISensitiveDataProtector>();
        _sensitiveDataProtectorMock
            .Setup(x => x.Unprotect(It.IsAny<string?>()))
            .Returns<string?>(s =>
            {
                if (string.IsNullOrEmpty(s))
                {
                    return s;
                }

                const string prefix = "prot:";
                return s.StartsWith(prefix, StringComparison.Ordinal) ? s[prefix.Length..] : null;
            });

        _exportService = new DataExportService(_context, _loggerMock.Object, _sensitiveDataProtectorMock.Object);
    }

    [Fact]
    public async Task ExportCatalogAsync_WithData_ReturnsValidCatalog()
    {
        // Arrange
        var manufacturer = new Manufacturer { Name = "Test Manufacturer" };
        await _context.Manufacturers.AddAsync(manufacturer);
        await _context.SaveChangesAsync();

        var filamentType = new FilamentType
        {
            Name = "PLA",
            DefaultHotendTemp = 205,
            DefaultBedTemp = 60
        };
        await _context.FilamentTypes.AddAsync(filamentType);
        await _context.SaveChangesAsync();

        // Act
        CatalogExportDto catalog = await _exportService.ExportCatalogAsync();

        // Assert
        catalog.Should().NotBeNull();
        catalog.Manufacturers.Should().Contain(m => m.Name == "Test Manufacturer");
        catalog.FilamentTypes.Should().Contain(f => f.Name == "PLA");
        catalog.FilamentTypes.First(f => f.Name == "PLA").DefaultHotendTemp.Should().Be(205);
    }

    [Fact]
    public async Task ExportCatalogAsync_EmptyDatabase_ReturnsEmptyCatalog()
    {
        // Act
        CatalogExportDto catalog = await _exportService.ExportCatalogAsync();

        // Assert
        catalog.Should().NotBeNull();
        catalog.Manufacturers.Should().BeEmpty();
        catalog.FilamentTypes.Should().BeEmpty();
        catalog.PrinterModels.Should().BeEmpty();
        catalog.Hotends.Should().BeEmpty();
        catalog.Extruders.Should().BeEmpty();
        catalog.Toolheads.Should().BeEmpty();
        catalog.Nozzles.Should().BeEmpty();
    }

    [Fact]
    public async Task ExportPrintersAsync_WithPrinters_ReturnsValidPrinters()
    {
        // Arrange
        var manufacturer = new Manufacturer { Name = "Prusa" };
        await _context.Manufacturers.AddAsync(manufacturer);
        await _context.SaveChangesAsync();

        var model = new PrinterModel
        {
            Name = "MK4S",
            ManufacturerId = manufacturer.Id,
            DefaultBackend = 1, // PrusaLink
            MaxX = 250,
            MaxY = 210,
            MaxZ = 220
        };
        await _context.PrinterModels.AddAsync(model);
        await _context.SaveChangesAsync();

        // Act
        List<PrinterExportDto> printers = await _exportService.ExportPrintersAsync();

        // Assert
        printers.Should().BeEmpty(); // No actual printer instances, just models
    }

    [Fact]
    public async Task ExportFullBackupAsync_WithData_ReturnsCompleteBackup()
    {
        // Arrange
        var manufacturer = new Manufacturer { Name = "Test Manufacturer" };
        await _context.Manufacturers.AddAsync(manufacturer);
        await _context.SaveChangesAsync();

        // Act
        FullBackupExportDto backup = await _exportService.ExportFullBackupAsync();

        // Assert
        backup.Should().NotBeNull();
        backup.Catalog.Should().NotBeNull();
        backup.Catalog.Manufacturers.Should().Contain(m => m.Name == "Test Manufacturer");
        backup.Printers.Should().NotBeNull();
        backup.Locations.Should().NotBeNull();
        backup.ExportedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ExportCatalogAsync_WithPrinterModel_IncludesModelDetails()
    {
        // Arrange
        var manufacturer = new Manufacturer { Name = "Voron" };
        await _context.Manufacturers.AddAsync(manufacturer);
        await _context.SaveChangesAsync();

        var model = new PrinterModel
        {
            Name = "Voron 2.4 350",
            ManufacturerId = manufacturer.Id,
            DefaultBackend = 0, // Moonraker
            MaxX = 350,
            MaxY = 350,
            MaxZ = 350,
            MotionType = 1, // CoreXY
            HasHeatedBed = true,
            HasEnclosure = true
        };
        await _context.PrinterModels.AddAsync(model);
        await _context.SaveChangesAsync();

        // Act
        CatalogExportDto catalog = await _exportService.ExportCatalogAsync();

        // Assert
        catalog.PrinterModels.Should().Contain(p => p.Name == "Voron 2.4 350");
        PrinterModelExportDto exportedModel = catalog.PrinterModels.First(p => p.Name == "Voron 2.4 350");
        exportedModel.ManufacturerName.Should().Be("Voron");
        exportedModel.MaxX.Should().Be(350);
        exportedModel.MaxY.Should().Be(350);
        exportedModel.MaxZ.Should().Be(350);
        exportedModel.MotionType.Should().Be(1); // CoreXY
        exportedModel.HasHeatedBed.Should().BeTrue();
    }
}
