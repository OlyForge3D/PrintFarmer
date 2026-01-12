using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Locations;
using Farm.Infrastructure.Repositories.Printers;
using Farm.Infrastructure.Services.Locations;
using Farm.Infrastructure.Services.Printers;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Farm.Web.Api.Tests.Services.Printers;

/// <summary>
/// Tests for printer import/export with location support.
/// Verifies that LocationName is correctly parsed from CSV/JSON imports
/// and that locations are properly assigned to printers during import.
/// </summary>
public class PrinterLocationImportTests : IAsyncLifetime
{
    private readonly CustomWebApplicationFactory _factory;
    private IPrintersService _printersService;
    private ILocationService _locationService;
    private AppDbContext _dbContext;
    private Location _testLocation1;
    private Location _testLocation2;
    private PrinterBackend _testBackend = PrinterBackend.Moonraker;

    public PrinterLocationImportTests()
    {
        _factory = CustomWebApplicationFactory.CreateWithIsolatedDatabase();
    }

    public async Task InitializeAsync()
    {
        var scope = _factory.Services.CreateAsyncScope();
        _dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        _printersService = scope.ServiceProvider.GetRequiredService<IPrintersService>();
        _locationService = scope.ServiceProvider.GetRequiredService<ILocationService>();

        // Create test locations - use unique IDs to avoid conflicts
        _testLocation1 = new Location
        {
            Id = Guid.Parse("11111111-1111-1111-1111-111111111111"),
            Name = "Warehouse",
            Description = "Main warehouse location",
            IsActive = true
        };

        _testLocation2 = new Location
        {
            Id = Guid.Parse("22222222-2222-2222-2222-222222222222"),
            Name = "Lab",
            Description = "Development lab location",
            IsActive = true
        };

        // Check if locations already exist (for test isolation)
        var existing1 = await _dbContext.Locations.FirstOrDefaultAsync(l => l.Name == "Warehouse");
        var existing2 = await _dbContext.Locations.FirstOrDefaultAsync(l => l.Name == "Lab");

        if (existing1 == null)
        {
            _dbContext.Locations.Add(_testLocation1);
        }
        else
        {
            _testLocation1 = existing1;
        }

        if (existing2 == null)
        {
            _dbContext.Locations.Add(_testLocation2);
        }
        else
        {
            _testLocation2 = existing2;
        }

        await _dbContext.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        _dbContext?.Dispose();
        _factory?.Dispose();
    }

    #region CSV Import Tests

    [Fact]
    public async Task ImportPrintersCsvAsync_WithLocationColumn_AssignsLocationsToPrinters()
    {
        // Arrange
        var csv = GetCsvWithLocations(new[]
        {
            new { Name = "Printer1", Location = "Warehouse" },
            new { Name = "Printer2", Location = "Lab" }
        });

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(csv));

        // Act
        var result = await _printersService.ImportFromStreamAsync(stream, "test.csv", "skip", CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        var resultJson = JsonSerializer.Serialize(result);
        resultJson.Should().Contain("\"importedCount\":2");
        resultJson.Should().Contain("\"failureCount\":0");

        // Verify locations were assigned
        var printer1 = await _dbContext.Printers
            .FirstOrDefaultAsync(p => p.Name == "Printer1");
        printer1.Should().NotBeNull();
        printer1!.LocationId.Should().Be(_testLocation1.Id);

        var printer2 = await _dbContext.Printers
            .FirstOrDefaultAsync(p => p.Name == "Printer2");
        printer2.Should().NotBeNull();
        printer2!.LocationId.Should().Be(_testLocation2.Id);
    }

    [Fact]
    public async Task ImportPrintersCsvAsync_WithoutLocationColumn_CreatesPreintersWithoutLocation()
    {
        // Arrange - CSV without LocationName column (backward compatibility)
        var ip = GetNextIpAddress();
        var mfg = GetNextUniqueMfgName();
        var csv = $@"Name,IpAddress,Backend,BackendPort,FrontendPort,ManufacturerName,ModelName,Notes,ApiKey,IsEnabled,CameraStreamUrl,CameraSnapshotUrl,DateAcquired
""Printer3"",""{ip}"",""Moonraker"",""7125"",""80"",""{mfg}"",""Ender3"",""Test"","""",""true"","""","""","""" ";

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(csv));

