using System;
using System.Threading.Tasks;
using Farm.Infrastructure.Data;
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

        var unknownManufacturer = await db.Manufacturers.FirstOrDefaultAsync(m => m.Name == "Unknown");
        Guid manufacturerId = unknownManufacturer != null ? unknownManufacturer.Id : Guid.Empty;

        Guid modelId = Guid.Empty;
        if (manufacturerId != Guid.Empty)
        {
            var unknownModel = await db.Models.FirstOrDefaultAsync(m => m.Name == "Unknown Model" && m.ManufacturerId == manufacturerId);
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
        var connection = new Microsoft.Data.Sqlite.SqliteConnection("DataSource=:memory:");
        connection.Open();
        var opts = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;

        var ctx = new AppDbContext(opts);
        ctx.Database.EnsureCreated();
        return ctx;
    }
}
