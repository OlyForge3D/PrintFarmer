using Farm.Infrastructure;
using Farm.Infrastructure.Dtos;
using Farm.Infrastructure.Services.Catalog;
using Farm.Slicer.Module.Api.Services;
using Farm.Slicer.Module.Data;
using Farm.Slicer.Module.Domain;
using Farm.Slicer.Module.Dtos;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace Farm.Slicer.Module.Tests.Services;

public sealed class ProfileManufacturerMaintenanceServiceTests
{
    [Fact]
    public async Task BackfillAsync_LegacyFamilies_UpdatesEligibleRowsAndIsIdempotent()
    {
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync();
        DbContextOptions<SlicerDbContext> options =
            new DbContextOptionsBuilder<SlicerDbContext>().UseSqlite(connection).Options;
        await using SlicerDbContext dbContext = new(options);
        await dbContext.Database.EnsureCreatedAsync();

        Guid manufacturerId = Guid.NewGuid();
        Guid modelId = Guid.NewGuid();
        Guid familyId = Guid.NewGuid();
        dbContext.MachineModelProfiles.AddRange(
            Family(familyId, modelId, "Custom"),
            Family(Guid.NewGuid(), modelId, "Already Correct"),
            Family(Guid.NewGuid(), null, "Custom"));
        dbContext.MachineProfiles.AddRange(
            Variant(familyId, modelId, "Custom"),
            Variant(null, modelId, "Custom"),
            Variant(familyId, modelId, "Already Correct"));
        _ = await dbContext.SaveChangesAsync();

        Mock<ICatalogService> catalog = new(MockBehavior.Strict);
        _ = catalog.Setup(service => service.GetModelsAsync(
                null,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((
                (IReadOnlyList<PrinterModelDto>)[new PrinterModelDto(modelId, "Micron", manufacturerId)],
                null));
        _ = catalog.Setup(service => service.GetManufacturersAsync(
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((
                (IReadOnlyList<ManufacturerDto>)[new ManufacturerDto(manufacturerId, "PrintersForAnts")],
                null));
        var service = new ProfileManufacturerMaintenanceService(dbContext, catalog.Object);

        ProfileManufacturerBackfillResultDto first =
            await service.BackfillAsync(CancellationToken.None);
        ProfileManufacturerBackfillResultDto second =
            await service.BackfillAsync(CancellationToken.None);

        first.Should().Be(new ProfileManufacturerBackfillResultDto(1, 2, 0));
        second.Should().Be(new ProfileManufacturerBackfillResultDto(0, 0, 0));
        (await dbContext.MachineModelProfiles.SingleAsync(row => row.Id == familyId))
            .Manufacturer.Should().Be("PrintersForAnts");
        (await dbContext.MachineProfiles.Where(row => row.Manufacturer == "PrintersForAnts").CountAsync())
            .Should().Be(2);
        (await dbContext.MachineProfiles.Where(row => row.Manufacturer == "Already Correct").CountAsync())
            .Should().Be(1);
        (await dbContext.MachineModelProfiles.Where(row => row.Manufacturer == "Custom").CountAsync())
            .Should().Be(1);
    }

    private static MachineModelProfile Family(Guid id, Guid? modelId, string manufacturer) =>
        new()
        {
            Id = id,
            Name = $"Family {id}",
            Manufacturer = manufacturer,
            PrinterModelId = modelId,
            SlicerType = SlicerType.OrcaSlicer,
            Hash = id.ToString("N"),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

    private static MachineProfile Variant(Guid? familyId, Guid modelId, string manufacturer) =>
        new()
        {
            Id = Guid.NewGuid(),
            Name = $"Variant {Guid.NewGuid()}",
            Manufacturer = manufacturer,
            PrinterModelId = modelId,
            MachineModelProfileId = familyId,
            SlicerType = SlicerType.OrcaSlicer,
            Hash = Guid.NewGuid().ToString("N"),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
}