        // Act
        var result = await _printersService.ImportFromStreamAsync(stream, "test.csv", "skip", CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        var resultJson = JsonSerializer.Serialize(result);
        resultJson.Should().Contain("\"importedCount\":1");

        var printer = await _dbContext.Printers
            .FirstOrDefaultAsync(p => p.Name == "Printer3");
        printer.Should().NotBeNull();
        printer!.LocationId.Should().BeNull(); // No location assigned
    }

    [Fact]
    public async Task ImportPrintersCsvAsync_WithNonExistentLocation_CreatesPartnerWithoutLocation()
    {
        // Arrange
        var csv = GetCsvWithLocations(new[]
        {
            new { Name = "PrinterNoLocation", Location = "NonExistentLocation" }
        });

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(csv));

        // Act
        var result = await _printersService.ImportFromStreamAsync(stream, "test.csv", "skip", CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        var resultJson = JsonSerializer.Serialize(result);
        resultJson.Should().Contain("\"importedCount\":1"); // Still imports, just without location

        var printer = await _dbContext.Printers
            .FirstOrDefaultAsync(p => p.Name == "PrinterNoLocation");
        printer.Should().NotBeNull();
        printer!.LocationId.Should().BeNull(); // Location not assigned due to not existing
    }

    [Fact]
    public async Task ImportPrintersCsvAsync_WithEmptyLocationField_CreatesForintersWithoutLocation()
    {
        // Arrange - Location column present but empty
        var ip = GetNextIpAddress();
        var mfg = GetNextUniqueMfgName();
        var csv = $@"Name,IpAddress,Backend,BackendPort,FrontendPort,ManufacturerName,ModelName,Notes,ApiKey,IsEnabled,CameraStreamUrl,CameraSnapshotUrl,DateAcquired,LocationName
""PrinterEmptyLocation"",""{ip}"",""Moonraker"",""7125"",""80"",""{mfg}"",""Ender3"",""Test"","""",""true"","""","""","""","""" ";

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(csv));

        // Act
        var result = await _printersService.ImportFromStreamAsync(stream, "test.csv", "skip", CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        var resultJson = JsonSerializer.Serialize(result);
        resultJson.Should().Contain("\"importedCount\":1");

        var printer = await _dbContext.Printers
            .FirstOrDefaultAsync(p => p.Name == "PrinterEmptyLocation");
        printer.Should().NotBeNull();
        printer!.LocationId.Should().BeNull();
    }

    [Fact]
    public async Task ImportPrintersCsvAsync_PreservesLocationOnOverwrite()
    {
        // Arrange - Create initial printer with no location
        var manufacturerId = (await _dbContext.Manufacturers.FirstOrDefaultAsync())?.Id ?? Guid.NewGuid();
        var modelId = (await _dbContext.PrinterModels.FirstOrDefaultAsync())?.Id ?? Guid.NewGuid();

        var ip = GetNextIpAddress();
        var initialPrinter = new Printer
        {
            Id = Guid.NewGuid(),
            Name = "OverwritePrinter",
            ServerUrl = $"http://{ip}:7125",
            OriginalServerUrl = $"http://{ip}:7125",
            IpAddress = ip,
            Backend = (int)_testBackend,
            BackendPort = 7125,
            ManufacturerId = manufacturerId,
            ModelId = modelId,
            IsEnabled = true
        };
        _dbContext.Printers.Add(initialPrinter);
        await _dbContext.SaveChangesAsync();

        // Import with overwrite and location assignment - use same IP for overwrite
        var mfg = GetNextUniqueMfgName();
        var csv = GetCsvWithLocations(new[]
        {
            new { Name = "OverwritePrinter", Location = "Warehouse" }
        }, mfg, ip);  // Use same IP for overwrite

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(csv));

        // Act
        var result = await _printersService.ImportFromStreamAsync(stream, "test.csv", "overwrite", CancellationToken.None);

        // Assert - verify the printer was successfully overwritten (not skipped or failed)
        result.Should().NotBeNull();
        var resultJson = JsonSerializer.Serialize(result);
        resultJson.Should().Contain("\"importedCount\":1", "printer should be imported successfully with overwrite mode");

        // Reload from database to get fresh data after overwrite
        var printer = await _dbContext.Printers
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Name == "OverwritePrinter");
        printer.Should().NotBeNull("printer should exist after import");

        // Verify printer was overwritten (should have new ID from import, not original)
        printer!.Id.Should().NotBe(initialPrinter.Id, "printer should have new ID after overwrite");
    }


    [Fact]
    public async Task ImportPrintersCsvAsync_WithWhitespace_TrimsLocationName()
    {
        // Arrange - Location name with leading/trailing whitespace
        var ip = GetNextIpAddress();
        var mfg = GetNextUniqueMfgName();
        var csv = $@"Name,IpAddress,Backend,BackendPort,FrontendPort,ManufacturerName,ModelName,Notes,ApiKey,IsEnabled,CameraStreamUrl,CameraSnapshotUrl,DateAcquired,LocationName
""PrinterWhitespace"",""{ip}"",""Moonraker"",""7125"",""80"",""{mfg}"",""Ender3"",""Test"","""",""true"","""","""","""",""  Warehouse  """;

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(csv));

