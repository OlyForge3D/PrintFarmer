using Farm.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Farm.Migrations.Sqlite;

/// <summary>
/// Creates the core database context for SQLite migration tooling.
/// </summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    /// <inheritdoc />
    public AppDbContext CreateDbContext(string[] args)
    {
        var builder = new DbContextOptionsBuilder<AppDbContext>();
        _ = builder.UseSqlite(
            "Data Source=printfarmer-migrations.db",
            options => options.MigrationsAssembly("Farm.Migrations.Sqlite"));

        return new AppDbContext(builder.Options);
    }
}
