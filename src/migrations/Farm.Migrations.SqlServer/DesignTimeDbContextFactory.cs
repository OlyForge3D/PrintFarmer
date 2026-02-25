using Farm.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Farm.Migrations.SqlServer;

/// <summary>
/// Design-time factory for <see cref="AppDbContext"/> used by EF Core tooling
/// to generate SQL Server migrations.
/// </summary>
/// <remarks>
/// Usage:
/// <code>
/// cd src
/// DB_PROVIDER=sqlserver dotnet ef migrations add MigrationName \
///     --project ./migrations/Farm.Migrations.SqlServer \
///     --startup-project ./migrations/Farm.Migrations.SqlServer \
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
        const string connectionString = "Server=localhost;Database=printfarmer;Trusted_Connection=True;TrustServerCertificate=True";
        _ = builder.UseSqlServer(connectionString, x =>
            x.MigrationsAssembly("Farm.Migrations.SqlServer"));

        return new AppDbContext(builder.Options);
    }
}