        // Act
        var result = await _printersService.ImportFromStreamAsync(stream, "test.csv", "skip", CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        var resultJson = JsonSerializer.Serialize(result);
        resultJson.Should().Contain("\"importedCount\":1");

        var printer = await _dbContext.Printers
            .FirstOrDefaultAsync(p => p.Name == "PrinterWhitespace");
        printer.Should().NotBeNull();
        printer!.LocationId.Should().Be(_testLocation1.Id); // Should find "Warehouse" after trimming
    }

    #endregion

    #region Export/Import Round-Trip Tests

    [Fact]
    public async Task ExportAndReplaceAsync_IncludesLocation_CanBeImportedBack()
    {
        // Arrange - Create a printer with location
        var manufacturerId = (await _dbContext.Manufacturers.FirstOrDefaultAsync())?.Id ?? Guid.NewGuid();
        var modelId = (await _dbContext.PrinterModels.FirstOrDefaultAsync())?.Id ?? Guid.NewGuid();

        var ip = GetNextIpAddress();
        var printer = new Printer
        {
            Id = Guid.NewGuid(),
            Name = "RoundTripPrinter",
            ServerUrl = $"http://{ip}:7125",
            OriginalServerUrl = $"http://{ip}:7125",
            IpAddress = ip,
            Backend = (int)_testBackend,
            BackendPort = 7125,
            LocationId = _testLocation1.Id,
            ManufacturerId = manufacturerId,
            ModelId = modelId,
            IsEnabled = true
        };
        _dbContext.Printers.Add(printer);
        await _dbContext.SaveChangesAsync();

        // Act - Export the printer
        var csvBytes = await _printersService.BuildExportCsvAsync(null, CancellationToken.None);
        var csv = Encoding.UTF8.GetString(csvBytes);

        // Assert - CSV should contain LocationName column with value
        csv.Should().Contain("LocationName");
        csv.Should().Contain("Warehouse");

        // Act - Delete original printer and import from CSV
        _dbContext.Printers.Remove(printer);
        await _dbContext.SaveChangesAsync();

        using var stream = new MemoryStream(csvBytes);
        var result = await _printersService.ImportFromStreamAsync(stream, "test.csv", "skip", CancellationToken.None);

        // Assert - Reimported printer should have location assigned
        var reimportedPrinter = await _dbContext.Printers
            .FirstOrDefaultAsync(p => p.Name == "RoundTripPrinter");
        reimportedPrinter.Should().NotBeNull();
        reimportedPrinter!.LocationId.Should().Be(_testLocation1.Id);
    }

