using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Printers;
using Farm.Web.Api.Tests.TestInfrastructure;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Farm.Web.Api.Tests.Services.Printers;

/// <summary>
/// Regression tests for the printer list endpoint (<see cref="PrintersService.GetAllCompleteDtosAsync"/>)
/// carrying the concurrency token. The React printers page sources printer objects from the
/// <c>GET /api/printers</c> list payload and every mutation guards on <c>rowVersion</c>; when the list
/// DTO omitted it, every printer mutation was blocked with "Printer revision unavailable".
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public class PrinterListDtoRowVersionTests : IAsyncLifetime
{
    private readonly CustomWebApplicationFactory _factory;
    private AsyncServiceScope _scope;
    private AppDbContext _dbContext = null!;
    private IPrintersService _printersService = null!;

    public PrinterListDtoRowVersionTests()
    {
        _factory = CustomWebApplicationFactory.CreateWithIsolatedDatabase();
    }

    public async Task InitializeAsync()
    {
        _scope = _factory.Services.CreateAsyncScope();
        _dbContext = _scope.ServiceProvider.GetRequiredService<AppDbContext>();
        _printersService = _scope.ServiceProvider.GetRequiredService<IPrintersService>();
        await Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _scope.DisposeAsync();
        _factory?.Dispose();
    }

    private async Task<Guid> SeedPrinterAsync()
    {
        Guid manufacturerId = Guid.NewGuid();
        Guid modelId = Guid.NewGuid();
        Guid printerId = Guid.NewGuid();

        _dbContext.Manufacturers.Add(new Manufacturer { Id = manufacturerId, Name = "RowVersion Mfg" });
        _dbContext.PrinterModels.Add(new PrinterModel { Id = modelId, ManufacturerId = manufacturerId, Name = "RowVersion Model" });
        _dbContext.Printers.Add(new Printer
        {
            Id = printerId,
            Name = "RowVersion Printer",
            ServerUrl = "http://192.168.1.60",
            BackendPort = 7125,
            Backend = (int)PrinterBackend.Moonraker,
            ManufacturerId = manufacturerId,
            ModelId = modelId
        });
        await _dbContext.SaveChangesAsync();

        return printerId;
    }

    [Fact]
    public async Task GetAllCompleteDtosAsync_IncludesNonNullBase64RowVersion()
    {
        Guid printerId = await SeedPrinterAsync();

        CompletePrinterDto[] dtos = await _printersService.GetAllCompleteDtosAsync(CancellationToken.None);

        CompletePrinterDto dto = dtos.Single(d => d.Id == printerId);
        dto.RowVersion.Should().NotBeNullOrEmpty("list consumers guard printer mutations on rowVersion");

        // Must be valid base-64 so the frontend can echo it back as an If-Match token.
        Action decode = () => Convert.FromBase64String(dto.RowVersion!);
        decode.Should().NotThrow();

        dto.ConfigurationRevision.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task GetAllCompleteDtosAsync_RowVersion_RoundTripsWithSinglePrinterEndpoint()
    {
        Guid printerId = await SeedPrinterAsync();

        CompletePrinterDto listDto = (await _printersService.GetAllCompleteDtosAsync(CancellationToken.None))
            .Single(d => d.Id == printerId);
        PrinterDto singleDto = await _printersService.GetPrinterDtoAsync(printerId, CancellationToken.None);

        listDto.RowVersion.Should().Be(singleDto.RowVersion,
            "the list and single-printer endpoints must expose the same concurrency token");
        listDto.ConfigurationRevision.Should().Be(singleDto.ConfigurationRevision);
    }
}
