using Farm.Slicer.Module.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Farm.Slicer.Migrations.SqlServer;

/// <summary>
/// Design-time factory for <see cref="SlicerDbContext"/> used by EF Core tooling
/// to generate SQL Server migrations.
/// </summary>
/// <remarks>
/// Usage:
/// <code>
/// cd src
/// DB_PROVIDER=sqlserver dotnet ef migrations add MigrationName \
///     --project ./migrations/Farm.Slicer.Migrations.SqlServer \
///     --startup-project ./migrations/Farm.Slicer.Migrations.SqlServer \
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
        const string connectionString = "Server=localhost;Database=printfarmer_slicer;Trusted_Connection=True;TrustServerCertificate=True";
        _ = builder.UseSqlServer(connectionString, x =>
            x.MigrationsAssembly("Farm.Slicer.Migrations.SqlServer"));

        return new SlicerDbContext(builder.Options);
    }
}
