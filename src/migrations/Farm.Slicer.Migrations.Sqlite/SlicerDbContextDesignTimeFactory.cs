using Farm.Slicer.Module.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Farm.Slicer.Migrations.Sqlite;

/// <summary>
/// Creates the slicer database context for SQLite migration tooling.
/// </summary>
public sealed class SlicerDbContextDesignTimeFactory : IDesignTimeDbContextFactory<SlicerDbContext>
{
    /// <inheritdoc />
    public SlicerDbContext CreateDbContext(string[] args)
    {
        var builder = new DbContextOptionsBuilder<SlicerDbContext>();
        _ = builder.UseSqlite(
            "Data Source=printfarmer-slicer-migrations.db",
            options => options.MigrationsAssembly("Farm.Slicer.Migrations.Sqlite"));

        return new SlicerDbContext(builder.Options);
    }
}
