using System.Text;
using System.Text.Json;
using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Farm.Web.Api.Tests.Integration;

/// <summary>
/// Facade integration tests for the printer import workflow.
/// Tests file validation, parsing, and error handling for CSV/JSON imports.
/// Note: Actual database persistence is tested in PrintersServiceTests.
/// </summary>
[Trait("Category", "Integration")]
[Trait("Category", "Facade")]
[TestTiming]
public sealed class PrinterImportFacadeIntegrationTests : IClassFixture<CustomWebApplicationFactory>, IDisposable
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly AppDbContext _dbContext;

    public PrinterImportFacadeIntegrationTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _dbContext = factory.Services.CreateAsyncScope().ServiceProvider.GetRequiredService<AppDbContext>();
    }

    public void Dispose()
    {
        _dbContext?.Dispose();
    }

    private IFormFile CreateCsvFormFile(string filename, string csvContent)
    {
        var bytes = Encoding.UTF8.GetBytes(csvContent);
        var stream = new MemoryStream(bytes);
        return new FormFile(stream, 0, bytes.Length, "file", filename)
        {
            Headers = new HeaderDictionary(),
            ContentType = "text/csv"
        };
    }

    private IFormFile CreateJsonFormFile(string filename, string jsonContent)
    {
        var bytes = Encoding.UTF8.GetBytes(jsonContent);
        var stream = new MemoryStream(bytes);
        return new FormFile(stream, 0, bytes.Length, "file", filename)
        {
            Headers = new HeaderDictionary(),
            ContentType = "application/json"
        };
    }

    // File Validation Tests

    [Fact]
    public async Task ImportFromFileAsync_WithEmptyCsvFile_ThrowsArgumentException()
    {
        // Arrange
        var printersService = _factory.Services.CreateAsyncScope().ServiceProvider
            .GetRequiredService<Farm.Infrastructure.Services.Printers.IPrintersService>();

        var csvContent = "";
        var file = CreateCsvFormFile("empty.csv", csvContent);

        // Act & Assert
        var exception = await Assert.ThrowsAsync<ArgumentException>(async () =>
        {
            using (var stream = file.OpenReadStream())
            {
                await printersService.ImportFromStreamAsync(stream, file.FileName, "skip", CancellationToken.None);
            }
        });
        exception.Message.Should().Contain("empty");
    }

    [Fact]
    public async Task ImportFromFileAsync_WithHeaderOnlyCsvFile_ThrowsInvalidOperationException()
    {
        // Arrange
        var printersService = _factory.Services.CreateAsyncScope().ServiceProvider
            .GetRequiredService<Farm.Infrastructure.Services.Printers.IPrintersService>();

        var csvContent = "Name,IpAddress,Backend\n";
        var file = CreateCsvFormFile("header-only.csv", csvContent);

        // Act & Assert
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            using (var stream = file.OpenReadStream()) { await printersService.ImportFromStreamAsync(stream, file.FileName, "skip", CancellationToken.None); }
        });
        exception.Message.Should().Contain("No valid printer");
    }

    [Fact]
    public async Task ImportFromFileAsync_WithInvalidJsonFile_ThrowsInvalidOperationException()
    {
        // Arrange
        var printersService = _factory.Services.CreateAsyncScope().ServiceProvider
            .GetRequiredService<Farm.Infrastructure.Services.Printers.IPrintersService>();

        var jsonContent = "{ invalid json }";
        var file = CreateJsonFormFile("invalid.json", jsonContent);

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            using (var stream = file.OpenReadStream()) { await printersService.ImportFromStreamAsync(stream, file.FileName, "skip", CancellationToken.None); }
        });
    }

    [Fact]
    public async Task ImportFromFileAsync_WithInvalidFileExtension_ThrowsArgumentException()
    {
        // Arrange
        var printersService = _factory.Services.CreateAsyncScope().ServiceProvider
            .GetRequiredService<Farm.Infrastructure.Services.Printers.IPrintersService>();

        var content = "some content";
        var bytes = Encoding.UTF8.GetBytes(content);
        var stream = new MemoryStream(bytes);
        var file = new FormFile(stream, 0, bytes.Length, "file", "printers.txt")
        {
            Headers = new HeaderDictionary(),
            ContentType = "text/plain"
        };

        // Act & Assert
        var exception = await Assert.ThrowsAsync<ArgumentException>(async () =>
        {
            using (var stream = file.OpenReadStream()) { await printersService.ImportFromStreamAsync(stream, file.FileName, "skip", CancellationToken.None); }
        });
        exception.Message.Should().Contain("CSV or JSON");
    }

    [Fact]
    public async Task ImportFromFileAsync_WithMissingRequiredCsvColumns_ThrowsInvalidOperationException()
    {
        // Arrange
        var printersService = _factory.Services.CreateAsyncScope().ServiceProvider
            .GetRequiredService<Farm.Infrastructure.Services.Printers.IPrintersService>();

        // Missing 'Backend' required column
        var csvContent = "Name,IpAddress\nPrinter1,192.168.1.100\n";
        var file = CreateCsvFormFile("missing-cols.csv", csvContent);

        // Act & Assert
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            using (var stream = file.OpenReadStream()) { await printersService.ImportFromStreamAsync(stream, file.FileName, "skip", CancellationToken.None); }
        });
        exception.Message.Should().Contain("required columns");
    }

    // Successful Parsing Tests

    [Fact]
    public async Task ImportFromFileAsync_WithValidJsonFile_ParsesSuccessfully()
    {
        // Arrange
        var printersService = _factory.Services.CreateAsyncScope().ServiceProvider
            .GetRequiredService<Farm.Infrastructure.Services.Printers.IPrintersService>();

        var jsonContent = JsonSerializer.Serialize(new[]
        {
            new CreatePrinterDto 
            { 
                Name = "JsonPrinter1", 
                IpAddress = "192.168.1.110",
                Backend = PrinterBackend.Moonraker 
            },
            new CreatePrinterDto 
            { 
                Name = "JsonPrinter2", 
                IpAddress = "192.168.1.111",
                Backend = PrinterBackend.PrusaLink 
            }
        });
        var file = CreateJsonFormFile("printers.json", jsonContent);

        // Act
        object result;
        using (var stream = file.OpenReadStream())
        {
            result = await printersService.ImportFromStreamAsync(stream, file.FileName, "skip", CancellationToken.None);
        }

        // Assert - Result should not be null (indicates successful import)
        result.Should().NotBeNull();
    }

    [Fact]
    public async Task ImportFromFileAsync_WithValidCsvFile_ParsesSuccessfully()
    {
        // Arrange
        var printersService = _factory.Services.CreateAsyncScope().ServiceProvider
            .GetRequiredService<Farm.Infrastructure.Services.Printers.IPrintersService>();

        var csvContent = @"Name,IpAddress,Backend,Notes
Prusa1,192.168.1.100,Moonraker,First printer
Prusa2,192.168.1.101,PrusaLink,Second printer
Prusa3,192.168.1.102,SDCP,Third printer
";
        var file = CreateCsvFormFile("printers-detailed.csv", csvContent);

        // Act
        object result;
        using (var stream = file.OpenReadStream())
        {
            result = await printersService.ImportFromStreamAsync(stream, file.FileName, "skip", CancellationToken.None);
        }

        // Assert
        result.Should().NotBeNull();
    }

    [Fact]
    public async Task ImportFromFileAsync_WithQuotedFieldsInCsv_HandlesCommasInFields()
    {
        // Arrange
        var printersService = _factory.Services.CreateAsyncScope().ServiceProvider
            .GetRequiredService<Farm.Infrastructure.Services.Printers.IPrintersService>();

        // CSV with quoted fields containing commas
        var csvContent = @"Name,IpAddress,Backend,Notes
""Printer, Special Model"",192.168.1.100,Moonraker,""Note with, comma inside""
SimpleNamePrinter,192.168.1.101,Moonraker,SimpleNote
";
        var file = CreateCsvFormFile("quoted.csv", csvContent);

        // Act
        object result;
        using (var stream = file.OpenReadStream())
        {
            result = await printersService.ImportFromStreamAsync(stream, file.FileName, "skip", CancellationToken.None);
        }

        // Assert
        result.Should().NotBeNull();
    }

    [Fact]
    public async Task ImportFromFileAsync_WithMultiplePrinterBackends_ParsesAllTypes()
    {
        // Arrange
        var printersService = _factory.Services.CreateAsyncScope().ServiceProvider
            .GetRequiredService<Farm.Infrastructure.Services.Printers.IPrintersService>();

        var csvContent = @"Name,IpAddress,Backend
MoonrakerPrinter,192.168.1.100,Moonraker
PrusaLinkPrinter,192.168.1.101,PrusaLink
SDCPPrinter,192.168.1.102,SDCP
OctoPrintPrinter,192.168.1.103,OctoPrint
";
        var file = CreateCsvFormFile("mixed-backends.csv", csvContent);

        // Act
        object result;
        using (var stream = file.OpenReadStream())
        {
            result = await printersService.ImportFromStreamAsync(stream, file.FileName, "skip", CancellationToken.None);
        }

        // Assert
        result.Should().NotBeNull();
    }

    [Fact]
    public async Task ImportFromFileAsync_WithOptionalFields_ParsesSuccessfully()
    {
        // Arrange
        var printersService = _factory.Services.CreateAsyncScope().ServiceProvider
            .GetRequiredService<Farm.Infrastructure.Services.Printers.IPrintersService>();

        var csvContent = @"Name,IpAddress,Backend,Notes,ManufacturerName,ModelName,ApiKey,IsEnabled,BackendPort,CameraStreamUrl
Printer1,192.168.1.100,Moonraker,Test,Prusa,CORE One,key123,true,7125,http://cam.local
Printer2,192.168.1.101,PrusaLink,Test2,Prusa,Mk3S+,key456,false,8008,http://cam2.local
";
        var file = CreateCsvFormFile("with-optional.csv", csvContent);

        // Act
        object result;
        using (var stream = file.OpenReadStream())
        {
            result = await printersService.ImportFromStreamAsync(stream, file.FileName, "skip", CancellationToken.None);
        }

        // Assert
        result.Should().NotBeNull();
    }
}

