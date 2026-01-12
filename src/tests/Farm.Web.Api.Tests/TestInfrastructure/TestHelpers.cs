using System;
using System.Threading.Tasks;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Farm.Web.Api.Tests.TestInfrastructure;

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
    /// Create an AppDbContext backed by a SQLite in-memory open connection.
    /// This provides relational behaviors (FKs, Include/ThenInclude) suitable for tests that rely on SQL semantics.
    /// Caller should dispose the returned context when done.
    /// </summary>
    public static AppDbContext CreateSqliteInMemoryDb()
    {
        SqliteConnection connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        DbContextOptions<AppDbContext> opts = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;

        AppDbContext ctx = new AppDbContext(opts);
        _ = ctx.Database.EnsureCreated();
        return ctx;
    }
}