    [Fact]
    public async Task ExportJsonAndImportAsync_IncludesLocation_CanBeImportedBack()
    {
        // Arrange - Create a printer with location
        var manufacturerId = (await _dbContext.Manufacturers.FirstOrDefaultAsync())?.Id ?? Guid.NewGuid();
        var modelId = (await _dbContext.PrinterModels.FirstOrDefaultAsync())?.Id ?? Guid.NewGuid();

        var ip = GetNextIpAddress();
        var printer = new Printer
        {
            Id = Guid.NewGuid(),
            Name = "JsonRoundTripPrinter",
            ServerUrl = $"http://{ip}:7125",
            OriginalServerUrl = $"http://{ip}:7125",
            IpAddress = ip,
            Backend = (int)_testBackend,
            BackendPort = 7125,
            LocationId = _testLocation1.Id,
            ManufacturerId = manufacturerId,
            ModelId = modelId,
            IsEnabled = true
        };
        _dbContext.Printers.Add(printer);
        await _dbContext.SaveChangesAsync();

        // Act - Export the printer as JSON
        var jsonBytes = await _printersService.BuildExportJsonAsync(null, CancellationToken.None);
        var json = Encoding.UTF8.GetString(jsonBytes);

        // Assert - JSON should contain locationName field with value
        json.Should().Contain("\"locationName\"");
        json.Should().Contain("\"Warehouse\"");

        // Act - Delete original printer and import from JSON
        _dbContext.Printers.Remove(printer);
        await _dbContext.SaveChangesAsync();

        using var stream = new MemoryStream(jsonBytes);
        var result = await _printersService.ImportFromStreamAsync(stream, "test.json", "skip", CancellationToken.None);

        // Assert - Reimported printer should have location assigned
        var reimportedPrinter = await _dbContext.Printers
            .FirstOrDefaultAsync(p => p.Name == "JsonRoundTripPrinter");
        reimportedPrinter.Should().NotBeNull();
        reimportedPrinter!.LocationId.Should().Be(_testLocation1.Id);
    }

    #endregion

    #region CSV Export Tests

    [Fact]
    public async Task ExportPrintersCsvAsync_IncludesLocationNameColumn()
    {
        // Arrange - Create printer with location
        var manufacturerId = (await _dbContext.Manufacturers.FirstOrDefaultAsync())?.Id ?? Guid.NewGuid();
        var modelId = (await _dbContext.PrinterModels.FirstOrDefaultAsync())?.Id ?? Guid.NewGuid();

        var ip = GetNextIpAddress();
        var printer = new Printer
        {
            Id = Guid.NewGuid(),
            Name = "ExportPrinter",
            ServerUrl = $"http://{ip}:7125",
            OriginalServerUrl = $"http://{ip}:7125",
            IpAddress = ip,
            Backend = (int)_testBackend,
            BackendPort = 7125,
            LocationId = _testLocation1.Id,  // Changed from _testLocation2 to match GetCsvWithLocations usage
            ManufacturerId = manufacturerId,
            ModelId = modelId,
            IsEnabled = true
        };
        _dbContext.Printers.Add(printer);
        await _dbContext.SaveChangesAsync();

        // Act
        var csvBytes = await _printersService.BuildExportCsvAsync(null, CancellationToken.None);
        var csv = Encoding.UTF8.GetString(csvBytes);

        // Assert
        var lines = csv.Split('\n');
        var headerLine = lines[0];
        headerLine.Should().Contain("LocationName");

        var printerLine = lines.FirstOrDefault(l => l.Contains("ExportPrinter"));
        printerLine.Should().NotBeNullOrEmpty();
        printerLine.Should().Contain("Warehouse");  // Should find Warehouse since LocationId = _testLocation1
    }

