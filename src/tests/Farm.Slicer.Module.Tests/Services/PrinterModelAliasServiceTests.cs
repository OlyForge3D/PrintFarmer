using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Gcode;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace Farm.Slicer.Module.Tests.Services;

public sealed class PrinterModelAliasServiceTests
{
    [Fact]
    public async Task ResolveAndEnsure_AreCaseInsensitive_WithEfInMemoryProvider()
    {
        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var dbContext = new AppDbContext(options);
        Guid modelId = Guid.NewGuid();
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
