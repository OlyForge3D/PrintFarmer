using System;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Network;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace Farm.Web.Api.Tests.TestInfrastructure;

public static class TestHelpers
{
    /// <summary>
    /// Build an <see cref="IEgressGuard"/> mock that allows every destination and pins a
    /// resolved address (rather than the null-<see cref="EgressCheckResult.ResolvedAddress"/>
    /// "no pinning" fallback), so tests that construct a controller depending on egress vetting
    /// exercise the same pinned code path production traffic takes, not just the legacy
    /// unpinned-by-hostname branch.
    /// </summary>
    public static IEgressGuard PermissiveEgressGuard()
    {
        var mock = new Mock<IEgressGuard>();
        mock.Setup(g => g.CheckAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string url, CancellationToken _) =>
                EgressCheckResult.Allow(new Uri(url), System.Net.IPAddress.Parse("203.0.113.100")));
        return mock.Object;
    }

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

    /// <summary>
    /// Open a SQLite in-memory connection the caller owns. Keeping the connection open
    /// keeps the database alive across multiple <see cref="AppDbContext"/> instances,
    /// which is required for concurrency/lost-update tests that need two contexts to see
    /// the same rows. Caller disposes the connection when done.
    /// </summary>
    public static SqliteConnection CreateOpenSqliteConnection()
    {
        SqliteConnection connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        return connection;
    }

    /// <summary>
    /// Build an <see cref="AppDbContext"/> over an already-open connection. Set
    /// <paramref name="ensureCreated"/> for the first context so the schema (including
    /// the shift-plan unique filtered index) is created exactly once.
    /// Caller disposes the returned context.
    /// </summary>
    public static AppDbContext CreateContext(SqliteConnection connection, bool ensureCreated = false)
    {
        DbContextOptions<AppDbContext> opts = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;

        AppDbContext ctx = new AppDbContext(opts);
        if (ensureCreated)
        {
            _ = ctx.Database.EnsureCreated();
        }

        return ctx;
    }
}
