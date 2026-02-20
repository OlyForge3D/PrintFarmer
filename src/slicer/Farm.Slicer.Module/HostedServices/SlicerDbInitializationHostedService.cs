using Farm.Slicer.Module.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Farm.Slicer.Module.HostedServices;

/// <summary>
/// One-shot hosted service that ensures the slicer database schema exists on startup.
/// Uses <see cref="DatabaseFacade.EnsureCreatedAsync"/> for SQLite (development)
/// and <see cref="RelationalDatabaseFacadeExtensions.MigrateAsync"/> for providers
/// with migration assemblies (PostgreSQL, SQL Server).
/// </summary>
public sealed class SlicerDbInitializationHostedService(
    IServiceProvider serviceProvider,
    ILogger<SlicerDbInitializationHostedService> logger) : IHostedService
{
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
                // SQLite: no migration assemblies, use EnsureCreated for rapid development
                _ = await db.Database.EnsureCreatedAsync(cancellationToken);
                logger.LogInformation("[Slicer] Database schema ensured (SQLite/EnsureCreated)");
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

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
