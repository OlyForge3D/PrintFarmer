using Farm.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Farm.Infrastructure.Services.Electricity;

/// <summary>
/// Background service that deletes <see cref="Domain.PowerReading"/> rows older than 90 days
/// (the hot-retention window). Runs once per day.
/// </summary>
public class PowerReadingPruneService(
    IServiceScopeFactory scopeFactory,
    ILogger<PowerReadingPruneService> logger) : BackgroundService
{
    private const int RetentionDays = 90;
    private readonly TimeSpan _interval = TimeSpan.FromHours(24);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
                AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

                DateTime cutoff = DateTime.UtcNow.AddDays(-RetentionDays);
                int deleted = await db.PowerReadings
                    .Where(r => r.RecordedAt < cutoff)
                    .ExecuteDeleteAsync(stoppingToken);

                if (deleted > 0)
                {
                    logger.LogInformation(
                        "PowerReadingPruneService: deleted {Count} readings older than {Days} days",
                        deleted,
                        RetentionDays);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "PowerReadingPruneService: error during prune");
            }

            await Task.Delay(_interval, stoppingToken);
        }
    }
}
