using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Gcode;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Farm.Slicer.Module.Tests.Services;

public sealed class PrinterModelAliasServiceTests
{
    [Fact]
    public async Task ResolveAndEnsure_AreCaseInsensitive_WithRelationalSqliteProvider()
    {
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync();
        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var dbContext = new AppDbContext(options);
        _ = await dbContext.Database.EnsureCreatedAsync();
        Guid manufacturerId = Guid.NewGuid();
        Guid modelId = Guid.NewGuid();
        dbContext.Manufacturers.Add(new Manufacturer
        {
            Id = manufacturerId,
            Name = "Voron Design",
        });
        dbContext.PrinterModels.Add(new PrinterModel
        {
            Id = modelId,
            ManufacturerId = manufacturerId,
            Name = "Micron 180",
        });
        dbContext.PrinterModelAliases.Add(new PrinterModelAlias
        {
            PrinterModelId = modelId,
            SlicerModelName = "Micron 180",
            SlicerType = "OrcaSlicer",
            CreatedAt = DateTime.UtcNow
        });
        _ = await dbContext.SaveChangesAsync();

        var service = new PrinterModelAliasService(dbContext);

        Guid? resolved = await service.ResolveModelAliasAsync("micron 180", "orcaslicer");
        await service.EnsureModelAliasAsync(modelId, "MICRON 180", "ORCASLICER");

        resolved.Should().Be(modelId);
        (await dbContext.PrinterModelAliases.CountAsync()).Should().Be(1);
    }
}
