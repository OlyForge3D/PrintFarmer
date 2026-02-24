using Farm.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Farm.Migrations.PostgreSQL;

/// <summary>
/// Design-time factory for <see cref="AppDbContext"/> used by EF Core tooling
/// to generate PostgreSQL migrations.
/// </summary>
/// <remarks>
/// Usage:
/// <code>
/// cd src
/// DB_PROVIDER=postgres dotnet ef migrations add MigrationName \
///     --project ./migrations/Farm.Migrations.PostgreSQL \
///     --startup-project ./migrations/Farm.Migrations.PostgreSQL \
///     --context AppDbContext
/// </code>
/// </remarks>
public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    /// <inheritdoc/>
    public AppDbContext CreateDbContext(string[] args)
    {
        var builder = new DbContextOptionsBuilder<AppDbContext>();

        // Default connection string for design-time migration generation only.
        const string connectionString = "Host=localhost;Database=printfarmer;Username=postgres;Password=postgres";
        _ = builder.UseNpgsql(connectionString, x =>
            x.MigrationsAssembly("Farm.Migrations.PostgreSQL"));

        return new AppDbContext(builder.Options);
    }
}
