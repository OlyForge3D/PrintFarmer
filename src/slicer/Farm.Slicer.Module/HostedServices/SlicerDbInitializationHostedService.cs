using Farm.Slicer.Module.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Farm.Slicer.Module.HostedServices;

/// <summary>
/// One-shot hosted service that ensures the slicer database schema exists on startup.
/// Uses <c>CreateTablesAsync</c> for SQLite (development) because the main FarmDbContext
/// may have already called <c>EnsureCreated</c> on the shared database file, which makes
/// a second <c>EnsureCreated</c> from SlicerDbContext a no-op (tables would be missing).
/// Uses <c>MigrateAsync</c> for providers with migration assemblies (PostgreSQL, SQL Server).
/// </summary>
public sealed class SlicerDbInitializationHostedService(
    IServiceProvider serviceProvider,
    ILogger<SlicerDbInitializationHostedService> logger) : IHostedService
{
    /// <inheritdoc/>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            using IServiceScope scope = serviceProvider.CreateScope();
            SlicerDbContext db = scope.ServiceProvider.GetRequiredService<SlicerDbContext>();

            string providerName = db.Database.ProviderName ?? string.Empty;
            bool isSqlite = providerName.Contains("Sqlite", StringComparison.OrdinalIgnoreCase);

            if (isSqlite)
            {
                // Ensure the database file exists
                _ = await db.Database.EnsureCreatedAsync(cancellationToken);

                // CreateTables adds any tables from this context's model that don't
                // already exist — unlike EnsureCreated which is a no-op when the DB exists.
                try
                {
                    IRelationalDatabaseCreator creator = db.Database.GetService<IRelationalDatabaseCreator>();
                    await creator.CreateTablesAsync(cancellationToken);
                }
                catch (DbUpdateException)
                {
                    // Tables already exist — safe to ignore
                }
                catch (Microsoft.Data.Sqlite.SqliteException)
                {
                    // Tables already exist — safe to ignore
                }

                logger.LogInformation("[Slicer] Database schema ensured (SQLite/CreateTables)");
            }
            else
            {
                // PostgreSQL / SQL Server: use MigrateAsync so migration history is respected
                await db.Database.MigrateAsync(cancellationToken);
                logger.LogInformation("[Slicer] Database migrations applied ({Provider})", providerName);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[Slicer] Database schema initialization skipped (non-fatal)");
        }
    }

    /// <inheritdoc/>
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
