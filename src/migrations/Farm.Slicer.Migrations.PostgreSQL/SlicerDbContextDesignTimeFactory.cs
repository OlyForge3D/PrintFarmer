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
#pragma warning disable S2068 // Design-time only, not a production credential
        const string connectionString = "Host=localhost;Database=printfarmer_slicer;Username=postgres;Password=postgres";
#pragma warning restore S2068
        _ = builder.UseNpgsql(connectionString, x =>
            x.MigrationsAssembly("Farm.Slicer.Migrations.PostgreSQL"));

        return new SlicerDbContext(builder.Options);
    }
}
