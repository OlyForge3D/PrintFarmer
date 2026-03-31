using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Printers;
using Farm.Web.Api.Tests.TestInfrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Farm.Web.Api.Tests.Services.Printers;

/// <summary>
/// Integration tests for the concurrency retry logic in <see cref="PrintersService.SaveChangesWithRetryAsync"/>.
/// Verifies that user-initiated printer updates succeed even when a background service
/// concurrently modifies the same printer row (changing the RowVersion).
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public class PrinterConcurrencyRetryTests : IAsyncLifetime
{
    private readonly CustomWebApplicationFactory _factory;
    private AsyncServiceScope _scope;
    private IPrintersService _printersService = null!;
    private AppDbContext _dbContext = null!;

    public PrinterConcurrencyRetryTests()
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

    private async Task<(Guid ManufacturerId, Guid ModelId)> SeedCatalogAsync()
    {
        await using AsyncServiceScope seedScope = _factory.Services.CreateAsyncScope();
        AppDbContext seedDb = seedScope.ServiceProvider.GetRequiredService<AppDbContext>();

        Guid manufacturerId = Guid.NewGuid();
        Guid modelId = Guid.NewGuid();

        seedDb.Manufacturers.Add(new Manufacturer { Id = manufacturerId, Name = "ConcurrencyTest Mfg" });
        seedDb.PrinterModels.Add(new PrinterModel { Id = modelId, ManufacturerId = manufacturerId, Name = "ConcurrencyTest Model" });
        await seedDb.SaveChangesAsync();

        return (manufacturerId, modelId);
    }

    [Fact]
    public async Task SaveChangesWithRetryAsync_ConcurrentRowVersionChange_RetriesAndSucceeds()
    {
        // Arrange: seed catalog and create a printer
        (Guid mfgId, Guid modelId) = await SeedCatalogAsync();
        Printer printer = new()
        {
            Id = Guid.NewGuid(),
            Name = "Retry Test Printer",
            ServerUrl = "http://192.168.1.50",
            BackendPort = 7125,
            Backend = (int)PrinterBackend.Moonraker,
            ManufacturerId = mfgId,
            ModelId = modelId
        };
        _dbContext.Printers.Add(printer);
        await _dbContext.SaveChangesAsync();

        // Load the printer with tracking (simulates what the PUT endpoint does)
        Printer? tracked = await _printersService.FindByIdForTemplateUpdateAsync(printer.Id, CancellationToken.None);
        tracked.Should().NotBeNull();

        // Simulate user editing the printer name
        tracked!.Name = "Updated By User";
        tracked.MultiMaterial = true;

        // Simulate a background service updating the same row via a separate scope
        // (changes RowVersion in the database, making our tracked entity stale)
        await using (AsyncServiceScope bgScope = _factory.Services.CreateAsyncScope())
        {
            AppDbContext bgDb = bgScope.ServiceProvider.GetRequiredService<AppDbContext>();
            Printer? bgPrinter = await bgDb.Printers.FirstOrDefaultAsync(p => p.Id == printer.Id);
            bgPrinter.Should().NotBeNull();
            bgPrinter!.Notes = "Background status update";
            await bgDb.SaveChangesAsync();
        }

        // Act: SaveChangesWithRetryAsync should handle the stale RowVersion
        Func<Task> act = () => _printersService.SaveChangesWithRetryAsync(CancellationToken.None);
        await act.Should().NotThrowAsync<DbUpdateConcurrencyException>();

        // Assert: user's changes persisted despite the concurrent modification
        await using AsyncServiceScope verifyScope = _factory.Services.CreateAsyncScope();
        AppDbContext verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        Printer? saved = await verifyDb.Printers.AsNoTracking().FirstOrDefaultAsync(p => p.Id == printer.Id);
        saved.Should().NotBeNull();
        saved!.Name.Should().Be("Updated By User");
        saved.MultiMaterial.Should().BeTrue();
        // Background service's change should also be present (merged via OriginalValues refresh)
        saved.Notes.Should().Be("Background status update");
    }

    [Fact]
    public async Task SaveChangesWithRetryAsync_NoConflict_SavesNormally()
    {
        // Arrange
        (Guid mfgId, Guid modelId) = await SeedCatalogAsync();
        Printer printer = new()
        {
            Id = Guid.NewGuid(),
            Name = "No Conflict Printer",
            ServerUrl = "http://192.168.1.51",
            BackendPort = 7125,
            Backend = (int)PrinterBackend.Moonraker,
            ManufacturerId = mfgId,
            ModelId = modelId
        };
        _dbContext.Printers.Add(printer);
        await _dbContext.SaveChangesAsync();

        // Load and modify
        Printer? tracked = await _printersService.FindByIdForTemplateUpdateAsync(printer.Id, CancellationToken.None);
        tracked.Should().NotBeNull();
        tracked!.Name = "Simply Updated";

        // Act: no concurrent modification — should save cleanly
        Func<Task> act = () => _printersService.SaveChangesWithRetryAsync(CancellationToken.None);
        await act.Should().NotThrowAsync();

        // Assert
        await using AsyncServiceScope verifyScope = _factory.Services.CreateAsyncScope();
        AppDbContext verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        Printer? saved = await verifyDb.Printers.AsNoTracking().FirstOrDefaultAsync(p => p.Id == printer.Id);
        saved!.Name.Should().Be("Simply Updated");
    }

    [Fact]
    public async Task SaveChangesWithRetryAsync_EntityDeleted_ThrowsException()
    {
        // Arrange
        (Guid mfgId, Guid modelId) = await SeedCatalogAsync();
        Printer printer = new()
        {
            Id = Guid.NewGuid(),
            Name = "Deleted Printer",
            ServerUrl = "http://192.168.1.52",
            BackendPort = 7125,
            Backend = (int)PrinterBackend.Moonraker,
            ManufacturerId = mfgId,
            ModelId = modelId
        };
        _dbContext.Printers.Add(printer);
        await _dbContext.SaveChangesAsync();

        // Load with tracking
        Printer? tracked = await _printersService.FindByIdForTemplateUpdateAsync(printer.Id, CancellationToken.None);
        tracked.Should().NotBeNull();
        tracked!.Name = "Will fail";

        // Delete the printer via a separate scope (simulates deletion by another process)
        await using (AsyncServiceScope bgScope = _factory.Services.CreateAsyncScope())
        {
            AppDbContext bgDb = bgScope.ServiceProvider.GetRequiredService<AppDbContext>();
            Printer? toDelete = await bgDb.Printers.FirstOrDefaultAsync(p => p.Id == printer.Id);
            bgDb.Printers.Remove(toDelete!);
            await bgDb.SaveChangesAsync();
        }

        // Act & Assert: should throw because the entity no longer exists
        Func<Task> act = () => _printersService.SaveChangesWithRetryAsync(CancellationToken.None);
        await act.Should().ThrowAsync<DbUpdateConcurrencyException>();
    }
}
