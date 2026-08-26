using Farm.Infrastructure.Data.Migrations;
using Farm.Slicer.Module.Data;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Farm.Slicer.Module.HostedServices;

/// <summary>
/// One-shot hosted service that applies migrations and reconciles persisted normalized values.
/// </summary>
public sealed class SlicerDbInitializationHostedService(
    IServiceProvider serviceProvider,
    ILogger<SlicerDbInitializationHostedService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        using IServiceScope scope = serviceProvider.CreateScope();
        SlicerDbContext db = scope.ServiceProvider.GetRequiredService<SlicerDbContext>();
        _ = await ProviderAwareMigrationRunner.MigrateAsync(
            db,
            DatabaseMigrationTarget.Slicer,
            logger,
            cancellationToken);
        await db.NormalizeMachineModelProfileNamesAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
