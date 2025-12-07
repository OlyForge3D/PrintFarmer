using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Farm.Importing.Services.Adapters;
using Farm.Importing.Services.Import;
using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using FluentAssertions;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Farm.Web.Api.Tests.Importing;

public class ImportParserServiceTests
{
    [Fact]
    public async Task ParseCsvAsync_WithQuotedFields_PreservesCommas()
    {
        string csv = "Name,ServerUrl,Notes\n\"My, Printer\",http://printer.local,\"note,1\"";
        var service = new ImportParserService();

        using var ms = new MemoryStream(Encoding.UTF8.GetBytes(csv));
        var (dtos, errors) = await service.ParseCsvAsync(ms, CancellationToken.None);

        errors.Should().BeEmpty();
        dtos.Should().ContainSingle();
        dtos[0].Name.Should().Be("My, Printer");
        dtos[0].Notes.Should().Be("note,1");
    }

    [Fact]
    public async Task ParseJsonAsync_WithInvalidPayload_ReturnsError()
    {
        var service = new ImportParserService();
        using var ms = new MemoryStream(Encoding.UTF8.GetBytes("not-json"));

        var (dtos, errors) = await service.ParseJsonAsync(ms, CancellationToken.None);

        dtos.Should().BeEmpty();
        errors.Should().ContainSingle();
    }
}

public class ImportProcessorServiceTests
{
    [Fact]
    public async Task ProcessAsync_CreatesPrinterAndStripsPort()
    {
        Guid manufacturerId = Guid.NewGuid();
        Guid modelId = Guid.NewGuid();

        await using var db = CreateDb();
        db.Manufacturers.Add(new Manufacturer { Id = manufacturerId, Name = "DefaultMan" });
        db.Models.Add(new PrinterModel { Id = modelId, Name = "DefaultModel", ManufacturerId = manufacturerId });
        await db.SaveChangesAsync();

        var validator = new AllowCreateValidator();
        var capabilityDiscovery = new NoopCapabilityDiscovery();
        var defaultCatalog = new DefaultCatalogStub(manufacturerId, modelId);
        var processor = new ImportProcessorService(db, validator, capabilityDiscovery, defaultCatalog);

        var dto = new CreatePrinterDto
        {
            Name = "Printer-One",
            ServerUrl = "http://example.local:8080/api",
            Backend = PrinterBackend.Moonraker
        };

        var results = await processor.ProcessAsync(new[] { dto }, "create", CancellationToken.None);

        results.Should().ContainSingle();
        results[0].Status.Should().Be("Imported");

        var saved = await db.Printers.SingleAsync();
        saved.ServerUrl.Should().Be("http://example.local");
        saved.ManufacturerId.Should().Be(manufacturerId);
        saved.ModelId.Should().Be(modelId);
    }

    private static AppDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        return new AppDbContext(options);
    }

    private sealed class AllowCreateValidator : AbstractValidator<CreatePrinterDto>
    {
        public AllowCreateValidator()
        {
            RuleFor(x => x.Name).NotEmpty();
            RuleFor(x => x.ServerUrl).NotEmpty();
        }
    }

    private sealed class NoopCapabilityDiscovery : IPrinterCapabilityDiscoveryAdapter
    {
        public Task<PrinterCapabilities?> DiscoverCapabilitiesAsync(Printer printer, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<PrinterCapabilities?>(null);
        }
    }

    private sealed class DefaultCatalogStub(Guid manufacturerId, Guid modelId) : IDefaultCatalogAdapter
    {
        public Task<(Guid ManufacturerId, Guid ModelId)> GetDefaultCatalogIdsAsync()
        {
            return Task.FromResult((manufacturerId, modelId));
        }
    }
}
