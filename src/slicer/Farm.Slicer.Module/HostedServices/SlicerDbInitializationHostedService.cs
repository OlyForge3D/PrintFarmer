using Farm.Slicer.Module.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Farm.Slicer.Module.HostedServices;

/// <summary>
/// One-shot hosted service that ensures the slicer database schema exists on startup.
/// Uses <see cref="RelationalDatabaseCreator.CreateTablesAsync"/> for development (SQLite)
/// and <see cref="RelationalDatabaseFacadeExtensions.MigrateAsync"/> for providers with migrations.
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
            _ = await db.Database.EnsureCreatedAsync(cancellationToken);
            logger.LogInformation("[Slicer] Database schema ensured");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[Slicer] Database schema initialization skipped (non-fatal)");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
