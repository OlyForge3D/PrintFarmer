using System.Text;
using System.Text.Json;
using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Discovery;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Printers;
using Farm.Web.Api.Tests.TestInfrastructure;
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
public sealed class PrinterImportFacadeIntegrationTests(CustomWebApplicationFactory factory) : IClassFixture<CustomWebApplicationFactory>, IDisposable
{
    private readonly CustomWebApplicationFactory _factory = factory;
    private readonly AppDbContext _dbContext = factory.Services.CreateAsyncScope().ServiceProvider.GetRequiredService<AppDbContext>();

    public void Dispose()
    {
        _dbContext?.Dispose();
    }

    private IFormFile CreateCsvFormFile(string filename, string csvContent)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(csvContent);
        var stream = new MemoryStream(bytes);
        return new FormFile(stream, 0, bytes.Length, "file", filename)
        {
            Headers = new HeaderDictionary(),
            ContentType = "text/csv"
        };
    }

    private IFormFile CreateJsonFormFile(string filename, string jsonContent)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(jsonContent);
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
        IPrintersService printersService = _factory.Services.CreateAsyncScope().ServiceProvider
            .GetRequiredService<Farm.Infrastructure.Services.Printers.IPrintersService>();

        string csvContent = "";
        IFormFile file = CreateCsvFormFile("empty.csv", csvContent);

        // Act & Assert
        ArgumentException exception = await Assert.ThrowsAsync<ArgumentException>(async () =>
        {
            using (Stream stream = file.OpenReadStream())
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
        IPrintersService printersService = _factory.Services.CreateAsyncScope().ServiceProvider
            .GetRequiredService<Farm.Infrastructure.Services.Printers.IPrintersService>();

        string csvContent = "Name,IpAddress,Backend\n";
        IFormFile file = CreateCsvFormFile("header-only.csv", csvContent);

        // Act & Assert
        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            using (Stream stream = file.OpenReadStream())
            { await printersService.ImportFromStreamAsync(stream, file.FileName, "skip", CancellationToken.None); }
        });
        exception.Message.Should().Contain("No valid printer");
    }

    [Fact]
    public async Task ImportFromFileAsync_WithInvalidJsonFile_ThrowsInvalidOperationException()
    {
        // Arrange
        IPrintersService printersService = _factory.Services.CreateAsyncScope().ServiceProvider
            .GetRequiredService<Farm.Infrastructure.Services.Printers.IPrintersService>();

        string jsonContent = "{ invalid json }";
        IFormFile file = CreateJsonFormFile("invalid.json", jsonContent);

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            using (Stream stream = file.OpenReadStream())
            { await printersService.ImportFromStreamAsync(stream, file.FileName, "skip", CancellationToken.None); }
        });
    }

    [Fact]
    public async Task ImportFromFileAsync_WithInvalidFileExtension_ThrowsArgumentException()
    {
        // Arrange
        IPrintersService printersService = _factory.Services.CreateAsyncScope().ServiceProvider
            .GetRequiredService<Farm.Infrastructure.Services.Printers.IPrintersService>();

        string content = "some content";
        byte[] bytes = Encoding.UTF8.GetBytes(content);
        var stream = new MemoryStream(bytes);
        var file = new FormFile(stream, 0, bytes.Length, "file", "printers.txt")
        {
            Headers = new HeaderDictionary(),
            ContentType = "text/plain"
        };

        // Act & Assert
        ArgumentException exception = await Assert.ThrowsAsync<ArgumentException>(async () =>
        {
            using (Stream stream = file.OpenReadStream())
            { await printersService.ImportFromStreamAsync(stream, file.FileName, "skip", CancellationToken.None); }
        });
        exception.Message.Should().Contain("CSV or JSON");
    }

    [Fact]
    public async Task ImportFromFileAsync_WithMissingRequiredCsvColumns_ThrowsInvalidOperationException()
    {
        // Arrange
        IPrintersService printersService = _factory.Services.CreateAsyncScope().ServiceProvider
            .GetRequiredService<Farm.Infrastructure.Services.Printers.IPrintersService>();

        // Missing 'Backend' required column
        string csvContent = "Name,IpAddress\nPrinter1,192.168.1.100\n";
        IFormFile file = CreateCsvFormFile("missing-cols.csv", csvContent);

        // Act & Assert
        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            using (Stream stream = file.OpenReadStream())
            { await printersService.ImportFromStreamAsync(stream, file.FileName, "skip", CancellationToken.None); }
        });
        exception.Message.Should().Contain("required columns");
    }

    // Successful Parsing Tests

    [Fact]
    public async Task ImportFromFileAsync_WithValidJsonFile_ParsesSuccessfully()
    {
        // Arrange
        IPrintersService printersService = _factory.Services.CreateAsyncScope().ServiceProvider
            .GetRequiredService<Farm.Infrastructure.Services.Printers.IPrintersService>();

        string jsonContent = JsonSerializer.Serialize(new[]
        {
            new CreatePrinterFromDiscoveryDto
            {
                Name = "JsonPrinter1",
                IpAddress = "192.168.1.110",
                Backend = PrinterBackend.Moonraker
            },
            new CreatePrinterFromDiscoveryDto
            {
                Name = "JsonPrinter2",
                IpAddress = "192.168.1.111",
                Backend = PrinterBackend.PrusaLink
            }
        });
        IFormFile file = CreateJsonFormFile("printers.json", jsonContent);

        // Act
        object result;
        using (Stream stream = file.OpenReadStream())
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
        IPrintersService printersService = _factory.Services.CreateAsyncScope().ServiceProvider
            .GetRequiredService<Farm.Infrastructure.Services.Printers.IPrintersService>();

        string csvContent = @"Name,IpAddress,Backend,Notes
Prusa1,192.168.1.100,Moonraker,First printer
Prusa2,192.168.1.101,PrusaLink,Second printer
Prusa3,192.168.1.102,SDCP,Third printer
";
        IFormFile file = CreateCsvFormFile("printers-detailed.csv", csvContent);

        // Act
        object result;
        using (Stream stream = file.OpenReadStream())
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
        IPrintersService printersService = _factory.Services.CreateAsyncScope().ServiceProvider
            .GetRequiredService<Farm.Infrastructure.Services.Printers.IPrintersService>();

        // CSV with quoted fields containing commas
        string csvContent = @"Name,IpAddress,Backend,Notes
""Printer, Special Model"",192.168.1.100,Moonraker,""Note with, comma inside""
SimpleNamePrinter,192.168.1.101,Moonraker,SimpleNote
";
        IFormFile file = CreateCsvFormFile("quoted.csv", csvContent);

        // Act
        object result;
        using (Stream stream = file.OpenReadStream())
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
        IPrintersService printersService = _factory.Services.CreateAsyncScope().ServiceProvider
            .GetRequiredService<Farm.Infrastructure.Services.Printers.IPrintersService>();

        string csvContent = @"Name,IpAddress,Backend
MoonrakerPrinter,192.168.1.100,Moonraker
PrusaLinkPrinter,192.168.1.101,PrusaLink
SDCPPrinter,192.168.1.102,SDCP
OctoPrintPrinter,192.168.1.103,OctoPrint
";
        IFormFile file = CreateCsvFormFile("mixed-backends.csv", csvContent);

        // Act
        object result;
        using (Stream stream = file.OpenReadStream())
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
        IPrintersService printersService = _factory.Services.CreateAsyncScope().ServiceProvider
            .GetRequiredService<Farm.Infrastructure.Services.Printers.IPrintersService>();

        string csvContent = @"Name,IpAddress,Backend,Notes,ManufacturerName,ModelName,ApiKey,IsEnabled,BackendPort,CameraStreamUrl
Printer1,192.168.1.100,Moonraker,Test,Prusa,CORE One,key123,true,7125,http://cam.local
Printer2,192.168.1.101,PrusaLink,Test2,Prusa,Mk3S+,key456,false,8008,http://cam2.local
";
        IFormFile file = CreateCsvFormFile("with-optional.csv", csvContent);

        // Act
        object result;
        using (Stream stream = file.OpenReadStream())
        {
            result = await printersService.ImportFromStreamAsync(stream, file.FileName, "skip", CancellationToken.None);
        }

        // Assert
        result.Should().NotBeNull();
    }
}

