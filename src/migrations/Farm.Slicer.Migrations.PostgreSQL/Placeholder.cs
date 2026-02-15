using Farm.Slicer.Module.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Farm.Slicer.Migrations.PostgreSQL;

/// <summary>
/// Design-time factory for <see cref="SlicerDbContext"/> used by EF Core tooling
/// to generate PostgreSQL migrations.
/// </summary>
/// <remarks>
/// Usage:
/// <code>
/// cd src
/// DB_PROVIDER=postgres dotnet ef migrations add MigrationName \
///     --project ./migrations/Farm.Slicer.Migrations.PostgreSQL \
///     --startup-project ./migrations/Farm.Slicer.Migrations.PostgreSQL \
///     --context SlicerDbContext
/// </code>
/// </remarks>
public class SlicerDbContextDesignTimeFactory : IDesignTimeDbContextFactory<SlicerDbContext>
{
    /// <inheritdoc/>
    public SlicerDbContext CreateDbContext(string[] args)
    {
        var builder = new DbContextOptionsBuilder<SlicerDbContext>();

        // Default connection string for design-time migration generation only.
        const string connectionString = "Host=localhost;Database=printfarmer_slicer;Username=postgres;Password=postgres";
        _ = builder.UseNpgsql(connectionString, x =>
            x.MigrationsAssembly("Farm.Slicer.Migrations.PostgreSQL"));

        return new SlicerDbContext(builder.Options);
    }
}
