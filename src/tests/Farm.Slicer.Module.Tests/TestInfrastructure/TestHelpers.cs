using System;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Farm.Slicer.Module.Tests.TestInfrastructure;

public static class TestHelpers
{
    /// <summary>
    /// Return the seeded Unknown manufacturer and Unknown Model IDs if present.
    /// If not present, returns Guid.Empty for that item.
    /// </summary>
    public static async Task<(Guid ManufacturerId, Guid ModelId)> GetUnknownCatalogIdsAsync(AppDbContext db)
    {
        ArgumentNullException.ThrowIfNull(db);

        Manufacturer? unknownManufacturer = await db.Manufacturers.FirstOrDefaultAsync(m => m.Name == "Unknown");
        Guid manufacturerId = unknownManufacturer != null ? unknownManufacturer.Id : Guid.Empty;

        Guid modelId = Guid.Empty;
        if (manufacturerId != Guid.Empty)
        {
            PrinterModel? unknownModel = await db.PrinterModels.FirstOrDefaultAsync(m => m.Name == "Unknown Model" && m.ManufacturerId == manufacturerId);
            if (unknownModel != null)
            {
                modelId = unknownModel.Id;
            }
        }

        return (manufacturerId, modelId);
    }

    /// <summary>
    /// Create an SlicerDbContext backed by a SQLite in-memory open connection.
    /// This provides relational behaviors (FKs, Include/ThenInclude) suitable for tests that rely on SQL semantics.
    /// Caller should dispose the returned context when done.
    /// </summary>
    public static SlicerDbContext CreateSqliteInMemoryDb()
    {
        SqliteConnection connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        DbContextOptions<SlicerDbContext> opts = new DbContextOptionsBuilder<SlicerDbContext>()
            .UseSqlite(connection)
            .Options;

        SlicerDbContext ctx = new SlicerDbContext(opts);
        _ = ctx.Database.EnsureCreated();
        return ctx;
    }
}
