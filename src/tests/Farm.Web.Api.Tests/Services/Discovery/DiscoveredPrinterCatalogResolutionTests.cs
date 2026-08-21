extern alias PrinterDiscoveryRef;
using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Discovery;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Dtos;
using Farm.Infrastructure.Services.Catalog;
using Farm.Infrastructure.Services.Printers;
using Farm.Web.Api.Tests.TestInfrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;
using DeterministicDiscoveryFixtureProvider =
    PrinterDiscoveryRef::PrinterDiscovery.Services.DeterministicDiscoveryFixtureProvider;
using DeterministicDiscoveryFixtureSettings =
    PrinterDiscoveryRef::PrinterDiscovery.Services.DeterministicDiscoveryFixtureSettings;

namespace Farm.Web.Api.Tests.Services.Discovery;

/// <summary>
/// Regression coverage for GitHub issue #1821: a deterministic-discovery "Discovered Voron V2.4"
/// printer registered successfully but was persisted as "Unknown / Unknown Model", and its
/// Slice Job page machine-profiles request 404'd. Root cause: the discovery fixture's
/// Manufacturer/Model strings ("Voron Design" / "V2.4") did not exactly match any seeded catalog
/// entry (catalog manufacturer "Voron", model "Voron 2.4 300"), so
/// <c>PrintersService.CreatePrinterFromDtoAsync</c>'s exact-name lookup failed and fell back to
/// the "Unknown" manufacturer/model. Because "Unknown" has no OrcaSlicer alias, the Slice Job
/// page's machine-profile-for-model lookup then 404'd.
///
/// These tests pull the Manufacturer/Model strings directly from
/// <see cref="DeterministicDiscoveryFixtureProvider"/> (rather than hardcoding duplicated
/// strings) and assert that registering a printer from those exact fixture values resolves to
/// the real seeded catalog manufacturer/model - not "Unknown" - and that the resolved model has
/// an OrcaSlicer alias, proving the machine-profiles lookup used by the Slice Job page succeeds.
/// This will fail again if the fixture data ever drifts from the seeded catalog.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public class DiscoveredPrinterCatalogResolutionTests : IAsyncLifetime
{
    private readonly CustomWebApplicationFactory _factory;
    private AsyncServiceScope _scope;
    private IPrintersService _printersService = null!;
    private ICatalogService _catalogService = null!;
    private AppDbContext _dbContext = null!;

    public DiscoveredPrinterCatalogResolutionTests()
    {
        _factory = CustomWebApplicationFactory.CreateWithIsolatedDatabase();
    }

    public async Task InitializeAsync()
    {
        _scope = _factory.Services.CreateAsyncScope();
        _printersService = _scope.ServiceProvider.GetRequiredService<IPrintersService>();
        _catalogService = _scope.ServiceProvider.GetRequiredService<ICatalogService>();
        _dbContext = _scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _scope.DisposeAsync();
        _factory?.Dispose();
    }

    private static DeterministicDiscoveryFixtureProvider CreateFixtureProvider() =>
        new(Options.Create(new DeterministicDiscoveryFixtureSettings { Enabled = true }));

    /// <summary>
    /// Registers a printer via <see cref="IPrintersService.CreatePrinterFromDtoAsync"/> using
    /// only the discovery-provided Manufacturer/Model strings (no explicit catalog IDs), mirroring
    /// the real bug path where a user adds a discovered printer without manually picking the
    /// manufacturer/model dropdowns in <c>PrinterDiscoveryModal</c>.
    /// </summary>
    private async Task<PrinterDto> RegisterFixtureAsync(Farm.Infrastructure.Discovery.DiscoveredPrinterDto fixture)
    {
        var dto = new CreatePrinterFromDiscoveryDto
        {
            Name = fixture.Name,
            ServerUrl = fixture.ServerUrl,
            Backend = fixture.Backend,
            BackendPort = fixture.BackendPort,
            Manufacturer = fixture.Manufacturer,
            Model = fixture.Model,
            IsEnabled = true,
        };

        return await _printersService.CreatePrinterFromDtoAsync(dto, CancellationToken.None);
    }

    /// <summary>
    /// Reads back the persisted printer's resolved manufacturer/model IDs. <see cref="PrinterDto"/>
    /// only exposes the resolved names, so the catalog IDs are read directly from the database to
    /// confirm the printer linked to the expected catalog rows rather than "Unknown".
    /// </summary>
    private async Task<(Guid ManufacturerId, Guid ModelId)> GetPersistedCatalogLinksAsync(Guid printerId)
    {
        Printer printer = await _dbContext.Printers.AsNoTracking().SingleAsync(p => p.Id == printerId);
        return (printer.ManufacturerId, printer.ModelId);
    }

    [Fact]
    public async Task CreatePrinterFromDto_DeterministicVoronFixture_ResolvesRealCatalogModel_NotUnknown()
    {
        // Sanity check that the seeded catalog actually contains a "Voron" / "Voron 2.4 300"
        // entry with an OrcaSlicer alias, per src/api/Data/seed/printer-models.yaml. If this
        // ever fails, the catalog seed itself changed and the regression below is moot.
        Manufacturer voronManufacturer = await _dbContext.Manufacturers.AsNoTracking().SingleAsync(m => m.Name == "Voron");
        PrinterModel voronModel = await _dbContext.PrinterModels.AsNoTracking()
            .SingleAsync(m => m.ManufacturerId == voronManufacturer.Id && m.Name == "Voron 2.4 300");

        // Pull the manufacturer/model strings straight from the deterministic discovery fixture
        // provider used by the /printers deterministic-discovery flow, so this test fails if the
        // fixture data ever drifts from the catalog again.
        Farm.Infrastructure.Discovery.DiscoveredPrinterDto voronFixture = CreateFixtureProvider()
            .GetPrinters([PrinterBackend.Moonraker])
            .Single(p => p.Name == "Discovered Voron V2.4");

        PrinterDto created = await RegisterFixtureAsync(voronFixture);
        PrinterDto persisted = await _printersService.GetPrinterDtoAsync(created.Id, CancellationToken.None);

        persisted.ManufacturerName.Should().Be("Voron");
        persisted.ModelName.Should().Be("Voron 2.4 300");

        (Guid persistedManufacturerId, Guid persistedModelId) = await GetPersistedCatalogLinksAsync(created.Id);
        persistedManufacturerId.Should().Be(voronManufacturer.Id);
        persistedModelId.Should().Be(voronModel.Id);

        // The Slice Job page requests machine profiles for the resolved model; that lookup
        // depends on an OrcaSlicer alias existing for the model. Before the fix, the printer
        // resolved to the "Unknown" model, which has no aliases, so this lookup returned 404.
        IEnumerable<SlicerModelAliasDto> aliases = await _catalogService.GetModelAliasesAsync(persistedModelId, CancellationToken.None);
        aliases.Should().Contain(a => a.SlicerType == "OrcaSlicer" && a.SlicerModelName == "Voron 2.4 300");
    }

    [Fact]
    public async Task CreatePrinterFromDto_DeterministicPrusaFixture_ResolvesRealCatalogModel_NotUnknown()
    {
        Manufacturer prusaManufacturer = await _dbContext.Manufacturers.AsNoTracking().SingleAsync(m => m.Name == "Prusa");
        PrinterModel prusaModel = await _dbContext.PrinterModels.AsNoTracking()
            .SingleAsync(m => m.ManufacturerId == prusaManufacturer.Id && m.Name == "Prusa MK4S");

        Farm.Infrastructure.Discovery.DiscoveredPrinterDto prusaFixture = CreateFixtureProvider()
            .GetPrinters([PrinterBackend.Moonraker])
            .Single(p => p.Name == "Discovered Prusa MK4S");

        PrinterDto created = await RegisterFixtureAsync(prusaFixture);
        PrinterDto persisted = await _printersService.GetPrinterDtoAsync(created.Id, CancellationToken.None);

        persisted.ManufacturerName.Should().Be("Prusa");
        persisted.ModelName.Should().Be("Prusa MK4S");

        (Guid persistedManufacturerId, Guid persistedModelId) = await GetPersistedCatalogLinksAsync(created.Id);
        persistedManufacturerId.Should().Be(prusaManufacturer.Id);
        persistedModelId.Should().Be(prusaModel.Id);

        IEnumerable<SlicerModelAliasDto> aliases = await _catalogService.GetModelAliasesAsync(persistedModelId, CancellationToken.None);
        aliases.Should().Contain(a => a.SlicerType == "OrcaSlicer" && a.SlicerModelName == "Prusa MK4S");
    }
}