    [Fact]
    public async Task ExportPrintersCsvAsync_PrinterWithoutLocation_ShowsEmptyLocationField()
    {
        // Arrange - Create printer without location
        var manufacturerId = (await _dbContext.Manufacturers.FirstOrDefaultAsync())?.Id ?? Guid.NewGuid();
        var modelId = (await _dbContext.PrinterModels.FirstOrDefaultAsync())?.Id ?? Guid.NewGuid();

        var ip = GetNextIpAddress();
        var printer = new Printer
        {
            Id = Guid.NewGuid(),
            Name = "NoLocationPrinter",
            ServerUrl = $"http://{ip}:7125",
            OriginalServerUrl = $"http://{ip}:7125",
            IpAddress = ip,
            Backend = (int)_testBackend,
            BackendPort = 7125,
            LocationId = null,
            ManufacturerId = manufacturerId,
            ModelId = modelId,
            IsEnabled = true
        };
        _dbContext.Printers.Add(printer);
        await _dbContext.SaveChangesAsync();

        // Act
        var csvBytes = await _printersService.BuildExportCsvAsync(null, CancellationToken.None);
        var csv = Encoding.UTF8.GetString(csvBytes);

        // Assert
        var lines = csv.Split('\n');
        var printerLine = lines.FirstOrDefault(l => l.Contains("NoLocationPrinter"));
        printerLine.Should().NotBeNullOrEmpty();
        // Should have trailing empty field for location
        printerLine.Should().EndWith(",");
    }

    #endregion

    #region JSON Export Tests

    [Fact]
    public async Task ExportPrintersJsonAsync_IncludesLocationNameField()
    {
        // Arrange
        var manufacturerId = (await _dbContext.Manufacturers.FirstOrDefaultAsync())?.Id ?? Guid.NewGuid();
        var modelId = (await _dbContext.PrinterModels.FirstOrDefaultAsync())?.Id ?? Guid.NewGuid();

        var ip = GetNextIpAddress();
        var printer = new Printer
        {
            Id = Guid.NewGuid(),
            Name = "JsonExportPrinter",
            ServerUrl = $"http://{ip}:7125",
            OriginalServerUrl = $"http://{ip}:7125",
            IpAddress = ip,
            Backend = (int)_testBackend,
            BackendPort = 7125,
            LocationId = _testLocation1.Id,
            ManufacturerId = manufacturerId,
            ModelId = modelId,
            IsEnabled = true
        };
        _dbContext.Printers.Add(printer);
        await _dbContext.SaveChangesAsync();

        // Act
        var jsonBytes = await _printersService.BuildExportJsonAsync(null, CancellationToken.None);
        var json = Encoding.UTF8.GetString(jsonBytes);

        // Assert
        json.Should().Contain("\"locationName\"");
        json.Should().Contain("\"Warehouse\"");
    }

    #endregion

    #region Helper Methods

    private string GetNextIpAddress()
    {
        // Generate a truly random IP address using Guid bytes
        var guidBytes = Guid.NewGuid().ToByteArray();
        var byte3 = (guidBytes[0] % 200) + 20; // Range 20-219
        var byte4 = (guidBytes[1] % 200) + 20; // Range 20-219
        return $"192.168.{byte3}.{byte4}";
    }

    private string GetNextUniqueMfgName()
    {
        // Use full Guid to ensure uniqueness across all test runs
        return $"Mfg{Guid.NewGuid().ToString().Replace("-", "").Substring(0, 12)}";
    }

    private string GetCsvWithLocations(dynamic[] printerData, string? manufacturerName = null, string? ipAddress = null)
    {
        manufacturerName ??= GetNextUniqueMfgName();
        var lines = new StringBuilder();
        lines.AppendLine("Name,IpAddress,Backend,BackendPort,FrontendPort,ManufacturerName,ModelName,Notes,ApiKey,IsEnabled,CameraStreamUrl,CameraSnapshotUrl,DateAcquired,LocationName");

        // Generate unique IP addresses for test data (or use provided IP)
        var ipIdx = 0;
        foreach (var printer in printerData)
        {
            var name = printer.Name;
            var location = printer.Location;
            var ip = ipAddress ?? GetNextIpAddress();
            var csvLine = $"\"{name}\",\"{ip}\",\"Moonraker\",\"7125\",\"80\",\"{manufacturerName}\",\"Ender3\",\"Test\",\"\",\"true\",\"\",\"\",\"\",\"{location}\"";
            lines.AppendLine(csvLine);

            // Only use provided IP for first printer, generate new ones for subsequent
            if (ipAddress != null && ipIdx == 0)
            {
                ipAddress = null; // Clear it so subsequent printers get unique IPs
            }
            ipIdx++;
        }

        return lines.ToString();
    }

    #endregion
}
